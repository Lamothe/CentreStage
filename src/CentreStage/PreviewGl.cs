using System.Numerics;
using CentreStage.Core;
using Silk.NET.OpenGL;

namespace CentreStage;

/// <summary>
/// GL and image plumbing shared by the neural-rendering preview windows: textured
/// quads with a single oversized triangle, a nearest-filtered upload (the previews
/// are shown at their capture resolution, never resampled), PSNR and PPM output.
/// </summary>
internal static class PreviewGl
{
    /// <summary>Uploads a top-down RGB frame as a nearest-filtered 2D texture.</summary>
    internal static unsafe uint Upload(GL gl, byte[] rgb, int width, int height)
    {
        if (rgb.Length != width * height * 3)
        {
            throw new ArgumentException("RGB length does not match the given dimensions");
        }
        var texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* pixels = rgb)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8, (uint)width, (uint)height,
                0, PixelFormat.Rgb, PixelType.UnsignedByte, pixels);
        }
        SetNearest(gl);
        return texture;
    }

    internal static void SetNearest(GL gl)
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }

    /// <summary>Draws texture <paramref name="uvMin"/>..<paramref name="uvMax"/> into a square viewport.</summary>
    internal static void DrawQuad(GL gl, uint texture, uint program, uint vao, int x, int y, int size,
        Vector2 uvMin, Vector2 uvMax)
    {
        if (size < 1)
        {
            return;
        }
        gl.UseProgram(program);
        gl.BindVertexArray(vao);
        gl.Viewport(x, y, (uint)size, (uint)size);
        gl.Uniform2(gl.GetUniformLocation(program, "uUvMin"), uvMin.X, uvMin.Y);
        gl.Uniform2(gl.GetUniformLocation(program, "uUvMax"), uvMax.X, uvMax.Y);
        gl.Uniform1(gl.GetUniformLocation(program, "tex"), 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    internal static uint BuildPresentProgram(GL gl)
    {
        const string vertex = """
            #version 330 core
            out vec2 uv;
            uniform vec2 uUvMin;
            uniform vec2 uUvMax;
            void main() {
                // A single oversized triangle; p is 0..1 across the viewport.
                vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
                // v runs down the image, so the top edge samples uUvMin.y.
                uv = vec2(mix(uUvMin.x, uUvMax.x, p.x), mix(uUvMax.y, uUvMin.y, p.y));
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 uv;
            uniform sampler2D tex;
            out vec4 colour;
            void main() { colour = vec4(texture(tex, uv).rgb, 1.0); }
            """;
        var vs = Compile(gl, ShaderType.VertexShader, vertex);
        var fs = Compile(gl, ShaderType.FragmentShader, fragment);
        var program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (linked == 0)
        {
            var log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"preview presenter link failed: {log}");
        }
        return program;
    }

    private static uint Compile(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            var log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"preview {type} failed: {log}");
        }
        return shader;
    }

    /// <summary>PSNR in dB of an 8-bit RGB buffer against the reference frame.</summary>
    internal static double Psnr(byte[] candidate, byte[] reference) =>
        RasterMetrics.Psnr(candidate, reference);

    /// <summary>PSNR over a rectangular region, in top-down pixel coordinates.</summary>
    internal static double PsnrRegion(byte[] candidate, byte[] reference, int width, int height,
        int x, int y, int regionWidth, int regionHeight) =>
        RasterMetrics.PsnrRegion(candidate, reference, width, height, x, y, regionWidth,
            regionHeight);

    /// <summary>Binary PPM (P6) of a top-down RGB frame, for eyeballing a capture.</summary>
    internal static void WritePpm(string path, byte[] rgb, int width, int height)
    {
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write($"P6\n{width} {height}\n255\n");
        writer.Flush();
        stream.Write(rgb);
    }
}