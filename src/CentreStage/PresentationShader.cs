namespace CentreStage;

/// <summary>
/// Live presentation variant. The original source remains untouched for pinned evidence.
/// Replacements fail loudly on source drift rather than silently dropping a stage.
/// </summary>
internal static class PresentationShader
{
    public static readonly string Fragment = Build();

    private static string Replace(string source, string before, string after)
    {
        if (source.IndexOf(before, StringComparison.Ordinal) < 0
            || source.IndexOf(before, StringComparison.Ordinal) != source.LastIndexOf(before, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Presentation shader anchor is missing or ambiguous");
        }
        return source.Replace(before, after, StringComparison.Ordinal);
    }

    private static string Build()
    {
        var source = Replace(ShaderSources.Fragment, "#version 330 core", "#version 330 core\n" + StudioEnvironment.Glsl + "\n" + """
            uniform float uEnvironmentIntensity;
            uniform float uEnvironmentRotation;
            uniform float uExposure;
            uniform float uBrightness;
            uniform float uContrast;
            uniform float uSaturation;
            uniform float uSkinDiffusion;

            vec3 environmentDirection(vec3 d)
            {
                float c = cos(uEnvironmentRotation);
                float s = sin(uEnvironmentRotation);
                return vec3(c*d.x - s*d.z, d.y, s*d.x + c*d.z);
            }
            """);
        source = Replace(source,
            "vec3 ambient = mix(uGroundColor, uSkyColor, n.y * 0.5 + 0.5);", """
            // Diffusion approximation: only diffuse irradiance sees the smoother normal.
            // The environment specular normal is softened separately; the atlas stays sharp.
            vec3 diffuseNormal = normalize(mix(n, normalize(vNormal), uSkinDiffusion));
            vec3 ambient = studioDiffuse(environmentDirection(diffuseNormal)) * uEnvironmentIntensity;
            """);
        source = Replace(source,
            "color += ambient * base.rgb * (1.0 - metallic * 0.6);", """
            // Surface diffusion broadens the dielectric lobe without blurring the atlas.
            float environmentRoughness = mix(roughness, max(roughness, 0.55), uSkinDiffusion);
            vec3 environmentNormal = normalize(mix(n, normalize(vNormal), uSkinDiffusion * 0.5));
            // Horizon specular occlusion: a normal-map reflection below the mesh
            // surface cannot see the environment. Avoid false mirror seams at steep pores.
            vec3 surfaceNormal = normalize(vNormal);
            if (dot(surfaceNormal, v) < 0.0) { surfaceNormal = -surfaceNormal; }
            vec3 reflected = reflect(-v, environmentNormal);
            float horizon = min(1.0 + dot(reflected, surfaceNormal), 1.0);
            horizon = max(horizon, 0.0);
            horizon *= horizon;
            vec2 brdf = studioBrdf(NoV, environmentRoughness);
            vec3 envFresnel = f0 + (vec3(f90) - f0) * pow(1.0 - NoV, 5.0);
            color += ambient * base.rgb * (1.0 - metallic) * (1.0 - envFresnel);
            color += studioSpecular(environmentDirection(reflect(-v, environmentNormal)), environmentRoughness)
                * uEnvironmentIntensity * (f0 * brdf.x + f90 * brdf.y) * horizon;
            // Clearcoat has its own dielectric environment lobe, above the base surface.
            float coatRoughness = clamp(uClearcoatRoughnessFactor, 0.04, 1.0);
            if (uHasClearcoatRoughnessTex != 0)
            {
                coatRoughness = clamp(coatRoughness * texture(uClearcoatRoughnessTex,
                    uvFor(uClearcoatRoughnessSet, uClearcoatRoughnessUv)).g, 0.04, 1.0);
            }
            vec2 coatBrdf = studioBrdf(NoV, coatRoughness);
            color += clearcoat * studioSpecular(environmentDirection(reflect(-v, n)),
                coatRoughness) * uEnvironmentIntensity
                * (0.04 * coatBrdf.x + coatBrdf.y);
            """);
        source = Replace(source, "color *= mix(1.0, ao, uOcclusionStrength);", """
            // Thin-surface diffusion also weakens baked micro-occlusion; it does not
            // blur albedo or claim to reconstruct a thickness/profile absent from glTF.
            ao = mix(ao, sqrt(max(ao, 0.0)), uSkinDiffusion);
            color *= mix(1.0, ao, uOcclusionStrength);
            """);
        // Keep the original radiance fallback, but sample an already graded backdrop
        // only after mapping. Emission is mapped independently for the transmitted branch.
        source = Replace(source, "if (transmission > 0.0)",
            "if (transmission > 0.0 && uHasSceneColor == 0)");
        source = Replace(source, "void main()", """
            vec3 presentationMap(vec3 radiance)
            {
                vec3 c = max(radiance * exp2(uExposure), 0.0);
                c = (c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14);
                c = linearToSrgb(c);
                float grey = dot(c, vec3(0.2126, 0.7152, 0.0722));
                c = mix(vec3(grey), c, uSaturation);
                // Gamma contrast and midtone lift preserve black and white.
                c = pow(max(c, 0.0), vec3(max(1.0 + uContrast, 0.01)));
                c += uBrightness * 4.0 * c * (1.0 - c);
                return clamp(c, 0.0, 1.0);
            }

            void main()
            """);
        source = Replace(source, "color += emissive;", """
            vec3 emissionLinear = srgbToLinear(presentationMap(emissive));
            color += emissive;
            """);
        source = Replace(source, "color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);",
            "color = presentationMap(color);");
        source = Replace(source, "color = linearToSrgb(color);", """
            if (transmission > 0.0 && uHasSceneColor != 0)
            {
                float t = clamp(transmission, 0.0, 1.0);
                vec3 nView = normalize(mat3(uView) * n);
                vec2 offset = nView.xy * (uRefractionScale * t * uSceneExtent);
                vec2 uv = (gl_FragCoord.xy - uSceneOrigin + offset) * uSceneTexel;
                vec3 behind = srgbToLinear(textureLod(uSceneColor,
                    clamp(uv, vec2(0.0), vec2(1.0)), 0.0).rgb);
                vec3 transmittedLinear = behind * base.rgb + emissionLinear;
                // Composite in display-linear light, never gamma-encoded sRGB.
                color = linearToSrgb(mix(srgbToLinear(color), transmittedLinear, t));
            }
            """);
        return source;
    }
}
