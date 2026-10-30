// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Michael Lamothe
//
// P/Invoke over the dlss5-linux native host boundary (vr/native-host.h).
// The ABI is the tested C surface: create/evaluate/destroy over RGB frames,
// the frames-since-reset counter, the arena report and the mode label that
// a host MUST display alongside any presented output ("not DLSS5 -
// demonstration transform with real weights (<model>, <license>)" for the
// neural path; "not DLSS5 - classical demonstration scaler (Lanczos3 /
// bilinear baseline)" for the fixed kernels; "synthetic weights - arbitrary
// transform" otherwise). The demonstration
// evaluator costs seconds per frame. Both are on-demand frame-by-frame
// processing with visible latency, never real-time upscaling.
using System.Reflection;
using System.Runtime.InteropServices;

namespace CentreStage;

internal enum Dlss5Status
{
    Ok = 0,
    NullPointer = 1,
    InvalidDimensions = 2,
    InvalidStride = 3,
    InvalidValues = 4,
    InvalidControls = 5,
    InvalidBuffer = 6,
}

/// <summary>
/// Raw imports of vr/native-host.h. The library resolves through
/// <see cref="ResolveNative"/>: CENTRESTAGE_DLSS_LIB (an explicit path) wins,
/// then the runtimes folder next to the app binary, then normal probing.
/// </summary>
internal static partial class Dlss5Native
{
    private const string Lib = "dlss5_nr_host";

    static Dlss5Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Dlss5Native).Assembly, ResolveNative);
    }

    private static IntPtr ResolveNative(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib)
        {
            return IntPtr.Zero;
        }

        var explicitPath = Environment.GetEnvironmentVariable("CENTRESTAGE_DLSS_LIB");
        if (!string.IsNullOrEmpty(explicitPath) && NativeLibrary.TryLoad(explicitPath, out var fromEnv))
        {
            return fromEnv;
        }

        var local = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native",
            "libdlss5_nr_host.so");
        if (NativeLibrary.TryLoad(local, out var fromRuntimes))
        {
            return fromRuntimes;
        }

        return NativeLibrary.TryLoad("libdlss5_nr_host.so", assembly, searchPath, out var probed)
            ? probed
            : IntPtr.Zero;
    }

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_label")]
    internal static partial IntPtr Label();

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_status_name")]
    internal static partial IntPtr StatusName(Dlss5Status status);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_create")]
    internal static partial Dlss5Status Create(int width, int height, int threads, out IntPtr handle);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_set_demo_transform")]
    internal static partial Dlss5Status SetDemoTransform(IntPtr handle, int scale);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_set_demo_kernel")]
    internal static partial Dlss5Status SetDemoKernel(IntPtr handle, int kernel);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_demo_model_load",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial Dlss5Status DemoModelLoad(string path);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_demo_model_loaded")]
    internal static partial int DemoModelLoaded();

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_demo_model_label")]
    internal static partial IntPtr DemoModelLabel();

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_label_for")]
    internal static partial IntPtr LabelFor(IntPtr handle);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_evaluate")]
    internal static partial Dlss5Status Evaluate(IntPtr handle, byte[] rgb, int rowStride,
        byte[] outRgb, int outStride, int reset);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_counter")]
    internal static partial uint Counter(IntPtr handle);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_arena_high_water")]
    internal static partial Dlss5Status ArenaHighWater(IntPtr handle, out nuint floats);

    [LibraryImport(Lib, EntryPoint = "dlss5_nr_destroy")]
    internal static partial void Destroy(IntPtr handle);
}

/// <summary>
/// One native pipeline over width x height RGB frames. The wrapper mirrors
/// the ABI's contract: the output buffer is untouched unless the call
/// returns Ok, and <see cref="Label"/> is the string the host displays with
/// any presented frame (synthetic weights — the transform is arbitrary).
/// </summary>
internal sealed class Dlss5Pipeline : IDisposable
{
    private IntPtr _handle;

    public Dlss5Pipeline(int width, int height, int threads)
    {
        Width = width;
        Height = height;
        var status = Dlss5Native.Create(width, height, threads, out _handle);
        if (status != Dlss5Status.Ok || _handle == IntPtr.Zero)
        {
            // Defensive against ABI drift: the native side nulls the handle
            // on failure, but never leak one it did hand back.
            if (_handle != IntPtr.Zero)
            {
                Dlss5Native.Destroy(_handle);
                _handle = IntPtr.Zero;
            }
            throw new InvalidOperationException(
                $"dlss5_nr_create failed: {StatusName(status)} ({status})");
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The in-band label a host must display with presented output.</summary>
    public static string Label =>
        Marshal.PtrToStringUTF8(Dlss5Native.Label()) ?? "synthetic weights — arbitrary transform";

    /// <summary>
    /// The label for THIS pipeline's mode, mandatory on every presented
    /// frame. The demonstration path's label names the actual transform
    /// ("demonstration transform with real weights (<model>)" for the
    /// neural path; "classical demonstration scaler (Lanczos3 / bilinear
    /// baseline)" for the fixed kernels).
    /// </summary>
    public string LabelFor =>
        Marshal.PtrToStringUTF8(Dlss5Native.LabelFor(_handle)) ?? Label;

    /// <summary>The demonstration-transform upscale factor (0 = off).</summary>
    public int DemoScale { get; private set; }

    /// <summary>The presented extent (scale x the capture in demo mode).</summary>
    public int OutputWidth => Width * Math.Max(1, DemoScale);
    public int OutputHeight => Height * Math.Max(1, DemoScale);

    /// <summary>
    /// Enables the demonstration transform (NOT DLSS5): a real upscaler
    /// runs behind the same ABI (a learned model with real open weights
    /// when loaded, or the classical Lanczos3 scaler) so the presented
    /// result visibly differs at the demo geometry. scale 2..4; 0 restores
    /// the synthetic pipeline.
    /// </summary>
    /// <summary>The A/B kernel: 1 = Lanczos3, 2 = the bilinear baseline,
    /// 3 = the neural demonstration (requires loaded real weights).</summary>
    public Dlss5Status SetDemoKernel(int kernel)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        return Dlss5Native.SetDemoKernel(_handle, kernel);
    }

    public Dlss5Status SetDemoTransform(int scale)
    {
        var status = Dlss5Native.SetDemoTransform(_handle, scale);
        if (status == Dlss5Status.Ok)
        {
            DemoScale = scale;
        }
        return status;
    }

    /// <summary>
    /// Load the demonstration model (real open weights: EDSR-base x2 or
    /// Real-ESRGAN x2, both openly licensed). Process-global; loaded once
    /// at startup. NOT DLSS5 - a demonstration.
    /// </summary>
    public static Dlss5Status LoadDemoModel(string path)
    {
        var status = Dlss5Native.DemoModelLoad(path);
        if (status == Dlss5Status.Ok)
        {
            LoadedModelName = System.IO.Path.GetFileName(path);
        }
        return status;
    }

    public static bool HasModel => Dlss5Native.DemoModelLoaded() != 0;

    /// <summary>The loaded model's honest label ("not DLSS5 - ...").</summary>
    public static string LoadedModelLabel =>
        Marshal.PtrToStringUTF8(Dlss5Native.DemoModelLabel())
        ?? "not DLSS5 - demonstration transform (no model loaded)";

    /// <summary>The loaded model's file name (for the report).</summary>
    public static string? LoadedModelName { get; private set; }

    /// <summary>
    /// The pipeline's primary demo kernel: 3 (the neural demonstration)
    /// only at the model's x2 ratio with the real weights loaded; the
    /// classical Lanczos3 scaler otherwise (the x4 extreme mode).
    /// </summary>
    public int PrimaryKernel => DemoScale == 2 && HasModel ? 3 : 1;

    public static string StatusName(Dlss5Status status) =>
        Marshal.PtrToStringUTF8(Dlss5Native.StatusName(status)) ?? status.ToString();

    public uint Counter => Dlss5Native.Counter(_handle);

    public nuint ArenaHighWaterFloats
    {
        get
        {
            var status = Dlss5Native.ArenaHighWater(_handle, out var floats);
            return status == Dlss5Status.Ok ? floats : 0;
        }
    }

    /// <summary>
    /// One frame: rgb (3*width per row) in; outRgb out at the same
    /// extent, or scale x the extent in the demonstration mode (the caller
    /// sizes it via OutputWidth/OutputHeight). A true <paramref
    /// name="reset"/> runs DLSSNR.Reset first (a no-op in the stateless
    /// demonstration mode).
    /// </summary>
    public Dlss5Status Evaluate(byte[] rgb, byte[] outRgb, bool reset)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        var outStride = 3 * OutputWidth;
        return Dlss5Native.Evaluate(_handle, rgb, 3 * Width, outRgb, outStride, reset ? 1 : 0);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Dlss5Native.Destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
