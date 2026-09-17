namespace Ember.Navigation
{
    /// <summary>簇间邻接边：门户归并后的加权边（权重 = 门户数，开口越大越便宜）。</summary>
    public struct NavClusterEdge
    {
        /// <summary>簇节点下标 A（&lt; B）。</summary>
        public int ClusterA;

        /// <summary>簇节点下标 B。</summary>
        public int ClusterB;

        /// <summary>权重（门户数量）。</summary>
        public float Weight;

        /// <summary>组成该边的门户在门户表中的起始下标。</summary>
        public int PortalStart;

        /// <summary>门户数量。</summary>
        public int PortalCount;
    }
}
