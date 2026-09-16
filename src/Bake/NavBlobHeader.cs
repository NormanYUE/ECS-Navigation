using Ember.Collision;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 烘焙 blob 头部（Meta 段，磁盘布局的固定前缀）。
    /// 所有偏移相对于 blob 起始；段按 8 字节对齐。
    /// </summary>
    public unsafe struct NavBlobHeader
    {
        /// <summary>当前 blob 格式版本。</summary>
        public const int CurrentVersion = 2;

        /// <summary>魔数 "NAVB"（小端 0x4E415642），校验文件类型。</summary>
        public const uint Magic = 0x4E415642;

        /// <summary>魔数。</summary>
        public uint MagicValue;

        /// <summary>格式版本。</summary>
        public int Version;

        /// <summary>导航维度。</summary>
        public CollisionDimension Dimension;

        /// <summary>距离场量化位宽（8 或 16）。</summary>
        public int DistanceBits;

        /// <summary>体素边长（米）。</summary>
        public float VoxelSize;

        /// <summary>烘焙半径上限（距离场满量程，米）。</summary>
        public float MaxBakeRadius;

        /// <summary>tile 边长（体素数）。</summary>
        public int TileSize;

        /// <summary>体素原点（世界坐标）。</summary>
        public float3 Origin;

        /// <summary>各轴体素数（无效轴 = 1）。</summary>
        public int3 Dimensions;

        /// <summary>世界包围盒（烘焙范围）。</summary>
        public Aabb Bounds;

        /// <summary>连通区域数。</summary>
        public int RegionCount;

        /// <summary>簇节点数。</summary>
        public int ClusterNodeCount;

        /// <summary>簇有向门户数。</summary>
        public int PortalCount;

        /// <summary>簇无向边数。</summary>
        public int ClusterEdgeCount;

        /// <summary>off-mesh link 数。</summary>
        public int LinkCount;

        /// <summary>各段字节偏移（相对 blob 起始）。</summary>
        public fixed long SegmentOffsets[(int)NavBlobSegment.Count];

        /// <summary>各段字节长度。</summary>
        public fixed long SegmentLengths[(int)NavBlobSegment.Count];
    }
}
