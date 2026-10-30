// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Michael Lamothe
//
// The DLSS seam's workflow: on-demand frame-by-frame processing with VISIBLE
// latency (the demonstration transform costs milliseconds, the synthetic
// geometry — it is never presented as real-time). One trigger captures the
// current view into the low-resolution framebuffer, evaluates it on a worker
// thread while the live view keeps rendering, then pins the returned frame as
// the presented view, named in the status text ("synthetic pipeline
// result" etc.); the transform label ("synthetic weights — arbitrary
// transform") is recorded, not bannered (the label policy below). Any
// camera input returns to the live view.
// The app's own workflow is untouched when the seam is idle.
//
// The unattended demo mode (--dlss-demo <dir>) drives the same path: it
// screenshots the live view, processes one frame, screenshots the presented
// result, writes a report with the measured latency AND the transform
// label (recorded there, not shown on screen) and closes — evidence to
// disk for headless-at-the-keyboard runs.
using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using StbImageWriteSharp;

namespace CentreStage;

internal sealed class DlssSeam : IDisposable
{
    private enum State
    {
        Idle,
        Processing,
        Presenting,
        Failed,
    }

    private readonly GL _gl;
    private readonly DlssStage _stage;
    private readonly string? _demoDir;
    private Dlss5Pipeline? _pipeline;
    private State _state = State.Idle;
    private string? _failure;
    private readonly Stopwatch _evaluateClock = new();
    private Task<(Dlss5Status Status, (string Name, byte[] Buf)[] Views, double Seconds)>? _worker;
    private double _lastSeconds;
    private double _presentationSeconds;
    private int _processed;
    // The pinned result's named views in F10 cycle order:
    //   model loaded: [neural, bilinear, Lanczos3]
    //   otherwise:    [Lanczos3, bilinear]
    private (string Name, byte[] Buf)[]? _pinnedViews;
    private int _pinnedView;
    private bool _abMissing;

    // The demo's captured screenshots (kept so the composite can be built
    // from the very pixels the evidence files contain).
    private readonly Dictionary<string, byte[]> _demoShots = new();

    /// <summary>The model shown (named in the demo report).</summary>
    public string AssetName { get; set; } = "(unnamed asset)";
    private byte[]? _reference;
    private int _demoViewIndex;
    private int _captureW = DlssStage.LowWidth;
    private int _captureH = DlssStage.LowHeight;
    private int _frameBufferW;
    private int _frameBufferH;

    /// <summary>The view shown with the pinned result (for the HUD).</summary>
    public string ViewName
    {
        get
        {
            if (_pinnedViews is null || _pinnedViews.Length == 0)
            {
                return "view: (none)";
            }
            var i = Math.Clamp(_pinnedView, 0, _pinnedViews.Length - 1);
            var next = _pinnedViews[(i + 1) % _pinnedViews.Length].Name;
            string Pretty(string name) => name switch
            {
                "neural" => "neural upscaler result",
                "bilinear" => "plain stretch baseline",
                "synthetic" => "synthetic pipeline result",
                _ => "classical sharp stretch",
            };
            return $"{Pretty(_pinnedViews[i].Name)} (F10: {Pretty(next)})";
        }
    }

    /// <summary>What the presented view IS, in plain words (the HUD).</summary>
    public string PlainDescription
    {
        get
        {
            if (_pinnedViews is null || _pinnedViews.Length == 0)
            {
                return "";
            }
            var name = _pinnedViews[Math.Clamp(_pinnedView, 0, _pinnedViews.Length - 1)].Name;
            var geo = $"{_captureW}x{_captureH}";
            var scale = _pipeline?.DemoScale ?? 0;
            var captured = scale == 2
                ? $"the scene was captured at half resolution ({geo})"
                : $"the scene was captured at low resolution ({geo})";
            var enlarge = scale > 0 ? $"enlarged {scale}x" : "processed";
            return name switch
            {
                "neural" => $"{captured} and {enlarge} by a learned model "
                    + "(real open weights)",
                "bilinear" => $"the same capture ({geo}), {enlarge} by simple "
                    + "stretching - the softest result",
                "synthetic" => $"the same capture ({geo}) through the synthetic "
                    + "pipeline (synthetic weights - arbitrary transform)",
                _ => $"the same capture ({geo}), {enlarge} by a fixed sharpness formula",
            };
        }
    }

    /// <summary>The pinned views' labels (the report's honesty lines).</summary>
    public IReadOnlyList<string> ViewLabels
    {
        get
        {
            var labels = new List<string>();
            if (_pinnedViews is not null)
            {
                foreach (var v in _pinnedViews)
                {
                    labels.Add(v.Name switch
                    {
                        "neural" => "neural: " + Dlss5Pipeline.LoadedModelLabel,
                        "synthetic" => "synthetic: synthetic weights - arbitrary " +
                            "transform (not DLSS5)",
                        _ => $"{v.Name}: not DLSS5 - classical demonstration " +
                            $"scaler ({(v.Name == "bilinear" ? "bilinear baseline" : "Lanczos3")})",
                    });
                }
            }
            return labels;
        }
    }

    /// <summary>F10: cycle the pinned result through the views.</summary>
    public void ToggleAB()
    {
        if (_state != State.Presenting || _pinnedViews is null || _pinnedViews.Length < 2)
        {
            Console.WriteLine("dlss: F10 ignored (no A/B pair)");
            return;
        }
        _pinnedView = (_pinnedView + 1) % _pinnedViews.Length;
        _stage.UploadResult(_pinnedViews[_pinnedView].Buf, _pipeline!.OutputWidth,
            _pipeline!.OutputHeight);
        Console.WriteLine($"dlss: F10 -> {ViewName}");
    }
    private bool _resetNext = true;
    private bool _capturePending;
    private int _demoStage;
    private long _demoFrame;

    private readonly CentreStage.Core.DemoUi _ui = new();

    /// <summary>
    /// A left click on a demo control. Returns true when the click is
    /// consumed (it must not also orbit the camera).
    /// </summary>
    public bool HandleMouseDown(float x, float y, int frameWidth, int frameHeight)
    {
        if (_pipeline is null)
        {
            return false;
        }

        if (_ui.MenuOpen)
        {
            // The menu eats every click while open: choose an item or dismiss.
            var item = _ui.HoveredItem(x, y, CentreStage.Core.DemoUi.MenuActions.Length);
            if (item >= 0 && ActionEnabled(CentreStage.Core.DemoUi.MenuActions[item]))
            {
                DoAction(CentreStage.Core.DemoUi.MenuActions[item]);
            }
            _ui.CloseMenu();
            return true;
        }

        foreach (var (action, _) in Buttons)
        {
            var rect = CentreStage.Core.DemoUi.Button(frameWidth, IndexOf(action));
            if (rect.Contains(x, y))
            {
                if (action is { } a && ActionEnabled(a))
                {
                    DoAction(a);
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A right click opens the demo's context menu (the mouse-driven way to
    /// run the experiment). Returns true when consumed (it must not also pan).
    /// </summary>
    public bool HandleRightClick(float x, float y, int frameWidth, int frameHeight)
    {
        if (_pipeline is null)
        {
            return false;
        }
        if (_ui.MenuOpen)
        {
            _ui.CloseMenu();
            return true;
        }
        _ui.OpenMenu(x, y, frameWidth, frameHeight,
            CentreStage.Core.DemoUi.MenuActions.Length);
        return true;
    }

    /// <summary>Test hook: open the context menu at (x, y) and leave it up.</summary>
    public void SynthMenuOpen(float x, float y, int frameWidth, int frameHeight)
    {
        // A fresh open: an already-open menu would CLOSE on the right
        // click (real toggling), leaving no menu up.
        if (_ui.MenuOpen)
        {
            _ui.CloseMenu();
        }
        HandleRightClick(x, y, frameWidth, frameHeight);
    }

    /// <summary>
    /// Test hook: right-click at (x, y) then choose the given menu item -
    /// the same Handle* entry points real mouse events call, so the whole
    /// mouse path is exercised minus the OS pointer delivery.
    /// </summary>
    public void SynthMenuPick(float x, float y, int item, int frameWidth, int frameHeight)
    {
        item = Math.Clamp(item, 0, CentreStage.Core.DemoUi.MenuActions.Length - 1);
        if (_ui.MenuOpen)
        {
            _ui.CloseMenu();
        }
        HandleRightClick(x, y, frameWidth, frameHeight);
        var rect = _ui.MenuItem(item);
        var cx = rect.X + rect.W * 0.5f;
        var cy = rect.Y + rect.H * 0.5f;
        HandleMouseMove(cx, cy);
        HandleMouseDown(cx, cy, frameWidth, frameHeight);
    }

    /// <summary>Tracks the cursor for menu hover (never consumes).</summary>
    public void HandleMouseMove(float x, float y)
    {
        _hoverItem = _ui.MenuOpen
            ? _ui.HoveredItem(x, y, CentreStage.Core.DemoUi.MenuActions.Length)
            : -1;
    }

    private int _hoverItem = -1;

    private void DoAction(CentreStage.Core.DemoAction action)
    {
        switch (action)
        {
            case CentreStage.Core.DemoAction.Enlarge:
                Trigger();
                break;
            case CentreStage.Core.DemoAction.NextView:
                ToggleAB();
                break;
            case CentreStage.Core.DemoAction.BackToLive:
                BackToLive();
                break;
        }
    }

    /// <summary>The on-screen control's way back to the live view.</summary>
    public void BackToLive()
    {
        if (_state == State.Presenting)
        {
            _state = State.Idle;
            Console.WriteLine("dlss: the on-screen control returned to the live view");
        }
    }

    private bool ActionEnabled(CentreStage.Core.DemoAction action) => action switch
    {
        CentreStage.Core.DemoAction.Enlarge => _state is State.Idle or State.Failed,
        CentreStage.Core.DemoAction.NextView => _state == State.Presenting,
        CentreStage.Core.DemoAction.BackToLive => _state == State.Presenting,
        _ => false,
    };

    private int IndexOf(CentreStage.Core.DemoAction? action)
    {
        var index = 0;
        foreach (var (candidate, _) in Buttons)
        {
            if (candidate == action)
            {
                return index;
            }
            index++;
        }
        return index;
    }

    /// <summary>The visible controls for the current state (top to bottom).</summary>
    private List<(CentreStage.Core.DemoAction? Action, string Text)> Buttons => _state switch
    {
        State.Processing => new() { (null, "WORKING... (this takes seconds)") },
        State.Presenting => new()
        {
            (CentreStage.Core.DemoAction.NextView, "NEXT COMPARISON VIEW  (F10)"),
            (CentreStage.Core.DemoAction.BackToLive, "BACK TO LIVE VIEW"),
        },
        State.Failed => new() { (CentreStage.Core.DemoAction.Enlarge, "TRY AGAIN  (F9)") },
        _ => _pipeline is null
            ? new()
            : new() { (CentreStage.Core.DemoAction.Enlarge, "ENLARGE THIS VIEW  (F9)") },
    };

    public DlssSeam(GL gl, string? demoDir)
    {
        _gl = gl;
        _stage = new DlssStage(gl);
        _demoDir = demoDir;
        if (demoDir is not null)
        {
            Directory.CreateDirectory(demoDir);
        }
    }

    /// <summary>
    /// The transform's honesty label. NOT shown on screen (the UI carries
    /// no disclaimer banner - Michael's preference); it is recorded in the
    /// demo report and available to any host via the ABI.
    /// </summary>
    public string Label => _pipeline?.LabelFor ?? Dlss5Pipeline.Label;

    /// <summary>
    /// Test-only fault injection (off by default): CENTRESTAGE_DLSS_FAULT=throw-once
    /// makes the first evaluation fault on the worker, exercising the
    /// faulted-worker path; later evaluations succeed so recovery is testable.
    /// </summary>
    private static int s_faultPending =
        Environment.GetEnvironmentVariable("CENTRESTAGE_DLSS_FAULT") == "throw-once" ? 1 : 0;

    /// <summary>Keyboard/camera trigger: process one frame, or unpin the result.</summary>
    public void Trigger()
    {
        if (_state == State.Presenting)
        {
            _state = State.Idle;
            Console.WriteLine("dlss: F9 unpinned the presented result");
            return;
        }
        if (_state == State.Processing)
        {
            Console.WriteLine("dlss: F9 ignored (a frame is already in flight)");
            return;
        }
        Console.WriteLine("dlss: F9 requested one frame");
        RequestFrame();
    }

    /// <summary>Camera input unpins a presented result (back to the live view).</summary>
    public void Release()
    {
        if (_state == State.Presenting)
        {
            _state = State.Idle;
            Console.WriteLine("dlss: camera input released the presented result");
        }
    }

    /// <summary>
    /// True while a frame is being evaluated: teardown must not enter the
    /// closing stage (whose GL teardown blocks) until it completes.
    /// </summary>
    public bool IsEvaluating => _state == State.Processing;

    private void RequestFrame()
    {
        if (_state == State.Processing)
        {
            return;
        }
        try
        {
            EnsurePipeline();
        }
        catch (Exception ex)
        {
            _state = State.Failed;
            _failure = ex.Message;
            Console.Error.WriteLine($"error: DLSS seam unavailable: {ex.Message}");
            return;
        }
        _failure = null; // a retry after Failed starts a fresh failure report
        _state = State.Idle; // capture happens at the next frame's start
        _capturePending = true;
    }

    private void EnsurePipeline()
    {
        {
            if (_pipeline is null)
            {
                // The mode decides the capture geometry (the seam's knob):
                //  - default: the demonstration transform at a HALF-WINDOW
                //    capture upscaled x2 (the geometry that looks sharp;
                //    64x72 x4 is imperceptible - Michael's F9 verdict),
                //  - CENTRESTAGE_DLSS_EXTREME=1: the recorded 64x72 x4
                //    stress case,
                //  - CENTRESTAGE_DLSS_SYNTHETIC=1: the synthetic temporal
                //    pipeline at 64x72 (its 320x320 boundary floor).
                var synthetic = Environment.GetEnvironmentVariable(
                    "CENTRESTAGE_DLSS_SYNTHETIC") == "1";
                var extreme = Environment.GetEnvironmentVariable(
                    "CENTRESTAGE_DLSS_EXTREME") == "1";
                if (synthetic || extreme)
                {
                    _pipeline = new Dlss5Pipeline(DlssStage.LowWidth, DlssStage.LowHeight,
                        threads: 8);
                    if (!synthetic)
                    {
                        var demoStatus = _pipeline.SetDemoTransform(4);
                        if (demoStatus != Dlss5Status.Ok)
                        {
                            Console.Error.WriteLine(
                                $"error: demo transform refused: {demoStatus}");
                        }
                    }
                    _captureW = DlssStage.LowWidth;
                    _captureH = DlssStage.LowHeight;
                }
                else if (_frameBufferW < 1 || _frameBufferH < 1)
                {
                    // The window size is not known yet (an early trigger):
                    // fall back to the synthetic geometry rather than
                    // capturing a silently wrong frame.
                    _captureW = DlssStage.LowWidth;
                    _captureH = DlssStage.LowHeight;
                    _pipeline = new Dlss5Pipeline(_captureW, _captureH, threads: 8);
                    _pipeline.SetDemoTransform(2);
                    _pipeline.SetDemoKernel(_pipeline.PrimaryKernel);
                }
                else
                {
                    // One uniform scale keeps the capture aspect equal to
                    // the window's (the projection matches).
                    var scale = Math.Min(
                        Math.Clamp(_frameBufferW / 2, 160, 960) / (double)_frameBufferW,
                        Math.Clamp(_frameBufferH / 2, 90, 540) / (double)_frameBufferH);
                    _captureW = Math.Max(2, (int)(_frameBufferW * scale) & ~1);
                    _captureH = Math.Max(2, (int)(_frameBufferH * scale) & ~1);
                    _pipeline = new Dlss5Pipeline(_captureW, _captureH, threads: 8);
                    var demoStatus = _pipeline.SetDemoTransform(2);
                    _pipeline.SetDemoKernel(_pipeline.PrimaryKernel);
                    if (demoStatus != Dlss5Status.Ok)
                    {
                        Console.Error.WriteLine(
                            $"error: demo transform refused: {demoStatus}");
                    }
                }
                Console.WriteLine(
                    $"dlss: capture {_captureW}x{_captureH} -> "
                    + $"{_pipeline.OutputWidth}x{_pipeline.OutputHeight} presented");
            }
        }
    }

    /// <summary>
    /// Per-frame entry (GL thread). Draws the low-resolution capture pass when
    /// one is due and presents the pinned result when there is one.
    /// Returns true when the presented result fills the frame (the caller
    /// skips its normal scene drawing).
    /// </summary>
    public bool BeginFrame(Action drawScene, int frameBufferWidth, int frameBufferHeight)
    {
        _frameBufferW = frameBufferWidth;
        _frameBufferH = frameBufferHeight;
        if (_capturePending && _pipeline is not null)
        {
            _capturePending = false;
            _stage.EnsureCaptureSize(_captureW, _captureH);
            _stage.BeginCapture();
            drawScene();
            var capture = _stage.EndCapture(frameBufferWidth, frameBufferHeight);
            if (_pipeline!.DemoScale > 0)
            {
                // The ground truth for the quality measurement: the same
                // view rendered at the PRESENTED size (the upscale's
                // honest reference).
                _stage.EnsureCaptureSize(_pipeline.OutputWidth, _pipeline.OutputHeight);
                _stage.BeginCapture();
                drawScene();
                _reference = _stage.EndCapture(_pipeline.OutputWidth, _pipeline.OutputHeight);
                _stage.EnsureCaptureSize(_captureW, _captureH);
            }
            var reset = _resetNext;
            _resetNext = false;
            _evaluateClock.Restart();
            var pipeline = _pipeline;
            _worker = Task.Run(() =>
            {
                // The authoritative latency is measured around the native
                // call on the worker: the render loop's cadence (throttled
                // while the session is idle-blanked) must never inflate it.
                if (Interlocked.Exchange(ref s_faultPending, 0) == 1)
                {
                    throw new InvalidOperationException(
                        "fault injection: CENTRESTAGE_DLSS_FAULT=throw-once");
                }
                var clock = Stopwatch.StartNew();
                _abMissing = false;
                var bytes = pipeline.OutputWidth * pipeline.OutputHeight * 3;
                var primary = new byte[bytes];
                var status = pipeline.Evaluate(capture, primary, reset);
                // The A/B views: the SAME capture through the alternate
                // kernels (the honest comparison at the same geometry).
                // With the real weights loaded the primary is the neural
                // demonstration and the alternates are the two classical
                // scalers; without it the primary is Lanczos3.
                string NameFor(int kernel) => (_pipeline?.DemoScale ?? 0) == 0
                    ? "synthetic"
                    : kernel == 3 ? "neural"
                    : kernel == 2 ? "bilinear" : "Lanczos3";
                var views = new List<(string Name, byte[] Buf)>
                {
                    (NameFor(_pipeline?.PrimaryKernel ?? 1), primary),
                };
                if (status == Dlss5Status.Ok && pipeline.DemoScale > 0)
                {
                    // Every alternate pass must restore the primary kernel
                    // even on failure (otherwise the next "neural" frame
                    // would silently be a classical scaler).
                    try
                    {
                        foreach (var kernel in new[] { 2, 1 })
                        {
                            if (kernel == (_pipeline?.PrimaryKernel ?? 1))
                            {
                                continue;
                            }
                            if (pipeline.SetDemoKernel(kernel) != Dlss5Status.Ok)
                            {
                                _abMissing = true;
                                continue;
                            }
                            var alt = new byte[bytes];
                            var altStatus = pipeline.Evaluate(capture, alt, reset: false);
                            if (altStatus == Dlss5Status.Ok)
                            {
                                views.Add((NameFor(kernel), alt));
                            }
                            else
                            {
                                // A failed alternate: the fact is recorded
                                // and the view is dropped - never a
                                // stand-in masquerading as a comparison.
                                _abMissing = true;
                                Console.Error.WriteLine(
                                    $"error: alternate view {NameFor(kernel)} failed: {altStatus}");
                            }
                        }
                    }
                    finally
                    {
                        var restore = pipeline.SetDemoKernel(_pipeline!.PrimaryKernel);
                        if (restore != Dlss5Status.Ok)
                        {
                            Console.Error.WriteLine(
                                $"error: demo kernel restore failed: {restore}");
                        }
                    }
                }
                return (status, views.ToArray(), clock.Elapsed.TotalSeconds);
            });
            _state = State.Processing;
        }

        if (_state == State.Processing && _worker is { IsCompleted: true })
        {
            try
            {
                var (status, views, evaluateSeconds) = _worker.GetAwaiter().GetResult();
                _evaluateClock.Stop();
                _lastSeconds = evaluateSeconds;
                _presentationSeconds = _evaluateClock.Elapsed.TotalSeconds;
                _worker = null;
                if (status != Dlss5Status.Ok)
                {
                    _state = State.Failed;
                    _failure = $"evaluate: {Dlss5Pipeline.StatusName(status)} ({status})";
                    Console.Error.WriteLine($"error: DLSS evaluate failed: {_failure}");
                }
                else
                {
                    _stage.UploadResult(views[0].Buf, _pipeline!.OutputWidth, _pipeline!.OutputHeight);
                    _pinnedViews = views;
                    _pinnedView = 0;
                    _processed++;
                    _state = State.Presenting;
                    Console.WriteLine(
                        $"  dlss: frame {_processed} processed in {_lastSeconds:F2} s "
                        + $"(capture-to-presented {_presentationSeconds:F2} s, "
                        + $"{_pipeline!.Counter} since reset) - frame-by-frame, not real-time");
                    // The transform label is recorded with every presented
                    // frame (the contract: it must not be lost from the
                    // host's records - on-screen display is a UI choice).
                    Console.WriteLine($"  dlss: transform label (recorded): {Label}");
                }
            }
            catch (Exception ex)
            {
                // A faulted worker (managed guard, marshalling, OOM) is a
                // failed frame, never a crash of the viewer.
                _evaluateClock.Stop();
                _worker = null;
                _state = State.Failed;
                _failure = ex.GetBaseException().Message;
                Console.Error.WriteLine($"error: DLSS evaluate faulted: {_failure}");
            }
        }

        if (_state == State.Presenting)
        {
            _stage.Present(frameBufferWidth, frameBufferHeight);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Per-frame exit (GL thread, after the caller's drawing): the status text
    /// for the seam's states and the unattended demo's screenshot steps.
    /// </summary>
    public void EndFrame(StatusOverlay overlay, int frameBufferWidth, int frameBufferHeight,
        float elapsed, Action requestClose)
    {
        try
        {
            EnsurePipeline();
        }
        catch (Exception ex)
        {
            if (_state != State.Failed)
            {
                _state = State.Failed;
                _failure = ex.Message;
                Console.Error.WriteLine($"error: DLSS seam unavailable: {ex.Message}");
            }
        }

        if (_state == State.Processing)
        {
            overlay.DrawHud(frameBufferWidth, frameBufferHeight,
                (_pipeline?.DemoScale == 2
                    ? $"view captured at half resolution ({_captureW}x{_captureH})"
                    : $"view captured at low resolution ({_captureW}x{_captureH})")
                + $" - enlarging it now ({_evaluateClock.Elapsed.TotalSeconds:F1} s so far; "
                + "frame-by-frame, not real-time)",
                17f, new Vector4(0.95f, 0.85f, 0.55f, 0.95f));
        }
        else if (_state == State.Presenting)
        {
            // The screen names the view only - no disclaimer banner
            // (Michael's UI preference). The transform label is recorded
            // in the demo report instead.
            overlay.DrawHud(frameBufferWidth, frameBufferHeight,
                $"{ViewName} - {PlainDescription} | enlarged to "
                + $"{_pipeline?.OutputWidth}x{_pipeline?.OutputHeight}, processed in "
                + $"{_lastSeconds:F1} s (frame-by-frame, not real-time) | "
                + "F9 or camera movement: back to the live view",
                17f, new Vector4(0.85f, 0.88f, 0.93f, 0.92f));
        }
        else if (_state == State.Failed)
        {
            overlay.DrawHud(frameBufferWidth, frameBufferHeight,
                $"DLSS seam failed: {_failure}", 17f, new Vector4(0.95f, 0.45f, 0.45f, 0.95f));
        }
        else if (_pipeline is not null)
        {
            // The live view: say what the experiment is before it runs.
            var hint = _pipeline.DemoScale == 0
                ? "live render at full resolution - press F9 to process one frame "
                  + "through the synthetic pipeline (synthetic weights - arbitrary transform)"
                : "live render at full resolution (the detail the experiment preserves) - "
                  + "press F9 to capture at "
                  + (_pipeline.DemoScale == 2 ? "half" : "low")
                  + " resolution and enlarge it two ways (F10 swaps the comparison view)";
            overlay.DrawHud(frameBufferWidth, frameBufferHeight, hint,
                17f, new Vector4(0.80f, 0.85f, 0.93f, 0.92f));
        }

        DrawControls(overlay, frameBufferWidth, frameBufferHeight);

        if (_demoDir is null)
        {
            return;
        }
        _demoFrame++;
        switch (_demoStage)
        {
            case 0:
                // Let the first ready frames settle (texture upload + framing),
                // then evidence the live view.
                if (_demoFrame > 2)
                {
                    SavePng(Path.Combine(_demoDir, "live.png"), frameBufferWidth, frameBufferHeight);
                    _demoStage = 1;
                }
                break;
            case 1:
                RequestFrame();
                _demoStage = 2;
                break;
            case 2:
                // One frame of the in-flight state: the live view must still
                // render at full size (the capture pass restores the
                // viewport) with the latency visible in the status text.
                if (_state == State.Processing)
                {
                    SavePng(Path.Combine(_demoDir, "processing.png"), frameBufferWidth,
                        frameBufferHeight);
                    _demoStage = 3;
                }
                else if (_state == State.Presenting)
                {
                    // Completed within one frame (not expected, but safe).
                    // Name the file for the view actually shown (a no-model
                    // run's primary is not the neural view).
                    SavePng(Path.Combine(_demoDir, DemoFileFor(
                            _pinnedViews?[0].Name ?? "Lanczos3")), frameBufferWidth,
                        frameBufferHeight);
                    WriteReport(_demoDir, frameBufferWidth, frameBufferHeight);
                    _demoStage = 4;
                    requestClose();
                }
                else if (_state == State.Failed)
                {
                    WriteReport(_demoDir, frameBufferWidth, frameBufferHeight);
                    _demoStage = 4;
                    requestClose();
                }
                break;
            case 3:
                if (_state == State.Presenting)
                {
                    SavePng(Path.Combine(_demoDir, DemoFileFor(
                            _pinnedViews![0].Name)), frameBufferWidth,
                        frameBufferHeight);
                    if (_pinnedViews is null || _pinnedViews.Length < 2)
                    {
                        // No A/B pair: record the fact rather than faking
                        // a duplicate.
                        Console.WriteLine(
                            "dlss demo: no A/B pair (the alternate pass failed)");
                        _abMissing = true;
                        WriteReport(_demoDir, frameBufferWidth, frameBufferHeight);
                        _demoStage = 4;
                        requestClose();
                        break;
                    }
                    _demoViewIndex = 1;
                    ToggleAB();
                    _demoStage = 5;
                }
                else if (_state == State.Failed)
                {
                    WriteReport(_demoDir, frameBufferWidth, frameBufferHeight);
                    _demoStage = 4;
                    requestClose();
                }
                break;
            case 5:
                // One frame after the A/B swap: the swapped texture has
                // been presented (a screenshot right after the upload
                // would capture the PREVIOUS view - the files must differ).
                if (_state == State.Presenting)
                {
                    string FileFor(string name) => name switch
                    {
                        "neural" => "processed.png",
                        "bilinear" => "baseline.png",
                        _ => "lanczos3.png",
                    };
                    var viewFile = FileFor(_pinnedViews![
                        Math.Min(_demoViewIndex, _pinnedViews.Length - 1)].Name);
                    SavePng(Path.Combine(_demoDir, viewFile), frameBufferWidth,
                        frameBufferHeight);
                    _demoViewIndex++;
                    if (_demoViewIndex < _pinnedViews.Length)
                    {
                        ToggleAB();
                        break;
                    }
                    WriteReport(_demoDir, frameBufferWidth, frameBufferHeight);
                    _demoStage = 4;
                    requestClose();
                }
                else
                {
                    WriteReport(_demoDir, frameBufferWidth, frameBufferHeight);
                    _demoStage = 4;
                    requestClose();
                }
                break;
        }
    }

    private void WriteReport(string demoDir, int width, int height)
    {
        // The honest per-view measurement: each presented view compared
        // against the ground truth (the same view rendered at the
        // presented size). PSNR/SSIM on the RGB bytes.
        var quality = new System.Text.StringBuilder();
        if (_reference is not null && _pinnedViews is not null)
        {
            var w = _pipeline!.OutputWidth;
            var h = _pipeline.OutputHeight;
            // The pre-declared centre crop (the central 50%): the region
            // where the subject carries detail, reported separately from
            // the full frame so the dilution by smooth background is
            // visible in the numbers.
            var cx0 = w / 4;
            var cy0 = h / 4;
            var cx1 = w - w / 4;
            var cy1 = h - h / 4;
            var refDetail = DetailEnergy(_reference, w, h, cx0, cy0, cx1, cy1);

            string Plain(string name) => name switch
            {
                "neural" => $"neural upscaler ({Dlss5Pipeline.LoadedModelName})",
                "bilinear" => "plain stretch baseline",
                "synthetic" => "synthetic pipeline",
                _ => "classical sharp stretch",
            };
            // A near-flat reference carries no detail to "keep"; such rows
            // report n/a rather than an unbounded percentage.
            var detailMeaningful = refDetail >= 1.0;
            var rows = new List<(string View, double DetailPct, double Psnr,
                double Ssim)>();
            for (var i = 0; i < _pinnedViews.Length; ++i)
            {
                var (psnr, ssim) = QualityVs(_reference, _pinnedViews[i].Buf,
                    w, h);
                var detail = DetailEnergy(_pinnedViews[i].Buf, w, h, cx0, cy0,
                    cx1, cy1);
                rows.Add((Plain(_pinnedViews[i].Name),
                    detailMeaningful ? 100.0 * detail / refDetail : 0.0,
                    psnr, ssim));
            }

            quality.AppendLine($"  subject: {AssetName} (the scene in the screenshots)");
            quality.AppendLine(
                "  what the numbers mean: every view below was made from ONE");
            quality.AppendLine(
                $"  capture of this scene at low resolution ({_captureW}x{_captureH}), then");
            quality.AppendLine(
                $"  enlarged {_pipeline.DemoScale}x (the live render above is the reference).");
            quality.AppendLine(
                "  \"detail kept\" is how much of the fine structure (hair,");
            quality.AppendLine(
                "  edges, texture) survives the enlargement - 100% = what the");
            quality.AppendLine(
                "  full-resolution live render has. \"sharpness\" and");
            quality.AppendLine(
                "  \"accuracy\" are standard scores against that live render");
            quality.AppendLine(
                "  (higher = closer).");
            quality.AppendLine();
            quality.AppendLine("  view                      detail kept   sharpness   accuracy");
            foreach (var (view, pct, psnr, ssim) in rows)
            {
                quality.AppendLine(detailMeaningful
                    ? $"  {view,-25} {pct,9:F0}%   {psnr,8:F2} dB   {ssim,8:F4}"
                    : $"  {view,-25} {"n/a",9}   {psnr,8:F2} dB   {ssim,8:F4}");
            }
            if (detailMeaningful && rows.Exists(r => r.DetailPct > 100.0))
            {
                quality.AppendLine(
                    "  (over 100% detail kept = more fine grain than the live");
                quality.AppendLine(
                    "  render - typically ringing or sharpening artefacts.)");
            }
            quality.AppendLine();

            // The plain-words verdict, computed from the actual rows: no
            // sentence here may assert anything the table above contradicts.
            var neural = rows.FindAll(r => r.View.StartsWith("neural"));
            var classical = rows.FindAll(r => !r.View.StartsWith("neural"));
            if (neural.Count > 0 && classical.Count > 0 && detailMeaningful)
            {
                var bestNeural = neural.OrderByDescending(r => r.DetailPct).First();
                var bestClassical = classical.OrderByDescending(r => r.DetailPct).First();
                if (bestNeural.DetailPct > bestClassical.DetailPct + 5)
                {
                    quality.AppendLine(
                        $"  In plain words: the neural result keeps {bestNeural.DetailPct:F0}% of the");
                    quality.AppendLine(
                        $"  fine detail where the {bestClassical.View} keeps {bestClassical.DetailPct:F0}% -");
                    quality.AppendLine(
                        "  that is the visible difference in the zoom strip of");
                    quality.AppendLine(
                        "  composite.png. The accuracy scores:");
                    if (bestNeural.Ssim < bestClassical.Ssim)
                    {
                        var bestSharp = classical.OrderByDescending(r => r.Ssim).First();
                        quality.AppendLine(
                            $"  favour the {bestSharp.View} ({bestSharp.Ssim:F4} vs {bestNeural.Ssim:F4}):");
                        quality.AppendLine(
                            "  blurring scores well against a smooth reference, so BOTH");
                        quality.AppendLine(
                            "  results are reported rather than picking the flattering one.");
                    }
                    else
                    {
                        quality.AppendLine(
                            $"  also favour the neural result ({bestNeural.Ssim:F4} vs {bestClassical.Ssim:F4})");
                        quality.AppendLine(
                            "  on this capture - both the detail and the scores agree here.");
                    }
                }
                else
                {
                    quality.AppendLine(
                        "  In plain words: on this capture the neural result did NOT");
                    quality.AppendLine(
                        "  keep visibly more detail than the classical views - the");
                    quality.AppendLine(
                        "  visible-detail claim is downgraded accordingly; the");
                    quality.AppendLine(
                        "  screenshots are the evidence either way.");
                }
            }
            else if (!detailMeaningful)
            {
                quality.AppendLine(
                    "  In plain words: this scene is too smooth to measure detail");
                quality.AppendLine(
                    "  recovery on; no detail claim is made from this run.");
            }
            quality.AppendLine();
            quality.AppendLine(
                "  technical record (for the measurement itself): detail energy");
            quality.AppendLine(
                "  = mean squared Laplacian of the luminance x1000 in the centre");
            quality.AppendLine(
                $"  crop; the ground truth carries {refDetail:F1}. The scores are");
            quality.AppendLine(
                "  computed on the full RGB frame.");
        }
        else
        {
            quality.AppendLine("  (no ground-truth capture available)");
        }
        var fileView = new Dictionary<string, string>();
        if (_pinnedViews is not null)
        {
            foreach (var v in _pinnedViews)
            {
                fileView[DemoFileFor(v.Name)] = PrettyView(v.Name);
            }
        }
        var legend = new System.Text.StringBuilder();
        legend.AppendLine("  the screenshots (all from ONE capture of one view):");
        legend.AppendLine("  live.png ........ top-left of composite.png: the "
            + "full-resolution live render");
        var positions = new Dictionary<string, string>
        {
            ["processed.png"] = "top-right",
            ["baseline.png"] = "bottom-left",
            ["lanczos3.png"] = "bottom-right",
            ["synthetic.png"] = "bottom-right",
        };
        foreach (var (file, pos) in positions)
        {
            if (fileView.TryGetValue(file, out var view))
            {
                legend.AppendLine($"  {file,-17} {pos}: {view} (the same capture, "
                    + (_demoShots.ContainsKey(file) ? "re-processed)" : "NOT CAPTURED in this run)"));
            }
        }
        legend.AppendLine("  processing.png .. the in-flight state (the latency is "
            + "visible in the text)");
        legend.AppendLine("  composite.png ... the views side by side (top: 2x2 "
            + "self-labelled full views; bottom: 2x-zoomed crops of the CENTRE "
            + "QUARTER of the frame - a fixed, pre-declared region inside the "
            + "larger centre crop the quality numbers measure; this strip is "
            + "where the upscaling difference is visible to the eye)");

        var labels = ViewLabels;
        var labelBlock = new System.Text.StringBuilder();
        foreach (var l in labels)
        {
            labelBlock.AppendLine($"  {l}");
        }
        var report = $"""
            Centre Stage x dlss5-linux native host - unattended demo run
            date (local): {DateTime.Now:dd/MM/yyyy HH:mm:ss}
            window framebuffer: {width}x{height}
            capture geometry: {_captureW}x{_captureH} -> {_pipeline?.OutputWidth}x{_pipeline?.OutputHeight} presented
            views (F10 cycles at the SAME capture): {string.Join(", ", labels)}
            A/B pair: {((_pipeline?.DemoScale ?? 0) == 0 ? "not attempted (synthetic mode)" : _abMissing ? "MISSING (an alternate pass failed)" : $"{_pinnedViews?.Length ?? 0} view(s) captured")}
            transform label (recorded here, not shown on screen): {Label}
            frames processed: {_processed}
            last evaluate: {_lastSeconds:F2} s (worker wall clock around the native call)
            capture-to-presented: {_presentationSeconds:F2} s (includes the render loop's
              cadence; the loop is throttled while the session is idle-blanked)
            frames since reset: {_pipeline?.Counter ?? 0}
            state: {_state}{(_failure is null ? "" : $" ({_failure})")}
            quality vs ground truth (the real capture):
            {quality.ToString().TrimEnd()}
            per-view labels:
            {labelBlock.ToString().TrimEnd()}
            {legend}
            NOT DLSS5 in any case: this seam proves the pipeline end to end,
            never vendor parity. Frame-by-frame processing with visible latency,
            not real-time (the neural view is seconds per frame on CPU - see
            the evaluate line above).
            """;
        File.WriteAllText(Path.Combine(demoDir, "report.txt"), report);
        BuildComposite(width, height);
        Console.WriteLine(report);
    }

    /// <summary>PSNR and SSIM of an 8-bit RGB buffer against the ground truth.</summary>
    private static (double Psnr, double Ssim) QualityVs(byte[] reference,
        byte[] view, int w, int h)
    {
        return QualityVs(reference, view, w, h, 0, 0, w, h);
    }

    /// <summary>The mean squared Laplacian of the luminance (x1000).</summary>
    private static double DetailEnergy(byte[] img, int w, int h, int x0,
        int y0, int x1, int y1)
    {
        double sum = 0;
        long n = 0;
        for (var y = Math.Max(1, y0); y < Math.Min(h - 1, y1); ++y)
        {
            for (var x = Math.Max(1, x0); x < Math.Min(w - 1, x1); ++x)
            {
                var idx = (y * w + x) * 3;
                var idxl = (y * w + x - 1) * 3;
                var idxr = (y * w + x + 1) * 3;
                var idxu = ((y - 1) * w + x) * 3;
                var idxd = ((y + 1) * w + x) * 3;
                // The luminance (the text says luminance; the metric
                // computes the luminance).
                static double Lum(byte[] im, int i) =>
                    0.299 * im[i] + 0.587 * im[i + 1] + 0.114 * im[i + 2];
                var lap = Lum(img, idxl) + Lum(img, idxr) + Lum(img, idxu)
                          + Lum(img, idxd) - 4.0 * Lum(img, idx);
                sum += lap * lap;
                n++;
            }
        }
        return n > 0 ? sum / n * 1000.0 : 0;
    }

    /// <summary>PSNR and SSIM over a crop of the frame.</summary>
    private static (double Psnr, double Ssim) QualityVs(byte[] reference,
        byte[] view, int w, int h, int x0, int y0, int x1, int y1)
    {
        if (reference.Length != view.Length || reference.Length == 0)
        {
            return (0, 0);
        }
        double mse = 0;
        long samples = 0;
        for (var y = y0; y < y1; ++y)
        {
            for (var x = x0; x < x1; ++x)
            {
                var idx = (y * w + x) * 3;
                for (var c = 0; c < 3; ++c)
                {
                    var d = reference[idx + c] - view[idx + c];
                    mse += d * d;
                    samples++;
                }
            }
        }
        mse = samples > 0 ? mse / samples : 0;
        var psnr = mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        // A single-window SSIM on the luminance (8x8 blocks, the standard
        // constants) - honest and documented as such.
        double ssimSum = 0;
        var blocks = 0;
        const int n = 8;
        if ((long)w * h * 3 != reference.Length)
        {
            return (psnr, 0);
        }
        for (var by = Math.Max(y0, (y0 / n) * n); by + n <= y1; by += n)
        {
            for (var bx = Math.Max(x0, (x0 / n) * n); bx + n <= x1; bx += n)
            {
                double m1 = 0, m2 = 0;
                for (var y = 0; y < n; ++y)
                {
                    for (var x = 0; x < n; ++x)
                    {
                        var idx = ((by + y) * w + (bx + x)) * 3;
                        var l1 = 0.299 * reference[idx] + 0.587 * reference[idx + 1]
                                 + 0.114 * reference[idx + 2];
                        var l2 = 0.299 * view[idx] + 0.587 * view[idx + 1]
                                 + 0.114 * view[idx + 2];
                        m1 += l1;
                        m2 += l2;
                    }
                }
                m1 /= n * n;
                m2 /= n * n;
                double v1 = 0, v2 = 0, cov = 0;
                for (var y = 0; y < n; ++y)
                {
                    for (var x = 0; x < n; ++x)
                    {
                        var idx = ((by + y) * w + (bx + x)) * 3;
                        var l1 = 0.299 * reference[idx] + 0.587 * reference[idx + 1]
                                 + 0.114 * reference[idx + 2];
                        var l2 = 0.299 * view[idx] + 0.587 * view[idx + 1]
                                 + 0.114 * view[idx + 2];
                        v1 += (l1 - m1) * (l1 - m1);
                        v2 += (l2 - m2) * (l2 - m2);
                        cov += (l1 - m1) * (l2 - m2);
                    }
                }
                v1 /= n * n - 1;
                v2 /= n * n - 1;
                cov /= n * n - 1;
                const double c1 = 6.5025, c2 = 58.5225;
                ssimSum += ((2 * m1 * m2 + c1) * (2 * cov + c2))
                           / ((m1 * m1 + m2 * m2 + c1) * (v1 + v2 + c2));
                blocks++;
            }
        }
        return (psnr, blocks > 0 ? ssimSum / blocks : 0);
    }

    private void SavePng(string path, int width, int height)
    {
        // Evidence capture: a write failure fails the run but must never take
        // down the render loop mid-demo.
        try
        {
            var rgba = Screenshot.CaptureRgba(_gl, width, height);
            Screenshot.WriteRgbaPng(path, rgba, width, height);
            _demoShots[Path.GetFileName(path)] = rgba;
            Console.WriteLine($"  dlss demo: captured {width}x{height} -> {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: screenshot '{path}' failed: {ex.Message}");
            Environment.ExitCode = 3;
        }
    }

    /// <summary>
    /// The mouse affordances: the state's buttons at the top-right and the
    /// right-click context menu on top. Plain words, same actions as the
    /// keys; the buttons are also IN the screenshots, so the evidence
    /// shows the first-time viewer where to click.
    /// </summary>
    private void DrawControls(StatusOverlay overlay, int frameWidth, int frameHeight)
    {
        if (_pipeline is null)
        {
            return;
        }

        var bg = new Vector4(0.10f, 0.11f, 0.14f, 0.88f);
        var fg = new Vector4(0.92f, 0.94f, 0.97f, 1f);
        var dim = new Vector4(0.55f, 0.58f, 0.64f, 1f);
        var accent = new Vector4(0.25f, 0.42f, 0.68f, 0.95f);

        var buttons = Buttons;
        for (var i = 0; i < buttons.Count; ++i)
        {
            var (action, text) = buttons[i];
            var rect = CentreStage.Core.DemoUi.Button(frameWidth, i);
            overlay.FillRect(rect.X, rect.Y, rect.W, rect.H, bg);
            var enabled = action is { } a && ActionEnabled(a);
            var label = enabled || action is null ? text : text + " (busy)";
            var width = overlay.TextWidth(label, 17f);
            overlay.DrawText(label, rect.X + (rect.W - width) * 0.5f,
                rect.Y + (rect.H - 20f) * 0.5f, 17f, enabled ? fg : dim);
        }

        if (_ui.MenuOpen)
        {
            DrawMenu(overlay);
        }

        // The batch must flush HERE on every path: DrawStatus/DrawHud reset
        // the vertex buffer at the start of their next call.
        overlay.Flush();
    }

    private void DrawMenu(StatusOverlay overlay)
    {
        var bg = new Vector4(0.10f, 0.11f, 0.14f, 0.88f);
        var fg = new Vector4(0.92f, 0.94f, 0.97f, 1f);
        var dim = new Vector4(0.55f, 0.58f, 0.64f, 1f);
        var accent = new Vector4(0.25f, 0.42f, 0.68f, 0.95f);
        var itemCount = CentreStage.Core.DemoUi.MenuActions.Length;
        var panel = _ui.MenuPanel(itemCount);
        overlay.FillRect(panel.X, panel.Y, panel.W, panel.H, bg);
        for (var i = 0; i < itemCount; ++i)
        {
            var action = CentreStage.Core.DemoUi.MenuActions[i];
            var item = _ui.MenuItem(i);
            if (i == _hoverItem)
            {
                overlay.FillRect(item.X, item.Y, item.W, item.H, accent);
            }
            var text = action switch
            {
                CentreStage.Core.DemoAction.Enlarge => "Enlarge this view  (F9)",
                CentreStage.Core.DemoAction.NextView => "Next comparison view  (F10)",
                _ => "Back to live view",
            };
            var enabled = ActionEnabled(action);
            var width = overlay.TextWidth(text, 17f);
            overlay.DrawText(text, item.X + (item.W - width) * 0.5f,
                item.Y + (item.H - 20f) * 0.5f, 17f, enabled ? fg : dim);
        }
    }

    private static string DemoFileFor(string name) => name switch
    {
        "neural" => "processed.png",
        "bilinear" => "baseline.png",
        "synthetic" => "synthetic.png",
        _ => "lanczos3.png",
    };

    private static string PrettyView(string name) => name switch
    {
        "neural" => "neural upscaler result",
        "bilinear" => "plain stretch baseline",
        "synthetic" => "synthetic pipeline result",
        _ => "classical sharp stretch",
    };

    /// <summary>
    /// The side-by-side evidence: a 2x2 grid of the captured screenshots.
    /// Each panel is the full window with its HUD status line, so the
    /// composite labels itself. Missing views leave dark slots.
    /// </summary>
    private void BuildComposite(int width, int height)
    {
        if (_demoDir is null)
        {
            return;
        }
        var slots = new[] { "live.png", "processed.png", "baseline.png", "lanczos3.png" };
        // Layout: the four self-labelled full views in a 2x2 grid, then a
        // strip of 2x-zoomed crops of the pre-declared centre region (the
        // region the quality numbers measure - not cherry-picked), one per
        // view, each labelled in-image. The zoom strip is where the
        // difference is visible to a human eye.
        var fileView = new Dictionary<string, string>();
        if (_pinnedViews is not null)
        {
            foreach (var v in _pinnedViews)
            {
                fileView[DemoFileFor(v.Name)] = PrettyView(v.Name);
            }
        }
        string ZoomLabel(int s) => s == 0
            ? "2x ZOOM - LIVE RENDER"
            : _demoShots.ContainsKey(slots[s]) && fileView.TryGetValue(slots[s], out var v)
                ? $"2x ZOOM - {v}"
                : $"2x ZOOM - {slots[s]} NOT CAPTURED";
        var cw = width * 2;
        var ch = height * 2 + height / 2; // 2x2 grid + one 400px zoom strip
        var composite = new byte[cw * ch * 4];
        for (var s = 0; s < slots.Length; ++s)
        {
            if (!_demoShots.TryGetValue(slots[s], out var panel)
                || panel.Length != width * height * 4)
            {
                continue;
            }
            var ox = (s % 2) * width;
            var oy = (s / 2) * height;
            for (var y = 0; y < height; ++y)
            {
                Array.Copy(panel, y * width * 4,
                    composite, ((oy + y) * cw + ox) * 4, width * 4);
            }

            // The zoom crop: the CENTRE QUARTER of the view (320x200 at
            // the default window) - a fixed, pre-declared region, nested
            // inside the larger central-50% crop the quality numbers
            // measure - enlarged 2x into a 640x400 panel with nearest
            // sampling, so the crop shows the actual pixels each upscaler
            // produced (no new smoothing in the evidence itself).
            var zw = width / 4;
            var zh = height / 4;
            var zx0 = width / 2 - zw / 2;
            var zy0 = height / 2 - zh / 2;
            var zox = s * (width / 2);
            for (var y = 0; y < zh * 2; ++y)
            {
                var sy = zy0 + y / 2;
                for (var x = 0; x < zw * 2; ++x)
                {
                    var sx = zx0 + x / 2;
                    var srcIdx = (sy * width + sx) * 4;
                    var dstIdx = ((height * 2 + y) * cw + zox + x) * 4;
                    composite[dstIdx] = panel[srcIdx];
                    composite[dstIdx + 1] = panel[srcIdx + 1];
                    composite[dstIdx + 2] = panel[srcIdx + 2];
                    composite[dstIdx + 3] = 255;
                }
            }
            TinyText.Draw(composite, cw, ch, zox + 8, height * 2 + 8, ZoomLabel(s));
        }
        try
        {
            Screenshot.WriteRgbaPng(Path.Combine(_demoDir, "composite.png"),
                composite, cw, ch);
            Console.WriteLine(
                $"  dlss demo: composite {cw}x{ch} -> composite.png");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: composite failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // A faulted worker must not throw during teardown; its failure was
        // already reported when the frame completed (or is unobserved here).
        try
        {
            _worker?.Wait();
        }
        catch (AggregateException)
        {
        }
        _pipeline?.Dispose();
        _stage.Dispose();
    }
}
