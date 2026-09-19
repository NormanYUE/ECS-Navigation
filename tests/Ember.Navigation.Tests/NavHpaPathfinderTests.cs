using System;
using Ember.Collision;
using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 分层 A* 对拍：多 tile 烘焙 → blob 段 → HPA* 路径 vs 单层 A*。
    /// 验收：可达性一致，代价 ≤ 1.15 × 单层 A*（门户启发式容差，见设计 §8）。
    /// </summary>
    [TestFixture]
    public unsafe class NavHpaPathfinderTests
    {
        private const int Tile = 8;

        private static NavBakeInput MakeInput() => new()
        {
            Dimension = CollisionDimension.XY,
            VoxelSize = 1f,
            TileSize = Tile,
            DistanceBits = 8,
            MaxBakeRadius = 4f,
            Connectivity = 8,
        };

        /// <summary>3×1 tile 的 24×8 世界：几堵墙制造多簇绕行。</summary>
        private static NavBakeCollider[] MakeColliders()
        {
            return new[]
            {
                new NavBakeCollider { Collider = Collider.Box2D(new float2(12f, 0.5f), new float3(12f, 0.25f, 0f)), Pose = BodyPose.Identity },
                new NavBakeCollider { Collider = Collider.Box2D(new float2(12f, 0.5f), new float3(12f, 7.75f, 0f)), Pose = BodyPose.Identity },
                new NavBakeCollider { Collider = Collider.Box2D(new float2(0.5f, 4f), new float3(0.25f, 4f, 0f)), Pose = BodyPose.Identity },
                new NavBakeCollider { Collider = Collider.Box2D(new float2(0.5f, 4f), new float3(23.75f, 4f, 0f)), Pose = BodyPose.Identity },
                // 错位墙：强制蛇形路线跨全部 tile。
                new NavBakeCollider { Collider = Collider.Box2D(new float2(0.4f, 2.4f), new float3(8f, 5.2f, 0f)), Pose = BodyPose.Identity },
                new NavBakeCollider { Collider = Collider.Box2D(new float2(0.4f, 2.4f), new float3(16f, 2.8f, 0f)), Pose = BodyPose.Identity },
            };
        }

        private struct Fixture : IDisposable
        {
            public NavGrid Grid;
            public NavBakeInput Input;
            public TestMemory Blob;
            public TestMemory VoxelNodes;
            public TestMemory ClusterG, ClusterParent, ClusterHeapF, ClusterHeapV, ClusterPath;
            public TestMemory AG, AParent, AHeapF, AHeapV;
            public TestMemory Waypoints, Segment;
            public NavBlobHeader Header;

            public void Dispose()
            {
                Blob?.Dispose(); VoxelNodes?.Dispose();
                ClusterG?.Dispose(); ClusterParent?.Dispose(); ClusterHeapF?.Dispose();
                ClusterHeapV?.Dispose(); ClusterPath?.Dispose();
                AG?.Dispose(); AParent?.Dispose(); AHeapF?.Dispose(); AHeapV?.Dispose();
                Waypoints?.Dispose(); Segment?.Dispose();
            }
        }

        private static Fixture BuildFixture()
        {
            var input = MakeInput();
            var colliders = MakeColliders();
            fixed (NavBakeCollider* colliderPtr = colliders)
            {
                NavBaker.PlanResult plan = NavBaker.Plan(in input, colliderPtr, colliders.Length, null, 0, 0);

                using var distance = TestMemory.Alloc(plan.DistanceBytes);
                using var occupancy = TestMemory.Alloc(plan.OccupancyBytes);
                using var costs = TestMemory.Alloc(plan.CostBytes);
                using var parent = TestMemory.Alloc(plan.ParentBytes);
                using var region = TestMemory.Alloc(plan.RegionBytes);
                using var lookup = TestMemory.Alloc(plan.NodeLookupBytes);
                using var voxelNodesScratch = TestMemory.Alloc(plan.VoxelNodesBytes);
                using var scratch = TestMemory.Alloc(plan.ClusterCounts.Nodes * 2 * sizeof(int));
                using var nodes = TestMemory.Alloc(plan.ClusterCounts.Nodes * sizeof(NavClusterNode));
                using var portals = TestMemory.Alloc(plan.ClusterCounts.Portals * sizeof(NavPortal));
                using var edges = TestMemory.Alloc(plan.ClusterCounts.EdgesBound * sizeof(NavClusterEdge));
                using var edgeKeys = TestMemory.Alloc(plan.ClusterCounts.EdgesBound * sizeof(long));
                using var edgeCounts = TestMemory.Alloc(plan.ClusterCounts.EdgesBound * sizeof(int));
                using var edgePortals = TestMemory.Alloc((plan.ClusterCounts.Portals / 2 + 1) * sizeof(NavPortal));
                var blob = TestMemory.Alloc(plan.BlobBytes);

                NavBaker.Bake(in plan, in input, colliderPtr, colliders.Length,
                    null, 0, null, 0, null, 0,
                    distance.As<float>(), occupancy.As<byte>(), costs.As<float>(),
                    parent.As<int>(), region.As<int>(), lookup.As<int>(), voxelNodesScratch.As<int>(), scratch.As<int>(),
                    nodes.As<NavClusterNode>(), portals.As<NavPortal>(), edges.As<NavClusterEdge>(),
                    edgeKeys.As<long>(), edgeCounts.As<int>(), edgePortals.As<NavPortal>(),
                    blob.As<byte>());

                var header = NavBlobReader.ReadHeader(blob.As<byte>());
                var grid = NavBlobReader.GridOf(in header);
                long voxelCount = grid.VoxelCount;

                var voxelNodes = TestMemory.Alloc(voxelCount * sizeof(int));
                var vnSrc = (int*)NavBlobReader.Segment(blob.As<byte>(), NavBlobSegment.VoxelNodes);
                for (long i = 0; i < voxelCount; i++) voxelNodes.As<int>()[i] = vnSrc[i];

                return new Fixture
                {
                    Grid = grid,
                    Input = input,
                    Blob = blob,
                    Header = header,
                    VoxelNodes = voxelNodes,
                    ClusterG = TestMemory.Alloc(header.ClusterNodeCount * sizeof(float)),
                    ClusterParent = TestMemory.Alloc(header.ClusterNodeCount * sizeof(int)),
                    ClusterHeapF = TestMemory.Alloc(header.ClusterNodeCount * sizeof(float)),
                    ClusterHeapV = TestMemory.Alloc(header.ClusterNodeCount * sizeof(int)),
                    ClusterPath = TestMemory.Alloc(header.ClusterNodeCount * sizeof(int)),
                    AG = TestMemory.Alloc(voxelCount * sizeof(float)),
                    AParent = TestMemory.Alloc(voxelCount * sizeof(int)),
                    AHeapF = TestMemory.Alloc(voxelCount * 8 * sizeof(float)),
                    AHeapV = TestMemory.Alloc(voxelCount * 8 * sizeof(int)),
                    Waypoints = TestMemory.Alloc(voxelCount * sizeof(int3)),
                    Segment = TestMemory.Alloc(voxelCount * sizeof(int3)),
                };
            }
        }

        private static NavHpaPathfinder.Context MakeHpaContext(in Fixture f)
        {
            byte* blob = f.Blob.As<byte>();
            return new NavHpaPathfinder.Context
            {
                Grid = f.Grid,
                Connectivity = 8,
                Occupancy = NavBlobReader.Segment(blob, NavBlobSegment.Occupancy),
                DistanceLevels = NavBlobReader.Segment(blob, NavBlobSegment.Distance),
                Costs = NavBlobReader.Segment(blob, NavBlobSegment.Cost),
                RequiredLevel = 0,
                Nodes = (NavClusterNode*)NavBlobReader.Segment(blob, NavBlobSegment.ClusterNodes),
                NodeCount = f.Header.ClusterNodeCount,
                Edges = (NavClusterEdge*)NavBlobReader.Segment(blob, NavBlobSegment.ClusterEdges),
                EdgeCount = f.Header.ClusterEdgeCount,
                EdgePortals = (NavPortal*)NavBlobReader.Segment(blob, NavBlobSegment.EdgePortals),
                VoxelNodes = f.VoxelNodes.As<int>(),
                ClusterG = f.ClusterG.As<float>(),
                ClusterParent = f.ClusterParent.As<int>(),
                ClusterHeapF = f.ClusterHeapF.As<float>(),
                ClusterHeapV = f.ClusterHeapV.As<int>(),
                ClusterHeapCapacity = f.Header.ClusterNodeCount,
                ClusterPath = f.ClusterPath.As<int>(),
                ClusterPathCapacity = f.Header.ClusterNodeCount,
                AStar = new NavAStar.Context
                {
                    Grid = f.Grid,
                    Connectivity = 8,
                    Occupancy = NavBlobReader.Segment(blob, NavBlobSegment.Occupancy),
                    DistanceLevels = NavBlobReader.Segment(blob, NavBlobSegment.Distance),
                    Costs = NavBlobReader.Segment(blob, NavBlobSegment.Cost),
                    G = f.AG.As<float>(),
                    Parent = f.AParent.As<int>(),
                    VoxelNodes = f.VoxelNodes.As<int>(),
                    HeapF = f.AHeapF.As<float>(),
                    HeapVoxels = f.AHeapV.As<int>(),
                    HeapCapacity = (int)f.Grid.VoxelCount * 8,
                    HeapCount = 0,
                    RequiredLevel = 0,
                    RestrictNode = -1,
                },
                SegmentWaypoints = f.Segment.As<int3>(),
                SegmentCapacity = (int)f.Grid.VoxelCount,
            };
        }

        private static float PathLength(NavGrid grid, int3* waypoints, int count)
        {
            float total = 0f;
            for (int i = 1; i < count; i++)
                total += math.length(grid.VoxelToWorld(waypoints[i]) - grid.VoxelToWorld(waypoints[i - 1]));
            return total;
        }

        [Test]
        public void HpaPath_ReachableAndWithinToleranceOfPlainAStar()
        {
            using var f = BuildFixture();
            var hpa = MakeHpaContext(f);
            var start = f.Grid.WorldToVoxelOnGrid(new float3(2f, 4f, 0f));
            var goal = f.Grid.WorldToVoxelOnGrid(new float3(21f, 4f, 0f));

            Assert.That(f.VoxelNodes.As<int>()[f.Grid.VoxelIndex(start)], Is.GreaterThanOrEqualTo(0));
            TestContext.Out.WriteLine($"goal voxel={goal} node={f.VoxelNodes.As<int>()[f.Grid.VoxelIndex(goal)]} nodes={f.Header.ClusterNodeCount}");
            byte* occP = NavBlobReader.Segment(f.Blob.As<byte>(), NavBlobSegment.Occupancy);
            int* regP = (int*)NavBlobReader.Segment(f.Blob.As<byte>(), NavBlobSegment.Region);
            long gpi = f.Grid.VoxelIndex(goal);
            TestContext.Out.WriteLine($"goal occ={((occP[gpi >> 3] >> (int)(gpi & 7)) & 1)} region={regP[gpi]}");
            Assert.That(f.VoxelNodes.As<int>()[f.Grid.VoxelIndex(goal)], Is.GreaterThanOrEqualTo(0));

            int hpaCount = NavHpaPathfinder.FindPath(ref hpa, start, goal,
                f.Waypoints.As<int3>(), (int)f.Grid.VoxelCount);

            Assert.That(hpaCount, Is.GreaterThan(1), "HPA* must find a path");
            Assert.That(f.Waypoints.As<int3>()[0], Is.EqualTo(start));
            Assert.That(f.Waypoints.As<int3>()[hpaCount - 1], Is.EqualTo(goal));

            // 单层 A* 基准。
            var astar = hpa.AStar;
            astar.RestrictNode = -1;
            astar.Goal = goal;
            NavAStar.Reset(ref astar, f.Grid.VoxelCount);
            Assert.That(NavAStar.Begin(ref astar, start), Is.True);
            Assert.That(NavAStar.RunToCompletion(ref astar), Is.True);
            int aCount = NavAStar.ExtractPath(ref astar, start, goal,
                f.Segment.As<int3>(), (int)f.Grid.VoxelCount);
            Assert.That(aCount, Is.GreaterThan(1));

            float hpaLength = PathLength(f.Grid, f.Waypoints.As<int3>(), hpaCount);
            float aLength = PathLength(f.Grid, f.Segment.As<int3>(), aCount);

            Assert.That(hpaLength, Is.LessThanOrEqualTo(aLength * 1.15f),
                $"HPA {hpaLength} must be within 15% of A* {aLength}");
        }

        [Test]
        public void HpaPath_UnreachableGoal_ReturnsMinusOne()
        {
            using var f = BuildFixture();
            var hpa = MakeHpaContext(f);

            // 造一个被墙完全围死的目标格：直接改占据位（测试隔离语义）。
            var goal = f.Grid.WorldToVoxelOnGrid(new float3(21f, 4f, 0f));
            byte* occ = NavBlobReader.Segment(f.Blob.As<byte>(), NavBlobSegment.Occupancy);
            long goalIndex = f.Grid.VoxelIndex(goal);
            occ[goalIndex >> 3] |= (byte)(1u << (int)(goalIndex & 7));

            int result = NavHpaPathfinder.FindPath(ref hpa, f.Grid.WorldToVoxelOnGrid(new float3(2f, 4f, 0f)), goal,
                f.Waypoints.As<int3>(), (int)f.Grid.VoxelCount);
            Assert.That(result, Is.EqualTo(-1));
        }

        [Test]
        public void PullString_SmoothsWithoutLeavingWalkable()
        {
            using var f = BuildFixture();
            var hpa = MakeHpaContext(f);
            var start = f.Grid.WorldToVoxelOnGrid(new float3(2f, 4f, 0f));
            var goal = f.Grid.WorldToVoxelOnGrid(new float3(21f, 4f, 0f));
            int count = NavHpaPathfinder.FindPath(ref hpa, start, goal,
                f.Waypoints.As<int3>(), (int)f.Grid.VoxelCount);
            Assert.That(count, Is.GreaterThan(1));

            using var smooth = TestMemory.Alloc(count * sizeof(int3));
            byte* blob = f.Blob.As<byte>();
            int smoothed = NavPathSmoother.PullString(in f.Grid,
                NavBlobReader.Segment(blob, NavBlobSegment.Occupancy),
                NavBlobReader.Segment(blob, NavBlobSegment.Distance),
                0, 0,
                f.Waypoints.As<int3>(), count,
                smooth.As<int3>(), count);
            Assert.That(smoothed, Is.GreaterThan(1).And.LessThanOrEqualTo(count));
            Assert.That(smooth.As<int3>()[0], Is.EqualTo(start));
            Assert.That(smooth.As<int3>()[smoothed - 1], Is.EqualTo(goal));

            // 平滑后每段保持通视。
            for (int i = 1; i < smoothed; i++)
            {
                Assert.That(NavPathSmoother.HasLineOfSight(in f.Grid,
                        NavBlobReader.Segment(blob, NavBlobSegment.Occupancy),
                        NavBlobReader.Segment(blob, NavBlobSegment.Distance),
                        0, smooth.As<int3>()[i - 1], smooth.As<int3>()[i]),
                    Is.True, $"smoothed segment {i - 1}->{i} must stay walkable");
            }
        }

        /// <summary>
        /// 偏好等级严于路径成立等级时，平滑必须**退化**而不是失败。
        ///
        /// 回归用例：两者曾并作一趟，偏好等级一旦卡住走廊（A* 按等级 A 搜到的路径
        /// 处处不满足等级 B），PullString 就返回 -1，A* 明明有解、请求却被判 Failed。
        /// 这里把偏好等级拉到不可能满足的 255，结果仍须是一份有效路径。
        /// </summary>
        [Test]
        public void PullString_FallsBackToMinLevelWhenPreferLevelIsUnreachable()
        {
            using var f = BuildFixture();
            var hpa = MakeHpaContext(f);
            var start = f.Grid.WorldToVoxelOnGrid(new float3(2f, 4f, 0f));
            var goal = f.Grid.WorldToVoxelOnGrid(new float3(21f, 4f, 0f));
            int count = NavHpaPathfinder.FindPath(ref hpa, start, goal,
                f.Waypoints.As<int3>(), (int)f.Grid.VoxelCount);
            Assert.That(count, Is.GreaterThan(1));

            using var smooth = TestMemory.Alloc(count * sizeof(int3));
            byte* blob = f.Blob.As<byte>();
            int smoothed = NavPathSmoother.PullString(in f.Grid,
                NavBlobReader.Segment(blob, NavBlobSegment.Occupancy),
                NavBlobReader.Segment(blob, NavBlobSegment.Distance),
                255, 0,
                f.Waypoints.As<int3>(), count,
                smooth.As<int3>(), count);

            Assert.That(smoothed, Is.GreaterThan(1));
            Assert.That(smooth.As<int3>()[0], Is.EqualTo(start));
            Assert.That(smooth.As<int3>()[smoothed - 1], Is.EqualTo(goal));
        }
    }
}