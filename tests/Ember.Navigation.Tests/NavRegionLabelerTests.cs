using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 连通区域标记：墙分隔 / 对角连通（4 vs 8）/ 占据格 -1 / 幂等确定性。
    /// </summary>
    [TestFixture]
    public unsafe class NavRegionLabelerTests
    {
        private static (NavGrid grid, TestMemory occupancy, TestMemory parent, TestMemory region) Setup(int3 dims)
        {
            var grid = new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 1f,
                Dimensions = dims,
                TileSize = 4,
            };
            long voxelCount = grid.VoxelCount;
            var occupancy = TestMemory.Alloc((voxelCount + 7) / 8);
            var parent = TestMemory.Alloc(voxelCount * sizeof(int));
            var region = TestMemory.Alloc(voxelCount * sizeof(int));
            for (long i = 0; i < (voxelCount + 7) / 8; i++) occupancy.As<byte>()[i] = 0;
            return (grid, occupancy, parent, region);
        }

        private static void SetOccupied(NavGrid grid, byte* occupancy, int3 voxel)
        {
            long index = grid.VoxelIndex(voxel);
            occupancy[index >> 3] |= (byte)(1u << (int)(index & 7));
        }

        private static int GetRegion(NavGrid grid, int* regionIds, int3 voxel) =>
            regionIds[grid.VoxelIndex(voxel)];

        [Test]
        public void EmptyGrid_IsSingleRegion()
        {
            var (grid, occ, parent, region) = Setup(new int3(8, 8, 1));
            int count = NavRegionLabeler.Label(in grid, 8, occ.As<byte>(), parent.As<int>(), region.As<int>());

            Assert.That(count, Is.EqualTo(1));
            for (long i = 0; i < grid.VoxelCount; i++)
                Assert.That(region.As<int>()[i], Is.EqualTo(0));
        }

        [Test]
        public void Wall_SeparatesIntoTwoRegions()
        {
            var (grid, occ, parent, region) = Setup(new int3(8, 8, 1));
            // y=3 行整行占据 → 上下两个房间。
            for (int x = 0; x < 8; x++) SetOccupied(grid, occ.As<byte>(), new int3(x, 3, 0));

            int count = NavRegionLabeler.Label(in grid, 4, occ.As<byte>(), parent.As<int>(), region.As<int>());

            Assert.That(count, Is.EqualTo(2));
            int top = GetRegion(grid, region.As<int>(), new int3(0, 0, 0));
            int bottom = GetRegion(grid, region.As<int>(), new int3(0, 7, 0));
            Assert.That(top, Is.Not.EqualTo(bottom));
            // 占据格 = -1。
            Assert.That(GetRegion(grid, region.As<int>(), new int3(4, 3, 0)), Is.EqualTo(-1));
        }

        [Test]
        public void DiagonalGap_4ConnectivityIsolatesCorners_8Connects()
        {
            // 3×3 十字墙：占据 (0,1)(1,0)(1,2)(2,1)。四个角格与中心 (1,1)。
            // 4 邻接：角格正交邻居全占据 → 各自孤立（5 个区域）。
            // 8 邻接：角格斜进中心 → 全部连通（1 个区域）。
            var (grid, occ, parent, region) = Setup(new int3(3, 3, 1));
            SetOccupied(grid, occ.As<byte>(), new int3(0, 1, 0));
            SetOccupied(grid, occ.As<byte>(), new int3(1, 0, 0));
            SetOccupied(grid, occ.As<byte>(), new int3(1, 2, 0));
            SetOccupied(grid, occ.As<byte>(), new int3(2, 1, 0));

            int count4 = NavRegionLabeler.Label(in grid, 4, occ.As<byte>(), parent.As<int>(), region.As<int>());
            Assert.That(count4, Is.EqualTo(5), "4-connectivity: each corner is its own region");

            for (long i = 0; i < grid.VoxelCount; i++) parent.As<int>()[i] = 0;
            int count8 = NavRegionLabeler.Label(in grid, 8, occ.As<byte>(), parent.As<int>(), region.As<int>());
            Assert.That(count8, Is.EqualTo(1), "8-connectivity: corners connect diagonally through center");
        }

        [Test]
        public void SameInput_TwiceRun_IdenticalOutput()
        {
            var (grid, occ, parent, region) = Setup(new int3(8, 8, 1));
            for (int x = 0; x < 8; x++) SetOccupied(grid, occ.As<byte>(), new int3(x, 3, 0));
            SetOccupied(grid, occ.As<byte>(), new int3(2, 5, 0));

            NavRegionLabeler.Label(in grid, 8, occ.As<byte>(), parent.As<int>(), region.As<int>());
            var first = new int[grid.VoxelCount];
            for (long i = 0; i < grid.VoxelCount; i++) first[i] = region.As<int>()[i];

            NavRegionLabeler.Label(in grid, 8, occ.As<byte>(), parent.As<int>(), region.As<int>());
            for (long i = 0; i < grid.VoxelCount; i++)
                Assert.That(region.As<int>()[i], Is.EqualTo(first[i]), $"voxel {i} nondeterministic");
        }
    }
}
