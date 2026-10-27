using System.Numerics;
using Silk.NET.OpenGL;

namespace CentreStage;

/// <summary>
/// On-screen status text: a rasterised TrueType face drawn as antialiased textured
/// quads, one shader and one dynamic vertex buffer. Used for load/close status so
/// long operations are never a silent freeze. Falls back to the embedded 5x7
/// bitmap font only if no system face could be rasterised.
/// </summary>
internal sealed class StatusOverlay : IDisposable
{
    private const int VertexFloats = 8; // pos2 + uv2 + colour4

    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly FontAtlas _font;
    private float[] _verts = new float[VertexFloats * 6 * 1024];
    private int _vertsUsed;
    private int _width;
    private int _height;
    private bool _disposed;

    public StatusOverlay(GL gl)
    {
        _gl = gl;
        _font = new FontAtlas(gl);

        const string vs = """
            #version 330 core
            layout (location = 0) in vec2 aPos;
            layout (location = 1) in vec2 aUv;
            layout (location = 2) in vec4 aColor;
            uniform vec2 uResolution;
            out vec2 vUv;
            out vec4 vColor;
            void main()
            {
                vec2 ndc = (aPos / uResolution) * 2.0 - 1.0;
                gl_Position = vec4(ndc.x, -ndc.y, 0.0, 1.0);
                vUv = aUv;
                vColor = aColor;
            }
            """;
        const string fs = """
            #version 330 core
            in vec2 vUv;
            in vec4 vColor;
            uniform sampler2D uAtlas;
            out vec4 FragColor;
            void main()
            {
                // uv.x < 0 marks the solid quads (progress bar) that need no glyph.
                float coverage = vUv.x < 0.0 ? 1.0 : texture(uAtlas, vUv).r;
                FragColor = vec4(vColor.rgb, vColor.a * coverage);
            }
            """;

        _program = gl.CreateProgram();
        var v = Compile(gl, ShaderType.VertexShader, vs);
        var f = Compile(gl, ShaderType.FragmentShader, fs);
        gl.AttachShader(_program, v);
        gl.AttachShader(_program, f);
        gl.LinkProgram(_program);
        gl.GetProgram(_program, GLEnum.LinkStatus, out var linked);
        if (linked == 0)
        {
            throw new InvalidOperationException($"Status shader link failed: {gl.GetProgramInfoLog(_program)}");
        }

        gl.DeleteShader(v);
        gl.DeleteShader(f);

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, VertexFloats * sizeof(float), null);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, VertexFloats * sizeof(float), (void*)(2 * sizeof(float)));
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, VertexFloats * sizeof(float), (void*)(4 * sizeof(float)));
        }

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws one status screen: title, detail line, and a progress bar
    /// (0..1 filled; negative draws the indeterminate sweep).
    /// </summary>
    public void DrawStatus(int width, int height, string title, string? detail, float progress, float elapsed)
    {
        _width = width;
        _height = height;
        _vertsUsed = 0;

        const float titleSize = 34f;
        const float detailSize = 21f;
        var centerX = width / 2f;

        var y = (height / 2f) - 70f;
        DrawText(title, centerX - (TextWidth(title, titleSize) / 2f), y, titleSize, new Vector4(0.92f, 0.94f, 0.98f, 1f));

        y += titleSize + 16f;
        if (detail is not null)
        {
            DrawText(detail, centerX - (TextWidth(detail, detailSize) / 2f), y, detailSize, new Vector4(0.62f, 0.68f, 0.78f, 1f));
        }

        var barW = MathF.Min(width * 0.5f, 480f);
        var barH = 10f;
        var barX = centerX - (barW / 2f);
        var barY = y + detailSize + 30f;
        PushSolid(new Vector4(0.20f, 0.23f, 0.30f, 1f), barX, barY, barW, barH);

        // Indeterminate bars sweep; determinate bars fill.
        var frac = progress >= 0f
            ? Math.Clamp(progress, 0f, 1f)
            : 0.25f + (0.5f * (0.5f + (0.5f * MathF.Sin(elapsed * 3f))));
        PushSolid(new Vector4(0.35f, 0.62f, 0.95f, 1f), barX, barY, barW * frac, barH);

        Flush();
    }

    /// <summary>
    /// Draws one line of text at pixel coordinates: x is the left edge, y the top
    /// of the line, size the em height in pixels.
    /// </summary>
    public void DrawText(string text, float x, float y, float size, Vector4 color)
    {
        if (!_font.Valid)
        {
            DrawTextBitmap(text, x, y, size, color);
            return;
        }

        var scale = size / FontAtlas.BakedPixelHeight;
        var penX = x;
        var baseline = y + (_font.Ascent * scale);
        foreach (var raw in text)
        {
            var uv = _font.Glyph(raw, out var xOff, out var yOff, out var advance);
            var gx = penX + (xOff * scale);
            var gy = baseline + (yOff * scale);
            // UV deltas map back to raster pixels via the atlas extent, then scale.
            var gw = (uv.U1 - uv.U0) * FontAtlas.AtlasSize * scale;
            var gh = (uv.V1 - uv.V0) * FontAtlas.AtlasSize * scale;
            if (gw > 0f && gh > 0f)
            {
                PushTextured(color, gx, gy, gw, gh, uv.U0, uv.V0, uv.U1, uv.V1);
            }

            penX += advance * scale;
        }
    }

    public float TextWidth(string text, float size)
    {
        if (!_font.Valid)
        {
            return text.Length * BitmapFont.Advance * (size / BitmapFont.GlyphHeight);
        }

        var scale = size / FontAtlas.BakedPixelHeight;
        var width = 0f;
        foreach (var raw in text)
        {
            _font.Glyph(raw, out _, out _, out var advance);
            width += advance * scale;
        }

        return width;
    }

    /// <summary>Fallback path: the hand-drawn 5x7 font, only if no system face baked.</summary>
    private void DrawTextBitmap(string text, float x, float y, float size, Vector4 color)
    {
        var scale = size / BitmapFont.GlyphHeight;
        var cursor = x;
        foreach (var raw in text)
        {
            var glyph = BitmapFont.Get(char.ToUpperInvariant(raw));
            for (var row = 0; row < BitmapFont.GlyphHeight; row++)
            {
                var art = glyph[row];
                for (var col = 0; col < BitmapFont.GlyphWidth; col++)
                {
                    if (art[col] == '#')
                    {
                        PushSolid(color, cursor + (col * scale), y + (row * scale), scale, scale);
                    }
                }
            }

            cursor += BitmapFont.Advance * scale;
        }
    }

    private void PushSolid(Vector4 color, float x, float y, float w, float h)
        => PushQuad(color, x, y, w, h, -1f, -1f, -1f, -1f);

    private void PushTextured(Vector4 color, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1)
        => PushQuad(color, x, y, w, h, u0, v0, u1, v1);

    private void PushQuad(Vector4 color, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1)
    {
        var needed = _vertsUsed + (6 * VertexFloats);
        if (needed > _verts.Length)
        {
            Array.Resize(ref _verts, Math.Max(needed, _verts.Length * 2));
        }

        Span<float> v = _verts.AsSpan(_vertsUsed);
        for (var i = 0; i < 6; i++)
        {
            // Two triangles: (tl, tr, br) and (tl, br, bl).
            var (px, py, u, vv) = i switch
            {
                0 => (x, y, u0, v0),
                1 => (x + w, y, u1, v0),
                2 => (x + w, y + h, u1, v1),
                3 => (x, y, u0, v0),
                4 => (x + w, y + h, u1, v1),
                _ => (x, y + h, u0, v1),
            };
            var o = i * VertexFloats;
            v[o + 0] = px;
            v[o + 1] = py;
            v[o + 2] = u;
            v[o + 3] = vv;
            v[o + 4] = color.X;
            v[o + 5] = color.Y;
            v[o + 6] = color.Z;
            v[o + 7] = color.W;
        }

        _vertsUsed += 6 * VertexFloats;
    }

    private void Flush()
    {
        if (_vertsUsed == 0)
        {
            return;
        }

        _gl.UseProgram(_program);
        _gl.Uniform2(_gl.GetUniformLocation(_program, "uResolution"), (float)_width, (float)_height);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _font.Texture);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "uAtlas"), 0);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (float* p = _verts)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertsUsed * sizeof(float)), p, BufferUsageARB.StreamDraw);
            }
        }

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.DepthTest);
        // Overlay quads are back-facing after the shader's y-flip, so any cull state
        // left behind by the model renderer would erase the text entirely.
        _gl.Disable(EnableCap.CullFace);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_vertsUsed / VertexFloats));
        _gl.BindVertexArray(0);
        _vertsUsed = 0;
    }

    private static uint Compile(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, GLEnum.CompileStatus, out var status);
        if (status == 0)
        {
            throw new InvalidOperationException($"{type} compile failed: {gl.GetShaderInfoLog(shader)}");
        }

        return shader;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _font.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }
}
