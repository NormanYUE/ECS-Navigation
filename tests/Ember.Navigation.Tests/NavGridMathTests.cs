using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>网格坐标变换的往返一致性。</summary>
    [TestFixture]
    public class NavGridMathTests
    {
        [Test]
        public void VoxelIndexRoundTrip_IsIdentity()
        {
            var grid = new NavGrid
            {
                Origin = new float3(-10f, 0f, -5f),
                VoxelSize = 0.5f,
                Dimensions = new int3(40, 1, 30),
                TileSize = 8,
            };

            for (int z = 0; z < grid.Dimensions.z; z += 7)
            for (int y = 0; y < grid.Dimensions.y; y++)
            for (int x = 0; x < grid.Dimensions.x; x += 5)
            {
                int3 voxel = new(x, y, z);
                int index = grid.VoxelIndex(voxel);
                Assert.That(grid.VoxelCoord(index), Is.EqualTo(voxel));
            }
        }

        [Test]
        public void WorldToVoxel_MatchesFloorSemantics()
        {
            var grid = new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 0.5f,
                Dimensions = new int3(10, 10, 10),
                TileSize = 4,
            };

            Assert.That(grid.WorldToVoxel(new float3(0.0f, 0.0f, 0.0f)), Is.EqualTo(new int3(0, 0, 0)));
            Assert.That(grid.WorldToVoxel(new float3(0.49f, 0.99f, 1.01f)), Is.EqualTo(new int3(0, 1, 2)));
            Assert.That(grid.WorldToVoxel(new float3(-0.1f, 0f, 0f)), Is.EqualTo(new int3(-1, 0, 0)));
        }

        [Test]
        public void VoxelToWorld_CentersInCell()
        {
            var grid = new NavGrid
            {
                Origin = new float3(1f, 2f, 3f),
                VoxelSize = 2f,
                Dimensions = new int3(8, 8, 8),
                TileSize = 4,
            };

            float3 world = grid.VoxelToWorld(new int3(1, 2, 3));
            Assert.That(world, Is.EqualTo(new float3(1f + 3f, 2f + 5f, 3f + 7f)));
        }

        [Test]
        public void TileCounts_RoundUp()
        {
            var grid = new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 1f,
                Dimensions = new int3(9, 17, 33),
                TileSize = 8,
            };

            Assert.That(grid.TileCounts, Is.EqualTo(new int3(2, 3, 5)));
            Assert.That(grid.TileCount, Is.EqualTo(30));
        }

        [Test]
        public void InactiveAxis_2DGridDetected()
        {
            var gridXY = new NavGrid { Dimensions = new int3(16, 16, 1) };
            var gridXZ = new NavGrid { Dimensions = new int3(16, 1, 16) };
            var grid3D = new NavGrid { Dimensions = new int3(16, 16, 16) };

            Assert.That(gridXY.InactiveAxis, Is.EqualTo(2));
            Assert.That(gridXZ.InactiveAxis, Is.EqualTo(1));
            Assert.That(grid3D.InactiveAxis, Is.EqualTo(-1));
        }
    }
}
