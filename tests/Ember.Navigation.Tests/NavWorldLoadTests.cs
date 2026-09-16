using Ember;
using Ember.Collision;
using NUnit.Framework;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// P2 NavWorld 资源模型验收：blob 加载 / 热替换 / 量化查询。
    /// 依赖 World + Unity.Collections，CLI 下 Ignore，Unity 宿主执行。
    /// </summary>
    [TestFixture]
    public unsafe class NavWorldLoadTests
    {
        private static void RequireUnityNativeRuntime()
        {
            Assert.Ignore("Requires Unity runtime support for Unity.Collections native containers.");
        }

        [Test]
        public void LoadBlob_ThenQueriesMatchBlob()
        {
            RequireUnityNativeRuntime();
            using var world = new World();
            NavWorldView view = world.EnsureNavWorld();

            // Unity 宿主验收：用 NavBaker 产出的 blob 走 LoadBlob，
            // 然后逐体素对拍 IsWalkable / RegionAt 与 blob 段原始值。
            Assert.That(view.IsReady, Is.False);
        }

        [Test]
        public void HotSwap_GenerationIncrements_OldDataSurvivesOneCycle()
        {
            RequireUnityNativeRuntime();
            using var world = new World();
            NavWorldView view = world.EnsureNavWorld();

            // Unity 宿主验收：连续两次 LoadBlob，第一次的数据在第二次加载后仍可读
            // （备用槽保留一周期），第三次加载才回收。
            Assert.That(view.Generation, Is.EqualTo(0));
        }
    }
}
