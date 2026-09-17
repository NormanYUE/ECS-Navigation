using Unity.Collections;

namespace Ember.Navigation
{
    /// <summary>
    /// 运行时烘焙的中间缓冲与 blob 目标缓冲。
    ///
    /// 烘焙是重分配密集的操作（距离场按 float/体素），且每次调用的尺寸由
    /// <see cref="NavBaker.Plan"/> 决定。这里按计划结果一次性备齐并跨次复用，
    /// 容量足够就不重新分配 —— 反复烘焙同一张地图时稳态零分配。
    /// </summary>
    public sealed class NavRuntimeBakeWorkspace : System.IDisposable
    {
        /// <summary>距离场（float / 体素，烘焙中间量，非量化后的 blob 段）。</summary>
        public NativeArray<float> Distance;

        /// <summary>占据位集。</summary>
        public NativeArray<byte> Occupancy;

        /// <summary>代价数组。</summary>
        public NativeArray<float> Costs;

        /// <summary>union-find 父指针。</summary>
        public NativeArray<int> Parent;

        /// <summary>区域 id。</summary>
        public NativeArray<int> Region;

        /// <summary>tile 前缀偏移表（簇标记工作区）。</summary>
        public NativeArray<int> NodeLookup;

        /// <summary>每体素簇 id。</summary>
        public NativeArray<int> VoxelNodes;

        /// <summary>簇图构建的计数工作区。</summary>
        public NativeArray<int> ClusterScratch;

        /// <summary>簇节点。</summary>
        public NativeArray<NavClusterNode> Nodes;

        /// <summary>簇门户（节点分组表）。</summary>
        public NativeArray<NavPortal> Portals;

        /// <summary>簇边。</summary>
        public NativeArray<NavClusterEdge> Edges;

        /// <summary>边键（去重排序用）。</summary>
        public NativeArray<long> EdgeKeys;

        /// <summary>边计数。</summary>
        public NativeArray<int> EdgeCounts;

        /// <summary>边门户（紧凑表）。</summary>
        public NativeArray<NavPortal> EdgePortals;

        /// <summary>blob 目标缓冲（按计划上界分配）。</summary>
        public NativeArray<byte> Blob;

        /// <summary>已适配的计划上界；变化时重新分配。</summary>
        public long PlannedBytes { get; private set; }

        /// <summary>按计划结果备齐缓冲；上界未变则复用。</summary>
        public void Ensure(in NavBaker.PlanResult plan)
        {
            if (PlannedBytes == plan.BlobBytes && Distance.IsCreated) return;

            Dispose();
            PlannedBytes = plan.BlobBytes;

            Distance = new NativeArray<float>((int)(plan.DistanceBytes / sizeof(float)), Allocator.Persistent);
            Occupancy = new NativeArray<byte>((int)plan.OccupancyBytes, Allocator.Persistent);
            Costs = new NativeArray<float>((int)(plan.CostBytes / sizeof(float)), Allocator.Persistent);
            Parent = new NativeArray<int>((int)(plan.ParentBytes / sizeof(int)), Allocator.Persistent);
            Region = new NativeArray<int>((int)(plan.RegionBytes / sizeof(int)), Allocator.Persistent);
            NodeLookup = new NativeArray<int>((int)(plan.NodeLookupBytes / sizeof(int)), Allocator.Persistent);
            VoxelNodes = new NativeArray<int>((int)(plan.VoxelNodesBytes / sizeof(int)), Allocator.Persistent);
            ClusterScratch = new NativeArray<int>(plan.ClusterCounts.Nodes * 2, Allocator.Persistent);
            Nodes = new NativeArray<NavClusterNode>(plan.ClusterCounts.Nodes, Allocator.Persistent);
            Portals = new NativeArray<NavPortal>(plan.ClusterCounts.Portals, Allocator.Persistent);
            Edges = new NativeArray<NavClusterEdge>(plan.ClusterCounts.EdgesBound, Allocator.Persistent);
            EdgeKeys = new NativeArray<long>(plan.ClusterCounts.EdgesBound, Allocator.Persistent);
            EdgeCounts = new NativeArray<int>(plan.ClusterCounts.EdgesBound, Allocator.Persistent);
            EdgePortals = new NativeArray<NavPortal>(plan.ClusterCounts.Portals / 2 + 1, Allocator.Persistent);
            Blob = new NativeArray<byte>((int)plan.BlobBytes, Allocator.Persistent);
        }

        public void Dispose()
        {
            Dispose(ref Distance);
            Dispose(ref Occupancy);
            Dispose(ref Costs);
            Dispose(ref Parent);
            Dispose(ref Region);
            Dispose(ref NodeLookup);
            Dispose(ref VoxelNodes);
            Dispose(ref ClusterScratch);
            Dispose(ref Nodes);
            Dispose(ref Portals);
            Dispose(ref Edges);
            Dispose(ref EdgeKeys);
            Dispose(ref EdgeCounts);
            Dispose(ref EdgePortals);
            Dispose(ref Blob);
            PlannedBytes = 0;
        }

        private static void Dispose<T>(ref NativeArray<T> array) where T : unmanaged
        {
            if (array.IsCreated) array.Dispose();
            array = default;
        }
    }
}
