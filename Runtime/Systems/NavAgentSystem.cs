using Ember.Collision;
using Ember.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// ORCA 避障系统：邻居查询 + 约束构建 + 线性规划，输出写回
    /// <see cref="LinearVelocity"/>。路径跟随只给期望速度，本系统决定实际速度。
    ///
    /// 串行外壳负责：收拢稠密快照 → 建均匀网格 → 调度并行求解 → 写回列；
    /// 自调度 Job 在 OnTick 内 Complete，满足框架「不允许跨 tick 挂起 Job」的硬约束。
    ///
    /// 注册在固定步长 ticker 上：ORCA 的时间视界预测要求稳定步长。
    /// 工作区用 <c>Allocator.Persistent</c> 的 <c>NativeArray</c>：
    /// 只在代理数增长时重分配，稳态零分配。
    /// </summary>
    public sealed class NavAgentSystem : SystemBase
    {
        private const int BlockSize = 64;

        // 查询在 OnCreate 构造，不用字段初始化器：系统由 SystemTicker.Register 立即构造，
        // 早于 ECSManager.Start()、早于 World 构造，而 ComponentMask.With<T>() 会当场读组件注册表。
        private EntityQuery m_Query;

        public override void OnCreate()
        {
            m_Query = new EntityQuery(
                new ComponentMask()
                    .With<LocalTransform>()
                    .With<LinearVelocity>()
                    .With<NavAgent>()
                    .With<NavDesiredVelocity>(),
                ComponentMask.Empty,
                new ComponentMask().With<Static>().With<Disabled>());
        }

        // ---- 代理快照 ----
        private NativeArray<float3> m_Positions;
        private NativeArray<float3> m_Velocities;
        private NativeArray<float3> m_Preferred;
        private NativeArray<float3> m_NewVelocities;
        private NativeArray<float> m_Radii;
        private NativeArray<float> m_MaxSpeeds;
        private NativeArray<float> m_NeighborDists;
        private NativeArray<byte> m_Modes;
        private NativeArray<int> m_NeighborCounts;

        // ---- 均匀网格 ----
        private NativeArray<int> m_CellIndices;
        private NativeArray<int> m_CellCounts;
        private NativeArray<int> m_CellStarts;
        private NativeArray<int> m_CellCursors;
        private NativeArray<int> m_BlockTotals;
        private NativeArray<int> m_SortedAgents;

        // ---- 逐代理工作区 ----
        private NativeArray<int> m_NeighborIndices;
        private NativeArray<float> m_NeighborDistances;
        private NativeArray<NavOrcaLine> m_Lines;
        private NativeArray<NavOrcaLine> m_Scratch;

        private int m_AgentCapacity;
        private int m_MaxNeighbors;

        protected override void DeclareAccess(AccessBuilder access) => access
            .Read<LocalTransform>()
            .Write<LinearVelocity>()
            .Read<NavAgent>()
            .Read<NavDesiredVelocity>()
            .Read<NavConfig>()
            .Read<NavWorld>()
            .Read<Static>()
            .Read<Disabled>();

        public override void OnDestroy()
        {
            Dispose(ref m_Positions);
            Dispose(ref m_Velocities);
            Dispose(ref m_Preferred);
            Dispose(ref m_NewVelocities);
            Dispose(ref m_Radii);
            Dispose(ref m_MaxSpeeds);
            Dispose(ref m_NeighborDists);
            Dispose(ref m_Modes);
            Dispose(ref m_NeighborCounts);
            Dispose(ref m_CellIndices);
            Dispose(ref m_CellCounts);
            Dispose(ref m_CellStarts);
            Dispose(ref m_CellCursors);
            Dispose(ref m_BlockTotals);
            Dispose(ref m_SortedAgents);
            Dispose(ref m_NeighborIndices);
            Dispose(ref m_NeighborDistances);
            Dispose(ref m_Lines);
            Dispose(ref m_Scratch);
            base.OnDestroy();
        }

        protected override void OnTick(SystemContext ctx)
        {
            World world = ctx.World;
            if (!world.TryGetSingleton<NavConfig>(out Entity configOwner)) return;
            NavConfig config = world.GetComponent<NavConfig>(configOwner);

            ReadOnlyChunkList chunks = world.CompileQuery(m_Query).GetChunks();
            int agentCount = CountAgents(chunks);
            if (agentCount <= 0) return;

            EnsureCapacity(agentCount, config);
            Gather(world, chunks, config);

            var job = new NavAgentJob
            {
                Positions = m_Positions,
                Velocities = m_Velocities,
                Preferred = m_Preferred,
                Radii = m_Radii,
                MaxSpeeds = m_MaxSpeeds,
                NeighborDists = m_NeighborDists,
                Modes = m_Modes,
                NewVelocities = m_NewVelocities,
                NeighborCounts = m_NeighborCounts,
                GridSize = m_GridSize,
                GridOrigin = m_GridOrigin,
                CellSize = m_CellSize,
                MaxRadius = m_MaxRadius,
                CellStarts = m_CellStarts,
                CellCounts = m_CellCounts,
                SortedAgents = m_SortedAgents,
                NeighborIndices = m_NeighborIndices,
                NeighborDistances = m_NeighborDistances,
                Lines = m_Lines,
                Scratch = m_Scratch,
                MaxNeighbors = m_MaxNeighbors,
                MaxLines = m_MaxNeighbors + 1,
                TimeHorizon = config.TimeHorizon,
                TimeHorizonObst = config.TimeHorizonObst,
                TimeStep = ctx.DeltaTime,
                Dimension = config.Dimension,
                DistanceFieldPtr = m_DistanceFieldPtr,
                ObstacleGrid = m_ObstacleGrid,
                DistanceBits = m_DistanceBits,
                MaxBakeRadius = m_MaxBakeRadius,
            };
            job.Schedule(agentCount, BlockSize, default).Complete();

            WriteBack(chunks);
        }

        // ---- 串行外壳 ----

        private static int CountAgents(ReadOnlyChunkList chunks)
        {
            int total = 0;
            for (int i = 0; i < chunks.Count; i++) total += chunks[i].Count;
            return total;
        }

        private void Gather(World world, ReadOnlyChunkList chunks, in NavConfig config)
        {
            int cursor = 0;
            float maxRadius = 0f;

            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];
                if (chunk.Count <= 0) continue;

                var transforms = chunk.GetColumn<LocalTransform>();
                var velocities = chunk.GetColumn<LinearVelocity>();
                var agents = chunk.GetColumn<NavAgent>();
                var desired = chunk.GetColumn<NavDesiredVelocity>();

                for (int row = 0; row < chunk.Count; row++)
                {
                    NavAgent agent = agents.At(row);
                    m_Positions[cursor] = transforms.At(row).Position;
                    m_Velocities[cursor] = velocities.At(row).Value;
                    m_Preferred[cursor] = desired.At(row).Value;
                    m_Radii[cursor] = agent.Radius;
                    m_MaxSpeeds[cursor] = agent.MaxSpeed;
                    m_NeighborDists[cursor] = agent.NeighborDist;
                    m_Modes[cursor] = (byte)agent.Mode;
                    cursor++;

                    maxRadius = math.max(maxRadius, agent.Radius);
                }
            }

            BuildNeighborGrid(cursor, maxRadius, config);
            LoadObstacleField(world);
        }

        private unsafe void BuildNeighborGrid(int agentCount, float maxRadius, in NavConfig config)
        {
            float3 min = new(float.MaxValue);
            float3 max = new(float.MinValue);
            for (int i = 0; i < agentCount; i++)
            {
                min = math.min(min, m_Positions[i]);
                max = math.max(max, m_Positions[i]);
            }

            m_CellSize = config.NeighborCellSize > 0f
                ? config.NeighborCellSize
                : NavNeighborGrid.SuggestCellSize(maxRadius);
            m_MaxRadius = maxRadius;
            m_GridOrigin = min;
            m_GridSize = NavNeighborGrid.ResolveGridSize(min, max, m_CellSize, config.Dimension);

            int cellCount = NavNeighborGrid.CellCount(m_GridSize);
            EnsureGridCapacity(cellCount, agentCount);

            NavNeighborGrid.Build(m_GridSize, m_GridOrigin, m_CellSize, config.Dimension,
                (float3*)m_Positions.GetUnsafePtr(),
                agentCount,
                (int*)m_CellIndices.GetUnsafePtr(),
                (int*)m_CellCounts.GetUnsafePtr(),
                (int*)m_CellStarts.GetUnsafePtr(),
                (int*)m_CellCursors.GetUnsafePtr(),
                (int*)m_BlockTotals.GetUnsafePtr(),
                BlockSize,
                (int*)m_SortedAgents.GetUnsafePtr());
        }

        /// <summary>
        /// 取当前帧的距离场给 Job 采样。未烘焙时置空数组 —— Job 侧采样直接返回 false，
        /// 静态障碍约束整体跳过（可能跨帧挂起的 NativeArray 视图不会被缓存）。
        /// </summary>
        private unsafe void LoadObstacleField(World world)
        {
            m_ObstacleGrid = default;
            m_DistanceBits = 8;
            m_MaxBakeRadius = 1f;
            m_DistanceFieldPtr = 0L;

            if (!world.TryGetNavWorld(out NavWorldView view) || !view.IsReady) return;

            NavWorld state = view.StateSnapshot;
            m_ObstacleGrid = view.Grid;
            m_DistanceBits = state.DistanceBits;
            m_MaxBakeRadius = math.max(state.MaxBakeRadius, 1e-6f);
            m_DistanceFieldPtr = state.DistanceBits == 16
                ? (long)world.GetBuffer<ushort>(state.Distance).UnsafePtr
                : (long)world.GetBuffer<byte>(state.Distance).UnsafePtr;
        }

        private void WriteBack(ReadOnlyChunkList chunks)
        {
            int cursor = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];
                if (chunk.Count <= 0) continue;

                var velocities = chunk.GetColumn<LinearVelocity>();
                for (int row = 0; row < chunk.Count; row++)
                    velocities.At(row) = new LinearVelocity(m_NewVelocities[cursor++]);
            }
        }

        // ---- 容量 ----

        private void EnsureCapacity(int agentCount, in NavConfig config)
        {
            if (agentCount > m_AgentCapacity)
            {
                m_AgentCapacity = math.max(agentCount, math.max(m_AgentCapacity * 2, 64));
                Reallocate(ref m_Positions, m_AgentCapacity);
                Reallocate(ref m_Velocities, m_AgentCapacity);
                Reallocate(ref m_Preferred, m_AgentCapacity);
                Reallocate(ref m_NewVelocities, m_AgentCapacity);
                Reallocate(ref m_Radii, m_AgentCapacity);
                Reallocate(ref m_MaxSpeeds, m_AgentCapacity);
                Reallocate(ref m_NeighborDists, m_AgentCapacity);
                Reallocate(ref m_Modes, m_AgentCapacity);
                Reallocate(ref m_NeighborCounts, m_AgentCapacity);
                Reallocate(ref m_SortedAgents, m_AgentCapacity);
            }

            int maxNeighbors = math.max(1, config.MaxNeighbors);
            if (maxNeighbors == m_MaxNeighbors
                && m_Lines.IsCreated
                && m_Lines.Length >= m_AgentCapacity * (m_MaxNeighbors + 1))
                return;

            m_MaxNeighbors = maxNeighbors;
            Reallocate(ref m_NeighborIndices, m_AgentCapacity * m_MaxNeighbors);
            Reallocate(ref m_NeighborDistances, m_AgentCapacity * m_MaxNeighbors);
            Reallocate(ref m_Lines, m_AgentCapacity * (m_MaxNeighbors + 1));
            Reallocate(ref m_Scratch, m_AgentCapacity * (m_MaxNeighbors + 1));
        }

        private void EnsureGridCapacity(int cellCount, int agentCount)
        {
            if (m_CellCounts.IsCreated && m_CellCounts.Length >= cellCount) return;

            Reallocate(ref m_CellIndices, agentCount);
            Reallocate(ref m_CellCounts, cellCount);
            Reallocate(ref m_CellStarts, cellCount);
            Reallocate(ref m_CellCursors, cellCount);
            Reallocate(ref m_BlockTotals, (cellCount + BlockSize - 1) / BlockSize + 1);
        }

        // ---- 字段 ----

        private int3 m_GridSize;
        private float3 m_GridOrigin;
        private float m_CellSize = 1f;
        private float m_MaxRadius = 1f;

        private long m_DistanceFieldPtr;
        private NavGrid m_ObstacleGrid;
        private byte m_DistanceBits = 8;
        private float m_MaxBakeRadius = 1f;

        private static void Reallocate<T>(ref NativeArray<T> array, int length)
            where T : unmanaged
        {
            if (array.IsCreated) array.Dispose();
            array = new NativeArray<T>(length, Allocator.Persistent);
        }

        private static void Dispose<T>(ref NativeArray<T> array) where T : unmanaged
        {
            if (array.IsCreated) array.Dispose();
            array = default;
        }
    }
}
