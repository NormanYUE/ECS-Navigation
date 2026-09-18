using Ember;
using Ember.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 场景视图里的导航可视化：体素网格（占据 / 可行走 / 距离不足，可选按区域着色）、
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
        private static readonly Color OccupiedColor = new(0.95f, 0.45f, 0.15f, 0.45f);
        private static readonly Color BlockedColor = new(0.85f, 0.85f, 0.25f, 0.30f);
        private static readonly Color IsolatedColor = new(0.55f, 0.55f, 0.55f, 0.30f);
        private static readonly Color WalkableColor = new(0.35f, 0.85f, 0.55f, 0.30f);
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

        private static void DrawMesh(in NavWorldView nav, in NavGrid grid)
        {
            int budget = NavDebugSettings.MeshBudget;
            long total = grid.VoxelCount;
            int step = 1;
            while (total / ((long)step * step * step) > budget) step++;

            float size = grid.VoxelSize * step * 0.9f;
            var cube = new Vector3(size, size, size);
            float radius = NavDebugSettings.AgentRadius;
            bool byRegion = NavDebugSettings.DrawRegions;

            int3 dims = grid.Dimensions;
            for (int z = 0; z < dims.z; z += step)
            for (int y = 0; y < dims.y; y += step)
            for (int x = 0; x < dims.x; x += step)
            {
                var voxel = new int3(x, y, z);
                Handles.color = byRegion ? RegionColor(nav.RegionAt(voxel)) : CellColor(nav, voxel, radius);
                Handles.DrawWireCube(grid.VoxelToWorld(voxel), cube);
            }
        }

        /// <summary>三态配色：占据 / 距离不足 / 区域外孤立 / 可走。</summary>
        private static Color CellColor(in NavWorldView nav, int3 voxel, float radius)
        {
            if (nav.IsOccupied(voxel)) return OccupiedColor;
            if (nav.RegionAt(voxel) < 0) return IsolatedColor;
            return nav.IsWalkable(voxel, radius) ? WalkableColor : BlockedColor;
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
                    var origin = new Vector3(position.x, position.y, position.z);

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

                    // 目标点：无论有没有路径都画 —— 目标落在墙里正是最常见的失败原因。
                    Handles.color = TargetColor;
                    var target = new Vector3(request.Target.x, request.Target.y, request.Target.z);
                    Handles.SphereHandleCap(0, target, Quaternion.identity, 0.25f, EventType.Repaint);

                    if (path.WaypointCount > 0 && path.Generation == nav.Generation) {
                        DrawWaypoints(world, transforms.At(row).Position, path);
                    }

                    drawn++;
                }
            }
        }

        private static void DrawWaypoints(World world, float3 position, in NavPathState path)
        {
            BufferSpan<float3> waypoints = world.GetBuffer<float3>(path.Waypoints);

            // 句柄可能来自上一代烘焙；长度对不上就跳过，不要按越界下标读。
            if (waypoints.Length < path.WaypointCount) return;

            Handles.color = PathColor;
            int start = math.clamp(path.CurrentIndex, 0, path.WaypointCount - 1);
            var previous = new Vector3(position.x, position.y, position.z);

            for (int i = start; i < path.WaypointCount; i++)
            {
                float3 waypoint = waypoints[i];
                var current = new Vector3(waypoint.x, waypoint.y, waypoint.z);
                Handles.DrawLine(previous, current);
                previous = current;
            }
        }

        private static void DrawArrow(float3 origin, float3 velocity, Color color)
        {
            if (math.lengthsq(velocity) < 1e-6f) return;

            Handles.color = color;
            var from = new Vector3(origin.x, origin.y, origin.z);
            var to = new Vector3(origin.x + velocity.x, origin.y + velocity.y, origin.z + velocity.z);
            Handles.DrawLine(from, to);
            Handles.SphereHandleCap(0, to, Quaternion.identity, 0.08f, EventType.Repaint);
        }

        #endregion
    }
}
