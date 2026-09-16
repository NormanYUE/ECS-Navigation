using Ember.Collision;

namespace Ember.Navigation
{
    /// <summary>烘焙输入：维度 / 体素 / tile / 距离场量化参数。</summary>
    public struct NavBakeInput
    {
        /// <summary>导航维度（XY / XZ 为 2D，XYZ 为 3D）。</summary>
        public CollisionDimension Dimension;

        /// <summary>体素边长（米）。</summary>
        public float VoxelSize;

        /// <summary>tile 边长（体素数）。</summary>
        public int TileSize;

        /// <summary>距离场量化位宽：8 或 16。</summary>
        public byte DistanceBits;

        /// <summary>距离场满量程 = 烘焙支持的最大代理半径（米）。</summary>
        public float MaxBakeRadius;

        /// <summary>2D 网格标记（Dimension != XYZ）。</summary>
        public readonly bool Is2D => Dimension != CollisionDimension.XYZ;

        /// <summary>量化级数（255 或 65535）。</summary>
        public readonly int QuantLevels => DistanceBits == 16 ? 65535 : 255;

        /// <summary>连通邻接模板：2D 用 4 或 8，3D 用 6 或 26。</summary>
        public byte Connectivity;
    }
}
