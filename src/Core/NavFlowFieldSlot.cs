using Ember;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 流场缓存槽位（blittable，存于 NavWorld 的槽位数组 buffer）。
    ///
    /// 场数据本身按槽位独立分配：槽位<b>按需</b>建缓冲，不预先按
    /// <c>FlowFieldCacheSize × 体素数</c> 满额分配 —— 那个乘积在大网格上
    /// 是几十 MB 级，而实际同时活跃的目标通常远少于缓存上限。
    /// </summary>
    public struct NavFlowFieldSlot
    {
        /// <summary>目标体素（槽位键）。</summary>
        public int3 Target;

        /// <summary>播种时的导航数据代际；数据热替换后旧场作废。</summary>
        public int Generation;

        /// <summary>最后一次被请求命中的帧号（LRU 依据）。</summary>
        public int LastUsedFrame;

        /// <summary>1 = 波前已耗尽，梯度可用。</summary>
        public int Complete;

        /// <summary>1 = 槽位已占用。</summary>
        public int InUse;

        /// <summary>分配时的体素数；与当前体素数不符则需重建。</summary>
        public int VoxelCount;

        /// <summary>累计代价数组（体素数）。</summary>
        public BufferHandle Distances;

        /// <summary>波前堆代价侧。</summary>
        public BufferHandle HeapCosts;

        /// <summary>波前堆体素侧。</summary>
        public BufferHandle HeapVoxels;

        /// <summary>当前堆内元素数。</summary>
        public int HeapCount;
    }
}
