﻿using System;
using OpenTK;
using OpenTK.Input;
using OpenTK.Graphics.OpenGL4;
using ImGuiNET;
using IDKEngine.GUI;

namespace IDKEngine.Render
{
    static class Gui
    {
        public static ImGuiController ImGuiController = new ImGuiController(832, 832);

        private static int selectedMeshIndex = -1;
        public static void Render(Window window, float frameTime)
        {
            ImGuiController.Update(window, frameTime);
            ImGui.Begin("Graphics");
            {
                ImGui.Text($"FPS: {window.FPS}");

                string[] renderModes = new string[] { "Rasterizer", "PathTracer" };
                string current = window.IsPathTracing ? renderModes[1] : renderModes[0];
                if (ImGui.BeginCombo("Render Path", current))
                {
                    for (int i = 0; i < renderModes.Length; i++)
                    {
                        bool isSelected = current == renderModes[i];
                        if (ImGui.Selectable(renderModes[i], isSelected))
                        {
                            current = renderModes[i];
                            window.IsPathTracing = current == renderModes[1];
                        }

                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                if (!window.IsPathTracing)
                {
                    ImGui.Checkbox("DrawAABB", ref window.IsDrawAABB);

                    if (ImGui.CollapsingHeader("VolumetricLighting"))
                    {
                        ImGui.Checkbox("IsVolumetricLighting", ref window.IsVolumetricLighting);
                        if (window.IsVolumetricLighting)
                        {
                            int tempInt = window.VolumetricLight.Samples;
                            if (ImGui.SliderInt("Samples", ref tempInt, 1, 100))
                            {
                                window.VolumetricLight.Samples = tempInt;
                            }


                            float tempFloat = window.VolumetricLight.Scattering;
                            if (ImGui.SliderFloat("Scattering", ref tempFloat, 0.0f, 1.0f))
                            {
                                window.VolumetricLight.Scattering = tempFloat;
                            }

                            System.Numerics.Vector3 tempVec = OpenTKToSystem(window.VolumetricLight.Absorbance);
                            if (ImGui.SliderFloat3("Absorbance", ref tempVec, 0.0f, 0.2f))
                            {
                                window.VolumetricLight.Absorbance = SystemToOpenTK(tempVec);
                            }
                        }
                    }

                    if (ImGui.CollapsingHeader("SSAO"))
                    {
                        ImGui.Checkbox("IsSSAO", ref window.IsSSAO);
                        if (window.IsSSAO)
                        {
                            int tempInt = window.SSAO.Samples;
                            if (ImGui.SliderInt("Samples  ", ref tempInt, 1, 50))
                            {
                                window.SSAO.Samples = tempInt;
                            }

                            float tempFloat = window.SSAO.Radius;
                            if (ImGui.SliderFloat("Radius", ref tempFloat, 0.0f, 2.0f))
                            {
                                window.SSAO.Radius = tempFloat;
                            }
                        }
                    }

                    if (ImGui.CollapsingHeader("SSR"))
                    {
                        ImGui.Checkbox("IsSSR", ref window.IsSSR);
                        if (window.IsSSR)
                        {
                            int tempInt = window.SSR.Samples;
                            if (ImGui.SliderInt("Samples ", ref tempInt, 1, 100))
                            {
                                window.SSR.Samples = tempInt;
                            }

                            tempInt = window.SSR.BinarySearchSamples;
                            if (ImGui.SliderInt("BinarySearchSamples", ref tempInt, 0, 40))
                            {
                                window.SSR.BinarySearchSamples = tempInt;
                            }

                            float tempFloat = window.SSR.MaxDist;
                            if (ImGui.SliderFloat("MaxDist", ref tempFloat, 1, 100))
                            {
                                window.SSR.MaxDist = tempFloat;
                            }
                        }
                    }
                }
                else
                {
                    if (ImGui.CollapsingHeader("PathTracing"))
                    {
                        int tempInt = window.PathTracer.RayDepth;
                        if (ImGui.SliderInt("MaxRayDepth", ref tempInt, 1, 50))
                        {
                            window.PathTracer.ResetRenderer();
                            window.PathTracer.RayDepth = tempInt;
                        }

                        float floatTemp = window.PathTracer.FocalLength;
                        if (ImGui.InputFloat("FocalLength", ref floatTemp, 0.1f))
                        {
                            window.PathTracer.ResetRenderer();
                            window.PathTracer.FocalLength = MathF.Max(floatTemp, 0);
                        }

                        floatTemp = window.PathTracer.ApertureDiameter;
                        if (ImGui.InputFloat("ApertureDiameter", ref floatTemp, 0.002f))
                        {
                            window.PathTracer.ResetRenderer();
                            window.PathTracer.ApertureDiameter = MathF.Max(floatTemp, 0);
                        }
                    }
                }

                if (ImGui.CollapsingHeader("EnvironmentMap"))
                {
                    string[] resolutions = new string[] { "2048", "1024", "512", "256", "128", "64", "32" };
                    current = window.AtmosphericScatterer.Result.Width.ToString();
                    if (ImGui.BeginCombo("Resolution", current))
                    {
                        for (int i = 0; i < resolutions.Length; i++)
                        {
                            bool isSelected = current == resolutions[i];
                            if (ImGui.Selectable(resolutions[i], isSelected))
                            {
                                current = resolutions[i];
                                window.AtmosphericScatterer.SetSize(Convert.ToInt32(current));
                                window.AtmosphericScatterer.Compute();
                                window.PathTracer.ResetRenderer();
                            }

                            if (isSelected)
                                ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }

                    int tempInt = window.AtmosphericScatterer.ISteps;
                    if (ImGui.SliderInt("InScatteringSamples", ref tempInt, 1, 100))
                    {
                        window.AtmosphericScatterer.ISteps = tempInt;
                        window.AtmosphericScatterer.Compute();
                        window.PathTracer.ResetRenderer();
                    }

                    tempInt = window.AtmosphericScatterer.JSteps;
                    if (ImGui.SliderInt("DensitySamples", ref tempInt, 1, 40))
                    {
                        window.AtmosphericScatterer.JSteps = tempInt;
                        window.AtmosphericScatterer.Compute();
                        window.PathTracer.ResetRenderer();
                    }

                    float tempFloat = window.AtmosphericScatterer.Time;
                    if (ImGui.DragFloat("Time", ref tempFloat, 0.005f))
                    {
                        window.AtmosphericScatterer.Time = tempFloat;
                        window.AtmosphericScatterer.Compute();
                        window.PathTracer.ResetRenderer();
                    }

                    tempFloat = window.AtmosphericScatterer.LightIntensity;
                    if (ImGui.DragFloat("Intensity", ref tempFloat, 0.2f))
                    {
                        window.AtmosphericScatterer.LightIntensity = tempFloat;
                        window.AtmosphericScatterer.Compute();
                        window.PathTracer.ResetRenderer();
                    }
                }

                ImGui.End();
            }

            if (selectedMeshIndex != Forward.MESH_INDEX_CLEAR_COLOR)
            {
                System.Numerics.Vector3 systemVec3;
                ImGui.Begin("GameObjectProperties", ImGuiWindowFlags.AlwaysAutoResize);
                {
                    bool hadChange = false;
                    GLSLMesh mesh = window.ModelSystem.Meshes[selectedMeshIndex];
                    GLSLDrawCommand drawCommand = window.ModelSystem.DrawCommands[selectedMeshIndex];

                    systemVec3 = OpenTKToSystem(mesh.Model.ExtractTranslation());
                    if (ImGui.DragFloat3("Position", ref systemVec3, 0.1f))
                    {
                        hadChange = true;
                        mesh.Model = mesh.Model.ClearTranslation() * Matrix4.CreateTranslation(SystemToOpenTK(systemVec3));
                    }

                    if (hadChange)
                    {
                        window.ModelSystem.ForEach(selectedMeshIndex, selectedMeshIndex + 1, (ref GLSLMesh curMesh) =>
                        {
                            curMesh = mesh;
                        });
                        window.PathTracer.ResetRenderer();
                    }

                    ImGui.End();
                }
            }

            ImGuiController.Render();
        }

        public static void Update(Window window)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            if (MouseManager.IsButtonTouched(MouseButton.Left) && !io.WantCaptureKeyboard && !io.WantCaptureMouse)
            {
                System.Drawing.Point point = window.PointToClient(new System.Drawing.Point(MouseManager.WindowPositionX, MouseManager.WindowPositionY));
                point.Y = window.Height - point.Y;
                
                window.ForwardRenderer.Framebuffer.GetPixels(point.X, point.Y, 1, 1, PixelFormat.RedInteger, PixelType.Int, ref selectedMeshIndex);
            }   
        }

        public static System.Numerics.Vector2 OpenTKToSystem(Vector2 vector2)
        {
            return new System.Numerics.Vector2(vector2.X, vector2.Y);
        }

        public static Vector2 SystemToOpenTK(System.Numerics.Vector2 vector2)
        {
            return new Vector2(vector2.X, vector2.Y);
        }

        public static System.Numerics.Vector3 OpenTKToSystem(Vector3 vector3)
        {
            return new System.Numerics.Vector3(vector3.X, vector3.Y, vector3.Z);
        }

        public static Vector3 SystemToOpenTK(System.Numerics.Vector3 vector3)
        {
            return new Vector3(vector3.X, vector3.Y, vector3.Z);
        }
    }
}
