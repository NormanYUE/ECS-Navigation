using Ember.Core;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 路径跟随系统：把 <see cref="NavPathState"/> 的航点推进成
    /// <see cref="NavDesiredVelocity"/>。只写期望速度，不做避障 ——
    /// 避障是下游 <c>NavAgentSystem</c> 的职责，两者之间只传这一量。
    ///
    /// 串行外壳（收指针、推进航点下标、写回组件）+ 并行 Job（逐代理求解），
    /// 模式同 Ember.Collision 管线；自调度 Job 在 OnTick 内 Complete，
    /// 满足框架「不允许跨 tick 挂起 Job」的硬约束。
    ///
    /// 注册在固定步长 ticker 上：ORCA 的时间视界预测要求稳定步长。
    /// </summary>
    public sealed class NavSteeringSystem : SystemBase
    {
        private readonly EntityQuery m_Query = new(
            new ComponentMask()
                .With<LocalTransform>()
                .With<NavAgent>()
                .With<NavPathState>()
                .With<NavDesiredVelocity>(),
            ComponentMask.Empty,
            new ComponentMask().With<Static>().With<Disabled>());

        /// <summary>稠密代理数组（跨帧复用，增长才分配）。</summary>
        private NativeList<NavSteeringAgent> m_Agents;

        protected override void DeclareAccess(AccessBuilder access) => access
            .Read<LocalTransform>()
            .Read<NavAgent>()
            .Write<NavPathState>()
            .Write<NavDesiredVelocity>()
            .Read<NavConfig>()
            .Read<Static>()
            .Read<Disabled>();

        public override void OnDestroy()
        {
            if (m_Agents.IsCreated) m_Agents.Dispose();
            base.OnDestroy();
        }

        protected override void OnTick(SystemContext ctx)
        {
            World world = ctx.World;
            if (!world.TryGetSingleton<NavConfig>(out Entity configOwner)) return;
            NavConfig config = world.GetComponent<NavConfig>(configOwner);

            ReadOnlyChunkList chunks = world.CompileQuery(m_Query).GetChunks();
            int agentCount = CountTrackedAgents(chunks);
            if (agentCount <= 0) return;

            EnsureCapacity(agentCount);
            m_Agents.Resize(agentCount, NativeArrayOptions.ClearMemory);
            if (!FillAgents(world, chunks, config)) return;

            var job = new NavSteeringJob { Agents = m_Agents.AsArray() };
            job.Schedule(m_Agents.Length, 64, default).Complete();

            WriteBack(chunks);
        }

        /// <summary>第一遍：数出带路径的代理数。</summary>
        private static int CountTrackedAgents(ReadOnlyChunkList chunks)
        {
            int total = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];
                if (chunk.Count <= 0) continue;

                var paths = chunk.GetColumn<NavPathState>();
                for (int row = 0; row < chunk.Count; row++)
                    if (paths.At(row).WaypointCount > 0) total++;
            }

            return total;
        }

        /// <summary>第二遍：把带路径的代理收进稠密数组，并取航点基址。</summary>
        private unsafe bool FillAgents(World world, ReadOnlyChunkList chunks, in NavConfig config)
        {
            int cursor = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];
                if (chunk.Count <= 0) continue;

                var transforms = chunk.GetColumn<LocalTransform>();
                var agents = chunk.GetColumn<NavAgent>();
                var paths = chunk.GetColumn<NavPathState>();

                for (int row = 0; row < chunk.Count; row++)
                {
                    NavPathState path = paths.At(row);
                    if (path.WaypointCount <= 0) continue;

                    NavAgent agent = agents.At(row);
                    m_Agents[cursor++] = new NavSteeringAgent
                    {
                        WaypointPtr = (long)world.GetBuffer<float3>(path.Waypoints).UnsafePtr,
                        WaypointCount = path.WaypointCount,
                        CurrentIndex = path.CurrentIndex,
                        Position = transforms.At(row).Position,
                        Speed = agent.MaxSpeed,
                        ArriveRadius = config.ArriveRadius,
                        LookAheadDistance = config.LookAheadDistance,
                    };
                }
            }

            return cursor > 0;
        }

        /// <summary>第三遍：按同一遍历顺序把推进结果写回组件。</summary>
        private void WriteBack(ReadOnlyChunkList chunks)
        {
            int cursor = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                Chunk chunk = chunks[i];
                if (chunk.Count <= 0) continue;

                var desired = chunk.GetColumn<NavDesiredVelocity>();
                var paths = chunk.GetColumn<NavPathState>();

                for (int row = 0; row < chunk.Count; row++)
                {
                    NavPathState path = paths.At(row);
                    if (path.WaypointCount <= 0) continue;

                    NavSteeringAgent agent = m_Agents[cursor++];
                    desired.At(row) = new NavDesiredVelocity(agent.DesiredVelocity);

                    path.CurrentIndex = agent.CurrentIndex;
                    paths.At(row) = path;
                }
            }
        }

        private void EnsureCapacity(int required)
        {
            if (!m_Agents.IsCreated)
                m_Agents = new NativeList<NavSteeringAgent>(required, Allocator.Persistent);
            else if (m_Agents.Capacity < required)
                m_Agents.Capacity = math.max(m_Agents.Capacity * 2, required);
        }
    }
}
