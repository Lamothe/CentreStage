using System.Buffers.Binary;
using System.Text.Json;

namespace CentreStage.Core.Glb;

/// <summary>
/// Parsed GLB container: 12-byte header plus chunks. The first chunk is the JSON manifest;
/// an optional second chunk ("BIN\0") holds the bulk binary data referenced by bufferViews.
/// </summary>
public sealed class GlbFile
{
    private const uint Magic = 0x46546C67; // 'glTF'
    private const uint JsonChunkType = 0x4E4F534A; // 'JSON'
    private const uint BinChunkType = 0x004E4942; // 'BIN\0'

    public uint Version { get; }
    public JsonElement Manifest { get; }
    public ReadOnlyMemory<byte> BinChunk { get; }

    private GlbFile(uint version, JsonElement manifest, ReadOnlyMemory<byte> binChunk)
    {
        Version = version;
        Manifest = manifest;
        BinChunk = binChunk;
    }

    /// <summary>True when the data starts with the GLB magic number.</summary>
    public static bool IsGlb(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic;

    public static GlbFile Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (span.Length < 12)
        {
            throw new InvalidDataException("GLB too short for a 12-byte header.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
        {
            throw new InvalidDataException("Not a GLB file (bad magic number).");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != 2)
        {
            throw new InvalidDataException($"Unsupported GLB version {version}; only version 2 is defined.");
        }

        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        if (totalLength > span.Length)
        {
            throw new InvalidDataException($"GLB declares {totalLength} bytes but only {span.Length} are present.");
        }

        JsonElement manifest = default;
        ReadOnlyMemory<byte> binChunk = default;
        var haveManifest = false;
        long offset = 12;

        while (offset + 8 <= totalLength)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)offset..]);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(span[((int)offset + 4)..]);
            var dataStart = offset + 8;
            if (dataStart + chunkLength > totalLength)
            {
                throw new InvalidDataException("GLB chunk length overruns the declared file length.");
            }

            if (chunkType == JsonChunkType && !haveManifest)
            {
                using var doc = JsonDocument.Parse(data.Slice((int)dataStart, (int)chunkLength));
                manifest = doc.RootElement.Clone();
                haveManifest = true;
            }
            else if (chunkType == BinChunkType && binChunk.IsEmpty)
            {
                binChunk = data.Slice((int)dataStart, (int)chunkLength);
            }

            // Chunk data is padded to 4-byte boundaries with the pad counted in the next
            // chunk header's alignment, not in chunkLength; step up to the aligned offset.
            offset = dataStart + ((chunkLength + 3) & ~3u);
        }

        if (!haveManifest)
        {
            throw new InvalidDataException("GLB contains no JSON chunk.");
        }

        return new GlbFile(version, manifest, binChunk);
    }
}
