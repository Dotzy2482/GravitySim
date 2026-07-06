#version 330 core
in vec2 vNdc;
out vec4 FragColor;

uniform mat4 uInvViewProj;
uniform vec3 uCamPos;
uniform float uTime;

// All output stays well below the bloom threshold (~1.15) so the sky never blooms.

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float n00 = mix(hash13(i + vec3(0.0, 0.0, 0.0)), hash13(i + vec3(1.0, 0.0, 0.0)), u.x);
    float n10 = mix(hash13(i + vec3(0.0, 1.0, 0.0)), hash13(i + vec3(1.0, 1.0, 0.0)), u.x);
    float n01 = mix(hash13(i + vec3(0.0, 0.0, 1.0)), hash13(i + vec3(1.0, 0.0, 1.0)), u.x);
    float n11 = mix(hash13(i + vec3(0.0, 1.0, 1.0)), hash13(i + vec3(1.0, 1.0, 1.0)), u.x);
    return mix(mix(n00, n10, u.y), mix(n01, n11, u.y), u.z);
}

float fbm(vec3 p)
{
    float s = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        s += amp * vnoise(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return s;
}

// Map a direction onto a cube face so star cells do not swim with the camera.
vec3 cubeCell(vec3 dir, out vec2 cellFrac)
{
    vec3 a = abs(dir);
    vec2 uv;
    float face;
    if (a.x >= a.y && a.x >= a.z) { uv = dir.yz / a.x; face = dir.x > 0.0 ? 0.0 : 1.0; }
    else if (a.y >= a.z)          { uv = dir.xz / a.y; face = dir.y > 0.0 ? 2.0 : 3.0; }
    else                          { uv = dir.xy / a.z; face = dir.z > 0.0 ? 4.0 : 5.0; }
    uv = uv * 0.5 + 0.5;
    const float cells = 200.0;
    cellFrac = fract(uv * cells);
    return vec3(floor(uv * cells), face);
}

void main()
{
    vec4 far = uInvViewProj * vec4(vNdc, 1.0, 1.0);
    vec3 dir = normalize(far.xyz / far.w - uCamPos);

    // Stars: sparse hashed cells, brightness tiers, gentle twinkle.
    vec2 f;
    vec3 cell = cubeCell(dir, f);
    float h = hash13(cell);
    float star = 0.0;
    if (h > 0.91)
    {
        vec2 center = vec2(fract(h * 37.0), fract(h * 91.0)) * 0.6 + 0.2;
        float d = length(f - center);
        float tier = fract(h * 13.0);
        float bright = 0.12 + 0.75 * tier * tier;
        float tw = 0.85 + 0.15 * sin(uTime * (0.8 + 3.0 * fract(h * 53.0)) + h * 40.0);
        star = smoothstep(0.10, 0.0, d) * bright * tw;
    }

    // Nebula: two faint dust layers, cool blue and dim violet.
    float n1 = fbm(dir * 2.6 + vec3(11.0, 3.0, 7.0));
    float n2 = fbm(dir * 1.4 + vec3(3.0, 17.0, 5.0));
    vec3 nebula = vec3(0.045, 0.065, 0.14) * n1 * n1 + vec3(0.075, 0.045, 0.11) * n2 * n2 * 0.7;

    vec3 col = vec3(star) * 0.55 + nebula * 0.5 + vec3(0.008, 0.009, 0.02);
    FragColor = vec4(col, 1.0);
}
