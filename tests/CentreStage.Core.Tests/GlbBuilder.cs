using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace CentreStage.Core.Tests;

/// <summary>Builds small GLB files in memory for parser tests.</summary>
internal static class GlbBuilder
{
    public static byte[] Build(object manifest, byte[]? binChunk = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var jsonPadded = Pad(json, 0x20); // JSON chunk pads with spaces
        var binPadded = binChunk is null ? Array.Empty<byte>() : Pad(binChunk, 0x00);

        var hasBin = binChunk is not null;
        var total = 12 + 8 + jsonPadded.Length + (hasBin ? 8 + binPadded.Length : 0);

        var buffer = new byte[total];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x46546C67); // 'glTF'
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);

        var offset = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)jsonPadded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 0x4E4F534A); // 'JSON'
        jsonPadded.CopyTo(span[(offset + 8)..]);
        offset += 8 + jsonPadded.Length;

        if (hasBin)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)binPadded.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 0x004E4942); // 'BIN\0'
            binPadded.CopyTo(span[(offset + 8)..]);
        }

        return buffer;
    }

    private static byte[] Pad(byte[] data, byte fill)
    {
        var paddedLength = (data.Length + 3) & ~3;
        if (paddedLength == data.Length)
        {
            return data;
        }

        var result = new byte[paddedLength];
        data.CopyTo(result, 0);
        result.AsSpan(data.Length).Fill(fill);
        return result;
    }

    /// <summary>A minimal manifest with one buffer of the given byte length.</summary>
    public static object Manifest(int bufferBytes, object? extra = null) => new
    {
        asset = new { version = "2.0" },
        buffers = new[] { new { byteLength = bufferBytes } },
    };
}
