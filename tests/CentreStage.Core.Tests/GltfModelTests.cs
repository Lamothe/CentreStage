namespace CentreStage.Core.Tests;

public class GltfModelTests
{
    [Fact]
    public void Load_ReadsMeshAndMaterial()
    {
        // One triangle, one material with transmission + clearcoat + texture transform.
        var positions = new byte[36];
        var posFloats = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        for (var i = 0; i < posFloats.Length; i++)
        {
            BitConverter.GetBytes(posFloats[i]).CopyTo(positions, i * 4);
        }
        var indices = new byte[] { 0, 0, 1, 0, 2, 0 };

        var manifest = new
        {
            asset = new { version = "2.0", generator = "centrestage-test" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0, translation = new[] { 5f, 0f, 0f } } },
            meshes = new[]
            {
                new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = 0 }, indices = 1, material = 0 } } },
            },
            accessors = new[]
            {
                new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3" },
                new { bufferView = 1, componentType = 5123, count = 3, type = "SCALAR" },
            },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 36 },
                new { buffer = 0, byteOffset = 36, byteLength = 6 },
            },
            buffers = new[] { new { byteLength = 42 } },
            materials = new[]
            {
                new
                {
                    name = "glassy",
                    pbrMetallicRoughness = new { baseColorFactor = new[] { 0.5f, 0.5f, 0.5f, 1f } },
                    doubleSided = true,
                    alphaMode = "BLEND",
                    extensions = new
                    {
                        KHR_materials_transmission = new { transmissionFactor = 0.75f },
                        KHR_materials_clearcoat = new { clearcoatFactor = 1f, clearcoatRoughnessFactor = 0.1f },
                        KHR_materials_ior = new { ior = 1.8f },
                    },
                },
            },
            extensionsUsed = new[] { "KHR_materials_transmission", "KHR_materials_clearcoat", "KHR_materials_ior" },
        };

        var bin = new byte[42];
        positions.CopyTo(bin, 0);
        indices.CopyTo(bin, 36);

        var model = GltfModel.Load(GlbBuilder.Build(manifest, bin));

        Assert.Equal("centrestage-test", model.Generator);
        Assert.Single(model.Meshes);
        var prim = model.Meshes[0].Primitives[0];
        Assert.Equal(3, prim.Positions.Length);
        Assert.Equal(new uint[] { 0, 1, 2 }, prim.Indices);

        Assert.Single(model.Nodes);
        Assert.Equal(new System.Numerics.Vector3(5, 0, 0), model.Nodes[0].World.Translation);

        var mat = model.Materials[0];
        Assert.Equal(0.75f, mat.TransmissionFactor);
        Assert.Equal(1f, mat.ClearcoatFactor);
        Assert.Equal(0.1f, mat.ClearcoatRoughnessFactor);
        Assert.Equal(1.8f, mat.Ior);
        Assert.True(mat.DoubleSided);
        Assert.Equal(AlphaMode.Blend, mat.AlphaMode);
    }

    [Fact]
    public void Load_PreservesSpecularGlossiness()
    {
        var manifest = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 4 } },
            materials = new[]
            {
                new
                {
                    extensions = new
                    {
                        KHR_materials_pbrSpecularGlossiness = new
                        {
                            diffuseFactor = new[] { 1f, 0f, 0f, 1f },
                            specularFactor = new[] { 0.2f, 0.3f, 0.4f },
                            glossinessFactor = 0.8f,
                        },
                    },
                },
            },
            extensionsUsed = new[] { "KHR_materials_pbrSpecularGlossiness" },
            extensionsRequired = new[] { "KHR_materials_pbrSpecularGlossiness" },
        };

        var model = GltfModel.Load(GlbBuilder.Build(manifest, new byte[4]));

        var mat = model.Materials[0];
        Assert.True(mat.IsSpecularGlossiness);
        Assert.Equal(0.8f, mat.SgGlossinessFactor);
        Assert.Equal(new System.Numerics.Vector3(0.2f, 0.3f, 0.4f), mat.SgSpecularFactor);
    }

    [Fact]
    public void Load_ReadsTextureTransform()
    {
        var manifest = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 4 } },
            images = new[] { new { bufferView = 0, mimeType = "image/png" } },
            textures = new[] { new { source = 0 } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 4 } },
            materials = new[]
            {
                new
                {
                    pbrMetallicRoughness = new
                    {
                        baseColorTexture = new
                        {
                            index = 0,
                            extensions = new
                            {
                                KHR_texture_transform = new
                                {
                                    offset = new[] { 0.5f, 0.25f },
                                    rotation = 1.5707964f,
                                    scale = new[] { 2f, 2f },
                                },
                            },
                        },
                    },
                },
            },
            extensionsUsed = new[] { "KHR_texture_transform" },
            extensionsRequired = new[] { "KHR_texture_transform" },
        };

        var model = GltfModel.Load(GlbBuilder.Build(manifest, new byte[4]));

        var mat = model.Materials[0];
        var texRef = Assert.IsType<TextureRef>(mat.BaseColorTexture);
        Assert.Equal(2f, texRef.Transform.Scale.X);
        // 90° rotation: (1,0) scaled (2,0) rotates to (0,2), plus offset (0.5, 0.25).
        var uv = texRef.Transform.Apply(new System.Numerics.Vector2(1, 0));
        Assert.True(System.Numerics.Vector2.Distance(new(0.5f, 2.25f), uv) < 1e-4f, $"got {uv}");
    }

    [Fact]
    public void Load_RefusesStructuralRequiredExtension()
    {
        var manifest = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 4 } },
            extensionsUsed = new[] { "KHR_draco_mesh_compression" },
            extensionsRequired = new[] { "KHR_draco_mesh_compression" },
        };

        var ex = Assert.Throws<UnsupportedExtensionsException>(
            () => GltfModel.Load(GlbBuilder.Build(manifest, new byte[4])));
        Assert.Contains("KHR_draco_mesh_compression", ex.Extensions);
    }

    [Fact]
    public void Load_LoadsButReportsNonStructuralRequiredExtension()
    {
        // E.g. KHR_xmp_json_ld metadata: required by the file, irrelevant to rendering.
        var manifest = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 4 } },
            extensionsUsed = new[] { "KHR_xmp_json_ld" },
            extensionsRequired = new[] { "KHR_xmp_json_ld" },
        };

        var model = GltfModel.Load(GlbBuilder.Build(manifest, new byte[4]));

        Assert.Contains("KHR_xmp_json_ld", model.IgnoredExtensions);
    }

    [Fact]
    public void Load_RejectsGltf1()
    {
        var manifest = new
        {
            asset = new { version = "1.0" },
            buffers = new[] { new { byteLength = 4 } },
        };

        Assert.Throws<InvalidDataException>(() => GltfModel.Load(GlbBuilder.Build(manifest, new byte[4])));
    }
}
