using System.Numerics;
using CentreStage.Core;

namespace CentreStage.Core.Tests;

public class AnimationTests
{
    private const float Eps = 1e-4f;

    private static RuntimeAnimationSampler Sampler(
        float[] input,
        float[] output,
        AnimationInterpolation interpolation = AnimationInterpolation.Linear) =>
        new() { Input = input, Output = output, Interpolation = interpolation };

    // --- STEP ---

    [Fact]
    public void Step_HoldsPreviousValue()
    {
        var s = Sampler([0f, 1f], [10f, 20f], AnimationInterpolation.Step);
        Span<float> v = stackalloc float[1];

        Assert.True(AnimationEvaluation.TryEvaluate(s, 0.5f, 1, v));
        Assert.Equal(10f, v[0], Eps);

        Assert.True(AnimationEvaluation.TryEvaluate(s, 0.999f, 1, v));
        Assert.Equal(10f, v[0], Eps);
    }

    // --- LINEAR ---

    [Fact]
    public void Linear_InterpolatesComponents()
    {
        var s = Sampler([0f, 2f], [0f, 0f, 0f, 4f, 6f, 8f]);
        Span<float> v = stackalloc float[3];

        Assert.True(AnimationEvaluation.TryEvaluate(s, 1f, 3, v));
        Assert.Equal(2f, v[0], Eps);
        Assert.Equal(3f, v[1], Eps);
        Assert.Equal(4f, v[2], Eps);
    }

    [Fact]
    public void Linear_ClampsOutsideRange()
    {
        var s = Sampler([1f, 2f], [5f, 9f]);
        Span<float> v = stackalloc float[1];

        Assert.True(AnimationEvaluation.TryEvaluate(s, 0f, 1, v));
        Assert.Equal(5f, v[0], Eps);

        Assert.True(AnimationEvaluation.TryEvaluate(s, 100f, 1, v));
        Assert.Equal(9f, v[0], Eps);
    }

    [Fact]
    public void SingleKeyframe_IsConstant()
    {
        var s = Sampler([0.5f], [3f]);
        Span<float> v = stackalloc float[1];

        Assert.True(AnimationEvaluation.TryEvaluate(s, 0f, 1, v));
        Assert.Equal(3f, v[0], Eps);
        Assert.True(AnimationEvaluation.TryEvaluate(s, 99f, 1, v));
        Assert.Equal(3f, v[0], Eps);
    }

    // --- Rotation (slerp) ---

    [Fact]
    public void Rotation_Linear_SlerpsAndNormalises()
    {
        // Identity -> 90° about Y; midpoint is 45° about Y.
        var half = MathF.Sin(MathF.PI / 4f);
        var s = Sampler(
            [0f, 1f],
            [0f, 0f, 0f, 1f, 0f, half, 0f, half]);
        Span<float> v = stackalloc float[4];

        Assert.True(AnimationEvaluation.TryEvaluateRotation(s, 0.5f, v));
        var q = new Quaternion(v[0], v[1], v[2], v[3]);
        Assert.Equal(1f, q.Length(), Eps);

        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        var dot = MathF.Abs(Quaternion.Dot(q, expected));
        Assert.Equal(1f, dot, 3e-3f);
    }

    [Fact]
    public void Rotation_Linear_TakesShortestArc()
    {
        // q and -q are the same rotation: interpolation must not travel the long way.
        var half = MathF.Sin(MathF.PI / 4f);
        var s = Sampler(
            [0f, 1f],
            [0f, 0f, 0f, 1f, 0f, -half, 0f, -half]);
        Span<float> v = stackalloc float[4];

        Assert.True(AnimationEvaluation.TryEvaluateRotation(s, 0.5f, v));
        var q = new Quaternion(v[0], v[1], v[2], v[3]);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        var dot = MathF.Abs(Quaternion.Dot(q, expected));
        Assert.Equal(1f, dot, 3e-3f);
    }

    // --- CUBICSPLINE ---

    [Fact]
    public void Cubic_ReproducesParabolaWithScaledTangents()
    {
        // y = t² on [0, 2]: values 0 and 4, derivatives 0 and 4. Layout per key is
        // (in-tangent, value, out-tangent); stored tangents scale by segment duration.
        var s = Sampler(
            [0f, 2f],
            [
                0f, 0f, 0f,     // key 0: in, value, out (out = dy/dt = 0)
                4f, 4f, 4f,     // key 1: in = dy/dt = 4, value = 4, out
            ],
            AnimationInterpolation.CubicSpline);
        Span<float> v = stackalloc float[1];

        Assert.True(AnimationEvaluation.TryEvaluate(s, 1f, 1, v));
        Assert.Equal(1f, v[0], Eps); // y(1) = 1

        Assert.True(AnimationEvaluation.TryEvaluate(s, 0.5f, 1, v));
        Assert.Equal(0.25f, v[0], Eps); // y(0.5) = 0.25
    }

    [Fact]
    public void Cubic_MidpointMatchesLinearWhenTangentsMatchSlope()
    {
        // Straight line: values 0 -> 2 over one second, slopes 2/s at both ends.
        // Layout per key is (in-tangent, value, out-tangent).
        var s = Sampler(
            [0f, 1f],
            [0f, 0f, 2f, 2f, 2f, 0f], // key0: in=0, v=0, out=2 | key1: in=2, v=2, out=0
            AnimationInterpolation.CubicSpline);
        Span<float> v = stackalloc float[1];

        Assert.True(AnimationEvaluation.TryEvaluate(s, 0.5f, 1, v));
        Assert.Equal(1f, v[0], Eps); // matches linear midpoint
    }

    [Fact]
    public void Cubic_RotationResultIsNormalised()
    {
        // Asymmetric tangents push the interpolated 4-vector off the unit sphere;
        // the output must renormalise. Layout per key: (in, value, out) x 4 comps.
        var s = Sampler(
            [0f, 1f],
            [
                0f, 0f, 0f, 0f,   // key0 in (unused)
                0f, 0f, 0f, 1f,   // key0 value
                0.4f, 0f, 0f, 0f, // key0 out
                0f, 0f, 0f, 0f,   // key1 in
                0f, 0f, 0f, 1f,   // key1 value
                0f, 0f, 0f, 0f,   // key1 out (unused)
            ],
            AnimationInterpolation.CubicSpline);
        Span<float> v = stackalloc float[4];

        Assert.True(AnimationEvaluation.TryEvaluateRotation(s, 0.5f, v));
        var len = MathF.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]) + (v[3] * v[3]));
        Assert.Equal(1f, len, Eps);
    }

    // --- Validation ---

    [Fact]
    public void TruncatedOutput_FailsInsteadOfReadingOutOfRange()
    {
        var s = Sampler([0f, 1f], [0f]); // needs 2 values for 2 keys
        Span<float> v = stackalloc float[1];
        Assert.False(AnimationEvaluation.TryEvaluate(s, 0.5f, 1, v));
    }

    [Fact]
    public void EmptySampler_Fails()
    {
        var s = Sampler([], []);
        Span<float> v = stackalloc float[1];
        Assert.False(AnimationEvaluation.TryEvaluate(s, 0.5f, 1, v));
    }

    // --- ScenePose: hierarchy and skinning ---

    private static GltfModel ModelWithNodesAndClip(
        object[] nodes,
        object animations,
        object? skins = null,
        byte[]? bin = null,
        object[]? accessors = null,
        object[]? bufferViews = null)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = nodes,
            ["animations"] = animations,
        };
        if (skins is not null)
        {
            manifest["skins"] = skins;
        }

        if (accessors is not null)
        {
            manifest["accessors"] = accessors;
            manifest["bufferViews"] = bufferViews;
            manifest["buffers"] = new[] { new { byteLength = bin?.Length ?? 0 } };
        }

        return GltfModel.Load(GlbBuilder.Build(manifest, bin));
    }

    private static object[] TrsClip(float[] times, float[] translations, string interpolation = "LINEAR") => new[]
    {
        new
        {
            name = "move",
            channels = new[] { new { sampler = 0, target = new { node = 1, path = "translation" } } },
            samplers = new[] { new { input = 0, output = 1, interpolation } },
        },
    };

    private static object[] ScalarAccessors(float[] times, float[] values) => new[]
    {
        new { bufferView = 0, componentType = 5126, count = times.Length, type = "SCALAR" },
        new { bufferView = 1, componentType = 5126, count = values.Length / 3, type = "VEC3" },
    };

    private static object[] TwoViews(int timesBytes, int valuesBytes) => new[]
    {
        new { buffer = 0, byteOffset = 0, byteLength = timesBytes },
        new { buffer = 0, byteOffset = timesBytes, byteLength = valuesBytes },
    };

    private static byte[] Floats(params float[][] arrays)
    {
        var total = arrays.Sum(a => a.Length) * 4;
        var bin = new byte[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            foreach (var f in a)
            {
                BitConverter.GetBytes(f).CopyTo(bin, offset);
                offset += 4;
            }
        }

        return bin;
    }

    [Fact]
    public void Pose_TranslationChannelMovesNodeAndChild()
    {
        var times = new[] { 0f, 1f };
        var values = new[] { 0f, 0f, 0f, 3f, 0f, 0f };
        var bin = Floats(times, values);

        var nodes = new object[]
        {
            new { children = new[] { 1 } },
            new { translation = new[] { 0f, 0f, 0f } },
        };

        var model = ModelWithNodesAndClip(
            nodes,
            TrsClip(times, values),
            bin: bin,
            accessors: ScalarAccessors(times, values),
            bufferViews: TwoViews(8, 24));

        var pose = new ScenePose(model);
        pose.Apply(model.Animations[0], 1f);

        Assert.Equal(3f, pose.World(1).Translation.X, Eps);
        Assert.Equal(0f, pose.World(0).Translation.X, Eps);
    }

    [Fact]
    public void Pose_ParentedNodeInheritsParentTransform()
    {
        var times = new[] { 0f, 1f };
        var values = new[] { 0f, 0f, 0f, 5f, 0f, 0f };
        var bin = Floats(times, values);

        // Node 0 is the animated one; node 1 is its child.
        var nodes = new object[]
        {
            new { children = new[] { 1 } },
            new { translation = new[] { 2f, 0f, 0f } },
        };
        var animations = new[]
        {
            new
            {
                channels = new[] { new { sampler = 0, target = new { node = 0, path = "translation" } } },
                samplers = new[] { new { input = 0, output = 1 } },
            },
        };

        var model = ModelWithNodesAndClip(
            nodes, animations, bin: bin,
            accessors: ScalarAccessors(times, values),
            bufferViews: TwoViews(8, 24));

        var pose = new ScenePose(model);
        pose.Apply(model.Animations[0], 1f);

        Assert.Equal(5f, pose.World(0).Translation.X, Eps);
        Assert.Equal(7f, pose.World(1).Translation.X, Eps); // parent's 5 + own 2
    }

    [Fact]
    public void Pose_NullClipRestoresRestPose()
    {
        var times = new[] { 0f, 1f };
        var values = new[] { 0f, 0f, 0f, 9f, 0f, 0f };
        var bin = Floats(times, values);

        var nodes = new object[] { new { }, new { } };
        var model = ModelWithNodesAndClip(
            nodes, TrsClip(times, values), bin: bin,
            accessors: ScalarAccessors(times, values),
            bufferViews: TwoViews(8, 24));

        var pose = new ScenePose(model);
        pose.Apply(model.Animations[0], 1f);
        Assert.Equal(9f, pose.World(1).Translation.X, Eps);

        pose.Apply(null, 0f);
        Assert.Equal(0f, pose.World(1).Translation.X, Eps);
    }

    [Fact]
    public void Pose_JointMatricesComposeInverseBindWithJointWorld()
    {
        // One joint (node 1) offset by x=2; inverse bind undoes that offset, so a
        // vertex at the origin maps to the joint's posed world position.
        var ibm = new float[16];
        ibm[0] = 1f;
        ibm[5] = 1f;
        ibm[10] = 1f;
        ibm[15] = 1f;
        ibm[12] = -2f; // column-major: translation x = -2

        var times = new[] { 0f, 1f };
        var values = new[] { 0f, 0f, 0f, 4f, 0f, 0f };
        var bin = Floats(times, values, ibm);

        var nodes = new object[]
        {
            new { children = new[] { 1, 2 } },
            new { translation = new[] { 2f, 0f, 0f } },
            new { },
        };
        var skins = new[] { new { joints = new[] { 1 }, inverseBindMatrices = 2 } };
        var animations = new[]
        {
            new
            {
                channels = new[] { new { sampler = 0, target = new { node = 1, path = "translation" } } },
                samplers = new[] { new { input = 0, output = 1 } },
            },
        };

        var model = ModelWithNodesAndClip(
            nodes, animations, skins, bin,
            accessors: new[]
            {
                new { bufferView = 0, componentType = 5126, count = 2, type = "SCALAR" },
                new { bufferView = 1, componentType = 5126, count = 2, type = "VEC3" },
                new { bufferView = 2, componentType = 5126, count = 1, type = "MAT4" },
            },
            bufferViews: new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 8 },
                new { buffer = 0, byteOffset = 8, byteLength = 24 },
                new { buffer = 0, byteOffset = 32, byteLength = 64 },
            });

        var pose = new ScenePose(model);
        pose.Apply(model.Animations[0], 1f); // joint node moves 2 -> 4

        var joints = new Matrix4x4[1];
        pose.JointMatrices(model.Skins[0], joints);

        // Skinning invariant: the joint's bind position maps to its posed world position.
        var posed = Vector3.Transform(new Vector3(2f, 0f, 0f), joints[0]);
        Assert.Equal(4f, posed.X, Eps);

        // The mesh-space origin sits 2 units from the joint; that offset survives.
        var origin = Vector3.Transform(Vector3.Zero, joints[0]);
        Assert.Equal(2f, origin.X, Eps);
    }

    [Fact]
    public void Pose_VersionTracksRealChangesOnly()
    {
        // The renderer keys per-frame re-sorting on Version: re-applying the same
        // (clip, time) every frame (paused playback) must not bump it, while any
        // real change must. Reset then re-apply must recompute (same clip/time is
        // only a no-op when that pose is already in effect).
        var ibm = new float[16];
        ibm[0] = 1f;
        ibm[5] = 1f;
        ibm[10] = 1f;
        ibm[15] = 1f;
        ibm[12] = -2f;

        var times = new[] { 0f, 1f };
        var values = new[] { 0f, 0f, 0f, 4f, 0f, 0f };
        var bin = Floats(times, values, ibm);

        var nodes = new object[]
        {
            new { children = new[] { 1, 2 } },
            new { translation = new[] { 2f, 0f, 0f } },
            new { },
        };
        var skins = new[] { new { joints = new[] { 1 }, inverseBindMatrices = 2 } };
        var animations = new[]
        {
            new
            {
                channels = new[] { new { sampler = 0, target = new { node = 1, path = "translation" } } },
                samplers = new[] { new { input = 0, output = 1 } },
            },
        };

        var model = ModelWithNodesAndClip(
            nodes, animations, skins, bin,
            accessors: new[]
            {
                new { bufferView = 0, componentType = 5126, count = 2, type = "SCALAR" },
                new { bufferView = 1, componentType = 5126, count = 2, type = "VEC3" },
                new { bufferView = 2, componentType = 5126, count = 1, type = "MAT4" },
            },
            bufferViews: new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 8 },
                new { buffer = 0, byteOffset = 8, byteLength = 24 },
                new { buffer = 0, byteOffset = 32, byteLength = 64 },
            });

        var pose = new ScenePose(model);
        pose.Apply(model.Animations[0], 1f);
        var first = pose.Version;

        pose.Apply(model.Animations[0], 1f); // same clip and time: no-op
        Assert.Equal(first, pose.Version);

        pose.Apply(model.Animations[0], 0.5f); // different time: recompute
        Assert.True(pose.Version > first);

        pose.Reset();
        var afterReset = pose.Version;
        pose.Reset(); // already at rest: no-op
        Assert.Equal(afterReset, pose.Version);

        pose.Apply(model.Animations[0], 1f); // re-apply after Reset must recompute
        Assert.True(pose.Version > afterReset);
    }

    [Fact]
    public void Pose_MatrixNodeWithShearFallsBackSafely()
    {
        // A matrix with a shear cannot decompose: pose must not throw or NaN.
        var nodes = new object[]
        {
            new { children = new[] { 1 } },
            new { matrix = new[] { 1f, 0.5f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 7f, 0f, 0f, 1f } },
        };
        var times = new[] { 0f, 1f };
        var values = new[] { 0f, 0f, 0f, 1f, 0f, 0f };
        var bin = Floats(times, values);

        var model = ModelWithNodesAndClip(
            nodes, TrsClip(times, values), bin: bin,
            accessors: ScalarAccessors(times, values),
            bufferViews: TwoViews(8, 24));

        var pose = new ScenePose(model);
        pose.Apply(model.Animations[0], 1f);

        var world = pose.World(1);
        Assert.False(float.IsNaN(world.M11));
    }
}
