#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vNormal;
out vec3 vWorldPos;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);
    vWorldPos = world.xyz;
    // Model is translate * uniform scale, so transforming the normal by the
    // upper 3x3 and renormalizing is exact.
    vNormal = normalize(mat3(uModel) * aNormal);
    gl_Position = uProjection * uView * world;
}
