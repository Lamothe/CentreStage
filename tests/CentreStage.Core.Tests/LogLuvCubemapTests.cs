using System.Buffers.Binary;
using System.IO.Compression;
using CentreStage.Core;

namespace CentreStage.Core.Tests;

public sealed class LogLuvCubemapTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1025)]
    [InlineData(2048)]
    [InlineData(int.MaxValue)]
    public void InvalidDimensionsAreRejectedBeforeOpeningFile(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogLuvCubemap.Load("not-a-file", size));
    }

    [Fact]
    public void DefaultLayoutDecodesFaceLocalChannelPlanes()
    {
        var payload = new List<byte>();
        for (var size = 2; size > 0; size >>= 1)
        {
            for (var face = 0; face < 6; face++)
            {
                foreach (var value in new byte[] { 61, 199, (byte)(127 + face), 0 })
                {
                    payload.AddRange(Enumerable.Repeat(value, size * size));
                }
            }
        }
        WithGzip(payload.ToArray(), path =>
        {
            var map = LogLuvCubemap.Load(path);
            foreach (var level in map.Levels)
            {
                for (var face = 0; face < 6; face++)
                {
                    var expected = ReferenceDecode(61, 199, (byte)(127 + face), 0);
                    AssertRgb(expected, level.Rgb.AsSpan(face * level.Size * level.Size * 3, 3).ToArray());
                }
            }
        });
    }

    [Fact]
    public void FullChainPreservesMipFaceAndPixelOrder()
    {
        var payload = new List<byte>();
        for (var size = 4; size > 0; size >>= 1)
        {
            for (var face = 0; face < 6; face++)
            {
                for (var pixel = 0; pixel < size * size; pixel++)
                {
                    payload.AddRange(new byte[] { (byte)(35 + face * 4 + pixel), 170, (byte)(120 + size), (byte)(pixel * 11) });
                }
            }
        }

        WithGzip(payload.ToArray(), path =>
        {
            var cubemap = LogLuvCubemap.Load(path, 4, planar: false);
            Assert.Equal(new[] { 4, 2, 1 }, cubemap.Levels.Select(level => level.Size));
            var offset = 0;
            foreach (var level in cubemap.Levels)
            {
                Assert.Equal(6 * level.Size * level.Size * 3, level.Rgb.Length);
                for (var pixel = 0; pixel < level.Rgb.Length / 3; pixel++)
                {
                    var expected = ReferenceDecode(payload[offset], payload[offset + 1], payload[offset + 2], payload[offset + 3]);
                    AssertRgb(expected, level.Rgb.AsSpan(pixel * 3, 3).ToArray());
                    offset += 4;
                }
            }
            Assert.Equal(payload.Count, offset);
        });
    }

    [Fact]
    public void DecodesBlackNeutralHdrAndClampsNegativeChannels()
    {
        // Near-neutral chromaticity quantised to bytes; B=127 gives Y=1.
        byte[] payload = [255, 0, 255, 255, 61, 199, 127, 0, 61, 199, 147, 255,
            0, 255, 127, 0, 255, 1, 255, 255, 0, 1, 0, 0];
        WithGzip(payload, path =>
        {
            var rgb = LogLuvCubemap.Load(path, 1, planar: false).Levels[0].Rgb;
            AssertRgb(new[] { 0.0, 0.0, 0.0 }, rgb[..3]);
            Assert.InRange(rgb[3], 0.85f, 0.95f);
            Assert.InRange(rgb[4], 0.85f, 0.95f);
            Assert.InRange(rgb[5], 0.85f, 0.95f);
            Assert.True(rgb[6] > 1000.0f);
            Assert.Equal(0.0f, rgb[9]);
            Assert.Equal(0.0f, rgb[11]);
            for (var pixel = 0; pixel < 6; pixel++)
            {
                AssertRgb(ReferenceDecode(payload[pixel * 4], payload[pixel * 4 + 1], payload[pixel * 4 + 2], payload[pixel * 4 + 3]), rgb.AsSpan(pixel * 3, 3).ToArray());
            }
            Assert.All(rgb, value => Assert.True(float.IsFinite(value) && value >= 0.0f));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(25)]
    [InlineData(4096)]
    public void IncorrectDecodedByteCountsAreRejected(int length)
    {
        WithGzip(new byte[length], path => Assert.Throws<InvalidDataException>(() => LogLuvCubemap.Load(path, 1)));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(25)]
    [InlineData(1000000)]
    public void ForgedTrailerCannotBypassBoundedPayloadRead(int length)
    {
        WithGzip(new byte[length], path =>
        {
            var compressed = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(compressed.Length - 4), 24);
            File.WriteAllBytes(path, compressed);
            Assert.Throws<InvalidDataException>(() => LogLuvCubemap.Load(path, 1));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void TruncatedGzipIsRejected(int removedBytes)
    {
        WithGzip(new byte[24], path =>
        {
            var compressed = File.ReadAllBytes(path);
            File.WriteAllBytes(path, compressed[..^removedBytes]);
            Assert.Throws<InvalidDataException>(() => LogLuvCubemap.Load(path, 1));
        });
    }

    [Fact]
    public void InvalidHeaderAndChecksumAreRejected()
    {
        WithGzip(new byte[24], path =>
        {
            var original = File.ReadAllBytes(path);
            var invalid = (byte[])original.Clone();
            invalid[0] = 0;
            File.WriteAllBytes(path, invalid);
            Assert.Throws<InvalidDataException>(() => LogLuvCubemap.Load(path, 1));
            invalid = (byte[])original.Clone();
            invalid[^8] ^= 0xff;
            File.WriteAllBytes(path, invalid);
            Assert.Throws<InvalidDataException>(() => LogLuvCubemap.Load(path, 1));
        });
    }

    [Fact]
    public void MaximumDimensionIsAcceptedWithoutAllocatingForMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() => LogLuvCubemap.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gz"), 1024));
    }

    private static double[] ReferenceDecode(byte r, byte g, byte b, byte a)
    {
        if (g == 0)
        {
            return [0.0, 0.0, 0.0];
        }
        var luminance = Math.Pow(2.0, (b + a / 255.0 - 127.0) * 0.5);
        // Algebraically factored reference, independent of the production matrix evaluation.
        var channels = new[]
        {
            luminance * ((6.0013 * r + 0.3007 * 255.0) / g - 1.332),
            luminance * ((-2.7 * r - 1.088 * 255.0) / g + 3.1029),
            luminance * ((-1.7995 * r + 5.6268 * 255.0) / g - 5.772),
        };
        return channels.Select(value => Math.Max(0.0, value)).ToArray();
    }

    private static void AssertRgb(double[] expected, float[] actual)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            Assert.InRange(Math.Abs(actual[channel] - expected[channel]), 0.0, Math.Max(1e-25, Math.Abs(expected[channel]) * 1e-6));
        }
    }

    private static void WithGzip(byte[] payload, Action<string> assertion)
    {
        var path = Path.Combine(Path.GetTempPath(), $"centrestage-logluv-{Guid.NewGuid():N}.gz");
        try
        {
            using (var file = File.Create(path))
            {
                using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
                gzip.Write(payload);
            }
            assertion(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
