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

    /// <summary>
    /// Coverage folded into each hair fragment by default: alpha' = 1-(1-alpha)^3.
    /// Measured on a hair-card stack whose authored alpha composites to well under one:
    /// k = 3 sits within a luma level of a fully opaque hair reference and raises edge
    /// energy rather than flattening the strands. Override with
    /// <c>CENTRESTAGE_BLEND_BOOST</c> (1 disables, up to 8).
    /// </summary>
    private const float DefaultHairOpacityBoost = 3f;

    /// <summary>Texture unit for the joint palette buffer texture; material slots use 0..11.</summary>
    private const int JointPaletteUnit = 15;

    /// <summary>Texture unit for the captured opaque scene, sampled by transmission.</summary>
    private const int SceneColorUnit = 14;

    private readonly GL _gl;
    private readonly GltfModel _model;
    private readonly uint _program;

    private readonly int[] _textureIds;
    private uint _environmentCubemap;
    private int _environmentMaxLod;
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
    private readonly int _jointCapacity;
    private readonly uint _jointTbo;
    private readonly uint _jointPaletteTex;
    private readonly Matrix4x4[] _jointScratch;

    // Blend-sort state: triangle order is recomputed only when the view or pose moved.
    private Matrix4x4 _sortedView;
    private int _sortedPoseVersion = -1;
    private bool _sortedOnce;
    private float[] _depthScratch = Array.Empty<float>();
    private ulong[] _sortKeys = Array.Empty<ulong>();

    // Optional alpha-test prepass for a set of blend material families (see
    // DrawBlendCutoutPrepass). Cutoff 0 keeps the pre-change single-pass path exactly.
    private readonly float _blendCutoutCutoff;
    private readonly string[] _blendCutoutFamilies;
    private readonly float _blendOpacityBoost;
    private readonly string[] _blendOpacityFamilies;

    // Measurement aid: suppress a blend material family entirely, to separate "the
    // hair is too transparent" from "the hair does not cover that part of the head".
    private readonly string[]? _hiddenBlendFamilies;

    // Diagnostic: drop every material's emissive term (see CENTRESTAGE_NO_EMISSIVE).
    private readonly bool _suppressEmissive = Environment.GetEnvironmentVariable("CENTRESTAGE_NO_EMISSIVE") == "1";

    // Transmission: transmissive primitives are deferred out of the opaque pass so the
    // fragment can sample the opaque scene behind it (see DrawTransmissivePass).
    // CENTRESTAGE_LEGACY_TRANSMISSION=1 restores the sky-tint approximation, and
    // CENTRESTAGE_REFRACTION_SCALE tunes the screen-space bend.
    private readonly bool _legacyTransmission =
        Environment.GetEnvironmentVariable("CENTRESTAGE_LEGACY_TRANSMISSION") == "1";

    /// <summary>Pinned-evidence renderers keep the pre-change shading path outright.</summary>
    private readonly bool _pinnedEvidence;
    private readonly PresentationSettings _presentation;

    // Measurement aid: skip transmissive primitives entirely, to see what a glass surface
    // is covering (an eye under a cornea, a room behind a window).
    private readonly bool _hideTransmissive =
        Environment.GetEnvironmentVariable("CENTRESTAGE_HIDE_TRANSMISSIVE") == "1";

    private readonly float _refractionScale;

    /// <summary>Set by <see cref="UploadGeometry"/> once the primitives and materials are known.</summary>
    private bool _hasTransmissive;
    private uint _sceneColorTex;
    private uint _sceneFbo;
    private int _sceneTexWidth;
    private int _sceneTexHeight;

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

        /// <summary>CPU-side source data for per-frame blend sorting. Owned by the model.</summary>
        public RuntimePrimitive? Source;
        /// <summary>Rest-pose joint palette (CPU-baked skins), for sort depth. Null when unskinned.</summary>
        public Matrix4x4[]? RestPalette;
        /// <summary>Scratch for the per-frame back-to-front triangle order (blend primitives only).</summary>
        public uint[]? SortedIndices;
    }

    /// <summary>
    /// OpenGL renderer for a GltfModel: draws each node's primitives with per-material
    /// uniforms. <paramref name="pinnedEvidence"/> keeps the exact pre-change shading
    /// path — the blend-opacity boost off and transmission on the sky-tint
    /// approximation — for the neural demo frames and training pairs, which are pinned
    /// by SHA-256 and the PSNRs they print; live viewing and <c>--capture</c> use the
    /// current path. The gate is in code, not inferred from the asset, so a pinned
    /// snapshot with glass in it cannot drift.
    /// </summary>
    public Renderer(GL gl, GltfModel model, ImageResult?[]? decodedTextures = null, bool animate = true,
        bool cheapScene = false, bool pinnedEvidence = false)
    {
        _gl = gl;
        _model = model;
        _pinnedEvidence = pinnedEvidence;
        _presentation = pinnedEvidence ? PresentationSettings.Legacy : PresentationSettings.FromEnvironment();
        if (!pinnedEvidence)
        {
            Console.WriteLine(_presentation.Enabled
                ? FormattableString.Invariant($"  presentation: studio intensity={_presentation.EnvironmentIntensity:0.###}, rotation={_presentation.EnvironmentRotation:0.###}deg, fallbackKey={_presentation.FallbackLightIntensity:0.###}, exposure={_presentation.Exposure:0.###} stops, brightness={_presentation.Brightness:0.###}, contrast={_presentation.Contrast:0.###}, saturation={_presentation.Saturation:0.###}, specularAA={_presentation.SpecularAntialiasing:0.###}, trilinear={_presentation.TrilinearFiltering}, diffusion={_presentation.SkinDiffusion:0.###} ({_presentation.SkinFamilies})")
                : "  presentation: legacy hemispheric lighting/tones");
        }

        var refractionText = Environment.GetEnvironmentVariable("CENTRESTAGE_REFRACTION_SCALE");
        var refractionParsed = float.TryParse(refractionText,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var refraction);
        // glTF transmission does not author a screen-space distortion distance.
        // Do not invent a fixed bend for every shell; retain the old pinned path.
        var defaultRefractionScale = _presentation.DefaultRefractionScale;
        _refractionScale = refractionParsed && refraction >= 0f && refraction <= 0.2f
            ? refraction
            : defaultRefractionScale;
        if (refractionText is { Length: > 0 } && !(refractionParsed && refraction >= 0f && refraction <= 0.2f))
        {
            Console.WriteLine($"  refraction: '{refractionText}' is not a value in [0, 0.2] - using {defaultRefractionScale:0.###}");
        }

        // Opt-in alpha-test prepass for one blend material family. Unset, or a cutoff
        // outside (0,1), leaves the pre-change single-pass blend path untouched.
        _blendCutoutCutoff =
            float.TryParse(Environment.GetEnvironmentVariable("CENTRESTAGE_BLEND_CUTOUT"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var cutoutCutoff)
            && cutoutCutoff > 0f && cutoutCutoff < 1f
                ? cutoutCutoff
                : 0f;
        // Family lists: an unset, empty or whitespace-only value falls back to the default.
        static string[] Families(string name) =>
            BlendMaterialFamily.Parse(Environment.GetEnvironmentVariable(name)) is { Length: > 0 } parsed
                ? parsed
                : BlendMaterialFamily.Parse(BlendMaterialFamily.Hair);

        _blendCutoutFamilies = Families("CENTRESTAGE_BLEND_CUTOUT_MATERIAL");

        // Hair opacity remap: alpha' = 1-(1-alpha)^k, which folds k cards' worth of
        // coverage into each fragment. k of 1 (or a value outside (1, 8]) leaves the
        // shader's default path untouched. On by default for the hair family because a
        // hair card stack at the authored alpha composites to well under one and the
        // skin under the parting shows through; the pinned evidence renders keep k = 1.
        // An explicit but unusable value is reported rather than silently turning the
        // shipped default off.
        var boostText = Environment.GetEnvironmentVariable("CENTRESTAGE_BLEND_BOOST");
        if (boostText is null)
        {
            _blendOpacityBoost = pinnedEvidence ? 1f : DefaultHairOpacityBoost;
        }
        else
        {
            _blendOpacityBoost = float.TryParse(boostText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var boost)
                && boost > 1f && boost <= 8f
                    ? boost
                    : 1f;
            if (_blendOpacityBoost == 1f && boostText.Trim() != "1")
            {
                Console.WriteLine($"  blend boost: '{boostText}' is not a value in (1, 8] - boost disabled");
            }
        }

        _blendOpacityFamilies = Families("CENTRESTAGE_BLEND_BOOST_MATERIAL");
        _hiddenBlendFamilies = BlendMaterialFamily.Parse(
            Environment.GetEnvironmentVariable("CENTRESTAGE_HIDE_BLEND")) is { Length: > 0 } hidden
                ? hidden
                : null;
        if (_blendOpacityBoost > 1f && _blendOpacityFamilies.Length > 0)
        {
            // Name the materials that actually matched: a family entry that matches
            // nothing (a misspelling, a separator inside an entry) is otherwise invisible
            // and the crown just stays bald.
            var matched = new List<string>();
            foreach (var material in model.Materials)
            {
                if (material.Name is { Length: > 0 } name
                    && BlendMaterialFamily.Matches(name, _blendOpacityFamilies))
                {
                    matched.Add(name);
                }
            }

            Console.WriteLine($"  blend boost: alpha remapped by 1-(1-a)^{_blendOpacityBoost:0.0} "
                + $"for material-name tokens {string.Join(",", _blendOpacityFamilies)}");
            Console.WriteLine(matched.Count == 0
                ? "  blend boost: matched no material - check the family list"
                : $"  blend boost: matched {matched.Count} material(s): {string.Join(", ", matched)}");
        }
        if (_blendCutoutCutoff > 0f)
        {
            Console.WriteLine($"  blend cutout: alpha >= {_blendCutoutCutoff:0.00} for materials named "
                + $"'{string.Join(",", _blendCutoutFamilies)}' draws opaque before the blend pass");
        }

        _program = BuildProgram(cheapScene);
        if (_presentation.Enabled && !cheapScene && _presentation.EnvironmentCubemap.Length > 0)
        {
            UploadEnvironmentCubemap();
        }
        _textureIds = new int[model.Textures.Count];
        Array.Fill(_textureIds, -1);
        _srgbTextures = TextureColorSpace.SrgbTextureIndices(model.Materials);
        _gpuSkinning = animate && model.Animations.Count > 0;
        if (_gpuSkinning)
        {
            // Joint palette as a texture buffer (RGBA32F, four texels per mat4). A
            // uniform block would be simpler but is only guaranteed to hold 256 mat4s,
            // which silently froze joints beyond that (the eagle's 287-joint feather
            // skin sat rigid at bind pose). Buffer textures have a far higher floor.
            var needed = 1;
            foreach (var skin in model.Skins)
            {
                needed = Math.Max(needed, skin.JointNodes.Count);
            }

            var capacity = (int)(_gl.GetInteger(GetPName.MaxTextureBufferSize) / 4); // RGBA texels -> mat4s
            _jointCapacity = Math.Min(needed, capacity);
            if (_jointCapacity < needed)
            {
                Console.Error.WriteLine(
                    $"warning: model needs {needed} joints but this GPU's texture buffer holds {_jointCapacity}; "
                    + $"only the first {_jointCapacity} animate.");
            }

            _jointScratch = new Matrix4x4[_jointCapacity];
            _jointTbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.TextureBuffer, _jointTbo);
            _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)(_jointCapacity * 16 * sizeof(float)), null, BufferUsageARB.DynamicDraw);
            _jointPaletteTex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureBuffer, _jointPaletteTex);
            _gl.TexBuffer((GLEnum)TextureTarget.TextureBuffer, (GLEnum)InternalFormat.Rgba32f, _jointTbo);
            _gl.UseProgram(_program);
            SetInt("uJointPalette", JointPaletteUnit);
        }
        else
        {
            _jointCapacity = 0;
            _jointScratch = Array.Empty<Matrix4x4>();
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

    private uint BuildProgram(bool cheapScene)
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
        var fs = Compile(ShaderType.FragmentShader, cheapScene ? ShaderSources.CheapFragment
            : _pinnedEvidence || !_presentation.Enabled ? ShaderSources.Fragment : PresentationShader.Fragment);
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

    private void UploadEnvironmentCubemap()
    {
        var path = _presentation.EnvironmentCubemap;
        var map = LogLuvCubemap.Load(path);
        _environmentCubemap = _gl.GenTexture();
        _gl.ActiveTexture(TextureUnit.Texture13);
        _gl.BindTexture(TextureTarget.TextureCubeMap, _environmentCubemap);
        for (var mip = 0; mip < map.Levels.Count; mip++)
        {
            var level = map.Levels[mip];
            var faceLength = level.Size * level.Size * 3;
            for (var face = 0; face < 6; face++)
            {
                fixed (float* pixels = &level.Rgb[face * faceLength])
                {
                    _gl.TexImage2D((TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face),
                        mip, InternalFormat.Rgb32f, (uint)level.Size, (uint)level.Size, 0,
                        PixelFormat.Rgb, PixelType.Float, pixels);
                }
            }
        }
        _environmentMaxLod = map.Levels.Count - 1;
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        foreach (var axis in new[] { TextureParameterName.TextureWrapS, TextureParameterName.TextureWrapT, TextureParameterName.TextureWrapR })
        {
            _gl.TexParameter(TextureTarget.TextureCubeMap, axis, (int)GLEnum.ClampToEdge);
        }
        Console.WriteLine($"  environment cubemap: {path}, size={map.Levels[0].Size}, mips={map.Levels.Count}, intensity={_presentation.CubemapIntensity}");
    }

    /// <summary>Uploads one decoded texture to the GPU. GL thread only.</summary>
    public void UploadTexture(int index, ImageResult image)
    {
        var tex = _model.Textures[index];
        var id = _gl.GenTexture();
        _gl.ActiveTexture(TextureUnit.Texture0);
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

        var minFilter = _presentation.Enabled && _presentation.TrilinearFiltering
            && tex.MinFilter == 9729 ? 9987 : tex.MinFilter;
        var wrapS = SamplerWraps.Contains(tex.WrapS) ? (GLEnum)tex.WrapS : GLEnum.Repeat;
        var wrapT = SamplerWraps.Contains(tex.WrapT) ? (GLEnum)tex.WrapT : GLEnum.Repeat;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrapS);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrapT);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)SamplerFilter(minFilter, GLEnum.LinearMipmapLinear));
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
                        // what the palette can address (invalid or beyond the buffer's
                        // capacity) drop out here exactly as SkinMatrix does on the CPU —
                        // and the index is sanitised to 0 too, because GLSL reads all four
                        // slots regardless of weight and an out-of-range texelFetch is
                        // undefined.
                        var joints = prim.Joints![i];
                        var weights = prim.Weights![i];
                        var jointCount = Math.Min(jointMatrices!.Length, _jointCapacity);
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
                            // Positive form: NaN fails both comparisons, so the negation
                            // rejects it too (a NaN index would make the shader's int()
                            // conversion and the texelFetch index undefined).
                            if (!(jx[s] >= 0f && jx[s] < jointCount))
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
                    // CPU-side data for the per-triangle blend sort: the raw primitive
                    // and the rest palette that produced baked world-space vertices.
                    Source = prim,
                    RestPalette = jointMatrices,
                    SortedIndices = new uint[indices.Length],
                });
            }
        }

        Bounds = any ? (min, max) : (Vector3.Zero, Vector3.One);

        // Known up front from the materials: decides whether the opaque pass has to be
        // split so transmission can sample what is behind it.
        _hasTransmissive = _primitives.Any(p => IsTransmissive(p.MaterialIndex));
        if (_hasTransmissive && !_legacyTransmission)
        {
            Console.WriteLine($"  transmission: {_primitives.Count(p => IsTransmissive(p.MaterialIndex))} "
                + $"primitive(s) sample the scene (refraction scale {_refractionScale:0.###})");
        }
    }

    private bool IsTransmissive(int materialIndex) =>
        materialIndex >= 0 && materialIndex < _model.Materials.Count
        && (_model.Materials[materialIndex].TransmissionFactor > 0f
            || _model.Materials[materialIndex].TransmissionTexture is not null);

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
    /// current pose. Clamped to the buffer's capacity (sized to the model's largest
    /// skin): oversized rigs keep their lower joints rather than overflowing.
    /// </summary>
    private void UploadJointPalette(ScenePose pose, int skinIndex)
    {
        var skin = _model.Skins[skinIndex];
        var count = Math.Min(skin.JointNodes.Count, _jointCapacity);

        pose.JointMatrices(skin, _jointScratch.AsSpan(0, count));
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, _jointTbo);
        fixed (Matrix4x4* p = _jointScratch)
        {
            _gl.BufferSubData(BufferTargetARB.TextureBuffer, 0, (nuint)(count * 16 * sizeof(float)), p);
        }

        _gl.ActiveTexture(TextureUnit.Texture0 + JointPaletteUnit);
        _gl.BindTexture(TextureTarget.TextureBuffer, _jointPaletteTex);
    }

    /// <summary>Weighted skin matrix for one vertex: sum of w * (inverseBind * jointWorld) over 4 joints.</summary>
    /// <summary>NaN joint indices map to 0 (their weight is zeroed by SanitiseWeight), matching the upload path.</summary>
    private static float SanitiseJoint(float joint) => float.IsNaN(joint) ? 0f : joint;

    /// <summary>Slots whose joint is invalid/NaN or whose weight is non-positive drop out.</summary>
    private static float SanitiseWeight(float weight, float joint) =>
        float.IsNaN(joint) || float.IsNaN(weight) ? 0f : MathF.Max(weight, 0f);

    private static Matrix4x4 SkinMatrix(ReadOnlySpan<Matrix4x4> jointMatrices, Vector4 joints, Vector4 weights)
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
    /// Rewrites a blend primitive's index buffer into back-to-front triangle order for
    /// the current view and pose (painter's algorithm — straight-alpha compositing is
    /// order-dependent). Depth is per-vertex view-space Z computed through exactly the
    /// transform chain the draw call uses, so the order matches what rasterises.
    /// </summary>
    private void UpdateBlendSort(GlPrimitive prim, Matrix4x4 view, ScenePose? pose)
    {
        if (prim.Source is not { } src || prim.SortedIndices is null)
        {
            return;
        }

        var triCount = prim.IndexCount / 3;
        if (triCount < 2)
        {
            return;
        }

        var positions = src.Positions;
        var vertexCount = positions.Length;
        if (_depthScratch.Length < vertexCount)
        {
            _depthScratch = new float[vertexCount];
        }

        if (_sortKeys.Length < triCount)
        {
            _sortKeys = new ulong[triCount];
        }

        // Mirror the draw path exactly: GPU-skinned primitives transform through the
        // posed palette at identity; CPU-baked skinned ones were uploaded world-space
        // through the rest palette; everything else takes its node's world matrix.
        // A GPU-skinned primitive drawn without a pose is NOT skinned at all (the
        // shader gets uSkinned=0 and raw vertices), so it must not be skinned here.
        var skinned = pose is not null && prim.GpuSkinned && prim.SkinIndex >= 0;
        Matrix4x4 model;
        ReadOnlySpan<Matrix4x4> posed = default;
        if (skinned)
        {
            var skin = _model.Skins[prim.SkinIndex];
            var count = Math.Min(skin.JointNodes.Count, _jointCapacity);
            pose!.JointMatrices(skin, _jointScratch.AsSpan(0, count));
            posed = _jointScratch.AsSpan(0, count);
            model = Matrix4x4.Identity;
        }
        else if (prim.GpuSkinned)
        {
            model = prim.World;
        }
        else
        {
            model = pose is not null ? pose.World(prim.NodeIndex) : prim.World;
        }

        var joints = src.Joints;
        var weights = src.Weights;
        var baked = prim.GpuSkinned ? null : prim.RestPalette;
        for (var i = 0; i < vertexCount; i++)
        {
            var pos = positions[i];
            if (joints is not null && weights is not null)
            {
                var j = joints[i];
                var w = weights[i];

                // Mirror the upload sanitisation exactly (NaN joints drop out with
                // their weight so the palette lookup is always defined) and the
                // shader's NaN fallback: a NaN weight sum makes the whole vertex
                // rasterise raw, so it must sort raw too. Without this the sort can
                // disagree with what actually draws on corrupt attributes.
                if (!float.IsNaN(w.X + w.Y + w.Z + w.W))
                {
                    var jx = new Vector4(SanitiseJoint(j.X), SanitiseJoint(j.Y), SanitiseJoint(j.Z), SanitiseJoint(j.W));
                    var wx = new Vector4(
                        SanitiseWeight(w.X, j.X), SanitiseWeight(w.Y, j.Y),
                        SanitiseWeight(w.Z, j.Z), SanitiseWeight(w.W, j.W));
                    if (!posed.IsEmpty)
                    {
                        pos = Vector3.Transform(pos, SkinMatrix(posed, jx, wx));
                    }
                    else if (baked is not null)
                    {
                        pos = Vector3.Transform(pos, SkinMatrix(baked, jx, wx));
                    }
                }
            }

            _depthScratch[i] = Vector3.Transform(Vector3.Transform(pos, model), view).Z;
        }

        TriangleSort.SortBackToFront(
            _depthScratch.AsSpan(0, vertexCount),
            src.Indices.AsSpan(0, prim.IndexCount),
            prim.SortedIndices.AsSpan(0, prim.IndexCount),
            _sortKeys.AsSpan(0, triCount));

        _gl.BindVertexArray(prim.Vao);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, prim.IndexBuffer);
        fixed (uint* p = prim.SortedIndices)
        {
            _gl.BufferSubData(BufferTargetARB.ElementArrayBuffer, 0, (nuint)(prim.IndexCount * sizeof(uint)), p);
        }
    }

    /// <summary>
    /// Draws the frame. <paramref name="pose"/> is null for static models (drawn exactly
    /// as uploaded) or a posed scene for animated models: unskinned primitives pick up
    /// their node's posed world matrix, GPU-skinned ones get the skin's joint palette
    /// uploaded before their draw call.
    /// </summary>
    public void Draw(OrbitCamera camera, float aspect, IReadOnlyList<RuntimeLight> lights, Vector3 sky, Vector3 ground, ScenePose? pose = null)
    {
        DrawCore(camera, aspect, lights, sky, ground, pose, coverage: false, blendMaterialFamily: null);
    }

    /// <summary>
    /// Diagnostic coverage pass: draws the opaque depth first, then composites only the
    /// blended primitives in front of it and leaves their accumulated coverage in the
    /// alpha channel (the colour channels are not meaningful). A pixel that never sees
    /// a blend fragment stays at alpha 0; a pixel covered by an opaque layer reaches 1.
    /// The caller must supply an RGBA target. <paramref name="blendMaterialFamily"/>
    /// optionally restricts the measurement to one material family (for example "hair").
    /// </summary>
    public void DrawCoverage(OrbitCamera camera, float aspect, IReadOnlyList<RuntimeLight> lights,
        Vector3 sky, Vector3 ground, ScenePose? pose = null, string? blendMaterialFamily = null)
    {
        DrawCore(camera, aspect, lights, sky, ground, pose, coverage: true, blendMaterialFamily);
    }

    private void DrawCore(OrbitCamera camera, float aspect, IReadOnlyList<RuntimeLight> lights,
        Vector3 sky, Vector3 ground, ScenePose? pose, bool coverage, string? blendMaterialFamily)
    {
        // Parsed once per frame rather than per primitive.
        var coverageFamilies = blendMaterialFamily is null ? null : BlendMaterialFamily.Parse(blendMaterialFamily);
        // Alpha-to-coverage needs a multisampled draw framebuffer. Key on the
        // framebuffer actually bound, not on a global flag: the demo captures render
        // into their own single-sample FBOs, so they keep the exact blending they
        // were pinned against even when the window is multisampled.
        var msaa = _gl.GetInteger(GetPName.Samples) > 1;
        _gl.Disable(EnableCap.SampleAlphaToCoverage);
        _gl.Disable(EnableCap.SampleAlphaToOne);

        _gl.ClearColor(sky.X * 0.25f, sky.Y * 0.25f, sky.Z * 0.25f, coverage ? 0f : 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _gl.UseProgram(_program);
        var view = camera.View;
        SetMat4("uView", view);
        SetMat4("uProjection", camera.Projection(aspect));
        SetVec3("uCameraPos", camera.Position);
        SetVec3("uSkyColor", sky);
        SetVec3("uGroundColor", ground);
        SetFloat("uEnvironmentIntensity", _presentation.EnvironmentIntensity);
        SetFloat("uEnvironmentRotation", _presentation.EnvironmentRotation * MathF.PI / 180f);
        SetInt("uHasEnvironmentCubemap", _environmentCubemap != 0 ? 1 : 0);
        SetInt("uEnvironmentCubemap", 13);
        SetFloat("uEnvironmentMaxLod", _environmentMaxLod);
        SetFloat("uCubemapIntensity", _presentation.CubemapIntensity);
        _gl.ActiveTexture(TextureUnit.Texture13);
        _gl.BindTexture(TextureTarget.TextureCubeMap, _environmentCubemap);
        SetFloat("uExposure", _presentation.Exposure);
        SetFloat("uBrightness", _presentation.Brightness);
        SetFloat("uContrast", _presentation.Contrast);
        SetFloat("uSaturation", _presentation.Saturation);

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
        if (coverage)
        {
            // The opaque pass exists only for its depth: keep its alpha out of the
            // coverage channel so the blend pass alone fills it.
            _gl.ColorMask(true, true, true, false);
        }

        // Opaque + masked first (depth writes on). Blended primitives then draw in
        // per-triangle back-to-front order (the painter's algorithm): straight-alpha
        // compositing is order-dependent, and index-order drawing lets near cards paint
        // over far ones that should show through. Triangle order is only recomputed when
        // the view or pose changed — a still frame costs nothing.
        var poseVersion = pose?.Version ?? -1;
        var sortDirty = !_sortedOnce || poseVersion != _sortedPoseVersion || view != _sortedView;
        if (sortDirty)
        {
            _sortedView = view;
            _sortedPoseVersion = poseVersion;
            _sortedOnce = true;
        }
        if (_ordered.Count != _primitives.Count)
        {
            _ordered.Clear();
            _ordered.AddRange(_primitives);
        }

        // Transmissive primitives leave the opaque pass so they can be drawn against a copy
        // of the scene behind them; without a copy (legacy path, or the coverage pass,
        // which only wants hair depth) they stay in the opaque pass and keep the old
        // sky-tint approximation.
        var deferTransmission = !_legacyTransmission && !_pinnedEvidence && _hasTransmissive && !coverage;
        var skipTransmission = deferTransmission || _hideTransmissive;

        foreach (var pass in new[] { AlphaMode.Opaque, AlphaMode.Blend })
        {
            if (pass == AlphaMode.Blend)
            {
                if (deferTransmission && !_hideTransmissive)
                {
                    DrawTransmissivePass(pose, view);
                }

                if (!coverage && _blendCutoutCutoff > 0f)
                {
                    DrawBlendCutoutPrepass(pose, view);
                }

                _gl.Enable(EnableCap.Blend);
                if (coverage)
                {
                    _gl.ColorMask(true, true, true, true);
                    // Accumulate the blended layers' union coverage in alpha:
                    // dst.a = src.a + (1 - src.a) * dst.a.
                    _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
                        BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                }
                else
                {
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
                _gl.DepthMask(false);
                if (msaa)
                {
                    // Hair cards are cutout-like cards, not a smooth glass surface:
                    // let the alpha choose which MSAA samples the fragment covers and
                    // force the alpha to one in the covered samples (SampleAlphaToOne),
                    // so the blend unit does not apply the same alpha a second time.
                    // Without AlphaToOne, coverage and blending both attenuate the
                    // result and the cards become more transparent, not less.
                    _gl.Enable(EnableCap.SampleAlphaToCoverage);
                    _gl.Enable(EnableCap.SampleAlphaToOne);
                }
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

                if (pass == AlphaMode.Opaque && skipTransmission && IsTransmissive(prim.MaterialIndex))
                {
                    continue;
                }

                if (coverage && pass == AlphaMode.Blend && coverageFamilies is not null
                    && !BlendMaterialMatches(prim.MaterialIndex, coverageFamilies))
                {
                    continue;
                }

                if (pass == AlphaMode.Blend && _hiddenBlendFamilies is not null
                    && BlendMaterialMatches(prim.MaterialIndex, _hiddenBlendFamilies))
                {
                    continue;
                }

                DrawPrimitive(prim, pose, view, sortIsDirty: pass == AlphaMode.Blend && sortDirty);
            }

            if (pass == AlphaMode.Blend)
            {
                if (msaa)
                {
                    _gl.Disable(EnableCap.SampleAlphaToCoverage);
                    _gl.Disable(EnableCap.SampleAlphaToOne);
                }
                _gl.DepthMask(true);
                _gl.Disable(EnableCap.Blend);
            }
        }

        _gl.BindVertexArray(0);
        _gl.ColorMask(true, true, true, true);
    }

    /// <summary>
    /// Draws the transmissive primitives after the opaque pass, sampling the colour that
    /// is actually behind them instead of the sky.
    ///
    /// Splitting the opaque pass is what makes the sample valid: a transmissive shell
    /// drawn in pass one would read its own pixel, or a pixel the eye behind it had not
    /// written yet. The capture holds opaque geometry only; blended material in front of
    /// glass still draws afterwards, in sorted order, and depth-tests against the glass,
    /// so it composites normally. Glass that is itself behind blended geometry is the one
    /// case this ordering cannot serve, and was already approximated before.
    /// </summary>
    private void DrawTransmissivePass(ScenePose? pose, Matrix4x4 view)
    {
        // A failed capture must not drop the surface: the shader falls back to the
        // pre-change sky tint when uHasSceneColor is 0, which is what the legacy path
        // uses anyway. Drawing unconditionally is what keeps a glass surface visible
        // on a driver that refuses the blit.
        var captured = CaptureSceneColor();
        SetInt("uHasSceneColor", captured ? 1 : 0);
        SetFloat("uRefractionScale", _refractionScale);
        foreach (var prim in _primitives)
        {
            if (prim.AlphaMode != AlphaMode.Opaque || !IsTransmissive(prim.MaterialIndex))
            {
                continue;
            }

            DrawPrimitive(prim, pose, view, sortIsDirty: false);
        }

        SetInt("uHasSceneColor", 0);
    }

    /// <summary>
    /// Copies the colour currently in the bound draw framebuffer into the refraction
    /// texture, resolving multisampling through the blit (a copy straight out of a
    /// multisampled buffer is not allowed). Returns false, leaving the shader on the
    /// sky-tint fallback, when the viewport is empty or taller/wider than the driver's
    /// texture limit.
    /// </summary>
    private bool CaptureSceneColor()
    {
        Span<int> viewport = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, viewport);
        var x = viewport[0];
        var y = viewport[1];
        var width = viewport[2];
        var height = viewport[3];
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var maxSize = _gl.GetInteger(GetPName.MaxTextureSize);
        if (width > maxSize || height > maxSize)
        {
            return false;
        }

        if (_sceneColorTex == 0)
        {
            _sceneColorTex = _gl.GenTexture();
            _sceneFbo = _gl.GenFramebuffer();
        }

        // Whatever the caller bound for drawing may not be framebuffer 0: the demo
        // renderers draw into their own single-sample FBOs. Save both bindings and put
        // them back, so creating or resizing the copy cannot leak a binding.
        var source = (uint)_gl.GetInteger(GetPName.DrawFramebufferBinding);
        var readSource = (uint)_gl.GetInteger(GetPName.ReadFramebufferBinding);

        if (width != _sceneTexWidth || height != _sceneTexHeight)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readSource);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _sceneColorTex, 0);
            var complete = _gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer)
                == GLEnum.FramebufferComplete;
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readSource);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, source);
            _sceneTexWidth = width;
            _sceneTexHeight = height;
            if (!complete)
            {
                _sceneTexWidth = 0;
                _sceneTexHeight = 0;
                return false;
            }
        }

        // Clear any earlier error so the check below only judges the blit. A blit whose
        // source and destination internal formats differ is INVALID_OPERATION and copies
        // nothing, which would leave the texture uninitialised while the shader samples
        // it as real colour; returning false keeps the sky-tint fallback instead.
        for (var i = 0; i < 8 && _gl.GetError() != GLEnum.NoError; i++)
        {
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, source);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneFbo);
        _gl.BlitFramebuffer(x, y, x + width, y + height, 0, 0, width, height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        var blitError = _gl.GetError();
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readSource);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, source);
        if (blitError != GLEnum.NoError)
        {
            return false;
        }

        _gl.ActiveTexture(TextureUnit.Texture0 + SceneColorUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        SetInt("uSceneColor", SceneColorUnit);
        SetVec2("uSceneTexel", 1f / width, 1f / height);
        SetVec2("uSceneOrigin", x, y);
        SetFloat("uSceneExtent", Math.Min(width, height));
        return true;
    }

    private bool BlendMaterialMatches(int materialIndex, ReadOnlySpan<string> families)
    {
        return materialIndex >= 0 && materialIndex < _model.Materials.Count
            && BlendMaterialFamily.Matches(_model.Materials[materialIndex].Name, families);
    }

    /// <summary>
    /// Draws the named blend material family's dense strand interiors as depth-writing
    /// opaque geometry, before the sorted blend pass runs.
    ///
    /// The hair atlas is alpha-painted: 30% of its texels are fully transparent gaps and
    /// the rest is spread across the range (only 36% of texels reach 0.5), so a stack of
    /// cards composites to a mean opacity well under one and the bald scalp UV beneath
    /// shows through. Splitting the material at a cutoff fixes that where the cards are
    /// dense — those fragments become opaque and write depth, so nothing behind them can
    /// bleed — while the genuinely thin remainder still blends, in sorted order, in the
    /// pass that follows.
    ///
    /// Depth does the de-duplication: a fragment written here sits at exactly its own
    /// depth, so the same fragment in the blend pass fails <c>GL_LESS</c> and is not
    /// composited a second time. No shader variant and no second alpha test are needed.
    /// </summary>
    private void DrawBlendCutoutPrepass(ScenePose? pose, Matrix4x4 view)
    {
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);

        foreach (var prim in _ordered)
        {
            if (prim.AlphaMode != AlphaMode.Blend
                || !BlendMaterialMatches(prim.MaterialIndex, _blendCutoutFamilies))
            {
                continue;
            }

            // DrawPrimitive applies the material, so the mask override has to survive
            // that call: pass it in rather than setting it here.
            DrawPrimitive(prim, pose, view, sortIsDirty: false,
                alphaModeOverride: (int)AlphaMode.Mask, alphaCutoff: _blendCutoutCutoff);
        }
    }

    private void DrawPrimitive(GlPrimitive prim, ScenePose? pose, Matrix4x4 view, bool sortIsDirty,
        int alphaModeOverride = -1, float alphaCutoff = 0f)
    {
        ApplyMaterial(prim.MaterialIndex);
        if (alphaModeOverride >= 0)
        {
            SetInt("uAlphaMode", alphaModeOverride);
            SetFloat("uAlphaCutoff", alphaCutoff);
        }

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

        if (sortIsDirty)
        {
            UpdateBlendSort(prim, view, pose);
        }

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

    private void UploadLights(IReadOnlyList<RuntimeLight> lights)
    {
        var count = Math.Min(lights.Count, MaxLights);
        SetInt("uLightCount", count);

        var hasSceneLights = count > 0;
        // Legacy/pinned views retain the original hard key. Studio lighting supplies
        // illumination itself; an optional diagnostic key is used only without authored lights.
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
            intensity[0] = _presentation.EffectiveFallbackLightIntensity;
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
        // Diagnostic: CENTRESTAGE_NO_EMISSIVE=1 drops every material's emissive term. An
        // asset whose exporter invented emission (factor 1 over a reflection map) then
        // reads correctly, which separates "the glTF says glow" from "the renderer does".
        SetVec3("uEmissiveFactor",
            _suppressEmissive ? Vector3.Zero : m?.EmissiveFactor ?? Vector3.Zero);
        SetFloat("uEmissiveStrength", m?.EmissiveStrength ?? 1f);
        SetFloat("uNormalScale", m?.NormalScale ?? 1f);
        SetFloat("uSpecularAntialiasing", _presentation.SpecularAntialiasing);
        SetFloat("uSkinDiffusion", BlendMaterialFamily.Matches(m?.Name, _presentation.SkinFamilies)
            ? _presentation.SkinDiffusion : 0f);
        SetFloat("uOcclusionStrength", m?.OcclusionStrength ?? 1f);
        SetInt("uAlphaMode", (int)(m?.AlphaMode ?? AlphaMode.Opaque));
        SetFloat("uAlphaCutoff", m?.AlphaCutoff ?? 0.5f);
        // The shader only consults this when uAlphaMode == 2, so the cutout prepass
        // (which overrides the mode to mask) is unaffected.
        SetFloat("uAlphaBoost",
            _blendOpacityBoost > 1f && BlendMaterialMatches(materialIndex, _blendOpacityFamilies)
                ? _blendOpacityBoost
                : 1f);
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
    private void SetVec2(string name, float x, float y) => _gl.Uniform2(Loc(name), x, y);
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

        if (_environmentCubemap != 0)
        {
            _gl.DeleteTexture(_environmentCubemap);
        }

        if (_jointPaletteTex != 0)
        {
            _gl.DeleteTexture(_jointPaletteTex);
            _gl.DeleteBuffer(_jointTbo);
        }

        if (_sceneColorTex != 0)
        {
            _gl.DeleteTexture(_sceneColorTex);
        }

        if (_sceneFbo != 0)
        {
            _gl.DeleteFramebuffer(_sceneFbo);
        }

        _gl.DeleteProgram(_program);
    }
}
