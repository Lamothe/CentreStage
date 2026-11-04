namespace CentreStage;

/// <summary>
/// Sampler-free, asset-independent studio lighting for GLSL 3.30. Append <see cref="Glsl"/>
/// after the shader's #version directive and before its callers. Directions are unit-length,
/// world-space, with +Y up; colours are linear radiance, not sRGB.
/// </summary>
/// <remarks>
/// This is an analytic approximation, not CPU-integrated IBL or a true cosine/GGX convolution.
/// Spherical-Gaussian softboxes are broadened while preserving their spherical mean. Diffuse
/// returns irradiance / pi (multiply by diffuse albedo, not another 1/pi); specular returns
/// approximate filtered radiance (multiply by F0 * studioBrdf(...).x + studioBrdf(...).y).
/// The near-neutral spherical mean is approximately 0.55 in every channel at every roughness.
/// No textures, uniforms, GL context, Bind or disposal are required. This class alone does not
/// change the renderer; callers must explicitly incorporate the GLSL into their shader.
/// </remarks>
internal static class StudioEnvironment
{
    public const string Glsl = """
        // Normalised spherical Gaussian: its integral over the sphere is 4*pi.
        // All sharpnesses used here are >= 0.23, avoiding cancellation at zero.
        float studioLobe(vec3 direction, vec3 axis, float sharpness)
        {
            float cosine = clamp(dot(direction, axis), -1.0, 1.0);
            return (2.0 * sharpness / (1.0 - exp(-2.0 * sharpness)))
                * exp(sharpness * (cosine - 1.0));
        }

        vec3 studioLighting(vec3 direction, vec3 sharpness)
        {
            // Grey room + broad upper key, upper rim and a subtle cool side fill.
            // These coefficients are mean radiance, NOT peak lobe intensities.
            return vec3(0.34)
                + vec3(0.11) * studioLobe(direction,
                    normalize(vec3(-0.6, 0.8, 0.3)), sharpness.x)
                + vec3(0.07) * studioLobe(direction,
                    normalize(vec3(0.5, 0.7, -0.6)), sharpness.y)
                + vec3(0.0282, 0.0297, 0.0321) * studioLobe(direction,
                    normalize(vec3(0.8, 0.25, 0.5)), sharpness.z);
        }

        vec3 studioDiffuse(vec3 n)
        {
            // Approximate clamped-cosine broadening of the same environment.
            // Preserves DC exactly; this is not exact hemispherical integration.
            vec3 sharpness = vec3(8.0, 5.0, 3.0);
            return studioLighting(n, sharpness / (vec3(1.0) + 0.32 * sharpness));
        }

        vec3 studioSpecular(vec3 r, float roughness)
        {
            float perceptualRoughness = clamp(roughness, 0.0, 1.0);
            float alpha = perceptualRoughness * perceptualRoughness;
            vec3 sharpness = vec3(8.0, 5.0, 3.0);
            // Heuristic GGX-like broadening, not a GGX prefilter. At zero roughness
            // this is exactly the unfiltered environment, without singularities.
            return studioLighting(r,
                sharpness / (vec3(1.0) + 4.0 * alpha * alpha * sharpness));
        }

        vec2 studioBrdf(float NoV, float roughness)
        {
            float nv = clamp(NoV, 0.0, 1.0);
            float pr = clamp(roughness, 0.0, 1.0);
            // Lazarov's environment BRDF fit (Epic, "Real Shading in Unreal Engine 4").
            // A two-coefficient split sum, not an integrated BRDF LUT; single scattering.
            vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
            vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
            vec4 t = pr * c0 + c1;
            float a004 = min(t.x * t.x, exp2(-9.28 * nv)) * t.x + t.y;
            vec2 fit = clamp(vec2(-1.04, 1.04) * a004 + t.zw, 0.0, 1.0);
            // Restore the exact Schlick mirror limit smoothly near roughness zero;
            // the original fit otherwise has a sizeable grazing error at that limit.
            float fc = pow(1.0 - nv, 5.0);
            return mix(vec2(1.0 - fc, fc), fit, smoothstep(0.0, 0.1, pr));
        }
        """;
}
