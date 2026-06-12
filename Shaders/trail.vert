#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in float aFade; // 0 = oldest point, 1 = newest

uniform mat4 uView;
uniform mat4 uProjection;

out float vFade;

void main()
{
    vFade = aFade;
    gl_Position = uProjection * uView * vec4(aPosition, 1.0);
}
