using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// ORCA 约束构建（RVO2 语义）：手算对拍、平面投影一致性、
    /// 静态障碍责任归属、退化输入与确定性。
    /// </summary>
    [TestFixture]
    public class NavOrcaMathTests
    {
        private const float Tol = 1e-4f;
        private static readonly float3 PlaneZ = new float3(0f, 0f, 1f);
        private static readonly float3 PlaneY = new float3(0f, 1f, 0f);

        private static void AssertVec3(float3 actual, float3 expected, string what) =>
            Assert.That(math.distance(actual, expected), Is.LessThan(Tol), $"{what}: got {actual}, want {expected}");

        [Test]
        public void HeadOn_MatchesHandComputedLine()
        {
            // A(0,0) v=(1,0) r=0.5；B(2,0) v=(-1,0) r=0.5；τ=2
            // rp=(2,0) rv=(2,0) cr=1 distSq=4 leg=√3
            // w = rv - rp/τ = (1,0)；det(rp,w)=0 → 取切锥另一侧切线
            // direction = -(rp·leg - rot90(rp)·cr)/distSq = (-0.8660254, 0.5, 0)
            // u = (rv·direction)·direction - rv = (-0.5, -0.8660254, 0)
            // point = v + 0.5·u = (0.75, -0.4330127, 0)
            // Normal = cross(Z, direction) = (-0.5, -0.8660254, 0)，Offset = dot(Normal, point) = 0
            bool built = NavOrcaMath.AgentConstraint(
                position: float3.zero, velocity: new float3(1f, 0f, 0f), radius: 0.5f,
                neighborPosition: new float3(2f, 0f, 0f), neighborVelocity: new float3(-1f, 0f, 0f),
                neighborRadius: 0.5f, timeHorizon: 2f, timeStep: 0.25f, planeNormal: PlaneZ,
                out NavOrcaLine line);

            Assert.That(built, Is.True);
            AssertVec3(line.Normal, new float3(-0.5f, -0.8660254f, 0f), "normal");
            Assert.That(line.Offset, Is.EqualTo(0f).Within(Tol), "offset");
            Assert.That(line.IsSatisfied(new float3(1f, 0f, 0f)), Is.False,
                "朝向邻居的当前速度必须被约束排除");
        }

        [Test]
        public void Normal_IsUnit()
        {
            NavOrcaMath.AgentConstraint(
                new float3(1f, 2f, 0f), new float3(3f, 0f, 0f), 0.5f,
                new float3(4f, 2.5f, 0f), new float3(-1f, 0f, 0f), 0.5f,
                2f, 0.25f, PlaneZ, out NavOrcaLine line);

            Assert.That(math.length(line.Normal), Is.EqualTo(1f).Within(Tol));
        }

        [Test]
        public void ParallelNeighbors_MovingApart_KeepVelocity()
        {
            // 同速同向、间隔足够：当前速度满足约束，无需避让
            NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 0f, 0f), 0.5f,
                new float3(10f, 0f, 0f), new float3(1f, 0f, 0f), 0.5f,
                2f, 0.25f, PlaneZ, out NavOrcaLine line);

            Assert.That(line.IsSatisfied(new float3(1f, 0f, 0f)), Is.True);
        }

        [Test]
        public void PlaneProjection_IgnoresInactiveAxis()
        {
            // XY 平面：邻居只在 Z 上不同，约束必须完全一致
            NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 0f, 0f), 0.5f,
                new float3(2f, 0f, 0f), new float3(-1f, 0f, 0f), 0.5f,
                2f, 0.25f, PlaneZ, out NavOrcaLine flat);

            NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 0f, 99f), 0.5f,
                new float3(2f, 0f, -40f), new float3(-1f, 0f, 5f), 0.5f,
                2f, 0.25f, PlaneZ, out NavOrcaLine lifted);

            AssertVec3(lifted.Normal, flat.Normal, "normal");
            Assert.That(lifted.Offset, Is.EqualTo(flat.Offset).Within(Tol), "offset");
        }

        [Test]
        public void XzPlane_NormalStaysInPlane()
        {
            NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 5f, 0f), 0.5f,
                new float3(2f, -7f, 0f), new float3(-1f, 3f, 0f), 0.5f,
                2f, 0.25f, PlaneY, out NavOrcaLine line);

            Assert.That(line.Normal.y, Is.EqualTo(0f).Within(Tol), "XZ 平面法线的 Y 分量必须为 0");
        }

        [Test]
        public void StaticObstacle_TakesFullResponsibility()
        {
            // 代理朝障碍直冲：距离 1、梯度背离障碍指向代理
            bool built = NavOrcaMath.StaticConstraint(
                position: float3.zero, velocity: new float3(1f, 0f, 0f), radius: 0.5f,
                distance: 1f, gradient: new float3(-1f, 0f, 0f),
                timeHorizon: 2f, timeStep: 0.25f, planeNormal: PlaneZ,
                out NavOrcaLine line);

            Assert.That(built, Is.True);
            Assert.That(line.IsSatisfied(new float3(1f, 0f, 0f)), Is.False, "冲向障碍的速度必须被排除");
            Assert.That(math.length(line.Normal), Is.EqualTo(1f).Within(Tol));
        }

        [Test]
        public void StaticObstacle_ResponsibilityIsFull()
        {
            // 同一几何下，静态障碍的半平面偏移量应比代理对（各半责）更远离原点：
            // 代理愿承担一半避让，障碍要求全部。
            NavOrcaMath.StaticConstraint(
                float3.zero, new float3(1f, 0f, 0f), 0.5f,
                1f, new float3(-1f, 0f, 0f), 2f, 0.25f, PlaneZ, out NavOrcaLine obstacle);

            NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 0f, 0f), 0.5f,
                new float3(-1f, 0f, 0f), float3.zero, 0f, 2f, 0.25f, PlaneZ, out NavOrcaLine half);

            Assert.That(math.dot(obstacle.Normal, new float3(1f, 0f, 0f)),
                Is.LessThan(math.dot(half.Normal, new float3(1f, 0f, 0f))),
                "静态障碍必须比半责代理更严格");
        }

        [Test]
        public void CoincidentAgents_CollisionBranchEscapesAlongRelativeVelocity()
        {
            // 相对位置为零向量：走已碰撞分支，逃离方向退化为相对速度方向，
            // 半平面法线即该方向的平面内垂线。位置重合时约束轴本身是任意的，
            // 这里锁定的是「确定性 + 分支正确」，不是物理直觉。
            bool built = NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 0f, 0f), 0.5f,
                float3.zero, new float3(-1f, 0f, 0f), 0.5f,
                2f, 0.25f, PlaneZ, out NavOrcaLine line);

            Assert.That(built, Is.True);
            AssertVec3(line.Normal, new float3(0f, 1f, 0f), "normal");
            Assert.That(math.length(line.Normal), Is.EqualTo(1f).Within(Tol));
        }

        [Test]
        public void ZeroTimeHorizon_BuildsNothing()
        {
            Assert.That(NavOrcaMath.AgentConstraint(
                float3.zero, float3.zero, 0.5f, new float3(1f, 0f, 0f), float3.zero, 0.5f,
                0f, 0.25f, PlaneZ, out _), Is.False);

            Assert.That(NavOrcaMath.AgentConstraint(
                float3.zero, float3.zero, 0.5f, new float3(1f, 0f, 0f), float3.zero, 0.5f,
                2f, 0f, PlaneZ, out _), Is.False);
        }

        [Test]
        public void SameInput_ProducesBitIdenticalLine()
        {
            NavOrcaMath.AgentConstraint(
                new float3(1.5f, -2.25f, 0f), new float3(0.75f, 1.5f, 0f), 0.4f,
                new float3(2.5f, -1f, 0f), new float3(-0.5f, 0.25f, 0f), 0.6f,
                1.5f, 0.02f, PlaneZ, out NavOrcaLine first);

            NavOrcaMath.AgentConstraint(
                new float3(1.5f, -2.25f, 0f), new float3(0.75f, 1.5f, 0f), 0.4f,
                new float3(2.5f, -1f, 0f), new float3(-0.5f, 0.25f, 0f), 0.6f,
                1.5f, 0.02f, PlaneZ, out NavOrcaLine second);

            Assert.That(second.Normal.x, Is.EqualTo(first.Normal.x));
            Assert.That(second.Normal.y, Is.EqualTo(first.Normal.y));
            Assert.That(second.Normal.z, Is.EqualTo(first.Normal.z));
            Assert.That(second.Offset, Is.EqualTo(first.Offset));
        }
    }
}
