#version 460

#include "common/utils.slang"
#include "common/ViewConstants.slang"

layout (local_size_x = 8, local_size_y = 8) in;

#define D_DOF 0

#define D_MSAA_SAMPLES 1

uniform bool g_bFlipY = false;

layout(binding = 0) uniform sampler2DMS g_tSourceMsaa;
layout(binding = 1, rgba16f) uniform writeonly image2D g_tDest;

#if D_DOF == 1
uniform mat4 g_invViewProjMatrix;
uniform vec4 g_vLensPlane;
uniform vec4 g_vNearFarScaleBias;
layout(binding = 2) uniform sampler2DMS g_tSceneDepth;
#endif

void main()
{
    ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
    ivec2 destSize = imageSize(g_tDest);

    if (coord.x >= destSize.x || coord.y >= destSize.y)
        return;

    ivec2 sampleCoord = coord;
    sampleCoord.y = g_bFlipY ? textureSize(g_tSourceMsaa).y - sampleCoord.y - 1 : sampleCoord.y;

    float invNumSamples = 1.0 / float(D_MSAA_SAMPLES);
    vec3 resolved = vec3(0.0);

#if D_DOF == 1
    // Reverse-Z: larger depth is closer to the camera. Alpha-to-coverage foliage can
    // contain both foreground leaf and background samples in one pixel, so use the
    // nearest covered depth for CoC instead of leaking the background CoC onto it.
    float nearestDepth = 0.0;
    // Alpha carries the circle of confusion for the depth of field pass (set below)
    float outputAlpha = 1.0;
#else
    // Box-average the source alpha so a transparent background survives (e.g. for screenshots)
    float outputAlpha = 0.0;
#endif

    for (int i = 0; i < D_MSAA_SAMPLES; i++)
    {
        vec4 texel = texelFetch(g_tSourceMsaa, sampleCoord, i);
        vec3 s = clamp(texel.rgb, vec3(0.0), vec3(65504.0));
        // Karis average: weight by inverse luminance to reduce fireflies
        resolved += s * (invNumSamples / (max(s.r, max(s.g, s.b)) + 1.0));

#if D_DOF == 1
        nearestDepth = max(texelFetch(g_tSceneDepth, sampleCoord, i).x, nearestDepth);
#else
        outputAlpha += texel.a * invNumSamples;
#endif
    }

    // Inverse Karis tonemap
    resolved *= 1.0 / (1.0 - max(resolved.r, max(resolved.g, resolved.b)));

#if D_DOF == 1
    vec2 sampleUV = (vec2(coord) + 0.5) * g_vInvViewportSize.xy;

    // The main scene occupies the 0.05..1.0 reverse-Z range. 3D skybox geometry is
    // deliberately rendered into 0.0..0.05 instead, so it remains behind the main
    // scene. Undo the respective depth-range mapping before reconstructing a world
    // position; treating sky depth as main-scene depth reconstructs it at infinity
    // and consequently leaves it permanently at maximum far blur.
    float sceneDepth = nearestDepth < g_flViewportMinZ
        ? nearestDepth / g_flViewportMinZ
        : LinearRamp(g_flViewportMinZ, g_flViewportMaxZ, nearestDepth);

    // Convert from screen coords to clipspace (normalised device coordinates)
    vec4 screenToClip = vec4(
        sampleUV.x * 2.0 - 1.0,
        sampleUV.y * 2.0 - 1.0,
        sceneDepth,
        1.0
    );

    // Undo view projection to get back 3d world coordinates
    vec4 worldPos = g_invViewProjMatrix * screenToClip;

    // An untouched depth value is the 2D/infinite sky. It has no finite world-space
    // distance, so retain the default full far blur for it.
    if (worldPos.w != 0.0)
    {
        vec4 worldPos3D = vec4(worldPos.xyz / worldPos.w, 1.0);

        // Projecting worldpos onto the lens plane gives us how far away the point is from the plane
        // g_vLensPlane.w is the plane offset, using dot between 4d vectors for this is clever
        float distanceFromLensPlane = dot(g_vLensPlane, worldPos3D);

        float nearScale = g_vNearFarScaleBias.x;
        float nearBias = g_vNearFarScaleBias.y;
        float farScale = g_vNearFarScaleBias.z;
        float farBias = g_vNearFarScaleBias.w;

        // NearScale is negative. NearBias must retain its sign so the near blurry/crisp
        // interval works on either side of the focal plane (e.g. 200 -> 400).
        float closeCoC = clamp((distanceFromLensPlane * nearScale) + nearBias, 0.0, 1.0);
        float farCoC = clamp((distanceFromLensPlane * farScale) + farBias, 0.0, 1.0);

        // Keep the CoC sign through to the blur pass: positive denotes far blur and
        // negative denotes near blur. When both bands overlap, retain the stronger
        // one just as the former unsigned CoC did.
        outputAlpha = farCoC >= closeCoC ? farCoC : -closeCoC;
    }
#endif

    imageStore(g_tDest, coord, vec4(resolved, outputAlpha));
}
