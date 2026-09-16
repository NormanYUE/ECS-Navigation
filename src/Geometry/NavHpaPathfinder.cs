using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 分层 A*（HPA*）：簇图上 A*（节点 = tile × 区域，边 = 门户）→ 簇序列 →
    /// 簇内 A*（RestrictNode）缝合门户穿越点 → 体素路径。
    /// 簇图节点数比体素少约两个数量级，全局搜索成本由此而来。
    /// 路径代价 ≥ 单层 A* 最优（门户选择启发式所致），验收对拍取 15% 容差。
    /// </summary>
    public static unsafe class NavHpaPathfinder
    {
        /// <summary>诊断：最近失败的簇内段（节点 / 起止体素）。仅在调试构建有意义。</summary>
        public static int DebugLastSegmentNode = -1;
        public static int3 DebugLastSegmentFrom;
        public static int3 DebugLastSegmentTo;

        /// <summary>最近一次寻路失败原因（诊断用；成功时 Success）。</summary>
        public enum PathStatus
        {
            /// <summary>成功。</summary>
            Success = 0,

            /// <summary>起止点越界。</summary>
            OutOfBounds = 1,

            /// <summary>起点无簇节点。</summary>
            NoStartNode = 2,

            /// <summary>终点无簇节点。</summary>
            NoGoalNode = 3,

            /// <summary>簇级搜索失败（不可达 / 堆溢出）。</summary>
            ClusterSearchFailed = 4,

            /// <summary>簇序列中的相邻簇找不到边（数据异常）。</summary>
            EdgeNotFound = 5,

            /// <summary>簇内 A* 起点不可走 / 入堆失败。</summary>
            SegmentBeginFailed = 6,

            /// <summary>簇内 A* 搜索穷尽未达目标。</summary>
            SegmentSearchFailed = 8,

            /// <summary>输出容量不足。</summary>
            CapacityExceeded = 7,
        }

        /// <summary>最近一次 <see cref="FindPath"/> 的状态。</summary>
        public static PathStatus LastStatus { get; private set; }

        /// <summary>HPA* 工作区。</summary>
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

            /// <summary>可行走最低距离场等级。</summary>
            public int RequiredLevel;

            /// <summary>簇节点。</summary>
            public NavClusterNode* Nodes;

            /// <summary>簇节点数。</summary>
            public int NodeCount;

            /// <summary>簇边。</summary>
            public NavClusterEdge* Edges;

            /// <summary>簇边数。</summary>
            public int EdgeCount;

            /// <summary>边门户紧凑表。</summary>
            public NavPortal* EdgePortals;

            /// <summary>体素 → 簇节点 id（blob VoxelNodes 段，加载时 memcpy）。</summary>
            public int* VoxelNodes;

            // ---- 簇级搜索工作区 ----
            /// <summary>簇级 g 分数。</summary>
            public float* ClusterG;

            /// <summary>簇级父节点。</summary>
            public int* ClusterParent;

            /// <summary>簇级堆（f 值）。</summary>
            public float* ClusterHeapF;

            /// <summary>簇级堆（节点）。</summary>
            public int* ClusterHeapV;

            /// <summary>
            /// 簇级堆容量。无 decrease-key 的惰性删除堆会重复入堆，
            /// 容量需 ≥ 8 × 节点数（经验上界；溢出时 Push 返回 false，搜索优雅失败）。
            /// P9 引入索引堆后收紧为节点数。
            /// </summary>
            public int ClusterHeapCapacity;

            /// <summary>簇序列输出缓冲。</summary>
            public int* ClusterPath;

            /// <summary>簇序列容量。</summary>
            public int ClusterPathCapacity;

            // ---- 簇内 A* 工作区（复用 NavAStar）----
            /// <summary>簇内 A* 上下文（G / Parent / 堆指针由调用方填好）。</summary>
            public NavAStar.Context AStar;

            /// <summary>簇内路径提取缓冲。</summary>
            public int3* SegmentWaypoints;

            /// <summary>单段容量。</summary>
            public int SegmentCapacity;
        }

        /// <summary>
        /// 分层寻路：输出体素航点（起点 → 目标）。
        /// </summary>
        /// <returns>航点数量；失败返回 -1。</returns>
        public static int FindPath(ref Context ctx, int3 start, int3 goal,
            int3* waypoints, int capacity)
        {
            var grid = ctx.Grid;
            if (!grid.IsInside(start) || !grid.IsInside(goal))
            {
                LastStatus = PathStatus.OutOfBounds;
                return -1;
            }
            int startNode = ctx.VoxelNodes[grid.VoxelIndex(start)];
            int goalNode = ctx.VoxelNodes[grid.VoxelIndex(goal)];
            if (startNode < 0)
            {
                LastStatus = PathStatus.NoStartNode;
                return -1;
            }
            if (goalNode < 0)
            {
                LastStatus = PathStatus.NoGoalNode;
                return -1;
            }

            // 同簇：直接簇内 A*。
            if (startNode == goalNode)
            {
                int single = RunSegment(ref ctx, start, goal, startNode, waypoints, capacity);
                if (single >= 0) LastStatus = PathStatus.Success;
                return single;
            }

            // 簇级 A*（Dijkstra：边权 = 门户两侧体素的世界距离）。
            int clusterCount = SearchClusters(ref ctx, startNode, goalNode);
            if (clusterCount < 0)
            {
                LastStatus = PathStatus.ClusterSearchFailed;
                return -1;
            }

            // 逐簇缝合。
            int written = 0;
            int3 cursor = start;
            for (int i = 0; i < clusterCount - 1; i++)
            {
                int nodeA = ctx.ClusterPath[i];
                int nodeB = ctx.ClusterPath[i + 1];
                if (!TryFindEdge(ref ctx, nodeA, nodeB, out NavClusterEdge edge))
                {
                    LastStatus = PathStatus.EdgeNotFound;
                    return -1;
                }

                // 门户选择：几何启发式 —— 取「当前位置 → 入口 + 出口 → 目标」
                // 距离最小的门户（决定性：严格小于取首个最优）。
                float bestPortalScore = float.MaxValue;
                int3 entry = default, exit = default;
                float3 cursorWorld = grid.VoxelToWorld(cursor);
                float3 goalWorld = grid.VoxelToWorld(goal);
                for (int p = 0; p < edge.PortalCount; p++)
                {
                    ref NavPortal portal = ref ctx.EdgePortals[edge.PortalStart + p];
                    int3 candidateEntry = ctx.VoxelNodes[portal.VoxelA] == nodeA
                        ? grid.VoxelCoord(portal.VoxelA)
                        : grid.VoxelCoord(portal.VoxelB);
                    int3 candidateExit = ctx.VoxelNodes[portal.VoxelA] == nodeA
                        ? grid.VoxelCoord(portal.VoxelB)
                        : grid.VoxelCoord(portal.VoxelA);

                    float score = math.length(grid.VoxelToWorld(candidateEntry) - cursorWorld)
                        + math.length(goalWorld - grid.VoxelToWorld(candidateExit));
                    if (score < bestPortalScore)
                    {
                        bestPortalScore = score;
                        entry = candidateEntry;
                        exit = candidateExit;
                    }
                }

                int segment = RunSegment(ref ctx, cursor, entry, nodeA,
                    ctx.SegmentWaypoints, ctx.SegmentCapacity);
                if (segment < 0)
                {
                    return -1; // LastStatus 已由 RunSegment 细分
                }
                if (written + segment > capacity)
                {
                    LastStatus = PathStatus.CapacityExceeded;
                    return -1;
                }
                for (int s = 0; s < segment; s++)
                    waypoints[written + s] = ctx.SegmentWaypoints[s];
                written += segment;
                cursor = exit;
            }

            int tail = RunSegment(ref ctx, cursor, goal, goalNode,
                ctx.SegmentWaypoints, ctx.SegmentCapacity);
            if (tail < 0)
            {
                return -1;
            }
            if (written + tail > capacity)
            {
                LastStatus = PathStatus.CapacityExceeded;
                return -1;
            }
            for (int s = 0; s < tail; s++)
                waypoints[written + s] = ctx.SegmentWaypoints[s];
            written += tail;

            LastStatus = PathStatus.Success;
            return written;
        }

        private static int RunSegment(ref Context ctx, int3 from, int3 to, int node,
            int3* output, int capacity)
        {
            var astar = ctx.AStar;
            astar.RestrictNode = node;
            astar.Goal = to;
            ctx.AStar = astar;

            long voxelCount = ctx.Grid.VoxelCount;
            NavAStar.Reset(ref ctx.AStar, voxelCount);
            DebugLastSegmentNode = node;
            DebugLastSegmentFrom = from;
            DebugLastSegmentTo = to;

            if (!NavAStar.Begin(ref ctx.AStar, from))
            {
                LastStatus = PathStatus.SegmentBeginFailed;
                return -1;
            }
            if (!NavAStar.RunToCompletion(ref ctx.AStar))
            {
                LastStatus = PathStatus.SegmentSearchFailed;
                return -1;
            }
            int extracted = NavAStar.ExtractPath(ref ctx.AStar, from, to, output, capacity);
            if (extracted < 0)
            {
                LastStatus = PathStatus.CapacityExceeded;
            }
            return extracted;
        }

        private static int SearchClusters(ref Context ctx, int startNode, int goalNode)
        {
            for (int i = 0; i < ctx.NodeCount; i++)
            {
                ctx.ClusterG[i] = float.MaxValue;
                ctx.ClusterParent[i] = -1;
            }
            int heapCount = 0;

            ctx.ClusterG[startNode] = 0f;
            if (!NavFlowFieldHeap.Push(ctx.ClusterHeapF, ctx.ClusterHeapV, ctx.ClusterHeapCapacity,
                    ref heapCount, 0f, startNode))
                return -1;

            while (heapCount > 0)
            {
                if (!NavFlowFieldHeap.Pop(ctx.ClusterHeapF, ctx.ClusterHeapV,
                        ref heapCount, out float cost, out int node))
                    return -1;
                if (node == goalNode) break;

                // 扩展邻边（Edges 线性扫描；簇图小，成本可忽略）。
                for (int e = 0; e < ctx.EdgeCount; e++)
                {
                    ref readonly NavClusterEdge edge = ref ctx.Edges[e];
                    int other = -1;
                    if (edge.ClusterA == node) other = edge.ClusterB;
                    else if (edge.ClusterB == node) other = edge.ClusterA;
                    if (other < 0) continue;

                    float step = EdgeStepCost(ref ctx, edge);
                    float candidate = cost + step;
                    if (candidate < ctx.ClusterG[other])
                    {
                        ctx.ClusterG[other] = candidate;
                        ctx.ClusterParent[other] = node;
                        if (!NavFlowFieldHeap.Push(ctx.ClusterHeapF, ctx.ClusterHeapV,
                                ctx.ClusterHeapCapacity, ref heapCount, candidate, other))
                            return -1;
                    }
                }
            }

            if (ctx.ClusterParent[goalNode] == -1 && startNode != goalNode) return -1;

            // 回溯簇序列。
            int count = 0;
            int current = goalNode;
            while (current != -1)
            {
                if (count >= ctx.ClusterPathCapacity) return -1;
                ctx.ClusterPath[count++] = current;
                if (current == startNode) break;
                current = ctx.ClusterParent[current];
            }
            if (count == 0 || ctx.ClusterPath[count - 1] != startNode) return -1;

            for (int i = 0; i < count / 2; i++)
            {
                (ctx.ClusterPath[i], ctx.ClusterPath[count - 1 - i]) =
                    (ctx.ClusterPath[count - 1 - i], ctx.ClusterPath[i]);
            }
            return count;
        }

        private static float EdgeStepCost(ref Context ctx, in NavClusterEdge edge)
        {
            ref NavPortal portal = ref ctx.EdgePortals[edge.PortalStart];
            float3 a = ctx.Grid.VoxelToWorld(ctx.Grid.VoxelCoord(portal.VoxelA));
            float3 b = ctx.Grid.VoxelToWorld(ctx.Grid.VoxelCoord(portal.VoxelB));
            return math.length(a - b);
        }

        private static bool TryFindEdge(ref Context ctx, int nodeA, int nodeB, out NavClusterEdge edge)
        {
            for (int e = 0; e < ctx.EdgeCount; e++)
            {
                if ((ctx.Edges[e].ClusterA == nodeA && ctx.Edges[e].ClusterB == nodeB)
                    || (ctx.Edges[e].ClusterA == nodeB && ctx.Edges[e].ClusterB == nodeA))
                {
                    edge = ctx.Edges[e];
                    return true;
                }
            }
            edge = default;
            return false;
        }
    }
}
