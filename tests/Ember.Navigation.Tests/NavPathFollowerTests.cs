using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 路径跟随：航点推进、到达判定、前瞻点位置、退化输入与确定性。
    /// </summary>
    [TestFixture]
    public unsafe class NavPathFollowerTests
    {
        private const float Tol = 1e-4f;

        private static bool Step(float3[] waypoints, float3 position, float speed,
            float arriveRadius, float lookAhead, ref int currentIndex, out float3 desired)
        {
            fixed (float3* pointer = waypoints)
            {
                return NavPathFollower.Step(pointer, waypoints.Length, position, speed,
                    arriveRadius, lookAhead, ref currentIndex, out desired);
            }
        }

        [Test]
        public void StraightPath_DesiredVelocityPointsAtLookAhead()
        {
            var waypoints = new[]
            {
                new float3(10f, 0f, 0f), new float3(20f, 0f, 0f), new float3(30f, 0f, 0f),
            };
            int index = 0;

            bool onPath = Step(waypoints, float3.zero, 3f, 0.5f, 4f, ref index, out float3 desired);

            Assert.That(onPath, Is.True);
            Assert.That(index, Is.EqualTo(0));
            Assert.That(desired.x, Is.EqualTo(3f).Within(Tol));
            Assert.That(math.abs(desired.y) + math.abs(desired.z), Is.LessThan(Tol));
        }

        [Test]
        public void LookAhead_CrossesWaypointBoundary()
        {
            // 折线在 (1,0,0) 处直角转弯；前瞻 2 应从 (1,0,0) 沿 +Y 再走 1
            var waypoints = new[] { new float3(1f, 0f, 0f), new float3(1f, 5f, 0f) };
            int index = 0;

            bool onPath = Step(waypoints, float3.zero, 2f, 0.1f, 2f, ref index, out float3 desired);

            Assert.That(onPath, Is.True);
            Assert.That(index, Is.EqualTo(0), "尚未进入到达半径，不应推进航点");
            // 目标点 (1,1,0)，位置 (0,0,0)，方向 (1,1,0)/√2
            float inverseRoot2 = 1f / math.sqrt(2f);
            Assert.That(desired.x, Is.EqualTo(2f * inverseRoot2).Within(Tol));
            Assert.That(desired.y, Is.EqualTo(2f * inverseRoot2).Within(Tol));
        }

        [Test]
        public void ReachingWaypoint_AdvancesIndex()
        {
            var waypoints = new[] { new float3(1f, 0f, 0f), new float3(10f, 0f, 0f) };
            int index = 0;

            bool onPath = Step(waypoints, new float3(1.1f, 0f, 0f), 3f, 0.5f, 1f,
                ref index, out float3 desired);

            Assert.That(onPath, Is.True);
            Assert.That(index, Is.EqualTo(1));
            Assert.That(desired.x, Is.EqualTo(3f).Within(Tol));
        }

        [Test]
        public void SkippedWaypoints_AreConsumedInOneStep()
        {
            // 位置已经越过前两个航点：一次调用应全部跳过
            var waypoints = new[]
            {
                new float3(1f, 0f, 0f), new float3(2f, 0f, 0f), new float3(20f, 0f, 0f),
            };
            int index = 0;

            Step(waypoints, new float3(2.2f, 0f, 0f), 1f, 0.5f, 1f, ref index, out _);

            Assert.That(index, Is.EqualTo(2));
        }

        [Test]
        public void FinalWaypointReached_ReportsDone()
        {
            var waypoints = new[] { new float3(1f, 0f, 0f), new float3(5f, 0f, 0f) };
            int index = 0;

            bool onPath = Step(waypoints, new float3(5.2f, 0f, 0f), 3f, 0.5f, 1f,
                ref index, out float3 desired);

            Assert.That(onPath, Is.False);
            Assert.That(desired, Is.EqualTo(float3.zero));
            Assert.That(index, Is.EqualTo(1));
        }

        [Test]
        public void SingleWaypoint_Reached_ReportsDone()
        {
            var waypoints = new[] { new float3(0.2f, 0f, 0f) };
            int index = 0;

            Assert.That(Step(waypoints, float3.zero, 3f, 0.5f, 1f, ref index, out _), Is.False);
        }

        [Test]
        public void EmptyPath_ReportsDoneAndZeroVelocity()
        {
            var waypoints = new float3[0];
            int index = 0;

            Assert.That(Step(waypoints, float3.zero, 3f, 0.5f, 1f, ref index, out float3 desired),
                Is.False);
            Assert.That(desired, Is.EqualTo(float3.zero));
        }

        [Test]
        public void DuplicateWaypoints_DoNotStallLookAhead()
        {
            var waypoints = new[]
            {
                new float3(5f, 0f, 0f), new float3(5f, 0f, 0f), new float3(10f, 0f, 0f),
            };
            int index = 0;

            bool onPath = Step(waypoints, float3.zero, 2f, 0.1f, 3f, ref index, out float3 desired);

            Assert.That(onPath, Is.True);
            Assert.That(math.length(desired), Is.EqualTo(2f).Within(Tol));
            Assert.That(desired.x, Is.EqualTo(2f).Within(Tol));
        }

        [Test]
        public void ZeroSpeed_StaysOnPathWithZeroVelocity()
        {
            var waypoints = new[] { new float3(10f, 0f, 0f) };
            int index = 0;

            Assert.That(Step(waypoints, float3.zero, 0f, 0.5f, 1f, ref index, out float3 desired),
                Is.True);
            Assert.That(desired, Is.EqualTo(float3.zero));
        }

        [Test]
        public void OutOfRangeIndex_IsClampedNotThrown()
        {
            var waypoints = new[] { new float3(1f, 0f, 0f), new float3(2f, 0f, 0f) };

            int high = 99;
            Step(waypoints, float3.zero, 1f, 0.5f, 0.5f, ref high, out _);
            Assert.That(high, Is.EqualTo(1));

            int low = -5;
            Step(waypoints, float3.zero, 1f, 0.5f, 0.5f, ref low, out _);
            Assert.That(low, Is.InRange(0, 1));
        }

        [Test]
        public void SameInput_ProducesIdenticalVelocity()
        {
            var waypoints = new[]
            {
                new float3(3f, 1f, 0f), new float3(7f, -2f, 0f), new float3(12f, 4f, 0f),
            };
            int firstIndex = 0;
            int secondIndex = 0;

            Step(waypoints, new float3(0.5f, 0.25f, 0f), 2.5f, 0.3f, 1.75f,
                ref firstIndex, out float3 first);
            Step(waypoints, new float3(0.5f, 0.25f, 0f), 2.5f, 0.3f, 1.75f,
                ref secondIndex, out float3 second);

            Assert.That(second.x, Is.EqualTo(first.x));
            Assert.That(second.y, Is.EqualTo(first.y));
            Assert.That(second.z, Is.EqualTo(first.z));
        }
    }
}
