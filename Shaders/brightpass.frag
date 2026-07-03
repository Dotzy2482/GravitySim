#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uScene;
uniform float uThreshold;

void main()
{
    vec3 c = texture(uScene, vUV).rgb;
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    // Keep only the energy above the threshold, preserving hue.
    float contrib = max(luma - uThreshold, 0.0) / max(luma, 1e-4);
    FragColor = vec4(c * contrib, 1.0);
}
