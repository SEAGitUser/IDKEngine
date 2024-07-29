﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SharpGLTF.Materials;
using Ktx;
using Meshoptimizer;
using BBLogger;
using BBOpenGL;
using IDKEngine.Utils;
using IDKEngine.Shapes;
using IDKEngine.GpuTypes;
using GLTexture = BBOpenGL.BBG.Texture;
using GLSampler = BBOpenGL.BBG.Sampler;
using GltfTexture = SharpGLTF.Schema2.Texture;
using GltfSampler = SharpGLTF.Schema2.TextureSampler;
using GltfNode = SharpGLTF.Schema2.Node;

namespace IDKEngine
{
    public static class ModelLoader
    {
        public static readonly string[] SupportedExtensions = [
            "KHR_materials_emissive_strength",
            "KHR_materials_volume",
            "KHR_materials_ior",
            "KHR_materials_transmission",
            "EXT_mesh_gpu_instancing",
            "KHR_texture_basisu",
            "IDK_BC5_normal_metallicRoughness"
        ];

        public record struct CpuModel
        {
            public Node RootNode;
            public GpuModel Model;
        }

        public record struct GpuModel
        {
            public BBG.DrawElementsIndirectCommand[] DrawCommands;
            public GpuMesh[] Meshes;
            public GpuMeshInstance[] MeshInstances;
            public GpuMaterial[] Materials;

            // Base geometry
            public GpuVertex[] Vertices;
            public Vector3[] VertexPositions;
            public uint[] VertexIndices;

            // Meshlet-rendering specific data
            public GpuMeshlet[] Meshlets;
            public GpuMeshletInfo[] MeshletsInfo;
            public uint[] MeshletsVertexIndices;
            public byte[] MeshletsLocalIndices;

            // Animations
            public uint[] JointIndices;
            public float[] JointWeights;
            public Matrix4[] JointMatrices;
        }

        public class Node
        {
            public bool IsRoot => Parent == null;
            public bool IsLeaf => Children.Length == 0;

            private Transformation _localTransform;
            public Transformation LocalTransform
            {
                get => _localTransform;

                set
                {
                    _localTransform = value;
                    MarkDirty();
                }
            }

            public Matrix4 GlobalTransform { get; private set; } = Matrix4.Identity; // local * parent.Global

            public Node Parent;
            public Node[] Children = Array.Empty<Node>();
            public Range MeshInstanceIds;
            public string Name = string.Empty;

            private bool isDirty;

            public void UpdateGlobalTransform()
            {
                if (IsRoot)
                {
                    GlobalTransform = LocalTransform.Matrix;
                }
                else
                {
                    GlobalTransform = LocalTransform.Matrix * Parent.GlobalTransform;
                }
            }

            public Node DeepClone()
            {
                Node newNode = new Node();
                newNode.Name = new string(Name);
                newNode.MeshInstanceIds = MeshInstanceIds;
                newNode.LocalTransform = LocalTransform;
                newNode.GlobalTransform = GlobalTransform;

                newNode.Children = new Node[Children.Length];
                for (int i = 0; i < Children.Length; i++)
                {
                    newNode.Children[i] = Children[i].DeepClone();
                    newNode.Children[i].Parent = newNode;
                }

                return newNode;
            }

            private void MarkDirty()
            {
                MarkParentsDirty(this);
                Traverse(this, (Node node) =>
                {
                    if (node.isDirty)
                    {
                        // Stop traversing if node, and subsequently its children, are already marked dirty
                        return false;
                    }

                    node.isDirty = true;
                    return true;
                });

                void MarkParentsDirty(Node node)
                {
                    if (!node.IsRoot)
                    {
                        node.Parent.isDirty = true;
                        MarkParentsDirty(node.Parent);
                    }
                }
            }

            /// <summary>
            /// Traverses all dirty nodes and marks them as no longer dirty. The caller is responsible for calling
            /// <seealso cref="UpdateGlobalTransform"/> inside <paramref name="updateNode"/>
            /// </summary>
            /// <param name="parent"></param>
            /// <param name="updateNode"></param>
            public static void TraverseUpdate(Node parent, Action<Node> updateNode)
            {
                if (!parent.isDirty)
                {
                    return;
                }

                updateNode(parent);
                parent.isDirty = false;

                for (int i = 0; i < parent.Children.Length; i++)
                {
                    TraverseUpdate(parent.Children[i], updateNode);
                }
            }

            public static void Traverse(Node node, Func<Node, bool> continueTraversal)
            {
                if (!continueTraversal(node))
                {
                    return;
                }
                for (int i = 0; i < node.Children.Length; i++)
                {
                    Traverse(node.Children[i], continueTraversal);
                }
            }
        }

        /// <summary>
        /// See <see href="https://github.com/zeux/meshoptimizer"></see> for details.
        /// When gltfpack is run these optimizations are already applied so doing them again is useless
        /// </summary>
        public record struct OptimizationSettings
        {
            /// <summary>
            /// <see href="https://github.com/zeux/meshoptimizer/tree/master?tab=readme-ov-file#indexing"></see>
            /// </summary>
            public bool VertexRemapOptimization;

            /// <summary>
            /// <see href="https://github.com/zeux/meshoptimizer/tree/master?tab=readme-ov-file#vertex-cache-optimization"></see>
            /// </summary>
            public bool VertexCacheOptimization;

            /// <summary>
            /// <see href="https://github.com/zeux/meshoptimizer/tree/master?tab=readme-ov-file#vertex-fetch-optimization"></see>
            /// </summary>
            public bool VertexFetchOptimization;

            public static readonly OptimizationSettings AllTurnedOff = new OptimizationSettings()
            {
                VertexRemapOptimization = false,
                VertexCacheOptimization = false,
                VertexFetchOptimization = false,
            };

            public static readonly OptimizationSettings Recommended = new OptimizationSettings()
            {
                VertexRemapOptimization = false,
                VertexCacheOptimization = true,
                VertexFetchOptimization = false,
            };
        }

        private record struct MaterialLoadData
        {
            public static readonly int TEXTURE_COUNT = Enum.GetValues<TextureType>().Length;

            public enum TextureType : int
            {
                BaseColor,
                MetallicRoughness,
                Normal,
                Emissive,
                Transmission,
            }

            public ref GltfTexture this[TextureType textureType]
            {
                get
                {
                    switch (textureType)
                    {
                        case TextureType.BaseColor: return ref Unsafe.AsRef(ref BaseColorTexture);
                        case TextureType.MetallicRoughness: return ref Unsafe.AsRef(ref MetallicRoughnessTexture);
                        case TextureType.Normal: return ref Unsafe.AsRef(ref NormalTexture);
                        case TextureType.Emissive: return ref Unsafe.AsRef(ref EmissiveTexture);
                        case TextureType.Transmission: return ref Unsafe.AsRef(ref TransmissionTexture);
                        default: throw new NotSupportedException($"Unsupported {nameof(TextureType)} {textureType}");
                    }
                }
            }

            public MaterialParams MaterialParams;

            public GltfTexture BaseColorTexture;
            public GltfTexture MetallicRoughnessTexture;
            public GltfTexture NormalTexture;
            public GltfTexture EmissiveTexture;
            public GltfTexture TransmissionTexture;

            public static readonly MaterialLoadData Default = new MaterialLoadData()
            {
                BaseColorTexture = { },
                MetallicRoughnessTexture = { },
                NormalTexture = { },
                EmissiveTexture = { },
                TransmissionTexture = { },
                MaterialParams = MaterialParams.Default,
            };
        }

        private record struct MaterialParams
        {
            public Vector3 EmissiveFactor;
            public Vector4 BaseColorFactor;
            public float TransmissionFactor;
            public float AlphaCutoff;
            public float RoughnessFactor;
            public float MetallicFactor;
            public Vector3 Absorbance;
            public float IOR;
            public bool DoAlphaBlending;

            public static readonly MaterialParams Default = new MaterialParams()
            {
                EmissiveFactor = new Vector3(0.0f),
                BaseColorFactor = new Vector4(1.0f),
                TransmissionFactor = 0.0f,
                AlphaCutoff = 0.5f,
                RoughnessFactor = 1.0f,
                MetallicFactor = 1.0f,
                Absorbance = new Vector3(0.0f),
                IOR = 1.0f, // by spec 1.5 IOR would be correct
            };
        }

        private record struct MeshGeometry
        {
            public MeshletData MeshletData;
            public GpuMeshlet[] Meshlets;
            public GpuMeshletInfo[] MeshletsInfo;

            public VertexData VertexData;
            public uint[] VertexIndices;
        }

        private record struct MeshletData
        {
            public Meshopt.Meshlet[] Meshlets;
            public int MeshletsLength;

            public uint[] VertexIndices;
            public int VertexIndicesLength;

            public byte[] LocalIndices;
            public int LocalIndicesLength;
        }

        private record struct VertexData
        {
            public GpuVertex[] Vertices;
            public Vector3[] Positons;

            public uint[] JointIndices;
            public float[] JointWeights;
        }

        private record struct GltfMeshPrimitiveDesc
        {
            public bool HasNormalAccessor => NormalAccessor != -1;
            public bool HasTexCoordAccessor => TexCoordAccessor != -1;
            public bool HasJointsAccessor => JointsAccessor != -1;
            public bool HasWeightsAccessor => WeightsAccessor != -1;
            public bool HasIndexAccessor => IndexAccessor != -1;

            public int PositionAccessor;
            public int NormalAccessor = -1;
            public int TexCoordAccessor = -1;
            public int JointsAccessor = -1;
            public int WeightsAccessor = -1;
            public int IndexAccessor = -1;

            public GltfMeshPrimitiveDesc()
            {
            }
        }

        private record struct GltfTextureDesc
        {
            public bool IsKtx2Compressed => Ktx2Texture != null;
            public int Width => IsKtx2Compressed ? Ktx2Texture.BaseWidth : ImageHeader.Width;
            public int Height => IsKtx2Compressed ? Ktx2Texture.BaseHeight : ImageHeader.Height;

            public Ktx2Texture Ktx2Texture;

            public ImageLoader.ImageHeader ImageHeader;
            public ReadOnlyMemory<byte> ImageData;

            public GLTexture.InternalFormat InternalFormat;
            public GLSampler.SamplerState SamplerState;
        }

        private unsafe struct USVec4
        {
            public fixed ushort Data[4];
        }

        public static event Action? TextureLoaded;

        public static CpuModel? LoadGltfFromFile(string path)
        {
            return LoadGltfFromFile(path, Matrix4.Identity);
        }

        public static CpuModel? LoadGltfFromFile(string path, in Matrix4 rootTransform)
        {
            return LoadGltfFromFile(path, rootTransform, OptimizationSettings.AllTurnedOff);
        }

        public static CpuModel? LoadGltfFromFile(string path, in Matrix4 rootTransform, OptimizationSettings optimizationSettings)
        {
            if (!File.Exists(path))
            {
                Logger.Log(Logger.LogLevel.Error, $"File \"{path}\" does not exist");
                return null;
            }
            
            ModelRoot gltf = ModelRoot.Load(path, new ReadSettings() { Validation = SharpGLTF.Validation.ValidationMode.Skip });
            string fileName = Path.GetFileName(path);
            foreach (string ext in gltf.ExtensionsUsed)
            {
                if (SupportedExtensions.Contains(ext))
                {
                    Logger.Log(Logger.LogLevel.Info, $"Model \"{fileName}\" uses extension {ext}");
                }
                else
                {
                    Logger.Log(Logger.LogLevel.Warn, $"Model \"{fileName}\" uses extension {ext} which is not supported");
                }
            }

            if (!GtlfpackWrapper.IsCompressed(gltf))
            {
                Logger.Log(Logger.LogLevel.Warn, $"Model \"{fileName}\" is uncompressed");
            }

            bool usesExtBc5NormalMetallicRoughness = gltf.ExtensionsUsed.Contains("IDK_BC5_normal_metallicRoughness");
            if (gltf.ExtensionsUsed.Contains("KHR_texture_basisu") && !usesExtBc5NormalMetallicRoughness)
            {
                Logger.Log(Logger.LogLevel.Warn, $"Model \"{fileName}\" uses extension KHR_texture_basisu without IDK_BC5_normal_metallicRoughness,\n" +
                                                  "causing normal and metallicRoughness textures with a suboptimal format (BC7) and potentially visible error.\n" +
                                                  "Optimal compression can be achieved with https://github.com/BoyBaykiller/meshoptimizer");
            }

            Stopwatch sw = Stopwatch.StartNew();
            CpuModel model = GltfToEngineFormat(gltf, rootTransform, optimizationSettings, usesExtBc5NormalMetallicRoughness, Path.GetFileName(path));
            sw.Stop();

            nint totalIndicesCount = 0;
            for (int i = 0; i < model.Model.DrawCommands.Length; i++)
            {
                ref readonly BBG.DrawElementsIndirectCommand cmd = ref model.Model.DrawCommands[i];
                totalIndicesCount += cmd.IndexCount * cmd.InstanceCount;
            }
            Logger.Log(Logger.LogLevel.Info, $"Loaded \"{fileName}\" in {sw.ElapsedMilliseconds}ms (Triangles = {totalIndicesCount / 3})");

            return model;
        }

        private static CpuModel GltfToEngineFormat(ModelRoot gltf, in Matrix4 rootTransform, OptimizationSettings optimizationSettings, bool useExtBc5NormalMetallicRoughness, string modelName)
        {
            Dictionary<GltfMeshPrimitiveDesc, MeshGeometry> meshPrimitivesGeometry = LoadMeshPrimitivesGeometry(gltf, optimizationSettings);
            List<GpuMaterial> listMaterials = new List<GpuMaterial>(LoadGpuMaterials(GetMaterialLoadDataFromGltf(gltf.LogicalMaterials), useExtBc5NormalMetallicRoughness));
            List<GpuMesh> listMeshes = new List<GpuMesh>();
            List<GpuMeshInstance> listMeshInstances = new List<GpuMeshInstance>();
            List<BBG.DrawElementsIndirectCommand> listDrawCommands = new List<BBG.DrawElementsIndirectCommand>();
            List<GpuVertex> listVertices = new List<GpuVertex>();
            List<Vector3> listVertexPositions = new List<Vector3>();
            List<uint> listIndices = new List<uint>();
            List<GpuMeshlet> listMeshlets = new List<GpuMeshlet>();
            List<GpuMeshletInfo> listMeshletsInfo = new List<GpuMeshletInfo>();
            List<uint> listMeshletsVertexIndices = new List<uint>();
            List<byte> listMeshletsLocalIndices = new List<byte>();
            List<uint> listJointIndices = new List<uint>();
            List<float> listJointWeights = new List<float>();
            List<Matrix4> listJointMatrices = new List<Matrix4>();

            Node myRoot = new Node();
            myRoot.Name = modelName;
            myRoot.LocalTransform = Transformation.FromMatrix(rootTransform);
            myRoot.UpdateGlobalTransform();

            Stack<ValueTuple<GltfNode, Node>> nodeStack = new Stack<ValueTuple<GltfNode, Node>>(gltf.LogicalNodes.Count);
            {
                GltfNode[] gltfChildren = gltf.DefaultScene.VisualChildren.ToArray();
                myRoot.Children = new Node[gltfChildren.Length];
                for (int i = 0; i < gltfChildren.Length; i++)
                {
                    GltfNode gltfNode = gltfChildren[i];

                    Node myNode = new Node();
                    myNode.Parent = myRoot;
                    myNode.Name = gltfNode.Name ?? $"RootNode_{i}";
                    myRoot.Children[i] = myNode;

                    nodeStack.Push((gltfNode, myNode));
                }
            }

            while (nodeStack.Count > 0)
            {
                (GltfNode gltfNode, Node myNode) = nodeStack.Pop();

                myNode.LocalTransform = Transformation.FromMatrix(gltfNode.LocalMatrix.ToOpenTK());
                myNode.UpdateGlobalTransform();

                {
                    GltfNode[] gltfChildren = gltfNode.VisualChildren.ToArray();
                    myNode.Children = new Node[gltfChildren.Length];
                    for (int i = 0; i < gltfChildren.Length; i++)
                    {
                        GltfNode gltfChild = gltfChildren[i];
                        
                        Node myChild = new Node();
                        myChild.Parent = myNode;
                        myChild.Name = gltfChild.Name ?? $"ChildNode_{i}";
                        myNode.Children[i] = myChild;

                        nodeStack.Push((gltfChild, myChild));
                    }
                }

                Mesh gltfMesh = gltfNode.Mesh;
                if (gltfMesh == null)
                {
                    continue;
                }

                if (gltfNode.Skin != null)
                {
                    IterateAccessor(gltfNode.Skin.GetInverseBindMatricesAccessor(), (in Matrix4 jointInverseTransform, int i) =>
                    {
                        listJointMatrices.Add(jointInverseTransform * myNode.GlobalTransform);
                    });
                }

                Matrix4[] nodeTransformations = GetNodeInstances(gltfNode.UseGpuInstancing(), myNode.LocalTransform.Matrix);

                Range meshInstanceIdsRange = new Range();
                meshInstanceIdsRange.Start = listMeshInstances.Count;

                for (int i = 0; i < gltfMesh.Primitives.Count; i++)
                {
                    MeshPrimitive gltfMeshPrimitive = gltfMesh.Primitives[i];
                    if (!meshPrimitivesGeometry.TryGetValue(GetMeshDesc(gltfMeshPrimitive), out MeshGeometry meshGeometry))
                    {
                        continue;
                    }

                    for (int j = 0; j < meshGeometry.Meshlets.Length; j++)
                    {
                        ref GpuMeshlet myMeshlet = ref meshGeometry.Meshlets[j];

                        // Adjust offsets in context of all meshlets
                        myMeshlet.VertexOffset += (uint)listMeshletsVertexIndices.Count;
                        myMeshlet.IndicesOffset += (uint)listMeshletsLocalIndices.Count;
                    }

                    GpuMesh mesh = new GpuMesh();
                    mesh.InstanceCount = nodeTransformations.Length;
                    mesh.EmissiveBias = 0.0f;
                    mesh.SpecularBias = 0.0f;
                    mesh.RoughnessBias = 0.0f;
                    mesh.TransmissionBias = 0.0f;
                    mesh.MeshletsStart = listMeshlets.Count;
                    mesh.MeshletCount = meshGeometry.Meshlets.Length;
                    mesh.IORBias = 0.0f;
                    mesh.AbsorbanceBias = new Vector3(0.0f);
                    if (gltfMeshPrimitive.Material != null)
                    {
                        bool hasNormalMap = listMaterials[gltfMeshPrimitive.Material.LogicalIndex].NormalTexture != FallbackTextures.White();
                        mesh.NormalMapStrength = hasNormalMap ? 1.0f : 0.0f;
                        mesh.MaterialIndex = gltfMeshPrimitive.Material.LogicalIndex;
                    }
                    else
                    {
                        GpuMaterial defaultGpuMaterial = LoadGpuMaterials([MaterialLoadData.Default])[0];
                        listMaterials.Add(defaultGpuMaterial);

                        mesh.NormalMapStrength = 0.0f;
                        mesh.MaterialIndex = listMaterials.Count - 1;
                    }

                    GpuMeshInstance[] meshInstances = new GpuMeshInstance[mesh.InstanceCount];
                    for (int j = 0; j < meshInstances.Length; j++)
                    {
                        ref GpuMeshInstance meshInstance = ref meshInstances[j];
                        meshInstance.ModelMatrix = nodeTransformations[j] * myNode.Parent.GlobalTransform;
                        meshInstance.MeshIndex = listMeshes.Count;
                    }

                    BBG.DrawElementsIndirectCommand drawCmd = new BBG.DrawElementsIndirectCommand();
                    drawCmd.IndexCount = meshGeometry.VertexIndices.Length;
                    drawCmd.InstanceCount = meshInstances.Length;
                    drawCmd.FirstIndex = listIndices.Count;
                    drawCmd.BaseVertex = listVertices.Count;
                    drawCmd.BaseInstance = listMeshInstances.Count;

                    listVertices.AddRange(meshGeometry.VertexData.Vertices);
                    listVertexPositions.AddRange(meshGeometry.VertexData.Positons);
                    listIndices.AddRange(meshGeometry.VertexIndices);
                    listMeshes.Add(mesh);
                    listMeshInstances.AddRange(meshInstances);
                    listDrawCommands.Add(drawCmd);
                    listMeshlets.AddRange(meshGeometry.Meshlets);
                    listMeshletsInfo.AddRange(meshGeometry.MeshletsInfo);
                    listMeshletsVertexIndices.AddRange(new ReadOnlySpan<uint>(meshGeometry.MeshletData.VertexIndices, 0, meshGeometry.MeshletData.VertexIndicesLength));
                    listMeshletsLocalIndices.AddRange(new ReadOnlySpan<byte>(meshGeometry.MeshletData.LocalIndices, 0, meshGeometry.MeshletData.LocalIndicesLength));
                    listJointIndices.AddRange(meshGeometry.VertexData.JointIndices);
                    listJointWeights.AddRange(meshGeometry.VertexData.JointWeights);
                }

                meshInstanceIdsRange.End = listMeshInstances.Count;
                myNode.MeshInstanceIds = meshInstanceIdsRange;
            }

            GpuModel model = new GpuModel();
            model.Meshes = listMeshes.ToArray();
            model.MeshInstances = listMeshInstances.ToArray();
            model.Materials = listMaterials.ToArray();
            model.DrawCommands = listDrawCommands.ToArray();
            model.Vertices = listVertices.ToArray();
            model.VertexPositions = listVertexPositions.ToArray();
            model.VertexIndices = listIndices.ToArray();
            model.Meshlets = listMeshlets.ToArray();
            model.MeshletsInfo = listMeshletsInfo.ToArray();
            model.MeshletsVertexIndices = listMeshletsVertexIndices.ToArray();
            model.MeshletsLocalIndices = listMeshletsLocalIndices.ToArray();
            model.JointIndices = listJointIndices.ToArray();
            model.JointWeights = listJointWeights.ToArray();
            model.JointMatrices = listJointMatrices.ToArray();

            CpuModel cpuModel = new CpuModel();
            cpuModel.Model = model;
            cpuModel.RootNode = myRoot;

            return cpuModel;
        }

        private static GpuMaterial[] LoadGpuMaterials(ReadOnlySpan<MaterialLoadData> materialsLoadData, bool useExtBc5NormalMetallicRoughness = false)
        {
            int maxTextures = MaterialLoadData.TEXTURE_COUNT * materialsLoadData.Length;
            Dictionary<GltfTextureDesc, GLTexture.BindlessHandle> uniqueBindlessHandles = new Dictionary<GltfTextureDesc, GLTexture.BindlessHandle>(maxTextures);

            GpuMaterial[] gpuMaterials = new GpuMaterial[materialsLoadData.Length];
            for (int i = 0; i < gpuMaterials.Length; i++)
            {
                ref readonly MaterialLoadData materialLoadData = ref materialsLoadData[i];
                ref GpuMaterial gpuMaterial = ref gpuMaterials[i];

                MaterialParams materialParams = materialLoadData.MaterialParams;
                gpuMaterial.EmissiveFactor = materialParams.EmissiveFactor;
                gpuMaterial.BaseColorFactor = Compression.CompressUR8G8B8A8(materialParams.BaseColorFactor);
                gpuMaterial.TransmissionFactor = materialParams.TransmissionFactor;
                gpuMaterial.AlphaCutoff = materialParams.AlphaCutoff;
                gpuMaterial.RoughnessFactor = materialParams.RoughnessFactor;
                gpuMaterial.MetallicFactor = materialParams.MetallicFactor;
                gpuMaterial.Absorbance = materialParams.Absorbance;
                gpuMaterial.IOR = materialParams.IOR;
                gpuMaterial.DoAlphaBlending = materialParams.DoAlphaBlending;

                for (int j = 0; j < GpuMaterial.TEXTURE_COUNT; j++)
                {
                    GpuMaterial.BindlessHandle textureType = (GpuMaterial.BindlessHandle)j;
                    GltfTexture gltfTexture = materialLoadData[(MaterialLoadData.TextureType)textureType];

                    if (gltfTexture == null)
                    {
                        // By having a pure white fallback we can keep the sampling logic
                        // in shaders the same and still comply to glTF spec
                        gpuMaterial[textureType] = FallbackTextures.White();
                        continue;
                    }

                    if (!TryGetGltfImageDesc(gltfTexture, textureType, useExtBc5NormalMetallicRoughness, out GltfTextureDesc textureDesc))
                    {
                        goto FallbackTexture;
                    }
                    if (!uniqueBindlessHandles.TryGetValue(textureDesc, out GLTexture.BindlessHandle bindlessHandle))
                    {
                        if (AsyncLoadGltfTexture(textureDesc, textureType, useExtBc5NormalMetallicRoughness, out GLTexture glTexture, out GLSampler glSampler))
                        {
                            bindlessHandle = glTexture.GetTextureHandleARB(glSampler);
                            uniqueBindlessHandles[textureDesc] = bindlessHandle;
                        }
                        else
                        {
                            goto FallbackTexture;
                        }
                    }

                    gpuMaterial[textureType] = bindlessHandle;
                    continue;

                    FallbackTexture:
                    if (textureType == GpuMaterial.BindlessHandle.BaseColor)
                    {
                        gpuMaterial[textureType] = FallbackTextures.PurpleBlack();
                    }
                    else
                    {
                        gpuMaterial[textureType] = FallbackTextures.White();
                    }
                }
            }

            return gpuMaterials;
        }

        private static unsafe bool AsyncLoadGltfTexture(GltfTextureDesc textureDesc, GpuMaterial.BindlessHandle textureType, bool useExtBc5NormalMetallicRoughness, out GLTexture glTexture, out GLSampler glSampler)
        {
            glSampler = new GLSampler(textureDesc.SamplerState);
            glTexture = new GLTexture(GLTexture.Type.Texture2D);
            if (textureType == GpuMaterial.BindlessHandle.MetallicRoughness && !useExtBc5NormalMetallicRoughness)
            {
                // By the spec "The metalness values are sampled from the B channel. The roughness values are sampled from the G channel"
                // We move metallic from B into R channel, unless IDK_BC5_normal_metallicRoughness is used where this is already standard behaviour.
                glTexture.SetSwizzleR(GLTexture.Swizzle.B);
            }

            bool mipmapsRequired = GLSampler.IsMipmapFilter(glSampler.State.MinFilter);
            int levels = textureDesc.IsKtx2Compressed ? textureDesc.Ktx2Texture.Levels : GLTexture.GetMaxMipmapLevel(textureDesc.Width, textureDesc.Height, 1);
            if (!mipmapsRequired)
            {
                levels = 1;
            }

            glTexture.ImmutableAllocate(textureDesc.Width, textureDesc.Height, 1, textureDesc.InternalFormat, levels);

            GLTexture glTextureCopy = glTexture;
            MainThreadQueue.AddToLazyQueue(() =>
            {
                /* For compressed textures:
                 * 1. Transcode the KTX texture into GPU compressed format in parallel 
                 * 2. Create staging buffer on main thread
                 * 3. Copy compressed pixels to staging buffer in parallel
                 * 4. Copy from staging buffer to texture on main thread
                 */

                /* For uncompressed textures:
                 * 1. Create staging buffer on main thread
                 * 2. Decode image and copy the pixels into staging buffer in parallel
                 * 3. Copy from staging buffer to texture on main thread
                 */

                // TODO: If the main thread is in Sleep State (for example when waiting on Parallel.For() to finish)
                //       it may end up participating as a worker in the ThreadPool.
                //       We want the main thread to only run the render loop only and not some random
                //       ThreadPool work (like loading texturs in this case), because it causes frame stutters. Fix!
                int threadPoolThreads = Math.Max(Environment.ProcessorCount / 2, 1);
                ThreadPool.SetMinThreads(threadPoolThreads, 1);
                ThreadPool.SetMaxThreads(threadPoolThreads, 1);

                if (textureDesc.IsKtx2Compressed)
                {
                    Task.Run(() =>
                    {
                        if (textureDesc.Ktx2Texture.NeedsTranscoding)
                        {
                            //int supercompressedImageSize = ktx2Texture.DataSize; // Supercompressed size before transcoding
                            Ktx2.ErrorCode errCode = textureDesc.Ktx2Texture.Transcode(GLFormatToKtxFormat(glTextureCopy.Format), Ktx2.TranscodeFlagBits.HighQuality);
                            if (errCode != Ktx2.ErrorCode.Success)
                            {
                                Logger.Log(Logger.LogLevel.Error, $"Failed to transcode KTX texture. {nameof(textureDesc.Ktx2Texture.Transcode)} returned {errCode}");
                                return;
                            }
                        }

                        MainThreadQueue.AddToLazyQueue(() =>
                        {
                            int compressedImageSize = textureDesc.Ktx2Texture.DataSize; // Compressed size after transcoding

                            BBG.TypedBuffer<byte> stagingBuffer = new BBG.TypedBuffer<byte>();
                            stagingBuffer.ImmutableAllocate(BBG.Buffer.MemLocation.DeviceLocal, BBG.Buffer.MemAccess.MappedIncoherent, compressedImageSize);

                            Task.Run(() =>
                            {
                                Memory.Copy(textureDesc.Ktx2Texture.Data, stagingBuffer.MappedMemory, compressedImageSize);

                                MainThreadQueue.AddToLazyQueue(() =>
                                {
                                    for (int level = 0; level < glTextureCopy.Levels; level++)
                                    {
                                        textureDesc.Ktx2Texture.GetImageDataOffset(level, out nint dataOffset);
                                        Vector3i size = GLTexture.GetMipmapLevelSize(textureDesc.Ktx2Texture.BaseWidth, textureDesc.Ktx2Texture.BaseHeight, textureDesc.Ktx2Texture.BaseDepth, level);
                                        glTextureCopy.UploadCompressed2D(stagingBuffer, size.X, size.Y, dataOffset, level);
                                    }
                                    stagingBuffer.Dispose();
                                    textureDesc.Ktx2Texture.Dispose();

                                    TextureLoaded?.Invoke();
                                });
                            });
                        });
                    });
                }
                else
                {
                    int imageSize = textureDesc.ImageHeader.Width * textureDesc.ImageHeader.Height * textureDesc.ImageHeader.Channels;
                    BBG.TypedBuffer<byte> stagingBuffer = new BBG.TypedBuffer<byte>();
                    stagingBuffer.ImmutableAllocateElements(BBG.Buffer.MemLocation.DeviceLocal, BBG.Buffer.MemAccess.MappedIncoherent, imageSize);

                    Task.Run(() =>
                    {
                        ReadOnlySpan<byte> imageData = textureDesc.ImageData.Span;
                        using ImageLoader.ImageResult imageResult = ImageLoader.Load(imageData, textureDesc.ImageHeader.Channels);

                        if (imageResult.RawPixels == null)
                        {
                            Logger.Log(Logger.LogLevel.Error, $"Image could not be decoded");
                            MainThreadQueue.AddToLazyQueue(() => { stagingBuffer.Dispose(); });
                            return;
                        }
                        Memory.Copy(imageResult.RawPixels, stagingBuffer.MappedMemory, imageSize);

                        MainThreadQueue.AddToLazyQueue(() =>
                        {
                            glTextureCopy.Upload2D(stagingBuffer, textureDesc.ImageHeader.Width, textureDesc.ImageHeader.Height, GLTexture.NumChannelsToPixelFormat(textureDesc.ImageHeader.Channels), GLTexture.PixelType.UByte, null);
                            if (mipmapsRequired)
                            {
                                glTextureCopy.GenerateMipmap();
                            }
                            stagingBuffer.Dispose();

                            TextureLoaded?.Invoke();
                        });
                    });
                }
            });
            
            return true;
        } 

        private static bool TryGetGltfImageDesc(GltfTexture gltfTexture, GpuMaterial.BindlessHandle textureType, bool useExtBc5NormalMetallicRoughness, out GltfTextureDesc textureDesc)
        {
            Image gltfImage = gltfTexture.PrimaryImage;
            textureDesc = new GltfTextureDesc();

            if (gltfImage.Content.IsPng || gltfImage.Content.IsJpg)
            {
                textureDesc.ImageData = gltfImage.Content.Content;
                if (!ImageLoader.TryGetImageHeader(textureDesc.ImageData.Span, out textureDesc.ImageHeader))
                {
                    Logger.Log(Logger.LogLevel.Error, $"Error parsing header of image \"{gltfImage.Name}\"");
                    return false;
                }

                textureDesc.InternalFormat = textureType switch
                {
                    GpuMaterial.BindlessHandle.BaseColor => GLTexture.InternalFormat.R8G8B8A8Srgb,
                    GpuMaterial.BindlessHandle.Emissive => GLTexture.InternalFormat.R8G8B8A8Srgb,
                    GpuMaterial.BindlessHandle.MetallicRoughness => GLTexture.InternalFormat.R11G11B10Float, // MetallicRoughnessTexture stores metalness and roughness in G and B components. Therefore need to load 3 channels :(
                    GpuMaterial.BindlessHandle.Normal => GLTexture.InternalFormat.R8G8Unorm,
                    GpuMaterial.BindlessHandle.Transmission => GLTexture.InternalFormat.R8Unorm,
                    _ => throw new NotSupportedException($"{nameof(MaterialLoadData.TextureType)} = {textureType} not supported")
                };
                if (textureType == GpuMaterial.BindlessHandle.Transmission)
                {
                    textureDesc.ImageHeader.SetChannels(ImageLoader.ColorComponents.R);
                }
                if (textureType == GpuMaterial.BindlessHandle.Emissive || textureType == GpuMaterial.BindlessHandle.MetallicRoughness)
                {
                    textureDesc.ImageHeader.SetChannels(ImageLoader.ColorComponents.RGB);
                }
            }
            else if (gltfImage.Content.IsKtx2)
            {
                ReadOnlySpan<byte> imageData = gltfImage.Content.Content.Span;

                Ktx2.ErrorCode errCode = Ktx2Texture.FromMemory(imageData, Ktx2.TextureCreateFlag.LoadImageDataBit, out textureDesc.Ktx2Texture);
                if (errCode != Ktx2.ErrorCode.Success)
                {
                    Logger.Log(Logger.LogLevel.Error, $"Failed to load KTX texture. {nameof(Ktx2Texture.FromMemory)} returned {errCode}");
                    return false;
                }
                if (!textureDesc.Ktx2Texture.NeedsTranscoding)
                {
                    Logger.Log(Logger.LogLevel.Error, "KTX textures are expected to require transcoding, meaning they are either ETC1S or UASTC encoded.\n" +
                                                        $"SupercompressionScheme = {textureDesc.Ktx2Texture.SupercompressionScheme}");
                    return false;
                }

                textureDesc.InternalFormat = textureType switch
                {
                    GpuMaterial.BindlessHandle.BaseColor => GLTexture.InternalFormat.BC7RgbaSrgb,
                    GpuMaterial.BindlessHandle.Emissive => GLTexture.InternalFormat.BC7RgbaSrgb,

                    // BC5 support added with gltfpack fork (https://github.com/BoyBaykiller/meshoptimizer) implementing IDK_BC5_normal_metallicRoughness
                    GpuMaterial.BindlessHandle.MetallicRoughness => useExtBc5NormalMetallicRoughness ? GLTexture.InternalFormat.BC5RgUnorm : GLTexture.InternalFormat.BC7RgbaUnorm,
                    GpuMaterial.BindlessHandle.Normal => useExtBc5NormalMetallicRoughness ? GLTexture.InternalFormat.BC5RgUnorm : GLTexture.InternalFormat.BC7RgbaUnorm,

                    GpuMaterial.BindlessHandle.Transmission => GLTexture.InternalFormat.BC4RUnorm,
                    _ => throw new NotSupportedException($"{nameof(textureType)} = {textureType} not supported")
                };
            }
            else
            {
                Logger.Log(Logger.LogLevel.Error, $"Unsupported MimeType = {gltfImage.Content.MimeType}");
                return false;
            }

            textureDesc.SamplerState = GetGLSamplerState(gltfTexture.Sampler);

            return true;
        }

        private static MaterialLoadData[] GetMaterialLoadDataFromGltf(IReadOnlyList<Material> gltfMaterials)
        {
            MaterialLoadData[] materialsLoadData = new MaterialLoadData[gltfMaterials.Count];
            for (int i = 0; i < gltfMaterials.Count; i++)
            {
                Material gltfMaterial = gltfMaterials[i];
                MaterialLoadData materialLoadData = MaterialLoadData.Default;

                materialLoadData.MaterialParams = GetMaterialParams(gltfMaterial);

                for (int j = 0; j < MaterialLoadData.TEXTURE_COUNT; j++)
                {
                    MaterialLoadData.TextureType imageType = MaterialLoadData.TextureType.BaseColor + j;
                    materialLoadData[imageType] = GetGltfTexture(gltfMaterial, imageType);
                }

                materialsLoadData[i] = materialLoadData;
                //materialsLoadData[i] = MaterialLoadData.Default;
            }

            return materialsLoadData;
        }
        
        private static GltfTexture? GetGltfTexture(Material material, MaterialLoadData.TextureType textureType)
        {
            KnownChannel channel = textureType switch
            {
                MaterialLoadData.TextureType.BaseColor => KnownChannel.BaseColor,
                MaterialLoadData.TextureType.MetallicRoughness => KnownChannel.MetallicRoughness,
                MaterialLoadData.TextureType.Normal => KnownChannel.Normal,
                MaterialLoadData.TextureType.Emissive => KnownChannel.Emissive,
                MaterialLoadData.TextureType.Transmission => KnownChannel.Transmission,
                _ => throw new NotSupportedException($"Can not convert {nameof(textureType)} = {textureType} to {nameof(channel)}"),
            };

            MaterialChannel? materialChannel = material.FindChannel(channel.ToString());
            if (materialChannel.HasValue)
            {
                return materialChannel.Value.Texture;
            }

            return null;
        }
        
        private static GLSampler.SamplerState GetGLSamplerState(GltfSampler sampler)
        {
            GLSampler.SamplerState state = new GLSampler.SamplerState();
            if (sampler == null)
            {
                state.WrapModeS = GLSampler.WrapMode.Repeat;
                state.WrapModeT = GLSampler.WrapMode.Repeat;
                state.MinFilter = GLSampler.MinFilter.LinearMipmapLinear;
                state.MagFilter = GLSampler.MagFilter.Linear;
            }
            else
            {
                state.WrapModeT = (GLSampler.WrapMode)sampler.WrapT;
                state.WrapModeS = (GLSampler.WrapMode)sampler.WrapS;
                state.MinFilter = (GLSampler.MinFilter)sampler.MinFilter;
                state.MagFilter = (GLSampler.MagFilter)sampler.MagFilter;

                if (sampler.MinFilter == TextureMipMapFilter.DEFAULT)
                {
                    state.MinFilter = GLSampler.MinFilter.LinearMipmapLinear;
                }
                if (sampler.MagFilter == TextureInterpolationFilter.DEFAULT)
                {
                    state.MagFilter = GLSampler.MagFilter.Linear;
                }
            }

            bool isMipmapFilter = GLSampler.IsMipmapFilter(state.MinFilter);
            state.Anisotropy = isMipmapFilter ? GLSampler.Anisotropy.Samples8x : GLSampler.Anisotropy.Samples1x;

            return state;
        }

        private static Matrix4[] GetNodeInstances(MeshGpuInstancing gpuInstancing, in Matrix4 localTransform)
        {
            if (gpuInstancing.Count == 0)
            {
                // If node does not define transformations using EXT_mesh_gpu_instancing we must use local transform
                return [localTransform];
            }

            Matrix4[] nodeInstances = new Matrix4[gpuInstancing.Count];
            for (int i = 0; i < nodeInstances.Length; i++)
            {
                nodeInstances[i] = gpuInstancing.GetLocalMatrix(i).ToOpenTK();
            }

            return nodeInstances;
        }
        
        private static Dictionary<GltfMeshPrimitiveDesc, MeshGeometry> LoadMeshPrimitivesGeometry(ModelRoot modelRoot, OptimizationSettings optimizationSettings)
        {
            int maxMeshPrimitives = modelRoot.LogicalMeshes.Sum(it => it.Primitives.Count);

            Task[] tasks = new Task[maxMeshPrimitives];
            Dictionary<GltfMeshPrimitiveDesc, MeshGeometry> uniqueMeshPrimitives = new Dictionary<GltfMeshPrimitiveDesc, MeshGeometry>(maxMeshPrimitives);

            int uniqueMeshPrimitivesCount = 0;
            for (int i = 0; i < modelRoot.LogicalMeshes.Count; i++)
            {
                Mesh mesh = modelRoot.LogicalMeshes[i];
                for (int j = 0; j < mesh.Primitives.Count; j++)
                {
                    MeshPrimitive meshPrimitive = mesh.Primitives[j];

                    GltfMeshPrimitiveDesc meshDesc = GetMeshDesc(meshPrimitive);
                    if (meshPrimitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                    {
                        Logger.Log(Logger.LogLevel.Error, $"Unsupported {nameof(MeshPrimitive.DrawPrimitiveType)} = {meshPrimitive.DrawPrimitiveType}");
                        continue;
                    }

                    if (uniqueMeshPrimitives.TryAdd(meshDesc, new MeshGeometry()))
                    {
                        tasks[uniqueMeshPrimitivesCount++] = Task.Run(() =>
                        {
                            (VertexData meshVertexData, uint[] meshIndices) = LoadVertexAndIndices(modelRoot.LogicalAccessors, meshDesc);
                            OptimizeMesh(ref meshVertexData.Vertices, ref meshVertexData.Positons, meshIndices, optimizationSettings);

                            MeshletData meshletData = GenerateMeshlets(meshVertexData.Positons, meshIndices);
                            (GpuMeshlet[] meshMeshlets, GpuMeshletInfo[] meshMeshletsInfo) = LoadGpuMeshlets(meshletData, meshVertexData.Positons);

                            MeshGeometry meshGeometry = new MeshGeometry();
                            meshGeometry.VertexData = meshVertexData;
                            meshGeometry.VertexIndices = meshIndices;
                            meshGeometry.MeshletData = meshletData;
                            meshGeometry.Meshlets = meshMeshlets;
                            meshGeometry.MeshletsInfo = meshMeshletsInfo;

                            uniqueMeshPrimitives[meshDesc] = meshGeometry;
                        });
                    }
                }
            }
            //int deduplicatedCount = totalMeshPrimitives - uniqueMeshPrimitivesCount;
            while (uniqueMeshPrimitivesCount < tasks.Length)
            {
                tasks[uniqueMeshPrimitivesCount++] = Task.CompletedTask;
            }

            Task.WaitAll(tasks);
            uniqueMeshPrimitives.TrimExcess();

            return uniqueMeshPrimitives;
        }

        private static GltfMeshPrimitiveDesc GetMeshDesc(MeshPrimitive meshPrimitive)
        {
            Accessor positonAccessor = meshPrimitive.VertexAccessors["POSITION"];
            bool hasNormals = meshPrimitive.VertexAccessors.TryGetValue("NORMAL", out Accessor normalAccessor);
            bool hasTexCoords = meshPrimitive.VertexAccessors.TryGetValue("TEXCOORD_0", out Accessor texCoordAccessor);
            bool hasJoints = meshPrimitive.VertexAccessors.TryGetValue("JOINTS_0", out Accessor jointsAccessor);
            bool hasWeights = meshPrimitive.VertexAccessors.TryGetValue("WEIGHTS_0", out Accessor weightsAccessor);
            bool hasIndices = meshPrimitive.IndexAccessor != null;

            GltfMeshPrimitiveDesc meshDesc = new GltfMeshPrimitiveDesc();
            meshDesc.PositionAccessor = positonAccessor.LogicalIndex;
            if (hasNormals) meshDesc.NormalAccessor = normalAccessor.LogicalIndex;
            if (hasTexCoords) meshDesc.TexCoordAccessor = texCoordAccessor.LogicalIndex;
            if (hasJoints) meshDesc.JointsAccessor = jointsAccessor.LogicalIndex;
            if (hasWeights) meshDesc.WeightsAccessor = weightsAccessor.LogicalIndex;
            if (hasIndices) meshDesc.IndexAccessor = meshPrimitive.IndexAccessor.LogicalIndex;

            return meshDesc;
        }

        private static unsafe ValueTuple<VertexData, uint[]> LoadVertexAndIndices(IReadOnlyList<Accessor> accessors, GltfMeshPrimitiveDesc meshDesc)
        {
            Accessor positonAccessor = accessors[meshDesc.PositionAccessor];

            VertexData vertexData;
            vertexData.Vertices = new GpuVertex[positonAccessor.Count];
            vertexData.Positons = new Vector3[positonAccessor.Count];
            vertexData.JointIndices = Array.Empty<uint>();
            vertexData.JointWeights = Array.Empty<float>();

            IterateAccessor(positonAccessor, (in Vector3 pos, int i) =>
            {
                vertexData.Positons[i] = pos;
            });

            if (meshDesc.HasNormalAccessor)
            {
                Accessor normalAccessor = accessors[meshDesc.NormalAccessor];
                IterateAccessor(normalAccessor, (in Vector3 normal, int i) =>
                {
                    vertexData.Vertices[i].Normal = Compression.CompressSR11G11B10(normal);

                    Vector3 c1 = Vector3.Cross(normal, Vector3.UnitZ);
                    Vector3 c2 = Vector3.Cross(normal, Vector3.UnitY);
                    Vector3 tangent = Vector3.Dot(c1, c1) > Vector3.Dot(c2, c2) ? c1 : c2;
                    vertexData.Vertices[i].Tangent = Compression.CompressSR11G11B10(tangent);
                });
            }
            else
            {
                Logger.Log(Logger.LogLevel.Error, "Mesh provides no vertex normals");
            }

            if (meshDesc.HasTexCoordAccessor)
            {
                Accessor texCoordAccessor = accessors[meshDesc.TexCoordAccessor];
                if (texCoordAccessor.Encoding == EncodingType.FLOAT)
                {
                    IterateAccessor(texCoordAccessor, (in Vector2 texCoord, int i) =>
                    {
                        vertexData.Vertices[i].TexCoord = texCoord;
                    });
                }
                else
                {
                    Logger.Log(Logger.LogLevel.Error, $"Unsupported TexCoord {nameof(texCoordAccessor.Encoding)} = {texCoordAccessor.Encoding}");
                }
            }

            if (meshDesc.HasJointsAccessor)
            {
                Accessor jointsAccessor = accessors[meshDesc.JointsAccessor];
                if (jointsAccessor.Encoding == EncodingType.UNSIGNED_SHORT)
                {
                    vertexData.JointIndices = new uint[jointsAccessor.Count * 4];
                    IterateAccessor(jointsAccessor, (in USVec4 usvec4, int i) =>
                    {
                        vertexData.JointIndices[i * 4 + 0] = usvec4.Data[0];
                        vertexData.JointIndices[i * 4 + 1] = usvec4.Data[1];
                        vertexData.JointIndices[i * 4 + 2] = usvec4.Data[2];
                        vertexData.JointIndices[i * 4 + 3] = usvec4.Data[2];
                    });
                }
                else
                {
                    Logger.Log(Logger.LogLevel.Error, $"Unsupported Joints {nameof(jointsAccessor.Encoding)} = {jointsAccessor.Encoding}");
                }
            }

            if (meshDesc.HasWeightsAccessor)
            {
                Accessor weightsAccessor = accessors[meshDesc.WeightsAccessor];
                if (weightsAccessor.Encoding == EncodingType.FLOAT)
                {
                    vertexData.JointWeights = new float[weightsAccessor.Count * 4];
                    IterateAccessor(weightsAccessor, (in Vector4 weights, int i) =>
                    {
                        vertexData.JointWeights[i * 4 + 0] = weights[0];
                        vertexData.JointWeights[i * 4 + 1] = weights[1];
                        vertexData.JointWeights[i * 4 + 2] = weights[2];
                        vertexData.JointWeights[i * 4 + 3] = weights[3];
                    });
                }
                else
                {
                    Logger.Log(Logger.LogLevel.Error, $"Unsupported JointWeights {nameof(weightsAccessor.Encoding)} = {weightsAccessor.Encoding}");
                }
            }

            uint[] vertexIndices = null;
            if (meshDesc.HasIndexAccessor)
            {
                Accessor accessor = accessors[meshDesc.IndexAccessor];
                vertexIndices = new uint[accessor.Count];
                IterateAccessor(accessor, (in uint index, int i) =>
                {
                    vertexIndices[i] = index;
                });
            }
            else
            {
                vertexIndices = new uint[positonAccessor.Count];
                Helper.FillIncreasing(vertexIndices);
            }

            return (vertexData, vertexIndices);
        }

        private static ValueTuple<GpuMeshlet[], GpuMeshletInfo[]> LoadGpuMeshlets(in MeshletData meshMeshletsData, ReadOnlySpan<Vector3> meshVertexPositions)
        {
            GpuMeshlet[] gpuMeshlets = new GpuMeshlet[meshMeshletsData.MeshletsLength];
            GpuMeshletInfo[] gpuMeshletsInfo = new GpuMeshletInfo[gpuMeshlets.Length];
            for (int i = 0; i < gpuMeshlets.Length; i++)
            {
                ref GpuMeshlet meshlet = ref gpuMeshlets[i];
                ref GpuMeshletInfo meshletInfo = ref gpuMeshletsInfo[i];
                ref readonly Meshopt.Meshlet meshOptMeshlet = ref meshMeshletsData.Meshlets[i];

                meshlet.VertexOffset = meshOptMeshlet.VertexOffset;
                meshlet.VertexCount = (byte)meshOptMeshlet.VertexCount;
                meshlet.IndicesOffset = meshOptMeshlet.TriangleOffset;
                meshlet.TriangleCount = (byte)meshOptMeshlet.TriangleCount;

                Box meshletBoundingBox = Box.Empty();
                for (uint j = meshlet.VertexOffset; j < meshlet.VertexOffset + meshlet.VertexCount; j++)
                {
                    uint vertexIndex = meshMeshletsData.VertexIndices[j];
                    meshletBoundingBox.GrowToFit(meshVertexPositions[(int)vertexIndex]);
                }
                meshletInfo.Min = meshletBoundingBox.Min;
                meshletInfo.Max = meshletBoundingBox.Max;
            }

            return (gpuMeshlets, gpuMeshletsInfo);
        }

        private static MaterialParams GetMaterialParams(Material gltfMaterial)
        {
            MaterialParams materialParams = MaterialParams.Default;

            if (gltfMaterial.Alpha == SharpGLTF.Schema2.AlphaMode.OPAQUE)
            {
                materialParams.AlphaCutoff = -1.0f;
            }
            else if (gltfMaterial.Alpha == SharpGLTF.Schema2.AlphaMode.MASK)
            {
                materialParams.AlphaCutoff = gltfMaterial.AlphaCutoff;
            }
            else if (gltfMaterial.Alpha == SharpGLTF.Schema2.AlphaMode.BLEND)
            {
                // Blending only yet supported in Path Tracer
                materialParams.DoAlphaBlending = true;
            }

            MaterialChannel? baseColorChannel = gltfMaterial.FindChannel(KnownChannel.BaseColor.ToString());
            if (baseColorChannel.HasValue)
            {
                System.Numerics.Vector4 baseColor = GetMaterialChannelParam<System.Numerics.Vector4>(baseColorChannel.Value, KnownProperty.RGBA);
                materialParams.BaseColorFactor = baseColor.ToOpenTK();
            }

            MaterialChannel? metallicRoughnessChannel = gltfMaterial.FindChannel(KnownChannel.MetallicRoughness.ToString());
            if (metallicRoughnessChannel.HasValue)
            {
                materialParams.RoughnessFactor = GetMaterialChannelParam<float>(metallicRoughnessChannel.Value, KnownProperty.RoughnessFactor);
                materialParams.MetallicFactor = GetMaterialChannelParam<float>(metallicRoughnessChannel.Value, KnownProperty.MetallicFactor);
            }

            MaterialChannel? emissiveChannel = gltfMaterial.FindChannel(KnownChannel.Emissive.ToString());
            if (emissiveChannel.HasValue) // KHR_materials_emissive_strength
            {
                float emissiveStrength = GetMaterialChannelParam<float>(emissiveChannel.Value, KnownProperty.EmissiveStrength);

                materialParams.EmissiveFactor = emissiveChannel.Value.Color.ToOpenTK().Xyz * emissiveStrength;
            }

            MaterialChannel? transmissionChannel = gltfMaterial.FindChannel(KnownChannel.Transmission.ToString());
            if (transmissionChannel.HasValue) // KHR_materials_transmission
            {
                materialParams.TransmissionFactor = GetMaterialChannelParam<float>(transmissionChannel.Value, KnownProperty.TransmissionFactor);

                if (materialParams.TransmissionFactor > 0.001f)
                {
                    // This is here because I only want to set IOR for transmissive objects,
                    // because for opaque objects default value of 1.5 looks bad
                    materialParams.IOR = gltfMaterial.IndexOfRefraction; // KHR_materials_ior
                }
            }

            MaterialChannel? volumeAttenuationChannel = gltfMaterial.FindChannel(KnownChannel.VolumeAttenuation.ToString());
            if (volumeAttenuationChannel.HasValue) // KHR_materials_volume
            {
                System.Numerics.Vector3 numericsGltfAttenuationColor = GetMaterialChannelParam<System.Numerics.Vector3>(volumeAttenuationChannel.Value, KnownProperty.RGB);
                Vector3 gltfAttenuationColor = numericsGltfAttenuationColor.ToOpenTK();

                float gltfAttenuationDistance = GetMaterialChannelParam<float>(volumeAttenuationChannel.Value, KnownProperty.AttenuationDistance);

                // We can combine glTF Attenuation Color and Distance into a single Absorbance value
                // Source: https://github.com/DassaultSystemes-Technology/dspbr-pt/blob/e7cfa6e9aab2b99065a90694e1f58564d675c1a4/packages/lib/shader/integrator/pt.glsl#L24
                float x = -MathF.Log(gltfAttenuationColor.X) / gltfAttenuationDistance;
                float y = -MathF.Log(gltfAttenuationColor.Y) / gltfAttenuationDistance;
                float z = -MathF.Log(gltfAttenuationColor.Z) / gltfAttenuationDistance;
                Vector3 absorbance = new Vector3(x, y, z);
                materialParams.Absorbance = absorbance;
            }

            return materialParams;
        }
        
        private static T GetMaterialChannelParam<T>(MaterialChannel materialChannel, KnownProperty property)
        {
            foreach (IMaterialParameter param in materialChannel.Parameters)
            {
                if (param.Name == property.ToString())
                {
                    return (T)param.Value;
                }
            }

            throw new UnreachableException($"{nameof(property)} = {property} is not a part of the {nameof(materialChannel)}");
        }

        private static Ktx2.TranscodeFormat GLFormatToKtxFormat(GLTexture.InternalFormat internalFormat)
        {
            switch (internalFormat)
            {
                case GLTexture.InternalFormat.BC1RgbUnorm:
                    return Ktx2.TranscodeFormat.Bc1Rgb;

                case GLTexture.InternalFormat.BC4RUnorm:
                    return Ktx2.TranscodeFormat.Bc4R;

                case GLTexture.InternalFormat.BC5RgUnorm:
                    return Ktx2.TranscodeFormat.Bc5Rg;

                case GLTexture.InternalFormat.BC7RgbaUnorm:
                case GLTexture.InternalFormat.BC7RgbaSrgb:
                    return Ktx2.TranscodeFormat.Bc7Rgba;

                case GLTexture.InternalFormat.Astc4X4RgbaKHR:
                case GLTexture.InternalFormat.Astc4X4RgbaSrgbKHR:
                    return Ktx2.TranscodeFormat.Astc4X4Rgba;

                default:
                    throw new NotSupportedException($"Can not convert {nameof(internalFormat)} = {internalFormat} to {nameof(Ktx2.TranscodeFormat)}");
            }
        }
        
        private static int EncodingToSize(EncodingType encodingType)
        {
            int size = encodingType switch
            {
                EncodingType.UNSIGNED_BYTE or EncodingType.BYTE => 1,
                EncodingType.UNSIGNED_SHORT or EncodingType.SHORT => 2,
                EncodingType.FLOAT or EncodingType.UNSIGNED_INT => 4,
                _ => throw new NotSupportedException($"Can not convert {nameof(encodingType)} = {encodingType} to {nameof(size)}"),
            };
            return size;
        }
        
        private static int DimensionsToNum(DimensionType dimensionType)
        {
            int num = dimensionType switch
            {
                DimensionType.SCALAR => 1,
                DimensionType.VEC2 => 2,
                DimensionType.VEC3 => 3,
                DimensionType.VEC4 => 4,
                DimensionType.MAT4 => 16,
                _ => throw new NotSupportedException($"Can not convert {nameof(dimensionType)} = {dimensionType} to {nameof(num)}"),
            };
            return num;
        }
        
        private static unsafe void OptimizeMesh(ref GpuVertex[] meshVertices, ref Vector3[] meshVertexPositions, Span<uint> meshIndices, OptimizationSettings optimizationSettings)
        {
            if (optimizationSettings.VertexRemapOptimization)
            {
                uint[] remapTable = new uint[meshVertices.Length];
                int optimizedVertexCount = 0;
                fixed (void* meshVerticesPtr = meshVertices, meshPositionsPtr = meshVertexPositions)
                {
                    Span<Meshopt.Stream> vertexStreams = stackalloc Meshopt.Stream[2];
                    vertexStreams[0] = new Meshopt.Stream() { Data = meshVerticesPtr, Size = (nuint)sizeof(GpuVertex), Stride = (nuint)sizeof(GpuVertex) };
                    vertexStreams[1] = new Meshopt.Stream() { Data = meshPositionsPtr, Size = (nuint)sizeof(Vector3), Stride = (nuint)sizeof(Vector3) };

                    optimizedVertexCount = (int)Meshopt.GenerateVertexRemapMulti(ref remapTable[0], meshIndices[0], (nuint)meshIndices.Length, (nuint)meshVertices.Length, vertexStreams[0], (nuint)vertexStreams.Length);

                    Meshopt.RemapIndexBuffer(ref meshIndices[0], meshIndices[0], (nuint)meshIndices.Length, remapTable[0]);
                    Meshopt.RemapVertexBuffer(vertexStreams[0].Data, vertexStreams[0].Data, (nuint)meshVertices.Length, vertexStreams[0].Stride, remapTable[0]);
                    Meshopt.RemapVertexBuffer(vertexStreams[1].Data, vertexStreams[1].Data, (nuint)meshVertexPositions.Length, vertexStreams[1].Stride, remapTable[0]);
                }
                Array.Resize(ref meshVertices, optimizedVertexCount);
                Array.Resize(ref meshVertexPositions, optimizedVertexCount);
            }
            if (optimizationSettings.VertexCacheOptimization)
            {
                Meshopt.OptimizeVertexCache(ref meshIndices[0], meshIndices[0], (nuint)meshIndices.Length, (nuint)meshVertices.Length);
            }
            if (optimizationSettings.VertexFetchOptimization)
            {
                uint[] remapTable = new uint[meshVertices.Length];
                fixed (void* meshVerticesPtr = meshVertices, meshPositionsPtr = meshVertexPositions)
                {
                    Meshopt.OptimizeVertexFetchRemap(ref remapTable[0], meshIndices[0], (nuint)meshIndices.Length, (nuint)meshVertices.Length);

                    Meshopt.RemapIndexBuffer(ref meshIndices[0], meshIndices[0], (nuint)meshIndices.Length, remapTable[0]);
                    Meshopt.RemapVertexBuffer(meshVerticesPtr, meshVerticesPtr, (nuint)meshVertices.Length, (nuint)sizeof(GpuVertex), remapTable[0]);
                    Meshopt.RemapVertexBuffer(meshPositionsPtr, meshPositionsPtr, (nuint)meshVertexPositions.Length, (nuint)sizeof(Vector3), remapTable[0]);
                }
            }
        }

        private static unsafe MeshletData GenerateMeshlets(ReadOnlySpan<Vector3> meshVertexPositions, ReadOnlySpan<uint> meshIndices)
        {
            const float CONE_WEIGHT = 0.0f;

            /// Keep in sync between shader and client code!
            // perfectly fits 4 32-sized subgroups
            const uint MESHLET_MAX_VERTEX_COUNT = 128;

            // (252 * 3) + 4(hardware reserved) = 760bytes. Which almost perfectly fits NVIDIA-Turing 128 byte allocation granularity.
            // Meshoptimizer also requires this to be divisible by 4
            const uint MESHLET_MAX_TRIANGLE_COUNT = 252;

            nuint maxMeshlets = Meshopt.BuildMeshletsBound((nuint)meshIndices.Length, MESHLET_MAX_VERTEX_COUNT, MESHLET_MAX_TRIANGLE_COUNT);

            Meshopt.Meshlet[] meshlets = new Meshopt.Meshlet[maxMeshlets];
            uint[] meshletsVertexIndices = new uint[maxMeshlets * MESHLET_MAX_VERTEX_COUNT];
            byte[] meshletsPrimitiveIndices = new byte[maxMeshlets * MESHLET_MAX_TRIANGLE_COUNT * 3];
            nuint meshletCount = Meshopt.BuildMeshlets(
                ref meshlets[0],
                meshletsVertexIndices[0],
                meshletsPrimitiveIndices[0],
                meshIndices[0],
                (nuint)meshIndices.Length,
                meshVertexPositions[0].X,
                (nuint)meshVertexPositions.Length,
                (nuint)sizeof(Vector3),
                MESHLET_MAX_VERTEX_COUNT,
                MESHLET_MAX_TRIANGLE_COUNT,
                CONE_WEIGHT
            );

            for (int i = 0; i < meshlets.Length; i++)
            {
                ref readonly Meshopt.Meshlet meshlet = ref meshlets[i];

                // https://zeux.io/2024/04/09/meshlet-triangle-locality/
                Meshopt.OptimizeMeshlet(
                    ref meshletsVertexIndices[meshlet.VertexOffset],
                    ref meshletsPrimitiveIndices[meshlet.TriangleOffset],
                    meshlet.TriangleCount,
                    meshlet.VertexCount
                );
            }

            ref readonly Meshopt.Meshlet last = ref meshlets[meshletCount - 1];
            uint meshletsVertexIndicesLength = last.VertexOffset + last.VertexCount;
            uint meshletsLocalIndicesLength = last.TriangleOffset + ((last.TriangleCount * 3u + 3u) & ~3u);

            MeshletData result;
            result.Meshlets = meshlets;
            result.MeshletsLength = (int)meshletCount;

            result.VertexIndices = meshletsVertexIndices;
            result.VertexIndicesLength = (int)meshletsVertexIndicesLength;

            result.LocalIndices = meshletsPrimitiveIndices;
            result.LocalIndicesLength = (int)meshletsLocalIndicesLength;

            return result;
        }

        private delegate void FuncAccessorItem<T>(in T item, int index);
        private static unsafe void IterateAccessor<T>(Accessor accessor, FuncAccessorItem<T> funcItem) where T : unmanaged
        {
            if (accessor.IsSparse)
            {
                throw new ArgumentException("Sparse accessor is not supported");
            }

            int itemSize = EncodingToSize(accessor.Encoding) * DimensionsToNum(accessor.Dimensions);
            int stride = accessor.SourceBufferView.ByteStride == 0 ? itemSize : accessor.SourceBufferView.ByteStride;

            if (sizeof(T) < itemSize)
            {
                throw new ArgumentException($"{nameof(T)} is smaller than a single item in the accessor ({nameof(itemSize)} = {itemSize})");
            }

            Span<byte> data = accessor.SourceBufferView.Content.AsSpan(accessor.ByteOffset, accessor.ByteLength);
            fixed (byte* ptr = data)
            {
                for (int i = 0; i < accessor.Count; i++)
                {
                    T t;
                    byte* head = ptr + i * stride;
                    Memory.Copy(head, &t, itemSize);

                    funcItem(t, i);
                }
            }
        }

        public static class GtlfpackWrapper
        {
            public const string CLI_NAME = "gltfpack"; // https://github.com/BoyBaykiller/meshoptimizer

            private static bool? _isCliFoundCached;
            public static bool IsCLIFoundCached
            {
                get
                {
                    if (!_isCliFoundCached.HasValue)
                    {
                        _isCliFoundCached = FindGltfpack();
                    }
                   return _isCliFoundCached.Value;
                }
            }

            public record struct GltfpackSettings
            {
                public string InputPath;
                public string OutputPath;
                public int ThreadsUsed;
                public bool UseInstancing;

                // Added in gltfpack fork
                public bool KeepMeshPrimitives;

                public Action<string>? ProcessError;
                public Action<string>? ProcessOutput;
            }

            public static Task? Run(GltfpackSettings settings)
            {
                if (!IsCLIFoundCached)
                {
                    Logger.Log(Logger.LogLevel.Error, $"Can't run {CLI_NAME}. Tool is not found");
                    return null;
                }

                // -v         = verbose output
                // -noq       = no mesh quantization (KHR_mesh_quantization)
                // -ac        = keep constant animation tracks even if they don't modify the node transform
                // -tc        = do KTX2 texture compression (KHR_texture_basisu)
                // -tq        = texture quality
                // -mi        = use instancing (EXT_mesh_gpu_instancing)
                // -kp        = disable mesh primitive merging (added in gltfpack fork)
                // -tj        = number of threads to use when compressing textures
                string arguments = $"-v -noq -ac -tc -tq 10 " +
                                   $"{MaybeArgument("-mi", settings.UseInstancing)} " +
                                   $"{MaybeArgument("-kp", settings.KeepMeshPrimitives)} " +
                                   $"-tj {settings.ThreadsUsed} " +
                                   $"-i {settings.InputPath} -o {settings.OutputPath}";

                ProcessStartInfo startInfo = new ProcessStartInfo()
                {
                    FileName = CLI_NAME,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Arguments = arguments,
                };

                try
                {
                    Logger.Log(Logger.LogLevel.Info, $"Running \"{CLI_NAME} {arguments}\"");

                    Process? proc = Process.Start(startInfo);

                    proc.BeginErrorReadLine();
                    proc.BeginOutputReadLine();
                    proc.ErrorDataReceived += (object sender, DataReceivedEventArgs e) =>
                    {
                        if (e.Data == null)
                        {
                            return;
                        }

                        settings.ProcessError?.Invoke($"{CLI_NAME}: {e.Data}");
                    };
                    proc.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
                    {
                        if (e.Data == null)
                        {
                            return;
                        }

                        settings.ProcessOutput?.Invoke($"{CLI_NAME}: {e.Data}");
                    };

                    return proc.WaitForExitAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log(Logger.LogLevel.Error, $"Failed to create process. {ex}");
                    return null;
                }

                static string MaybeArgument(string argument, bool yes)
                {
                    if (yes)
                    {
                        return argument;
                    }

                    return string.Empty;
                }
            }

            public static bool IsCompressed(ModelRoot gltf)
            {
                if (gltf.LogicalTextures.Count == 0)
                {
                    return true;
                }

                foreach (string ext in gltf.ExtensionsUsed)
                {
                    // The definition of wether a glTF is compressed may be expanded in the future
                    if (ext == "KHR_texture_basisu")
                    {
                        return true;
                    }
                }
                return false;
            }

            private static bool FindGltfpack()
            {
                List<string> pathsToSearch = [Directory.GetCurrentDirectory()];
                {
                    if (TryGetEnvironmentVariable("PATH", out string[] envPath))
                    {
                        pathsToSearch.AddRange(envPath);
                    }
                    if (TryGetEnvironmentVariable("Path", out envPath))
                    {
                        pathsToSearch.AddRange(envPath);
                    }
                }

                for (int i = 0; i < pathsToSearch.Count; i++)
                {
                    string envPath = pathsToSearch[i];
                    if (!Directory.Exists(envPath))
                    {
                        continue;
                    }

                    string[] results = Directory.GetFiles(envPath, $"{CLI_NAME}.*");
                    if (results.Length > 0)
                    {
                        return true;
                    }
                }

                static bool TryGetEnvironmentVariable(string envVar, out string[] strings)
                {
                    string? data = Environment.GetEnvironmentVariable(envVar);
                    strings = data?.Split(';');

                    return data != null;
                }

                return false;
            }
        }

        private static class FallbackTextures
        {
            private static GLTexture pureWhiteTexture;
            private static GLTexture.BindlessHandle pureWhiteTextureHandle;

            private static GLTexture purpleBlackTexture;
            private static GLTexture.BindlessHandle purpleBlackTextureHandle;

            public static GLTexture.BindlessHandle White()
            {
                if (pureWhiteTexture == null)
                {
                    pureWhiteTexture = new GLTexture(GLTexture.Type.Texture2D);
                    pureWhiteTexture.ImmutableAllocate(1, 1, 1, GLTexture.InternalFormat.R16G16B16A16Float);
                    pureWhiteTexture.Clear(GLTexture.PixelFormat.RGBA, GLTexture.PixelType.Float, new Vector4(1.0f));
                    pureWhiteTextureHandle = pureWhiteTexture.GetTextureHandleARB(new GLSampler(new GLSampler.SamplerState()));
                }
                return pureWhiteTextureHandle;
            }

            public static GLTexture.BindlessHandle PurpleBlack()
            {
                if (purpleBlackTexture == null)
                {
                    purpleBlackTexture = new GLTexture(GLTexture.Type.Texture2D);
                    purpleBlackTexture.ImmutableAllocate(2, 2, 1, GLTexture.InternalFormat.R16G16B16A16Float);
                    purpleBlackTexture.Upload2D(2, 2, GLTexture.PixelFormat.RGBA, GLTexture.PixelType.Float, new Vector4[]
                    {
                        // Source: https://en.wikipedia.org/wiki/File:Minecraft_missing_texture_block.svg
                        new Vector4(251.0f / 255.0f, 62.0f / 255.0f, 249.0f / 255.0f, 1.0f), // Purple
                        new Vector4(0.0f, 0.0f, 0.0f, 1.0f), // Black
                        new Vector4(0.0f, 0.0f, 0.0f, 1.0f), // Black
                        new Vector4(251.0f / 255.0f, 62.0f / 255.0f, 249.0f / 255.0f, 1.0f), // Purple
                    }[0]);

                    purpleBlackTextureHandle = purpleBlackTexture.GetTextureHandleARB(new GLSampler(new GLSampler.SamplerState()
                    {
                        WrapModeS = GLSampler.WrapMode.Repeat,
                        WrapModeT = GLSampler.WrapMode.Repeat
                    }));
                }

                return purpleBlackTextureHandle;
            }
        }
    }
}
