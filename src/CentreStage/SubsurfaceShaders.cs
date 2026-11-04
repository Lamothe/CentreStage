namespace CentreStage;

internal static class SubsurfaceShaders
{
    internal const string Vertex = """
        #version 330 core
        out vec2 uv;
        void main() {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            uv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string Common = """
        #version 330 core
        in vec2 uv;
        out vec4 result;
        uniform sampler2D source;
        uniform sampler2D depthMap;
        uniform sampler2D maskMap;
        uniform vec2 nearFar;
        float distanceAt(vec2 p) {
            float z = texture(depthMap, p).r * 2.0 - 1.0;
            return 2.0 * nearFar.x * nearFar.y /
                max(nearFar.y + nearFar.x - z * (nearFar.y - nearFar.x), 0.00001);
        }
        """;

    internal static readonly string Blur = Common + """
        uniform vec2 direction;
        uniform float radiusWorld;
        uniform float projectionScale;
        void main() {
            vec4 center = texture(source, uv);
            if (texture(maskMap, uv).a < 0.5) { result = center; return; }
            float depth = distanceAt(uv);
            // Radius is world-relative and perspective-correct, capped only for cost/edge safety.
            vec2 stepUv = direction * min(projectionScale * radiusWorld / depth, 0.08);
            vec3 total = vec3(0.0);
            vec3 weights = vec3(0.0);
            for (int i = -8; i <= 8; i++) {
                float x = float(i) / 8.0;
                vec2 p = uv + stepUv * x;
                // Red travels farther than green and blue; normalised per channel.
                vec3 w = exp(-vec3(2.0, 6.0, 12.0) * x*x);
                bool inside = all(greaterThanEqual(p, vec2(0.0))) && all(lessThanEqual(p, vec2(1.0)));
                float dz = abs(distanceAt(clamp(p, 0.0, 1.0)) - depth);
                float edge = 1.0 - smoothstep(radiusWorld * 0.25, radiusWorld, dz);
                if (!inside || texture(maskMap, clamp(p, 0.0, 1.0)).a < 0.5) { edge = 0.0; }
                total += mix(center.rgb, texture(source, clamp(p, 0.0, 1.0)).rgb, edge) * w;
                weights += w;
            }
            result = vec4(total / weights, center.a);
        }
        """;

    internal static readonly string Combine = Common + """
        uniform sampler2D radianceMap;
        uniform sampler2D originalDiffuse;
        uniform float strength;
        uniform float exposure;
        uniform float brightness;
        uniform float contrast;
        uniform float saturation;
        vec3 linearToSrgb(vec3 c) {
            return mix(1.055 * pow(max(c, 0.0), vec3(1.0/2.4)) - 0.055,
                12.92 * c, lessThanEqual(c, vec3(0.0031308)));
        }
        void main() {
            vec4 original = texture(originalDiffuse, uv);
            vec3 radiance = texture(radianceMap, uv).rgb;
            gl_FragDepth = texture(depthMap, uv).r;
            // Negative alpha classifies unlit/background display pixels: never grade them.
            if (original.a < 0.0) { result = vec4(radiance, 1.0); return; }
            vec3 softened = texture(source, uv).rgb;
            // Preserve all non-diffuse radiance, including specular microdetail and emission.
            vec3 c = max(radiance + (softened - original.rgb) * strength * original.a, 0.0);
            c *= exp2(exposure);
            c = (c * (2.51*c + 0.03)) / (c * (2.43*c + 0.59) + 0.14);
            c = linearToSrgb(c);
            float grey = dot(c, vec3(0.2126, 0.7152, 0.0722));
            c = mix(vec3(grey), c, saturation);
            c = pow(max(c, 0.0), vec3(max(1.0 + contrast, 0.01)));
            c += brightness * 4.0 * c * (1.0-c);
            result = vec4(clamp(c, 0.0, 1.0), 1.0);
        }
        """;

}
