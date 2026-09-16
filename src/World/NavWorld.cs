using Ember;

namespace Ember.Navigation
{
    /// <summary>
    /// 导航运行时数据（单例组件）：网格参数 + 各段 BufferHandle + 热替换状态。
    /// 资源模型与 CollisionWorld / SpatialTree 完全一致 —— 只存标量与句柄，
    /// 纯 blittable，无需 Dispose，随 World.Dispose 自动释放。
    ///
    /// 热替换：LoadBlob 把新数据写入<b>新</b> buffer 并翻转句柄 + Generation++；
    /// 旧句柄进 Pending 槽，帧末由 <see cref="NavWorldView.RecyclePending"/> 销毁。
    /// 正在使用旧数据的系统在本帧继续读完（句柄按 Generation 捕获）。
    /// </summary>
    public struct NavWorld : ISingletonComponent
    {
        // ---- 网格参数（加载时从 blob Meta 段写入）----
        public float OriginX;
        public float OriginY;
        public float OriginZ;
        public float VoxelSize;
        public int DimX;
        public int DimY;
        public int DimZ;
        public int TileSize;

        /// <summary>距离场满量程（烘焙半径上限，米）。量化判定必需。</summary>
        public float MaxBakeRadius;

        // ---- 当前段句柄 ----
        public BufferHandle Occupancy;      // byte 位集
        public BufferHandle Distance;       // byte 或 ushort
        public BufferHandle Region;         // int
        public BufferHandle Cost;           // byte
        public BufferHandle ClusterNodes;   // NavClusterNode
        public BufferHandle ClusterPortals; // NavPortal
        public BufferHandle ClusterEdges;   // NavClusterEdge
        public BufferHandle EdgePortals;    // NavPortal
        public BufferHandle Links;          // NavOffMeshLink

        // ---- 计数 ----
        public long VoxelCount;
        public int RegionCount;
        public int ClusterNodeCount;
        public int PortalCount;
        public int ClusterEdgeCount;
        public int LinkCount;
        public byte DistanceBits;

        /// <summary>数据已加载可查询。</summary>
        public byte Ready;

        /// <summary>热替换代际：每次加载 +1，系统按代际辨别旧数据。</summary>
        public int Generation;

        // ---- 备用旧句柄（按元素类型各一槽；热替换时复用，替换下来的真正销毁）----
        // 避免跨类型 DestroyBuffer 的不可能（BufferStore 按类型分库），
        // 同时让连续重烘焙复用内存、零分配。
        public BufferHandle SpareOccupancy;      // byte
        public BufferHandle SpareDistance;       // byte 或 ushort（按 DistanceBits 区分槽语义）
        public BufferHandle SpareRegion;         // int
        public BufferHandle SpareCost;           // byte
        public BufferHandle SpareClusterNodes;   // NavClusterNode
        public BufferHandle SpareClusterPortals; // NavPortal（节点分组表）
        public BufferHandle SpareClusterEdges;   // NavClusterEdge
        public BufferHandle SpareEdgePortals;    // NavPortal（边紧凑表）
        public BufferHandle SpareLinks;          // NavOffMeshLink

        /// <summary>是否已完成首次加载。</summary>
        public readonly bool IsReady => Ready != 0;
    }
}
