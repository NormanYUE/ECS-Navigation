using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>NavAStar 隔离测试：开放网格 / 障碍绕行 / 簇约束。</summary>
    [TestFixture]
    public unsafe class NavAStarTests
    {
        private const int W = 12;

        private struct Fx : System.IDisposable
        {
            public NavGrid Grid;
            public TestMemory Occ, Levels, Costs, G, Parent, HeapF, HeapV, Nodes;

            public void Dispose()
            {
                Occ.Dispose(); Levels.Dispose(); Costs.Dispose(); G.Dispose();
                Parent.Dispose(); HeapF.Dispose(); HeapV.Dispose(); Nodes.Dispose();
            }
        }

        private static Fx Setup()
        {
            var grid = new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 1f,
                Dimensions = new int3(W, W, 1),
                TileSize = W,
            };
            long vc = grid.VoxelCount;
            var fx = new Fx
            {
                Grid = grid,
                Occ = TestMemory.Alloc((vc + 7) / 8),
                Levels = TestMemory.Alloc(vc),
                Costs = TestMemory.Alloc(vc),
                G = TestMemory.Alloc(vc * sizeof(float)),
                Parent = TestMemory.Alloc(vc * sizeof(int)),
                HeapF = TestMemory.Alloc(vc * 8 * sizeof(float)),
                HeapV = TestMemory.Alloc(vc * 8 * sizeof(int)),
                Nodes = TestMemory.Alloc(vc * sizeof(int)),
            };
            for (long i = 0; i < (vc + 7) / 8; i++) fx.Occ.As<byte>()[i] = 0;
            for (long i = 0; i < vc; i++)
            {
                fx.Levels.As<byte>()[i] = 255;
                fx.Costs.As<byte>()[i] = 85;
                fx.Nodes.As<int>()[i] = 0;
            }
            return fx;
        }

        private static NavAStar.Context Ctx(Fx fx, int3 goal)
        {
            return new NavAStar.Context
            {
                Grid = fx.Grid,
                Connectivity = 8,
                Occupancy = fx.Occ.As<byte>(),
                DistanceLevels = fx.Levels.As<byte>(),
                Costs = fx.Costs.As<byte>(),
                G = fx.G.As<float>(),
                Parent = fx.Parent.As<int>(),
                VoxelNodes = fx.Nodes.As<int>(),
                HeapF = fx.HeapF.As<float>(),
                HeapVoxels = fx.HeapV.As<int>(),
                HeapCapacity = (int)fx.Grid.VoxelCount * 8,
                HeapCount = 0,
                RequiredLevel = 0,
                Goal = goal,
                RestrictNode = -1,
            };
        }

        [Test]
        public void OpenGrid_FindsStraightPath()
        {
            using var fx = Setup();
            var ctx = Ctx(fx, new int3(10, 10, 0));
            NavAStar.Reset(ref ctx, fx.Grid.VoxelCount);
            Assert.That(NavAStar.Begin(ref ctx, new int3(1, 1, 0)), Is.True);
            Assert.That(NavAStar.RunToCompletion(ref ctx), Is.True, "must reach goal");

            using var wp = TestMemory.Alloc(256 * sizeof(int3));
            int count = NavAStar.ExtractPath(ref ctx, new int3(1, 1, 0), new int3(10, 10, 0),
                wp.As<int3>(), 256);
            Assert.That(count, Is.GreaterThan(1));
            Assert.That(wp.As<int3>()[0], Is.EqualTo(new int3(1, 1, 0)));
            Assert.That(wp.As<int3>()[count - 1], Is.EqualTo(new int3(10, 10, 0)));
        }

        [Test]
        public void RestrictedToCluster_ReachableInsideCluster()
        {
            using var fx = Setup();
            // 墙：x=6, y∈[3,9]，顶部留路（y=10..11 通）。
            for (int y = 3; y <= 9; y++)
            {
                long idx = fx.Grid.VoxelIndex(new int3(6, y, 0));
                fx.Occ.As<byte>()[idx >> 3] |= (byte)(1u << (int)(idx & 7));
            }

            var ctx = Ctx(fx, new int3(9, 6, 0));
            ctx.RestrictNode = 0;
            NavAStar.Reset(ref ctx, fx.Grid.VoxelCount);
            Assert.That(NavAStar.Begin(ref ctx, new int3(2, 6, 0)), Is.True);
            Assert.That(NavAStar.RunToCompletion(ref ctx), Is.True, "detour over the top must stay in cluster");

            using var wp = TestMemory.Alloc(256 * sizeof(int3));
            int count = NavAStar.ExtractPath(ref ctx, new int3(2, 6, 0), new int3(9, 6, 0),
                wp.As<int3>(), 256);
            Assert.That(count, Is.GreaterThan(1));
        }
    }
}
