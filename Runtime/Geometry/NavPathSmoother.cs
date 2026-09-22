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

        /// <summary>
        /// 两点通视：把线段经过的<b>每一个</b>体素都查一遍（超覆盖遍历），
        /// 而不是按固定步长取采样点。
        ///
        /// <b>为什么不能用点采样</b>：线段只要在某个不可走体素里掠过一小段
        /// （比采样步长短），采样点就会整体跨过去、判成通视。这不是偶发 ——
        /// 8 邻接的对角步长 0.5×√2，<b>中点精确落在四个体素共享的角上</b>，
        /// 正是最容易擦到被堵角落的位置；按半格采样时采样点落在 1/3、2/3 处，
        /// 恰好跨过那个中点。实测消费方 23 条路径边里 7 条按代理半径走不通，
        /// 按旧口径<b>全部</b>判成通视 —— 代理沿这条线走就会踩进不可走体素，
        /// 然后被自己的防穿墙层拦下，站在原地再也过不去。
        ///
        /// 恰好从格点（多个轴同时跨越）穿过时，把这些轴的<b>所有非空组合</b>格子都查一遍：
        /// 斜对角那一格正是这么被擦到的，漏掉它整件事就白做。
        /// 格对齐几何下这条路径是常态而非例外 —— 对角步每一步都同时跨两个轴。
        /// </summary>
        public static bool HasLineOfSight(
            in NavGrid grid,
            byte* occupancy,
            byte* distanceLevels,
            int requiredLevel,
            int3 a,
            int3 b)
        {
            if (!IsWalkable(grid, occupancy, distanceLevels, requiredLevel, a)) return false;
            if (!IsWalkable(grid, occupancy, distanceLevels, requiredLevel, b)) return false;

            float3 worldA = grid.VoxelToWorld(a);
            float3 worldB = grid.VoxelToWorld(b);
            float3 delta = worldB - worldA;

            var step = new int3(
                delta.x > 0f ? 1 : delta.x < 0f ? -1 : 0,
                delta.y > 0f ? 1 : delta.y < 0f ? -1 : 0,
                delta.z > 0f ? 1 : delta.z < 0f ? -1 : 0);

            float cell = grid.VoxelSize;
            float3 origin = grid.Origin;

            // 沿线段到下一条格线的参数距离，以及跨一整格所需的参数增量。
            // 该轴无位移时给正无穷，取最小值时自然让别的轴独占。
            var tMax = new float3(
                NextLine(delta.x, step.x, origin.x, a.x, worldA.x, cell),
                NextLine(delta.y, step.y, origin.y, a.y, worldA.y, cell),
                NextLine(delta.z, step.z, origin.z, a.z, worldA.z, cell));
            var tDelta = new float3(
                step.x == 0 ? float.PositiveInfinity : cell / math.abs(delta.x),
                step.y == 0 ? float.PositiveInfinity : cell / math.abs(delta.y),
                step.z == 0 ? float.PositiveInfinity : cell / math.abs(delta.z));

            int3 cur = a;
            // 上界：两格之间最多跨越 |Δx|+|Δy|+|Δz| 条格线。留余量防浮点退化。
            int guard = math.abs(b.x - a.x) + math.abs(b.y - a.y) + math.abs(b.z - a.z) + 4;

            for (int i = 0; i < guard; i++)
            {
                if (math.all(cur == b)) return true;

                float tNext = math.cmin(tMax);
                // 还有轴可跨却已无路可走 —— 只可能是数值退化，保守判不通。
                if (!math.isfinite(tNext)) return false;

                bool3 tie = math.abs(tMax - tNext) <= 1e-6f;

                // 同时跨越的各轴的所有非空组合格子。最多 3 轴同时跨越，即 7 格。
                for (int mask = 1; mask < 8; mask++)
                {
                    var offset = new int3(
                        (mask & 1) != 0 && tie.x ? step.x : 0,
                        (mask & 2) != 0 && tie.y ? step.y : 0,
                        (mask & 4) != 0 && tie.z ? step.z : 0);
                    if (offset.x == 0 && offset.y == 0 && offset.z == 0) continue;
                    if (!IsWalkable(grid, occupancy, distanceLevels, requiredLevel, cur + offset))
                        return false;
                }

                if (tie.x) { cur.x += step.x; tMax.x += tDelta.x; }
                if (tie.y) { cur.y += step.y; tMax.y += tDelta.y; }
                if (tie.z) { cur.z += step.z; tMax.z += tDelta.z; }

                if (!IsWalkable(grid, occupancy, distanceLevels, requiredLevel, cur)) return false;
            }

            return false;
        }

        /// <summary>沿线段到下一条格线的参数距离；该轴无位移时给正无穷。</summary>
        private static float NextLine(float delta, int step, float origin, int cellIndex,
            float from, float cell)
        {
            if (step == 0) return float.PositiveInfinity;
            float line = origin + (cellIndex + (step > 0 ? 1 : 0)) * cell;
            return (line - from) / delta;
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
