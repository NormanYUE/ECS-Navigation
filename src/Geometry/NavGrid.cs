using Ember.Collision;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 导航体素网格：原点 / 维度 / 体素与 tile 尺寸。
    /// 2D 时无效轴维度为 1（XY：Z=1；XZ：Y=1）。
    /// 全部坐标变换的唯一权威实现，烘焙与运行时寻路共用。
    /// </summary>
    public struct NavGrid
    {
        /// <summary>世界原点（网格最小角）。</summary>
        public float3 Origin;

        /// <summary>体素边长（米）。</summary>
        public float VoxelSize;

        /// <summary>各轴体素数（无效轴 = 1）。</summary>
        public int3 Dimensions;

        /// <summary>tile 边长（体素数）。</summary>
        public int TileSize;

        /// <summary>无效轴（2D 时被忽略的轴下标；3D 返回 -1）。</summary>
        public readonly int InactiveAxis
        {
            get
            {
                if (Dimensions.z == 1 && (Dimensions.x > 1 || Dimensions.y > 1)) return 2;
                if (Dimensions.y == 1 && Dimensions.x > 1) return 1;
                return -1;
            }
        }

        /// <summary>总体素数。</summary>
        public readonly long VoxelCount => (long)Dimensions.x * Dimensions.y * Dimensions.z;

        /// <summary>各轴 tile 数 = ceil(Dimensions / TileSize)。</summary>
        public readonly int3 TileCounts => new(
            (Dimensions.x + TileSize - 1) / TileSize,
            (Dimensions.y + TileSize - 1) / TileSize,
            (Dimensions.z + TileSize - 1) / TileSize);

        /// <summary>总 tile 数。</summary>
        public readonly long TileCount
        {
            get
            {
                int3 t = TileCounts;
                return (long)t.x * t.y * t.z;
            }
        }

        /// <summary>世界坐标 → 体素坐标（向下取整，不做边界钳制）。</summary>
        public readonly int3 WorldToVoxel(float3 world) =>
            (int3)math.floor((world - Origin) / VoxelSize);

        /// <summary>体素坐标 → 体素中心世界坐标。</summary>
        public readonly float3 VoxelToWorld(int3 voxel) =>
            Origin + ((float3)voxel + 0.5f) * VoxelSize;

        /// <summary>体素线性下标（调用方保证分量在界内）。</summary>
        public readonly int VoxelIndex(int3 voxel) =>
            voxel.x + Dimensions.x * (voxel.y + Dimensions.y * voxel.z);

        /// <summary>体素线性下标 → 体素坐标。</summary>
        public readonly int3 VoxelCoord(int index) =>
            new(index % Dimensions.x,
                (index / Dimensions.x) % Dimensions.y,
                index / (Dimensions.x * Dimensions.y));

        /// <summary>体素所在 tile 的各轴下标。</summary>
        public readonly int3 VoxelToTileCoord(int3 voxel) => voxel / TileSize;

        /// <summary>tile 线性下标。</summary>
        public readonly int TileIndex(int3 tileCoord)
        {
            int3 t = TileCounts;
            return tileCoord.x + t.x * (tileCoord.y + t.y * tileCoord.z);
        }

        /// <summary>体素线性下标 → tile 线性下标。</summary>
        public readonly int VoxelToTileIndex(int3 voxel) => TileIndex(VoxelToTileCoord(voxel));

        /// <summary>坐标是否在网格界内。</summary>
        public readonly bool IsInside(int3 voxel) =>
            voxel.x >= 0 && voxel.y >= 0 && voxel.z >= 0
            && voxel.x < Dimensions.x && voxel.y < Dimensions.y && voxel.z < Dimensions.z;
    }
}
