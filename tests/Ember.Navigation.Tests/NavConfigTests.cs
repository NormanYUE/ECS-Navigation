using Ember.Navigation;
using NUnit.Framework;

namespace Ember.Navigation.Tests
{
    /// <summary>P0 骨架冒烟：组件默认值与配置出厂值。</summary>
    [TestFixture]
    public class NavConfigTests
    {
        [Test]
        public void Default_HasExpectedShape()
        {
            NavConfig config = NavConfig.Default;

            Assert.That(config.VoxelSize, Is.GreaterThan(0f));
            Assert.That(config.TileSize, Is.GreaterThan(0));
            Assert.That(config.MaxBakeRadius, Is.GreaterThan(0f));
            Assert.That(config.RequestBudgetPerFrame, Is.GreaterThan(0));
            Assert.That(config.FlowFieldCacheSize, Is.GreaterThan(0));
            Assert.That(config.DistanceFieldBits, Is.EqualTo((byte)8).Or.EqualTo((byte)16));
        }

        [Test]
        public void Agent_DefaultsAreGroundMode()
        {
            var agent = new NavAgent(radius: 0.5f, maxSpeed: 3f);

            Assert.That(agent.Mode, Is.EqualTo(NavAgentMode.Ground));
            Assert.That(agent.Radius, Is.EqualTo(0.5f));
            Assert.That(agent.TimeHorizonOverride, Is.LessThanOrEqualTo(0f));
        }

        [Test]
        public void Request_StatusDefaultsToNone()
        {
            var request = new NavRequest();
            Assert.That(request.Status, Is.EqualTo(NavRequestStatus.None));
        }
    }
}
