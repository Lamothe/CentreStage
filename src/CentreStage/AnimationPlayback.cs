using CentreStage.Core;

namespace CentreStage;

/// <summary>
/// Clip selection and clock for the viewer. Owns the ScenePose so the render loop
/// just advances time, applies, and draws.
/// </summary>
internal sealed class AnimationPlayback
{
    private readonly ScenePose _pose;

    public IReadOnlyList<RuntimeAnimation> Animations { get; }
    public int Current { get; private set; }
    public float Time { get; private set; }
    public bool Playing { get; set; } = true;

    public AnimationPlayback(GltfModel model)
    {
        Animations = model.Animations;
        _pose = new ScenePose(model);
    }

    public bool HasAnimations => Animations.Count > 0;

    public RuntimeAnimation? Clip => HasAnimations ? Animations[Current] : null;

    public ScenePose Pose => _pose;

    /// <summary>Advances the clock, looping within the clip's duration.</summary>
    public void Advance(float deltaSeconds)
    {
        if (!Playing || Clip is not { } clip)
        {
            return;
        }

        Time += deltaSeconds;
        if (clip.Duration > 0f)
        {
            Time %= clip.Duration;
        }
        else
        {
            Time = 0f;
        }
    }

    /// <summary>Selects a clip by index (clamped) and restarts its clock.</summary>
    public void Select(int index)
    {
        if (!HasAnimations)
        {
            return;
        }

        Current = Math.Clamp(index, 0, Animations.Count - 1);
        Time = 0f;
    }

    public void Next()
    {
        if (HasAnimations)
        {
            Select((Current + 1) % Animations.Count);
        }
    }

    public void Previous()
    {
        if (HasAnimations)
        {
            Select((Current - 1 + Animations.Count) % Animations.Count);
        }
    }

    /// <summary>Applies the current clip (or the rest pose) to the scene.</summary>
    public void Apply()
    {
        _pose.Apply(Clip, Time);
    }

    /// <summary>One-line selector description for the HUD.</summary>
    public string Describe()
    {
        if (Clip is not { } clip)
        {
            return string.Empty;
        }

        var name = string.IsNullOrEmpty(clip.Name) ? "(unnamed)" : clip.Name;
        var state = Playing ? "playing" : "paused";
        return $"{Current + 1}/{Animations.Count}  \"{name}\"  {Time:F1}s / {clip.Duration:F1}s  {state}   "
            + "[space] pause  [left/right] change";
    }
}
