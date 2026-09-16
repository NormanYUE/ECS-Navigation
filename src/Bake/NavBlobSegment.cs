namespace Ember.Navigation
{
    /// <summary>blob 段下标（偏移表索引）。</summary>
    public enum NavBlobSegment : int
    {
        /// <summary>Meta 段（头部本身：版本 / 网格参数 / 计数 / 偏移表）。</summary>
        Meta = 0,

        /// <summary>占据位集。</summary>
        Occupancy = 1,

        /// <summary>量化距离场（byte 或 ushort）。</summary>
        Distance = 2,

        /// <summary>连通区域 id（int / 体素）。</summary>
        Region = 3,

        /// <summary>代价乘数量化（byte，85 = 1.0x，范围 0.25x–3.0x）。</summary>
        Cost = 4,

        /// <summary>簇图节点。</summary>
        ClusterNodes = 5,

        /// <summary>簇图门户（按节点分组）。</summary>
        ClusterPortals = 6,

        /// <summary>簇图无向边。</summary>
        ClusterEdges = 7,

        /// <summary>边门户紧凑表（每条边连续区间）。</summary>
        EdgePortals = 8,

        /// <summary>off-mesh link 双向表。</summary>
        Links = 9,

        /// <summary>段总数。</summary>
        Count = 10,
    }
}
