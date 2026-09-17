using Unity.Collections;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 寻路求解器的临时工作区（分层 A* + 拉绳）。
    ///
    /// 这些缓冲是<b>求解过程中的暂存</b>，不跨系统、不跨帧保留语义，
    /// 因此随系统生命周期分配（<c>Allocator.Persistent</c>），
    /// 只在导航数据代际或体素数变化时重建 —— 不占 NavWorld 的段句柄表。
    ///
    /// 堆容量取 <c>体素数 × 8</c>：惰性删除的二叉堆会重复入堆，
    /// 容量不足表现为寻路静默失败。这里按已验证的配置留足余量，
    /// 换成索引堆后可以显著下调（P9）。
    /// </summary>
    internal sealed class NavPathWorkspace : System.IDisposable
    {
        private const int HeapFactor = 8;
        private const int MinHeapCapacity = 1024;

        /// <summary>分层 A* 的簇间搜索缓冲。</summary>
        public NativeArray<float> ClusterG;
        public NativeArray<int> ClusterParent;
        public NativeArray<float> ClusterHeapF;
        public NativeArray<int> ClusterHeapVoxels;
        public NativeArray<int> ClusterPath;

        /// <summary>簇内体素 A* 缓冲。</summary>
        public NativeArray<float> AStarG;
        public NativeArray<int> AStarParent;
        public NativeArray<float> AStarHeapF;
        public NativeArray<int> AStarHeapVoxels;

        /// <summary>航点缓冲：原始 → 拉绳输入 → 拉绳输出。</summary>
        public NativeArray<int3> SegmentWaypoints;
        public NativeArray<int3> RawWaypoints;
        public NativeArray<int3> SmoothWaypoints;

        /// <summary>已分配的体素数（0 表示未分配）。</summary>
        public long VoxelCount { get; private set; }

        /// <summary>已分配的簇节点数。</summary>
        public int ClusterNodeCount { get; private set; }

        /// <summary>容量是否已匹配给定的导航数据规模。</summary>
        public bool Matches(long voxelCount, int clusterNodeCount) =>
            VoxelCount == voxelCount && ClusterNodeCount == clusterNodeCount;

        /// <summary>按导航数据规模重建（先释放旧缓冲）。</summary>
        public void Rebuild(long voxelCount, int clusterNodeCount)
        {
            Dispose();

            VoxelCount = voxelCount;
            ClusterNodeCount = clusterNodeCount;

            int voxels = (int)math.max(voxelCount, 1);
            int nodes = math.max(clusterNodeCount, 1);
            int astarHeap = math.max(voxels * HeapFactor, MinHeapCapacity);
            int clusterHeap = math.max(nodes * HeapFactor, MinHeapCapacity);

            ClusterG = new NativeArray<float>(nodes, Allocator.Persistent);
            ClusterParent = new NativeArray<int>(nodes, Allocator.Persistent);
            ClusterHeapF = new NativeArray<float>(clusterHeap, Allocator.Persistent);
            ClusterHeapVoxels = new NativeArray<int>(clusterHeap, Allocator.Persistent);
            ClusterPath = new NativeArray<int>(nodes, Allocator.Persistent);

            AStarG = new NativeArray<float>(voxels, Allocator.Persistent);
            AStarParent = new NativeArray<int>(voxels, Allocator.Persistent);
            AStarHeapF = new NativeArray<float>(astarHeap, Allocator.Persistent);
            AStarHeapVoxels = new NativeArray<int>(astarHeap, Allocator.Persistent);

            SegmentWaypoints = new NativeArray<int3>(voxels, Allocator.Persistent);
            RawWaypoints = new NativeArray<int3>(voxels, Allocator.Persistent);
            SmoothWaypoints = new NativeArray<int3>(voxels, Allocator.Persistent);
        }

        public void Dispose()
        {
            Dispose(ref ClusterG);
            Dispose(ref ClusterParent);
            Dispose(ref ClusterHeapF);
            Dispose(ref ClusterHeapVoxels);
            Dispose(ref ClusterPath);
            Dispose(ref AStarG);
            Dispose(ref AStarParent);
            Dispose(ref AStarHeapF);
            Dispose(ref AStarHeapVoxels);
            Dispose(ref SegmentWaypoints);
            Dispose(ref RawWaypoints);
            Dispose(ref SmoothWaypoints);

            VoxelCount = 0;
            ClusterNodeCount = 0;
        }

        private static void Dispose<T>(ref NativeArray<T> array) where T : unmanaged
        {
            if (array.IsCreated) array.Dispose();
            array = default;
        }
    }
}
