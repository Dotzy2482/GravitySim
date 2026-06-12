#version 330 core
in vec3 vNormal;
in vec3 vWorldPos;

uniform vec3 uColor;
uniform vec3 uLightPos;   // world-space point light (the star)
uniform vec3 uViewPos;
uniform float uEmissive;  // 0 = lit planet, 1 = self-luminous star

out vec4 FragColor;

void main()
{
    vec3 n = normalize(vNormal);
    vec3 lightDir = normalize(uLightPos - vWorldPos);

    float ambient = 0.08;
    float diffuse = max(dot(n, lightDir), 0.0);

    vec3 lit = uColor * (ambient + diffuse);

    // Stars: mostly flat bright color with a soft rim glow toward the silhouette.
    vec3 viewDir = normalize(uViewPos - vWorldPos);
    float rim = pow(1.0 - max(dot(n, viewDir), 0.0), 2.0);
    vec3 glow = uColor * (1.0 + 0.6 * rim);

    vec3 color = mix(lit, glow, uEmissive);
    FragColor = vec4(color, 1.0);
}
