using System.Numerics;
using CentreStage.Core.Schema;

namespace CentreStage.Core;

/// <summary>A texture reference as used by one material slot, with its UV transform.</summary>
public sealed class TextureRef
{
    public required int TextureIndex { get; init; }
    /// <summary>UV set to sample (0 = TEXCOORD_0, 1 = TEXCOORD_1), unless overridden by a transform.</summary>
    public int TexCoord { get; init; }
    public UvTransform Transform { get; init; }
}

/// <summary>KHR_texture_transform: uv' = offset + R(rotation) * (scale * uv).</summary>
public struct UvTransform
{
    public Vector2 Offset;
    public float Rotation;
    public Vector2 Scale;

    public static readonly UvTransform Identity = new() { Scale = Vector2.One };

    public bool IsIdentity => Offset == Vector2.Zero && Rotation == 0f && Scale == Vector2.One;

    public Vector2 Apply(Vector2 uv)
    {
        if (IsIdentity)
        {
            return uv;
        }

        var scaled = uv * Scale;
        var (s, c) = MathF.SinCos(Rotation);
        return new Vector2((c * scaled.X) - (s * scaled.Y), (s * scaled.X) + (c * scaled.Y)) + Offset;
    }
}

/// <summary>
/// Resolved material: core metallic-roughness plus the extension parameters the corpus uses.
/// The legacy pbrSpecularGlossiness model is kept alongside (not converted) so the shader can
/// shade it faithfully: spec-gloss is just F0 = specularColor, roughness = 1 - glossiness.
/// </summary>
public sealed class RuntimeMaterial
{
    public string? Name { get; init; }

    // core metallic-roughness
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;
    public TextureRef? BaseColorTexture { get; init; }
    public float MetallicFactor { get; init; } = 1f;
    public float RoughnessFactor { get; init; } = 1f;
    public TextureRef? MetallicRoughnessTexture { get; init; }
    public TextureRef? NormalTexture { get; init; }
    public float NormalScale { get; init; } = 1f;
    public TextureRef? OcclusionTexture { get; init; }
    public float OcclusionStrength { get; init; } = 1f;
    public Vector3 EmissiveFactor { get; init; }
    public TextureRef? EmissiveTexture { get; init; }
    public float EmissiveStrength { get; init; } = 1f;
    public AlphaMode AlphaMode { get; init; } = AlphaMode.Opaque;
    public float AlphaCutoff { get; init; } = 0.5f;
    public bool DoubleSided { get; init; }
    public bool Unlit { get; init; }

    // KHR_materials_specular / KHR_materials_ior
    public float SpecularFactor { get; init; } = 1f;
    public Vector3 SpecularColorFactor { get; init; } = Vector3.One;
    public TextureRef? SpecularTexture { get; init; }
    public TextureRef? SpecularColorTexture { get; init; }
    public float Ior { get; init; } = 1.5f;

    // KHR_materials_transmission
    public float TransmissionFactor { get; init; }
    public TextureRef? TransmissionTexture { get; init; }

    // KHR_materials_clearcoat
    public float ClearcoatFactor { get; init; }
    public float ClearcoatRoughnessFactor { get; init; }
    public TextureRef? ClearcoatTexture { get; init; }
    public TextureRef? ClearcoatRoughnessTexture { get; init; }
    public TextureRef? ClearcoatNormalTexture { get; init; }

    // KHR_materials_sheen
    public Vector3 SheenColorFactor { get; init; }
    public float SheenRoughnessFactor { get; init; } = 1f;
    public TextureRef? SheenColorTexture { get; init; }
    public TextureRef? SheenRoughnessTexture { get; init; }

    // legacy KHR_materials_pbrSpecularGlossiness
    public bool IsSpecularGlossiness { get; init; }
    public Vector4 SgDiffuseFactor { get; init; } = Vector4.One;
    public TextureRef? SgDiffuseTexture { get; init; }
    public Vector3 SgSpecularFactor { get; init; } = Vector3.One;
    public float SgGlossinessFactor { get; init; } = 1f;
    public TextureRef? SgSpecularGlossinessTexture { get; init; }
}

public enum AlphaMode
{
    Opaque,
    Mask,
    Blend,
}

/// <summary>Decoded geometry, one per glTF primitive.</summary>
public sealed class RuntimePrimitive
{
    public required Vector3[] Positions { get; init; }
    public Vector3[]? Normals { get; init; }
    public Vector4[]? Tangents { get; init; }
    public Vector2[]? TexCoords0 { get; init; }
    public Vector2[]? TexCoords1 { get; init; }
    public Vector4[]? Colors { get; init; }
    /// <summary>JOINTS_0: up to 4 joint indices per vertex (skin.joints array positions).</summary>
    public Vector4[]? Joints { get; init; }
    /// <summary>WEIGHTS_0: per-vertex skinning weights (normalised to sum 1).</summary>
    public Vector4[]? Weights { get; init; }
    public required uint[] Indices { get; init; }
    public int MaterialIndex { get; init; } = -1;
    /// <summary>4 = triangles (default). Other modes are preserved but not yet rendered.</summary>
    public int Mode { get; init; } = 4;
}

/// <summary>A skin: joint node references plus their inverse bind matrices.</summary>
public sealed class RuntimeSkin
{
    public string? Name { get; init; }
    /// <summary>Node indices of the joints, in skin.joints order.</summary>
    public required IReadOnlyList<int> JointNodes { get; init; }
    /// <summary>Inverse bind matrices, one per joint (identity when absent).</summary>
    public required IReadOnlyList<Matrix4x4> InverseBindMatrices { get; init; }
}

public sealed class RuntimeMesh
{
    public string? Name { get; init; }
    public required IReadOnlyList<RuntimePrimitive> Primitives { get; init; }
}

/// <summary>A scene node with its precomputed world transform.</summary>
public sealed class RuntimeNode
{
    public string? Name { get; init; }
    /// <summary>Index of this node in the manifest's nodes array.</summary>
    public int NodeIndex { get; init; }
    public required Matrix4x4 World { get; init; }
    public int MeshIndex { get; init; } = -1;
    public int LightIndex { get; init; } = -1;
    /// <summary>
    /// Skin index when this node carries a skinned mesh. Per the glTF spec the node's own
    /// transform is then ignored for the mesh; the skin's joint matrices define the pose.
    /// </summary>
    public int SkinIndex { get; init; } = -1;
}

public sealed class RuntimeTexture
{
    public required byte[] Data { get; init; }
    public string? MimeType { get; init; }
    public int WrapS { get; init; } = 10497;
    public int WrapT { get; init; } = 10497;
    public int MagFilter { get; init; } = 9729;
    public int MinFilter { get; init; } = 9987;
}

public sealed class RuntimeLight
{
    public string? Name { get; init; }
    public required LightType Type { get; init; }
    public Vector3 Color { get; init; } = Vector3.One;
    public float Intensity { get; init; } = 1f;
    public float Range { get; init; } = float.PositiveInfinity;
    public float InnerConeAngle { get; init; } = 0f;
    public float OuterConeAngle { get; init; } = MathF.PI / 4f;
    /// <summary>Position/direction derived from the referencing node's world transform.</summary>
    public Vector3 Position { get; init; }
    public Vector3 Direction { get; init; } = -Vector3.UnitZ;
}

public enum LightType
{
    Directional,
    Point,
    Spot,
}

/// <summary>Thrown when a file requires an extension that carries data we cannot decode.</summary>
public sealed class UnsupportedExtensionsException : IOException
{
    public IReadOnlyList<string> Extensions { get; }

    public UnsupportedExtensionsException(IReadOnlyList<string> extensions)
        : base($"Model requires unsupported extension(s): {string.Join(", ", extensions)}. "
            + "These carry structural data (compression/encoding) with no fallback in the file.")
    {
        Extensions = extensions;
    }
}
