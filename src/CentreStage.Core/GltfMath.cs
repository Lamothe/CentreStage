using System.Numerics;

namespace CentreStage.Core;

/// <summary>
/// Transform helpers for glTF's column-major matrices.
///
/// glTF uses column-vector convention (p' = M·p) with column-major storage. System.Numerics
/// uses row-vector convention (p' = p·N) with row-major field order. The two conventions are
/// transposes of each other, so a glTF array read into a Numerics matrix in stored order is
/// exactly the transpose we need. Composition order reverses accordingly: glTF world =
/// parent·child becomes Numerics world = child·parent (see tests).
/// </summary>
public static class GltfMath
{
    /// <summary>Reads a 16-float column-major glTF matrix into a Numerics matrix.</summary>
    public static Matrix4x4 FromGltfMatrix(ReadOnlySpan<float> m)
    {
        if (m.Length != 16)
        {
            throw new ArgumentException("glTF matrices have exactly 16 elements.", nameof(m));
        }

        return new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);
    }

    /// <summary>Composes a node's TRS into a local transform (glTF: M = T·R·S).</summary>
    public static Matrix4x4 ComposeTrs(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
    }

    /// <summary>Combines a child local transform with the parent's world transform.</summary>
    public static Matrix4x4 Combine(Matrix4x4 childLocal, Matrix4x4 parentWorld) =>
        childLocal * parentWorld;
}
