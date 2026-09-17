namespace Ember.Navigation
{
    /// <summary>门户：簇边界上双向可通行的体素对。</summary>
    public struct NavPortal
    {
        /// <summary>本簇侧体素线性下标。</summary>
        public int VoxelA;

        /// <summary>相邻簇侧体素线性下标。</summary>
        public int VoxelB;

        /// <summary>相邻簇节点下标。</summary>
        public int OtherCluster;
    }
}
