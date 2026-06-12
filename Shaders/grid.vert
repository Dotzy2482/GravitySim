#version 330 core
layout(location = 0) in vec3 aPosition;

uniform mat4 uView;
uniform mat4 uProjection;

out float vDepthFactor; // 0 = flat, 1 = deep in a gravity well (for coloring)

uniform float uBaseHeight; // resting height of the undisturbed sheet
uniform float uMaxDip;     // displacement at which the well color saturates

void main()
{
    vDepthFactor = clamp((uBaseHeight - aPosition.y) / max(uMaxDip, 0.0001), 0.0, 1.0);
    gl_Position = uProjection * uView * vec4(aPosition, 1.0);
}
