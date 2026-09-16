using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 多源 Dijkstra 流场求解器（纯算法，裸指针，可 CLI 对拍）：
    /// 加权波前推进（吃代价层），产出每体素「到最近目标的累计代价」，
    /// 实体侧按梯度下降取期望方向 —— 不存梯度场，查询时扫邻格即可。
    ///
    /// 分帧：Step 以「弹出预算」推进，波前（堆 + 距离数组）常驻跨帧，
    /// 未完成字段标记 Pending，实体沿用旧流场或直冲（由系统层处理）。
    ///
    /// 可行走性：调用方给出 RequiredLevel（距离场量化半径等级），
    /// 距离场量化值 &lt; RequiredLevel 或占据的格不可走。
    /// 确定性：堆同代价按下标升序，波前按确定顺序扩展。
    /// </summary>
    public static unsafe class NavFlowFieldSolver
    {
        /// <summary>求解器工作区（跨帧常驻，World buffer 承载）。</summary>
        public struct Context
        {
            /// <summary>网格。</summary>
            public NavGrid Grid;

            /// <summary>邻接模板（4/8 或 6/26，与烘焙一致）。</summary>
            public int Connectivity;

            /// <summary>每体素累计代价（∞ = 未达）。</summary>
            public float* Distances;

            /// <summary>距离场量化值（8 位；可行走性判定）。</summary>
            public byte* DistanceLevels;

            /// <summary>占据位集。</summary>
            public byte* Occupancy;

            /// <summary>代价乘数（量化 byte，85 = 1.0x）。</summary>
            public byte* Costs;

            /// <summary>波前堆（代价）。</summary>
            public float* HeapCosts;

            /// <summary>波前堆（体素）。</summary>
            public int* HeapVoxels;

            /// <summary>堆容量。</summary>
            public int HeapCapacity;

            /// <summary>当前堆长（调用方初始置 0）。</summary>
            public int HeapCount;

            /// <summary>可行走的最低距离场量化等级。</summary>
            public int RequiredLevel;
        }

        private const float Infinity = float.MaxValue;

        /// <summary>重置距离数组（全部 ∞），堆清空。</summary>
        public static void Reset(ref Context ctx, long voxelCount)
        {
            for (long i = 0; i < voxelCount; i++)
                ctx.Distances[i] = Infinity;
            ctx.HeapCount = 0;
        }

        /// <summary>播种单源（目标体素）；不可走的源被忽略。</summary>
        public static bool Seed(ref Context ctx, int3 target)
        {
            var grid = ctx.Grid;
            if (!grid.IsInside(target)) return false;
            int index = grid.VoxelIndex(target);
            if (!IsWalkable(ref ctx, index)) return false;

            ctx.Distances[index] = 0f;
            return NavFlowFieldHeap.Push(ctx.HeapCosts, ctx.HeapVoxels, ctx.HeapCapacity,
                ref ctx.HeapCount, 0f, index);
        }

        /// <summary>
        /// 推进波前至多 <paramref name="popBudget"/> 次弹出。
        /// </summary>
        /// <returns>true = 波前耗尽（全部可达格已结算）。</returns>
        public static bool Step(ref Context ctx, long popBudget)
        {
            var grid = ctx.Grid;
            long pops = 0;

            while (ctx.HeapCount > 0 && pops < popBudget)
            {
                if (!NavFlowFieldHeap.Pop(ctx.HeapCosts, ctx.HeapVoxels, ref ctx.HeapCount,
                        out float cost, out int index))
                    return true;
                pops++;

                // 过期堆项（已有更优结算）跳过。
                if (cost > ctx.Distances[index]) continue;

                Relax(ref ctx, grid, index, cost);
            }

            return ctx.HeapCount == 0;
        }

        /// <summary>
        /// 查询某体素的梯度方向：可达邻格中累计代价最低者。
        /// 返回 false = 自身不可走或所有邻格更差（已到局部最优 / 目标）。
        /// </summary>
        public static bool TryGetNext(ref Context ctx, int3 voxel, out int3 next)
        {
            next = default;
            var grid = ctx.Grid;
            if (!grid.IsInside(voxel)) return false;
            int index = grid.VoxelIndex(voxel);
            if (!IsWalkable(ref ctx, index)) return false;

            float best = ctx.Distances[index];
            int3 bestCoord = voxel;
            bool found = false;

            int3 dims = grid.Dimensions;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!IsValidNeighbor(dims, ctx.Connectivity, voxel, dx, dy, dz, out int3 n))
                    continue;
                int ni = grid.VoxelIndex(n);
                float d = ctx.Distances[ni];
                if (d < best)
                {
                    best = d;
                    bestCoord = n;
                    found = true;
                }
            }

            if (!found) return false;
            next = bestCoord;
            return true;
        }

        /// <summary>体素到目标的累计代价（不可走 / 未达返回 ∞）。</summary>
        public static float DistanceAt(ref Context ctx, int3 voxel)
        {
            var grid = ctx.Grid;
            if (!grid.IsInside(voxel)) return Infinity;
            int index = grid.VoxelIndex(voxel);
            if (!IsWalkable(ref ctx, index)) return Infinity;
            return ctx.Distances[index];
        }

        private static void Relax(ref Context ctx, NavGrid grid, int index, float cost)
        {
            int3 voxel = grid.VoxelCoord(index);
            float voxelSize = grid.VoxelSize;
            int3 dims = grid.Dimensions;

            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!IsValidNeighbor(dims, ctx.Connectivity, voxel, dx, dy, dz, out int3 n))
                    continue;
                int ni = grid.VoxelIndex(n);
                if (!IsWalkable(ref ctx, ni)) continue;

                float step = math.length((float3)(n - voxel)) * voxelSize;
                float stepCost = step * (ctx.Costs[ni] / 85f);
                float candidate = cost + stepCost;

                if (candidate < ctx.Distances[ni])
                {
                    ctx.Distances[ni] = candidate;
                    NavFlowFieldHeap.Push(ctx.HeapCosts, ctx.HeapVoxels, ctx.HeapCapacity,
                        ref ctx.HeapCount, candidate, ni);
                }
            }
        }

        /// <summary>
        /// 邻格有效性：模板（4/8 限制在平面内，6/26 允许立体）+ 界内 + 非自身。
        /// </summary>
        private static bool IsValidNeighbor(int3 dims, int connectivity, int3 voxel,
            int dx, int dy, int dz, out int3 neighbor)
        {
            neighbor = default;
            if (dx == 0 && dy == 0 && dz == 0) return false;

            int manhattan = math.abs(dx) + math.abs(dy) + math.abs(dz);
            bool diagonal = connectivity == 8 || connectivity == 26;

            if (manhattan > 1 && !diagonal) return false;

            // 2D 模板限制在平面内。
            if ((connectivity == 4 || connectivity == 8) && dims.z == 1 && dz != 0) return false;
            if ((connectivity == 4 || connectivity == 8) && dims.y == 1 && dy != 0) return false;

            int3 n = voxel + new int3(dx, dy, dz);
            if (n.x < 0 || n.y < 0 || n.z < 0 || n.x >= dims.x || n.y >= dims.y || n.z >= dims.z)
                return false;

            neighbor = n;
            return true;
        }

        private static bool IsWalkable(ref Context ctx, int index)
        {
            if ((ctx.Occupancy[index >> 3] & (byte)(1u << (index & 7))) != 0) return false;
            return ctx.DistanceLevels[index] >= ctx.RequiredLevel;
        }
    }
}
