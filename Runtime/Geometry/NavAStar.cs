using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 体素级 A*（纯算法，裸指针）：可行走判定 / 代价层 / 可选簇约束
    /// （RestrictNode ≥ 0 时只扩展到 voxel→node 等于该值的格 —— 簇内细化用）。
    /// 分帧：Step 以弹出预算推进；路径由父指针链回溯。
    /// 确定性：堆同 (f, h) 按下标升序。
    /// </summary>
    public static unsafe class NavAStar
    {
        /// <summary>A* 工作区。</summary>
        public struct Context
        {
            /// <summary>网格。</summary>
            public NavGrid Grid;

            /// <summary>邻接模板。</summary>
            public int Connectivity;

            /// <summary>占据位集。</summary>
            public byte* Occupancy;

            /// <summary>距离场量化值（8 位）。</summary>
            public byte* DistanceLevels;

            /// <summary>代价乘数（byte，85 = 1.0x）。</summary>
            public byte* Costs;

            /// <summary>g 分数（∞ = 未达）。</summary>
            public float* G;

            /// <summary>父体素下标（-1 = 无父）。</summary>
            public int* Parent;

            /// <summary>体素 → 簇节点 id（簇内约束用； unrestricted 时可为 null）。</summary>
            public int* VoxelNodes;

            /// <summary>堆（f 值）。</summary>
            public float* HeapF;

            /// <summary>堆（体素）。</summary>
            public int* HeapVoxels;

            /// <summary>堆容量。</summary>
            public int HeapCapacity;

            /// <summary>堆长。</summary>
            public int HeapCount;

            /// <summary>可行走最低距离场等级。</summary>
            public int RequiredLevel;

            /// <summary>目标（启发式用）。</summary>
            public int3 Goal;

            /// <summary>簇内约束：&lt; 0 不约束；否则只扩展 VoxelNodes == 该值的格。</summary>
            public int RestrictNode;
        }

        private const float Infinity = float.MaxValue;

        /// <summary>重置（g 全 ∞，父 -1，堆清空）。</summary>
        public static void Reset(ref Context ctx, long voxelCount)
        {
            for (long i = 0; i < voxelCount; i++)
            {
                ctx.G[i] = Infinity;
                ctx.Parent[i] = -1;
            }
            ctx.HeapCount = 0;
        }

        /// <summary>起点的可走性校验 + 入堆。不可走返回 false。</summary>
        public static bool Begin(ref Context ctx, int3 start)
        {
            var grid = ctx.Grid;
            if (!grid.IsInside(start)) return false;
            int index = grid.VoxelIndex(start);
            if (!IsWalkable(ref ctx, index)) return false;

            ctx.G[index] = 0f;
            float f = Heuristic(ref ctx, start);
            return NavFlowFieldHeap.Push(ctx.HeapF, ctx.HeapVoxels, ctx.HeapCapacity,
                ref ctx.HeapCount, f, index);
        }

        /// <summary>推进至多 <paramref name="popBudget"/> 次弹出；找到目标返回 true。</summary>
        public static bool Step(ref Context ctx, long popBudget)
        {
            var grid = ctx.Grid;
            long pops = 0;
            int goalIndex = grid.VoxelIndex(ctx.Goal);

            while (ctx.HeapCount > 0 && pops < popBudget)
            {
                if (!NavFlowFieldHeap.Pop(ctx.HeapF, ctx.HeapVoxels, ref ctx.HeapCount,
                        out _, out int index))
                    return false;
                pops++;

                if (index == goalIndex) return true;

                Relax(ref ctx, grid, index);
            }

            return false;
        }

        /// <summary>一次跑完（预算 = 体素数上界）。</summary>
        public static bool RunToCompletion(ref Context ctx)
        {
            return Step(ref ctx, long.MaxValue);
        }

        /// <summary>
        /// 回溯路径写入调用方缓冲（起点 → 目标顺序）。
        /// 返回航点数量；缓冲不足返回 -1。
        /// </summary>
        public static int ExtractPath(ref Context ctx, int3 start, int3 goal,
            int3* waypoints, int capacity)
        {
            var grid = ctx.Grid;
            int goalIndex = grid.VoxelIndex(goal);
            int startIndex = grid.VoxelIndex(start);

            int count = 0;
            int current = goalIndex;
            bool reachedStart = false;
            while (current != -1)
            {
                if (count >= capacity) return -1;
                waypoints[count++] = grid.VoxelCoord(current);
                if (current == startIndex) { reachedStart = true; break; }
                current = ctx.Parent[current];
            }

            if (!reachedStart) return -1;

            // 反转成起点 → 目标。
            for (int i = 0; i < count / 2; i++)
            {
                (waypoints[i], waypoints[count - 1 - i]) = (waypoints[count - 1 - i], waypoints[i]);
            }
            return count;
        }

        private static void Relax(ref Context ctx, NavGrid grid, int index)
        {
            int3 voxel = grid.VoxelCoord(index);
            float g = ctx.G[index];
            float voxelSize = grid.VoxelSize;
            int3 dims = grid.Dimensions;

            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!NavNeighbor.IsValid(dims, ctx.Connectivity, voxel, dx, dy, dz, out int3 n))
                    continue;
                int ni = grid.VoxelIndex(n);
                if (!IsWalkable(ref ctx, ni)) continue;
                if (ctx.RestrictNode >= 0 && ctx.VoxelNodes[ni] != ctx.RestrictNode) continue;

                float step = math.length((float3)(n - voxel)) * voxelSize;
                float candidate = g + step * (ctx.Costs[ni] / 85f);

                if (candidate < ctx.G[ni])
                {
                    ctx.G[ni] = candidate;
                    ctx.Parent[ni] = index;
                    float f = candidate + Heuristic(ref ctx, n);
                    NavFlowFieldHeap.Push(ctx.HeapF, ctx.HeapVoxels, ctx.HeapCapacity,
                        ref ctx.HeapCount, f, ni);
                }
            }
        }

        private static float Heuristic(ref Context ctx, int3 voxel)
        {
            // 可采纳下界：欧氏距离 × 最小代价系数（0.25x）。
            float distance = math.length((float3)(ctx.Goal - voxel)) * ctx.Grid.VoxelSize;
            return distance * 0.25f;
        }

        private static bool IsWalkable(ref Context ctx, int index)
        {
            if ((ctx.Occupancy[index >> 3] & (byte)(1u << (index & 7))) != 0) return false;
            return ctx.DistanceLevels[index] >= ctx.RequiredLevel;
        }
    }
}
