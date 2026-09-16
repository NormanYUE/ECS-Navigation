using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 距离场采样：等级→世界距离换算、中心差分梯度方向、越界与空场退化。
    /// </summary>
    [TestFixture]
    public unsafe class NavDistanceFieldTests
    {
        private const float Tol = 1e-3f;
        private const float MaxRadius = 8f;

        private static NavGrid MakeGrid() => new()
        {
            Origin = float3.zero,
            VoxelSize = 1f,
            Dimensions = new int3(8, 8, 1),
            TileSize = 4,
        };

        /// <summary>把「到 x = 0 平面的距离」写进 8 位等级场。</summary>
        private static TestMemory MakePlanarField(NavGrid grid, float maxRadius)
        {
            var memory = TestMemory.Alloc((int)grid.VoxelCount);
            byte* field = (byte*)memory.Ptr;
            for (int z = 0; z < grid.Dimensions.z; z++)
            for (int y = 0; y < grid.Dimensions.y; y++)
            for (int x = 0; x < grid.Dimensions.x; x++)
            {
                float worldDistance = math.min(x * grid.VoxelSize, maxRadius);
                field[grid.VoxelIndex(new int3(x, y, z))] =
                    (byte)math.round(worldDistance / maxRadius * 255f);
            }

            return memory;
        }

        [Test]
        public void DistanceAt_ConvertsLevelsToWorldUnits()
        {
            var grid = MakeGrid();
            using TestMemory memory = MakePlanarField(grid, MaxRadius);

            // x = 4 → 世界距离 4；等级 128/255 * 8 ≈ 4.016（量化误差）
            float distance = NavDistanceField.DistanceAt(grid, memory.Ptr, false, MaxRadius,
                new int3(4, 0, 0));

            Assert.That(distance, Is.EqualTo(4f).Within(0.05f));
        }

        [Test]
        public void DistanceAt_ClampsOutOfRangeVoxel()
        {
            var grid = MakeGrid();
            using TestMemory memory = MakePlanarField(grid, MaxRadius);

            float inside = NavDistanceField.DistanceAt(grid, memory.Ptr, false, MaxRadius,
                new int3(7, 0, 0));
            float beyond = NavDistanceField.DistanceAt(grid, memory.Ptr, false, MaxRadius,
                new int3(99, 0, 0));

            Assert.That(beyond, Is.EqualTo(inside).Within(Tol), "越界体素必须钳到网格内");
        }

        [Test]
        public void Sample_GradientPointsAwayFromObstacle()
        {
            var grid = MakeGrid();
            using TestMemory memory = MakePlanarField(grid, MaxRadius);

            bool ok = NavDistanceField.Sample(grid, memory.Ptr, false, MaxRadius,
                new float3(4.5f, 4.5f, 0f), out float distance, out float3 gradient);

            Assert.That(ok, Is.True);
            // 最近体素采样：取的是体素 (4,4,0) 存储的距离 4.0，不做插值
            Assert.That(distance, Is.EqualTo(4f).Within(0.05f));
            Assert.That(gradient.x, Is.GreaterThan(0.99f), "梯度应背离 x = 0 平面");
            Assert.That(math.abs(gradient.y), Is.LessThan(Tol));
            Assert.That(math.abs(gradient.z), Is.LessThan(Tol), "单层轴不得产生假梯度");
        }

        [Test]
        public void Sample_OutsideGrid_ReturnsFalse()
        {
            var grid = MakeGrid();
            using TestMemory memory = MakePlanarField(grid, MaxRadius);

            bool ok = NavDistanceField.Sample(grid, memory.Ptr, false, MaxRadius,
                new float3(-5f, 0f, 0f), out float distance, out float3 gradient);

            Assert.That(ok, Is.False);
            Assert.That(distance, Is.EqualTo(0f));
            Assert.That(gradient, Is.EqualTo(float3.zero));
        }

        [Test]
        public void Sample_SixteenBitField_UsesSameWorldScale()
        {
            var grid = MakeGrid();
            using TestMemory memory = TestMemory.Alloc((int)grid.VoxelCount * sizeof(ushort));
            ushort* field = (ushort*)memory.Ptr;
            for (int i = 0; i < grid.VoxelCount; i++)
            {
                int x = i % grid.Dimensions.x;
                field[i] = (ushort)math.round(math.min(x * grid.VoxelSize, MaxRadius) / MaxRadius * 65535f);
            }

            bool ok = NavDistanceField.Sample(grid, memory.Ptr, true, MaxRadius,
                new float3(4.5f, 4.5f, 0f), out float distance, out _);

            Assert.That(ok, Is.True);
            Assert.That(distance, Is.EqualTo(4f).Within(0.01f), "16 位场的世界刻度必须与 8 位一致");
        }

        [Test]
        public void Sample_NullField_ReturnsFalse()
        {
            var grid = MakeGrid();
            Assert.That(NavDistanceField.Sample(grid, null, false, MaxRadius, float3.zero,
                out _, out _), Is.False);
        }

        [Test]
        public void IsBlocked_TrueAtObstacleAndOutsideGrid()
        {
            var grid = MakeGrid();
            using TestMemory memory = MakePlanarField(grid, MaxRadius);

            Assert.That(NavDistanceField.IsBlocked(grid, memory.Ptr, false, MaxRadius,
                new float3(0.5f, 0.5f, 0f)), Is.True);
            Assert.That(NavDistanceField.IsBlocked(grid, memory.Ptr, false, MaxRadius,
                new float3(5.5f, 0.5f, 0f)), Is.False);
            Assert.That(NavDistanceField.IsBlocked(grid, memory.Ptr, false, MaxRadius,
                new float3(-5f, 0f, 0f)), Is.True, "网格外视为不可通行");
        }
    }
}
