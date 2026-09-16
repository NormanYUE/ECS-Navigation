using System;
using Ember.Collision;
using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// NavBaker 端到端 + blob 回环：小世界烘焙 → 校验头部 → 段内容对拍。
    /// </summary>
    [TestFixture]
    public unsafe class NavBakerTests
    {
        private static NavBakeInput MakeInput() => new()
        {
            Dimension = CollisionDimension.XY,
            VoxelSize = 0.5f,
            TileSize = 8,
            DistanceBits = 8,
            MaxBakeRadius = 4f,
            Connectivity = 8,
        };

        private static NavBakeCollider[] MakeRoomColliders()
        {
            // 12×12 米房间，中央一堵 2 米墙把房间分成左右两半，底部留门。
            return new[]
            {
                new NavBakeCollider
                {
                    Collider = Collider.Box2D(new float2(6f, 0.5f), new float3(6f, 0.25f, 0f)),
                    Pose = BodyPose.Identity,
                },
                new NavBakeCollider
                {
                    Collider = Collider.Box2D(new float2(6f, 0.5f), new float3(6f, 11.75f, 0f)),
                    Pose = BodyPose.Identity,
                },
                new NavBakeCollider
                {
                    Collider = Collider.Box2D(new float2(0.5f, 6f), new float3(0.25f, 6f, 0f)),
                    Pose = BodyPose.Identity,
                },
                new NavBakeCollider
                {
                    Collider = Collider.Box2D(new float2(0.5f, 6f), new float3(11.75f, 6f, 0f)),
                    Pose = BodyPose.Identity,
                },
                // 中央隔墙：y ∈ [4,8]，x = 6，底部 y<4 留门。
                new NavBakeCollider
                {
                    Collider = Collider.Box2D(new float2(0.25f, 2f), new float3(6f, 6f, 0f)),
                    Pose = BodyPose.Identity,
                },
            };
        }

        private static long Bake(in NavBakeInput input, NavBakeCollider[] colliders, out TestMemory blob, out NavBaker.PlanResult plan)
        {
            fixed (NavBakeCollider* colliderPtr = colliders)
            {
                plan = NavBaker.Plan(in input, colliderPtr, colliders.Length, null, 0, 0);

                using var distance = TestMemory.Alloc(plan.DistanceBytes);
                using var occupancy = TestMemory.Alloc(plan.OccupancyBytes);
                using var costs = TestMemory.Alloc(plan.CostBytes);
                using var parent = TestMemory.Alloc(plan.ParentBytes);
                using var region = TestMemory.Alloc(plan.RegionBytes);
                using var lookup = TestMemory.Alloc(plan.NodeLookupBytes);
                for (long i = 0; i < plan.NodeLookupBytes / sizeof(int); i++) lookup.As<int>()[i] = -1;
                using var scratch = TestMemory.Alloc(plan.ClusterCounts.Nodes * sizeof(int));
                using var nodes = TestMemory.Alloc(plan.ClusterCounts.Nodes * sizeof(NavClusterNode));
                using var portals = TestMemory.Alloc(plan.ClusterCounts.Portals * sizeof(NavPortal));
                using var edges = TestMemory.Alloc(plan.ClusterCounts.EdgesBound * sizeof(NavClusterEdge));
                using var edgeKeys = TestMemory.Alloc(plan.ClusterCounts.EdgesBound * sizeof(long));
                using var edgeCounts = TestMemory.Alloc(plan.ClusterCounts.EdgesBound * sizeof(int));
                using var edgePortals = TestMemory.Alloc((plan.ClusterCounts.Portals / 2 + 1) * sizeof(NavPortal));
                blob = TestMemory.Alloc(plan.BlobBytes);

                return NavBaker.Bake(in plan, in input, colliderPtr, colliders.Length,
                    null, 0, null, 0, null, 0,
                    distance.As<float>(), occupancy.As<byte>(), costs.As<float>(),
                    parent.As<int>(), region.As<int>(), lookup.As<int>(), scratch.As<int>(),
                    nodes.As<NavClusterNode>(), portals.As<NavPortal>(), edges.As<NavClusterEdge>(),
                    edgeKeys.As<long>(), edgeCounts.As<int>(), edgePortals.As<NavPortal>(),
                    blob.As<byte>());
            }
        }

        [Test]
        public void Bake_RoomWithGappedWall_IsSingleRegion()
        {
            var input = MakeInput();
            long bytes = Bake(in input, MakeRoomColliders(), out var blob, out var plan);

            Assert.That(bytes, Is.GreaterThan(0));
            Assert.That(bytes, Is.LessThanOrEqualTo(plan.BlobBytes));

            var status = NavBlobReader.Validate(blob.As<byte>(), CollisionDimension.XY);
            Assert.That(status, Is.EqualTo(NavBlobReader.Status.Ok));

            // 隔墙只到 y=4，底部留门 → 门内外：房外圈与房内各一个区域。
            var header = NavBlobReader.ReadHeader(blob.As<byte>());
            Assert.That(header.RegionCount, Is.EqualTo(2), "outside ring + connected interior");
            blob.Dispose();
        }

        [Test]
        public void Bake_RoomWithSealedWall_IsTwoRegions()
        {
            var input = MakeInput();
            var colliders = MakeRoomColliders();
            // 整高隔墙 y ∈ [0.5, 11.25] 与原墙合并，彻底封死左右。
            Array.Resize(ref colliders, colliders.Length + 1);
            colliders[^1] = new NavBakeCollider
            {
                Collider = Collider.Box2D(new float2(0.25f, 5.375f), new float3(6f, 5.875f, 0f)),
                Pose = BodyPose.Identity,
            };

            Bake(in input, colliders, out var blob, out _);
            var header = NavBlobReader.ReadHeader(blob.As<byte>());
            Assert.That(header.RegionCount, Is.EqualTo(3), "sealed wall: outside + two interior halves");
            blob.Dispose();
        }

        [Test]
        public void Blob_RoundTrip_SegmentsIntact()
        {
            var input = MakeInput();
            Bake(in input, MakeRoomColliders(), out var blob, out var plan);

            var header = NavBlobReader.ReadHeader(blob.As<byte>());
            var grid = NavBlobReader.GridOf(in header);

            Assert.That(grid.Dimensions, Is.EqualTo(header.Dimensions));
            Assert.That(header.VoxelSize, Is.EqualTo(0.5f));

            // Occupancy 段：重建距离场对拍占据位。
            byte* occSegment = NavBlobReader.Segment(blob.As<byte>(), NavBlobSegment.Occupancy);
            long voxelCount = grid.VoxelCount;
            using var distance = TestMemory.Alloc(voxelCount * sizeof(float));
            using var occupancy = TestMemory.Alloc(plan.OccupancyBytes);

            fixed (NavBakeCollider* colliderPtr = MakeRoomColliders())
            {
                var colliders = MakeRoomColliders();
                fixed (NavBakeCollider* cp = colliders)
                {
                    NavVoxelizer.BakeDistanceField(in input, in grid, cp, colliders.Length,
                        null, 0, distance.As<float>(), occupancy.As<byte>());
                }
            }

            for (long i = 0; i < plan.OccupancyBytes; i++)
            {
                Assert.That(occSegment[i], Is.EqualTo(occupancy.As<byte>()[i]),
                    $"occupancy byte {i} mismatch between blob and re-baked reference");
            }

            // Distance 段量化对拍。
            byte* distSegment = NavBlobReader.Segment(blob.As<byte>(), NavBlobSegment.Distance);
            for (long i = 0; i < voxelCount; i++)
            {
                byte expected = NavBlobWriter.Quantize8(distance.As<float>()[i], input.MaxBakeRadius);
                Assert.That(distSegment[i], Is.EqualTo(expected), $"voxel {i} quantized distance mismatch");
            }

            blob.Dispose();
        }

        [Test]
        public void Validate_RejectsWrongDimension()
        {
            var input = MakeInput();
            Bake(in input, MakeRoomColliders(), out var blob, out _);

            Assert.That(NavBlobReader.Validate(blob.As<byte>(), CollisionDimension.XYZ),
                Is.EqualTo(NavBlobReader.Status.BadDimension));

            // 破坏魔数。
            blob.As<byte>()[0] = (byte)'X';
            Assert.That(NavBlobReader.Validate(blob.As<byte>(), CollisionDimension.XY),
                Is.EqualTo(NavBlobReader.Status.BadMagic));

            blob.Dispose();
        }

    }
}

