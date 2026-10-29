// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Michael Lamothe
//
// The DLSS seam's GL side: render the scene into a low-resolution offscreen
// framebuffer (the demo geometry's 64x72, the native boundary's documented
// bound), read the RGB bytes back for the native pipeline, and present the
// returned frame as a nearest-filtered fullscreen texture. The transform is
// ARBITRARY (synthetic weights) and the evaluator costs seconds per frame —
// this is on-demand frame-by-frame processing with visible latency, not
// real-time upscaling; callers show the in-band label with every presented
// frame (Dlss5Pipeline.Label).
using Silk.NET.OpenGL;

namespace CentreStage;

internal sealed class DlssStage : IDisposable
{
    // The demo geometry of the native boundary (64x72 pads to its 320x320
    // working floor; larger frames are refused by dlss5_nr_create).
    public const int LowWidth = 64;
    public const int LowHeight = 72;

    private readonly GL _gl;
    private readonly uint _fbo;
    private readonly uint _fboTexture;
    private readonly uint _depthBuffer;
    private readonly uint _presentProgram;
    private readonly int _presentTexLocation;
    private readonly uint _presentVao;
    private uint _resultTexture;
    private bool _disposed;

    public DlssStage(GL gl)
    {
        _gl = gl;

        _fboTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _fboTexture);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, LowWidth, LowHeight,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _depthBuffer = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthBuffer);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            LowWidth, LowHeight);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _fboTexture, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthBuffer);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"DLSS capture framebuffer incomplete: {status}");
        }

        _presentProgram = BuildPresentProgram(gl);
        _presentTexLocation = gl.GetUniformLocation(_presentProgram, "tex");
        _presentVao = gl.GenVertexArray();
        _resultTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _resultTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }

    /// <summary>Binds the low-resolution framebuffer for the scene draw.</summary>
    public void BeginCapture()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, LowWidth, LowHeight);
    }

    /// <summary>
    /// Reads the captured RGB bytes back (bottom-up, GL order), unbinds the
    /// framebuffer and restores the window viewport (the app's own drawing
    /// resumes at full size on the very next call — the capture pass must
    /// never leave the live view squashed into the low-res corner).
    /// </summary>
    public unsafe byte[] EndCapture(int frameBufferWidth, int frameBufferHeight)
    {
        var rgb = new byte[LowWidth * LowHeight * 3];
        fixed (byte* p = rgb)
        {
            _gl.ReadPixels(0, 0, LowWidth, LowHeight, PixelFormat.Rgb, PixelType.UnsignedByte, p);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)frameBufferWidth, (uint)frameBufferHeight);
        return rgb;
    }

    /// <summary>Uploads one processed frame for presentation.</summary>
    public unsafe void UploadResult(byte[] rgb)
    {
        if (rgb.Length != LowWidth * LowHeight * 3)
        {
            throw new ArgumentException($"expected {LowWidth * LowHeight * 3} bytes", nameof(rgb));
        }
        fixed (byte* p = rgb)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _resultTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8, LowWidth, LowHeight,
                0, PixelFormat.Rgb, PixelType.UnsignedByte, p);
        }
    }

    /// <summary>Presents the processed frame fullscreen (nearest upscale, honest pixels).</summary>
    public void Present(int frameBufferWidth, int frameBufferHeight)
    {
        _gl.Viewport(0, 0, (uint)frameBufferWidth, (uint)frameBufferHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_presentProgram);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _resultTexture);
        _gl.Uniform1(_presentTexLocation, 0);
        _gl.BindVertexArray(_presentVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_fboTexture);
        _gl.DeleteTexture(_resultTexture);
        _gl.DeleteRenderbuffer(_depthBuffer);
        _gl.DeleteVertexArray(_presentVao);
        _gl.DeleteProgram(_presentProgram);
    }

    private static uint BuildPresentProgram(GL gl)
    {
        const string vertexSource = """
            #version 330 core
            out vec2 uv;
            void main()
            {
                // A fullscreen triangle from gl_VertexID (no vertex buffer).
                vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
                uv = p;
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        const string fragmentSource = """
            #version 330 core
            in vec2 uv;
            uniform sampler2D tex;
            out vec4 outColor;
            void main()
            {
                outColor = vec4(texture(tex, uv).rgb, 1.0);
            }
            """;

        var vertex = Compile(gl, ShaderType.VertexShader, vertexSource);
        var fragment = Compile(gl, ShaderType.FragmentShader, fragmentSource);
        var program = gl.CreateProgram();
        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        if (linked == 0)
        {
            var log = gl.GetProgramInfoLog(program);
            throw new InvalidOperationException($"DLSS present program link failed: {log}");
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
            throw new InvalidOperationException($"DLSS present shader compile failed: {log}");
        }
        return shader;
    }
}
