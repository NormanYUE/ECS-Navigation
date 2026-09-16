using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 簇图：跨区域 tile 的节点拆分、门户双向性、边去重与权重。
    /// </summary>
    [TestFixture]
    public unsafe class NavClusterGraphBuilderTests
    {
        private const int TileSize = 4;
        private const int RegionCount = 1;
        private const int GridWidth = 8;

        /// <summary>8×1×1 网格（两个 tile），全部可行走、同一区域。</summary>
        private static (NavGrid grid, TestMemory region, TestMemory lookup, TestMemory scratch) SetupWalkable()
        {
            var grid = new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 1f,
                Dimensions = new int3(GridWidth, 1, 1),
                TileSize = TileSize,
            };
            long voxelCount = grid.VoxelCount;
            var region = TestMemory.Alloc(voxelCount * sizeof(int));
            var lookup = TestMemory.Alloc(grid.TileCount * 2 * sizeof(int));
            var scratch = TestMemory.Alloc(voxelCount * sizeof(int));

            for (long i = 0; i < voxelCount; i++) region.As<int>()[i] = 0;
            for (long i = 0; i < grid.TileCount * 2; i++) lookup.As<int>()[i] = -1;
            return (grid, region, lookup, scratch);
        }

        private static (TestMemory nodes, TestMemory portals, TestMemory edges, TestMemory edgeKeys, TestMemory edgeCounts, TestMemory edgePortals)
            AllocOutputs(NavClusterGraphBuilder.Counts counts)
        {
            return (
                TestMemory.Alloc(counts.Nodes * sizeof(NavClusterNode)),
                TestMemory.Alloc(counts.Portals * sizeof(NavPortal)),
                TestMemory.Alloc(counts.EdgesBound * sizeof(NavClusterEdge)),
                TestMemory.Alloc(counts.EdgesBound * sizeof(long)),
                TestMemory.Alloc(counts.EdgesBound * sizeof(int)),
                TestMemory.Alloc((counts.Portals / 2 + 1) * sizeof(NavPortal)));
        }

        [Test]
        public void TwoTilesOneRegion_TwoNodesOneEdge()
        {
            var (grid, region, lookup, scratch) = SetupWalkable();
            var counts = NavClusterGraphBuilder.Count(in grid, region.As<int>(), RegionCount, lookup.As<int>());

            Assert.That(counts.Nodes, Is.EqualTo(2), "one cluster node per tile");

            using var nodes = TestMemory.Alloc(counts.Nodes * sizeof(NavClusterNode));
            using var portals = TestMemory.Alloc(counts.Portals * sizeof(NavPortal));
            using var edges = TestMemory.Alloc(counts.EdgesBound * sizeof(NavClusterEdge));
            using var edgeKeys = TestMemory.Alloc(counts.EdgesBound * sizeof(long));
            using var edgeCounts = TestMemory.Alloc(counts.EdgesBound * sizeof(int));
            using var edgePortals = TestMemory.Alloc((counts.Portals / 2 + 1) * sizeof(NavPortal));

            int edgeCount = NavClusterGraphBuilder.Build(in grid, region.As<int>(), RegionCount,
                lookup.As<int>(), scratch.As<int>(),
                nodes.As<NavClusterNode>(), portals.As<NavPortal>(), edges.As<NavClusterEdge>(),
                edgeKeys.As<long>(), edgeCounts.As<int>(), edgePortals.As<NavPortal>());

            Assert.That(edgeCount, Is.EqualTo(1), "single adjacency between the two tiles");

            var nodeArray = nodes.As<NavClusterNode>();
            Assert.That(nodeArray[0].RegionId, Is.EqualTo(0));
            Assert.That(nodeArray[1].RegionId, Is.EqualTo(0));
            Assert.That(nodeArray[0].TileIndex, Is.Not.EqualTo(nodeArray[1].TileIndex));

            // 门户双向：tile0 节点的门户指向 tile1 节点，反之亦然。
            ref var node0 = ref nodeArray[0];
            bool forward = false, backward = false;
            for (int p = 0; p < node0.PortalCount; p++)
            {
                var portal = portals.As<NavPortal>()[node0.PortalStart + p];
                if (portal.OtherCluster == 1) forward = true;
            }
            ref var node1 = ref nodeArray[1];
            for (int p = 0; p < node1.PortalCount; p++)
            {
                var portal = portals.As<NavPortal>()[node1.PortalStart + p];
                if (portal.OtherCluster == 0) backward = true;
            }
            Assert.That(forward, Is.True, "portal tile0 -> tile1 must exist");
            Assert.That(backward, Is.True, "portal tile1 -> tile0 must exist");

            // 边权重 = 开口体素数：两个 tile 在 x=3/x=4 交界，1×1×1 截面 → 1 个无向门户。
            var edge = edges.As<NavClusterEdge>()[0];
            Assert.That(edge.PortalCount, Is.EqualTo(1));
            Assert.That(edge.Weight, Is.EqualTo(1f));
        }

        [Test]
        public void WallBetweenTiles_NoPortalBetweenRegions()
        {
            var (grid, region, lookup, scratch) = SetupWalkable();
            // 手动构造两个区域：左 tile region 0，右 tile region 1。
            for (int x = 0; x < GridWidth; x++)
                region.As<int>()[grid.VoxelIndex(new int3(x, 0, 0))] = x < TileSize ? 0 : 1;

            var counts = NavClusterGraphBuilder.Count(in grid, region.As<int>(), 2, lookup.As<int>());
            Assert.That(counts.Nodes, Is.EqualTo(2));

            using var nodes = TestMemory.Alloc(counts.Nodes * sizeof(NavClusterNode));
            using var portals = TestMemory.Alloc(counts.Portals * sizeof(NavPortal));
            using var edges = TestMemory.Alloc(counts.EdgesBound * sizeof(NavClusterEdge));
            using var edgeKeys = TestMemory.Alloc(counts.EdgesBound * sizeof(long));
            using var edgeCounts = TestMemory.Alloc(counts.EdgesBound * sizeof(int));
            using var edgePortals = TestMemory.Alloc((counts.Portals / 2 + 1) * sizeof(NavPortal));

            int edgeCount = NavClusterGraphBuilder.Build(in grid, region.As<int>(), 2,
                lookup.As<int>(), scratch.As<int>(),
                nodes.As<NavClusterNode>(), portals.As<NavPortal>(), edges.As<NavClusterEdge>(),
                edgeKeys.As<long>(), edgeCounts.As<int>(), edgePortals.As<NavPortal>());

            Assert.That(edgeCount, Is.EqualTo(0), "different regions must not connect without a link");
        }

        [Test]
        public void SameInput_BuildIsDeterministic()
        {
            (NavGrid grid, TestMemory region, TestMemory lookup, TestMemory scratch) RunOnce(
                out TestMemory nodes, out TestMemory portals, out TestMemory edges, out int edgeCount)
            {
                var setup = SetupWalkable();
                var counts = NavClusterGraphBuilder.Count(in setup.grid, setup.region.As<int>(), RegionCount, setup.lookup.As<int>());
                nodes = TestMemory.Alloc(counts.Nodes * sizeof(NavClusterNode));
                portals = TestMemory.Alloc(counts.Portals * sizeof(NavPortal));
                edges = TestMemory.Alloc(counts.EdgesBound * sizeof(NavClusterEdge));
                using var edgeKeys = TestMemory.Alloc(counts.EdgesBound * sizeof(long));
                using var edgeCounts = TestMemory.Alloc(counts.EdgesBound * sizeof(int));
                using var edgePortals = TestMemory.Alloc((counts.Portals / 2 + 1) * sizeof(NavPortal));

                edgeCount = NavClusterGraphBuilder.Build(in setup.grid, setup.region.As<int>(), RegionCount,
                    setup.lookup.As<int>(), setup.scratch.As<int>(),
                    nodes.As<NavClusterNode>(), portals.As<NavPortal>(), edges.As<NavClusterEdge>(),
                    edgeKeys.As<long>(), edgeCounts.As<int>(), edgePortals.As<NavPortal>());
                return setup;
            }

            var a = RunOnce(out var nodesA, out var portalsA, out var edgesA, out int edgesCountA);
            var b = RunOnce(out var nodesB, out var portalsB, out var edgesB, out int edgesCountB);

            Assert.That(edgesCountA, Is.EqualTo(edgesCountB));
            for (int i = 0; i < edgesCountA; i++)
            {
                Assert.That(edgesA.As<NavClusterEdge>()[i].ClusterA, Is.EqualTo(edgesB.As<NavClusterEdge>()[i].ClusterA));
                Assert.That(edgesA.As<NavClusterEdge>()[i].ClusterB, Is.EqualTo(edgesB.As<NavClusterEdge>()[i].ClusterB));
                Assert.That(edgesA.As<NavClusterEdge>()[i].Weight, Is.EqualTo(edgesB.As<NavClusterEdge>()[i].Weight));
            }

            a.region.Dispose(); a.lookup.Dispose(); a.scratch.Dispose();
            b.region.Dispose(); b.lookup.Dispose(); b.scratch.Dispose();
            nodesA.Dispose(); portalsA.Dispose(); edgesA.Dispose();
            nodesB.Dispose(); portalsB.Dispose(); edgesB.Dispose();
        }
    }
}
