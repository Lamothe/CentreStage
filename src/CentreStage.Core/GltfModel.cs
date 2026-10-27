using System.Numerics;
using System.Text;
using System.Text.Json;
using CentreStage.Core.Schema;

namespace CentreStage.Core;

/// <summary>
/// Loads a GLB (or .gltf + external/data-URI buffers) into a runtime model ready to render.
/// This is the layer a thin UI shell can never reach through — everything the file says
/// is decoded here, or explicitly reported as unsupported.
/// </summary>
public sealed class GltfModel
{
    /// <summary>Extensions we can honour. Anything structural and unsupported is refused at load.</summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.Ordinal)
    {
        "KHR_lights_punctual",
        "KHR_materials_clearcoat",
        "KHR_materials_emissive_strength",
        "KHR_materials_ior",
        "KHR_materials_pbrSpecularGlossiness",
        "KHR_materials_sheen",
        "KHR_materials_specular",
        "KHR_materials_transmission",
        "KHR_materials_unlit",
        "KHR_texture_transform",
    };

    /// <summary>Required extensions whose data we cannot recover without a decoder.</summary>
    private static readonly HashSet<string> StructuralExtensions = new(StringComparer.Ordinal)
    {
        "KHR_draco_mesh_compression",
        "EXT_meshopt_compression",
        "KHR_texture_basisu",
        "KHR_mesh_quantization",
    };

    public GltfRoot Root { get; }
    public BufferData Buffers { get; }
    public IReadOnlyList<RuntimeMesh> Meshes { get; }
    public IReadOnlyList<RuntimeNode> Nodes { get; }
    public IReadOnlyList<RuntimeMaterial> Materials { get; }
    public IReadOnlyList<RuntimeTexture> Textures { get; }
    public IReadOnlyList<RuntimeLight> Lights { get; }
    public IReadOnlyList<RuntimeSkin> Skins { get; }
    /// <summary>Animation clips carried by the file.</summary>
    public IReadOnlyList<RuntimeAnimation> Animations { get; }
    /// <summary>Extensions used but not honoured — model loads, some aspects render plainly.</summary>
    public IReadOnlyList<string> IgnoredExtensions { get; }
    public string? Generator { get; }
    /// <summary>
    /// World transforms by manifest node index — for the whole node graph, not just the
    /// default scene, because skin.joints need not be reachable from the scene roots.
    /// </summary>
    public IReadOnlyDictionary<int, Matrix4x4> NodeWorlds { get; }

    private GltfModel(
        GltfRoot root,
        BufferData buffers,
        IReadOnlyList<RuntimeMesh> meshes,
        IReadOnlyList<RuntimeNode> nodes,
        IReadOnlyList<RuntimeMaterial> materials,
        IReadOnlyList<RuntimeTexture> textures,
        IReadOnlyList<RuntimeLight> lights,
        IReadOnlyList<RuntimeSkin> skins,
        IReadOnlyList<RuntimeAnimation> animations,
        IReadOnlyDictionary<int, Matrix4x4> nodeWorlds,
        IReadOnlyList<string> ignoredExtensions)
    {
        Root = root;
        Buffers = buffers;
        Meshes = meshes;
        Nodes = nodes;
        Materials = materials;
        Textures = textures;
        Lights = lights;
        Skins = skins;
        Animations = animations;
        NodeWorlds = nodeWorlds;
        IgnoredExtensions = ignoredExtensions;
        Generator = root.Asset?.Generator;
    }

    public static GltfModel Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Load(bytes, Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    public static GltfModel Load(byte[] data, string? baseDirectory = null)
    {
        GltfRoot root;
        ReadOnlyMemory<byte> binChunk = default;

        if (Glb.GlbFile.IsGlb(data))
        {
            var glb = Glb.GlbFile.Parse(data);
            root = JsonSerializer.Deserialize<GltfRoot>(glb.Manifest.GetRawText())
                ?? throw new InvalidDataException("GLB manifest deserialised to null.");
            binChunk = glb.BinChunk;
        }
        else
        {
            root = JsonSerializer.Deserialize<GltfRoot>(data)
                ?? throw new InvalidDataException("glTF JSON deserialised to null.");
        }

        CheckVersion(root);
        var ignored = CheckExtensions(root);

        var bufferBytes = ResolveBuffers(root, binChunk, baseDirectory);
        var buffers = new BufferData(root, bufferBytes);
        var textures = BuildTextures(root, buffers);
        var materials = BuildMaterials(root);
        var (meshes, nodes, lights, nodeWorlds) = BuildScene(root, buffers);
        var skins = BuildSkins(root, buffers);
        var animations = BuildAnimations(root, buffers);

        return new GltfModel(root, buffers, meshes, nodes, materials, textures, lights, skins, animations, nodeWorlds, ignored);
    }

    private static List<RuntimeSkin> BuildSkins(GltfRoot root, BufferData buffers)
    {
        var result = new List<RuntimeSkin>();
        var skins = root.Skins ?? new List<GltfSkin>();
        foreach (var skin in skins)
        {
            var joints = skin.Joints ?? throw new InvalidDataException("Skin without joints is not valid glTF.");
            var matrices = new List<Matrix4x4>(joints.Count);
            if (skin.InverseBindMatrices is int ibmAccessor)
            {
                var floats = buffers.ReadAccessorFloats(ibmAccessor);
                for (var j = 0; j < joints.Count; j++)
                {
                    matrices.Add(GltfMath.FromGltfMatrix(floats.AsSpan(j * 16, 16)));
                }
            }
            else
            {
                for (var j = 0; j < joints.Count; j++)
                {
                    matrices.Add(Matrix4x4.Identity);
                }
            }

            result.Add(new RuntimeSkin
            {
                Name = skin.Name,
                JointNodes = joints,
                InverseBindMatrices = matrices,
            });
        }

        return result;
    }

    /// <summary>
    /// Decodes animation clips: keyframe times plus packed sampler outputs. Channels
    /// targeting morph weights are recorded but not applied (morphs are unsupported);
    /// samplers they exclusively own are still kept so clip duration stays honest.
    /// </summary>
    private static List<RuntimeAnimation> BuildAnimations(GltfRoot root, BufferData buffers)
    {
        var result = new List<RuntimeAnimation>();
        foreach (var anim in root.Animations ?? new List<GltfAnimation>())
        {
            var samplers = new List<RuntimeAnimationSampler>();
            foreach (var s in anim.Samplers ?? new List<GltfAnimationSampler>())
            {
                var input = buffers.ReadAccessorFloats(s.Input);
                var output = buffers.ReadAccessorFloats(s.Output);
                samplers.Add(new RuntimeAnimationSampler
                {
                    Input = input,
                    Output = output,
                    Interpolation = s.Interpolation switch
                    {
                        "STEP" => AnimationInterpolation.Step,
                        "CUBICSPLINE" => AnimationInterpolation.CubicSpline,
                        _ => AnimationInterpolation.Linear,
                    },
                });
            }

            var channels = new List<RuntimeAnimationChannel>();
            foreach (var c in anim.Channels ?? new List<GltfAnimationChannel>())
            {
                // Target-less channels (KHR_animation_pointer) cannot be resolved here.
                if (c.Target?.Node is not int node || node < 0)
                {
                    continue;
                }

                var path = c.Target.Path switch
                {
                    "translation" => AnimationPath.Translation,
                    "rotation" => AnimationPath.Rotation,
                    "scale" => AnimationPath.Scale,
                    "weights" => AnimationPath.Weights,
                    _ => (AnimationPath?)null,
                };
                if (path is null || c.Sampler < 0 || c.Sampler >= samplers.Count)
                {
                    continue;
                }

                channels.Add(new RuntimeAnimationChannel
                {
                    NodeIndex = node,
                    Path = path.Value,
                    SamplerIndex = c.Sampler,
                });
            }

            var duration = 0f;
            foreach (var s in samplers)
            {
                if (s.KeyCount > 0)
                {
                    duration = MathF.Max(duration, s.Input[s.KeyCount - 1]);
                }
            }

            result.Add(new RuntimeAnimation
            {
                Name = anim.Name,
                Duration = duration,
                Channels = channels,
                Samplers = samplers,
            });
        }

        return result;
    }

    private static void CheckVersion(GltfRoot root)
    {
        var version = root.Asset?.Version;
        if (version is null || !version.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported glTF asset version '{version ?? "(none)"}'; this loader reads glTF 2.x.");
        }
    }

    /// <summary>
    /// Enforces the extensionsRequired contract without Exhibit's zero-tolerance reading of it.
    ///
    /// Required + structural (compression/encoding baked into the data) = genuinely undecodable,
    /// refuse with the extension named. Required + anything else (metadata, material extras,
    /// variant switching) = load anyway and report it: geometry still renders, and a locked
    /// door is the worst outcome of all. Unsupported-but-optional extensions are reported too.
    /// </summary>
    private static IReadOnlyList<string> CheckExtensions(GltfRoot root)
    {
        var required = root.ExtensionsRequired ?? (IReadOnlyList<string>)Array.Empty<string>();
        var fatal = required.Where(e => StructuralExtensions.Contains(e)).ToList();
        if (fatal.Count > 0)
        {
            throw new UnsupportedExtensionsException(fatal);
        }

        var used = root.ExtensionsUsed ?? (IReadOnlyList<string>)Array.Empty<string>();
        return required.Concat(used)
            .Where(e => !SupportedExtensions.Contains(e))
            .Distinct()
            .ToList();
    }

    private static List<byte[]> ResolveBuffers(GltfRoot root, ReadOnlyMemory<byte> binChunk, string? baseDirectory)
    {
        var result = new List<byte[]>();
        var buffers = root.Buffers ?? new List<GltfBuffer>();
        for (var i = 0; i < buffers.Count; i++)
        {
            var spec = buffers[i];
            byte[] bytes;
            if (spec.Uri is null)
            {
                if (binChunk.IsEmpty)
                {
                    throw new InvalidDataException($"Buffer #{i} has no URI but the file has no BIN chunk.");
                }

                bytes = binChunk.ToArray();
            }
            else if (spec.Uri.StartsWith("data:", StringComparison.Ordinal))
            {
                bytes = DecodeDataUri(spec.Uri, i);
            }
            else
            {
                var uri = Uri.UnescapeDataString(spec.Uri);
                var dir = baseDirectory ?? throw new InvalidDataException(
                    $"Buffer #{i} references external file '{uri}' but no base directory is known.");
                bytes = File.ReadAllBytes(Path.Combine(dir, uri));
            }

            if (bytes.Length < spec.ByteLength)
            {
                throw new InvalidDataException($"Buffer #{i} is {bytes.Length} bytes; manifest declares {spec.ByteLength}.");
            }

            result.Add(bytes);
        }

        return result;
    }

    private static byte[] DecodeDataUri(string uri, int bufferIndex)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0)
        {
            throw new InvalidDataException($"Buffer #{bufferIndex} has a malformed data URI.");
        }

        var meta = uri[..comma];
        var payload = uri[(comma + 1)..];
        return meta.Contains(";base64", StringComparison.Ordinal)
            ? Convert.FromBase64String(payload)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
    }

    private static List<RuntimeTexture> BuildTextures(GltfRoot root, BufferData buffers)
    {
        var result = new List<RuntimeTexture>();
        var textures = root.Textures ?? new List<GltfTexture>();
        foreach (var tex in textures)
        {
            if (tex.Source is not int src)
            {
                throw new InvalidDataException("Texture without a source image is not valid glTF.");
            }

            var image = root.Images?[src] ?? throw new InvalidDataException($"Texture references missing image #{src}.");
            byte[] data;
            string? mime = image.MimeType;
            if (image.BufferView is int bv)
            {
                data = buffers.GetBufferView(bv).ToArray();
            }
            else if (image.Uri is not null && image.Uri.StartsWith("data:", StringComparison.Ordinal))
            {
                data = DecodeDataUri(image.Uri, src);
                var semi = image.Uri.IndexOf(';');
                if (semi > 5)
                {
                    mime = image.Uri[5..semi];
                }
            }
            else if (image.Uri is not null)
            {
                throw new InvalidDataException($"Image '{image.Uri}' is an external file; embedded images only for now.");
            }
            else
            {
                throw new InvalidDataException($"Image #{src} has neither bufferView nor URI.");
            }

            var sampler = tex.Sampler is int s && root.Samplers is not null ? root.Samplers[s] : new GltfSampler();
            result.Add(new RuntimeTexture
            {
                Data = data,
                MimeType = mime,
                WrapS = sampler.WrapS ?? 10497,
                WrapT = sampler.WrapT ?? 10497,
                MagFilter = sampler.MagFilter ?? 9729,
                MinFilter = sampler.MinFilter ?? 9987,
            });
        }

        return result;
    }

    private static List<RuntimeMaterial> BuildMaterials(GltfRoot root)
    {
        var result = new List<RuntimeMaterial>();
        var materials = root.Materials ?? new List<GltfMaterial>();
        foreach (var m in materials)
        {
            var pbr = m.PbrMetallicRoughness;
            var ext = m.Extensions;
            var sg = ext?.PbrSpecularGlossiness;

            result.Add(new RuntimeMaterial
            {
                Name = m.Name,
                BaseColorFactor = ToVec4(pbr?.BaseColorFactor, Vector4.One),
                BaseColorTexture = ToTextureRef(pbr?.BaseColorTexture),
                MetallicFactor = pbr?.MetallicFactor ?? 1f,
                RoughnessFactor = pbr?.RoughnessFactor ?? 1f,
                MetallicRoughnessTexture = ToTextureRef(pbr?.MetallicRoughnessTexture),
                NormalTexture = ToTextureRef(m.NormalTexture),
                NormalScale = m.NormalTexture?.Scale ?? 1f,
                OcclusionTexture = ToTextureRef(m.OcclusionTexture),
                OcclusionStrength = m.OcclusionTexture?.Strength ?? 1f,
                EmissiveFactor = ToVec3(m.EmissiveFactor, Vector3.Zero),
                EmissiveTexture = ToTextureRef(m.EmissiveTexture),
                EmissiveStrength = ext?.EmissiveStrength?.EmissiveStrength ?? 1f,
                AlphaMode = m.AlphaMode switch
                {
                    "MASK" => AlphaMode.Mask,
                    "BLEND" => AlphaMode.Blend,
                    _ => AlphaMode.Opaque,
                },
                AlphaCutoff = m.AlphaCutoff ?? 0.5f,
                DoubleSided = m.DoubleSided == true,
                Unlit = ext?.Unlit is not null,

                SpecularFactor = ext?.Specular?.SpecularFactor ?? 1f,
                SpecularColorFactor = ToVec3(ext?.Specular?.SpecularColorFactor, Vector3.One),
                SpecularTexture = ToTextureRef(ext?.Specular?.SpecularTexture),
                SpecularColorTexture = ToTextureRef(ext?.Specular?.SpecularColorTexture),
                Ior = ext?.Ior?.Ior ?? 1.5f,

                TransmissionFactor = ext?.Transmission?.TransmissionFactor ?? 0f,
                TransmissionTexture = ToTextureRef(ext?.Transmission?.TransmissionTexture),

                ClearcoatFactor = ext?.Clearcoat?.ClearcoatFactor ?? 0f,
                ClearcoatRoughnessFactor = ext?.Clearcoat?.ClearcoatRoughnessFactor ?? 0f,
                ClearcoatTexture = ToTextureRef(ext?.Clearcoat?.ClearcoatTexture),
                ClearcoatRoughnessTexture = ToTextureRef(ext?.Clearcoat?.ClearcoatRoughnessTexture),
                ClearcoatNormalTexture = ToTextureRef(ext?.Clearcoat?.ClearcoatNormalTexture),

                SheenColorFactor = ToVec3(ext?.Sheen?.SheenColorFactor, Vector3.Zero),
                SheenRoughnessFactor = ext?.Sheen?.SheenRoughnessFactor ?? 1f,
                SheenColorTexture = ToTextureRef(ext?.Sheen?.SheenColorTexture),
                SheenRoughnessTexture = ToTextureRef(ext?.Sheen?.SheenRoughnessTexture),

                IsSpecularGlossiness = sg is not null,
                SgDiffuseFactor = ToVec4(sg?.DiffuseFactor, Vector4.One),
                SgDiffuseTexture = ToTextureRef(sg?.DiffuseTexture),
                SgSpecularFactor = ToVec3(sg?.SpecularFactor, Vector3.One),
                SgGlossinessFactor = sg?.GlossinessFactor ?? 1f,
                SgSpecularGlossinessTexture = ToTextureRef(sg?.SpecularGlossinessTexture),
            });
        }

        return result;
    }

    private static (List<RuntimeMesh> meshes, List<RuntimeNode> nodes, List<RuntimeLight> lights, IReadOnlyDictionary<int, Matrix4x4> nodeWorlds)
        BuildScene(GltfRoot root, BufferData buffers)
    {
        var meshes = new List<RuntimeMesh>();
        var meshList = root.Meshes ?? new List<GltfMesh>();
        foreach (var mesh in meshList)
        {
            var prims = new List<RuntimePrimitive>();
            foreach (var prim in mesh.Primitives ?? new List<GltfPrimitive>())
            {
                prims.Add(DecodePrimitive(root, buffers, prim));
            }

            meshes.Add(new RuntimeMesh { Name = mesh.Name, Primitives = prims });
        }

        // Lights: KHR_lights_punctual declares lights at the root; a node references one,
        // and the node's world transform gives the light its pose.
        var lights = new List<RuntimeLight>();
        var lightsByNode = new Dictionary<int, int>();
        var declaredLights = root.Extensions?.LightsPunctual?.Lights;
        if (declaredLights is not null)
        {
            var nodes = root.Nodes ?? new List<GltfNode>();
            for (var n = 0; n < nodes.Count; n++)
            {
                if (nodes[n].Extensions?.LightsPunctual is { } lref && lref.Light < declaredLights.Count)
                {
                    lightsByNode[n] = lights.Count;
                    var l = declaredLights[lref.Light];
                    lights.Add(new RuntimeLight
                    {
                        Name = l.Name,
                        Type = l.Type switch
                        {
                            "point" => LightType.Point,
                            "spot" => LightType.Spot,
                            _ => LightType.Directional,
                        },
                        Color = ToVec3(l.Color, Vector3.One),
                        Intensity = l.Intensity ?? 1f,
                        Range = l.Range ?? float.PositiveInfinity,
                        InnerConeAngle = l.Spot?.InnerConeAngle ?? 0f,
                        OuterConeAngle = l.Spot?.OuterConeAngle ?? MathF.PI / 4f,
                    });
                }
            }
        }

        var worldByNode = new Matrix4x4[root.Nodes?.Count ?? 0];
        var runtimeNodes = new List<RuntimeNode>();
        var nodeCount = worldByNode.Length;

        Matrix4x4 LocalOf(GltfNode node) =>
            node.Matrix is { Length: 16 } m
                ? GltfMath.FromGltfMatrix(m)
                : GltfMath.ComposeTrs(
                    ToVec3(node.Translation, Vector3.Zero),
                    ToQuat(node.Rotation),
                    ToVec3(node.Scale, Vector3.One));

        void WalkWorld(int index, Matrix4x4 parentWorld, bool[] visited)
        {
            if (visited[index])
            {
                return;
            }

            visited[index] = true;
            var node = root.Nodes![index];
            var world = GltfMath.Combine(LocalOf(node), parentWorld);
            worldByNode[index] = world;
            foreach (var child in node.Children ?? new List<int>())
            {
                WalkWorld(child, world, visited);
            }
        }

        // World transforms for the ENTIRE node graph, not just the default scene:
        // skin.joints may reference nodes unreachable from the scene roots.
        {
            var visited = new bool[nodeCount];
            var isChild = new bool[nodeCount];
            for (var i = 0; i < nodeCount; i++)
            {
                foreach (var child in root.Nodes![i].Children ?? new List<int>())
                {
                    if (child >= 0 && child < nodeCount)
                    {
                        isChild[child] = true;
                    }
                }
            }

            for (var i = 0; i < nodeCount; i++)
            {
                if (!isChild[i])
                {
                    WalkWorld(i, Matrix4x4.Identity, visited);
                }
            }

            for (var i = 0; i < nodeCount; i++)
            {
                if (!visited[i])
                {
                    // Orphaned (e.g. cycle) — treat as a graph root.
                    WalkWorld(i, Matrix4x4.Identity, visited);
                }
            }
        }

        var sceneIndex = root.Scene ?? 0;
        var scenes = root.Scenes;
        var roots = scenes is not null && scenes.Count > 0
            ? scenes[Math.Min(sceneIndex, scenes.Count - 1)].Nodes ?? new List<int>()
            : Enumerable.Range(0, nodeCount).ToList();

        void WalkScene(int index)
        {
            var node = root.Nodes![index];
            runtimeNodes.Add(new RuntimeNode
            {
                Name = node.Name,
                NodeIndex = index,
                World = worldByNode[index],
                MeshIndex = node.Mesh ?? -1,
                SkinIndex = node.Skin ?? -1,
                LightIndex = lightsByNode.TryGetValue(index, out var li) ? li : -1,
            });

            foreach (var child in node.Children ?? new List<int>())
            {
                WalkScene(child);
            }
        }

        foreach (var r in roots)
        {
            WalkScene(r);
        }

        // Fill in light poses from their nodes.
        foreach (var rn in runtimeNodes)
        {
            if (rn.LightIndex >= 0)
            {
                var light = lights[rn.LightIndex];
                lights[rn.LightIndex] = new RuntimeLight
                {
                    Name = light.Name,
                    Type = light.Type,
                    Color = light.Color,
                    Intensity = light.Intensity,
                    Range = light.Range,
                    InnerConeAngle = light.InnerConeAngle,
                    OuterConeAngle = light.OuterConeAngle,
                    Position = rn.World.Translation,
                    // glTF lights point down their node's -Z. Transform two points and subtract
                    // to get the direction independent of the matrix's translation; scale only
                    // affects length, which normalisation removes.
                    Direction = Vector3.Normalize(
                        Vector3.Transform(-Vector3.UnitZ, rn.World) - rn.World.Translation),
                };
            }
        }

        var nodeWorlds = new Dictionary<int, Matrix4x4>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            nodeWorlds[i] = worldByNode[i];
        }

        return (meshes, runtimeNodes, lights, nodeWorlds);
    }

    private static RuntimePrimitive DecodePrimitive(GltfRoot root, BufferData buffers, GltfPrimitive prim)
    {
        var attrs = prim.Attributes ?? new Dictionary<string, int>();
        if (!attrs.TryGetValue("POSITION", out var posAccessor))
        {
            throw new InvalidDataException("Primitive without POSITION is not valid glTF.");
        }

        var pos = buffers.ReadAccessorFloats(posAccessor);
        var positions = new Vector3[pos.Length / 3];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = new Vector3(pos[(i * 3) + 0], pos[(i * 3) + 1], pos[(i * 3) + 2]);
        }

        uint[] indices;
        if (prim.Indices is int idxAccessor)
        {
            indices = buffers.ReadAccessorIndices(idxAccessor);
        }
        else
        {
            // Non-indexed primitive: sequential indices.
            indices = new uint[positions.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = (uint)i;
            }
        }

        return new RuntimePrimitive
        {
            Positions = positions,
            Normals = ReadVec3s(buffers, attrs, "NORMAL"),
            Tangents = ReadVec4s(buffers, attrs, "TANGENT"),
            TexCoords0 = ReadVec2s(buffers, attrs, "TEXCOORD_0"),
            TexCoords1 = ReadVec2s(buffers, attrs, "TEXCOORD_1"),
            Colors = ReadColors(root, buffers, attrs),
            Joints = ReadVec4s(buffers, attrs, "JOINTS_0"),
            Weights = ReadVec4s(buffers, attrs, "WEIGHTS_0"),
            Indices = indices,
            MaterialIndex = prim.Material ?? -1,
            Mode = prim.Mode ?? 4,
        };
    }

    /// <summary>COLOR_0 may be VEC3 or VEC4; read whichever the accessor declares (alpha 1 for VEC3).</summary>
    private static Vector4[]? ReadColors(GltfRoot root, BufferData buffers, Dictionary<string, int> attrs)
    {
        if (!attrs.TryGetValue("COLOR_0", out var accessorIndex))
        {
            return null;
        }

        var accessor = root.Accessors![accessorIndex];
        var f = buffers.ReadAccessorFloats(accessorIndex);
        if (accessor.Type == "VEC3")
        {
            var result = new Vector4[f.Length / 3];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new Vector4(f[(i * 3) + 0], f[(i * 3) + 1], f[(i * 3) + 2], 1f);
            }

            return result;
        }

        var v4 = new Vector4[f.Length / 4];
        for (var i = 0; i < v4.Length; i++)
        {
            v4[i] = new Vector4(f[(i * 4) + 0], f[(i * 4) + 1], f[(i * 4) + 2], f[(i * 4) + 3]);
        }

        return v4;
    }

    private static Vector3[]? ReadVec3s(BufferData buffers, Dictionary<string, int> attrs, string name)
    {
        if (!attrs.TryGetValue(name, out var a))
        {
            return null;
        }

        var f = buffers.ReadAccessorFloats(a);
        var result = new Vector3[f.Length / 3];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Vector3(f[(i * 3) + 0], f[(i * 3) + 1], f[(i * 3) + 2]);
        }

        return result;
    }

    private static Vector2[]? ReadVec2s(BufferData buffers, Dictionary<string, int> attrs, string name)
    {
        if (!attrs.TryGetValue(name, out var a))
        {
            return null;
        }

        var f = buffers.ReadAccessorFloats(a);
        var result = new Vector2[f.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Vector2(f[(i * 2) + 0], f[(i * 2) + 1]);
        }

        return result;
    }

    private static Vector4[]? ReadVec4s(BufferData buffers, Dictionary<string, int> attrs, string name)
    {
        if (!attrs.TryGetValue(name, out var a))
        {
            return null;
        }

        var f = buffers.ReadAccessorFloats(a);
        var result = new Vector4[f.Length / 4];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Vector4(f[(i * 4) + 0], f[(i * 4) + 1], f[(i * 4) + 2], f[(i * 4) + 3]);
        }

        return result;
    }

    // --- small converters ------------------------------------------------

    private static TextureRef? ToTextureRef(GltfTextureInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        var tr = info.Extensions?.TextureTransform;
        var transform = tr is null
            ? UvTransform.Identity
            : new UvTransform
            {
                Offset = ToVec2(tr.Offset, Vector2.Zero),
                Rotation = tr.Rotation ?? 0f,
                Scale = ToVec2(tr.Scale, Vector2.One),
            };

        return new TextureRef
        {
            TextureIndex = info.Index,
            TexCoord = tr?.TexCoord ?? info.TexCoord ?? 0,
            Transform = transform,
        };
    }

    private static Vector2 ToVec2(float[]? v, Vector2 fallback) =>
        v is { Length: >= 2 } ? new Vector2(v[0], v[1]) : fallback;

    private static Vector3 ToVec3(float[]? v, Vector3 fallback) =>
        v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : fallback;

    private static Vector4 ToVec4(float[]? v, Vector4 fallback) =>
        v is { Length: >= 4 } ? new Vector4(v[0], v[1], v[2], v[3]) : fallback;

    private static Quaternion ToQuat(float[]? v) =>
        v is { Length: >= 4 } ? new Quaternion(v[0], v[1], v[2], v[3]) : Quaternion.Identity;
}
