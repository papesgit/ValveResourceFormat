#version 460

uniform float flMaxBlurSize;
uniform vec4[256] g_vSampleOffsetsRadMinMax;
uniform int g_nSamples;

layout(binding = 0) uniform sampler2D g_tInputColor_CoC;
layout(location = 0) out vec4 outputColor;

void main()
{
    vec2 fragCoord = gl_FragCoord.xy / textureSize(g_tInputColor_CoC, 0);

    vec4 centerSample = texture(g_tInputColor_CoC, fragCoord);
    float centerCoC = centerSample.w;

    float weightSum = 1.0;
    vec3 finalColor = centerSample.xyz;

    for (int i = 0; i < g_nSamples; i++)
    {
        vec2 currentSampleOffset = (fragCoord + g_vSampleOffsetsRadMinMax[i].xy).xy;

        vec4 offsetSample = texture(g_tInputColor_CoC, currentSampleOffset);

        // This is a gather pass, so the center pixel determines its sampling radius.
        // Using the neighboring pixel's CoC made near-blurred foliage depend on whether
        // the geometry visible through/behind it happened to be blurred as well. A crisp
        // foreground center still has a zero radius and therefore rejects background blur.
        float t = smoothstep(g_vSampleOffsetsRadMinMax[i].z, g_vSampleOffsetsRadMinMax[i].w, centerCoC * flMaxBlurSize);
      
        finalColor += mix(finalColor / weightSum, offsetSample.rgb, t);

        weightSum += 1.0;
    }

    outputColor = vec4(finalColor / weightSum, 1.0);
}

