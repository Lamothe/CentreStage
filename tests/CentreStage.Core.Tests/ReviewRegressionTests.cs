namespace CentreStage.Core.Tests;

public class ReviewRegressionTests
{
    [Fact]
    public void ReadAccessorIndices_AppliesSparseOverrides()
    {
        // Base indices: [0, 0, 0]. Sparse overrides index 1 -> 7.
        var bin = new byte[20];
        // sparse indices at offset 12: one ushort = 1
        bin[12] = 1;
        // sparse values at offset 16: one uint = 7
        BitConverter.GetBytes(7u).CopyTo(bin, 16);

        var manifest = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 20 } },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 12 },
                new { buffer = 0, byteOffset = 12, byteLength = 2 },
                new { buffer = 0, byteOffset = 16, byteLength = 4 },
            },
            accessors = new[]
            {
                new
                {
                    bufferView = 0,
                    componentType = 5125,
                    count = 3,
                    type = "SCALAR",
                    sparse = new
                    {
                        count = 1,
                        indices = new { bufferView = 1, componentType = 5123 },
                        values = new { bufferView = 2 },
                    },
                },
            },
        };

        var model = GltfModel.Load(GlbBuilder.Build(manifest, bin));

        var result = model.Buffers.ReadAccessorIndices(0);
        Assert.Equal(new uint[] { 0, 7, 0 }, result);
    }

    [Fact]
    public void ReadAccessor_ThrowsOnOverrun()
    {
        // Accessor claims 100 floats but the bufferView holds 3.
        var manifest = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 12 } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 12 } },
            accessors = new[] { new { bufferView = 0, componentType = 5126, count = 100, type = "SCALAR" } },
        };

        var model = GltfModel.Load(GlbBuilder.Build(manifest, new byte[12]));

        Assert.Throws<InvalidDataException>(() => model.Buffers.ReadAccessorFloats(0));
    }

    [Fact]
    public void Load_ReadsVec3Colors()
    {
        var bin = new byte[9];
        // One VEC3 colour: (1, 0, 0.5) as floats would be 12 bytes; use normalized ubytes.
        bin[0] = 255;
        bin[1] = 0;
        bin[2] = 128;

        var manifest = new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0 } },
            meshes = new[]
            {
                new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = 0, ["COLOR_0"] = 1 } } } },
            },
            accessors = new[]
            {
                new { bufferView = 0, componentType = 5126, normalized = false, count = 1, type = "VEC3" },
                new { bufferView = 1, componentType = 5121, normalized = true, count = 1, type = "VEC3" },
            },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 12 },
                new { buffer = 0, byteOffset = 12, byteLength = 3 },
            },
            buffers = new[] { new { byteLength = 15 } },
        };

        var fullBin = new byte[15];
        BitConverter.GetBytes(1f).CopyTo(fullBin, 0);
        BitConverter.GetBytes(2f).CopyTo(fullBin, 4);
        BitConverter.GetBytes(3f).CopyTo(fullBin, 8);
        fullBin[12] = 255;
        fullBin[13] = 0;
        fullBin[14] = 128;

        var model = GltfModel.Load(GlbBuilder.Build(manifest, fullBin));

        var colors = model.Meshes[0].Primitives[0].Colors;
        Assert.NotNull(colors);
        Assert.Single(colors!);
        Assert.Equal(1f, colors![0].X);
        Assert.Equal(0f, colors[0].Y);
        Assert.Equal(128f / 255f, colors[0].Z, 5);
        Assert.Equal(1f, colors[0].W); // VEC3 colours get alpha 1
    }

    [Fact]
    public void NodeWorlds_CoversNodesOutsideDefaultScene()
    {
        // Two graph roots: node 0 is in the scene, node 1 is not (but must still get
        // a world transform, because skins may reference it).
        var manifest = new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[]
            {
                new { mesh = 0, translation = new[] { 1f, 0f, 0f }, children = Array.Empty<int>() },
                new { mesh = -1, translation = new[] { 0f, 5f, 0f }, children = new[] { 2 } },
                new { mesh = -1, translation = new[] { 0f, 1f, 0f }, children = Array.Empty<int>() },
            },
            meshes = new[] { new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = 0 } } } } },
            accessors = new[] { new { bufferView = 0, componentType = 5126, count = 1, type = "VEC3" } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 12 } },
            buffers = new[] { new { byteLength = 12 } },
        };

        var model = GltfModel.Load(GlbBuilder.Build(manifest, new byte[12]));

        Assert.Equal(3, model.NodeWorlds.Count);
        Assert.Equal(new System.Numerics.Vector3(1, 0, 0), model.NodeWorlds[0].Translation);
        Assert.Equal(new System.Numerics.Vector3(0, 5, 0), model.NodeWorlds[1].Translation);
        // Child world = local + parent.
        Assert.Equal(new System.Numerics.Vector3(0, 6, 0), model.NodeWorlds[2].Translation);
        // The scene contains only node 0.
        Assert.Single(model.Nodes);
    }

    [Fact]
    public void ReadAccessorIndices_SparseOverrideOutOfBounds_Throws()
    {
        var bin = new byte[8];
        bin[0] = 9; // target index 9, but accessor count is 3
        BitConverter.GetBytes(7u).CopyTo(bin, 4);

        var manifest = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 8 } },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 2 },
                new { buffer = 0, byteOffset = 4, byteLength = 4 },
            },
            accessors = new[]
            {
                new
                {
                    componentType = 5125,
                    count = 3,
                    type = "SCALAR",
                    sparse = new
                    {
                        count = 1,
                        indices = new { bufferView = 0, componentType = 5123 },
                        values = new { bufferView = 1 },
                    },
                },
            },
        };

        var model = GltfModel.Load(GlbBuilder.Build(manifest, bin));

        Assert.Throws<InvalidDataException>(() => model.Buffers.ReadAccessorIndices(0));
    }
}
