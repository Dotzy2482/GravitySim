#version 330 core
// Full-screen triangle generated from gl_VertexID - no vertex buffer needed.
out vec2 vUV;

void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUV = p;                       // 0..2; visible region maps to 0..1
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
