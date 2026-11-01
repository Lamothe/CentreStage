using System.Runtime.InteropServices;
using System.Text;

namespace CentreStage;

/// <summary>Minimal P/Invoke wrapper for dlss5-linux/scene-train/host.h.</summary>
internal static partial class SceneNeuralNative
{
    private const string Library = "scene_host";

    static SceneNeuralNative()
    {
        NativeLibraryLoader.EnsureRegistered();
    }

    [LibraryImport(Library, EntryPoint = "scene_host_label")]
    internal static partial IntPtr Label();

    [LibraryImport(Library, EntryPoint = "scene_host_load", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Load(string checkpoint, out IntPtr host, byte[] error, nuint errorCapacity);

    [LibraryImport(Library, EntryPoint = "scene_host_infer", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Infer(IntPtr host, byte[] input, nuint inputSize, nuint inputStride,
        byte[] output, nuint outputSize, nuint outputStride, uint width, uint height,
        byte[] error, nuint errorCapacity);

    [LibraryImport(Library, EntryPoint = "scene_host_destroy")]
    internal static partial void Destroy(IntPtr host);
}

internal sealed class SceneNeuralModel : IDisposable
{
    private IntPtr _handle;

    public SceneNeuralModel(string checkpoint)
    {
        // Name the host being used before the first call, and fail with the build hint.
        Library = NativeLibraryLoader.SceneLibraryPath();
        var error = new byte[1024];
        var status = SceneNeuralNative.Load(checkpoint, out _handle, error, (nuint)error.Length);
        if (status != 0 || _handle == IntPtr.Zero)
        {
            var detail = ErrorText(error);
            throw new InvalidOperationException($"scene model load failed: {detail}");
        }
    }

    /// <summary>Path of the native host this model was loaded from.</summary>
    public string Library { get; }

    /// <summary>The host's own one-line description of the transformation it applies.</summary>
    public static string Describe()
    {
        var pointer = SceneNeuralNative.Label();
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }

    public byte[] Infer(byte[] rgb, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (width < 1 || height < 1 || rgb.Length != checked(width * height * 3))
        {
            throw new ArgumentException("scene inference expects tightly packed RGB8", nameof(rgb));
        }

        var output = new byte[rgb.Length];
        var error = new byte[1024];
        var status = SceneNeuralNative.Infer(_handle, rgb, (nuint)rgb.Length, (nuint)(width * 3),
            output, (nuint)output.Length, (nuint)(width * 3), (uint)width, (uint)height,
            error, (nuint)error.Length);
        if (status != 0)
        {
            var detail = ErrorText(error);
            throw new InvalidOperationException($"scene inference failed: {detail}");
        }

        return output;
    }

    private static string ErrorText(byte[] error)
    {
        var end = Array.IndexOf(error, (byte)0);
        return Encoding.UTF8.GetString(error, 0, end >= 0 ? end : error.Length);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            SceneNeuralNative.Destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
