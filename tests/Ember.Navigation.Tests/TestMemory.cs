using System;
using System.Runtime.InteropServices;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 测试用非托管内存分配（Marshal.AllocHGlobal），纯 .NET CLI 可执行，
    /// 替代运行时才可分配的 NativeArray。用完必须 Dispose。
    /// </summary>
    internal unsafe sealed class TestMemory : IDisposable
    {
        private IntPtr m_Block;
        public void* Ptr => m_Block.ToPointer();
        public long Bytes { get; }

        private TestMemory(long bytes)
        {
            Bytes = bytes;
            m_Block = Marshal.AllocHGlobal((IntPtr)bytes);
        }

        public static TestMemory Alloc(long bytes) => new(bytes);

        public T* As<T>() where T : unmanaged => (T*)Ptr;

        public void Dispose()
        {
            if (m_Block != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(m_Block);
                m_Block = IntPtr.Zero;
            }
        }
    }
}
