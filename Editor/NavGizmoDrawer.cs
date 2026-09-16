using Ember;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 场景视图里的导航网格可视化：把已加载距离场的可行走体素画成线框方块。
    ///
    /// 体素数在大地图上是百万级，逐格画会拖垮编辑器 —— 按<b>目标绘制量</b>
    /// 反推步长，并在菜单里给一个可调上限。因此画出来的是采样视图，
    /// 用于确认整体形状与空洞，不是精确的逐格对照。
    ///
    /// 只在 Play 模式有效：导航数据在运行中的 ECS 世界里。
    /// </summary>
    [InitializeOnLoad]
    public static class NavGizmoDrawer
    {
        private const string EnabledKey = "Ember.Navigation.DrawNavMesh";
        private const string BudgetKey = "Ember.Navigation.DrawBudget";
        private const string MenuPath = "Ember/Navigation/显示导航网格";

        /// <summary>每帧绘制上限（体素方块数）。改这里即可，按目标绘制量反推采样步长。</summary>
        private const int DefaultBudget = 4000;

        private static bool s_Enabled;
        private static int s_Budget;

        static NavGizmoDrawer()
        {
            s_Enabled = EditorPrefs.GetBool(EnabledKey, false);
            s_Budget = EditorPrefs.GetInt(BudgetKey, DefaultBudget);
            SceneView.duringSceneGui += OnSceneGui;
        }

        [MenuItem(MenuPath, priority = 100)]
        private static void Toggle()
        {
            s_Enabled = !s_Enabled;
            EditorPrefs.SetBool(EnabledKey, s_Enabled);
            SceneView.RepaintAll();
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, s_Enabled);
            return true;
        }

        [MenuItem("Ember/Navigation/可视化绘制上限/1000", priority = 101)]
        private static void Budget1000() => SetBudget(1000);

        [MenuItem("Ember/Navigation/可视化绘制上限/4000", priority = 102)]
        private static void Budget4000() => SetBudget(4000);

        [MenuItem("Ember/Navigation/可视化绘制上限/16000", priority = 103)]
        private static void Budget16000() => SetBudget(16000);

        [MenuItem("Ember/Navigation/可视化绘制上限/1000", validate = true)]
        [MenuItem("Ember/Navigation/可视化绘制上限/4000", validate = true)]
        [MenuItem("Ember/Navigation/可视化绘制上限/16000", validate = true)]
        private static bool BudgetValidate()
        {
            Menu.SetChecked("Ember/Navigation/可视化绘制上限/1000", s_Budget == 1000);
            Menu.SetChecked("Ember/Navigation/可视化绘制上限/4000", s_Budget == 4000);
            Menu.SetChecked("Ember/Navigation/可视化绘制上限/16000", s_Budget == 16000);
            return true;
        }

        private static void SetBudget(int budget)
        {
            s_Budget = budget;
            EditorPrefs.SetInt(BudgetKey, budget);
            SceneView.RepaintAll();
        }

        private static void OnSceneGui(SceneView view)
        {
            if (!s_Enabled || !Application.isPlaying) return;

            ECSManager manager = NavBakeContext.Manager;
            if (manager == null || manager.World == null) return;
            if (!manager.World.TryGetNavWorld(out NavWorldView nav) || !nav.IsReady) return;

            DrawWalkable(nav, nav.Grid);
        }

        private static void DrawWalkable(in NavWorldView nav, in NavGrid grid)
        {
            long total = grid.VoxelCount;
            int step = 1;
            while (total / ((long)step * step * step) > s_Budget) step++;

            float cubeSize = grid.VoxelSize * step * 0.9f;
            Handles.color = new Color(0.35f, 0.85f, 0.55f, 0.35f);

            int3 dims = grid.Dimensions;
            for (int z = 0; z < dims.z; z += step)
            for (int y = 0; y < dims.y; y += step)
            for (int x = 0; x < dims.x; x += step)
            {
                var voxel = new int3(x, y, z);
                if (nav.IsOccupied(voxel)) continue;

                Handles.DrawWireCube(grid.VoxelToWorld(voxel), new Vector3(cubeSize, cubeSize, cubeSize));
            }
        }

    }
}
