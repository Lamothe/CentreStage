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
    private Task<(Dlss5Status Status, byte[] Out, double Seconds)>? _worker;
    private double _lastSeconds;
    private double _presentationSeconds;
    private int _processed;
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
                _pipeline = new Dlss5Pipeline(DlssStage.LowWidth, DlssStage.LowHeight, threads: 8);
                // The scope-pivot visible-quality path: the demonstration
                // transform (NOT DLSS5 - real openly documented Lanczos3
                // weights) runs behind the identical ABI/capture/present
                // seam. Geometry: the 64x72 capture upscales 4x to a
                // 256x288 presented frame. Frame-by-frame, not real-time.
                var demoStatus = _pipeline.SetDemoTransform(4);
                if (demoStatus != Dlss5Status.Ok)
                {
                    Console.Error.WriteLine(
                        $"error: demo transform refused: {demoStatus}");
                }
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
        if (_capturePending && _pipeline is not null)
        {
            _capturePending = false;
            _stage.BeginCapture();
            drawScene();
            var capture = _stage.EndCapture(frameBufferWidth, frameBufferHeight);
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
                var output = new byte[pipeline.OutputWidth * pipeline.OutputHeight * 3];
                var status = pipeline.Evaluate(capture, output, reset);
                return (status, output, clock.Elapsed.TotalSeconds);
            });
            _state = State.Processing;
        }

        if (_state == State.Processing && _worker is { IsCompleted: true })
        {
            try
            {
                var (status, output, evaluateSeconds) = _worker.GetAwaiter().GetResult();
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
                    _stage.UploadResult(output, _pipeline!.OutputWidth, _pipeline!.OutputHeight);
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
                $"DLSS: processing {DlssStage.LowWidth}x{DlssStage.LowHeight} frame {_processed + 1} "
                + $"({_evaluateClock.Elapsed.TotalSeconds:F1} s so far) - frame-by-frame, not real-time",
                17f, new Vector4(0.95f, 0.85f, 0.55f, 0.95f));
        }
        else if (_state == State.Presenting)
        {
            overlay.DrawStatus(frameBufferWidth, frameBufferHeight, Label,
                $"{DlssStage.LowWidth}x{DlssStage.LowHeight} processed in {_lastSeconds:F2} s - "
                + $"frame-by-frame, not real-time ({_processed} done, {(_pipeline?.Counter ?? 0)} since reset; "
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
        }
    }

    private void WriteReport(string demoDir, int width, int height)
    {
        var report = $"""
            Centre Stage x dlss5-linux native host - unattended demo run
            date (local): {DateTime.Now:dd/MM/yyyy HH:mm:ss}
            window framebuffer: {width}x{height}
            low-resolution frame: {DlssStage.LowWidth}x{DlssStage.LowHeight} (the native boundary's demo geometry)
            in-band label: {Label}
            frames processed: {_processed}
            last evaluate: {_lastSeconds:F2} s (worker wall clock around the native call)
            capture-to-presented: {_presentationSeconds:F2} s (includes the render loop's
              cadence; the loop is throttled while the session is idle-blanked)
            frames since reset: {_pipeline?.Counter ?? 0}
            arena high-water: {_pipeline?.ArenaHighWaterFloats ?? 0} floats
            state: {_state}{(_failure is null ? "" : $" ({_failure})")}
            evidence: live.png (live view), processing.png (in-flight, latency visible),
                      processed.png (presented result, label in-band)
            {(_pipeline?.DemoScale > 0
                ? "not DLSS5 - demonstration transform with real weights (Lanczos3, " +
                  $"x{_pipeline.DemoScale} upscale; quality measured in dlss5-linux's " +
                  "vr/tests/native-demo-upscale-test.cpp: PSNR/SSIM vs a bilinear baseline)"
                : "synthetic weights - the transformation is arbitrary; no image-quality or vendor-parity claim")}
            NOT DLSS5 in any case: this seam proves the pipeline end to end,
            never vendor parity. Frame-by-frame processing with visible latency,
            not real-time.
            """;
        File.WriteAllText(Path.Combine(demoDir, "report.txt"), report);
        Console.WriteLine(report);
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
