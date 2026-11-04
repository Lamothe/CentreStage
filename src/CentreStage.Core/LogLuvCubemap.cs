using System.Buffers.Binary;
using System.IO.Compression;

namespace CentreStage.Core;

/// <summary>A full osgjs LogLuv mip chain, largest first, with six consecutive faces per level.</summary>
public sealed class LogLuvCubemap
{
    public IReadOnlyList<LogLuvCubemapLevel> Levels { get; }

    private LogLuvCubemap(List<LogLuvCubemapLevel> levels)
    {
        Levels = levels.AsReadOnly();
    }

    /// <summary>Infers dimensions from the gzip trailer, then validates the full payload.</summary>
    public static LogLuvCubemap Load(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 18)
        {
            throw new InvalidDataException("The gzip cubemap is truncated.");
        }
        file.Seek(-4, SeekOrigin.End);
        Span<byte> trailer = stackalloc byte[4];
        file.ReadExactly(trailer);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        for (var size = 1; size <= 1024; size *= 2)
        {
            var count = 0;
            for (var mip = size; mip > 0; mip >>= 1)
            {
                count += mip * mip * 24;
            }
            if (length == count)
            {
                return Load(path, size);
            }
        }
        throw new InvalidDataException("Unsupported full-mip LogLuv cubemap dimensions.");
    }

    /// <summary>
    /// Loads a single gzip member containing face-local planar RGBA bytes by default (or explicitly interleaved RGBA).
    /// Face order and pixel order are preserved; RGB values are linear, non-negative and finite.
    /// </summary>
    public static LogLuvCubemap Load(string path, int size, bool planar = true)
    {
        if (size < 1 || size > 1024 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Cubemap size must be a power of two from 1 to 1024.");
        }

        var expectedBytes = 0;
        for (var levelSize = size; levelSize > 0; levelSize >>= 1)
        {
            expectedBytes = checked(expectedBytes + levelSize * levelSize * 6 * 4);
        }

        using var file = File.OpenRead(path);
        // GZipStream can accept a missing trailer. Check ISIZE as well as reading to EOF,
        // so a truncated gzip with an otherwise complete decoded payload is rejected too.
        if (file.Length < 18)
        {
            throw new InvalidDataException("The gzip cubemap is truncated.");
        }
        file.Seek(-4, SeekOrigin.End);
        Span<byte> trailerSize = stackalloc byte[4];
        file.ReadExactly(trailerSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailerSize) != (uint)expectedBytes)
        {
            throw new InvalidDataException("The gzip cubemap byte count does not match its dimensions.");
        }
        file.Position = 0;

        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var levels = new List<LogLuvCubemapLevel>();
        try
        {
            for (var levelSize = size; levelSize > 0; levelSize >>= 1)
            {
                var pixelCount = levelSize * levelSize * 6;
                var encoded = new byte[pixelCount * 4];
                gzip.ReadExactly(encoded);
                var rgb = new float[pixelCount * 3];
                for (var pixel = 0; pixel < pixelCount; pixel++)
                {
                    var facePixels = levelSize * levelSize;
                    var faceStart = pixel / facePixels * facePixels * 4;
                    var local = pixel % facePixels;
                    var source = planar ? faceStart + local : pixel * 4;
                    var stride = planar ? facePixels : 1;
                    var target = pixel * 3;
                    if (encoded[source + stride] == 0)
                    {
                        continue;
                    }

                    var r = encoded[source] / 255.0;
                    var g = encoded[source + stride] / 255.0;
                    var b = encoded[source + stride * 2] / 255.0;
                    var a = encoded[source + stride * 3] / 255.0;
                    var le = b * 255.0 + a;
                    var y = Math.Pow(2.0, (le - 127.0) / 2.0);
                    var z = y / g;
                    var x = r * z;
                    rgb[target] = Clamp(6.0013 * x - 1.332 * y + 0.3007 * z);
                    rgb[target + 1] = Clamp(-2.7 * x + 3.1029 * y - 1.088 * z);
                    rgb[target + 2] = Clamp(-1.7995 * x - 5.772 * y + 5.6268 * z);
                }
                levels.Add(new LogLuvCubemapLevel(levelSize, rgb));
            }

            // Never copy an unbounded decompression stream: consume only the expected
            // payload plus one byte to detect excess data, including extra gzip members.
            if (gzip.ReadByte() != -1)
            {
                throw new InvalidDataException("The gzip cubemap contains excess decoded data.");
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The gzip cubemap mip chain is truncated.", exception);
        }

        return new LogLuvCubemap(levels);
    }

    private static float Clamp(double value)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            return 0.0f;
        }
        return (float)Math.Min(value, float.MaxValue);
    }
}

/// <summary>Six consecutive faces of Size × Size linear RGB pixels.</summary>
public sealed record LogLuvCubemapLevel(int Size, float[] Rgb);
