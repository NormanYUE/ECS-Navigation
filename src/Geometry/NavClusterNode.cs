namespace Ember.Navigation
{
    /// <summary>簇图节点：一个 tile 内的一个连通区域块（tile × region 唯一）。</summary>
    public struct NavClusterNode
    {
        /// <summary>tile 线性下标。</summary>
        public int TileIndex;

        /// <summary>全局连通区域 id（<see cref="NavRegionLabeler"/> 产出）。</summary>
        public int RegionId;

        /// <summary>门户在门户表中的起始下标。</summary>
        public int PortalStart;

        /// <summary>门户数量。</summary>
        public int PortalCount;
    }
}
