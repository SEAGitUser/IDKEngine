#version 460 core
#extension GL_NV_mesh_shader : require
#extension GL_NV_gpu_shader5 : require
#extension GL_ARB_bindless_texture : require
#extension GL_KHR_shader_subgroup_ballot : require

AppInclude(include/Constants.glsl)
AppInclude(include/IntersectionRoutines.glsl)

#define MESHLETS_PER_WORKGROUP 32
layout(local_size_x = MESHLETS_PER_WORKGROUP) in;

struct DrawElementsCmd
{
    uint IndexCount;
    uint InstanceCount;
    uint FirstIndex;
    uint BaseVertex;
    uint BaseInstance;

    uint BlasRootNodeIndex;
};

struct Mesh
{
    int MaterialIndex;
    float NormalMapStrength;
    float EmissiveBias;
    float SpecularBias;
    float RoughnessBias;
    float RefractionChance;
    float IOR;
    uint MeshletsStart;
    vec3 Absorbance;
    uint MeshletCount;
};

struct MeshInstance
{
    mat4 ModelMatrix;
    mat4 InvModelMatrix;
    mat4 PrevModelMatrix;
};

struct Meshlet
{
    uint VertexOffset;
    uint IndicesOffset;

    uint8_t VertexCount;
    uint8_t TriangleCount;
};

struct MeshletInfo
{
    vec3 Min;
    float _pad0;

    vec3 Max;
    float _pad1;
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

layout(std430, binding = 0) restrict readonly buffer DrawElementsCmdSSBO
{
    DrawElementsCmd DrawCommands[];
} drawElementsCmdSSBO;

layout(std430, binding = 1) restrict readonly buffer MeshSSBO
{
    Mesh Meshes[];
} meshSSBO;

layout(std430, binding = 2) restrict readonly buffer MeshInstanceSSBO
{
    MeshInstance MeshInstances[];
} meshInstanceSSBO;

layout(std430, binding = 12) restrict readonly buffer MeshletSSBO
{
    Meshlet Meshlets[];
} meshletSSBO;

layout(std430, binding = 13) restrict readonly buffer MeshletInfoSSBO
{
    MeshletInfo MeshletsInfo[];
} meshletInfoSSBO;

layout(std140, binding = 1) uniform ShadowDataUBO
{
    PointShadow PointShadows[GPU_MAX_UBO_POINT_SHADOW_COUNT];
    int Count;
} shadowDataUBO;

taskNV out InOutVars
{
    // interstingly this needs to be passed down, otherwise we get subtle bugs
    uint MeshID;
    uint MeshletsStart;
    uint8_t SurvivingMeshlets[MESHLETS_PER_WORKGROUP];
} outData;

layout(location = 0) uniform int ShadowIndex;
layout(location = 1) uniform int FaceIndex;

void main()
{
    uint meshID = gl_DrawID;
    Mesh mesh = meshSSBO.Meshes[meshID];
    if (gl_GlobalInvocationID.x >= mesh.MeshletCount)
    {
        return;
    }

    uint8_t localMeshlet = uint8_t(gl_LocalInvocationIndex);
    uint workgroupFirstMeshlet = mesh.MeshletsStart + (gl_WorkGroupID.x * MESHLETS_PER_WORKGROUP);
    uint workgroupThisMeshlet = workgroupFirstMeshlet + localMeshlet;

    DrawElementsCmd drawCmd = drawElementsCmdSSBO.DrawCommands[meshID];
    MeshletInfo meshletInfo = meshletInfoSSBO.MeshletsInfo[workgroupThisMeshlet];
    
    mat4 projView = shadowDataUBO.PointShadows[ShadowIndex].ProjViewMatrices[FaceIndex];
    mat4 model = meshInstanceSSBO.MeshInstances[drawCmd.BaseInstance].ModelMatrix;

    Frustum frustum = GetFrustum(projView * model);
    bool isVisible = FrustumBoxIntersect(frustum, Box(meshletInfo.Min, meshletInfo.Max));
    
    uvec4 visibleMeshletsBitmask = subgroupBallot(isVisible);
    if (isVisible)
    {
        uint offset = subgroupBallotExclusiveBitCount(visibleMeshletsBitmask);
        outData.SurvivingMeshlets[offset] = localMeshlet;
    }

    if (gl_LocalInvocationIndex == 0)
    {
        outData.MeshletsStart = workgroupFirstMeshlet;
        outData.MeshID = meshID;
        
        uint survivingCount = subgroupBallotBitCount(visibleMeshletsBitmask);
        gl_TaskCountNV = survivingCount;
    }
}