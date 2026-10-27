using System.Text.Json;

namespace CentreStage.Core.Tests;

public class BufferDataTests
{
    private static (BufferData buffers, GltfModel model) Build(object manifest, byte[] bin)
    {
        var glb = GlbBuilder.Build(manifest, bin);
        var model = GltfModel.Load(glb);
        return (model.Buffers, model);
    }

    private static object ManifestWithAccessor(object accessor, int bufferBytes, object? bufferView = null) => new
    {
        asset = new { version = "2.0" },
        buffers = new[] { new { byteLength = bufferBytes } },
        bufferViews = new[] { bufferView ?? new { buffer = 0, byteOffset = 0, byteLength = bufferBytes } },
        accessors = new[] { accessor },
    };

    [Fact]
    public void ReadAccessorFloats_ReadsTightlyPackedVec3()
    {
        var bin = new byte[36];
        for (var i = 0; i < 9; i++)
        {
            BitConverter.GetBytes((float)(i + 1)).CopyTo(bin, i * 4);
        }

        var (buffers, _) = Build(
            ManifestWithAccessor(new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3" }, 36),
            bin);

        var result = buffers.ReadAccessorFloats(0);

        Assert.Equal(9, result.Length);
        Assert.Equal(1f, result[0]);
        Assert.Equal(9f, result[8]);
    }

    [Fact]
    public void ReadAccessorFloats_HonoursInterleavedStride()
    {
        // 3 vertices, each 16 bytes: VEC3 float (12) + 4 bytes padding.
        var bin = new byte[48];
        for (var i = 0; i < 3; i++)
        {
            BitConverter.GetBytes(10f + i).CopyTo(bin, (i * 16) + 0);
            BitConverter.GetBytes(20f + i).CopyTo(bin, (i * 16) + 4);
            BitConverter.GetBytes(30f + i).CopyTo(bin, (i * 16) + 8);
        }

        var (buffers, _) = Build(
            ManifestWithAccessor(
                new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3" },
                48,
                new { buffer = 0, byteOffset = 0, byteLength = 48, byteStride = 16 }),
            bin);

        var result = buffers.ReadAccessorFloats(0);

        Assert.Equal(new[] { 10f, 20f, 30f, 11f, 21f, 31f, 12f, 22f, 32f }, result);
    }

    [Fact]
    public void ReadAccessorFloats_NormalisesUnsignedBytes()
    {
        var bin = new byte[] { 0, 255, 128 };

        var (buffers, _) = Build(
            ManifestWithAccessor(new { bufferView = 0, componentType = 5121, count = 3, type = "SCALAR", normalized = true }, 3),
            bin);

        var result = buffers.ReadAccessorFloats(0);

        Assert.Equal(0f, result[0]);
        Assert.Equal(1f, result[1]);
        Assert.Equal(128f / 255f, result[2], 5);
    }

    [Fact]
    public void ReadAccessorIndices_ReadsUnsignedShort()
    {
        var bin = new byte[] { 1, 0, 2, 0, 3, 0 };

        var (buffers, _) = Build(
            ManifestWithAccessor(new { bufferView = 0, componentType = 5123, count = 3, type = "SCALAR" }, 6),
            bin);

        var result = buffers.ReadAccessorIndices(0);

        Assert.Equal(new uint[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void ReadAccessorFloats_AppliesSparseOverrides()
    {
        // Base bufferView: 3 scalars at 0.0 (float). Sparse values override #1 with 42.
        var bin = new byte[12 + 8];
        // sparse indices at offset 12: one ushort = 1
        bin[12] = 1;
        // sparse values at offset 16: one float = 42
        BitConverter.GetBytes(42f).CopyTo(bin, 16);

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
                    componentType = 5126,
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

        var (buffers, _) = Build(manifest, bin);

        var result = buffers.ReadAccessorFloats(0);

        Assert.Equal(new[] { 0f, 42f, 0f }, result);
    }

    [Fact]
    public void ReadAccessorFloats_SparseWithoutBaseIsZeroFilled()
    {
        var bin = new byte[8];
        bin[0] = 0; // index 0
        BitConverter.GetBytes(7f).CopyTo(bin, 4);

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
                    componentType = 5126,
                    count = 2,
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

        var (buffers, _) = Build(manifest, bin);

        var result = buffers.ReadAccessorFloats(0);

        Assert.Equal(new[] { 7f, 0f }, result);
    }
}
