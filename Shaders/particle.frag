#version 330 core
in float vTemp;
in float vAlpha;
in vec3 vColor;
in float vCrowd;

uniform float uBrightness;
uniform float uSmooth;

out vec4 FragColor;

// Approximate blackbody-ish emission ramp:
// cold(0) -> deep red -> orange -> yellow -> white-hot(>1).
vec3 heatColor(float t)
{
    t = max(t, 0.0);
    vec3 red    = vec3(0.85, 0.10, 0.02);
    vec3 orange = vec3(1.00, 0.45, 0.10);
    vec3 yellow = vec3(1.00, 0.85, 0.45);
    vec3 white  = vec3(1.00, 0.97, 0.92);
    if (t < 0.33)      return mix(red, orange, t / 0.33);
    else if (t < 0.66) return mix(orange, yellow, (t - 0.33) / 0.33);
    else               return mix(yellow, white, clamp((t - 0.66) / 0.34, 0.0, 1.0));
}

void main()
{
    vec2 d = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(d, d);
    if (r2 > 1.0) discard;

    // Crowded particles use a wider, softer profile and contribute less each, so many
    // overlapping sprites accumulate into a smooth, connected blob instead of dots.
    float blend = uSmooth * vCrowd;
    float falloff = exp(-r2 * mix(2.2, 1.0, blend));
    float crowdAlpha = mix(1.0, 0.45, blend);

    // Hot particles glow as blackbody; cool ones settle to dim material color.
    float hot = clamp(vTemp, 0.0, 1.0);
    vec3 mat = vColor * 0.6;
    vec3 col = mix(mat, heatColor(vTemp), hot);

    // HDR intensity: hot cores exceed 1.0 so the bloom pass catches them.
    float intensity = max(uBrightness * (0.25 + 1.9 * vTemp), 0.15);

    FragColor = vec4(col * intensity * falloff, falloff * vAlpha * crowdAlpha);
}
