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

        /// <summary>
        /// 开关布局版本。<b>改动下面四个开关的默认值时必须递增它。</b>
        ///
        /// 为什么需要：开关存在 <see cref="EditorPrefs"/> 里，而
        /// <c>GetBool(key, default)</c> <b>只读不写</b> —— 只有用户点过调试窗口的勾选才会落盘。
        /// 于是「改默认值」对已经动过设置的人完全无效，他们会一直看到旧布局。
        /// 版本不符时清掉这四个键，让新默认值生效。
        ///
        /// 只清开关，不动 <c>MeshBudget</c> / <c>AgentRadius</c> / <c>DrawPlaneZ</c> ——
        /// 那些是调好的参数，与图层默认值无关，不该被连带重置。
        /// </summary>
        private const int LayoutVersion = 2;

        private const string VersionKey = Prefix + "LayoutVersion";

        /// <summary>
        /// 版本不符就把四个开关重置到新默认值。匹配时只是一次整数比较，开销可忽略 ——
        /// 所以在每个 getter/setter 里都先走一遍，省去「谁负责初始化」的时序问题。
        ///
        /// setter 也要走：否则用户先点勾选、后触发版本检查时，刚写的值会被清掉。
        /// </summary>
        private static void EnsureLayoutVersion()
        {
            if (EditorPrefs.GetInt(VersionKey, 0) == LayoutVersion) return;

            EditorPrefs.DeleteKey(Prefix + "Mesh");
            EditorPrefs.DeleteKey(Prefix + "Regions");
            EditorPrefs.DeleteKey(Prefix + "Agents");
            EditorPrefs.DeleteKey(Prefix + "Paths");
            EditorPrefs.SetInt(VersionKey, LayoutVersion);
        }

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

        /// <summary>
        /// 2D 网格的绘制平面 Z。导航网格的<b>烘焙平面</b>未必等于游戏平面 ——
        /// 本工程烘焙落在 z ≈ -3.75（网格原点 z = -4），而道路摆在 z = 0；
        /// 透视视角下按烘焙平面画会整体偏移，看起来「像路但偏了一大截」。
        /// 因此 2D 网格（Dimensions.z == 1）一律按本值绘制。
        /// </summary>
        public static float DrawPlaneZ
        {
            get => EditorPrefs.GetFloat(Prefix + "PlaneZ", 0f);
            set { EditorPrefs.SetFloat(Prefix + "PlaneZ", value); Repaint(); }
        }

        /// <summary>
        /// 体素网格：占据 / 可行走 / 距离不足三态。
        /// <b>默认关</b>：它按目标量反推采样步长铺满全屏，是「路网长什么样」的诊断视图，
        /// 与「代理要去哪」无关。要看它就在调试窗口里打开。
        /// </summary>
        public static bool DrawMesh
        {
            get { EnsureLayoutVersion(); return EditorPrefs.GetBool(Prefix + "Mesh", false); }
            set { EnsureLayoutVersion(); EditorPrefs.SetBool(Prefix + "Mesh", value); Repaint(); }
        }

        /// <summary>按连通区域着色（覆盖三态配色，用来看「路是不是断成几块」）。</summary>
        public static bool DrawRegions
        {
            get { EnsureLayoutVersion(); return EditorPrefs.GetBool(Prefix + "Regions", false); }
            set { EnsureLayoutVersion(); EditorPrefs.SetBool(Prefix + "Regions", value); Repaint(); }
        }

        /// <summary>
        /// 代理：半径圆 + 当前速度 + ORCA 期望速度。
        /// <b>默认关</b>：一个代理三笔（圆 + 两条箭头），几百个代理就是上千条线，
        /// 把真正的信息（目标点与路径）淹掉。排查避障行为时再单独打开。
        /// </summary>
        public static bool DrawAgents
        {
            get { EnsureLayoutVersion(); return EditorPrefs.GetBool(Prefix + "Agents", false); }
            set { EnsureLayoutVersion(); EditorPrefs.SetBool(Prefix + "Agents", value); Repaint(); }
        }

        /// <summary>
        /// 路径：航点连线 + 当前寻路目标点。<b>默认开，且是唯一默认开的图层</b> ——
        /// 「代理实际要去哪、走哪条线」是绝大多数时候唯一要看的东西。
        /// </summary>
        public static bool DrawPaths
        {
            get { EnsureLayoutVersion(); return EditorPrefs.GetBool(Prefix + "Paths", true); }
            set { EnsureLayoutVersion(); EditorPrefs.SetBool(Prefix + "Paths", value); Repaint(); }
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
