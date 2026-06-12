#version 330 core
in float vDepthFactor;

uniform vec4 uBaseColor; // rgba, semi-transparent
uniform vec3 uWellColor; // tint where spacetime dips deepest

out vec4 FragColor;

void main()
{
    vec3 color = mix(uBaseColor.rgb, uWellColor, vDepthFactor);
    // Deeper grid lines glow slightly brighter so the wells read clearly.
    float alpha = uBaseColor.a * (0.55 + 0.45 * vDepthFactor);
    FragColor = vec4(color, alpha);
}
