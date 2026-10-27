using System.Numerics;

namespace CentreStage.Core.Tests;

public class GltfMathTests
{
    [Fact]
    public void FromGltfMatrix_ReadsColumnMajorTranslation()
    {
        // glTF column-major: translation lives in elements 12..14.
        var m = new float[16];
        m[0] = m[5] = m[10] = m[15] = 1;
        m[12] = 3;
        m[13] = 4;
        m[14] = 5;

        var matrix = GltfMath.FromGltfMatrix(m);

        // Numerics row-vector convention: translation is M41..M43 = the transposed read.
        Assert.Equal(new Vector3(3, 4, 5), matrix.Translation);
    }

    [Fact]
    public void ComposeTrs_AppliesScaleRotateTranslate()
    {
        var t = GltfMath.ComposeTrs(
            new Vector3(10, 0, 0),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            new Vector3(2, 2, 2));

        // Point (1,0,0): scale -> (2,0,0), rotate 90° about Y -> (0,0,-2), translate -> (10,0,-2).
        var p = Vector3.Transform(new Vector3(1, 0, 0), t);
        Assert.True(Vector3.Distance(new Vector3(10, 0, -2), p) < 1e-4f, $"got {p}");
    }

    [Fact]
    public void Combine_AppliesChildBeforeParent()
    {
        var parent = Matrix4x4.CreateTranslation(100, 0, 0);
        var child = Matrix4x4.CreateTranslation(5, 0, 0);

        var world = GltfMath.Combine(child, parent);

        // Child offset must land at 105, not 100 in parent's space only.
        Assert.Equal(new Vector3(105, 0, 0), world.Translation);
    }
}
