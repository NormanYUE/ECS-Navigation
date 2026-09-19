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

        /// <summary>
        /// 代理被推到路径外侧时，前瞻点必须落在**折线上**，而不是从代理位置直接朝下一个
        /// 航点切过去。
        ///
        /// 回归用例：前瞻原先从代理的实际位置起步，而避障会把代理推离折线 ——
        /// 从那个偏离点朝十几米外的航点直线前进，方向会切过弯角（中间隔着墙）。
        /// 战场上表现为顶着墙角原地磨、来回抖。现在先把位置投到折线上再前进。
        /// </summary>
        [Test]
        public void LookAhead_FromOffPathPosition_StaysOnPath()
        {
            // 直角路径：先沿 +Y 到 (0,10)，再沿 +X 到 (12,10)。代理被推到路径右侧 (2,4)，
            // 且已推进到最后一个航点（现场正是「当前航点到了末点、离终点还有十几米」）。
            var waypoints = new[]
            {
                new float3(0f, 0f, 0f), new float3(0f, 10f, 0f), new float3(12f, 10f, 0f),
            };
            int index = 2;

            bool onPath = Step(waypoints, new float3(2f, 4f, 0f), 3f, 0.5f, 2f, ref index,
                out float3 desired);

            Assert.That(onPath, Is.True);
            // 投影点 (2,10)，沿水平段再前进 2 得目标 (4,10)；方向即 (2,6)/√40
            float inverse = 1f / math.sqrt(40f);
            Assert.That(desired.x / 3f, Is.EqualTo(2f * inverse).Within(Tol), "x 方向");
            Assert.That(desired.y / 3f, Is.EqualTo(6f * inverse).Within(Tol), "y 方向");
        }

        [Test]
        public void LookAhead_CrossesWaypointBoundary()
        {
            // 折线从 (1,0,0) 起沿 +Y 走，代理在 (0,0,0)（路径起点之前 1 米）。
            //
            // 前瞻**只沿折线**：代理先被投到折线起点 (1,0,0)，再前进 2 得 (1,2,0)。
            // 它不再把「代理 → 折线起点」那一段算进前瞻里程 —— 那一段不是路径，
            // 算进去就等于让代理朝一个偏离点直冲，正是切过弯角、顶着墙磨的来源。
            var waypoints = new[] { new float3(1f, 0f, 0f), new float3(1f, 5f, 0f) };
            int index = 0;

            bool onPath = Step(waypoints, float3.zero, 2f, 0.1f, 2f, ref index, out float3 desired);

            Assert.That(onPath, Is.True);
            Assert.That(index, Is.EqualTo(0), "尚未进入到达半径，不应推进航点");
            // 目标点 (1,2,0)，位置 (0,0,0)，方向 (1,2,0)/√5
            float inverseRoot5 = 1f / math.sqrt(5f);
            Assert.That(desired.x, Is.EqualTo(2f * inverseRoot5).Within(Tol));
            Assert.That(desired.y, Is.EqualTo(4f * inverseRoot5).Within(Tol));
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
