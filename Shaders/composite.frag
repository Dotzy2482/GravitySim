#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uBloomStrength;

void main()
{
    // Additive bloom on top of the untouched scene (no tonemap, so the existing
    // look is preserved; hot cores clip to white which reads as molten).
    vec3 c = texture(uScene, vUV).rgb + uBloomStrength * texture(uBloom, vUV).rgb;
    FragColor = vec4(c, 1.0);
}
