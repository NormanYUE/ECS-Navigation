using Ember.Navigation;
using NUnit.Framework;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 净空口径：代理半径 → 距离场量化等级。
    ///
    /// 这个函数是可走判定的唯一口径（<c>IsWalkable</c>、流场建场 / 查询、A* 搜索共用），
    /// 所以边界值要钉死：半径 0 必须等价于「只看占据」的旧口径，否则升级会静默改行为。
    /// </summary>
    [TestFixture]
    public class NavWorldViewClearanceTests
    {
        [Test]
        public void ZeroRadius_IsTheOccupancyOnlyLegacyEntry()
        {
            Assert.That(NavWorldView.RequiredLevelFor(0f, 8, 8f), Is.EqualTo(0));
            Assert.That(NavWorldView.RequiredLevelFor(0f, 16, 8f), Is.EqualTo(0));
        }

        [Test]
        public void FullScaleRadius_SaturatesBothBitWidths()
        {
            Assert.That(NavWorldView.RequiredLevelFor(8f, 8, 8f), Is.EqualTo(255));
            Assert.That(NavWorldView.RequiredLevelFor(8f, 16, 8f), Is.EqualTo(65535));
        }

        [Test]
        public void HalfScaleRadius_IsHalfTheLevels()
        {
            Assert.That(NavWorldView.RequiredLevelFor(4f, 8, 8f), Is.EqualTo(128).Within(1));
            Assert.That(NavWorldView.RequiredLevelFor(4f, 16, 8f), Is.EqualTo(32768).Within(1));
        }

        [Test]
        public void OutOfRangeRadii_ClampInsteadOfWrapping()
        {
            Assert.That(NavWorldView.RequiredLevelFor(-1f, 8, 8f), Is.EqualTo(0));
            Assert.That(NavWorldView.RequiredLevelFor(100f, 8, 8f), Is.EqualTo(255));
            Assert.That(NavWorldView.RequiredLevelFor(float.PositiveInfinity, 8, 8f), Is.EqualTo(255));
        }

        [Test]
        public void DegenerateMaxBakeRadius_DoesNotProduceNaN()
        {
            // MaxBakeRadius = 0 是非法烘焙结果，但查询不该因此炸掉或返回 NaN 等级。
            Assert.That(NavWorldView.RequiredLevelFor(0f, 8, 0f), Is.EqualTo(0));
            Assert.That(NavWorldView.RequiredLevelFor(0.5f, 8, 0f), Is.EqualTo(255));
        }

        [Test]
        public void LevelIsMonotonicInRadius()
        {
            int previous = -1;
            for (int i = 0; i <= 16; i++)
            {
                int level = NavWorldView.RequiredLevelFor(i * 0.5f, 16, 8f);
                Assert.That(level, Is.GreaterThanOrEqualTo(previous));
                previous = level;
            }
        }
    }
}
