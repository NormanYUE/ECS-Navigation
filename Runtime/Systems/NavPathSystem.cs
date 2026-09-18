using System.Diagnostics;
using Ember.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 寻路求解（串行外壳）：对 <see cref="NavRequestStatus.InProgress"/> 的请求跑
    /// 分层 A* + 拉绳，把世界空间航点写进代理自己的 World buffer。
    ///
    /// <b>双限预算的时间侧</b>在本系统：每处理一个请求前检查时间上限
    /// （<see cref="NavConfig.RequestBudgetMs"/>），超时即返回，剩余请求留到下一帧。
    /// 数量上限由上游 <c>NavRequestSystem</c> 把关。
    ///
    /// 逐请求<b>串行</b>求解，与数量上限的设定一致 —— 预算本就是为约束主线程耗时而生。
    /// 并行化（把每个请求的搜索丢进 Job）留到 P9，届时 <c>NavHpaPathfinder</c> 的
    /// 静态状态需要先搬进 <c>Context</c>。
    ///
    /// 路径句柄按实体存放在 <c>NativeParallelHashMap</c> 里：请求完成时复用旧 buffer
    /// （容量不足才 Resize），实体销毁时回收 —— 否则每次重寻路都会漏一个 buffer。
    ///
    /// 距离场目前按 8 位等级解释；16 位需要把求解器的等级宽度参数化（P9）。
    /// </summary>
    public sealed class NavPathSystem : SystemBase
    {
        /// <summary>时间预算换算：毫秒 → Stopwatch 计时单位。</summary>
        private static readonly long TicksPerMillisecond = Stopwatch.Frequency / 1000;

        // 查询在 OnCreate 构造，不用字段初始化器：系统由 SystemTicker.Register 立即构造，
        // 早于 ECSManager.Start()、早于 World 构造，而 ComponentMask.With<T>() 会当场读组件注册表。
        private EntityQuery m_Query;

        public override void OnCreate()
        {
            m_Query = new EntityQuery(
                new ComponentMask()
                    .With<NavRequest>()
                    .With<NavAgent>()
                    .With<LocalTransform>()
                    .With<NavPathState>(),
                ComponentMask.Empty,
                new ComponentMask().With<Static>().With<Disabled>());
        }

        private readonly NavPathWorkspace m_Workspace = new();
        private NativeParallelHashMap<Entity, BufferHandle> m_Paths;

        protected override void DeclareAccess(AccessBuilder access) => access
            .Write<NavRequest>()
            .Write<NavPathState>()
            .Read<NavAgent>()
            .Read<LocalTransform>()
            .Read<NavConfig>()
            .Read<NavWorld>()
            .Read<Static>()
            .Read<Disabled>();

        public override void OnDestroy()
        {
            m_Workspace.Dispose();
            if (m_Paths.IsCreated) m_Paths.Dispose();
            base.OnDestroy();
        }

        protected override void OnTick(SystemContext ctx)
        {
            World world = ctx.World;
            if (!world.TryGetSingleton<NavConfig>(out Entity configOwner)) return;
            NavConfig config = world.GetComponent<NavConfig>(configOwner);

            if (!world.TryGetNavWorld(out NavWorldView view) || !view.IsReady) return;

            NavWorld state = view.StateSnapshot;
            if (state.DistanceBits != 8 || state.Connectivity == 0) return;

            NavGrid grid = view.Grid;
            if (!m_Workspace.Matches(grid.VoxelCount, state.ClusterNodeCount))
                m_Workspace.Rebuild(grid.VoxelCount, state.ClusterNodeCount);

            SweepDeadPaths(world);

            ReadOnlyChunkList chunks = world.CompileQuery(m_Query).GetChunks();
            unsafe
            {
                NavHpaPathfinder.Context context = BuildContext(world, grid, state);
                byte* occupancy = (byte*)world.GetBuffer<byte>(state.Occupancy).UnsafePtr;
                byte* distanceLevels = (byte*)world.GetBuffer<byte>(state.Distance).UnsafePtr;

                long deadline = Stopwatch.GetTimestamp()
                    + (long)(math.max(config.RequestBudgetMs, 0f) * TicksPerMillisecond);

                for (int c = 0; c < chunks.Count; c++)
                {
                    Chunk chunk = chunks[c];
                    if (chunk.Count <= 0) continue;

                    var requests = chunk.GetColumn<NavRequest>();
                    var transforms = chunk.GetColumn<LocalTransform>();
                    var agents = chunk.GetColumn<NavAgent>();
                    var paths = chunk.GetColumn<NavPathState>();

                    for (int row = 0; row < chunk.Count; row++)
                    {
                        if (requests.At(row).Status != NavRequestStatus.InProgress) continue;
                        if (Stopwatch.GetTimestamp() >= deadline) return;

                        SolveOne(world, view, grid, state, ref context, occupancy, distanceLevels,
                            chunk, requests, transforms, agents, paths, row);
                    }
                }
            }
        }

        /// <summary>单个请求：HPA* → 拉绳 → 写航点 buffer → 推进状态。</summary>
        private unsafe void SolveOne(
            World world,
            in NavWorldView view,
            in NavGrid grid,
            in NavWorld state,
            ref NavHpaPathfinder.Context context,
            byte* occupancy,
            byte* distanceLevels,
            in Chunk chunk,
            ChunkColumn<NavRequest> requests,
            ChunkColumn<LocalTransform> transforms,
            ChunkColumn<NavAgent> agents,
            ChunkColumn<NavPathState> paths,
            int row)
        {
            int3 start = grid.WorldToVoxelOnGrid(transforms.At(row).Position);
            int3 goal = grid.WorldToVoxelOnGrid(requests.At(row).Target);
            int requiredLevel = RequiredLevel(state, agents.At(row).Radius);
            context.RequiredLevel = requiredLevel;
            context.AStar.RequiredLevel = requiredLevel;
            context.AStar.Goal = goal;

            // 目标已有完成的流场时改走梯度下降：多代理共目标只需一次 Dijkstra，
            // 每代理只剩沿路径走的开销。路径不如 A* 短，但接着要拉绳平滑。
            int raw = -1;
            NavFlowFieldSlot* flowSlots = view.FlowSlots();
            int flowSlot = flowSlots == null ? -1 : FindCompletedFlowSlot(flowSlots, state, goal);
            if (flowSlot >= 0
                && view.TryExtractFlowPath(ref flowSlots[flowSlot], start, goal,
                    (int3*)m_Workspace.RawWaypoints.GetUnsafePtr(),
                    m_Workspace.RawWaypoints.Length, out int flowCount))
            {
                raw = flowCount;
            }

            if (raw <= 0)
                raw = NavHpaPathfinder.FindPath(ref context, start, goal,
                    (int3*)m_Workspace.RawWaypoints.GetUnsafePtr(), m_Workspace.RawWaypoints.Length);

            // HPA* 失败不等于无解：簇图的门户是烘焙期按无边界的可走性建的，
            // 按代理半径过滤后可能过不去，而全图仍有路。回退一次全图搜索补完备性。
            if (raw <= 0)
                raw = NavHpaPathfinder.FindPathExhaustive(ref context, start, goal,
                    (int3*)m_Workspace.RawWaypoints.GetUnsafePtr(), m_Workspace.RawWaypoints.Length);

            int smoothed = raw > 0
                ? NavPathSmoother.PullString(in grid, occupancy, distanceLevels, requiredLevel,
                    (int3*)m_Workspace.RawWaypoints.GetUnsafePtr(), raw,
                    (int3*)m_Workspace.SmoothWaypoints.GetUnsafePtr(), m_Workspace.SmoothWaypoints.Length)
                : -1;

            if (smoothed > 0)
            {
                BufferHandle handle = AcquirePathBuffer(world, chunk.GetEntity(row), smoothed);
                BufferSpan<float3> waypoints = world.GetBuffer<float3>(handle);
                for (int i = 0; i < smoothed; i++)
                    waypoints[i] = grid.VoxelToWorld(m_Workspace.SmoothWaypoints[i]);

                NavPathState pathState = paths.At(row);
                pathState.Waypoints = handle;
                pathState.WaypointCount = smoothed;
                pathState.CurrentIndex = 0;
                pathState.Generation = state.Generation;
                paths.At(row) = pathState;
            }

            NavRequest request = requests.At(row);
            request.Status = smoothed > 0 ? NavRequestStatus.Ready : NavRequestStatus.Failed;
            requests.At(row) = request;
        }

        /// <summary>找到与目标体素匹配、且波前已耗尽的流场槽位；没有返回 -1。</summary>
        private static unsafe int FindCompletedFlowSlot(
            NavFlowFieldSlot* slots, in NavWorld state, int3 goal)
        {
            for (int i = 0; i < state.FlowSlotCount; i++)
            {
                if (slots[i].InUse == 0 || slots[i].Complete == 0) continue;
                if (slots[i].Generation != state.Generation) continue;
                if (slots[i].Target.Equals(goal)) return i;
            }

            return -1;
        }

        /// <summary>把代理半径换算成距离场的量化等级（与 <c>NavWorldView.IsWalkable</c> 同口径）。</summary>
        private static int RequiredLevel(in NavWorld state, float radius)
        {
            float maxRadius = math.max(state.MaxBakeRadius, 1e-6f);
            int levels = state.DistanceBits == 16 ? 65535 : 255;
            return (int)math.round(math.clamp(radius, 0f, maxRadius) / maxRadius * levels);
        }

        /// <summary>取代理的航点 buffer；已有则复用并按需扩容。</summary>
        private BufferHandle AcquirePathBuffer(World world, Entity entity, int required)
        {
            if (!m_Paths.IsCreated)
                m_Paths = new NativeParallelHashMap<Entity, BufferHandle>(64, Allocator.Persistent);

            if (m_Paths.TryGetValue(entity, out BufferHandle existing))
            {
                if (world.GetBufferLength<float3>(existing) < required)
                    world.ResizeBuffer<float3>(existing, required);
                return existing;
            }

            // CreateSizedBuffer 建出来即有长度；与上面「复用」分支的 Resize 语义一致。
            BufferHandle created = world.CreateSizedBuffer<float3>(math.max(required, 8));
            m_Paths.Add(entity, created);
            return created;
        }

        /// <summary>回收已销毁实体的航点 buffer。</summary>
        private void SweepDeadPaths(World world)
        {
            if (!m_Paths.IsCreated || m_Paths.IsEmpty) return;

            var pairs = m_Paths.GetKeyValueArrays(Allocator.Temp);
            for (int i = 0; i < pairs.Keys.Length; i++)
            {
                Entity entity = pairs.Keys[i];
                if (world.Exists(entity)) continue;

                BufferHandle handle = pairs.Values[i];
                if (!handle.IsNull) world.DestroyBuffer<float3>(handle);
                m_Paths.Remove(entity);
            }

            pairs.Dispose();
        }

        private unsafe NavHpaPathfinder.Context BuildContext(World world, in NavGrid grid, in NavWorld state) => new()
        {
            Grid = grid,
            Connectivity = state.Connectivity,
            Occupancy = (byte*)world.GetBuffer<byte>(state.Occupancy).UnsafePtr,
            DistanceLevels = (byte*)world.GetBuffer<byte>(state.Distance).UnsafePtr,
            Costs = (byte*)world.GetBuffer<byte>(state.Cost).UnsafePtr,
            RequiredLevel = 0,
            Nodes = (NavClusterNode*)world.GetBuffer<NavClusterNode>(state.ClusterNodes).UnsafePtr,
            NodeCount = state.ClusterNodeCount,
            Edges = (NavClusterEdge*)world.GetBuffer<NavClusterEdge>(state.ClusterEdges).UnsafePtr,
            EdgeCount = state.ClusterEdgeCount,
            EdgePortals = (NavPortal*)world.GetBuffer<NavPortal>(state.EdgePortals).UnsafePtr,
            VoxelNodes = (int*)world.GetBuffer<int>(state.VoxelNodes).UnsafePtr,
            ClusterG = (float*)m_Workspace.ClusterG.GetUnsafePtr(),
            ClusterParent = (int*)m_Workspace.ClusterParent.GetUnsafePtr(),
            ClusterHeapF = (float*)m_Workspace.ClusterHeapF.GetUnsafePtr(),
            ClusterHeapV = (int*)m_Workspace.ClusterHeapVoxels.GetUnsafePtr(),
            ClusterHeapCapacity = m_Workspace.ClusterHeapF.Length,
            ClusterPath = (int*)m_Workspace.ClusterPath.GetUnsafePtr(),
            ClusterPathCapacity = m_Workspace.ClusterPath.Length,
            AStar = new NavAStar.Context
            {
                Grid = grid,
                Connectivity = state.Connectivity,
                Occupancy = (byte*)world.GetBuffer<byte>(state.Occupancy).UnsafePtr,
                DistanceLevels = (byte*)world.GetBuffer<byte>(state.Distance).UnsafePtr,
                Costs = (byte*)world.GetBuffer<byte>(state.Cost).UnsafePtr,
                G = (float*)m_Workspace.AStarG.GetUnsafePtr(),
                Parent = (int*)m_Workspace.AStarParent.GetUnsafePtr(),
                VoxelNodes = (int*)world.GetBuffer<int>(state.VoxelNodes).UnsafePtr,
                HeapF = (float*)m_Workspace.AStarHeapF.GetUnsafePtr(),
                HeapVoxels = (int*)m_Workspace.AStarHeapVoxels.GetUnsafePtr(),
                HeapCapacity = m_Workspace.AStarHeapF.Length,
                HeapCount = 0,
                RequiredLevel = 0,
                Goal = default,
                RestrictNode = -1,
            },
            SegmentWaypoints = (int3*)m_Workspace.SegmentWaypoints.GetUnsafePtr(),
            SegmentCapacity = m_Workspace.SegmentWaypoints.Length,
        };
    }
}
