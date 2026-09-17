using Ember;

namespace Ember.Navigation
{
    /// <summary>World 上的导航扩展：单例访问入口（模式同 CollisionWorldExtensions）。</summary>
    public static class NavWorldExtensions
    {
        /// <summary>
        /// 确保导航单例存在（不存在则创建实体 + 组件，NavConfig 写入出厂默认）。
        /// 重复调用为空操作。
        /// </summary>
        public static NavWorldView EnsureNavWorld(this World world)
        {
            Entity configEntity = world.GetOrCreateSingleton<NavConfig>();
            Entity worldEntity = world.GetOrCreateSingleton<NavWorld>();

            // 首次创建时写入出厂默认（VoxelSize 为 0 即未初始化）。
            if (world.GetComponent<NavConfig>(configEntity).VoxelSize <= 0f)
                world.GetComponent<NavConfig>(configEntity) = NavConfig.Default;

            return new NavWorldView(world, worldEntity);
        }

        /// <summary>取导航视图（单例未创建时抛异常，先用 <see cref="TryGetNavWorld"/>）。</summary>
        public static NavWorldView GetNavWorld(this World world)
        {
            return !world.TryGetNavWorld(out NavWorldView view)
                ? throw new System.InvalidOperationException(
                    "导航模块未启用：请先调用 EnsureNavWorld() 或注册导航系统组并运行至少一帧")
                : view;
        }

        /// <summary>尝试取导航视图；未初始化时返回 false。</summary>
        public static bool TryGetNavWorld(this World world, out NavWorldView view)
        {
            view = default;
            if (!world.TryGetSingleton<NavWorld>(out Entity owner)) return false;
            view = new NavWorldView(world, owner);
            return true;
        }
    }
}
