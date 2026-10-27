using System.Text.Json.Serialization;

namespace CentreStage.Core.Schema;

// Typed DTOs for the glTF 2.0 JSON manifest. Properties that the spec gives a default
// value to are nullable here; defaults are applied at use sites (see GltfModel).

public sealed class GltfRoot
{
    [JsonPropertyName("asset")] public GltfAsset? Asset { get; set; }
    [JsonPropertyName("scene")] public int? Scene { get; set; }
    [JsonPropertyName("scenes")] public List<GltfScene>? Scenes { get; set; }
    [JsonPropertyName("nodes")] public List<GltfNode>? Nodes { get; set; }
    [JsonPropertyName("meshes")] public List<GltfMesh>? Meshes { get; set; }
    [JsonPropertyName("accessors")] public List<GltfAccessor>? Accessors { get; set; }
    [JsonPropertyName("bufferViews")] public List<GltfBufferView>? BufferViews { get; set; }
    [JsonPropertyName("buffers")] public List<GltfBuffer>? Buffers { get; set; }
    [JsonPropertyName("materials")] public List<GltfMaterial>? Materials { get; set; }
    [JsonPropertyName("textures")] public List<GltfTexture>? Textures { get; set; }
    [JsonPropertyName("images")] public List<GltfImage>? Images { get; set; }
    [JsonPropertyName("samplers")] public List<GltfSampler>? Samplers { get; set; }
    [JsonPropertyName("skins")] public List<GltfSkin>? Skins { get; set; }
    [JsonPropertyName("animations")] public List<GltfAnimation>? Animations { get; set; }
    [JsonPropertyName("extensionsUsed")] public List<string>? ExtensionsUsed { get; set; }
    [JsonPropertyName("extensionsRequired")] public List<string>? ExtensionsRequired { get; set; }
    [JsonPropertyName("extensions")] public GltfRootExtensions? Extensions { get; set; }
}

public sealed class GltfAsset
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("generator")] public string? Generator { get; set; }
}

public sealed class GltfScene
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("nodes")] public List<int>? Nodes { get; set; }
}

public sealed class GltfNode
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("children")] public List<int>? Children { get; set; }
    [JsonPropertyName("mesh")] public int? Mesh { get; set; }
    [JsonPropertyName("skin")] public int? Skin { get; set; }
    [JsonPropertyName("camera")] public int? Camera { get; set; }
    [JsonPropertyName("matrix")] public float[]? Matrix { get; set; }
    [JsonPropertyName("translation")] public float[]? Translation { get; set; }
    [JsonPropertyName("rotation")] public float[]? Rotation { get; set; }
    [JsonPropertyName("scale")] public float[]? Scale { get; set; }
    [JsonPropertyName("extensions")] public GltfNodeExtensions? Extensions { get; set; }
}

public sealed class GltfMesh
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("primitives")] public List<GltfPrimitive>? Primitives { get; set; }
}

public sealed class GltfPrimitive
{
    [JsonPropertyName("attributes")] public Dictionary<string, int>? Attributes { get; set; }
    [JsonPropertyName("indices")] public int? Indices { get; set; }
    [JsonPropertyName("material")] public int? Material { get; set; }
    /// <summary>4 = triangles (spec default), 5 = triangle strip, 6 = triangle fan, others = points/lines.</summary>
    [JsonPropertyName("mode")] public int? Mode { get; set; }
}

public sealed class GltfAccessor
{
    [JsonPropertyName("bufferView")] public int? BufferView { get; set; }
    [JsonPropertyName("byteOffset")] public int? ByteOffset { get; set; }
    [JsonPropertyName("componentType")] public int ComponentType { get; set; }
    [JsonPropertyName("normalized")] public bool? Normalized { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("max")] public float[]? Max { get; set; }
    [JsonPropertyName("min")] public float[]? Min { get; set; }
    [JsonPropertyName("sparse")] public GltfSparse? Sparse { get; set; }
}

public sealed class GltfSparse
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("indices")] public GltfSparseIndices? Indices { get; set; }
    [JsonPropertyName("values")] public GltfSparseValues? Values { get; set; }
}

public sealed class GltfSparseIndices
{
    [JsonPropertyName("bufferView")] public int BufferView { get; set; }
    [JsonPropertyName("byteOffset")] public int? ByteOffset { get; set; }
    [JsonPropertyName("componentType")] public int ComponentType { get; set; }
}

public sealed class GltfSparseValues
{
    [JsonPropertyName("bufferView")] public int BufferView { get; set; }
    [JsonPropertyName("byteOffset")] public int? ByteOffset { get; set; }
}

public sealed class GltfBufferView
{
    [JsonPropertyName("buffer")] public int Buffer { get; set; }
    [JsonPropertyName("byteOffset")] public int? ByteOffset { get; set; }
    [JsonPropertyName("byteLength")] public int ByteLength { get; set; }
    [JsonPropertyName("byteStride")] public int? ByteStride { get; set; }
}

public sealed class GltfBuffer
{
    [JsonPropertyName("uri")] public string? Uri { get; set; }
    [JsonPropertyName("byteLength")] public int ByteLength { get; set; }
}

public sealed class GltfTexture
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sampler")] public int? Sampler { get; set; }
    [JsonPropertyName("source")] public int? Source { get; set; }
}

public sealed class GltfImage
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("uri")] public string? Uri { get; set; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("bufferView")] public int? BufferView { get; set; }
}

public sealed class GltfSampler
{
    /// <summary>9728 = nearest, 9729 = linear.</summary>
    [JsonPropertyName("magFilter")] public int? MagFilter { get; set; }
    /// <summary>9728..9729, 9984..9987 (mipmap variants).</summary>
    [JsonPropertyName("minFilter")] public int? MinFilter { get; set; }
    /// <summary>33071 = clamp, 33648 = mirrored repeat, 10497 = repeat (default).</summary>
    [JsonPropertyName("wrapS")] public int? WrapS { get; set; }
    [JsonPropertyName("wrapT")] public int? WrapT { get; set; }
}

public sealed class GltfMaterial
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("pbrMetallicRoughness")] public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; set; }
    [JsonPropertyName("normalTexture")] public GltfNormalTextureInfo? NormalTexture { get; set; }
    [JsonPropertyName("occlusionTexture")] public GltfOcclusionTextureInfo? OcclusionTexture { get; set; }
    [JsonPropertyName("emissiveTexture")] public GltfTextureInfo? EmissiveTexture { get; set; }
    [JsonPropertyName("emissiveFactor")] public float[]? EmissiveFactor { get; set; }
    /// <summary>"OPAQUE" (default), "MASK" or "BLEND".</summary>
    [JsonPropertyName("alphaMode")] public string? AlphaMode { get; set; }
    [JsonPropertyName("alphaCutoff")] public float? AlphaCutoff { get; set; }
    [JsonPropertyName("doubleSided")] public bool? DoubleSided { get; set; }
    [JsonPropertyName("extensions")] public GltfMaterialExtensions? Extensions { get; set; }
}

public sealed class GltfPbrMetallicRoughness
{
    [JsonPropertyName("baseColorFactor")] public float[]? BaseColorFactor { get; set; }
    [JsonPropertyName("baseColorTexture")] public GltfTextureInfo? BaseColorTexture { get; set; }
    [JsonPropertyName("metallicFactor")] public float? MetallicFactor { get; set; }
    [JsonPropertyName("roughnessFactor")] public float? RoughnessFactor { get; set; }
    [JsonPropertyName("metallicRoughnessTexture")] public GltfTextureInfo? MetallicRoughnessTexture { get; set; }
}

public class GltfTextureInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("texCoord")] public int? TexCoord { get; set; }
    [JsonPropertyName("extensions")] public GltfTextureInfoExtensions? Extensions { get; set; }
}

public sealed class GltfNormalTextureInfo : GltfTextureInfo
{
    [JsonPropertyName("scale")] public float? Scale { get; set; }
}

public sealed class GltfOcclusionTextureInfo : GltfTextureInfo
{
    [JsonPropertyName("strength")] public float? Strength { get; set; }
}

// --- Extension objects -------------------------------------------------
// Only the extensions seen in (and relevant to) the real corpus are typed;
// anything else is ignored, and required-but-unsupported extensions are surfaced.

public sealed class GltfRootExtensions
{
    [JsonPropertyName("KHR_lights_punctual")] public GltfKhrLightsPunctual? LightsPunctual { get; set; }
}

public sealed class GltfKhrLightsPunctual
{
    [JsonPropertyName("lights")] public List<GltfKhrLight>? Lights { get; set; }
}

public sealed class GltfKhrLight
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>"directional", "point" or "spot".</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("color")] public float[]? Color { get; set; }
    [JsonPropertyName("intensity")] public float? Intensity { get; set; }
    [JsonPropertyName("range")] public float? Range { get; set; }
    [JsonPropertyName("spot")] public GltfKhrLightSpot? Spot { get; set; }
}

public sealed class GltfKhrLightSpot
{
    [JsonPropertyName("innerConeAngle")] public float? InnerConeAngle { get; set; }
    [JsonPropertyName("outerConeAngle")] public float? OuterConeAngle { get; set; }
}

public sealed class GltfNodeExtensions
{
    [JsonPropertyName("KHR_lights_punctual")] public GltfKhrLightReference? LightsPunctual { get; set; }
}

public sealed class GltfKhrLightReference
{
    [JsonPropertyName("light")] public int Light { get; set; }
}

public sealed class GltfMaterialExtensions
{
    [JsonPropertyName("KHR_materials_specular")] public GltfKhrMaterialsSpecular? Specular { get; set; }
    [JsonPropertyName("KHR_materials_ior")] public GltfKhrMaterialsIor? Ior { get; set; }
    [JsonPropertyName("KHR_materials_transmission")] public GltfKhrMaterialsTransmission? Transmission { get; set; }
    [JsonPropertyName("KHR_materials_clearcoat")] public GltfKhrMaterialsClearcoat? Clearcoat { get; set; }
    [JsonPropertyName("KHR_materials_sheen")] public GltfKhrMaterialsSheen? Sheen { get; set; }
    [JsonPropertyName("KHR_materials_emissive_strength")] public GltfKhrMaterialsEmissiveStrength? EmissiveStrength { get; set; }
    [JsonPropertyName("KHR_materials_unlit")] public GltfKhrMaterialsUnlit? Unlit { get; set; }
    [JsonPropertyName("KHR_materials_pbrSpecularGlossiness")] public GltfKhrMaterialsPbrSpecularGlossiness? PbrSpecularGlossiness { get; set; }
}

public sealed class GltfKhrMaterialsSpecular
{
    [JsonPropertyName("specularFactor")] public float? SpecularFactor { get; set; }
    [JsonPropertyName("specularTexture")] public GltfTextureInfo? SpecularTexture { get; set; }
    [JsonPropertyName("specularColorFactor")] public float[]? SpecularColorFactor { get; set; }
    [JsonPropertyName("specularColorTexture")] public GltfTextureInfo? SpecularColorTexture { get; set; }
}

public sealed class GltfKhrMaterialsIor
{
    [JsonPropertyName("ior")] public float? Ior { get; set; }
}

public sealed class GltfKhrMaterialsTransmission
{
    [JsonPropertyName("transmissionFactor")] public float? TransmissionFactor { get; set; }
    [JsonPropertyName("transmissionTexture")] public GltfTextureInfo? TransmissionTexture { get; set; }
}

public sealed class GltfKhrMaterialsClearcoat
{
    [JsonPropertyName("clearcoatFactor")] public float? ClearcoatFactor { get; set; }
    [JsonPropertyName("clearcoatTexture")] public GltfTextureInfo? ClearcoatTexture { get; set; }
    [JsonPropertyName("clearcoatRoughnessFactor")] public float? ClearcoatRoughnessFactor { get; set; }
    [JsonPropertyName("clearcoatRoughnessTexture")] public GltfTextureInfo? ClearcoatRoughnessTexture { get; set; }
    [JsonPropertyName("clearcoatNormalTexture")] public GltfNormalTextureInfo? ClearcoatNormalTexture { get; set; }
}

public sealed class GltfKhrMaterialsSheen
{
    [JsonPropertyName("sheenColorFactor")] public float[]? SheenColorFactor { get; set; }
    [JsonPropertyName("sheenColorTexture")] public GltfTextureInfo? SheenColorTexture { get; set; }
    [JsonPropertyName("sheenRoughnessFactor")] public float? SheenRoughnessFactor { get; set; }
    [JsonPropertyName("sheenRoughnessTexture")] public GltfTextureInfo? SheenRoughnessTexture { get; set; }
}

public sealed class GltfKhrMaterialsEmissiveStrength
{
    [JsonPropertyName("emissiveStrength")] public float? EmissiveStrength { get; set; }
}

public sealed class GltfKhrMaterialsUnlit
{
    // Presence-only extension; no properties.
}

public sealed class GltfKhrMaterialsPbrSpecularGlossiness
{
    [JsonPropertyName("diffuseFactor")] public float[]? DiffuseFactor { get; set; }
    [JsonPropertyName("diffuseTexture")] public GltfTextureInfo? DiffuseTexture { get; set; }
    [JsonPropertyName("specularFactor")] public float[]? SpecularFactor { get; set; }
    [JsonPropertyName("specularGlossinessTexture")] public GltfTextureInfo? SpecularGlossinessTexture { get; set; }
    [JsonPropertyName("glossinessFactor")] public float? GlossinessFactor { get; set; }
}

public sealed class GltfTextureInfoExtensions
{
    [JsonPropertyName("KHR_texture_transform")] public GltfKhrTextureTransform? TextureTransform { get; set; }
}

public sealed class GltfKhrTextureTransform
{
    [JsonPropertyName("offset")] public float[]? Offset { get; set; }
    [JsonPropertyName("rotation")] public float? Rotation { get; set; }
    [JsonPropertyName("scale")] public float[]? Scale { get; set; }
    [JsonPropertyName("texCoord")] public int? TexCoord { get; set; }
}

// --- Animation / skin (parsed so structure is visible; playback is future work) ---

public sealed class GltfSkin
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("inverseBindMatrices")] public int? InverseBindMatrices { get; set; }
    [JsonPropertyName("skeleton")] public int? Skeleton { get; set; }
    [JsonPropertyName("joints")] public List<int>? Joints { get; set; }
}

public sealed class GltfAnimation
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("channels")] public List<GltfAnimationChannel>? Channels { get; set; }
    [JsonPropertyName("samplers")] public List<GltfAnimationSampler>? Samplers { get; set; }
}

public sealed class GltfAnimationChannel
{
    [JsonPropertyName("sampler")] public int Sampler { get; set; }
    [JsonPropertyName("target")] public GltfAnimationTarget? Target { get; set; }
}

public sealed class GltfAnimationTarget
{
    [JsonPropertyName("node")] public int? Node { get; set; }
    /// <summary>"translation", "rotation", "scale" or "weights".</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }
}

public sealed class GltfAnimationSampler
{
    [JsonPropertyName("input")] public int Input { get; set; }
    [JsonPropertyName("output")] public int Output { get; set; }
    /// <summary>"LINEAR" (default), "STEP" or "CUBICSPLINE".</summary>
    [JsonPropertyName("interpolation")] public string? Interpolation { get; set; }
}
