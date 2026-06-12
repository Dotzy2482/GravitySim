#version 330 core
in float vFade;

uniform vec3 uColor;
uniform float uAlpha;

out vec4 FragColor;

void main()
{
    FragColor = vec4(uColor, uAlpha * vFade);
}
