namespace CentreStage.Core;

/// <summary>
/// Orders the triangles of an alpha-blended primitive back-to-front. Straight-alpha
/// compositing is order-dependent: drawing in index order lets near triangles paint
/// over far ones that should be visible through them (feather and hair cards are the
/// usual victims). Painter's algorithm on triangle depth is the standard remedy.
/// </summary>
public static class TriangleSort
{
    /// <summary>
    /// Rewrites <paramref name="sortedIndices"/> with the triangles of <paramref name="indices"/>
    /// ordered farthest-first, where depth is the sum of each triangle's three
    /// <paramref name="vertexDepth"/> values (equivalent to the centroid mean for ordering).
    /// View space looks down -Z, so the most negative depth is farthest and sorts first.
    /// Ties keep original triangle order, so coplanar cards never flicker between frames.
    /// <paramref name="keys"/> is caller-provided scratch of at least indices.Length / 3 elements.
    /// </summary>
    public static void SortBackToFront(
        ReadOnlySpan<float> vertexDepth,
        ReadOnlySpan<uint> indices,
        Span<uint> sortedIndices,
        Span<ulong> keys)
    {
        var triCount = indices.Length / 3;
        for (var t = 0; t < triCount; t++)
        {
            var i0 = (int)indices[3 * t];
            var i1 = (int)indices[3 * t + 1];
            var i2 = (int)indices[3 * t + 2];
            var depth = vertexDepth[i0] + vertexDepth[i1] + vertexDepth[i2];

            // Pack depth order and original triangle index into one sortable ulong:
            // float bits made monotone (negative floats invert) in the high word,
            // triangle index in the low word. Unsigned comparison is essential —
            // depths on both sides of zero are normal when the camera sits inside
            // the geometry, and a signed sort would invert those groups. The index
            // tie-break makes the sort stable.
            var bits = (uint)BitConverter.SingleToInt32Bits(depth);
            var sortable = (bits & 0x8000_0000u) != 0 ? ~bits : bits | 0x8000_0000u;
            keys[t] = ((ulong)sortable << 32) | (uint)t;
        }

        keys[..triCount].Sort();

        for (var t = 0; t < triCount; t++)
        {
            var src = 3 * (int)(keys[t] & 0xFFFF_FFFF);
            sortedIndices[3 * t] = indices[src];
            sortedIndices[3 * t + 1] = indices[src + 1];
            sortedIndices[3 * t + 2] = indices[src + 2];
        }
    }
}
