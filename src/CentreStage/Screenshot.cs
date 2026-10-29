using Silk.NET.OpenGL;
using StbImageWriteSharp;

namespace CentreStage;

/// <summary>
/// Framebuffer screenshot helper shared by the DLSS demo driver and the F12
/// capture key (GL rows are bottom-up; PNGs are stored top-down).
/// </summary>
internal static unsafe class Screenshot
{
    public static void SavePng(GL gl, string path, int width, int height)
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        new ImageWriter().WritePng(flipped, width, height, ColorComponents.RedGreenBlueAlpha, stream);
    }
}
