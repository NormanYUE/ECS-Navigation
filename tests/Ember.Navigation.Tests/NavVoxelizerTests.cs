using System;
using Ember.Collision;
using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 体素化对拍：距离场 / 占据位集 vs 暴力逐体素 × 全碰撞体的 ShapeQuery 重算。
    /// 暴力路径不做 AABB 覆盖剔除，专抓覆盖范围的边界错误。
    /// </summary>
    [TestFixture]
    public unsafe class NavVoxelizerTests
    {
        private const float Tol = 1e-3f;

        private static NavBakeInput MakeInput()
        {
            return new NavBakeInput
            {
                Dimension = CollisionDimension.XY,
                VoxelSize = 0.5f,
                TileSize = 8,
                DistanceBits = 8,
                MaxBakeRadius = 4f,
                Connectivity = 8,
            };
        }

        private static NavGrid MakeGrid()
        {
            return new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 0.5f,
                Dimensions = new int3(24, 24, 1),
                TileSize = 8,
            };
        }

        private static NavBakeCollider[] MakeColliders()
        {
            // 盒（旋转）+ 圆 + 胶囊：覆盖多种形状与位姿组合。
            return new[]
            {
                new NavBakeCollider
                {
                    Collider = Collider.Box(new float3(1f, 0.5f, 0.5f), new float3(6f, 6f, 0f)),
                    Pose = new BodyPose
                    {
                        Position = float3.zero,
                        Rotation = quaternion.AxisAngle(new float3(0f, 0f, 1f), 0.6f),
                        Scale = 1f,
                    },
                    Filter = default,
                    Flags = 1,
                },
                new NavBakeCollider
                {
                    Collider = Collider.Circle(radius: 1.2f, center: new float3(4f, 8f, 0f)),
                    Pose = BodyPose.Identity,
                    Filter = default,
                    Flags = 1,
                },
                new NavBakeCollider
                {
                    Collider = Collider.Capsule2D(radius: 0.4f, halfHeight: 1.5f, axis: 0,
                        center: new float3(9f, 3f, 0f)),
                    Pose = BodyPose.Identity,
                    Filter = default,
                    Flags = 1,
                },
            };
        }

        [Test]
        public void DistanceField_MatchesBruteForceOracle()
        {
            var input = MakeInput();
            var grid = MakeGrid();
            var colliders = MakeColliders();
            long voxelCount = grid.VoxelCount;

            using var distance = TestMemory.Alloc(voxelCount * sizeof(float));
            using var occupancy = TestMemory.Alloc((voxelCount + 7) / 8);

            fixed (NavBakeCollider* colliderPtr = colliders)
            {
                NavVoxelizer.BakeDistanceField(in input, in grid, colliderPtr, colliders.Length,
                    null, 0, distance.As<float>(), occupancy.As<byte>());
            }

            for (int z = 0; z < grid.Dimensions.z; z++)
            for (int y = 0; y < grid.Dimensions.y; y++)
            for (int x = 0; x < grid.Dimensions.x; x++)
            {
                int3 voxel = new(x, y, z);
                long index = grid.VoxelIndex(voxel);
                float3 point = grid.VoxelToWorld(voxel);

                // 暴力 oracle：全部碰撞体逐一枚举（无覆盖剔除）。
                float expected = float.MaxValue;
                foreach (ref NavBakeCollider entry in colliders.AsSpan())
                {
                    float d = ShapeQuery.ClosestPoint(in entry.Collider, in entry.Pose,
                        input.Dimension, point, out _, out _);
                    if (d < expected) expected = d;
                }
                if (expected > input.MaxBakeRadius) expected = float.MaxValue; // 覆盖范围外保持饱和

                float actual = distance.As<float>()[index];
                if (expected >= float.MaxValue)
                {
                    Assert.That(actual, Is.GreaterThanOrEqualTo(input.MaxBakeRadius),
                        $"voxel {voxel} outside coverage must saturate");
                }
                else
                {
                    Assert.That(actual, Is.EqualTo(expected).Within(Tol),
                        $"voxel {voxel} distance mismatch (actual {actual}, expected {expected})");
                }

                bool occupiedExpected = expected <= 0f;
                bool occupiedActual = (occupancy.As<byte>()[index >> 3] & (1u << (int)(index & 7))) != 0;
                Assert.That(occupiedActual, Is.EqualTo(occupiedExpected), $"voxel {voxel} occupancy mismatch");
            }
        }

        [Test]
        public void Annotation_ForceWalkable_ClearsOccupancy()
        {
            var input = MakeInput();
            var grid = MakeGrid();
            var colliders = MakeColliders();
            long voxelCount = grid.VoxelCount;

            using var distance = TestMemory.Alloc(voxelCount * sizeof(float));
            using var occupancy = TestMemory.Alloc((voxelCount + 7) / 8);
            using var costs = TestMemory.Alloc(voxelCount * sizeof(float));

            var annotations = new[]
            {
                new NavBakeAnnotation
                {
                    Region = Aabb.FromCenterExtents(new float3(6f, 6f, 0f), new float3(1.5f, 1.5f, 1f)),
                    WalkableOverride = 1,
                    CostMultiplier = 3f,
                },
            };

            fixed (NavBakeCollider* colliderPtr = colliders)
            fixed (NavBakeAnnotation* annotationPtr = annotations)
            {
                NavVoxelizer.BakeDistanceField(in input, in grid, colliderPtr, colliders.Length,
                    null, 0, distance.As<float>(), occupancy.As<byte>());
                NavVoxelizer.ApplyAnnotations(in grid, annotationPtr, annotations.Length,
                    distance.As<float>(), occupancy.As<byte>(), costs.As<float>());

                // 标注盒内的体素：占据必须被清除，代价必须写入 3.0。
                int3 probe = grid.WorldToVoxel(new float3(6f, 6f, 0f));
                long probeIndex = grid.VoxelIndex(probe);
                Assert.That((occupancy.As<byte>()[probeIndex >> 3] & (1u << (int)(probeIndex & 7))), Is.EqualTo(0),
                    "annotation must clear occupancy inside the region");
                Assert.That(costs.As<float>()[probeIndex], Is.EqualTo(3f).Within(1e-5f));
            }
        }

        [Test]
        public void Annotation_ForceBlocked_SetsOccupancy()
        {
            var input = MakeInput();
            var grid = MakeGrid();
            long voxelCount = grid.VoxelCount;

            using var distance = TestMemory.Alloc(voxelCount * sizeof(float));
            using var occupancy = TestMemory.Alloc((voxelCount + 7) / 8);
            using var costs = TestMemory.Alloc(voxelCount * sizeof(float));

            // 空场景：全部可行走。强制阻挡一块。
            var annotations = new[]
            {
                new NavBakeAnnotation
                {
                    Region = Aabb.FromCenterExtents(new float3(6f, 6f, 0f), new float3(1f, 1f, 1f)),
                    WalkableOverride = 2,
                    CostMultiplier = 0f,
                },
            };

            fixed (NavBakeAnnotation* annotationPtr = annotations)
            {
                NavVoxelizer.BakeDistanceField(in input, in grid, null, 0,
                    null, 0, distance.As<float>(), occupancy.As<byte>());
                NavVoxelizer.ApplyAnnotations(in grid, annotationPtr, annotations.Length,
                    distance.As<float>(), occupancy.As<byte>(), costs.As<float>());

                int3 probe = grid.WorldToVoxel(new float3(6f, 6f, 0f));
                long probeIndex = grid.VoxelIndex(probe);
                Assert.That((occupancy.As<byte>()[probeIndex >> 3] & (1u << (int)(probeIndex & 7))), Is.Not.EqualTo(0),
                    "force-blocked region must set occupancy");

                // 标注区外的体素保持可行走。
                int3 outside = grid.WorldToVoxel(new float3(1f, 1f, 0f));
                long outsideIndex = grid.VoxelIndex(outside);
                Assert.That((occupancy.As<byte>()[outsideIndex >> 3] & (1u << (int)(outsideIndex & 7))), Is.EqualTo(0));
            }
        }
    }
}
