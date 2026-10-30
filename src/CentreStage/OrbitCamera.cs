using System.Numerics;

namespace CentreStage;

/// <summary>Orbit camera: target-centric yaw/pitch/distance with pan and zoom.</summary>
internal sealed class OrbitCamera
{
    public Vector3 Target = Vector3.Zero;
    public float Distance = 5f;
    public float Yaw = -MathF.PI / 4f;
    public float Pitch = -MathF.PI / 6f;
    public float FieldOfView = MathF.PI / 4f;
    public float Near = 0.01f;
    public float Far = 1000f;

    public Vector3 Position
    {
        get
        {
            var (sy, cy) = MathF.SinCos(Yaw);
            var (sp, cp) = MathF.SinCos(Pitch);
            return Target + new Vector3(cy * cp, -sp, sy * cp) * Distance;
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    private float _frameAspect = 1.6f;

    /// <summary>The window aspect the model was framed for.</summary>
    public void SetFrameAspect(float aspect) => _frameAspect = MathF.Max(aspect, 1e-3f);

    public Matrix4x4 Projection(float aspect)
    {
        _frameAspect = MathF.Max(aspect, 1e-3f);
        return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, _frameAspect, Near, Far);
    }

    public void Orbit(float dx, float dy)
    {
        Yaw += dx;
        Pitch = Math.Clamp(Pitch + dy, -1.55f, 1.55f);
    }

    public void Zoom(float delta)
    {
        Distance = Math.Clamp(Distance * MathF.Exp(-delta), 0.05f, 5000f);
        UpdateClip();
    }

    public void Pan(float dx, float dy)
    {
        var view = View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        Target += (right * dx + up * dy) * Distance * 0.0015f;
    }

    /// <summary>
    /// Frames a bounding box so the model fills the view at the default
    /// orbit pose (the demo needs fine detail to be visible: the old
    /// bbox-diagonal fit left a long model at ~28% of the frame). The fit
    /// projects all eight box corners into the default camera orientation
    /// and solves for the distance that contains them - exact at startup.
    /// Orbiting away from the default pose can clip extremes; zoom out.
    /// </summary>
    public void FrameBounds(Vector3 min, Vector3 max)
    {
        Target = (min + max) * 0.5f;
        var size = max - min;
        if (size.Length() < 1e-4f)
        {
            size = Vector3.One;
        }

        // Camera-space extents of the box corners at the default yaw/pitch.
        var (sy, cy) = MathF.SinCos(Yaw);
        var (sp, cp) = MathF.SinCos(Pitch);
        var forward = new Vector3(cy * cp, -sp, sy * cp);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Cross(forward, right);
        var half = size * 0.5f;
        float halfRight = 0, halfUp = 0, halfFwd = 0;
        for (var i = 0; i < 8; ++i)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
            halfRight = MathF.Max(halfRight, MathF.Abs(Vector3.Dot(corner, right)));
            halfUp = MathF.Max(halfUp, MathF.Abs(Vector3.Dot(corner, up)));
            halfFwd = MathF.Max(halfFwd, MathF.Abs(Vector3.Dot(corner, forward)));
        }

        var t = MathF.Tan(FieldOfView * 0.5f);
        var aspect = MathF.Max(_frameAspect, 0.5f);
        var d = MathF.Max(halfUp / t, halfRight / (t * aspect));
        if (d < 1e-3f)
        {
            d = 1f;
        }
        Distance = d * 1.02f;
        _frustumHalfDepth = MathF.Max(halfFwd, 0.01f);
        UpdateClip();
    }

    private float _frustumHalfDepth = 1f;

    private void UpdateClip()
    {
        Near = MathF.Max(Distance * 0.001f, 0.001f);
        Far = MathF.Max((Distance + _frustumHalfDepth) * 2f, 100f);
    }
}
