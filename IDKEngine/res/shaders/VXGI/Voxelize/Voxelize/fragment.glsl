#version 460 core
#define PI 3.14159265
#define EPSILON 0.001
#extension GL_ARB_bindless_texture : require

#define TAKE_ATOMIC_FP16_PATH AppInsert(TAKE_ATOMIC_FP16_PATH)

#if TAKE_ATOMIC_FP16_PATH
    #extension GL_NV_shader_atomic_fp16_vector : require
    #extension GL_NV_gpu_shader5 : require
#endif

AppInclude(include/Constants.glsl)
AppInclude(include/Compression.glsl)
AppInclude(include/Transformations.glsl)

layout(binding = 0, rgba16f) restrict uniform image3D ImgResult;

#if !TAKE_ATOMIC_FP16_PATH
layout(binding = 1, r32ui) restrict uniform uimage3D ImgResultR;
layout(binding = 2, r32ui) restrict uniform uimage3D ImgResultG;
layout(binding = 3, r32ui) restrict uniform uimage3D ImgResultB;
#endif

struct Material
{
    vec3 EmissiveFactor;
    uint BaseColorFactor;

    vec2 _pad0;
    float RoughnessFactor;
    float MetallicFactor;

    sampler2D BaseColor;
    sampler2D MetallicRoughness;

    sampler2D Normal;
    sampler2D Emissive;
};

struct PointShadow
{
    samplerCube Texture;
    samplerCubeShadow ShadowTexture;

    mat4 ProjViewMatrices[6];

    vec3 Position;
    float NearPlane;

    vec3 _pad0;
    float FarPlane;
};

struct Light
{
    vec3 Position;
    float Radius;
    vec3 Color;
    int PointShadowIndex;
};

layout(std430, binding = 3) restrict readonly buffer MaterialSSBO
{
    Material Materials[];
} materialSSBO;

layout(std140, binding = 1) uniform ShadowDataUBO
{
    PointShadow PointShadows[GLSL_MAX_UBO_POINT_SHADOW_COUNT];
} shadowDataUBO;

layout(std140, binding = 2) uniform LightsUBO
{
    Light Lights[GLSL_MAX_UBO_LIGHT_COUNT];
    int Count;
} lightsUBO;

layout(std140, binding = 5) uniform VoxelizerDataUBO
{
    mat4 OrthoProjection;
    vec3 GridMin;
    float _pad0;
    vec3 GridMax;
    float _pad1;
} voxelizerDataUBO;

in InOutVars
{
    vec3 FragPos;
    vec2 TexCoord;
    vec3 Normal;
    flat uint MaterialIndex;
    flat float EmissiveBias;
} inData;

vec3 GetDirectLighting(Light light, vec3 albedo, vec3 sampleToLight);
float Visibility(PointShadow pointShadow, vec3 lightSpacePos);
float GetLightSpaceDepth(PointShadow pointShadow, vec3 lightSpacePos);
ivec3 WorlSpaceToVoxelImageSpace(vec3 worldPos);

void main()
{
    ivec3 voxelPos = WorlSpaceToVoxelImageSpace(inData.FragPos);

    Material material = materialSSBO.Materials[inData.MaterialIndex];
    vec4 albedoAlpha = texture(material.BaseColor, inData.TexCoord) * DecompressUR8G8B8A8(material.BaseColorFactor);
    vec3 emissive = MATERIAL_EMISSIVE_FACTOR * (texture(material.Emissive, inData.TexCoord).rgb * material.EmissiveFactor) + inData.EmissiveBias * albedoAlpha.rgb;

    vec3 directLighting = vec3(0.0);
    for (int i = 0; i < lightsUBO.Count; i++)
    {
        Light light = lightsUBO.Lights[i];

        vec3 sampleToLight = light.Position - inData.FragPos;
        vec3 contrib = GetDirectLighting(light, albedoAlpha.rgb, sampleToLight);
        if (light.PointShadowIndex >= 0)
        {
            PointShadow pointShadow = shadowDataUBO.PointShadows[light.PointShadowIndex];
            contrib *= Visibility(pointShadow, -sampleToLight);
        }

        directLighting += contrib;
    }

    const float ambient = 0.03;
    directLighting += albedoAlpha.rgb * ambient;
    directLighting += emissive;

#if TAKE_ATOMIC_FP16_PATH

    imageAtomicMax(ImgResult, voxelPos, f16vec4(directLighting, 1.0));

#else

    imageAtomicMax(ImgResultR, voxelPos, floatBitsToUint(directLighting.r));
    imageAtomicMax(ImgResultG, voxelPos, floatBitsToUint(directLighting.g));
    imageAtomicMax(ImgResultB, voxelPos, floatBitsToUint(directLighting.b));
    imageStore(ImgResult, voxelPos, vec4(0.0, 0.0, 0.0, 1.0));

#endif

}

vec3 GetDirectLighting(Light light, vec3 albedo, vec3 sampleToLight)
{
    float sampleToLightLength = length(sampleToLight);

    vec3 lightDir = sampleToLight / sampleToLightLength;
    float cosTerm = dot(inData.Normal, lightDir);
    if (cosTerm > 0.0)
    {
        vec3 diffuse = light.Color * cosTerm * albedo;
        vec3 attenuation = light.Color / (4.0 * PI * sampleToLightLength * sampleToLightLength);

        return diffuse * attenuation;
    }

    return vec3(0.0);
}

float Visibility(PointShadow pointShadow, vec3 lightSpacePos)
{
    float bias = 0.02;
    const float sampleDiskRadius = 0.08;

    float depth = GetLightSpaceDepth(pointShadow, lightSpacePos * (1.0 - bias));
    float visibilityFactor = texture(pointShadow.ShadowTexture, vec4(lightSpacePos, depth));

    return visibilityFactor;
}

float GetLightSpaceDepth(PointShadow pointShadow, vec3 lightSpacePos)
{
    float dist = max(abs(lightSpacePos.x), max(abs(lightSpacePos.y), abs(lightSpacePos.z)));
    float depth = GetLogarithmicDepth(pointShadow.NearPlane, pointShadow.FarPlane, dist);

    return depth;
}

ivec3 WorlSpaceToVoxelImageSpace(vec3 worldPos)
{
    vec3 ndc = (voxelizerDataUBO.OrthoProjection * vec4(worldPos, 1.0)).xyz;
    vec3 uvw = NdcToUvDepth(ndc);
    ivec3 voxelPos = ivec3(uvw * imageSize(ImgResult));
    return voxelPos;
}
