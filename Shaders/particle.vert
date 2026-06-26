#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in float aTemp;   // 0 = cold, ~1 orange, >1.2 white-hot (HDR)
layout(location = 2) in float aAlpha;  // lifetime fade
layout(location = 3) in vec3 aColor;   // original material color

uniform mat4 uView;
uniform mat4 uProjection;
uniform float uViewportHeight;
uniform float uParticleRadius;  // world-space sprite radius

out float vTemp;
out float vAlpha;
out vec3 vColor;

void main()
{
    vec4 viewPos = uView * vec4(aPos, 1.0);
    gl_Position = uProjection * viewPos;

    // Perspective-correct point size: hotter particles read a touch larger.
    float dist = max(-viewPos.z, 0.001);
    float sizeScale = 0.7 + 0.6 * clamp(aTemp, 0.0, 1.5);
    float pix = uParticleRadius * sizeScale * uProjection[1][1] * 0.5 * uViewportHeight / dist;
    gl_PointSize = clamp(pix, 1.0, 80.0);

    vTemp = aTemp;
    vAlpha = aAlpha;
    vColor = aColor;
}
