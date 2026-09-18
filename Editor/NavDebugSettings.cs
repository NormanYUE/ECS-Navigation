using Ember;
using UnityEditor;
using UnityEngine;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 导航可视化的开关与预算。状态落在 <see cref="EditorPrefs"/> 里，
    /// 调试窗口与场景绘制器共用同一份，改完立刻 <c>SceneView.RepaintAll</c>。
    /// </summary>
    public static class NavDebugSettings
    {
        private const string Prefix = "Ember.Navigation.Debug.";

        /// <summary>绘制体素方块的总量上限。超出后按目标量反推采样步长，画出来的是采样视图。</summary>
        public static int MeshBudget
        {
            get => EditorPrefs.GetInt(Prefix + "MeshBudget", 16000);
            set { EditorPrefs.SetInt(Prefix + "MeshBudget", Mathf.Max(1, value)); Repaint(); }
        }

        /// <summary>判定「可站」用的代理半径（米）。只有距离场等级 ≥ 它的体素才算可走。</summary>
        public static float AgentRadius
        {
            get => EditorPrefs.GetFloat(Prefix + "AgentRadius", 0.5f);
            set { EditorPrefs.SetFloat(Prefix + "AgentRadius", Mathf.Max(0f, value)); Repaint(); }
        }

        /// <summary>代理与路径的最大绘制条数（每个代理一个圆 + 两条箭头）。</summary>
        public static int AgentLimit
        {
            get => EditorPrefs.GetInt(Prefix + "AgentLimit", 600);
            set { EditorPrefs.SetInt(Prefix + "AgentLimit", Mathf.Max(1, value)); Repaint(); }
        }

        /// <summary>体素网格：占据 / 可行走 / 距离不足三态。</summary>
        public static bool DrawMesh
        {
            get => EditorPrefs.GetBool(Prefix + "Mesh", true);
            set { EditorPrefs.SetBool(Prefix + "Mesh", value); Repaint(); }
        }

        /// <summary>按连通区域着色（覆盖三态配色，用来看「路是不是断成几块」）。</summary>
        public static bool DrawRegions
        {
            get => EditorPrefs.GetBool(Prefix + "Regions", false);
            set { EditorPrefs.SetBool(Prefix + "Regions", value); Repaint(); }
        }

        /// <summary>代理：半径圆 + 当前速度 + ORCA 期望速度。</summary>
        public static bool DrawAgents
        {
            get => EditorPrefs.GetBool(Prefix + "Agents", true);
            set { EditorPrefs.SetBool(Prefix + "Agents", value); Repaint(); }
        }

        /// <summary>路径：航点连线 + 当前寻路目标点。</summary>
        public static bool DrawPaths
        {
            get => EditorPrefs.GetBool(Prefix + "Paths", true);
            set { EditorPrefs.SetBool(Prefix + "Paths", value); Repaint(); }
        }

        /// <summary>
        /// 当前运行的 ECS 管理器。优先用业务侧显式注册的
        /// <see cref="NavBakeContext.Manager"/>，没注册就退回框架的
        /// <see cref="ECSManager.Active"/> —— 后者让包开箱即可用，不必要求消费方多写一行。
        /// </summary>
        public static ECSManager ResolveManager() => NavBakeContext.Manager ?? ECSManager.Active;

        private static void Repaint()
        {
            SceneView.RepaintAll();
        }
    }
}
