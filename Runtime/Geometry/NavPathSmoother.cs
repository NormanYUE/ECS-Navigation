using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 路径拉绳（string pulling）：输入体素航点（A* 原始路径），
    /// 贪心可见性替代逐段射线 —— 沿线段按半步长采样，全部可走（含半径侵蚀）
    /// 即通视，直接连到最远可见航点。主线程热点由此消失（距离场读取代射线检测）。
    /// 确定性：采样步长固定，判定只依赖场数据。
    /// </summary>
    public static unsafe class NavPathSmoother
    {
        /// <summary>
        /// 拉绳简化。
        /// </summary>
        /// <param name="grid">网格。</param>
        /// <param name="occupancy">占据位集。</param>
        /// <param name="distanceLevels">距离场量化值。</param>
        /// <param name="preferLevel">
        /// 抄近道偏好的净空等级（通常严于 <paramref name="minLevel"/>）：按它通视才肯跳过去，
        /// 于是平滑后的路径天然离墙远一点。它只影响「跳多远」，不影响路径成不成立。
        /// </param>
        /// <param name="minLevel">
        /// 路径成立的**最低**等级 —— 必须与 A* 搜索用的等级一致。
        /// 混淆这两个等级会让平滑凭空失败：A* 按等级 A 验过的路径可能处处不满足等级 B，
        /// 于是通视处处不成立、整条请求被判 Failed（详见下方两趟扫描）。
        /// </param>
        /// <param name="waypoints">输入航点（体素坐标）。</param>
        /// <param name="count">输入航点数。</param>
        /// <param name="output">输出缓冲。</param>
        /// <param name="capacity">输出容量。</param>
        /// <returns>输出航点数；首点不可走或容量不足返回 -1。</returns>
        public static int PullString(
            in NavGrid grid,
            byte* occupancy,
            byte* distanceLevels,
            int preferLevel,
            int minLevel,
            int3* waypoints,
            int count,
            int3* output,
            int capacity)
        {
            if (count <= 0 || capacity <= 0) return -1;
            // 起点校验用 minLevel：它本来就是 A* 验过的路径体素。
            if (!IsWalkable(grid, occupancy, distanceLevels, minLevel, waypoints[0])) return -1;

            int written = 0;
            output[written++] = waypoints[0];

            int anchor = 0;
            while (anchor < count - 1)
            {
                // 第一趟：按偏好等级找最远通视航点 —— 跳得越远，路径越直、越离墙。
                int furthest = FurthestVisible(grid, occupancy, distanceLevels, preferLevel,
                    waypoints, count, anchor);

                // 第二趟：退到路径成立的最低等级。相邻航点在同一等级的 A* 里必然相连，
                // 所以这一趟至少能推进一格 —— 平滑退化成「少省几个点」，
                // 而不是把整条请求判失败。
                //
                // 两趟并作一趟是有代价的：偏好等级一旦严于搜索等级（例如按半径 × 余量取），
                // 只要走廊里有一段净空落在两者之间，第一趟就处处不通视、
                // PullString 直接返回 -1，A* 明明搜到了路，请求却被判 Failed。
                if (furthest < 0)
                    furthest = FurthestVisible(grid, occupancy, distanceLevels, minLevel,
                        waypoints, count, anchor);

                if (furthest < 0) return -1; // 相邻航点都不通视 —— 数据异常

                if (written >= capacity) return -1;
                output[written++] = waypoints[furthest];
                anchor = furthest;
            }

            return written;
        }

        /// <summary>从最远端往回找第一个与 <paramref name="anchor"/> 通视的航点；没有返回 -1。</summary>
        private static int FurthestVisible(in NavGrid grid, byte* occupancy, byte* distanceLevels,
            int requiredLevel, int3* waypoints, int count, int anchor)
        {
            for (int candidate = count - 1; candidate > anchor; candidate--)
            {
                if (HasLineOfSight(grid, occupancy, distanceLevels, requiredLevel,
                        waypoints[anchor], waypoints[candidate]))
                {
                    return candidate;
                }
            }

            return -1;
        }

        /// <summary>两点通视：线段按体素边长一半步长采样，全部可走即通视。</summary>
        public static bool HasLineOfSight(
            in NavGrid grid,
            byte* occupancy,
            byte* distanceLevels,
            int requiredLevel,
            int3 a,
            int3 b)
        {
            float3 worldA = grid.VoxelToWorld(a);
            float3 worldB = grid.VoxelToWorld(b);
            float length = math.length(worldB - worldA);
            float step = math.max(grid.VoxelSize * 0.5f, 1e-5f);
            int samples = math.max(1, (int)math.ceil(length / step));

            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                float3 point = math.lerp(worldA, worldB, t);
                int3 voxel = grid.WorldToVoxel(point);
                if (!grid.IsInside(voxel)) return false;
                if (!IsWalkable(grid, occupancy, distanceLevels, requiredLevel, voxel)) return false;
            }
            return true;
        }

        private static bool IsWalkable(in NavGrid grid,
            byte* occupancy, byte* distanceLevels, int requiredLevel, int3 voxel)
        {
            if (!grid.IsInside(voxel)) return false;
            long index = grid.VoxelIndex(voxel);
            if ((occupancy[index >> 3] & (byte)(1u << (int)(index & 7))) != 0) return false;
            return distanceLevels[index] >= requiredLevel;
        }
    }
}
