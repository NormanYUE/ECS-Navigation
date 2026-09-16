using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 飞行 ORCA：三维约束构建（RVO2-3D 语义）与三维线性规划的精确性对拍。
    /// 最优性用「随机可行采样找不到更近点」佐证 —— 顶点枚举若能给出次优解，
    /// 细密采样有相当概率命中更优的可行点。
    /// </summary>
    [TestFixture]
    public unsafe class NavOrca3DTests
    {
        private const float Tol = 1e-4f;

        private static void AssertVec3(float3 actual, float3 expected, string what) =>
            Assert.That(math.distance(actual, expected), Is.LessThan(Tol), $"{what}: got {actual}, want {expected}");

        // ---- 约束构建 ----

        [Test]
        public void HeadOn_MatchesHandComputedLine()
        {
            // A(0,0,0) v=(1,0,0) r=0.5；B(2,0,0) v=(-1,0,0) r=0.5；τ=2
            // rp=(2,0,0) rv=(2,0,0) cr=1：落在锥内且判别式为 0（退化双根）
            // t = (b + √(b²-ac))/a = 1；w = rv - t·rp = 0 → 方向退化为兜底轴 (1,0,0)
            // u = (cr·t - 0)·dir = (1,0,0)；point = v + 0.5u = (1.5,0,0)
            // 约束：dot(v, (1,0,0)) >= 1.5
            bool built = NavOrcaMath3D.Build(
                position: float3.zero, velocity: new float3(1f, 0f, 0f), radius: 0.5f,
                neighborPosition: new float3(2f, 0f, 0f), neighborVelocity: new float3(-1f, 0f, 0f),
                neighborRadius: 0.5f, timeHorizon: 2f, timeStep: 0.25f, responsibility: 0.5f,
                out NavOrcaLine line);

            Assert.That(built, Is.True);
            AssertVec3(line.Normal, new float3(1f, 0f, 0f), "normal");
            Assert.That(line.Offset, Is.EqualTo(1.5f).Within(Tol));
        }

        [Test]
        public void ZeroPlaneNormal_RoutesToThreeDimensionalBuilder()
        {
            // 同一输入下零法线（三维）与非零法线（二维）必须给出不同结果，
            // 否则说明分派没生效。
            NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 0f, 1f), 0.5f,
                new float3(2f, 0f, 0f), new float3(-1f, 0f, 0f), 0.5f,
                2f, 0.25f, float3.zero, out NavOrcaLine threeDimensional);

            NavOrcaMath.AgentConstraint(
                float3.zero, new float3(1f, 0f, 1f), 0.5f,
                new float3(2f, 0f, 0f), new float3(-1f, 0f, 0f), 0.5f,
                2f, 0.25f, new float3(0f, 0f, 1f), out NavOrcaLine planar);

            Assert.That(math.length(threeDimensional.Normal), Is.EqualTo(1f).Within(Tol));
            Assert.That(math.length(planar.Normal), Is.EqualTo(1f).Within(Tol));
            Assert.That(math.distance(threeDimensional.Normal, planar.Normal), Is.GreaterThan(Tol));
            Assert.That(planar.Normal.z, Is.EqualTo(0f).Within(Tol), "平面约束的法线必须落在平面内");
        }

        // ---- 线性规划 ----

        private static bool Solve(NavOrcaLine[] lines, float maxSpeed, float3 preferred, out float3 result)
        {
            fixed (NavOrcaLine* pointer = lines)
                return NavLinearProgram3D.Solve(pointer, lines.Length, maxSpeed, preferred, out result);
        }

        private static bool Feasible(NavOrcaLine[] lines, float3 velocity, float maxSpeed)
        {
            if (math.length(velocity) > maxSpeed + 1e-3f) return false;
            for (int i = 0; i < lines.Length; i++)
                if (math.dot(lines[i].Normal, velocity) < lines[i].Offset - 1e-3f) return false;
            return true;
        }

        [Test]
        public void NoConstraints_ClampsPreferredToMaxSpeed()
        {
            Solve(new NavOrcaLine[0], 2f, new float3(10f, 0f, 0f), out float3 result);
            AssertVec3(result, new float3(2f, 0f, 0f), "result");
        }

        [Test]
        public void FeasiblePreferred_IsKept()
        {
            var lines = new[] { new NavOrcaLine { Normal = new float3(0f, 0f, 1f), Offset = -1f } };
            Assert.That(Solve(lines, 5f, new float3(1f, 2f, 3f), out float3 result), Is.True);
            AssertVec3(result, new float3(1f, 2f, 3f), "result");
        }

        [Test]
        public void ThreeOrthogonalPlanes_ResolveToCorner()
        {
            // x>=1, y>=2, z>=3 与 |v|<=10 的可行域在角点 (1,2,3) 上离原点最近
            var lines = new[]
            {
                new NavOrcaLine { Normal = new float3(1f, 0f, 0f), Offset = 1f },
                new NavOrcaLine { Normal = new float3(0f, 1f, 0f), Offset = 2f },
                new NavOrcaLine { Normal = new float3(0f, 0f, 1f), Offset = 3f },
            };
            Assert.That(Solve(lines, 10f, float3.zero, out float3 result), Is.True);
            AssertVec3(result, new float3(1f, 2f, 3f), "corner");
        }

        [Test]
        public void PreferredBeyondSphereWithPlane_ResolvesOnSpherePlaneCircle()
        {
            // 期望速度 (0,0,10) 越出速度球，且被 x >= 0.5 挡住：
            // 最优解落在「平面 ∩ 速度球」的交圆上，即 (0.5, 0, √(4-0.25))
            var lines = new[] { new NavOrcaLine { Normal = new float3(1f, 0f, 0f), Offset = 0.5f } };
            Assert.That(Solve(lines, 2f, new float3(0f, 0f, 10f), out float3 result), Is.True);

            Assert.That(math.length(result), Is.EqualTo(2f).Within(1e-3f));
            Assert.That(result.x, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(result.z, Is.EqualTo(math.sqrt(3.75f)).Within(1e-3f));
            Assert.That(result.y, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void InfeasibleSet_ReportsFailureInsteadOfClaimingSuccess()
        {
            // v.x >= 5 与 v.x <= -5 在 |v| <= 1 内无解
            var lines = new[]
            {
                new NavOrcaLine { Normal = new float3(1f, 0f, 0f), Offset = 5f },
                new NavOrcaLine { Normal = new float3(-1f, 0f, 0f), Offset = 5f },
            };

            Assert.That(Solve(lines, 1f, float3.zero, out float3 result), Is.False);
            Assert.That(math.all(math.isfinite(result)), Is.True);
            Assert.That(math.length(result), Is.LessThanOrEqualTo(1f + Tol));
        }

        [Test]
        public void RandomSets_AreFeasibleAndNotBeatenByDenseSampling()
        {
            const int caseCount = 120;
            const int sampleCount = 4000;
            var random = new Random(20260916);
            int checkedCases = 0;

            for (int c = 0; c < caseCount; c++)
            {
                float maxSpeed = random.NextFloat(0.5f, 4f);
                float3 preferred = new float3(
                    random.NextFloat(-3f, 3f), random.NextFloat(-3f, 3f), random.NextFloat(-3f, 3f));

                // 以速度球内的锚点构造约束，保证可行域非空 —— 否则测的是空集行为，不是最优性。
                // 锚点必须严格在球内：三个分量各取 ±0.7·maxSpeed 时模长可达 1.21·maxSpeed。
                float3 anchorDirection = math.normalize(new float3(
                    random.NextFloat(-1f, 1f), random.NextFloat(-1f, 1f), random.NextFloat(-1f, 1f))
                    + new float3(0.01f));
                float3 anchor = anchorDirection * (maxSpeed * 0.7f * random.NextFloat(0f, 1f));

                int lineCount = random.NextInt(1, 7);
                var lines = new NavOrcaLine[lineCount];
                for (int i = 0; i < lineCount; i++)
                {
                    float3 normal = math.normalize(new float3(
                        random.NextFloat(-1f, 1f), random.NextFloat(-1f, 1f), random.NextFloat(-1f, 1f))
                        + new float3(0.01f));
                    lines[i] = new NavOrcaLine
                    {
                        Normal = normal,
                        Offset = math.dot(normal, anchor) - random.NextFloat(0f, 0.5f),
                    };
                }

                bool solved = Solve(lines, maxSpeed, preferred, out float3 result);
                Assert.That(solved, Is.True, $"case {c}: 构造出的可行域非空，不该报告无解");
                Assert.That(Feasible(lines, result, maxSpeed), Is.True, $"case {c}: 结果越出约束");

                float resultDistance = math.distancesq(result, preferred);
                for (int s = 0; s < sampleCount; s++)
                {
                    float3 sample = new float3(
                        random.NextFloat(-maxSpeed, maxSpeed),
                        random.NextFloat(-maxSpeed, maxSpeed),
                        random.NextFloat(-maxSpeed, maxSpeed));
                    if (!Feasible(lines, sample, maxSpeed)) continue;

                    Assert.That(math.distancesq(sample, preferred), Is.GreaterThanOrEqualTo(resultDistance - 1e-4f),
                        $"case {c}: 采样点 {sample} 比求解结果 {result} 更接近期望速度");
                }

                checkedCases++;
            }

            Assert.That(checkedCases, Is.EqualTo(caseCount));
        }

        [Test]
        public void SameInput_IsBitIdentical()
        {
            var lines = new[]
            {
                new NavOrcaLine { Normal = math.normalize(new float3(0.3f, 0.5f, 0.8f)), Offset = 0.4f },
                new NavOrcaLine { Normal = math.normalize(new float3(-0.7f, 0.2f, 0.1f)), Offset = -0.2f },
            };

            Solve(lines, 3f, new float3(-1f, 2f, 0.5f), out float3 first);
            Solve(lines, 3f, new float3(-1f, 2f, 0.5f), out float3 second);

            Assert.That(second.x, Is.EqualTo(first.x));
            Assert.That(second.y, Is.EqualTo(first.y));
            Assert.That(second.z, Is.EqualTo(first.z));
        }
    }
}
