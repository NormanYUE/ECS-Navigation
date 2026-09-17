using Ember.Collision;
using UnityEngine;

namespace Ember.Navigation
{
    /// <summary>
    /// 烘焙产物资产：编辑器烘焙窗口写出的 blob，运行时由
    /// <see cref="NavBakedAssetLoader"/> 读进 <see cref="NavWorld"/>。
    ///
    /// 存 <c>byte[]</c> 而非 <c>TextAsset</c>：blob 是二进制的固定布局，
    /// 走 Unity 的文本资产管线会被编码假设污染；字节数组原样存盘、原样读出。
    /// </summary>
    [CreateAssetMenu(menuName = "Ember/Navigation Baked Data", fileName = "NavBakedData")]
    public sealed class NavBakedAsset : ScriptableObject
    {
        /// <summary>blob 字节。</summary>
        [SerializeField] private byte[] m_Blob = System.Array.Empty<byte>();

        /// <summary>烘焙维度（用于加载前的格式校验）。</summary>
        [SerializeField] private CollisionDimension m_Dimension = CollisionDimension.XY;

        /// <summary>体素边长（米）。</summary>
        [SerializeField] private float m_VoxelSize = 0.5f;

        /// <summary>blob 字节数。</summary>
        public int BlobLength => m_Blob?.Length ?? 0;

        /// <summary>烘焙维度。</summary>
        public CollisionDimension Dimension => m_Dimension;

        /// <summary>体素边长。</summary>
        public float VoxelSize => m_VoxelSize;

        /// <summary>写入烘焙结果（编辑器烘焙窗口调用）。</summary>
        public void SetData(byte[] blob, CollisionDimension dimension, float voxelSize)
        {
            m_Blob = blob ?? System.Array.Empty<byte>();
            m_Dimension = dimension;
            m_VoxelSize = voxelSize;
        }

        /// <summary>
        /// 校验并加载进导航世界。
        /// 加载必须在 <c>fixed</c> 块内完成 —— blob 是托管数组，出了块就不再锁定，
        /// 把指针交给外部使用会在 GC 搬移后失效。
        /// </summary>
        public unsafe bool LoadInto(World world)
        {
            if (m_Blob == null || m_Blob.Length == 0) return false;

            fixed (byte* blob = m_Blob)
            {
                if (NavBlobReader.Validate(blob, m_Dimension) != NavBlobReader.Status.Ok)
                    return false;

                world.GetNavWorld().LoadBlob(blob);
                return true;
            }
        }
    }
}
