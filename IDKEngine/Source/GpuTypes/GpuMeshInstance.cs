﻿using System.Runtime.Intrinsics;
using OpenTK.Mathematics;

namespace IDKEngine.GpuTypes
{
    public record struct GpuMeshInstance
    {
        // In a typical 4x4 matrix the W component is useless:
        // (x, y, z, 0.0)
        // (x, y, z, 0.0)
        // (x, y, z, 0.0)
        // (x, y, z, 1.0)
        // We can't store Matrix4x3 as GLSL-std430 requires vec4 alignment (vec3s would add padding). For that reason we store a Matrix3x4.
        // We get this Matrix3x4 by taking the Matrix4x4, constructing a Matrix4x3 by removing the useless W, and then transposing. In GLSL we then specify row_major.

        public Matrix4 ModelMatrix
        {
            get => Mat3x4ToMat4x4Tranposed(ModelMatrix3x4);

            set
            {
                ModelMatrix3x4 = Mat4x4ToMat3x4Transposed(value);
            }
        }
        
        public Matrix4 InvModelMatrix => Mat3x4ToMat4x4Tranposed(invModelMatrix3x4);
        
        public Matrix4 PrevModelMatrix => Mat3x4ToMat4x4Tranposed(prevModelMatrix3x4);

        public Matrix3x4 ModelMatrix3x4
        {
            get => modelMatrix3x4;

            private set
            {
                modelMatrix3x4 = value;
                invModelMatrix3x4 = Matrix3x4.Invert(ModelMatrix3x4);
            }
        }

        private Matrix3x4 modelMatrix3x4;
        private Matrix3x4 invModelMatrix3x4;
        private Matrix3x4 prevModelMatrix3x4;

        public int MeshIndex;
        private readonly float _pad0;
        private readonly float _pad1;
        private readonly float _pad2;

        public bool DidMove()
        {
            return !FastMatrix3x4Equal(prevModelMatrix3x4, ModelMatrix3x4);
        }

        public void SetPrevToCurrentMatrix()
        {
            prevModelMatrix3x4 = ModelMatrix3x4;
        }

        public static Matrix3x4 Mat4x4ToMat3x4Transposed(in Matrix4 model)
        {
            Matrix4x3 fourByThree = new Matrix4x3(
                model.Row0.Xyz,
                model.Row1.Xyz,
                model.Row2.Xyz,
                model.Row3.Xyz
            );

            Matrix3x4 result = Matrix4x3.Transpose(fourByThree);

            return result;
        }

        public static Matrix4 Mat3x4ToMat4x4Tranposed(in Matrix3x4 model)
        {
            Matrix4x3 tranposed = Matrix3x4.Transpose(model);

            Matrix4 result = new Matrix4(
                new Vector4(tranposed.Row0, 0.0f),
                new Vector4(tranposed.Row1, 0.0f),
                new Vector4(tranposed.Row2, 0.0f),
                new Vector4(tranposed.Row3, 1.0f)
            );

            return result;
        }

        private static bool FastMatrix3x4Equal(in Matrix3x4 lhs, in Matrix3x4 rhs)
        {
            // Soon gets implemented in OpenTK https://github.com/opentk/opentk/pull/1721
            Vector256<float> aLo = Vector256.LoadUnsafe(in lhs.Row0.X);
            Vector256<float> bLo = Vector256.LoadUnsafe(in rhs.Row0.X);

            Vector128<float> aHi = Vector128.LoadUnsafe(in lhs.Row2.X);
            Vector128<float> bHi = Vector128.LoadUnsafe(in rhs.Row2.X);

            return aLo == bLo && aHi == bHi;
        }
    }
}
