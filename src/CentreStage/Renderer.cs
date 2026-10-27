using System.Numerics;
using System.Runtime.InteropServices;
using CentreStage.Core;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace CentreStage;

/// <summary>
/// OpenGL renderer for a GltfModel: uploads geometry and textures, then draws each
/// node's primitives with per-material PBR uniforms.
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    private const int MaxLights = 4;

    /// <summary>Joint palette capacity in the uniform block (16 KB: the guaranteed minimum).</summary>
    private const int MaxJoints = 256;

    private readonly GL _gl;
    private readonly GltfModel _model;
    private readonly uint _program;

    private readonly int[] _textureIds;
    private readonly HashSet<int> _srgbTextures;
    private readonly List<GlPrimitive> _primitives = new();
    private readonly List<GlPrimitive> _ordered = new();
    private bool _disposed;
    private readonly Dictionary<string, int> _materialUniformCache = new();

    /// <summary>
    /// Animated models skin on the GPU (per-frame joint palettes); everything else keeps
    /// the CPU-baked static pose. One path or the other per model, never both.
    /// </summary>
    private readonly bool _gpuSkinning;
    private readonly uint _jointUbo;
    private readonly Matrix4x4[] _jointScratch = new Matrix4x4[MaxJoints];

    private static readonly int[] SamplerWraps = { 10497, 33071, 33648 }; // repeat, clamp, mirrored

    private sealed class GlPrimitive
    {
        public uint Vao;
        public uint VertexBuffer;
        public uint IndexBuffer;
        public int IndexCount;
        public int MaterialIndex;
        public Matrix4x4 World;
        public Vector3 Centre;
        public int Seq;
        public bool DoubleSided;
        public AlphaMode AlphaMode;
        public int NodeIndex;
        public int SkinIndex = -1;
        /// <summary>True when this primitive is skinned by the GPU palette (animated models).</summary>
        public bool GpuSkinned;
    }

    public Renderer(GL gl, GltfModel model, ImageResult?[]? decodedTextures = null)
    {
        _gl = gl;
        _model = model;
        _program = BuildProgram();
        _textureIds = new int[model.Textures.Count];
        Array.Fill(_textureIds, -1);
        _srgbTextures = TextureColorSpace.SrgbTextureIndices(model.Materials);
        _gpuSkinning = model.Animations.Count > 0;
        if (_gpuSkinning)
        {
            _jointUbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _jointUbo);
            _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)(MaxJoints * 16 * sizeof(float)), null, BufferUsageARB.DynamicDraw);
            var blockIndex = _gl.GetUniformBlockIndex(_program, "JointBlock");
            if (blockIndex != 0xFFFFFFFF) // GL_INVALID_INDEX: block not present (e.g. solid diagnostic shader)
            {
                _gl.UniformBlockBinding(_program, blockIndex, 0);
            }

            _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _jointUbo);
        }
        foreach (var shared in TextureColorSpace.SharedUsage(model.Materials))
        {
            Console.Error.WriteLine(
                $"warning: texture {shared} is used as both colour and data; uploading sRGB (data reads will be decoded).");
        }

        UploadGeometry();
        if (decodedTextures is not null)
        {
            UploadTextures(decodedTextures);
        }

        var triangleCount = _primitives.Sum(p => (long)p.IndexCount / 3);
        Console.WriteLine($"  gl: primitives={_primitives.Count} triangles={triangleCount}");
        for (var err = _gl.GetError(); err != GLEnum.NoError; err = _gl.GetError())
        {
            Console.Error.WriteLine($"warning: GL error during upload: {err}");
        }
    }

    public (Vector3 min, Vector3 max) Bounds { get; private set; } = (Vector3.Zero, Vector3.Zero);

    private uint BuildProgram()
    {
        var mode = Environment.GetEnvironmentVariable("CENTRESTAGE_SOLID");
        if (mode is "1" or "2")
        {
            // Diagnostic: minimal passthrough — proves whether geometry rasterizes at all.
            // Mode 2 bypasses vertex attributes entirely (positions from gl_VertexID),
            // separating "draw call/VAO broken" from "attribute fetch broken".
            var solidVs = mode == "2"
                ? """
                  #version 330 core
                  uniform mat4 uModel;
                  uniform mat4 uView;
                  uniform mat4 uProjection;
                  void main()
                  {
                      vec2 corners[3] = vec2[3](vec2(-0.8, -0.8), vec2(0.8, -0.8), vec2(0.0, 0.8));
                      gl_Position = vec4(corners[gl_VertexID % 3], 0.5, 1.0);
                  }
                  """
                : """
                  #version 330 core
                  layout (location = 0) in vec3 aPosition;
                  uniform mat4 uModel;
                  uniform mat4 uView;
                  uniform mat4 uProjection;
                  void main()
                  {
                      gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
                  }
                  """;
            var solidFs = """
                #version 330 core
                out vec4 FragColor;
                void main() { FragColor = vec4(1.0, 0.0, 0.0, 1.0); }
                """;
            var svs = Compile(ShaderType.VertexShader, solidVs);
            var sfs = Compile(ShaderType.FragmentShader, solidFs);
            var prog = _gl.CreateProgram();
            _gl.AttachShader(prog, svs);
            _gl.AttachShader(prog, sfs);
            _gl.LinkProgram(prog);
            _gl.GetProgram(prog, GLEnum.LinkStatus, out var st);
            if (st == 0)
            {
                throw new InvalidOperationException($"Shader link failed: {_gl.GetProgramInfoLog(prog)}");
            }

            return prog;
        }

        var vs = Compile(ShaderType.VertexShader, ShaderSources.Vertex);
        var fs = Compile(ShaderType.FragmentShader, ShaderSources.Fragment);
        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, GLEnum.LinkStatus, out var status);
        if (status == 0)
        {
            var log = _gl.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return program;
    }

    private uint Compile(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, GLEnum.CompileStatus, out var status);
        if (status == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }

        return shader;
    }

    /// <summary>
    /// Decodes one compressed image to RGBA pixels. Pure CPU work — safe (and worthwhile)
    /// on a background thread: decoding is by far the slowest part of loading a model.
    /// </summary>
    public static ImageResult? DecodeTexture(RuntimeTexture tex, int index)
    {
        try
        {
            return ImageResult.FromMemory(tex.Data, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            // A texture that won't decode must not take the whole model down with it.
            Console.Error.WriteLine($"warning: texture #{index} failed to decode ({ex.Message}); rendering without it.");
            return null;
        }
    }

    /// <summary>
    /// Decodes all textures in parallel. Pure CPU work — safe (and worthwhile) on a
    /// background thread: decoding is by far the slowest part of loading a model.
    /// </summary>
    public static ImageResult?[] DecodeTextures(IReadOnlyList<RuntimeTexture> textures, Action<int, int>? progress = null)
    {
        var results = new ImageResult?[textures.Count];
        var done = 0;
        var total = textures.Count;
        Parallel.For(0, total, i =>
        {
            results[i] = DecodeTexture(textures[i], i);
            var n = Interlocked.Increment(ref done);
            progress?.Invoke(n, total);
        });

        return results;
    }

    /// <summary>Uploads one decoded texture to the GPU. GL thread only.</summary>
    public void UploadTexture(int index, ImageResult image)
    {
        var tex = _model.Textures[index];
        var id = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, id);
        // Colour textures are sRGB-encoded in the file; sampling from an sRGB internal
        // format decodes them to linear so lighting runs in linear space and the output
        // encode is the only gamma step. Data textures stay plain linear RGBA.
        var format = _srgbTextures.Contains(index) ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8;
        fixed (byte* p = image.Data)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, format,
                (uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        _gl.GenerateMipmap(TextureTarget.Texture2D);

        var wrapS = SamplerWraps.Contains(tex.WrapS) ? (GLEnum)tex.WrapS : GLEnum.Repeat;
        var wrapT = SamplerWraps.Contains(tex.WrapT) ? (GLEnum)tex.WrapT : GLEnum.Repeat;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrapS);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrapT);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)SamplerFilter(tex.MinFilter, GLEnum.LinearMipmapLinear));
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)SamplerFilter(tex.MagFilter, GLEnum.Linear));
        _textureIds[index] = (int)id;
    }

    /// <summary>Uploads every decoded texture in one go (capture/CLI path).</summary>
    public void UploadTextures(ImageResult?[] decoded)
    {
        for (var i = 0; i < decoded.Length && i < _textureIds.Length; i++)
        {
            if (decoded[i] is { } image)
            {
                UploadTexture(i, image);
                decoded[i] = null; // release pixels as soon as the GPU has them
            }
        }
    }

    /// <summary>Validates a glTF sampler filter enum (9728/9729/9984..9987) or falls back.</summary>
    private static GLEnum SamplerFilter(int filter, GLEnum fallback) => filter switch
    {
        9728 => GLEnum.Nearest,
        9729 => GLEnum.Linear,
        9984 => GLEnum.NearestMipmapNearest,
        9985 => GLEnum.LinearMipmapNearest,
        9986 => GLEnum.NearestMipmapLinear,
        9987 => GLEnum.LinearMipmapLinear,
        _ => fallback,
    };

    private void UploadGeometry()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;

        // Per-vertex scratch for joint sanitisation, allocated once here (CA2014:
        // stackalloc inside the loops would grow the stack per primitive/vertex).
        Span<float> jx = stackalloc float[4];
        Span<float> wx = stackalloc float[4];

        foreach (var node in _model.Nodes)
        {
            if (node.MeshIndex < 0)
            {
                continue;
            }

            var mesh = _model.Meshes[node.MeshIndex];

            // glTF spec: a skinned mesh ignores its node's transform; its pose comes from the
            // joint matrices. Two paths: animated models carry joints/weights to the GPU and
            // skin per frame from the posed palette; everything else bakes the rest pose on
            // the CPU at upload — exactly what the file's rest pose gives us.
            Matrix4x4[]? jointMatrices = null;
            var world = Matrix4x4.Identity;
            if (node.SkinIndex >= 0)
            {
                var skin = _model.Skins[node.SkinIndex];
                jointMatrices = new Matrix4x4[skin.JointNodes.Count];
                for (var j = 0; j < skin.JointNodes.Count; j++)
                {
                    jointMatrices[j] = skin.InverseBindMatrices[j] * JointWorld(skin.JointNodes[j]);
                }
            }
            else
            {
                world = node.World;
            }

            foreach (var prim in mesh.Primitives)
            {
                if (prim.Mode != 4)
                {
                    Console.Error.WriteLine($"warning: primitive mode {prim.Mode} is not triangles; skipped.");
                    continue;
                }

                var vao = _gl.GenVertexArray();
                _gl.BindVertexArray(vao);

                // Interleave position/normal/tangent/uv0/uv1/colour into one stream:
                // pos(3) nrm(3) tan(4) uv0(2) uv1(2) colour(4) = 18 floats. GPU-skinned
                // primitives append joints(4) weights(4) = 26 floats.
                var gpuSkinned = _gpuSkinning && jointMatrices is not null
                    && prim.Weights is not null && prim.Joints is not null;
                var floatsPerVertex = gpuSkinned ? 26 : 18;
                var vertexCount = prim.Positions.Length;
                var stride = floatsPerVertex * sizeof(float);
                var data = new float[vertexCount * floatsPerVertex];
                var primMin = new Vector3(float.MaxValue);
                var primMax = new Vector3(float.MinValue);
                for (var i = 0; i < vertexCount; i++)
                {
                    var rawPosition = prim.Positions[i];
                    var rawNormal = prim.Normals is { } normals ? normals[i] : Vector3.UnitY;
                    var rawTangent = prim.Tangents is { } tangents ? tangents[i] : new Vector4(1, 0, 0, 1);

                    // CPU-baked skinning stores world-space vertices and draws at identity;
                    // unskinned and GPU-skinned vertices stay raw and the transform happens
                    // at draw time. Never both — that transforms twice.
                    Vector3 storedPosition;
                    Vector3 storedNormal;
                    Vector4 storedTangent;
                    Vector3 worldPosition;

                    if (gpuSkinned)
                    {
                        storedPosition = rawPosition;
                        storedNormal = rawNormal;
                        storedTangent = rawTangent;
                        // Bounds still need the rest-pose world extent.
                        var restMatrix = SkinMatrix(jointMatrices!, prim.Joints![i], prim.Weights![i]);
                        worldPosition = Vector3.Transform(rawPosition, restMatrix);
                    }
                    else if (jointMatrices is not null && prim.Weights is { } weights && prim.Joints is { } joints)
                    {
                        var skinMatrix = SkinMatrix(jointMatrices, joints[i], weights[i]);
                        storedPosition = Vector3.Transform(rawPosition, skinMatrix);
                        storedNormal = Vector3.Normalize(Vector3.TransformNormal(rawNormal, skinMatrix));
                        // Tangent xyz is a direction: transform without the matrix translation.
                        // W is the handedness sign and must pass through untouched.
                        var t = Vector3.TransformNormal(new Vector3(rawTangent.X, rawTangent.Y, rawTangent.Z), skinMatrix);
                        storedTangent = new Vector4(t, rawTangent.W);
                        worldPosition = storedPosition;
                    }
                    else
                    {
                        storedPosition = rawPosition;
                        storedNormal = rawNormal;
                        storedTangent = rawTangent;
                        worldPosition = Vector3.Transform(rawPosition, world);
                    }

                    var o = i * floatsPerVertex;
                    data[o + 0] = storedPosition.X;
                    data[o + 1] = storedPosition.Y;
                    data[o + 2] = storedPosition.Z;
                    data[o + 3] = storedNormal.X;
                    data[o + 4] = storedNormal.Y;
                    data[o + 5] = storedNormal.Z;
                    data[o + 6] = storedTangent.X;
                    data[o + 7] = storedTangent.Y;
                    data[o + 8] = storedTangent.Z;
                    data[o + 9] = storedTangent.W;

                    if (prim.TexCoords0 is { } uv0)
                    {
                        data[o + 10] = uv0[i].X;
                        data[o + 11] = uv0[i].Y;
                    }

                    if (prim.TexCoords1 is { } uv1)
                    {
                        data[o + 12] = uv1[i].X;
                        data[o + 13] = uv1[i].Y;
                    }

                    if (prim.Colors is { } colors)
                    {
                        data[o + 14] = colors[i].X;
                        data[o + 15] = colors[i].Y;
                        data[o + 16] = colors[i].Z;
                        data[o + 17] = colors[i].W;
                    }
                    else
                    {
                        data[o + 14] = 1f;
                        data[o + 15] = 1f;
                        data[o + 16] = 1f;
                        data[o + 17] = 1f;
                    }

                    if (gpuSkinned)
                    {
                        // Joints are indices into the skin's palette; slots pointing outside
                        // what the palette can address (invalid or beyond MaxJoints) drop out
                        // here exactly as SkinMatrix does on the CPU — and the index is
                        // sanitised to 0 too, because GLSL reads all four slots regardless
                        // of weight and an out-of-range uJoints[] read is undefined.
                        var joints = prim.Joints![i];
                        var weights = prim.Weights![i];
                        var jointCount = Math.Min(jointMatrices!.Length, MaxJoints);
                        jx[0] = joints.X;
                        jx[1] = joints.Y;
                        jx[2] = joints.Z;
                        jx[3] = joints.W;
                        wx[0] = MathF.Max(weights.X, 0f);
                        wx[1] = MathF.Max(weights.Y, 0f);
                        wx[2] = MathF.Max(weights.Z, 0f);
                        wx[3] = MathF.Max(weights.W, 0f);
                        for (var s = 0; s < 4; s++)
                        {
                            if (jx[s] < 0f || jx[s] >= jointCount)
                            {
                                wx[s] = 0f;
                                jx[s] = 0f;
                            }
                        }

                        data[o + 18] = jx[0];
                        data[o + 19] = jx[1];
                        data[o + 20] = jx[2];
                        data[o + 21] = jx[3];
                        data[o + 22] = wx[0];
                        data[o + 23] = wx[1];
                        data[o + 24] = wx[2];
                        data[o + 25] = wx[3];
                    }

                    min = Vector3.Min(min, worldPosition);
                    max = Vector3.Max(max, worldPosition);
                    primMin = Vector3.Min(primMin, worldPosition);
                    primMax = Vector3.Max(primMax, worldPosition);
                    any = true;
                }

                var vbo = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
                fixed (float* fp = data)
                {
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), fp, BufferUsageARB.StaticDraw);
                }

                void Attrib(uint index, int size, int offsetFloats)
                {
                    _gl.EnableVertexAttribArray(index);
                    _gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, (uint)stride, (void*)(offsetFloats * sizeof(float)));
                }

                Attrib(0, 3, 0);  // position
                Attrib(1, 3, 3);  // normal
                Attrib(2, 4, 6);  // tangent
                Attrib(3, 2, 10); // uv0
                Attrib(4, 2, 12); // uv1
                Attrib(5, 4, 14); // colour
                if (gpuSkinned)
                {
                    Attrib(6, 4, 18); // joints
                    Attrib(7, 4, 22); // weights
                }

                var ibo = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ibo);
                var indices = prim.Indices;
                fixed (uint* ip = indices)
                {
                    _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), ip, BufferUsageARB.StaticDraw);
                }

                _gl.BindVertexArray(0);

                var matIndex = prim.MaterialIndex;
                _primitives.Add(new GlPrimitive
                {
                    Vao = vao,
                    VertexBuffer = vbo,
                    IndexBuffer = ibo,
                    IndexCount = indices.Length,
                    MaterialIndex = matIndex,
                    World = world,
                    // World-space centre for the per-frame translucent sort. Skinned
                    // vertices are baked to world at identity, so bounds already match.
                    Centre = (primMin + primMax) * 0.5f,
                    Seq = _primitives.Count,
                    DoubleSided = matIndex >= 0 && _model.Materials[matIndex].DoubleSided,
                    // MASK is shader-side discard and belongs in the opaque pass;
                    // only BLEND needs the second pass.
                    AlphaMode = matIndex >= 0 && _model.Materials[matIndex].AlphaMode == AlphaMode.Blend
                        ? AlphaMode.Blend
                        : AlphaMode.Opaque,
                    NodeIndex = node.NodeIndex,
                    SkinIndex = node.SkinIndex,
                    GpuSkinned = gpuSkinned,
                });
            }
        }

        Bounds = any ? (min, max) : (Vector3.Zero, Vector3.One);
    }

    /// <summary>World transform of a joint node, from the model's complete node-world map.</summary>
    private Matrix4x4 JointWorld(int nodeIndex)
    {
        if (_model.NodeWorlds.TryGetValue(nodeIndex, out var world))
        {
            return world;
        }

        throw new InvalidDataException(
            $"Skin joint references node #{nodeIndex}, which does not exist in the file.");
    }

    /// <summary>
    /// Computes and uploads a skin's joint palette (inverseBind x jointWorld) for the
    /// current pose. Clamped to <see cref="MaxJoints"/>: oversized rigs fall back to
    /// lower joints rather than overflowing the uniform block.
    /// </summary>
    private void UploadJointPalette(ScenePose pose, int skinIndex)
    {
        var skin = _model.Skins[skinIndex];
        var count = Math.Min(skin.JointNodes.Count, MaxJoints);
        if (skin.JointNodes.Count > MaxJoints)
        {
            Console.Error.WriteLine(
                $"warning: skin '{skin.Name ?? skinIndex.ToString()}' has {skin.JointNodes.Count} joints; "
                + $"only the first {MaxJoints} animate.");
        }

        pose.JointMatrices(skin, _jointScratch.AsSpan(0, count));
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, _jointUbo);
        fixed (Matrix4x4* p = _jointScratch)
        {
            _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)(count * 16 * sizeof(float)), p);
        }
    }

    /// <summary>Weighted skin matrix for one vertex: sum of w * (inverseBind * jointWorld) over 4 joints.</summary>
    private static Matrix4x4 SkinMatrix(Matrix4x4[] jointMatrices, Vector4 joints, Vector4 weights)
    {
        var sum = default(Matrix4x4);
        var total = 0f;
        Span<int> indices = stackalloc int[4]
        {
            (int)joints.X, (int)joints.Y, (int)joints.Z, (int)joints.W,
        };
        Span<float> w = stackalloc float[4] { weights.X, weights.Y, weights.Z, weights.W };

        for (var j = 0; j < 4; j++)
        {
            if (w[j] <= 0f || indices[j] < 0 || indices[j] >= jointMatrices.Length)
            {
                continue;
            }

            var m = jointMatrices[indices[j]];
            sum += m * w[j];
            total += w[j];
        }

        if (total <= 0f)
        {
            // No usable weights: fall back to identity (the vertex stays in mesh space).
            return Matrix4x4.Identity;
        }

        return total < 0.999f || total > 1.001f ? sum * (1f / total) : sum;
    }

    /// <summary>
    /// Draws the frame. <paramref name="pose"/> is null for static models (drawn exactly
    /// as uploaded) or a posed scene for animated models: unskinned primitives pick up
    /// their node's posed world matrix, GPU-skinned ones get the skin's joint palette
    /// uploaded before their draw call.
    /// </summary>
    public void Draw(OrbitCamera camera, float aspect, IReadOnlyList<RuntimeLight> lights, Vector3 sky, Vector3 ground, ScenePose? pose = null)
    {
        _gl.ClearColor(sky.X * 0.25f, sky.Y * 0.25f, sky.Z * 0.25f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _gl.UseProgram(_program);
        SetMat4("uView", camera.View);
        SetMat4("uProjection", camera.Projection(aspect));
        SetVec3("uCameraPos", camera.Position);
        SetVec3("uSkyColor", sky);
        SetVec3("uGroundColor", ground);

        UploadLights(lights);

        // Re-establish pipeline state every frame: the status overlay uses the same
        // context with depth testing off, and state set in the constructor won't survive it.
        if (Environment.GetEnvironmentVariable("CENTRESTAGE_WIREFRAME") == "1")
        {
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.DepthTest);
        }
        else
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Back);
        }

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        // Opaque + masked first (depth writes on). Blended primitives then draw
        // farthest-centre-first — straight-alpha compositing is order-dependent, and
        // an unsorted pass lets near shells (hair caps) paint over far wisps. This is
        // a per-primitive approximation: triangles within one draw call stay in index
        // order, and a large shell can still overtake small cards near its centre.
        // Exact ordering needs per-triangle sorting or order-independent translucency.
        if (_ordered.Count != _primitives.Count)
        {
            _ordered.Clear();
            _ordered.AddRange(_primitives);
        }

        foreach (var pass in new[] { AlphaMode.Opaque, AlphaMode.Blend })
        {
            if (pass == AlphaMode.Blend)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _gl.DepthMask(false);
                _ordered.Sort((a, b) =>
                {
                    var cmp = Vector3.DistanceSquared(b.Centre, camera.Position).CompareTo(
                        Vector3.DistanceSquared(a.Centre, camera.Position));
                    return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq);
                });
            }

            foreach (var prim in pass == AlphaMode.Blend ? _ordered : _primitives)
            {
                if (prim.AlphaMode != pass)
                {
                    continue;
                }

                ApplyMaterial(prim.MaterialIndex);

                var model = prim.World;
                var skinned = false;
                if (pose is not null)
                {
                    if (prim.GpuSkinned && prim.SkinIndex >= 0)
                    {
                        UploadJointPalette(pose, prim.SkinIndex);
                        skinned = true;
                        model = Matrix4x4.Identity; // pose lives in the palette
                    }
                    else
                    {
                        model = pose.World(prim.NodeIndex);
                    }
                }

                SetMat4("uModel", model);
                SetInt("uSkinned", skinned ? 1 : 0);

                if (prim.DoubleSided)
                {
                    _gl.Disable(EnableCap.CullFace);
                }
                else
                {
                    _gl.Enable(EnableCap.CullFace);
                }

                _gl.BindVertexArray(prim.Vao);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)prim.IndexCount, DrawElementsType.UnsignedInt, null);
            }

            if (pass == AlphaMode.Blend)
            {
                _gl.DepthMask(true);
                _gl.Disable(EnableCap.Blend);
            }
        }

        _gl.BindVertexArray(0);
    }

    private void UploadLights(IReadOnlyList<RuntimeLight> lights)
    {
        var count = Math.Min(lights.Count, MaxLights);
        SetInt("uLightCount", count);

        var hasSceneLights = count > 0;
        // Always give the camera a key light so unlit-by-author models stay legible;
        // it is zeroed when the scene already carries its own lights.
        var pos = new Vector3[MaxLights];
        var dir = new Vector3[MaxLights];
        var color = new Vector3[MaxLights];
        var intensity = new float[MaxLights];
        var range = new float[MaxLights];
        var directional = new float[MaxLights];
        var spot = new float[MaxLights];
        var innerCos = new float[MaxLights];
        var outerCos = new float[MaxLights];

        for (var i = 0; i < count; i++)
        {
            var l = lights[i];
            pos[i] = l.Position;
            dir[i] = l.Direction;
            color[i] = l.Color;
            intensity[i] = l.Intensity > 100f ? l.Intensity / 683f : l.Intensity; // candela -> sane scale
            range[i] = float.IsFinite(l.Range) ? l.Range : 0f;
            directional[i] = l.Type == LightType.Directional ? 1f : 0f;
            spot[i] = l.Type == LightType.Spot ? 1f : 0f;
            innerCos[i] = MathF.Cos(l.InnerConeAngle);
            outerCos[i] = MathF.Cos(l.OuterConeAngle);
        }

        if (!hasSceneLights)
        {
            pos[0] = new Vector3(2, 4, 2);
            dir[0] = Vector3.Normalize(new Vector3(-2, -4, -2));
            color[0] = Vector3.One;
            intensity[0] = 2.5f;
            directional[0] = 1f;
            SetInt("uLightCount", 1);
        }

        SetVec3Array("uLightPos", pos);
        SetVec3Array("uLightDir", dir);
        SetVec3Array("uLightColor", color);
        SetFloatArray("uLightIntensity", intensity);
        SetFloatArray("uLightRange", range);
        SetFloatArray("uLightDirectional", directional);
        SetFloatArray("uLightSpot", spot);
        SetFloatArray("uLightInnerCos", innerCos);
        SetFloatArray("uLightOuterCos", outerCos);
    }

    private void ApplyMaterial(int materialIndex)
    {
        // Default material (glTF spec): white, fully metallic-roughness mid-values.
        var m = materialIndex >= 0 ? _model.Materials[materialIndex] : null;
        var sg = m?.IsSpecularGlossiness == true;

        // Spec-gloss materials carry their own diffuse/texture slots; map them onto the
        // core slots so the shader's single base-colour path works either way.
        var baseColorFactor = sg ? m!.SgDiffuseFactor : (m?.BaseColorFactor ?? Vector4.One);
        var baseColorTexture = sg ? m!.SgDiffuseTexture : m?.BaseColorTexture;

        SetVec4("uBaseColorFactor", baseColorFactor);
        SetFloat("uMetallicFactor", m?.MetallicFactor ?? 1f);
        SetFloat("uRoughnessFactor", m?.RoughnessFactor ?? 1f);
        SetFloat("uSpecularFactor", m?.SpecularFactor ?? 1f);
        SetVec3("uSpecularColorFactor", m?.SpecularColorFactor ?? Vector3.One);
        SetFloat("uIor", m?.Ior ?? 1.5f);
        SetFloat("uTransmissionFactor", m?.TransmissionFactor ?? 0f);
        SetFloat("uClearcoatFactor", m?.ClearcoatFactor ?? 0f);
        SetFloat("uClearcoatRoughnessFactor", m?.ClearcoatRoughnessFactor ?? 0f);
        SetVec3("uSheenColorFactor", m?.SheenColorFactor ?? Vector3.Zero);
        SetFloat("uSheenRoughnessFactor", m?.SheenRoughnessFactor ?? 1f);
        SetVec3("uEmissiveFactor", m?.EmissiveFactor ?? Vector3.Zero);
        SetFloat("uEmissiveStrength", m?.EmissiveStrength ?? 1f);
        SetFloat("uNormalScale", m?.NormalScale ?? 1f);
        SetFloat("uOcclusionStrength", m?.OcclusionStrength ?? 1f);
        SetInt("uAlphaMode", (int)(m?.AlphaMode ?? AlphaMode.Opaque));
        SetFloat("uAlphaCutoff", m?.AlphaCutoff ?? 0.5f);
        SetInt("uUnlit", m?.Unlit == true ? 1 : 0);
        SetInt("uSpecularGlossiness", sg ? 1 : 0);
        SetFloat("uSgGlossinessFactor", m?.SgGlossinessFactor ?? 1f);
        SetVec3("uSgSpecularFactor", m?.SgSpecularFactor ?? Vector3.One);

        // In spec-gloss mode the combined specular-glossiness texture also drives
        // roughness (1 - glossiness.a), so it goes in the metallic-roughness slot.
        var mrTexture = sg ? m!.SgSpecularGlossinessTexture : m?.MetallicRoughnessTexture;
        var specularTexture = sg ? m!.SgSpecularGlossinessTexture : m?.SpecularTexture;

        BindTextureSlot(0, "uBaseColorTex", baseColorTexture, "uHasBaseColorTex", "uBaseColorUv", "uBaseColorSet");
        BindTextureSlot(1, "uMetallicRoughnessTex", mrTexture, "uHasMetallicRoughnessTex", "uMetallicRoughnessUv", "uMetallicRoughnessSet");
        BindTextureSlot(2, "uNormalTex", m?.NormalTexture, "uHasNormalTex", "uNormalUv", "uNormalSet");
        BindTextureSlot(3, "uOcclusionTex", m?.OcclusionTexture, "uHasOcclusionTex", "uOcclusionUv", "uOcclusionSet");
        BindTextureSlot(4, "uEmissiveTex", m?.EmissiveTexture, "uHasEmissiveTex", "uEmissiveUv", "uEmissiveSet");
        BindTextureSlot(5, "uSpecularTex", specularTexture, "uHasSpecularTex", "uSpecularUv", "uSpecularSet");
        BindTextureSlot(6, "uSpecularColorTex", m?.SpecularColorTexture, "uHasSpecularColorTex", "uSpecularColorUv", "uSpecularColorSet");
        BindTextureSlot(7, "uTransmissionTex", m?.TransmissionTexture, "uHasTransmissionTex", "uTransmissionUv", "uTransmissionSet");
        BindTextureSlot(8, "uClearcoatTex", m?.ClearcoatTexture, "uHasClearcoatTex", "uClearcoatUv", "uClearcoatSet");
        BindTextureSlot(9, "uClearcoatRoughnessTex", m?.ClearcoatRoughnessTexture, "uHasClearcoatRoughnessTex", "uClearcoatRoughnessUv", "uClearcoatRoughnessSet");
        BindTextureSlot(10, "uSheenColorTex", m?.SheenColorTexture, "uHasSheenColorTex", "uSheenColorUv", "uSheenColorSet");
        BindTextureSlot(11, "uSheenRoughnessTex", m?.SheenRoughnessTexture, "uHasSheenRoughnessTex", "uSheenRoughnessUv", "uSheenRoughnessSet");
    }

    private void BindTextureSlot(int slot, string samplerName, TextureRef? texRef, string hasName, string uvName, string setName)
    {
        var textureId = texRef is not null && texRef.TextureIndex < _textureIds.Length
            ? _textureIds[texRef.TextureIndex]
            : -1;
        var present = textureId >= 0 ? 1 : 0;
        SetInt(hasName, present);

        _gl.ActiveTexture(TextureUnit.Texture0 + slot);
        _gl.BindTexture(TextureTarget.Texture2D, present == 1 ? (uint)textureId : 0);
        SetInt(samplerName, slot);
        SetInt(setName, texRef?.TexCoord == 1 ? 1 : 0);

        // Per-slot KHR_texture_transform: uv' = offset + R(rotation) * (scale * uv),
        // packed as a GLSL mat3 with the offset in the third column.
        var t = texRef?.Transform ?? UvTransform.Identity;
        var (s, c) = MathF.SinCos(t.Rotation);
        var m = new[]
        {
            t.Scale.X * c, t.Scale.X * s, 0f,
            -t.Scale.Y * s, t.Scale.Y * c, 0f,
            t.Offset.X, t.Offset.Y, 1f,
        };
        _gl.UniformMatrix3(Loc(uvName), 1, false, m);
    }

    // --- uniform helpers -------------------------------------------------

    private int Loc(string name)
    {
        if (_materialUniformCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var loc = _gl.GetUniformLocation(_program, name);
        _materialUniformCache[name] = loc;
        return loc;
    }

    private void SetInt(string name, int v) => _gl.Uniform1(Loc(name), v);
    private void SetFloat(string name, float v) => _gl.Uniform1(Loc(name), v);
    private void SetVec3(string name, Vector3 v) => _gl.Uniform3(Loc(name), v.X, v.Y, v.Z);
    private void SetVec4(string name, Vector4 v) => _gl.Uniform4(Loc(name), v.X, v.Y, v.Z, v.W);

    private void SetFloatArray(string name, float[] values) =>
        _gl.Uniform1(Loc(name), (uint)values.Length, values);

    private void SetVec3Array(string name, Vector3[] values)
    {
        var flat = new float[values.Length * 3];
        for (var i = 0; i < values.Length; i++)
        {
            flat[(i * 3) + 0] = values[i].X;
            flat[(i * 3) + 1] = values[i].Y;
            flat[(i * 3) + 2] = values[i].Z;
        }

        _gl.Uniform3(Loc(name), (uint)values.Length, flat);
    }

    private void SetMat4(string name, Matrix4x4 m)
    {
        Span<float> f = stackalloc float[16];
        f[0] = m.M11; f[1] = m.M12; f[2] = m.M13; f[3] = m.M14;
        f[4] = m.M21; f[5] = m.M22; f[6] = m.M23; f[7] = m.M24;
        f[8] = m.M31; f[9] = m.M32; f[10] = m.M33; f[11] = m.M34;
        f[12] = m.M41; f[13] = m.M42; f[14] = m.M43; f[15] = m.M44;
        _gl.UniformMatrix4(Loc(name), 1, false, f);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var prim in _primitives)
        {
            _gl.DeleteVertexArray(prim.Vao);
            _gl.DeleteBuffer(prim.VertexBuffer);
            _gl.DeleteBuffer(prim.IndexBuffer);
        }

        foreach (var id in _textureIds)
        {
            if (id >= 0)
            {
                _gl.DeleteTexture((uint)id);
            }
        }

        _gl.DeleteProgram(_program);
    }
}
