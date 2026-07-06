#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uBloomStrength;
uniform float uExposure;

// Narkowicz ACES filmic approximation. Maps HDR into 0..1 with a soft
// shoulder, so hot debris reads as glowing orange instead of clipped white.
vec3 acesTonemap(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    vec3 hdr = texture(uScene, vUV).rgb + uBloomStrength * texture(uBloom, vUV).rgb;
    // NOTE: the pipeline has never gamma-encoded; all existing colors were
    // authored in that space. Keep it that way - exposure compensates.
    FragColor = vec4(acesTonemap(hdr * uExposure), 1.0);
}
