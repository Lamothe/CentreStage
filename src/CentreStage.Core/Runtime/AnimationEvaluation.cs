namespace CentreStage.Core;

/// <summary>
/// Pure sampler evaluation: keyframe lookup plus the three glTF interpolation modes
/// (Appendix C). Times outside a sampler's range clamp to the first/last keyframe value —
/// animation-level looping wraps time before it reaches here. Quaternions interpolate by
/// slerp on the shortest arc and re-normalise (also after CUBICSPLINE, per the spec).
/// </summary>
public static class AnimationEvaluation
{
    /// <summary>
    /// Evaluates a translation/scale sampler (3 components) at <paramref name="time"/>.
    /// Returns false when the sampler cannot be evaluated (no keyframes, or output length
    /// does not match the expected layout).
    /// </summary>
    public static bool TryEvaluate(
        RuntimeAnimationSampler sampler,
        float time,
        int components,
        Span<float> result)
    {
        if (components == 4)
        {
            throw new ArgumentException("Use TryEvaluateRotation for quaternion samplers.", nameof(components));
        }

        if (result.Length < components || !TryFindSegment(sampler, time, components, out var key, out var u))
        {
            return false;
        }

        if (key < 0)
        {
            // Clamped endpoint (or single keyframe): the keyframe value.
            CopyValue(sampler, key, components, result);
            return true;
        }

        switch (sampler.Interpolation)
        {
            case AnimationInterpolation.Step:
                CopyValue(sampler, key, components, result);
                return true;

            case AnimationInterpolation.CubicSpline:
                EvaluateCubic(sampler, key, u, components, result);
                return true;

            default:
                EvaluateLinear(sampler, key, u, components, result);
                return true;
        }
    }

    /// <summary>Evaluates a rotation sampler (4 components) at <paramref name="time"/>.</summary>
    public static bool TryEvaluateRotation(
        RuntimeAnimationSampler sampler,
        float time,
        Span<float> result)
    {
        const int components = 4;
        if (result.Length < components || !TryFindSegment(sampler, time, components, out var key, out var u))
        {
            return false;
        }

        if (key < 0)
        {
            CopyValue(sampler, key, components, result);
            Normalise(result);
            return true;
        }

        switch (sampler.Interpolation)
        {
            case AnimationInterpolation.Step:
                CopyValue(sampler, key, components, result);
                break;

            case AnimationInterpolation.CubicSpline:
                EvaluateCubic(sampler, key, u, components, result);
                break;

            default:
                Slerp(sampler, key, u, result);
                break;
        }

        Normalise(result);
        return true;
    }

    /// <summary>
    /// Finds the segment containing <paramref name="time"/>. Key -1 with u = 0 means
    /// "use the first keyframe value", key -2 means "use the last keyframe value".
    /// </summary>
    private static bool TryFindSegment(
        RuntimeAnimationSampler sampler,
        float time,
        int components,
        out int key,
        out float u)
    {
        key = 0;
        u = 0f;

        var keys = sampler.KeyCount;
        if (keys == 0)
        {
            return false;
        }

        var stride = sampler.Interpolation == AnimationInterpolation.CubicSpline ? 3 * components : components;
        if (sampler.Output.Length < keys * stride)
        {
            return false;
        }

        if (keys == 1)
        {
            key = -1;
            return true;
        }

        if (time <= sampler.Input[0])
        {
            key = -1;
            return true;
        }

        if (time >= sampler.Input[keys - 1])
        {
            key = -2;
            return true;
        }

        // Last keyframe whose time is <= time; Input is strictly increasing.
        var lo = 0;
        var hi = keys - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (sampler.Input[mid] <= time)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        key = lo;
        u = (time - sampler.Input[lo]) / (sampler.Input[lo + 1] - sampler.Input[lo]);
        return true;
    }

    /// <summary>
    /// Keyframe value at key <paramref name="key"/>. Negative keys are the clamped
    /// endpoints (-1 first, -2 last). For CUBICSPLINE the value is the middle element of
    /// the key's (in-tangent, value, out-tangent) triple.
    /// </summary>
    private static void CopyValue(
        RuntimeAnimationSampler sampler,
        int key,
        int components,
        Span<float> result)
    {
        var keys = sampler.KeyCount;
        var resolved = key switch
        {
            -1 => 0,
            -2 => keys - 1,
            _ => key,
        };
        var cubic = sampler.Interpolation == AnimationInterpolation.CubicSpline;
        var perKey = cubic ? 3 * components : components;
        var offset = (resolved * perKey) + (cubic ? components : 0);
        sampler.Output.AsSpan(offset, components).CopyTo(result);
    }

    private static void EvaluateLinear(
        RuntimeAnimationSampler sampler,
        int key,
        float u,
        int components,
        Span<float> result)
    {
        var a = sampler.Output.AsSpan(key * components, components);
        var b = sampler.Output.AsSpan((key + 1) * components, components);
        for (var c = 0; c < components; c++)
        {
            result[c] = a[c] + ((b[c] - a[c]) * u);
        }
    }

    /// <summary>Slerp between rotation keyframes, flipping to the shortest arc first.</summary>
    private static void Slerp(RuntimeAnimationSampler sampler, int key, float u, Span<float> result)
    {
        var a = sampler.Output.AsSpan(key * 4, 4);
        var b = sampler.Output.AsSpan((key + 1) * 4, 4);

        // Shortest arc: negated quaternions are the same rotation.
        var dot = (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]) + (a[3] * b[3]);
        var sign = dot < 0f ? -1f : 1f;

        // Near-parallel quaternions: nlerp is stable where slerp divides by sin(θ)≈0.
        if (MathF.Abs(dot) > 0.9995f)
        {
            for (var c = 0; c < 4; c++)
            {
                result[c] = a[c] + ((sign * b[c] - a[c]) * u);
            }

            return;
        }

        var theta = MathF.Acos(Math.Clamp(MathF.Abs(dot), -1f, 1f));
        var sinTheta = MathF.Sin(theta);
        var wa = MathF.Sin((1f - u) * theta) / sinTheta;
        var wb = (MathF.Sin(u * theta) / sinTheta) * sign;
        for (var c = 0; c < 4; c++)
        {
            result[c] = (wa * a[c]) + (wb * b[c]);
        }
    }

    /// <summary>
    /// Cubic Hermite on the segment, per glTF 2.0 Appendix C.5. Stored tangents are
    /// direction vectors and scale by the segment duration before use.
    /// </summary>
    private static void EvaluateCubic(
        RuntimeAnimationSampler sampler,
        int key,
        float u,
        int components,
        Span<float> result)
    {
        var segmentDuration = sampler.Input[key + 1] - sampler.Input[key];
        var perKey = 3 * components;
        // Layout per key: [in-tangent, value, out-tangent].
        var outTangent = sampler.Output.AsSpan((key * perKey) + (2 * components), components);
        var value0 = sampler.Output.AsSpan((key * perKey) + components, components);
        var inTangent = sampler.Output.AsSpan((key + 1) * perKey, components);
        var value1 = sampler.Output.AsSpan(((key + 1) * perKey) + components, components);

        var u2 = u * u;
        var u3 = u2 * u;
        var h00 = (2f * u3) - (3f * u2) + 1f;
        var h10 = u3 - (2f * u2) + u;
        var h01 = (-2f * u3) + (3f * u2);
        var h11 = u3 - u2;

        for (var c = 0; c < components; c++)
        {
            result[c] = (h00 * value0[c])
                + (h10 * segmentDuration * outTangent[c])
                + (h01 * value1[c])
                + (h11 * segmentDuration * inTangent[c]);
        }
    }

    private static void Normalise(Span<float> q)
    {
        var len = MathF.Sqrt((q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]));
        if (len <= 1e-8f)
        {
            q[0] = 0f;
            q[1] = 0f;
            q[2] = 0f;
            q[3] = 1f;
            return;
        }

        var inv = 1f / len;
        q[0] *= inv;
        q[1] *= inv;
        q[2] *= inv;
        q[3] *= inv;
    }
}
