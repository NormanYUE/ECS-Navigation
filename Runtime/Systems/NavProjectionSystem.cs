using Ember.Core;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 导航位置投影：把越出可行走区的代理拉回最近的可行走体素。
    ///
    /// <b>为什么需要</b>：ORCA 是软约束 —— 它求的是「尽量不撞」的速度，不保证结果落在
    /// 可行走区内。人挤人时编队外圈的代理会被持续挤压，加上路径跟随的横向修正，
    /// 代理会一点点蹭进墙边的间隔不足带、甚至墙里。这些代理随后会一直卡在那儿：
    /// 起点不可走时寻路直接失败（<c>NavAStar.Begin</c> 的可走性校验），而失败不会被重试。
    ///
    /// <b>为什么是硬投影而不是加权软约束</b>：软约束只在「代理还愿意往墙里走」时起作用，
    /// 而这里的情形是代理已经被别人推到了墙里 —— 需要的是一次确定性的纠正，不是再给一个力。
    ///
    /// <b>注册位置</b>：必须在<b>积分之后</b>（代理位置已更新）且在同一 tick 内。
    /// 包内不放进任何系统组 —— 各家的积分系统不同，由消费方在积分系统之后注册：
    /// <code>ticker.Register&lt;NavProjectionSystem&gt;();</code>
    ///
    /// 逐代理做常数次体素查询，串行即可：只有确实越界的代理才付搜索代价，
    /// 且需要读 World 托管侧缓冲（非 job-safe），与 <see cref="NavDynamicObstacleSystem"/> 同理。
    /// </summary>
    public sealed class NavProjectionSystem : SystemBase
    {
        /// <summary>搜索的最大体素环数（4 × 体素边长；0.5 体素时即 2 米）。</summary>
        public const int MaxSearchRings = 4;

        private EntityQuery m_Query;

        // 查询在 OnCreate 构造，不用字段初始化器：系统由 SystemTicker.Register 立即构造，
        // 早于 ECSManager.Start()、早于 World 构造，而 ComponentMask.With<T>() 会当场读组件注册表。
        public override void OnCreate()
        {
            m_Query = new EntityQuery(
                new ComponentMask().With<LocalTransform>().With<NavAgent>(),
                ComponentMask.Empty,
                new ComponentMask().With<Static>().With<Disabled>());
        }

        protected override void DeclareAccess(AccessBuilder access) => access
            .Read<NavAgent>()
            .Read<NavWorld>()
            .Read<Static>()
            .Read<Disabled>()
            .Write<LocalTransform>();

        protected override void OnTick(SystemContext ctx)
        {
            World world = ctx.World;
            if (!world.TryGetNavWorld(out NavWorldView view) || !view.IsReady) return;

            ReadOnlyChunkList chunks = world.CompileQuery(m_Query).GetChunks();
            if (chunks.Count <= 0) return;

            NavGrid grid = view.Grid;
            for (int c = 0; c < chunks.Count; c++)
            {
                Chunk chunk = chunks[c];
                ChunkColumn<LocalTransform> transforms = chunk.GetColumn<LocalTransform>();
                ChunkColumn<NavAgent> agents = chunk.GetColumn<NavAgent>();

                for (int row = 0; row < chunk.Count; row++)
                {
                    float3 position = transforms.At(row).Position;
                    float radius = agents.At(row).Radius;

                    int3 voxel = grid.WorldToVoxelOnGrid(position);
                    if (!grid.IsInside(voxel)) continue;
                    if (view.IsWalkable(voxel, radius)) continue;

                    if (!TryFindNearestWalkable(in view, grid, voxel, radius, out int3 target))
                        continue;

                    float3 projected = grid.VoxelToWorld(target);
                    transforms.At(row).Position = ProjectOntoGridAxes(position, projected, grid);
                }
            }
        }

        /// <summary>
        /// 逐环向外找第一个可走体素：位移最小，因此不会把代理甩到墙的另一侧
        /// （墙厚 1.5 米时，从墙内出发的最近可走点就在它进来的那一侧）。
        /// </summary>
        private static bool TryFindNearestWalkable(
            in NavWorldView view, in NavGrid grid, int3 origin, float radius, out int3 result)
        {
            result = origin;

            for (int ring = 0; ring <= MaxSearchRings; ring++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                for (int dy = -ring; dy <= ring; dy++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    // 只取本环的边界：环内已在更小的环查过。
                    if (math.max(math.abs(dx), math.max(math.abs(dy), math.abs(dz))) != ring) continue;

                    int3 candidate = origin + new int3(dx, dy, dz);
                    if (!grid.IsInside(candidate) || !view.IsWalkable(candidate, radius)) continue;

                    result = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 只改写网格有铺开的那两个轴，第三个轴保持原值。
        /// 二维网格（z 或 y 只有一层）里，第三个轴是网格自己挑的常数平面 ——
        /// 把它写进代理位置等于把代理挪到那个平面上（见 0.2.13 修的 z 漂移）。
        /// </summary>
        private static float3 ProjectOntoGridAxes(float3 position, float3 projected, in NavGrid grid)
        {
            int3 dims = grid.Dimensions;
            if (dims.z == 1) return new float3(projected.x, projected.y, position.z);
            if (dims.y == 1) return new float3(projected.x, position.y, projected.z);
            return projected;
        }
    }
}
