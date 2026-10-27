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

    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, MathF.Max(aspect, 1e-3f), Near, Far);

    public void Orbit(float dx, float dy)
    {
        Yaw += dx;
        Pitch = Math.Clamp(Pitch + dy, -1.55f, 1.55f);
    }

    public void Zoom(float delta)
    {
        Distance = Math.Clamp(Distance * MathF.Exp(-delta), 0.05f, 5000f);
    }

    public void Pan(float dx, float dy)
    {
        var view = View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        Target += (right * dx + up * dy) * Distance * 0.0015f;
    }

    /// <summary>Frames a bounding box so the whole model is visible.</summary>
    public void FrameBounds(Vector3 min, Vector3 max)
    {
        Target = (min + max) * 0.5f;
        var radius = (max - min).Length() * 0.5f;
        if (radius < 1e-4f)
        {
            radius = 1f;
        }

        Distance = radius / MathF.Tan(FieldOfView * 0.5f) * 1.25f;
        Near = MathF.Max(radius * 0.001f, 0.001f);
        Far = MathF.Max(radius * 100f, 100f);
    }
}
