using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 流场求解器对拍：与暴力 Dijkstra（数组扫描式）的距离值一致性、
    /// 代价层加权、不可走格隔离、分帧推进等价性、梯度方向正确性。
    /// </summary>
    [TestFixture]
    public unsafe class NavFlowFieldSolverTests
    {
        private const int Width = 12;
        private const int Height = 12;

        private static (NavGrid grid, TestMemory occ, TestMemory dist, TestMemory costs, TestMemory d, TestMemory hc, TestMemory hv)
            Setup(byte requiredLevel = 0)
        {
            var grid = new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 1f,
                Dimensions = new int3(Width, Height, 1),
                TileSize = 4,
            };
            long voxelCount = grid.VoxelCount;
            var occ = TestMemory.Alloc((voxelCount + 7) / 8);
            var dist = TestMemory.Alloc(voxelCount * sizeof(float));
            var costs = TestMemory.Alloc(voxelCount);
            var d = TestMemory.Alloc(voxelCount * sizeof(float));
            var hc = TestMemory.Alloc(voxelCount * sizeof(float));
            var hv = TestMemory.Alloc(voxelCount * sizeof(int));
            for (long i = 0; i < (voxelCount + 7) / 8; i++) occ.As<byte>()[i] = 0;
            for (long i = 0; i < voxelCount; i++) { dist.As<byte>()[i] = 255; costs.As<byte>()[i] = 85; }
            return (grid, occ, dist, costs, d, hc, hv);
        }

        private static NavFlowFieldSolver.Context MakeContext(
            (NavGrid grid, TestMemory occ, TestMemory dist, TestMemory costs, TestMemory d, TestMemory hc, TestMemory hv) s,
            byte requiredLevel = 0)
        {
            return new NavFlowFieldSolver.Context
            {
                Grid = s.grid,
                Connectivity = 8,
                Distances = s.d.As<float>(),
                DistanceLevels = s.dist.As<byte>(),
                Occupancy = s.occ.As<byte>(),
                Costs = s.costs.As<byte>(),
                HeapCosts = s.hc.As<float>(),
                HeapVoxels = s.hv.As<int>(),
                HeapCapacity = (int)s.grid.VoxelCount,
                HeapCount = 0,
                RequiredLevel = requiredLevel,
            };
        }

        private static void SetOccupied(NavGrid grid, byte* occ, int3 voxel)
        {
            long index = grid.VoxelIndex(voxel);
            occ[index >> 3] |= (byte)(1u << (int)(index & 7));
        }

        [Test]
        public void OpenGrid_DistancesMatchBruteForceDijkstra()
        {
            var s = Setup();
            var ctx = MakeContext(s);
            var target = new int3(Width - 1, Height - 1, 0);
            NavFlowFieldSolver.Reset(ref ctx, s.grid.VoxelCount);
            Assert.That(NavFlowFieldSolver.Seed(ref ctx, target), Is.True);
            Assert.That(NavFlowFieldSolver.Step(ref ctx, long.MaxValue), Is.True, "wavefront must drain");

            // 暴力 Dijkstra（数组扫描，独立于堆实现）。
            var brute = new float[s.grid.VoxelCount];
            for (long i = 0; i < s.grid.VoxelCount; i++) brute[i] = float.MaxValue;
            brute[s.grid.VoxelIndex(target)] = 0f;
            var visited = new bool[s.grid.VoxelCount];

            for (;;)
            {
                float best = float.MaxValue;
                int bestIndex = -1;
                for (int i = 0; i < brute.Length; i++)
                {
                    if (!visited[i] && brute[i] < best) { best = brute[i]; bestIndex = i; }
                }
                if (bestIndex < 0) break;
                visited[bestIndex] = true;

                int3 v = s.grid.VoxelCoord(bestIndex);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int3 n = v + new int3(dx, dy, 0);
                    if (n.x < 0 || n.y < 0 || n.x >= Width || n.y >= Height) continue;
                    int ni = s.grid.VoxelIndex(n);
                    if (visited[ni]) continue;
                    float step = math.length(new float2(dx, dy));
                    if (best + step < brute[ni]) brute[ni] = best + step;
                }
            }

            for (int i = 0; i < brute.Length; i++)
                Assert.That(ctx.Distances[i], Is.EqualTo(brute[i]).Within(1e-3f), $"voxel {i}");
        }

        [Test]
        public void Wall_BlocksWavefront_DetoursAround()
        {
            var s = Setup();
            var ctx = MakeContext(s);
            // 竖墙 x=6，y∈[2,10]，留顶部通道。
            for (int y = 2; y <= 10; y++) SetOccupied(s.grid, s.occ.As<byte>(), new int3(6, y, 0));

            var target = new int3(0, 5, 0);
            NavFlowFieldSolver.Reset(ref ctx, s.grid.VoxelCount);
            NavFlowFieldSolver.Seed(ref ctx, target);
            NavFlowFieldSolver.Step(ref ctx, long.MaxValue);

            // 右侧经顶部绕行可达，距离 > 直线 5。
            float right = NavFlowFieldSolver.DistanceAt(ref ctx, new int3(10, 5, 0));
            Assert.That(right, Is.GreaterThan(5f).And.LessThan(20f), "must detour over the top");

            // 墙内格不可走。
            Assert.That(NavFlowFieldSolver.DistanceAt(ref ctx, new int3(6, 5, 0)), Is.EqualTo(float.MaxValue));
        }

        [Test]
        public void CostLayer_SlowRegion_IncreasesDistanceThroughIt()
        {
            var s = Setup();
            // 左半代价 1x，右半代价 3x（byte 255）。
            for (int y = 0; y < Height; y++)
            for (int x = Width / 2; x < Width; x++)
                s.costs.As<byte>()[s.grid.VoxelIndex(new int3(x, y, 0))] = 255;

            var ctx = MakeContext(s);
            NavFlowFieldSolver.Reset(ref ctx, s.grid.VoxelCount);
            NavFlowFieldSolver.Seed(ref ctx, new int3(0, 0, 0));
            NavFlowFieldSolver.Step(ref ctx, long.MaxValue);

            // (0,0) → (4,0) 全 1x：距离 4。
            Assert.That(NavFlowFieldSolver.DistanceAt(ref ctx, new int3(4, 0, 0)),
                Is.EqualTo(4f).Within(1e-3f));
            // (4,0) → (8,0)：x=5 仍 1x，x=6..8 为 3x → +1+3+3+3 = +10。
            Assert.That(NavFlowFieldSolver.DistanceAt(ref ctx, new int3(8, 0, 0)),
                Is.EqualTo(14f).Within(1e-3f));
        }

        [Test]
        public void FractionalSteps_EqualToFullRun()
        {
            var s = Setup();
            var ctx = MakeContext(s);
            var target = new int3(Width - 1, Height - 1, 0);

            NavFlowFieldSolver.Reset(ref ctx, s.grid.VoxelCount);
            NavFlowFieldSolver.Seed(ref ctx, target);
            bool done = false;
            for (int i = 0; i < 10000 && !done; i++)
                done = NavFlowFieldSolver.Step(ref ctx, 3); // 每次只弹 3 个
            Assert.That(done, Is.True, "fractional stepping must finish");

            var s2 = Setup();
            var ctx2 = MakeContext(s2);
            NavFlowFieldSolver.Reset(ref ctx2, s2.grid.VoxelCount);
            NavFlowFieldSolver.Seed(ref ctx2, target);
            NavFlowFieldSolver.Step(ref ctx2, long.MaxValue);

            for (long i = 0; i < s.grid.VoxelCount; i++)
                Assert.That(ctx.Distances[i], Is.EqualTo(ctx2.Distances[i]).Within(1e-4f),
                    $"voxel {i} fractional mismatch");
        }

        [Test]
        public void Gradient_LeadsTowardTarget_Monotonic()
        {
            var s = Setup();
            var ctx = MakeContext(s);
            var target = new int3(11, 11, 0);
            NavFlowFieldSolver.Reset(ref ctx, s.grid.VoxelCount);
            NavFlowFieldSolver.Seed(ref ctx, target);
            NavFlowFieldSolver.Step(ref ctx, long.MaxValue);

            // 从 (0,0) 沿梯度走 40 步必到目标。
            int3 current = new(0, 0, 0);
            for (int step = 0; step < 40; step++)
            {
                if (current.Equals(target)) return;
                Assert.That(NavFlowFieldSolver.TryGetNext(ref ctx, current, out int3 next), Is.True,
                    $"stuck at {current} step {step}");
                current = next;
            }
            Assert.That(current.Equals(target), Is.True, "gradient walk must reach target");
        }

        [Test]
        public void RequiredLevel_ErodesNarrowPassage()
        {
            var s = Setup();
            // 全部可走，但把一条通道的距离场等级压到 10，其余 255。
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                s.dist.As<byte>()[s.grid.VoxelIndex(new int3(x, y, 0))] = 255;
            for (int y = 4; y <= 6; y++) s.dist.As<byte>()[s.grid.VoxelIndex(new int3(6, y, 0))] = 10;

            var ctx = MakeContext(s, requiredLevel: 100);
            NavFlowFieldSolver.Reset(ref ctx, s.grid.VoxelCount);
            NavFlowFieldSolver.Seed(ref ctx, new int3(11, 5, 0));
            NavFlowFieldSolver.Step(ref ctx, long.MaxValue);

            // 左侧被侵蚀带隔离（经顶部绕行 —— 但带只压了 y∈[4,6]，上方可绕）。
            float left = NavFlowFieldSolver.DistanceAt(ref ctx, new int3(0, 5, 0));
            Assert.That(left, Is.GreaterThan(11f), "eroded band must force a detour");
        }
    }
}
