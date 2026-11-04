using CentreStage.Core;
using Silk.NET.OpenGL;

namespace CentreStage;

/// <summary>Linear HDR opaque MRT, depth-aware separable diffuse diffusion, then display mapping.
/// Transparent and transmissive surfaces are drawn afterwards by Renderer.</summary>
internal sealed unsafe class SubsurfacePass : IDisposable
{
    private readonly GL _gl;
    private readonly uint _blur;
    private readonly uint _combine;
    private readonly uint _vao;
    private readonly uint[] _textures = new uint[5];
    private readonly uint[] _fbos = new uint[3];
    private readonly int[] _viewport = new int[4];
    private int _destination;
    private int _width;
    private int _height;

    public SubsurfacePass(GL gl)
    {
        _gl = gl;
        _blur = Program(SubsurfaceShaders.Blur);
        _combine = Program(SubsurfaceShaders.Combine);
        _vao = gl.GenVertexArray();
    }

    public void Begin()
    {
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, out _destination);
        fixed (int* v = _viewport) { _gl.GetInteger(GLEnum.Viewport, v); }
        Resize(_viewport[2], _viewport[3]);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)_destination);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbos[0]);
        // Preserve the caller's clear colour, already cleared before this pass.
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.BlitFramebuffer(_viewport[0], _viewport[1], _viewport[0]+_width, _viewport[1]+_height,
            0, 0, _width, _height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbos[0]);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        var buffers = stackalloc DrawBufferMode[] { DrawBufferMode.ColorAttachment0, DrawBufferMode.ColorAttachment1 };
        _gl.DrawBuffers(2, buffers);
        var zero = stackalloc float[4];
        for (var i = 0; i < 4; i++) { zero[i] = 0; }
        zero[3] = -2f;
        _gl.ClearBuffer(BufferKind.Color, 1, zero);
    }

    public void Resolve(OrbitCamera camera, PresentationSettings settings, float modelExtent)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.UseProgram(_blur);
        Bind(_blur, "depthMap", 1, _textures[2]);
        Bind(_blur, "maskMap", 2, _textures[1]);
        _gl.Uniform2(_gl.GetUniformLocation(_blur, "nearFar"), camera.Near, camera.Far);
        _gl.Uniform1(_gl.GetUniformLocation(_blur, "radiusWorld"), modelExtent * settings.SubsurfaceRadius);
        _gl.Uniform1(_gl.GetUniformLocation(_blur, "projectionScale"), 0.5f / MathF.Tan(camera.FieldOfView * 0.5f));
        for (var pass = 0; pass < 2; pass++)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbos[pass + 1]);
            Bind(_blur, "source", 0, pass == 0 ? _textures[1] : _textures[3]);
            _gl.Uniform2(_gl.GetUniformLocation(_blur, "direction"), pass == 0 ? (float)_height/_width : 0f, pass == 0 ? 0f : 1f);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        // Fragment depth transfer works with any destination depth format and sample count.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)_destination);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Always);
        _gl.DepthMask(true);
        _gl.Viewport(_viewport[0], _viewport[1], (uint)_width, (uint)_height);
        _gl.UseProgram(_combine);
        Bind(_combine, "source", 0, _textures[4]);
        Bind(_combine, "radianceMap", 1, _textures[0]);
        Bind(_combine, "originalDiffuse", 2, _textures[1]);
        Bind(_combine, "depthMap", 3, _textures[2]);
        foreach (var (name, value) in new[] { ("strength", settings.SubsurfaceScattering), ("exposure", settings.Exposure),
            ("brightness", settings.Brightness), ("contrast", settings.Contrast), ("saturation", settings.Saturation) })
        {
            _gl.Uniform1(_gl.GetUniformLocation(_combine, name), value);
        }
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private void Bind(uint program, string name, int unit, uint texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.Uniform1(_gl.GetUniformLocation(program, name), unit);
    }

    private void Resize(int width, int height)
    {
        if (width == _width && height == _height) { return; }
        ReleaseTargets();
        _width = width;
        _height = height;
        for (var i = 0; i < _textures.Length; i++)
        {
            _textures[i] = _gl.GenTexture();
            _gl.ActiveTexture(TextureUnit.Texture12);
            _gl.BindTexture(TextureTarget.Texture2D, _textures[i]);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, i == 2 ? InternalFormat.DepthComponent24 : InternalFormat.Rgba16f,
                (uint)width, (uint)height, 0, i == 2 ? PixelFormat.DepthComponent : PixelFormat.Rgba, PixelType.Float, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }
        for (var i = 0; i < _fbos.Length; i++)
        {
            _fbos[i] = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbos[i]);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _textures[i == 0 ? 0 : i + 2], 0);
            if (i == 0)
            {
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _textures[1], 0);
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _textures[2], 0);
            }
            if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException("Subsurface framebuffer incomplete");
            }
        }
    }

    private uint Program(string fragment)
    {
        uint Shader(ShaderType type, string source)
        {
            var id = _gl.CreateShader(type);
            _gl.ShaderSource(id, source);
            _gl.CompileShader(id);
            _gl.GetShader(id, ShaderParameterName.CompileStatus, out var ok);
            if (ok == 0) { throw new InvalidOperationException(_gl.GetShaderInfoLog(id)); }
            return id;
        }
        var vertex = Shader(ShaderType.VertexShader, SubsurfaceShaders.Vertex);
        var pixel = Shader(ShaderType.FragmentShader, fragment);
        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vertex);
        _gl.AttachShader(program, pixel);
        _gl.LinkProgram(program);
        _gl.DeleteShader(vertex);
        _gl.DeleteShader(pixel);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0) { throw new InvalidOperationException(_gl.GetProgramInfoLog(program)); }
        return program;
    }

    private void ReleaseTargets()
    {
        foreach (var id in _textures) { if (id != 0) { _gl.DeleteTexture(id); } }
        foreach (var id in _fbos) { if (id != 0) { _gl.DeleteFramebuffer(id); } }
        Array.Clear(_textures);
        Array.Clear(_fbos);
    }

    public void Dispose()
    {
        ReleaseTargets();
        _gl.DeleteProgram(_blur);
        _gl.DeleteProgram(_combine);
        _gl.DeleteVertexArray(_vao);
    }
}
