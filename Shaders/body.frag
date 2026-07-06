#version 330 core
in vec3 vNormal;
in vec3 vWorldPos;
in vec3 vObjPos;

uniform vec3 uColor;
uniform vec3 uLightPos;   // world-space point light (the star)
uniform vec3 uViewPos;
uniform float uEmissive;  // 0 = lit planet, 1 = self-luminous star
uniform float uSeed;      // per-body variation
uniform float uTime;      // star surface churn

out vec4 FragColor;

float hash13(vec3 p)
{
    p = fract(p * 0.1031 + uSeed);
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

void main()
{
    vec3 n = normalize(vNormal);
    vec3 lightDir = normalize(uLightPos - vWorldPos);
    vec3 viewDir = normalize(uViewPos - vWorldPos);
    float ndv = max(dot(n, viewDir), 0.0);

    // ---- planet branch -------------------------------------------------
    // Two-tone procedural terrain derived from the body color, so the user
    // color picker still drives the overall look.
    float h = fbm(vObjPos * 3.1);
    float landMix = smoothstep(0.38, 0.62, h);
    vec3 lowland = uColor * 0.5;
    vec3 highland = mix(uColor, vec3(1.0), 0.22);
    vec3 albedo = mix(lowland, highland, landMix);
    // Fine grain so close-ups do not look vinyl-smooth.
    albedo *= 0.92 + 0.16 * vnoise(vObjPos * 14.0);

    // Half-Lambert wrap: softer terminator than raw N.L.
    float ndl = dot(n, lightDir);
    float wrap = clamp((ndl + 0.28) / 1.28, 0.0, 1.0);
    float diffuse = wrap * wrap;

    vec3 halfVec = normalize(lightDir + viewDir);
    float spec = pow(max(dot(n, halfVec), 0.0), 48.0) * 0.12 * diffuse;

    vec3 lit = albedo * (0.05 + diffuse) + vec3(spec);

    // Atmosphere: fresnel rim tinted between sky blue and the body color,
    // stronger on the lit side.
    float fresnel = pow(1.0 - ndv, 2.6);
    vec3 atmColor = mix(vec3(0.36, 0.56, 0.95), uColor, 0.35);
    lit += atmColor * fresnel * 0.4 * clamp(diffuse + 0.3, 0.0, 1.0);

    // ---- star branch ---------------------------------------------------
    // Limb darkening (bright center, dimmer edge) + slow churning surface.
    float limb = 0.5 + 0.5 * pow(ndv, 0.55);
    float churn = fbm(vObjPos * 4.0 + vec3(0.0, uTime * 0.12, uTime * 0.05));
    vec3 sun = uColor * limb * (0.85 + 0.6 * churn) * 1.7; // HDR: exceeds bloom threshold
    sun += uColor * pow(1.0 - ndv, 2.0) * 0.7;             // soft rim into the bloom

    vec3 color = mix(lit, sun, uEmissive);
    FragColor = vec4(color, 1.0);
}
