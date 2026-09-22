using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// NavPathSmoother 隔离测试。重点是<b>通视判定的采样盲区</b>：
    /// 线段只要在某个不可走体素里掠过一小段（比采样步长短），点采样就会整体跨过去。
    /// </summary>
    [TestFixture]
    public unsafe class NavPathSmootherTests
    {
        private const int W = 8;
        private const float R = 0.30f;

        private struct Fx : System.IDisposable
        {
            public NavGrid Grid;
            public TestMemory Occ, Levels;

            public void Dispose()
            {
                Occ.Dispose();
                Levels.Dispose();
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
            };
            for (long i = 0; i < (vc + 7) / 8; i++) fx.Occ.As<byte>()[i] = 0;
            // 255 = 该格中心距障碍 1 个体素，够半径 0.3 通过。
            for (long i = 0; i < vc; i++) fx.Levels.As<byte>()[i] = 255;
            return fx;
        }

        /// <summary>把某格标成不可走：占据位集置位（距离场不动 —— 判据先看占据）。</summary>
        private static void Block(Fx fx, int3 voxel)
        {
            long idx = fx.Grid.VoxelIndex(voxel);
            fx.Occ.As<byte>()[idx >> 3] |= (byte)(1u << (int)(idx & 7));
        }

        private static int Level(float radius)
        {
            // 与 NavPathSystem.RequiredLevel 同口径：MaxBakeRadius = 1（本测试网格），
            // 距离场用 byte 满量程模拟，直接用「半径占一格的比例」换算。
            return (int)math.round(radius * 255f);
        }

        private static bool Los(Fx fx, int3 a, int3 b, int level)
        {
            return NavPathSmoother.HasLineOfSight(fx.Grid, fx.Occ.As<byte>(),
                fx.Levels.As<byte>(), level, a, b);
        }

        [Test]
        public void OpenGrid_DiagonalIsVisible()
        {
            using var fx = Setup();
            Assert.That(Los(fx, new int3(1, 1, 0), new int3(4, 4, 0), Level(R)), Is.True,
                "开阔地的对角步必须判通视，否则平滑器退化成逐格走");
        }

        [Test]
        public void OpenGrid_LongLineIsVisible()
        {
            using var fx = Setup();
            Assert.That(Los(fx, new int3(0, 0, 0), new int3(W - 1, W - 1, 0), Level(R)), Is.True);
        }

        /// <summary>
        /// <b>回归</b>：对角步的两个正交格都被堵，只有对角的两端可走。
        ///
        /// 线段恰好从四格共享的角上穿过，只在两个被堵格里各掠过「一个点」。
        /// 按体素边长一半取采样点（0.5 米一步）时，采样落在 1/3、2/3 处，
        /// 恰好跨过那个角 —— 判成通视，代理沿这条线走就会从两堵墙的夹角里穿过去。
        ///
        /// 这正是消费工程实测的形状：格对齐的对角步长 0.5×√2，中点精确落在
        /// 四格共享的角上，而 8 邻接的搜索会大量产出这种步。
        /// </summary>
        [Test]
        public void DiagonalSqueezingThroughBlockedCorner_IsNotVisible()
        {
            using var fx = Setup();
            Block(fx, new int3(2, 3, 0));
            Block(fx, new int3(3, 2, 0));

            Assert.That(Los(fx, new int3(2, 2, 0), new int3(3, 3, 0), Level(R)), Is.False,
                "从两堵墙的夹角斜切过去必须判不通视");
            Assert.That(Los(fx, new int3(3, 3, 0), new int3(2, 2, 0), Level(R)), Is.False,
                "反向也必须一致");
        }

        /// <summary>只堵一个正交格时同样不许斜切 —— 代理的圆盘会扫过它。</summary>
        [Test]
        public void DiagonalSqueezingPastOneBlockedNeighbor_IsNotVisible()
        {
            using var fx = Setup();
            Block(fx, new int3(3, 2, 0));

            Assert.That(Los(fx, new int3(2, 2, 0), new int3(3, 3, 0), Level(R)), Is.False);
        }

        /// <summary>正交步不受影响：中间格不存在，两端可走即通视。</summary>
        [Test]
        public void OrthogonalStep_UnaffectedByCornerRule()
        {
            using var fx = Setup();
            Block(fx, new int3(3, 2, 0));

            Assert.That(Los(fx, new int3(2, 2, 0), new int3(3, 2, 0), Level(R)), Is.False,
                "终点自己不可走，当然不通");
            Assert.That(Los(fx, new int3(2, 3, 0), new int3(4, 3, 0), Level(R)), Is.True,
                "水平两格、中间那格可走 —— 正交步不适用切角规则");
        }

        /// <summary>
        /// 端到端：A* 出的路径，相邻航点必须真的通视。
        ///
        /// 这是平滑器退回「最低等级」那趟兜底的前提（<c>PullString</c> 里写着
        /// 「相邻航点在同一等级的 A* 里必然相连」）。若 A* 允许切角，这条前提就破了 ——
        /// 兜底那趟也会通视失败，整条请求被判 Failed。
        /// </summary>
        [Test]
        public void AStarPath_AdjacentWaypointsAreAlwaysVisible()
        {
            using var fx = Setup();
            // 一道斜着留了「一格宽」缺口的墙：8 邻接会想从缺口两角斜切过去。
            for (int y = 0; y < W; y++)
            {
                if (y == 4) continue;
                Block(fx, new int3(4, y, 0));
            }

            using var heapF = TestMemory.Alloc(fx.Grid.VoxelCount * 8 * sizeof(float));
            using var heapV = TestMemory.Alloc(fx.Grid.VoxelCount * 8 * sizeof(int));
            using var gScore = TestMemory.Alloc(fx.Grid.VoxelCount * sizeof(float));
            using var parent = TestMemory.Alloc(fx.Grid.VoxelCount * sizeof(int));
            using var costs = TestMemory.Alloc(fx.Grid.VoxelCount);
            using var nodes = TestMemory.Alloc(fx.Grid.VoxelCount * sizeof(int));

            for (long i = 0; i < fx.Grid.VoxelCount; i++)
            {
                costs.As<byte>()[i] = 85;
                nodes.As<int>()[i] = 0;
            }

            var start = new int3(2, 4, 0);
            var goal = new int3(6, 4, 0);
            var ctx = new NavAStar.Context
            {
                Grid = fx.Grid,
                Connectivity = 8,
                Occupancy = fx.Occ.As<byte>(),
                DistanceLevels = fx.Levels.As<byte>(),
                Costs = costs.As<byte>(),
                G = gScore.As<float>(),
                Parent = parent.As<int>(),
                VoxelNodes = nodes.As<int>(),
                HeapF = heapF.As<float>(),
                HeapVoxels = heapV.As<int>(),
                HeapCapacity = (int)fx.Grid.VoxelCount * 8,
                HeapCount = 0,
                RequiredLevel = Level(R),
                Goal = goal,
                RestrictNode = -1,
            };

            NavAStar.Reset(ref ctx, fx.Grid.VoxelCount);
            Assert.That(NavAStar.Begin(ref ctx, start), Is.True);
            Assert.That(NavAStar.RunToCompletion(ref ctx), Is.True, "必须能绕到缺口");

            using var wp = TestMemory.Alloc(256 * sizeof(int3));
            int count = NavAStar.ExtractPath(ref ctx, start, goal, wp.As<int3>(), 256);
            Assert.That(count, Is.GreaterThan(1));

            int level = Level(R);
            for (int i = 0; i + 1 < count; i++)
            {
                Assert.That(Los(fx, wp.As<int3>()[i], wp.As<int3>()[i + 1], level), Is.True,
                    $"相邻航点 {i}→{i + 1} 必须通视，否则平滑器的兜底那趟会整条判 Failed");
            }
        }
    }
}
