using System.Collections.Generic;
using Ember.Collision;
using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 2D 线性规划对拍：与「候选点枚举」暴力解比较最优性，
    /// 外加全约束满足性、超速截断、回退路径与确定性。
    /// </summary>
    [TestFixture]
    public unsafe class NavLinearProgram2DTests
    {
        private const float Tol = 1e-4f;
        private static readonly float3 PlaneZ = new float3(0f, 0f, 1f);

        private static NavOrcaLine Line(float3 direction, float3 point) =>
            NavOrcaLine.FromDirectionPoint(direction, point, PlaneZ);

        private static bool Feasible(NavOrcaLine[] lines, float3 velocity, float maxSpeed)
        {
            if (math.length(velocity) > maxSpeed + Tol) return false;
            for (int i = 0; i < lines.Length; i++)
                if (math.dot(lines[i].Normal, velocity) < lines[i].Offset - Tol) return false;
            return true;
        }

        /// <summary>
        /// 暴力最优解：凸可行域（半平面 ∩ 速度圆）上离期望速度最近的点必在边界上，
        /// 边界由直线段与圆弧组成，故候选集 = 各直线上的垂足、直线两两交点、
        /// 直线与圆的交点、圆上沿期望方向点。
        /// </summary>
        private static bool BruteForce(NavOrcaLine[] lines, float maxSpeed, float3 preferred,
            out float3 result)
        {
            var candidates = new List<float3> { preferred };

            for (int i = 0; i < lines.Length; i++)
            {
                float3 direction = lines[i].Direction(PlaneZ);
                float3 point = lines[i].BoundaryPoint;

                candidates.Add(point + math.dot(direction, preferred - point) * direction);

                float projection = math.dot(point, direction);
                float constant = math.lengthsq(point) - maxSpeed * maxSpeed;
                float discriminant = projection * projection - constant;
                if (discriminant >= 0f)
                {
                    float root = math.sqrt(discriminant);
                    candidates.Add(point + (-projection - root) * direction);
                    candidates.Add(point + (-projection + root) * direction);
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                for (int j = i + 1; j < lines.Length; j++)
                {
                    float3 directionI = lines[i].Direction(PlaneZ);
                    float3 directionJ = lines[j].Direction(PlaneZ);
                    float determinant = math.dot(math.cross(directionI, directionJ), PlaneZ);
                    if (math.abs(determinant) < 1e-6f) continue;

                    float t = math.dot(math.cross(
                        lines[j].BoundaryPoint - lines[i].BoundaryPoint, directionJ), PlaneZ) / determinant;
                    candidates.Add(lines[i].BoundaryPoint + t * directionI);
                }
            }

            float preferredLength = math.length(preferred);
            if (preferredLength > 1e-6f)
                candidates.Add(preferred * (maxSpeed / preferredLength));

            result = float3.zero;
            float bestDistanceSq = float.MaxValue;
            bool found = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!Feasible(lines, candidates[i], maxSpeed)) continue;
                float distanceSq = math.distancesq(candidates[i], preferred);
                if (distanceSq >= bestDistanceSq) continue;
                bestDistanceSq = distanceSq;
                result = candidates[i];
                found = true;
            }

            return found;
        }

        private static bool Solve(NavOrcaLine[] lines, int obstacleLineCount, float maxSpeed,
            float3 preferred, out float3 result)
        {
            fixed (NavOrcaLine* linePtr = lines)
            {
                NavOrcaLine* scratch = stackalloc NavOrcaLine[lines.Length + 1];
                return NavLinearProgram2D.Solve(linePtr, lines.Length, obstacleLineCount, maxSpeed,
                    preferred, CollisionDimension.XY, scratch, out result);
            }
        }

        [Test]
        public void NoConstraints_ClampsPreferredToMaxSpeed()
        {
            var lines = new NavOrcaLine[0];
            Solve(lines, 0, maxSpeed: 2f, preferred: new float3(10f, 0f, 0f), out float3 result);

            Assert.That(math.length(result), Is.EqualTo(2f).Within(Tol));
            Assert.That(result.x, Is.EqualTo(2f).Within(Tol));
        }

        [Test]
        public void PreferredInsideSpeedLimit_IsReturnedUnchanged()
        {
            var lines = new NavOrcaLine[0];
            Solve(lines, 0, maxSpeed: 5f, preferred: new float3(1f, -2f, 0f), out float3 result);

            Assert.That(math.distance(result, new float3(1f, -2f, 0f)), Is.LessThan(Tol));
        }

        [Test]
        public void FeasiblePreferred_IsKeptWhenConstraintsAllow()
        {
            // direction 取平面内 -Y 时法线为 +X，即约束 v.x >= 0；期望速度 (1,0) 已满足
            var lines = new[] { Line(new float3(0f, -1f, 0f), float3.zero) };
            bool ok = Solve(lines, 0, 5f, new float3(1f, 0f, 0f), out float3 result);

            Assert.That(ok, Is.True);
            Assert.That(math.distance(result, new float3(1f, 0f, 0f)), Is.LessThan(Tol));
        }

        [Test]
        public void ViolatedConstraint_ProjectsOntoBoundary()
        {
            // 约束 v.x >= 1，期望速度 (0,0)：最优解是边界上离原点最近的点 (1,0)
            var lines = new[] { new NavOrcaLine { Normal = new float3(1f, 0f, 0f), Offset = 1f } };
            bool ok = Solve(lines, 0, 5f, float3.zero, out float3 result);

            Assert.That(ok, Is.True);
            Assert.That(math.distance(result, new float3(1f, 0f, 0f)), Is.LessThan(Tol));
        }

        [Test]
        public void TwoConstraints_MatchBruteForce()
        {
            var lines = new[]
            {
                new NavOrcaLine { Normal = new float3(1f, 0f, 0f), Offset = 1f },
                new NavOrcaLine { Normal = new float3(0f, 1f, 0f), Offset = 0.5f },
            };
            Solve(lines, 0, 5f, float3.zero, out float3 result);

            Assert.That(BruteForce(lines, 5f, float3.zero, out float3 expected), Is.True);
            Assert.That(math.distance(result, expected), Is.LessThan(Tol * 10f), $"got {result}, want {expected}");
        }

        [Test]
        public void RandomSets_MatchBruteForceAndSatisfyAllConstraints()
        {
            const int caseCount = 300;
            var random = new Random(20260916);
            int checkedCases = 0;

            for (int c = 0; c < caseCount; c++)
            {
                int lineCount = random.NextInt(1, 7);
                var lines = new NavOrcaLine[lineCount];
                for (int i = 0; i < lineCount; i++)
                {
                    float angle = random.NextFloat(0f, math.PI * 2f);
                    float3 direction = new float3(math.cos(angle), math.sin(angle), 0f);
                    float3 point = new float3(
                        random.NextFloat(-2f, 2f), random.NextFloat(-2f, 2f), 0f);
                    lines[i] = Line(direction, point);
                }

                float maxSpeed = random.NextFloat(0.5f, 4f);
                float3 preferred = new float3(
                    random.NextFloat(-3f, 3f), random.NextFloat(-3f, 3f), 0f);

                if (!BruteForce(lines, maxSpeed, preferred, out float3 expected)) continue;
                checkedCases++;

                bool ok = Solve(lines, 0, maxSpeed, preferred, out float3 result);
                Assert.That(ok, Is.True, $"case {c}: 可行域非空却求解失败");
                Assert.That(math.distance(result, expected), Is.LessThan(Tol * 10f),
                    $"case {c}: got {result}, want {expected}");
                Assert.That(Feasible(lines, result, maxSpeed), Is.True, $"case {c}: 结果越出约束");
            }

            Assert.That(checkedCases, Is.GreaterThan(50), "有效样本过少，对拍无意义");
        }

        [Test]
        public void ContradictoryConstraints_ReturnFiniteInLimitResult()
        {
            // 两条互斥的「障碍」约束（v.x >= 10 与 v.x <= -10）在 |v| <= 5 内不可同时满足：
            // 回退路径不得崩溃，且结果仍在速度上限内。
            var lines = new[]
            {
                new NavOrcaLine { Normal = new float3(1f, 0f, 0f), Offset = 10f },
                new NavOrcaLine { Normal = new float3(-1f, 0f, 0f), Offset = 10f },
            };
            bool ok = Solve(lines, 2, 5f, float3.zero, out float3 result);

            Assert.That(ok, Is.False, "不可行时必须如实报告，不得谎称满足");
            Assert.That(math.length(result), Is.LessThanOrEqualTo(5f + Tol));
            Assert.That(math.all(math.isfinite(result)), Is.True);
        }

        [Test]
        public void SameInput_IsBitIdentical()
        {
            var lines = new[]
            {
                Line(new float3(0.3f, 0.95f, 0f), new float3(1.5f, -0.25f, 0f)),
                Line(new float3(-0.8f, 0.6f, 0f), new float3(-0.5f, 1f, 0f)),
                Line(new float3(1f, 0f, 0f), new float3(0.25f, 0.75f, 0f)),
            };

            Solve(lines, 0, 3f, new float3(-2f, 1f, 0f), out float3 first);
            Solve(lines, 0, 3f, new float3(-2f, 1f, 0f), out float3 second);

            Assert.That(second.x, Is.EqualTo(first.x));
            Assert.That(second.y, Is.EqualTo(first.y));
            Assert.That(second.z, Is.EqualTo(first.z));
        }

        [Test]
        public void XzPlane_SolvesInTheHorizontalPlane()
        {
            // XZ 平面（法线 Y）：约束 v.x >= 1，期望速度沿 -X
            var lines = new[] { new NavOrcaLine { Normal = new float3(1f, 0f, 0f), Offset = 1f } };
            fixed (NavOrcaLine* linePtr = lines)
            {
                NavOrcaLine* scratch = stackalloc NavOrcaLine[1];
                bool ok = NavLinearProgram2D.Solve(linePtr, 1, 0, 5f, new float3(-3f, 7f, 2f),
                    CollisionDimension.XZ, scratch, out float3 result);

                Assert.That(ok, Is.True);
                Assert.That(result.x, Is.EqualTo(1f).Within(Tol));
                Assert.That(result.y, Is.EqualTo(0f).Within(Tol), "XZ 平面求解不得引入法线分量");
                Assert.That(result.z, Is.EqualTo(2f).Within(Tol));
            }
        }
    }
}
