using System.Collections.Generic;
using Ember.Collision;
using NUnit.Framework;
using Unity.Mathematics;

namespace Ember.Navigation.Tests
{
    /// <summary>
    /// 均匀网格邻居查询对拍：邻居集合与顺序对照「全量扫描 + 按距离排序」的暴力解，
    /// 外加网格退化、越界钳制、自排除、2D 投影与确定性。
    /// </summary>
    [TestFixture]
    public unsafe class NavNeighborGridTests
    {
        private const int BlockSize = 64;
        private const float CellSize = 1f;

        /// <summary>当前用例的导航维度；建表与查询共用，默认 XY。</summary>
        private CollisionDimension m_Dimension = CollisionDimension.XY;

        private static readonly float3 Origin = float3.zero;

        /// <summary>建表时记录的最大半径，作为查询扫描半径的上界。</summary>
        private float m_MaxRadius = 1f;

        /// <summary>暴力解：全量扫描 + 按 (距离, 下标) 排序取前 N。</summary>
        private static List<int> BruteForce(
            float3[] positions, float[] radii, float[] neighborDists,
            int self, int maxNeighbors, bool flattenZ)
        {
            var found = new List<(float DistanceSq, int Index)>();
            for (int j = 0; j < positions.Length; j++)
            {
                if (j == self) continue;
                float3 delta = positions[j] - positions[self];
                if (flattenZ) delta.z = 0f;
                float distanceSq = math.lengthsq(delta);
                float threshold = math.max(neighborDists[self], radii[self] + radii[j]);
                if (distanceSq >= threshold * threshold) continue;
                found.Add((distanceSq, j));
            }

            found.Sort((a, b) => a.DistanceSq != b.DistanceSq
                ? a.DistanceSq.CompareTo(b.DistanceSq)
                : a.Index.CompareTo(b.Index));

            var result = new List<int>();
            for (int i = 0; i < found.Count && i < maxNeighbors; i++) result.Add(found[i].Index);
            return result;
        }

        private int3 BuildGrid(
            float3[] positions, out float3 min, out float3 max, out int[] cellIndices,
            out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents)
        {
            min = new float3(float.MaxValue);
            max = new float3(float.MinValue);
            for (int i = 0; i < positions.Length; i++)
            {
                min = math.min(min, positions[i]);
                max = math.max(max, positions[i]);
            }

            int3 gridSize = NavNeighborGrid.ResolveGridSize(min, max, CellSize, m_Dimension);
            int cellCount = NavNeighborGrid.CellCount(gridSize);

            cellIndices = new int[positions.Length];
            cellCounts = new int[cellCount];
            cellStarts = new int[cellCount];
            var cursors = new int[cellCount];
            var blockTotals = new int[(cellCount + BlockSize - 1) / BlockSize + 1];
            sortedAgents = new int[positions.Length];

            fixed (float3* positionPtr = positions)
            fixed (int* indices = cellIndices)
            fixed (int* counts = cellCounts)
            fixed (int* starts = cellStarts)
            fixed (int* cursorPtr = cursors)
            fixed (int* totals = blockTotals)
            fixed (int* sorted = sortedAgents)
            {
                NavNeighborGrid.Build(gridSize, min, CellSize, m_Dimension, positionPtr, positions.Length,
                    indices, counts, starts, cursorPtr, totals, BlockSize, sorted);
            }

            return gridSize;
        }

        private int Query(
            int3 gridSize, float3 min, int[] cellCounts, int[] cellStarts, int[] sortedAgents,
            float3[] positions, float[] radii, float[] neighborDists,
            int self, int maxNeighbors,
            out int[] neighbors, out float[] distancesSq)
        {
            neighbors = new int[maxNeighbors];
            distancesSq = new float[maxNeighbors];

            fixed (int* counts = cellCounts)
            fixed (int* starts = cellStarts)
            fixed (int* sorted = sortedAgents)
            fixed (float3* positionPtr = positions)
            fixed (float* radiusPtr = radii)
            fixed (float* distPtr = neighborDists)
            fixed (int* neighborPtr = neighbors)
            fixed (float* distanceSqPtr = distancesSq)
            {
                return NavNeighborGrid.QueryNeighbors(gridSize, min, CellSize, starts, counts, sorted,
                    positionPtr, radiusPtr, distPtr, self, maxNeighbors, m_MaxRadius, m_Dimension,
                    neighborPtr, distanceSqPtr);
            }
        }

        [Test]
        public void ResolveGridSize_CollapsesInactiveAxisToOne()
        {
            // XY 模式：Z 轴塌成一层（Y 轴跨度为 0，本来也是 1）
            var xy = NavNeighborGrid.ResolveGridSize(
                new float3(0f, 5f, 0f), new float3(10f, 5f, 10f), 1f, CollisionDimension.XY);
            Assert.That(xy, Is.EqualTo(new int3(11, 1, 1)));

            // XZ 模式：Y 轴塌成一层，即使跨度不为 0
            var xz = NavNeighborGrid.ResolveGridSize(
                new float3(0f, 0f, 0f), new float3(10f, 10f, 10f), 1f, CollisionDimension.XZ);
            Assert.That(xz, Is.EqualTo(new int3(11, 1, 11)));

            // XYZ 模式：三轴都按跨度计算
            var xyz = NavNeighborGrid.ResolveGridSize(
                new float3(0f, 0f, 0f), new float3(10f, 4f, 10f), 1f, CollisionDimension.XYZ);
            Assert.That(xyz, Is.EqualTo(new int3(11, 5, 11)));
        }

        [Test]
        public void CellOf_ClampsOutOfRangePosition()
        {
            var gridSize = new int3(4, 4, 4);
            Assert.That(NavNeighborGrid.CellOf(Origin, 1f, gridSize, new float3(-5f, 0f, 0f), CollisionDimension.XYZ).x, Is.EqualTo(0));
            Assert.That(NavNeighborGrid.CellOf(Origin, 1f, gridSize, new float3(99f, 0f, 0f), CollisionDimension.XYZ).x, Is.EqualTo(3));
            Assert.That(NavNeighborGrid.CellOf(Origin, 1f, gridSize, new float3(2.5f, 0f, 0f), CollisionDimension.XYZ).x, Is.EqualTo(2));
        }

        [Test]
        public void Build_PlacesEveryAgentInItsOwnCell()
        {
            var positions = new[]
            {
                new float3(0.5f, 0.5f, 0f), new float3(0.6f, 0.5f, 0f),
                new float3(3.5f, 0.5f, 0f), new float3(7.5f, 0.5f, 0f),
            };
            var gridSize = BuildGrid(positions, out float3 min, out _, out int[] cellIndices,
                out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents);

            // 前两个代理同格
            Assert.That(cellIndices[0], Is.EqualTo(cellIndices[1]));
            Assert.That(cellCounts[cellIndices[0]], Is.EqualTo(2));
            Assert.That(cellCounts[cellIndices[2]], Is.EqualTo(1));

            for (int cell = 0; cell < cellCounts.Length; cell++)
            {
                for (int slot = cellStarts[cell]; slot < cellStarts[cell] + cellCounts[cell]; slot++)
                    Assert.That(cellIndices[sortedAgents[slot]], Is.EqualTo(cell));
            }

            Assert.That(gridSize.x, Is.GreaterThan(7));
            Assert.That(min, Is.EqualTo(new float3(0.5f, 0.5f, 0f)));
        }

        [Test]
        public void QueryNeighbors_MatchesBruteForce()
        {
            const int agentCount = 120;
            const int maxNeighbors = 8;
            var random = new Random(20260916);
            var positions = new float3[agentCount];
            var radii = new float[agentCount];
            var neighborDists = new float[agentCount];

            for (int i = 0; i < agentCount; i++)
            {
                positions[i] = new float3(
                    random.NextFloat(0f, 12f), random.NextFloat(0f, 12f), 0f);
                radii[i] = random.NextFloat(0.3f, 0.9f);
                neighborDists[i] = random.NextFloat(0f, 3f);
            }

            int3 gridSize = BuildGrid(positions, out float3 min, out _, out _,
                out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents);

            for (int self = 0; self < agentCount; self++)
            {
                int count = Query(gridSize, min, cellCounts, cellStarts, sortedAgents,
                    positions, radii, neighborDists, self, maxNeighbors,
                    out int[] neighbors, out float[] distancesSq);

                List<int> expected = BruteForce(positions, radii, neighborDists, self, maxNeighbors,
                    flattenZ: true);

                Assert.That(count, Is.EqualTo(expected.Count), $"agent {self} 邻居数不符");
                for (int k = 0; k < count; k++)
                {
                    Assert.That(neighbors[k], Is.EqualTo(expected[k]), $"agent {self} 第 {k} 个邻居不符");
                    if (k > 0)
                        Assert.That(distancesSq[k], Is.GreaterThanOrEqualTo(distancesSq[k - 1] - 1e-6f),
                            "邻居必须按距离升序");
                }
            }
        }

        [Test]
        public void QueryNeighbors_2D_IgnoresInactiveAxis()
        {
            var positions = new[]
            {
                new float3(0f, 0f, 0f),
                new float3(0.2f, 0f, 0f),
                new float3(0.2f, 0f, 50f),   // 仅 Z 相差 50：XY 模式下仍是邻居
            };
            var radii = new[] { 0.5f, 0.5f, 0.5f };
            var neighborDists = new[] { 1f, 1f, 1f };

            int3 gridSize = BuildGrid(positions, out float3 min, out _, out _,
                out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents);

            int count = Query(gridSize, min, cellCounts, cellStarts, sortedAgents,
                positions, radii, neighborDists, 0, 4,
                out int[] neighbors, out _);

            Assert.That(count, Is.EqualTo(2));
            Assert.That(neighbors, Does.Contain(2));
        }

        [Test]
        public void QueryNeighbors_3D_UsesFullDistance()
        {
            m_Dimension = CollisionDimension.XYZ;
            var positions = new[]
            {
                new float3(0f, 0f, 0f),
                new float3(0.2f, 0f, 0f),
                new float3(0.2f, 0f, 50f),
            };
            var radii = new[] { 0.5f, 0.5f, 0.5f };
            var neighborDists = new[] { 1f, 1f, 1f };

            int3 gridSize = BuildGrid(positions, out float3 min, out _, out _,
                out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents);

            int count = Query(gridSize, min, cellCounts, cellStarts, sortedAgents,
                positions, radii, neighborDists, 0, 4,
                out int[] neighbors, out _);

            Assert.That(count, Is.EqualTo(1), "XYZ 模式下 Z 相差 50 的代理不是邻居");
            Assert.That(neighbors[0], Is.EqualTo(1));
        }

        [Test]
        public void QueryNeighbors_KeepsOnlyNearestWhenOverCapacity()
        {
            // 沿 X 等距排开，容量 2：应保留最近的两个（下标 1、2）
            var positions = new[]
            {
                new float3(0f, 0f, 0f), new float3(1f, 0f, 0f),
                new float3(2f, 0f, 0f), new float3(3f, 0f, 0f),
            };
            var radii = new[] { 0.5f, 0.5f, 0.5f, 0.5f };
            var neighborDists = new[] { 10f, 10f, 10f, 10f };

            int3 gridSize = BuildGrid(positions, out float3 min, out _, out _,
                out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents);

            int count = Query(gridSize, min, cellCounts, cellStarts, sortedAgents,
                positions, radii, neighborDists, 0, 2,
                out int[] neighbors, out _);

            Assert.That(count, Is.EqualTo(2));
            Assert.That(neighbors[0], Is.EqualTo(1));
            Assert.That(neighbors[1], Is.EqualTo(2));
        }

        [Test]
        public void QueryNeighbors_NeverReturnsSelf()
        {
            var positions = new[] { new float3(0f, 0f, 0f), new float3(0.1f, 0f, 0f) };
            var radii = new[] { 0.5f, 0.5f };
            var neighborDists = new[] { 5f, 5f };

            int3 gridSize = BuildGrid(positions, out float3 min, out _, out _,
                out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents);

            int count = Query(gridSize, min, cellCounts, cellStarts, sortedAgents,
                positions, radii, neighborDists, 0, 4,
                out int[] neighbors, out _);

            // 数组其余槽位是容量内未使用的零值，只校验前 count 个
            Assert.That(count, Is.EqualTo(1));
            Assert.That(neighbors[0], Is.EqualTo(1));
        }

        [Test]
        public void QueryNeighbors_ZeroCapacity_ReturnsEmpty()
        {
            var positions = new[] { new float3(0f, 0f, 0f), new float3(0.1f, 0f, 0f) };
            var radii = new[] { 0.5f, 0.5f };
            var neighborDists = new[] { 5f, 5f };

            int3 gridSize = BuildGrid(positions, out float3 min, out _, out _,
                out int[] cellCounts, out int[] cellStarts, out int[] sortedAgents);

            Assert.That(Query(gridSize, min, cellCounts, cellStarts, sortedAgents,
                positions, radii, neighborDists, 0, 0, out _, out _),
                Is.EqualTo(0));
        }

        [Test]
        public void Build_IsDeterministic()
        {
            const int agentCount = 60;
            var random = new Random(7);
            var positions = new float3[agentCount];
            for (int i = 0; i < agentCount; i++)
                positions[i] = new float3(random.NextFloat(0f, 8f), random.NextFloat(0f, 8f), 0f);

            int3 first = BuildGrid(positions, out _, out _, out _, out _, out _, out int[] sortedFirst);
            int3 second = BuildGrid(positions, out _, out _, out _, out _, out _, out int[] sortedSecond);

            Assert.That(first, Is.EqualTo(second));
            Assert.That(sortedSecond, Is.EqualTo(sortedFirst));
        }
    }
}
