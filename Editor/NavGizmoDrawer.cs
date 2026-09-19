using Ember;
using Ember.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 场景视图里的导航可视化：体素网格（可走绿色空心 / 不可走红色实心，可选按区域着色）、
    /// 代理（半径 + 当前速度 + ORCA 期望速度）、路径（航点连线 + 当前目标）。
    ///
    /// 体素数在大地图上是百万级，逐格画会拖垮编辑器 —— 按<b>目标绘制量</b>
    /// 反推步长，并在 <see cref="NavDebugWindow"/> 里给一个可调上限。
    /// 因此网格画出来是采样视图，用于确认整体形状与空洞，不是精确的逐格对照。
    /// 代理与路径数量有限，按条数上限截断，不采样。
    ///
    /// 只在 Play 模式有效：导航数据在运行中的 ECS 世界里。
    /// </summary>
    [InitializeOnLoad]
    public static class NavGizmoDrawer
    {
        /// <summary>可走：绿色空心（只描边、不填充），一眼看出通行走廊的边界。</summary>
        private static readonly Color WalkableOutline = new(0.25f, 1f, 0.35f, 1f);

        /// <summary>不可走：红色实心（填充 + 描边）。占据、距离不足、区域外一律归此类。</summary>
        private static readonly Color BlockedFill = new(0.85f, 0.12f, 0.12f, 0.45f);
        private static readonly Color BlockedOutline = new(1f, 0.25f, 0.2f, 1f);
        private static readonly Color IsolatedColor = new(0.55f, 0.55f, 0.55f, 0.30f);

        /// <summary>逐格填充用的四点缓冲。每次重绘会画上万格，不能每格 new 一个数组。</summary>
        private static readonly Vector3[] s_Quad = new Vector3[4];
        private static readonly Color RadiusColor = new(1f, 1f, 1f, 0.6f);
        private static readonly Color VelocityColor = new(0.2f, 0.6f, 1f, 1f);
        private static readonly Color DesiredColor = new(1f, 0.85f, 0.2f, 1f);
        private static readonly Color PathColor = new(0.2f, 1f, 0.9f, 1f);
        private static readonly Color TargetColor = new(1f, 0.35f, 0.35f, 1f);

        static NavGizmoDrawer()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        private static void OnSceneGui(SceneView view)
        {
            // 只在 Repaint 事件绘制：duringSceneGui 对 Layout/鼠标事件同样触发，
            // 此时发 GL（HandleCap 硬编码 Repaint 不区分事件）会污染 Scene 视图渲染（花屏）。
            if (Event.current.type != EventType.Repaint) return;
            if (!Application.isPlaying) return;
            if (!NavDebugSettings.DrawMesh && !NavDebugSettings.DrawAgents && !NavDebugSettings.DrawPaths) return;

            ECSManager manager = NavDebugSettings.ResolveManager();
            World world = manager?.World;
            if (world is not { IsDisposed: false }) return;
            if (!world.TryGetNavWorld(out NavWorldView nav) || !nav.IsReady) return;

            if (NavDebugSettings.DrawMesh || NavDebugSettings.DrawRegions) {
                DrawMesh(nav, nav.Grid);
            }

            DrawAgents(world, nav);
            DrawPaths(world, nav);
        }

        #region 网格

        /// <summary>
        /// 逐格画平面方格（不是线框立方体）：2D 网格只有一层体素，填充色才看得出「面」。
        ///
        /// 判据是二元的 <see cref="NavWorldView.IsWalkable"/>（占位 + 距离场等级），
        /// 与寻路用的是同一个函数 —— 绿色描边即「代理站得下」，红色实心即「站不下」。
        /// 占据、距离不足、区域外都归红色，不细分：调试时关心的是能不能走，不是为什么不能。
        /// </summary>
        private static void DrawMesh(in NavWorldView nav, in NavGrid grid)
        {
            int budget = NavDebugSettings.MeshBudget;
            long total = grid.VoxelCount;
            int step = 1;
            while (total / ((long)step * step * step) > budget) step++;

            float half = grid.VoxelSize * step * 0.5f;
            float radius = NavDebugSettings.AgentRadius;
            bool byRegion = NavDebugSettings.DrawRegions;

            int3 dims = grid.Dimensions;
            for (int z = 0; z < dims.z; z += step)
            for (int y = 0; y < dims.y; y += step)
            for (int x = 0; x < dims.x; x += step)
            {
                var voxel = new int3(x, y, z);
                float3 center = grid.VoxelToWorld(voxel);
                SetQuad(center, half, PlaneZ(nav, center.z));

                if (byRegion) {
                    Handles.DrawSolidRectangleWithOutline(s_Quad, RegionColor(nav.RegionAt(voxel)), Color.clear);
                    continue;
                }

                if (nav.IsWalkable(voxel, radius)) {
                    Handles.DrawSolidRectangleWithOutline(s_Quad, Color.clear, WalkableOutline);
                } else {
                    Handles.DrawSolidRectangleWithOutline(s_Quad, BlockedFill, BlockedOutline);
                }
            }
        }

        /// <summary>2D 网格（只有一层体素）统一画到设定平面上，避免与游戏平面错开。</summary>
        private static float PlaneZ(in NavWorldView nav, float gridZ) =>
            nav.Grid.Dimensions.z == 1 ? NavDebugSettings.DrawPlaneZ : gridZ;

        /// <summary>把复用缓冲铺成体素中心的 XY 平面方格。</summary>
        private static void SetQuad(float3 center, float half, float planeZ)
        {
            s_Quad[0] = new Vector3(center.x - half, center.y - half, planeZ);
            s_Quad[1] = new Vector3(center.x - half, center.y + half, planeZ);
            s_Quad[2] = new Vector3(center.x + half, center.y + half, planeZ);
            s_Quad[3] = new Vector3(center.x + half, center.y - half, planeZ);
        }

        /// <summary>按连通区域上色。区域 id 是烘焙期分配的稠密下标，取模到固定调色板即可区分。</summary>
        private static Color RegionColor(int region)
        {
            if (region < 0) return IsolatedColor;

            const int slots = 8;
            int slot = region % slots;
            float hue = slot / (float)slots;
            var color = Color.HSVToRGB(hue, 0.55f, 0.9f);
            color.a = 0.30f;
            return color;
        }

        #endregion

        #region 代理与路径

        private static void DrawAgents(World world, in NavWorldView nav)
        {
            if (!NavDebugSettings.DrawAgents) return;

            var mask = new ComponentMask()
                .With<LocalTransform>()
                .With<NavAgent>()
                .With<LinearVelocity>()
                .With<NavDesiredVelocity>();
            var chunks = world.CompileQuery(new EntityQuery(mask)).GetChunks();

            int limit = NavDebugSettings.AgentLimit;
            int drawn = 0;

            for (int c = 0; c < chunks.Count && drawn < limit; c++)
            {
                Chunk chunk = chunks[c];
                var transforms = chunk.GetColumn<LocalTransform>();
                var agents = chunk.GetColumn<NavAgent>();
                var velocities = chunk.GetColumn<LinearVelocity>();
                var desired = chunk.GetColumn<NavDesiredVelocity>();

                for (int row = 0; row < chunk.Count && drawn < limit; row++)
                {
                    float3 position = transforms.At(row).Position;
                    if (!IsFinite(position)) continue;
                    var origin = new Vector3(position.x, position.y, PlaneZ(nav, position.z));

                    // 半径：导航判定的代理半径，不是碰撞形状的外接圆。
                    Handles.color = RadiusColor;
                    Handles.DrawWireDisc(origin, Vector3.forward, agents.At(row).Radius);

                    // 当前速度（积分用的）与 ORCA 期望速度分开画：两者背离说明在避障。
                    DrawArrow(origin, velocities.At(row).Value, VelocityColor);
                    DrawArrow(origin, desired.At(row).Value, DesiredColor);
                    drawn++;
                }
            }
        }

        private static void DrawPaths(World world, in NavWorldView nav)
        {
            if (!NavDebugSettings.DrawPaths) return;

            var mask = new ComponentMask()
                .With<LocalTransform>()
                .With<NavPathState>()
                .With<NavRequest>();
            var chunks = world.CompileQuery(new EntityQuery(mask)).GetChunks();

            int limit = NavDebugSettings.AgentLimit;
            int drawn = 0;

            for (int c = 0; c < chunks.Count && drawn < limit; c++)
            {
                Chunk chunk = chunks[c];
                var transforms = chunk.GetColumn<LocalTransform>();
                var paths = chunk.GetColumn<NavPathState>();
                var requests = chunk.GetColumn<NavRequest>();

                for (int row = 0; row < chunk.Count && drawn < limit; row++)
                {
                    NavPathState path = paths.At(row);
                    NavRequest request = requests.At(row);
                    if (!IsFinite(request.Target) || !IsFinite(transforms.At(row).Position)) continue;

                    // 目标点：无论有没有路径都画 —— 目标落在墙里正是最常见的失败原因。
                    Handles.color = TargetColor;
                    var target = new Vector3(request.Target.x, request.Target.y,
                        PlaneZ(nav, request.Target.z));
                    Handles.SphereHandleCap(0, target, Quaternion.identity, 0.25f, EventType.Repaint);

                    if (path.WaypointCount > 0 && path.Generation == nav.Generation) {
                        DrawWaypoints(world, nav, transforms.At(row).Position, path);
                    }

                    drawn++;
                }
            }
        }

        private static void DrawWaypoints(World world, in NavWorldView nav, float3 position, in NavPathState path)
        {
            BufferSpan<float3> waypoints = world.GetBuffer<float3>(path.Waypoints);

            // 句柄可能来自上一代烘焙；长度对不上就跳过，不要按越界下标读。
            if (waypoints.Length < path.WaypointCount) return;

            Handles.color = PathColor;
            int start = math.clamp(path.CurrentIndex, 0, path.WaypointCount - 1);
            var previous = new Vector3(position.x, position.y, PlaneZ(nav, position.z));

            for (int i = start; i < path.WaypointCount; i++)
            {
                float3 waypoint = waypoints[i];
                // 缓冲跨代复用可能读出坏值：坏点跳过该段，别让一条线污染整屏。
                if (!IsFinite(waypoint)) continue;
                var current = new Vector3(waypoint.x, waypoint.y, PlaneZ(nav, waypoint.z));
                Handles.DrawLine(previous, current);
                previous = current;
            }
        }

        private static void DrawArrow(float3 origin, float3 velocity, Color color)
        {
            // NaN 与 lengthsq 比较为 false，原守卫拦不住 NaN 速度。
            if (!IsFinite(velocity) || math.lengthsq(velocity) < 1e-6f) return;

            Handles.color = color;
            var from = new Vector3(origin.x, origin.y, origin.z);
            var to = new Vector3(origin.x + velocity.x, origin.y + velocity.y, origin.z + velocity.z);
            Handles.DrawLine(from, to);
            Handles.SphereHandleCap(0, to, Quaternion.identity, 0.08f, EventType.Repaint);
        }

        #endregion

        private static bool IsFinite(float3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
