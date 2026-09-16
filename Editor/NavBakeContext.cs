using Ember;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 烘焙窗口与运行中 ECS 世界的衔接点。
    ///
    /// <see cref="ECSManager"/> 是普通类而非 <c>UnityEngine.Object</c>，
    /// 编辑器的 <c>FindObjectOfType</c> 找不到它，序列化字段也留不住它的引用。
    /// 因此由业务侧在启动时赋一次值：
    /// <code>NavBakeContext.Manager = manager;</code>
    /// 编译期用 <c>UNITY_EDITOR</c> 包住这一行，打包时自然消失。
    ///
    /// 只在 Play 模式有意义 —— 碰撞世界只在运行时存在。
    /// </summary>
    public static class NavBakeContext
    {
        /// <summary>当前运行的 ECS 管理器；未设置时为 null。</summary>
        public static ECSManager Manager { get; set; }
    }
}
