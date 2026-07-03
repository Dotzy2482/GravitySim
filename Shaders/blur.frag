#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uImage;
uniform vec2 uTexel;   // (1/w, 0) for horizontal pass, (0, 1/h) for vertical

void main()
{
    float w[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
    vec3 result = texture(uImage, vUV).rgb * w[0];
    for (int i = 1; i < 5; i++)
    {
        result += texture(uImage, vUV + uTexel * float(i)).rgb * w[i];
        result += texture(uImage, vUV - uTexel * float(i)).rgb * w[i];
    }
    FragColor = vec4(result, 1.0);
}
