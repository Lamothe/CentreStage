// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Michael Lamothe
//
// The DLSS seam's workflow: on-demand frame-by-frame processing with VISIBLE
// latency (the demonstration transform costs milliseconds, the synthetic
// geometry — it is never presented as real-time). One trigger captures the
// current view into the low-resolution framebuffer, evaluates it on a worker
// thread while the live view keeps rendering, then pins the returned frame as
// the presented view with the in-band label ("synthetic weights — arbitrary
// transform") in the status text. Any camera input returns to the live view.
// The app's own workflow is untouched when the seam is idle.
//
// The unattended demo mode (--dlss-demo <dir>) drives the same path: it
// screenshots the live view, processes one frame, screenshots the presented
// result (label in-band), writes a report with the measured latency and
// closes — evidence to disk for headless-at-the-keyboard runs.
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
                "neural" => "neural demonstration",
                "bilinear" => "bilinear baseline",
                _ => "Lanczos3 demonstration",
            };
            return $"view: {Pretty(_pinnedViews[i].Name)} (F10: {next})";
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
                    labels.Add(v.Name == "neural"
                        ? "neural: " + Dlss5Pipeline.LoadedModelLabel
                        : $"{v.Name}: not DLSS5 - classical demonstration " +
                          $"scaler ({(v.Name == "bilinear" ? "bilinear baseline" : "Lanczos3")})");
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

    /// <summary>The in-band label shown with every presented frame.</summary>
    /// <summary>The label for the current mode (the demonstration
    /// transform's mandatory label in demo mode).</summary>
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
                string NameFor(int kernel) => kernel == 3
                    ? "neural"
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
        if (_state == State.Processing)
        {
            overlay.DrawHud(frameBufferWidth, frameBufferHeight,
                $"DLSS: processing {_captureW}x{_captureH} frame {_processed + 1} "
                + $"({_evaluateClock.Elapsed.TotalSeconds:F1} s so far) - frame-by-frame, not real-time",
                17f, new Vector4(0.95f, 0.85f, 0.55f, 0.95f));
        }
        else if (_state == State.Presenting)
        {
            overlay.DrawStatus(frameBufferWidth, frameBufferHeight, Label,
                $"{_captureW}x{_captureH} -> {_pipeline?.OutputWidth}x{_pipeline?.OutputHeight} "
                + $"processed in {_lastSeconds:F2} s - {ViewName} - "
                + $"frame-by-frame, not real-time ({_processed} done; "
                + "camera or F9: live view)",
                -1f, elapsed);
        }
        else if (_state == State.Failed)
        {
            overlay.DrawHud(frameBufferWidth, frameBufferHeight,
                $"DLSS seam failed: {_failure}", 17f, new Vector4(0.95f, 0.45f, 0.45f, 0.95f));
        }

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
                    SavePng(Path.Combine(_demoDir, "processed.png"), frameBufferWidth,
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
                    string PrimaryFile(string name) => name switch
                    {
                        "neural" => "processed.png",
                        "bilinear" => "baseline.png",
                        _ => "lanczos3.png",
                    };
                    SavePng(Path.Combine(_demoDir, PrimaryFile(
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
            var names = _pinnedViews.Select(v => v.Name == "neural"
                    ? $"neural ({Dlss5Pipeline.LoadedModelName})"
                    : v.Name == "bilinear" ? "bilinear baseline" : "Lanczos3")
                .ToArray();
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
            quality.AppendLine(
                $"  ground-truth detail energy (centre crop): {refDetail:F1}");
            for (var i = 0; i < _pinnedViews.Length && i < names.Length; ++i)
            {
                var (psnr, ssim) = QualityVs(_reference, _pinnedViews[i].Buf,
                    w, h);
                var (cpsnr, cssim) = QualityVs(_reference, _pinnedViews[i].Buf,
                    w, h, cx0, cy0, cx1, cy1);
                var detail = DetailEnergy(_pinnedViews[i].Buf, w, h, cx0, cy0,
                    cx1, cy1);
                quality.AppendLine(
                    $"  view {names[i]}: full frame PSNR {psnr:F2} dB, " +
                    $"SSIM {ssim:F4}; centre crop PSNR {cpsnr:F2} dB, " +
                    $"SSIM {cssim:F4}; detail energy {detail:F1}");
            }
            quality.AppendLine(
                "  ground truth: the scene rendered at the presented size " +
                "(real content, the demo's actual capture). Detail energy " +
                "= mean squared Laplacian of the luminance x1000 in the " +
                "centre crop (how much fine structure each view carries).");
        }
        else
        {
            quality.AppendLine("  (no ground-truth capture available)");
        }
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
            in-band label: {Label}
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
            evidence: live.png (live view), processing.png (in-flight, latency visible),
                      one file per view: {string.Join(" + ", _pinnedViews?.Select(v => v.Name == "neural" ? "processed.png" : v.Name == "bilinear" ? "baseline.png" : "lanczos3.png") ?? Array.Empty<string>())}
            NOT DLSS5 in any case: this seam proves the pipeline end to end,
            never vendor parity. Frame-by-frame processing with visible latency,
            not real-time (the neural view is seconds per frame on CPU - see
            the evaluate line above).
            """;
        File.WriteAllText(Path.Combine(demoDir, "report.txt"), report);
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
            Screenshot.SavePng(_gl, path, width, height);
            Console.WriteLine($"  dlss demo: captured {width}x{height} -> {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: screenshot '{path}' failed: {ex.Message}");
            Environment.ExitCode = 3;
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
