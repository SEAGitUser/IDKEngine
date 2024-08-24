﻿using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IDKEngine.Utils
{
    public static unsafe class Memory
    {
        public static T* Malloc<T>(nint count = 1) where T : unmanaged
        {
            return (T*)NativeMemory.Alloc((nuint)(sizeof(T) * count));
        }

        public static T* Malloc<T>(int count = 1) where T : unmanaged
        {
            return (T*)NativeMemory.Alloc((nuint)(sizeof(T) * count));
        }

        public static void Free(void* ptr)
        {
            NativeMemory.Free(ptr);
        }

        public static void Fill(void* ptr, byte value, nint byteCount)
        {
            NativeMemory.Fill(ptr, (nuint)byteCount, value);
        }

        public static void CopyElements<T>(in T src, ref T dest, nint numElements) where T : unmanaged
        {
            fixed (T* srcPtr = &src, destPtr = &dest)
            {
                CopyElements(srcPtr, destPtr, numElements);
            }
        }

        public static void CopyElements<T>(T* src, T* dest, nint numElements) where T : unmanaged
        {
            Copy(src, dest, sizeof(T) * numElements);
        }

        public static void Copy(void* src, void* dest, nint byteCount)
        {
            NativeMemory.Copy(src, dest, (nuint)byteCount);
        }
    }
}
