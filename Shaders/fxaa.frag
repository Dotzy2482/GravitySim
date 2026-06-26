#version 330 core
// Compact FXAA (console variant) — cheap edge AA now that MSAA is off.
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uImage;
uniform vec2 uTexel;

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main()
{
    vec3 rgbM  = texture(uImage, vUV).rgb;
    float lM   = luma(rgbM);
    float lNW  = luma(texture(uImage, vUV + vec2(-uTexel.x, -uTexel.y)).rgb);
    float lNE  = luma(texture(uImage, vUV + vec2( uTexel.x, -uTexel.y)).rgb);
    float lSW  = luma(texture(uImage, vUV + vec2(-uTexel.x,  uTexel.y)).rgb);
    float lSE  = luma(texture(uImage, vUV + vec2( uTexel.x,  uTexel.y)).rgb);

    float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE)));
    float lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));

    vec2 dir;
    dir.x = -((lNW + lNE) - (lSW + lSE));
    dir.y =  ((lNW + lSW) - (lNE + lSE));

    float reduce = max((lNW + lNE + lSW + lSE) * 0.25 * 0.125, 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);
    dir = clamp(dir * rcpDirMin, vec2(-8.0), vec2(8.0)) * uTexel;

    vec3 rgbA = 0.5 * (texture(uImage, vUV + dir * (1.0 / 3.0 - 0.5)).rgb +
                       texture(uImage, vUV + dir * (2.0 / 3.0 - 0.5)).rgb);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (texture(uImage, vUV + dir * -0.5).rgb +
                                     texture(uImage, vUV + dir *  0.5).rgb);
    float lB = luma(rgbB);
    FragColor = vec4((lB < lMin || lB > lMax) ? rgbA : rgbB, 1.0);
}
