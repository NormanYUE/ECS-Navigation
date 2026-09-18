using Ember;
using Ember.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 导航调试窗口：开关场景视图的各图层，并显示当前世界的体素 / 区域 / 请求统计。
    ///
    /// 统计是为了省掉「为什么单位不动」的反复猜测：
    /// 请求状态直方图能一眼看出是不是大面积 <c>Failed</c>，
    /// 体素三态计数能看出可行走面积是不是被切碎了。
    /// </summary>
    public class NavDebugWindow : EditorWindow
    {
        /// <summary>统计扫描的采样上限：大地图上按步长采样，避免每次重绘都全量扫。</summary>
        private const int StatSampleBudget = 200000;

        private Vector2 m_Scroll;

        [MenuItem("Ember/Navigation/调试窗口", priority = 0)]
        private static void Open()
        {
            var window = GetWindow<NavDebugWindow>("Ember 导航调试");
            window.minSize = new Vector2(300f, 260f);
        }

        private void OnInspectorUpdate()
        {
            // 运行时数据每帧都在变，低频重绘保持读数活着。
            Repaint();
        }

        private void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            DrawLayerToggles();
            EditorGUILayout.Space(6f);
            DrawStats();

            EditorGUILayout.EndScrollView();
        }

        private static void DrawLayerToggles()
        {
            EditorGUILayout.LabelField("图层", EditorStyles.boldLabel);
            NavDebugSettings.DrawMesh = EditorGUILayout.ToggleLeft(
                "体素网格（绿色空心 = 可走 / 红色实心 = 不可走）", NavDebugSettings.DrawMesh);
            NavDebugSettings.DrawRegions = EditorGUILayout.ToggleLeft(
                "按连通区域着色（覆盖绿/红配色）", NavDebugSettings.DrawRegions);
            NavDebugSettings.DrawAgents = EditorGUILayout.ToggleLeft(
                "代理（半径 + 速度 + 期望速度）", NavDebugSettings.DrawAgents);
            NavDebugSettings.DrawPaths = EditorGUILayout.ToggleLeft(
                "路径（航点连线 + 当前目标）", NavDebugSettings.DrawPaths);

            EditorGUILayout.Space(4f);
            NavDebugSettings.AgentRadius = EditorGUILayout.Slider(
                "判定半径（米）", NavDebugSettings.AgentRadius, 0f, 3f);
            NavDebugSettings.DrawPlaneZ = EditorGUILayout.FloatField(
                "2D 绘制平面 Z", NavDebugSettings.DrawPlaneZ);
            NavDebugSettings.MeshBudget = EditorGUILayout.IntField(
                "网格绘制上限（体素）", NavDebugSettings.MeshBudget);
            NavDebugSettings.AgentLimit = EditorGUILayout.IntField(
                "代理/路径绘制上限", NavDebugSettings.AgentLimit);
        }

        private static void DrawStats()
        {
            EditorGUILayout.LabelField("运行时统计", EditorStyles.boldLabel);

            if (!Application.isPlaying) {
                EditorGUILayout.HelpBox("导航数据只在 Play 模式存在。", MessageType.Info);
                return;
            }

            ECSManager manager = NavDebugSettings.ResolveManager();
            World world = manager?.World;
            if (world is not { IsDisposed: false } || !world.TryGetNavWorld(out NavWorldView nav) || !nav.IsReady) {
                EditorGUILayout.HelpBox("没有就绪的 NavWorld。", MessageType.Warning);
                return;
            }

            NavGrid grid = nav.Grid;
            NavWorld state = nav.StateSnapshot;

            EditorGUILayout.LabelField("代际", state.Generation.ToString());
            EditorGUILayout.LabelField("体素尺寸", grid.VoxelSize.ToString("F3"));
            EditorGUILayout.LabelField("网格", $"{grid.Dimensions.x} × {grid.Dimensions.y} × {grid.Dimensions.z}");
            EditorGUILayout.LabelField("烘焙半径上界", state.MaxBakeRadius.ToString("F2"));
            EditorGUILayout.LabelField("烘焙平面 Z", grid.Origin.z.ToString("F2"));
            EditorGUILayout.LabelField("连通区域数", state.RegionCount.ToString());
            EditorGUILayout.LabelField("区域间连接数", state.LinkCount.ToString());

            EditorGUILayout.Space(4f);
            DrawVoxelStats(nav, grid);

            EditorGUILayout.Space(4f);
            DrawAgentStats(world, nav);
        }

        private static void DrawVoxelStats(in NavWorldView nav, in NavGrid grid)
        {
            long total = grid.VoxelCount;
            int step = 1;
            while (total / ((long)step * step * step) > StatSampleBudget) step++;

            float radius = NavDebugSettings.AgentRadius;
            int occupied = 0, isolated = 0, blocked = 0, walkable = 0;

            int3 dims = grid.Dimensions;
            for (int z = 0; z < dims.z; z += step)
            for (int y = 0; y < dims.y; y += step)
            for (int x = 0; x < dims.x; x += step)
            {
                var voxel = new int3(x, y, z);
                if (nav.IsOccupied(voxel)) { occupied++; continue; }
                if (nav.RegionAt(voxel) < 0) { isolated++; continue; }
                if (nav.IsWalkable(voxel, radius)) walkable++;
                else blocked++;
            }

            EditorGUILayout.LabelField($"体素（{step} 步采样）", $"占据 {occupied} / 孤立 {isolated} / 距离不足 {blocked} / 可走 {walkable}");
        }

        private static void DrawAgentStats(World world, in NavWorldView nav)
        {
            var mask = new ComponentMask()
                .With<LocalTransform>()
                .With<NavAgent>()
                .With<NavPathState>()
                .With<NavRequest>();
            var chunks = world.CompileQuery(new EntityQuery(mask)).GetChunks();

            int total = 0;
            int withPath = 0;
            int none = 0, pending = 0, inProgress = 0, ready = 0, failed = 0;

            for (int c = 0; c < chunks.Count; c++)
            {
                Chunk chunk = chunks[c];
                var paths = chunk.GetColumn<NavPathState>();
                var requests = chunk.GetColumn<NavRequest>();

                for (int row = 0; row < chunk.Count; row++)
                {
                    total++;
                    if (paths.At(row).WaypointCount > 0) withPath++;
                    switch (requests.At(row).Status) {
                        case NavRequestStatus.None: none++; break;
                        case NavRequestStatus.Pending: pending++; break;
                        case NavRequestStatus.InProgress: inProgress++; break;
                        case NavRequestStatus.Ready: ready++; break;
                        case NavRequestStatus.Failed: failed++; break;
                    }
                }
            }

            EditorGUILayout.LabelField("代理总数", total.ToString());
            EditorGUILayout.LabelField("持有路径", withPath.ToString());
            EditorGUILayout.LabelField("请求状态",
                $"无 {none} / 待调度 {pending} / 搜索中 {inProgress} / 就绪 {ready} / 失败 {failed}");

            if (failed > 0) {
                EditorGUILayout.HelpBox(
                    $"{failed} 个请求失败。失败不会被重试 —— 常见原因是目标点落在墙里" +
                    "（编队展开宽度大于道路格宽），或请求发出时导航数据还没就绪。",
                    MessageType.Warning);
            }
        }
    }
}
