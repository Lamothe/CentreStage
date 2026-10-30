using Silk.NET.OpenGL;
using StbImageWriteSharp;

namespace CentreStage;

/// <summary>
/// Framebuffer screenshot helper shared by the DLSS demo driver and the F12
/// capture key (GL rows are bottom-up; PNGs are stored top-down). The raw
/// capture is kept so the demo can assemble a labelled composite from the
/// same pixels it wrote.
/// </summary>
internal static unsafe class Screenshot
{
    /// <summary>Reads the framebuffer as top-down RGBA bytes.</summary>
    public static byte[] CaptureRgba(GL gl, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
        {
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        var rowBytes = width * 4;
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(pixels, y * rowBytes, flipped, (height - 1 - y) * rowBytes, rowBytes);
        }
        return flipped;
    }

    public static void WriteRgbaPng(string path, byte[] topDownRgba, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        new ImageWriter().WritePng(topDownRgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
    }

    public static void SavePng(GL gl, string path, int width, int height)
    {
        WriteRgbaPng(path, CaptureRgba(gl, width, height), width, height);
    }
}
