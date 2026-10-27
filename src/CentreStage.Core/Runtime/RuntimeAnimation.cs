namespace CentreStage.Core;

/// <summary>Which node property an animation channel drives.</summary>
public enum AnimationPath
{
    Translation,
    Rotation,
    Scale,
    /// <summary>Morph-target weights: parsed but not applied (morphs are unsupported).</summary>
    Weights,
}

public enum AnimationInterpolation
{
    Linear,
    Step,
    CubicSpline,
}

/// <summary>
/// One animation sampler: keyframe times plus packed keyframe values.
/// </summary>
public sealed class RuntimeAnimationSampler
{
    /// <summary>Strictly increasing keyframe times, in seconds.</summary>
    public required float[] Input { get; init; }

    /// <summary>
    /// Packed keyframe values. For LINEAR/STEP: one element per keyframe per component.
    /// For CUBICSPLINE: three per keyframe (in-tangent, value, out-tangent), in that order.
    /// </summary>
    public required float[] Output { get; init; }

    public AnimationInterpolation Interpolation { get; init; } = AnimationInterpolation.Linear;

    /// <summary>Keyframe count.</summary>
    public int KeyCount => Input.Length;
}

/// <summary>One channel: a sampler driving one property of one node.</summary>
public sealed class RuntimeAnimationChannel
{
    /// <summary>Index into the manifest's nodes array.</summary>
    public required int NodeIndex { get; init; }

    public required AnimationPath Path { get; init; }

    /// <summary>Index into the owning animation's samplers.</summary>
    public required int SamplerIndex { get; init; }
}

/// <summary>A named animation clip.</summary>
public sealed class RuntimeAnimation
{
    public string? Name { get; init; }

    /// <summary>Clip length in seconds: the last keyframe time across its samplers.</summary>
    public required float Duration { get; init; }

    public required IReadOnlyList<RuntimeAnimationChannel> Channels { get; init; }

    public required IReadOnlyList<RuntimeAnimationSampler> Samplers { get; init; }
}
