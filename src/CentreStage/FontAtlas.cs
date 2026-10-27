using Silk.NET.OpenGL;
using StbTrueTypeSharp;

namespace CentreStage;

/// <summary>
/// Glyph atlas: a system TrueType face rasterised once at startup into a coverage
/// texture. Proportional advances and antialiased edges — the load overlay is part
/// of the look of the application, not a debug console. Returns false from the
/// bake when no usable face is installed (text then falls back to the bitmap font).
/// </summary>
internal sealed class FontAtlas : IDisposable
{
    internal const int BakedPixelHeight = 64; // raster size; drawn scaled from this
    internal const int AtlasSize = 512;
    private const int FirstChar = 32;
    private const int CharCount = 95; // ASCII 32..126

    // TrueType (glyf) faces; the candidates cover mainstream desktops and each is
    // tried in turn, so one unusable file never costs the overlay its text.
    private static readonly string[] Candidates =
    [
        "/usr/share/fonts/noto/NotoSans-Regular.ttf",
        "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
        "/usr/share/fonts/liberation-sans-fonts/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
    ];

    private readonly GL _gl;
    private readonly uint _texture;
    private readonly StbTrueType.stbtt_bakedchar[] _chars = new StbTrueType.stbtt_bakedchar[CharCount];
    private bool _disposed;

    /// <summary>Distance from the top of a text line to its baseline, at baked size.</summary>
    public float Ascent { get; }

    public bool Valid { get; }

    /// <summary>GL name of the coverage atlas; only meaningful when <see cref="Valid"/>.</summary>
    public uint Texture => _texture;

    public FontAtlas(GL gl)
    {
        _gl = gl;
        Valid = Bake(gl, out _texture, out var ascent);
        Ascent = ascent;
    }

    /// <summary>Atlas UV rect for a character; falls back to '?' for anything unprintable.</summary>
    public (float U0, float V0, float U1, float V1) Glyph(char c, out float xOff, out float yOff, out float advance)
    {
        var i = c >= FirstChar && c < FirstChar + CharCount ? c - FirstChar : '?' - FirstChar;
        var g = _chars[i];
        xOff = g.xoff;
        yOff = g.yoff;
        advance = g.xadvance;
        return ((float)g.x0 / AtlasSize, (float)g.y0 / AtlasSize,
                (float)g.x1 / AtlasSize, (float)g.y1 / AtlasSize);
    }

    private bool Bake(GL gl, out uint texture, out float ascent)
    {
        texture = 0;
        ascent = 0f;

        foreach (var path in Candidates)
        {
            byte[]? data;
            try
            {
                data = File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // Unreadable candidate: try the next.
            }

            if (data is not null && BakeFace(gl, data, out texture, out ascent))
            {
                return true;
            }
        }

        return false;
    }

    private bool BakeFace(GL gl, byte[] data, out uint texture, out float ascent)
    {
        texture = 0;
        ascent = 0f;

        var pixels = new byte[AtlasSize * AtlasSize];
        unsafe
        {
            fixed (byte* pTtf = data)
            fixed (byte* pPixels = pixels)
            fixed (StbTrueType.stbtt_bakedchar* pChars = _chars)
            {
                // The Boolean overload reports "!= 0", so stb's negative error codes
                // (-1 corrupt font, -i overflow) read as success; use the raw result
                // and require > 0. The final glyph then proves the whole range packed.
                var result = StbTrueType.stbtt_BakeFontBitmap(pTtf, 0, BakedPixelHeight,
                    pPixels, AtlasSize, AtlasSize, FirstChar, CharCount, pChars);
                if (result <= 0 || _chars[CharCount - 1].xadvance <= 0f)
                {
                    return false;
                }

                float a, d, g;
                // The port dereferences all three metric pointers; no nulls allowed.
                StbTrueType.stbtt_GetScaledFontVMetrics(pTtf, 0, BakedPixelHeight, &a, &d, &g);
                ascent = a;
            }
        }

        texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        unsafe
        {
            fixed (byte* p = pixels)
            {
                // Single-channel coverage; the atlas width is a multiple of 4 anyway.
                gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, AtlasSize, AtlasSize,
                    0, PixelFormat.Red, PixelType.UnsignedByte, p);
            }
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Valid)
        {
            _gl.DeleteTexture(_texture);
        }
    }
}
