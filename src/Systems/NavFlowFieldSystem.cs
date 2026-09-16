using Ember.Core;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 流场缓存系统（串行外壳 + 分帧推进）：
    /// 维护「目标体素 → 多源 Dijkstra 距离场」的缓存池，供多代理共用同一目标时
    /// 省掉每个代理各跑一次搜索。
    ///
    /// <b>分帧</b>：每帧把 <see cref="NavConfig.FlowFieldPopBudget"/> 在所有未完成的场之间
    /// 均摊，单帧主线程耗时有上界；未完成的场在下一帧继续，梯度查询在完成前一律返回失败。
    ///
    /// <b>淘汰</b>：槽位按目标体素命中复用，新目标优先占空闲槽，无空闲则淘汰最久未命中者
    /// （LRU，依据 <see cref="NavFlowFieldSlot.LastUsedFrame"/>）。导航数据热替换后
    /// 代际不符的槽位立即回收。
    ///
    /// 场数据按槽位<b>按需分配</b>，不按缓存上限满额预分配。
    /// </summary>
    public sealed class NavFlowFieldSystem : SystemBase
    {
        private readonly EntityQuery m_Query = new(
            new ComponentMask().With<NavRequest>().With<LocalTransform>(),
            ComponentMask.Empty,
            new ComponentMask().With<Static>().With<Disabled>());

        private int m_Frame;

        protected override void DeclareAccess(AccessBuilder access) => access
            .Read<NavRequest>()
            .Read<LocalTransform>()
            .Read<NavConfig>()
            .Write<NavWorld>()
            .Read<Static>()
            .Read<Disabled>();

        protected override void OnTick(SystemContext ctx)
        {
            World world = ctx.World;
            if (!world.TryGetSingleton<NavConfig>(out Entity configOwner)) return;
            NavConfig config = world.GetComponent<NavConfig>(configOwner);

            if (!world.TryGetNavWorld(out NavWorldView view) || !view.IsReady) return;

            NavWorld state = view.StateSnapshot;
            if (state.Connectivity == 0 || state.DistanceBits != 8) return;

            view.ConfigureFlowFields(math.max(config.FlowFieldCacheSize, 0));
            unsafe
            {
                NavFlowFieldSlot* slots = view.FlowSlots();
                if (slots == null || state.FlowSlotCount <= 0) return;

                m_Frame++;
                NavGrid grid = view.Grid;

                TrackRequestedTargets(world, view, slots, state, grid);
                AdvancePendingFields(view, slots, state, config);
            }
        }

        /// <summary>把本帧请求涉及的目标体素映射到槽位，并刷新其 LRU 时间戳。</summary>
        private unsafe void TrackRequestedTargets(
            World world, in NavWorldView view, NavFlowFieldSlot* slots, in NavWorld state,
            in NavGrid grid)
        {
            ReadOnlyChunkList chunks = world.CompileQuery(m_Query).GetChunks();
            for (int c = 0; c < chunks.Count; c++)
            {
                Chunk chunk = chunks[c];
                if (chunk.Count <= 0) continue;

                var requests = chunk.GetColumn<NavRequest>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    NavRequestStatus status = requests.At(row).Status;
                    if (status != NavRequestStatus.InProgress && status != NavRequestStatus.Ready)
                        continue;

                    int3 target = grid.WorldToVoxelOnGrid(requests.At(row).Target);
                    int slot = FindOrAcquireSlot(view, slots, state, target);
                    if (slot >= 0) slots[slot].LastUsedFrame = m_Frame;
                }
            }
        }

        /// <summary>推进未完成的场；预算在它们之间均摊。</summary>
        private static unsafe void AdvancePendingFields(
            in NavWorldView view, NavFlowFieldSlot* slots, in NavWorld state, in NavConfig config)
        {
            int pending = 0;
            for (int i = 0; i < state.FlowSlotCount; i++)
            {
                if (slots[i].InUse == 0) continue;
                if (slots[i].Generation != state.Generation || slots[i].FieldEpoch != state.FieldEpoch)
                {
                    view.ReleaseFlowSlot(ref slots[i]);
                    continue;
                }
                if (slots[i].Complete == 0) pending++;
            }

            if (pending <= 0) return;

            long budget = math.max(1, config.FlowFieldPopBudget / pending);
            for (int i = 0; i < state.FlowSlotCount; i++)
            {
                if (slots[i].InUse == 0 || slots[i].Complete != 0) continue;
                view.StepFlowSlot(ref slots[i], budget);
            }
        }

        /// <summary>命中已有槽位；否则占空闲槽；再否则淘汰最久未命中者并重新播种。</summary>
        private static unsafe int FindOrAcquireSlot(
            in NavWorldView view, NavFlowFieldSlot* slots, in NavWorld state, int3 target)
        {
            int free = -1;
            int oldest = -1;
            int oldestFrame = int.MaxValue;

            for (int i = 0; i < state.FlowSlotCount; i++)
            {
                if (slots[i].InUse == 0)
                {
                    if (free < 0) free = i;
                    continue;
                }

                // 数据热替换：代际不符的槽位直接作废，当作空闲。
                if (slots[i].Generation != state.Generation)
                {
                    view.ReleaseFlowSlot(ref slots[i]);
                    if (free < 0) free = i;
                    continue;
                }

                if (slots[i].Target.Equals(target)) return i;
                if (slots[i].LastUsedFrame < oldestFrame)
                {
                    oldestFrame = slots[i].LastUsedFrame;
                    oldest = i;
                }
            }

            int slot = free >= 0 ? free : oldest;
            if (slot < 0) return -1;

            if (slots[slot].InUse != 0) view.ReleaseFlowSlot(ref slots[slot]);
            slots[slot].InUse = 1;
            slots[slot].LastUsedFrame = 0;
            view.SeedFlowSlot(ref slots[slot], target, state.Generation);
            return slot;
        }
    }
}
