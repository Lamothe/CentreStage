namespace CentreStage;

/// <summary>
/// GLSL for the viewer. The fragment shader is a full metallic-roughness PBR implementation
/// (Cook-Torrance GGX) with the corpus's extension set folded in:
///   KHR_materials_specular / _ior      - F0 and F90 variation
///   KHR_materials_clearcoat            - second GGX lobe at F0 = 0.04
///   KHR_materials_sheen                - retro-reflective cloth lobe
///   KHR_materials_transmission         - refraction tint against the environment colour
///   KHR_materials_emissive_strength    - emissive scaling above 1.0
///   KHR_materials_pbrSpecularGlossiness- shaded natively (F0 = specular, roughness = 1 - gloss)
///   KHR_materials_unlit                - skips lighting entirely
///
/// Every texture slot is optional: sampling an unbound GL texture unit returns black, so
/// each texture() call is gated on its uHas* flag and falls back to the factor alone.
/// </summary>
internal static class ShaderSources
{
    public const string Vertex = """
        #version 330 core
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aNormal;
        layout (location = 2) in vec4 aTangent;
        layout (location = 3) in vec2 aTexCoord0;
        layout (location = 4) in vec2 aTexCoord1;
        layout (location = 5) in vec4 aColor;
        layout (location = 6) in vec4 aJoints;
        layout (location = 7) in vec4 aWeights;

        // Skinning palette: inverseBind x jointWorld per joint, uploaded per frame.
        const int MAX_JOINTS = 256;
        layout (std140) uniform JointBlock { mat4 uJoints[MAX_JOINTS]; };

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;
        uniform bool uSkinned;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec3 vTangent;
        out vec3 vBitangent;
        out vec2 vTexCoord0;
        out vec2 vTexCoord1;
        out vec4 vColor;

        void main()
        {
            vec3 localPosition = aPosition;
            vec3 localNormal = aNormal;
            vec3 localTangent = aTangent.xyz;
            if (uSkinned)
            {
                // Weights are pre-normalised on upload; dividing by their sum covers
                // the all-zero fallback (identity) without another branch.
                vec4 w = aWeights;
                float wsum = w.x + w.y + w.z + w.w;
                if (wsum > 0.0)
                {
                    mat4 skin = (w.x * uJoints[int(aJoints.x)]
                        + w.y * uJoints[int(aJoints.y)]
                        + w.z * uJoints[int(aJoints.z)]
                        + w.w * uJoints[int(aJoints.w)]) / wsum;
                    localPosition = (skin * vec4(aPosition, 1.0)).xyz;
                    localNormal = mat3(skin) * aNormal;
                    localTangent = mat3(skin) * aTangent.xyz;
                }
            }

            vec4 world = uModel * vec4(localPosition, 1.0);
            vWorldPos = world.xyz;
            mat3 normalMat = mat3(uModel);
            vNormal = normalize(normalMat * localNormal);
            vTangent = normalize(normalMat * localTangent);
            vBitangent = cross(vNormal, vTangent) * aTangent.w;
            vTexCoord0 = aTexCoord0;
            vTexCoord1 = aTexCoord1;
            vColor = aColor;
            gl_Position = uProjection * uView * world;
        }
        """;

    public const string Fragment = """
        #version 330 core
        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec3 vTangent;
        in vec3 vBitangent;
        in vec2 vTexCoord0;
        in vec2 vTexCoord1;
        in vec4 vColor;

        out vec4 FragColor;

        uniform vec3 uCameraPos;
        uniform vec3 uSkyColor;
        uniform vec3 uGroundColor;

        // Core metallic-roughness (KHR_materials_pbrSpecularGlossiness maps in too).
        uniform vec4 uBaseColorFactor;
        uniform float uMetallicFactor;
        uniform float uRoughnessFactor;
        uniform float uSpecularFactor;        // KHR_materials_specular
        uniform vec3 uSpecularColorFactor;
        uniform float uIor;                   // KHR_materials_ior
        uniform float uTransmissionFactor;    // KHR_materials_transmission
        uniform float uClearcoatFactor;       // KHR_materials_clearcoat
        uniform float uClearcoatRoughnessFactor;
        uniform vec3 uSheenColorFactor;       // KHR_materials_sheen
        uniform float uSheenRoughnessFactor;
        uniform vec3 uEmissiveFactor;
        uniform float uEmissiveStrength;      // KHR_materials_emissive_strength
        uniform float uNormalScale;
        uniform float uOcclusionStrength;
        uniform int uAlphaMode;               // 0 opaque, 1 mask, 2 blend
        uniform float uAlphaCutoff;
        uniform bool uUnlit;
        uniform bool uSpecularGlossiness;
        uniform float uSgGlossinessFactor;
        uniform vec3 uSgSpecularFactor;

        uniform sampler2D uBaseColorTex;
        uniform sampler2D uMetallicRoughnessTex;
        uniform sampler2D uNormalTex;
        uniform sampler2D uOcclusionTex;
        uniform sampler2D uEmissiveTex;
        uniform sampler2D uSpecularTex;
        uniform sampler2D uSpecularColorTex;
        uniform sampler2D uTransmissionTex;
        uniform sampler2D uClearcoatTex;
        uniform sampler2D uClearcoatRoughnessTex;
        uniform sampler2D uSheenColorTex;
        uniform sampler2D uSheenRoughnessTex;

        uniform int uHasBaseColorTex;
        uniform int uHasMetallicRoughnessTex;
        uniform int uHasNormalTex;
        uniform int uHasOcclusionTex;
        uniform int uHasEmissiveTex;
        uniform int uHasSpecularTex;
        uniform int uHasSpecularColorTex;
        uniform int uHasTransmissionTex;
        uniform int uHasClearcoatTex;
        uniform int uHasClearcoatRoughnessTex;
        uniform int uHasSheenColorTex;
        uniform int uHasSheenRoughnessTex;

        // KHR_texture_transform per slot: uv' = mat3 * vec3(uvSet, 1), including offset
        // in the matrix's third column. uXxxSet picks TEXCOORD_0 vs TEXCOORD_1.
        uniform mat3 uBaseColorUv;
        uniform mat3 uMetallicRoughnessUv;
        uniform mat3 uNormalUv;
        uniform mat3 uOcclusionUv;
        uniform mat3 uEmissiveUv;
        uniform mat3 uSpecularUv;
        uniform mat3 uSpecularColorUv;
        uniform mat3 uTransmissionUv;
        uniform mat3 uClearcoatUv;
        uniform mat3 uClearcoatRoughnessUv;
        uniform mat3 uSheenColorUv;
        uniform mat3 uSheenRoughnessUv;

        uniform int uBaseColorSet;
        uniform int uMetallicRoughnessSet;
        uniform int uNormalSet;
        uniform int uOcclusionSet;
        uniform int uEmissiveSet;
        uniform int uSpecularSet;
        uniform int uSpecularColorSet;
        uniform int uTransmissionSet;
        uniform int uClearcoatSet;
        uniform int uClearcoatRoughnessSet;
        uniform int uSheenColorSet;
        uniform int uSheenRoughnessSet;

        // Up to 4 KHR_lights_punctual lights, uploaded per frame by the renderer.
        uniform int uLightCount;
        uniform vec3 uLightPos[4];
        uniform vec3 uLightDir[4];
        uniform vec3 uLightColor[4];
        uniform float uLightIntensity[4];
        uniform float uLightRange[4];
        uniform float uLightDirectional[4];
        uniform float uLightSpot[4];
        uniform float uLightInnerCos[4];
        uniform float uLightOuterCos[4];

        const float PI = 3.141592653589793;

        /// Exact inverse of the GPU's GL_SRGB8_ALPHA8 decode (piecewise), so a texture
        /// that round-trips through upload and output lands back on its authored value.
        vec3 linearToSrgb(vec3 c)
        {
            c = max(c, 0.0);
            vec3 lo = c * 12.92;
            vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
            return mix(hi, lo, lessThanEqual(c, vec3(0.0031308)));
        }

        vec2 uvFor(int set, mat3 transform)
        {
            return (transform * vec3(set == 1 ? vTexCoord1 : vTexCoord0, 1.0)).xy;
        }

        vec3 normalSample(vec2 uv)
        {
            vec3 n = texture(uNormalTex, uv).xyz * 2.0 - 1.0;
            n.xy *= uNormalScale;
            return normalize(vTangent * n.x + vBitangent * n.y + vNormal * n.z);
        }

        float distributionGGX(float NoH, float roughness)
        {
            float a = roughness * roughness;
            float a2 = a * a;
            float d = NoH * NoH * (a2 - 1.0) + 1.0;
            return a2 / max(PI * d * d, 1e-6);
        }

        float geometrySchlickGGX(float NoV, float roughness)
        {
            float r = roughness + 1.0;
            float k = (r * r) / 8.0;
            return NoV / (NoV * (1.0 - k) + k);
        }

        float geometrySmith(float NoV, float NoL, float roughness)
        {
            return geometrySchlickGGX(NoV, roughness) * geometrySchlickGGX(NoL, roughness);
        }

        vec3 fresnelSchlick(float VoH, vec3 f0)
        {
            return f0 + (1.0 - f0) * pow(clamp(1.0 - VoH, 0.0, 1.0), 5.0);
        }

        void main()
        {
            vec2 baseUv = uvFor(uBaseColorSet, uBaseColorUv);
            vec4 base = uBaseColorFactor * vColor;
            if (uHasBaseColorTex != 0)
            {
                base *= texture(uBaseColorTex, baseUv);
            }

            if (uAlphaMode == 1 && base.a < uAlphaCutoff)
            {
                discard;
            }

            vec3 n = uHasNormalTex != 0
                ? normalSample(uvFor(uNormalSet, uNormalUv))
                : normalize(vNormal);
            vec3 v = normalize(uCameraPos - vWorldPos);
            float NoV = max(dot(n, v), 1e-4);

            // Ambient from a simple hemispheric sky, standing in for a full IBL probe.
            vec3 ambient = mix(uGroundColor, uSkyColor, n.y * 0.5 + 0.5);

            vec3 emissive = uEmissiveFactor * uEmissiveStrength;
            if (uHasEmissiveTex != 0)
            {
                emissive *= texture(uEmissiveTex, uvFor(uEmissiveSet, uEmissiveUv)).rgb;
            }

            if (uUnlit)
            {
                // Unlit displays the base colour as authored: the sampled sRGB texture is
                // linear here, so encode it back out rather than lighting it. Emissive is
                // additive and allowed above 1 — the encode passes HDR values through the
                // piecewise curve without pre-clipping.
                vec3 authored = linearToSrgb(base.rgb + emissive);
                FragColor = vec4(authored, base.a);
                return;
            }

            float metallic;
            float roughness;
            vec3 f0;
            float f90 = 1.0;

            if (uSpecularGlossiness)
            {
                // KHR_materials_pbrSpecularGlossiness: diffuse is albedo, specular is F0,
                // glossiness (factor * texture alpha) inverts to roughness.
                vec3 specular = uSgSpecularFactor;
                if (uHasSpecularTex != 0)
                {
                    specular *= texture(uSpecularTex, uvFor(uSpecularSet, uSpecularUv)).rgb;
                }

                float gloss = uSgGlossinessFactor;
                if (uHasMetallicRoughnessTex != 0)
                {
                    gloss *= texture(uMetallicRoughnessTex, uvFor(uMetallicRoughnessSet, uMetallicRoughnessUv)).a;
                }

                roughness = clamp(1.0 - gloss, 0.04, 1.0);
                metallic = 0.0;
                f0 = specular;
            }
            else
            {
                vec2 mr = vec2(1.0);
                if (uHasMetallicRoughnessTex != 0)
                {
                    mr = texture(uMetallicRoughnessTex, uvFor(uMetallicRoughnessSet, uMetallicRoughnessUv)).bg;
                }

                metallic = clamp(uMetallicFactor * mr.x, 0.0, 1.0);
                roughness = clamp(uRoughnessFactor * mr.y, 0.04, 1.0);

                // KHR_materials_specular + _ior modulate dielectric F0 and grazing F90.
                float dielectricF0 = pow((uIor - 1.0) / (uIor + 1.0), 2.0);
                float specularW = uSpecularFactor;
                if (uHasSpecularTex != 0)
                {
                    specularW *= texture(uSpecularTex, uvFor(uSpecularSet, uSpecularUv)).a;
                }

                vec3 specularColor = uSpecularColorFactor;
                if (uHasSpecularColorTex != 0)
                {
                    specularColor *= texture(uSpecularColorTex, uvFor(uSpecularColorSet, uSpecularColorUv)).rgb;
                }

                vec3 dielectric = vec3(dielectricF0) * specularColor * specularW;
                f0 = mix(dielectric, base.rgb, metallic);
                f90 = mix(clamp(specularW * 20.0, 0.0, 1.0), 1.0, metallic);
            }

            float transmission = uTransmissionFactor;
            if (uHasTransmissionTex != 0)
            {
                transmission *= texture(uTransmissionTex, uvFor(uTransmissionSet, uTransmissionUv)).r;
            }

            float clearcoat = uClearcoatFactor;
            if (uHasClearcoatTex != 0)
            {
                clearcoat *= texture(uClearcoatTex, uvFor(uClearcoatSet, uClearcoatUv)).r;
            }

            vec3 sheenColor = uSheenColorFactor;
            if (uHasSheenColorTex != 0)
            {
                sheenColor *= texture(uSheenColorTex, uvFor(uSheenColorSet, uSheenColorUv)).rgb;
            }

            float sheenRough = uSheenRoughnessFactor;
            if (uHasSheenRoughnessTex != 0)
            {
                // KHR_materials_sheen: roughness lives in the alpha channel (the one
                // glTF roughness slot that is not green).
                sheenRough *= texture(uSheenRoughnessTex, uvFor(uSheenRoughnessSet, uSheenRoughnessUv)).a;
            }

            vec3 color = vec3(0.0);
            for (int i = 0; i < 4; i++)
            {
                if (i >= uLightCount)
                {
                    break;
                }

                vec3 l = uLightPos[i] - vWorldPos;
                float dist = length(l);
                if (uLightDirectional[i] < 0.5 && uLightRange[i] > 0.0 && dist > uLightRange[i])
                {
                    continue;
                }

                l = uLightDirectional[i] > 0.5 ? normalize(-uLightDir[i]) : l / max(dist, 1e-4);

                float attenuation = 1.0;
                if (uLightDirectional[i] < 0.5 && uLightRange[i] > 0.0)
                {
                    // KHR_lights_punctual windowed range falloff.
                    float t = clamp(dist / uLightRange[i], 0.0, 1.0);
                    attenuation = max(1.0 - t * t * t * t, 0.0) / max(dist * dist, 1e-4);
                }

                // KHR_lights_punctual spot cone between inner (full) and outer (zero).
                if (uLightSpot[i] > 0.5)
                {
                    float cosAngle = dot(-l, normalize(uLightDir[i]));
                    float cone = clamp(
                        (cosAngle - uLightOuterCos[i]) / max(uLightInnerCos[i] - uLightOuterCos[i], 1e-4),
                        0.0, 1.0);
                    attenuation *= cone * cone;
                }

                float NoL = max(dot(n, l), 0.0);
                if (NoL <= 0.0)
                {
                    continue;
                }

                vec3 h = normalize(v + l);
                float VoH = max(dot(v, h), 0.0);
                float NoH = max(dot(n, h), 0.0);

                float ndf = distributionGGX(NoH, roughness);
                float geo = geometrySmith(NoV, NoL, roughness);
                vec3 fresnel = fresnelSchlick(VoH, f0) * f90;

                vec3 specular = (ndf * geo * fresnel) / max(4.0 * NoV * NoL, 1e-4);
                vec3 kd = (1.0 - fresnel) * (1.0 - metallic);
                vec3 radiance = uLightColor[i] * uLightIntensity[i] * attenuation;

                color += (kd * base.rgb / PI + specular) * radiance * NoL;

                // KHR_materials_clearcoat: thin glossy layer over the base lobe.
                if (clearcoat > 0.0)
                {
                    float ccRough = clamp(uClearcoatRoughnessFactor, 0.04, 1.0);
                    if (uHasClearcoatRoughnessTex != 0)
                    {
                        ccRough = clamp(ccRough * texture(uClearcoatRoughnessTex, uvFor(uClearcoatRoughnessSet, uClearcoatRoughnessUv)).g, 0.04, 1.0);
                    }

                    float ccNdF = distributionGGX(NoH, ccRough);
                    float ccGeo = geometrySmith(NoV, NoL, ccRough);
                    vec3 ccFres = fresnelSchlick(VoH, vec3(0.04)) * clearcoat;
                    vec3 ccSpec = (ccNdF * ccGeo * ccFres) / max(4.0 * NoV * NoL, 1e-4);
                    color += ccSpec * radiance * NoL;
                }

                // KHR_materials_sheen: retro-reflective cloth highlight near grazing angles.
                if (sheenColor.r + sheenColor.g + sheenColor.b > 0.0)
                {
                    float sheen = pow(1.0 - VoH, max(1.0 / max(sheenRough, 0.02), 1.0));
                    color += sheenColor * sheen * radiance * NoL;
                }
            }

            color += ambient * base.rgb * (1.0 - metallic * 0.6);

            // KHR_materials_transmission: what refracts through is approximated as the
            // environment colour, tinted by the base colour.
            if (transmission > 0.0)
            {
                vec3 refracted = mix(uSkyColor, base.rgb, 0.35);
                color = mix(color, refracted * base.rgb * 1.4, clamp(transmission, 0.0, 1.0));
            }

            if (uOcclusionStrength > 0.0 && uHasOcclusionTex != 0)
            {
                float ao = texture(uOcclusionTex, uvFor(uOcclusionSet, uOcclusionUv)).r;
                color *= mix(1.0, ao, uOcclusionStrength);
            }

            color += emissive;

            // ACES-ish tone mapping keeps bright emissives and speculars from clipping.
            color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
            color = linearToSrgb(color);

            FragColor = vec4(color, base.a);
        }
        """;
}
