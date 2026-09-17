using Ember.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 寻路请求调度（串行）：把 <see cref="NavRequestStatus.Pending"/> 的请求按优先级
    /// 挑出本帧配额，置为 <see cref="NavRequestStatus.InProgress"/>，
    /// 交给下游 <c>NavPathSystem</c> 实际搜索。
    ///
    /// <b>双限预算</b>在本模块拆成两半，二者先到为准：
    /// 本系统管<b>数量上限</b>（<see cref="NavConfig.RequestBudgetPerFrame"/>），
    /// <c>NavPathSystem</c> 管<b>时间上限</b>（<see cref="NavConfig.RequestBudgetMs"/>）。
    /// 数量上限只约束「本帧新开工多少」，时间上限约束「本帧实际算多久」，
    /// 剩下的留在 InProgress 由后续帧继续消化。
    ///
    /// 优先级用计数排序（<see cref="NavAgent.Priority"/> 是 byte）：
    /// 稳定且 O(n)，同优先级按遍历序，结果确定。
    ///
    /// <b>即时失败</b>：目标与自身不在同一连通区域且不存在 off-mesh link 时直接判失败，
    /// 不进搜索 —— 无解请求不该占用预算。
    /// </summary>
    public sealed class NavRequestSystem : SystemBase
    {
        // 查询在 OnCreate 构造，不用字段初始化器：系统由 SystemTicker.Register 立即构造，
        // 早于 ECSManager.Start()、早于 World 构造，而 ComponentMask.With<T>() 会当场读组件注册表。
        private EntityQuery m_Query;

        public override void OnCreate()
        {
            m_Query = new EntityQuery(
                new ComponentMask().With<NavRequest>().With<NavAgent>().With<LocalTransform>(),
                ComponentMask.Empty,
                new ComponentMask().With<Static>().With<Disabled>());
        }

        /// <summary>按优先级倒序排列的请求下标（引用稠密收集序）。</summary>
        private NativeList<int> m_Order;
        private NativeList<int> m_RequestIndices;
        private NativeArray<int> m_PriorityCounts;

        protected override void DeclareAccess(AccessBuilder access) => access
            .Write<NavRequest>()
            .Read<NavAgent>()
            .Read<LocalTransform>()
            .Read<NavConfig>()
            .Read<NavWorld>()
            .Read<Static>()
            .Read<Disabled>();

        public override void OnDestroy()
        {
            if (m_Order.IsCreated) m_Order.Dispose();
            if (m_RequestIndices.IsCreated) m_RequestIndices.Dispose();
            if (m_PriorityCounts.IsCreated) m_PriorityCounts.Dispose();
            base.OnDestroy();
        }

        protected override void OnTick(SystemContext ctx)
        {
            World world = ctx.World;
            if (!world.TryGetSingleton<NavConfig>(out Entity configOwner)) return;
            NavConfig config = world.GetComponent<NavConfig>(configOwner);

            ReadOnlyChunkList chunks = world.CompileQuery(m_Query).GetChunks();
            int requestCount = GatherPending(chunks);
            if (requestCount <= 0) return;

            // 没有导航数据时只做即时失败判定，不进入搜索配额。
            bool hasNavData = world.TryGetNavWorld(out NavWorldView view) && view.IsReady;

            int budget = math.max(0, config.RequestBudgetPerFrame);
            int scheduled = 0;
            for (int i = 0; i < m_Order.Length && scheduled < budget; i++)
            {
                int index = m_Order[i];
                if (!hasNavData || IsObviouslyUnreachable(view, chunks, index))
                {
                    SetStatus(chunks, index, NavRequestStatus.Failed);
                    continue;
                }

                SetStatus(chunks, index, NavRequestStatus.InProgress);
                scheduled++;
            }

            // 剩余 Pending 原样留给后续帧；预算为 0 时不改动状态。
        }

        /// <summary>
        /// 第一遍：收集 Pending 请求，按优先级倒序排好（同优先级保持遍历序）。
        /// </summary>
        private int GatherPending(ReadOnlyChunkList chunks)
        {
            if (!m_RequestIndices.IsCreated)
                m_RequestIndices = new NativeList<int>(64, Allocator.Persistent);
            if (!m_Order.IsCreated)
                m_Order = new NativeList<int>(64, Allocator.Persistent);

            m_RequestIndices.Clear();
            m_Order.Clear();

            for (int c = 0; c < chunks.Count; c++)
            {
                Chunk chunk = chunks[c];
                if (chunk.Count <= 0) continue;

                var requests = chunk.GetColumn<NavRequest>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    if (requests.At(row).Status != NavRequestStatus.Pending) continue;
                    m_RequestIndices.Add(Encode(chunks, c, row));
                }
            }

            if (m_RequestIndices.Length <= 0) return 0;

            EnsurePriorityBuckets();
            return SortByPriority(chunks);
        }

        /// <summary>计数排序：优先级高者在前，同级保持收集顺序。</summary>
        private int SortByPriority(ReadOnlyChunkList chunks)
        {
            for (int i = 0; i < m_PriorityCounts.Length; i++) m_PriorityCounts[i] = 0;

            for (int i = 0; i < m_RequestIndices.Length; i++)
                m_PriorityCounts[GetPriority(chunks, m_RequestIndices[i])]++;

            // 从最高优先级向下累加起始偏移。
            int running = 0;
            for (int priority = m_PriorityCounts.Length - 1; priority >= 0; priority--)
            {
                int count = m_PriorityCounts[priority];
                m_PriorityCounts[priority] = running;
                running += count;
            }

            for (int i = 0; i < m_RequestIndices.Length; i++)
            {
                int index = m_RequestIndices[i];
                m_Order.Add(0);
                m_Order[m_PriorityCounts[GetPriority(chunks, index)]++] = index;
            }

            return m_Order.Length;
        }

        /// <summary>目标与自身不在同一连通区域、且没有 off-mesh link 可跨时，无解。</summary>
        private static bool IsObviouslyUnreachable(
            in NavWorldView view, ReadOnlyChunkList chunks, int index)
        {
            NavWorld state = view.StateSnapshot;
            if (state.LinkCount > 0) return false;

            NavGrid grid = view.Grid;
            int3 start = grid.WorldToVoxelOnGrid(GetPosition(chunks, index));
            int3 goal = grid.WorldToVoxelOnGrid(GetTarget(chunks, index));
            if (!grid.IsInside(start) || !grid.IsInside(goal)) return true;

            int startRegion = view.RegionAt(start);
            if (startRegion < 0) return true;

            return startRegion != view.RegionAt(goal);
        }

        // ---- 下标编解码：把 (Chunk, Row) 压进一个 int ----

        private static int Encode(ReadOnlyChunkList chunks, int chunkIndex, int row) =>
            (chunkIndex << 16) | (row & 0xFFFF);

        private static int ChunkOf(int index) => index >> 16;
        private static int RowOf(int index) => index & 0xFFFF;

        private static float3 GetTarget(ReadOnlyChunkList chunks, int index)
        {
            Chunk chunk = chunks[ChunkOf(index)];
            return chunk.GetColumn<NavRequest>().At(RowOf(index)).Target;
        }

        private static void SetStatus(ReadOnlyChunkList chunks, int index, NavRequestStatus status)
        {
            Chunk chunk = chunks[ChunkOf(index)];
            var requests = chunk.GetColumn<NavRequest>();
            NavRequest request = requests.At(RowOf(index));
            request.Status = status;
            requests.At(RowOf(index)) = request;
        }

        private static float3 GetPosition(ReadOnlyChunkList chunks, int index)
        {
            Chunk chunk = chunks[ChunkOf(index)];
            return chunk.GetColumn<LocalTransform>().At(RowOf(index)).Position;
        }

        private static int GetPriority(ReadOnlyChunkList chunks, int index)
        {
            Chunk chunk = chunks[ChunkOf(index)];
            return chunk.GetColumn<NavAgent>().At(RowOf(index)).Priority;
        }

        private void EnsurePriorityBuckets()
        {
            if (!m_PriorityCounts.IsCreated) m_PriorityCounts = new NativeArray<int>(256, Allocator.Persistent);
        }
    }
}
