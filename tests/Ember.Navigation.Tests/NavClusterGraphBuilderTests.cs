using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 簇图（基于 tile 局部分量）：节点拆分、门户双向性、边去重、确定性。
    /// </summary>
    [TestFixture]
    public unsafe class NavClusterGraphBuilderTests
    {
        private const int TileSize = 4;
        private const int GridWidth = 8;

        private struct SetupResult
        {
            public NavGrid Grid;
            public TestMemory Occ;
            public TestMemory Region;
            public TestMemory Parent;
            public TestMemory TileOffsets;
            public TestMemory VoxelNodes;
            public int NodeCount;
        }

        private static SetupResult SetupWalkable()
        {
            var grid = new NavGrid
            {
                Origin = float3.zero,
                VoxelSize = 1f,
                Dimensions = new int3(GridWidth, 1, 1),
                TileSize = TileSize,
            };
            long voxelCount = grid.VoxelCount;
            var r = new SetupResult
            {
                Grid = grid,
                Occ = TestMemory.Alloc((voxelCount + 7) / 8),
                Region = TestMemory.Alloc(voxelCount * sizeof(int)),
                Parent = TestMemory.Alloc(voxelCount * sizeof(int)),
                TileOffsets = TestMemory.Alloc(grid.TileCount * sizeof(int)),
                VoxelNodes = TestMemory.Alloc(voxelCount * sizeof(int)),
            };
            for (long i = 0; i < (voxelCount + 7) / 8; i++) r.Occ.As<byte>()[i] = 0;
            for (long i = 0; i < voxelCount; i++) r.Region.As<int>()[i] = 0;
            r.NodeCount = NavTileLocalLabeler.Label(in grid, 8, r.Occ.As<byte>(),
                r.Parent.As<int>(), r.TileOffsets.As<int>(), r.VoxelNodes.As<int>());
            return r;
        }

        private static int BuildGraph(ref SetupResult s, out TestMemory nodes, out TestMemory portals,
            out TestMemory edges, out TestMemory edgeKeys, out TestMemory edgeCounts, out TestMemory edgePortals)
        {
            var counts = NavClusterGraphBuilder.Count(in s.Grid, s.VoxelNodes.As<int>(),
                s.Region.As<int>(), s.NodeCount);

            nodes = TestMemory.Alloc(counts.Nodes * sizeof(NavClusterNode));
            portals = TestMemory.Alloc(counts.Portals * sizeof(NavPortal));
            edges = TestMemory.Alloc(counts.EdgesBound * sizeof(NavClusterEdge));
            edgeKeys = TestMemory.Alloc(counts.EdgesBound * sizeof(long));
            edgeCounts = TestMemory.Alloc(counts.EdgesBound * sizeof(int));
            edgePortals = TestMemory.Alloc((counts.Portals / 2 + 1) * sizeof(NavPortal));
            using var scratch = TestMemory.Alloc(counts.Nodes * 2 * sizeof(int));

            return NavClusterGraphBuilder.Build(in s.Grid, s.VoxelNodes.As<int>(), s.Region.As<int>(),
                counts.Nodes, scratch.As<int>(),
                nodes.As<NavClusterNode>(), portals.As<NavPortal>(), edges.As<NavClusterEdge>(),
                edgeKeys.As<long>(), edgeCounts.As<int>(), edgePortals.As<NavPortal>());
        }

        [Test]
        public void TwoTilesOneRegion_TwoNodesOneEdge()
        {
            var s = SetupWalkable();
            Assert.That(s.NodeCount, Is.EqualTo(2), "one local component per tile");

            int edgeCount = BuildGraph(ref s, out var nodes, out var portals, out var edges,
                out var edgeKeys, out var edgeCounts, out var edgePortals);
            Assert.That(edgeCount, Is.EqualTo(1), "single adjacency between the two tiles");

            var nodeArray = nodes.As<NavClusterNode>();
            Assert.That(nodeArray[0].TileIndex, Is.Not.EqualTo(nodeArray[1].TileIndex));

            // 门户双向。
            bool forward = false, backward = false;
            for (int p = 0; p < nodeArray[0].PortalCount; p++)
            {
                if (portals.As<NavPortal>()[nodeArray[0].PortalStart + p].OtherCluster == 1) forward = true;
            }
            for (int p = 0; p < nodeArray[1].PortalCount; p++)
            {
                if (portals.As<NavPortal>()[nodeArray[1].PortalStart + p].OtherCluster == 0) backward = true;
            }
            Assert.That(forward, Is.True, "portal tile0 -> tile1 must exist");
            Assert.That(backward, Is.True, "portal tile1 -> tile0 must exist");

            var edge = edges.As<NavClusterEdge>()[0];
            Assert.That(edge.PortalCount, Is.EqualTo(1));
            Assert.That(edge.Weight, Is.EqualTo(1f));

            nodes.Dispose(); portals.Dispose(); edges.Dispose();
            edgeKeys.Dispose(); edgeCounts.Dispose(); edgePortals.Dispose();
            s.Occ.Dispose(); s.Region.Dispose(); s.Parent.Dispose();
            s.TileOffsets.Dispose(); s.VoxelNodes.Dispose();
        }

        [Test]
        public void TileWithTwoLocalComponents_TwoNodesInThatTile()
        {
            var s = SetupWalkable();
            // 左 tile 中间一列占据（4 邻接）→ 左 tile 两个局部分量。
            for (int y = 0; y < 1; y++)
            {
                long idx = s.Grid.VoxelIndex(new int3(2, 0, 0));
                s.Occ.As<byte>()[idx >> 3] |= (byte)(1u << (int)(idx & 7));
            }
            int nodeCount = NavTileLocalLabeler.Label(in s.Grid, 4, s.Occ.As<byte>(),
                s.Parent.As<int>(), s.TileOffsets.As<int>(), s.VoxelNodes.As<int>());
            Assert.That(nodeCount, Is.EqualTo(3), "left tile splits into two local components");

            var counts = NavClusterGraphBuilder.Count(in s.Grid, s.VoxelNodes.As<int>(),
                s.Region.As<int>(), nodeCount);
            Assert.That(counts.Nodes, Is.EqualTo(3));

            s.Occ.Dispose(); s.Region.Dispose(); s.Parent.Dispose();
            s.TileOffsets.Dispose(); s.VoxelNodes.Dispose();
        }

        [Test]
        public void SameInput_BuildIsDeterministic()
        {
            void RunOnce(out int firstClusterA, out int edgeCount, out float firstWeight)
            {
                var s = SetupWalkable();
                using var nodes = TestMemory.Alloc(s.NodeCount * sizeof(NavClusterNode));
                var counts = NavClusterGraphBuilder.Count(in s.Grid, s.VoxelNodes.As<int>(),
                    s.Region.As<int>(), s.NodeCount);
                using var portals = TestMemory.Alloc(counts.Portals * sizeof(NavPortal));
                using var edges = TestMemory.Alloc(counts.EdgesBound * sizeof(NavClusterEdge));
                using var edgeKeys = TestMemory.Alloc(counts.EdgesBound * sizeof(long));
                using var edgeCounts = TestMemory.Alloc(counts.EdgesBound * sizeof(int));
                using var edgePortals = TestMemory.Alloc((counts.Portals / 2 + 1) * sizeof(NavPortal));
                using var scratch = TestMemory.Alloc(counts.Nodes * 2 * sizeof(int));

                edgeCount = NavClusterGraphBuilder.Build(in s.Grid, s.VoxelNodes.As<int>(),
                    s.Region.As<int>(), counts.Nodes, scratch.As<int>(),
                    nodes.As<NavClusterNode>(), portals.As<NavPortal>(), edges.As<NavClusterEdge>(),
                    edgeKeys.As<long>(), edgeCounts.As<int>(), edgePortals.As<NavPortal>());

                firstClusterA = edges.As<NavClusterEdge>()[0].ClusterA;
                firstWeight = edges.As<NavClusterEdge>()[0].Weight;
                s.Occ.Dispose(); s.Region.Dispose(); s.Parent.Dispose();
                s.TileOffsets.Dispose(); s.VoxelNodes.Dispose();
            }

            RunOnce(out int a, out int countA, out float weightA);
            RunOnce(out int b, out int countB, out float weightB);
            Assert.That(countA, Is.EqualTo(countB));
            Assert.That(a, Is.EqualTo(b));
            Assert.That(weightA, Is.EqualTo(weightB));
        }
    }
}
