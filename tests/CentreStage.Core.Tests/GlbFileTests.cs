using CentreStage.Core.Glb;

namespace CentreStage.Core.Tests;

public class GlbFileTests
{
    [Fact]
    public void Parse_ReadsHeaderAndManifest()
    {
        var glb = GlbBuilder.Build(new { asset = new { version = "2.0", generator = "test" } });

        var parsed = GlbFile.Parse(glb);

        Assert.Equal(2u, parsed.Version);
        Assert.Equal("test", parsed.Manifest.GetProperty("asset").GetProperty("generator").GetString());
    }

    [Fact]
    public void Parse_ReadsBinChunk()
    {
        var bin = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var glb = GlbBuilder.Build(GlbBuilder.Manifest(bin.Length), bin);

        var parsed = GlbFile.Parse(glb);

        Assert.Equal(bin, parsed.BinChunk.ToArray());
    }

    [Fact]
    public void Parse_PadsJsonChunkWithSpaces()
    {
        // Manifest length 56 is already 4-aligned; 57 needs padding.
        var glb = GlbBuilder.Build(new { asset = new { version = "2.0", x = 1 } });

        var parsed = GlbFile.Parse(glb);

        Assert.True(GlbFile.IsGlb(glb));
        Assert.Equal(2u, parsed.Version);
    }

    [Fact]
    public void Parse_RejectsBadMagic()
    {
        var glb = GlbBuilder.Build(new { asset = new { version = "2.0" } });
        glb[0] = 0x00;

        Assert.Throws<InvalidDataException>(() => GlbFile.Parse(glb));
    }

    [Fact]
    public void Parse_RejectsTruncatedFile()
    {
        var glb = GlbBuilder.Build(new { asset = new { version = "2.0" } });

        Assert.Throws<InvalidDataException>(() => GlbFile.Parse(glb.AsSpan(0, 8).ToArray()));
    }

    [Fact]
    public void Parse_RejectsVersion1()
    {
        var glb = GlbBuilder.Build(new { asset = new { version = "2.0" } });
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 1);

        Assert.Throws<InvalidDataException>(() => GlbFile.Parse(glb));
    }
}
