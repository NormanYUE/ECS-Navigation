using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 距离场采样（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 距离场按<b>等级</b>量化存储（8 位 0–255 / 16 位 0–65535），
    /// 满量程对应 <see cref="NavWorld.MaxBakeRadius"/>，换算成世界距离需要
    /// <c>level / levels * maxRadius</c> —— 直接当米用会差一个满量程倍数。
    ///
    /// 梯度用中心差分求得，方向背离最近障碍，正好是 ORCA 静态障碍约束需要的
    /// 「最近障碍方向 + 距离」，无需对障碍做形状查询或射线。
    /// </summary>
    public static unsafe class NavDistanceField
    {
        private const float Epsilon = 1e-6f;

        /// <summary>按位宽换算满量程等级数。</summary>
        public static float Levels(bool sixteenBit) => sixteenBit ? 65535f : 255f;

        /// <summary>
        /// 体素处的世界距离。体素越界时钳到网格内（边界处中心差分退化为单侧差分，
        /// 幅度偏小但方向仍可用）。
        /// </summary>
        public static float DistanceAt(
            NavGrid grid, void* field, bool sixteenBit, float maxRadius, int3 voxel)
        {
            if (field == null) return 0f;
            voxel = math.clamp(voxel, int3.zero, grid.Dimensions - 1);
            int index = grid.VoxelIndex(voxel);
            float level = sixteenBit ? ((ushort*)field)[index] : ((byte*)field)[index];
            return level / Levels(sixteenBit) * maxRadius;
        }

        /// <summary>
        /// 世界点处的距离与单位梯度，取<b>最近体素</b>的值（不做三线性插值），
        /// 与流场/障碍判定的取样口径一致。
        /// 点在网格外时返回 false，距离与梯度归零。
        /// </summary>
        public static bool Sample(
            NavGrid grid, void* field, bool sixteenBit, float maxRadius, float3 world,
            out float distance, out float3 gradient)
        {
            distance = 0f;
            gradient = float3.zero;
            if (field == null) return false;

            int3 voxel = grid.WorldToVoxelOnGrid(world);
            if (!grid.IsInside(voxel)) return false;

            distance = DistanceAt(grid, field, sixteenBit, maxRadius, voxel);

            float3 step = new float3(1f, 0f, 0f);
            float3 difference = new(
                DistanceAt(grid, field, sixteenBit, maxRadius, voxel + (int3)step)
                    - DistanceAt(grid, field, sixteenBit, maxRadius, voxel - (int3)step),
                DistanceAt(grid, field, sixteenBit, maxRadius, voxel + new int3(0, 1, 0))
                    - DistanceAt(grid, field, sixteenBit, maxRadius, voxel - new int3(0, 1, 0)),
                DistanceAt(grid, field, sixteenBit, maxRadius, voxel + new int3(0, 0, 1))
                    - DistanceAt(grid, field, sixteenBit, maxRadius, voxel - new int3(0, 0, 1)));

            float lengthSq = math.lengthsq(difference);
            if (lengthSq <= Epsilon * Epsilon) return true;

            gradient = difference * math.rsqrt(lengthSq);
            return true;
        }

        /// <summary>世界点处是否落在障碍上或障碍内（距离为 0）。</summary>
        public static bool IsBlocked(
            NavGrid grid, void* field, bool sixteenBit, float maxRadius, float3 world)
        {
            int3 voxel = grid.WorldToVoxelOnGrid(world);
            if (!grid.IsInside(voxel)) return true;
            return DistanceAt(grid, field, sixteenBit, maxRadius, voxel) <= 0f;
        }
    }
}
