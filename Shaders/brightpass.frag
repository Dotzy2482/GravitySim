#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uScene;
uniform float uThreshold;

void main()
{
    vec3 c = texture(uScene, vUV).rgb;
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));

    // Soft knee: quadratic ramp-in around the threshold instead of a hard
    // cut. Kills flicker and stops mid-brightness pixels from smearing.
    float knee = uThreshold * 0.5;
    float soft = clamp(luma - uThreshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-4);
    float contrib = max(soft, luma - uThreshold) / max(luma, 1e-4);

    FragColor = vec4(c * contrib, 1.0);
}
