using CentreStage.Core.Schema;

namespace CentreStage.Core;

/// <summary>
/// The glTF binary data model: buffers resolved to bytes, and accessors decoded from them.
///
/// The chain is buffer -> bufferView -> accessor:
///   buffer:      a blob of bytes (the GLB BIN chunk, or an external/data-URI buffer).
///   bufferView:  a named slice of a buffer, optionally interleaved (byteStride).
///   accessor:    a typed view over a bufferView: componentType, element type (VEC3 etc),
///                count, alignment and normalisation, plus optional sparse overrides.
/// </summary>
public sealed class BufferData
{
    private readonly List<byte[]> _buffers = new();
    private readonly GltfRoot _root;

    internal BufferData(GltfRoot root, IReadOnlyList<byte[]> buffers)
    {
        _root = root;
        _buffers.AddRange(buffers);
    }

    public int BufferCount => _buffers.Count;

    public ReadOnlySpan<byte> GetBuffer(int index) => _buffers[index];

    /// <summary>Resolves a bufferView to its raw bytes (unstrided slice of the parent buffer).</summary>
    public ReadOnlySpan<byte> GetBufferView(int index)
    {
        var views = _root.BufferViews ?? throw new InvalidDataException($"No bufferViews in file (wanted #{index}).");
        var view = views[index];
        var buffer = _buffers[view.Buffer];
        var offset = view.ByteOffset ?? 0;
        if (offset + view.ByteLength > buffer.Length)
        {
            throw new InvalidDataException($"bufferView #{index} overruns its buffer.");
        }

        return buffer.AsSpan(offset, view.ByteLength);
    }

    /// <summary>Interleaved stride in bytes, or 0 when elements are tightly packed.</summary>
    public int GetStride(int bufferViewIndex)
    {
        var views = _root.BufferViews!;
        return views[bufferViewIndex].ByteStride ?? 0;
    }

    // --- Accessor decoding ---------------------------------------------

    public enum ComponentType
    {
        Byte = 5120,
        UnsignedByte = 5121,
        Short = 5122,
        UnsignedShort = 5123,
        UnsignedInt = 5125,
        Float = 5126,
    }

    /// <summary>Number of components per element for an accessor type string.</summary>
    public static int TypeCardinality(string? type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => throw new InvalidDataException($"Unknown accessor type '{type}'."),
    };

    public static int ComponentSize(ComponentType type) => type switch
    {
        ComponentType.Byte or ComponentType.UnsignedByte => 1,
        ComponentType.Short or ComponentType.UnsignedShort => 2,
        ComponentType.UnsignedInt or ComponentType.Float => 4,
        _ => throw new InvalidDataException($"Unknown componentType {(int)type}."),
    };

    /// <summary>
    /// Decodes an accessor to floats. Integer components are normalised per the spec when
    /// the accessor is marked normalised (e.g. UNSIGNED_BYTE 255 -> 1.0), otherwise scaled
    /// to their raw numeric value.
    /// </summary>
    public float[] ReadAccessorFloats(int accessorIndex)
    {
        var accessor = GetAccessor(accessorIndex);
        var cardinality = TypeCardinality(accessor.Type);
        var result = new float[accessor.Count * cardinality];

        if (accessor.BufferView is int bv)
        {
            var componentType = (ComponentType)accessor.ComponentType;
            var compSize = ComponentSize(componentType);
            var elementSize = compSize * cardinality;
            var stride = GetStride(bv);
            if (stride == 0)
            {
                stride = elementSize;
            }

            var bytes = GetBufferView(bv);
            var offset = accessor.ByteOffset ?? 0;
            CheckRange(bv, offset, accessor.Count, stride, compSize, accessorIndex);
            for (var i = 0; i < accessor.Count; i++)
            {
                var elemOffset = offset + (i * stride);
                for (var c = 0; c < cardinality; c++)
                {
                    result[(i * cardinality) + c] = ReadComponent(
                        bytes, elemOffset + (c * compSize), componentType, accessor.Normalized == true);
                }
            }
        }
        // else: accessor is defined entirely by sparse, or is all zeroes.

        if (accessor.Sparse is { } sparse)
        {
            ApplySparse(accessor, sparse, cardinality, result);
        }

        return result;
    }

    /// <summary>Decodes an indices accessor to unsigned ints (any integer component type).</summary>
    public uint[] ReadAccessorIndices(int accessorIndex)
    {
        var accessor = GetAccessor(accessorIndex);
        var componentType = (ComponentType)accessor.ComponentType;
        if (componentType == ComponentType.Float)
        {
            throw new InvalidDataException($"Index accessor #{accessorIndex} may not use float components.");
        }

        var cardinality = TypeCardinality(accessor.Type);
        if (cardinality != 1)
        {
            throw new InvalidDataException($"Index accessor #{accessorIndex} must be SCALAR.");
        }

        var result = new uint[accessor.Count];
        if (accessor.BufferView is int bv)
        {
            var compSize = ComponentSize(componentType);
            var stride = GetStride(bv);
            if (stride == 0)
            {
                stride = compSize;
            }

            var bytes = GetBufferView(bv);
            var offset = accessor.ByteOffset ?? 0;
            CheckRange(bv, offset, accessor.Count, stride, compSize, accessorIndex);
            for (var i = 0; i < accessor.Count; i++)
            {
                var elemOffset = offset + (i * stride);
                result[i] = componentType switch
                {
                    ComponentType.UnsignedByte => bytes[elemOffset],
                    ComponentType.UnsignedShort => BitConverter.ToUInt16(bytes[elemOffset..]),
                    ComponentType.UnsignedInt => BitConverter.ToUInt32(bytes[elemOffset..]),
                    ComponentType.Byte => (uint)(sbyte)bytes[elemOffset],
                    ComponentType.Short => (uint)(short)BitConverter.ToUInt16(bytes[elemOffset..]),
                    _ => throw new InvalidDataException($"Invalid index componentType {(int)componentType}."),
                };
            }
        }

        if (accessor.Sparse is { } sparse)
        {
            ApplySparseIndices(accessor, sparse, componentType, result);
        }

        return result;
    }

    private void ApplySparseIndices(GltfAccessor accessor, GltfSparse sparse, ComponentType componentType, uint[] result)
    {
        if (sparse.Indices is not { } sparseIndices || sparse.Values is not { } sparseValues)
        {
            throw new InvalidDataException("Sparse accessor is missing its indices or values block.");
        }

        var indicesView = GetBufferView(sparseIndices.BufferView);
        var valuesView = GetBufferView(sparseValues.BufferView);
        var indexType = (ComponentType)sparseIndices.ComponentType;
        var indexSize = ComponentSize(indexType);
        var valueSize = ComponentSize(componentType);
        var indexOffset = sparseIndices.ByteOffset ?? 0;
        var valueOffset = sparseValues.ByteOffset ?? 0;

        for (var s = 0; s < sparse.Count; s++)
        {
            var target = ReadSparseTarget(indicesView, indexOffset + (s * indexSize), indexType);
            if (target >= result.Length)
            {
                throw new InvalidDataException($"Sparse override index {target} exceeds accessor count {result.Length}.");
            }

            var valueBytes = valuesView.Slice(valueOffset + (s * valueSize), valueSize);
            result[target] = componentType switch
            {
                ComponentType.UnsignedByte => valueBytes[0],
                ComponentType.UnsignedShort => BitConverter.ToUInt16(valueBytes),
                ComponentType.UnsignedInt => BitConverter.ToUInt32(valueBytes),
                ComponentType.Byte => (uint)(sbyte)valueBytes[0],
                ComponentType.Short => (uint)(short)BitConverter.ToUInt16(valueBytes),
                _ => throw new InvalidDataException($"Invalid sparse value componentType {(int)componentType}."),
            };
        }
    }

    private void ApplySparse(GltfAccessor accessor, GltfSparse sparse, int cardinality, float[] result)
    {
        if (sparse.Indices is not { } sparseIndices || sparse.Values is not { } sparseValues)
        {
            throw new InvalidDataException("Sparse accessor is missing its indices or values block.");
        }

        var indicesView = GetBufferView(sparseIndices.BufferView);
        var valuesView = GetBufferView(sparseValues.BufferView);
        var indexType = (ComponentType)sparseIndices.ComponentType;
        var indexSize = ComponentSize(indexType);
        var valueType = (ComponentType)accessor.ComponentType;
        var valueSize = ComponentSize(valueType);
        var indexOffset = sparseIndices.ByteOffset ?? 0;
        var valueOffset = sparseValues.ByteOffset ?? 0;

        for (var s = 0; s < sparse.Count; s++)
        {
            var target = ReadSparseTarget(indicesView, indexOffset + (s * indexSize), indexType);
            if (target >= accessor.Count)
            {
                throw new InvalidDataException($"Sparse override index {target} exceeds accessor count {accessor.Count}.");
            }

            for (var c = 0; c < cardinality; c++)
            {
                result[(target * cardinality) + c] = ReadComponent(
                    valuesView, valueOffset + (((s * cardinality) + c) * valueSize), valueType, accessor.Normalized == true);
            }
        }
    }

    private static int ReadSparseTarget(ReadOnlySpan<byte> indicesView, int offset, ComponentType indexType)
    {
        var slice = indicesView.Slice(offset, ComponentSize(indexType));
        return indexType switch
        {
            ComponentType.UnsignedByte => slice[0],
            ComponentType.UnsignedShort => BitConverter.ToUInt16(slice),
            ComponentType.UnsignedInt => checked((int)BitConverter.ToUInt32(slice)),
            _ => throw new InvalidDataException($"Invalid sparse index componentType {(int)indexType}."),
        };
    }

    /// <summary>Throws when an accessor's elements would read past its bufferView.</summary>
    private void CheckRange(int bufferViewIndex, int offset, int count, int stride, int minElementBytes, int accessorIndex)
    {
        if (count == 0)
        {
            return;
        }

        var length = GetBufferView(bufferViewIndex).Length;
        var lastEnd = offset + ((count - 1) * stride) + minElementBytes;
        if (offset < 0 || lastEnd > length)
        {
            throw new InvalidDataException(
                $"Accessor #{accessorIndex} reads [{offset}, {lastEnd}) but bufferView #{bufferViewIndex} is only {length} bytes.");
        }
    }

    private GltfAccessor GetAccessor(int index)
    {
        var accessors = _root.Accessors ?? throw new InvalidDataException($"No accessors in file (wanted #{index}).");
        return accessors[index];
    }

    private static float ReadComponent(ReadOnlySpan<byte> bytes, int offset, ComponentType type, bool normalised)
    {
        switch (type)
        {
            case ComponentType.Float:
                return BitConverter.ToSingle(bytes[offset..]);
            case ComponentType.Byte:
            {
                var v = (sbyte)bytes[offset];
                return normalised ? MathF.Max(v / 127f, -1f) : v;
            }
            case ComponentType.UnsignedByte:
            {
                var v = bytes[offset];
                return normalised ? v / 255f : v;
            }
            case ComponentType.Short:
            {
                var v = (short)BitConverter.ToUInt16(bytes[offset..]);
                return normalised ? MathF.Max(v / 32767f, -1f) : v;
            }
            case ComponentType.UnsignedShort:
            {
                var v = BitConverter.ToUInt16(bytes[offset..]);
                return normalised ? v / 65535f : v;
            }
            case ComponentType.UnsignedInt:
                return BitConverter.ToUInt32(bytes[offset..]);
            default:
                throw new InvalidDataException($"Unknown componentType {(int)type}.");
        }
    }
}
