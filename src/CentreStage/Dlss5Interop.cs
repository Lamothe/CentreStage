// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Michael Lamothe
//
// P/Invoke over the dlss5-linux native host boundary (vr/native-host.h).
// The ABI is the tested C surface: create/evaluate/destroy over RGB frames,
// the frames-since-reset counter, the arena report and the in-band label
// ("synthetic weights — arbitrary transform") that a host MUST display
// alongside any presented output. The evaluator is CPU-only and costs
// seconds per frame at this boundary's geometry — callers present it as
// on-demand frame-by-frame processing with visible latency, never as
// real-time upscaling.
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
    /// One frame through the temporal step: rgb (3*width per row) in,
    /// outRgb out at the same extent. A true <paramref name="reset"/> runs
    /// DLSSNR.Reset first (history = 0, counter = 0).
    /// </summary>
    public Dlss5Status Evaluate(byte[] rgb, byte[] outRgb, bool reset)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        return Dlss5Native.Evaluate(_handle, rgb, 3 * Width, outRgb, 3 * Width, reset ? 1 : 0);
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
