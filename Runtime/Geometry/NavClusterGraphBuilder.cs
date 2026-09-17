using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 簇图构建（HPA* 结构，烘焙期一次构建永久复用）：
    /// 节点 = tile 内局部连通分量（<see cref="NavTileLocalLabeler"/> 产出的全局化 id）；
    /// 门户 = 相邻 tile 边界上同全局区域、不同局部簇的体素对；
    /// 边 = 节点对归并（权重 = 门户数）。
    /// 两遍式：先 <see cref="Count"/> 拿精确数量（容量决策），再 <see cref="Build"/> 填充。
    /// 全部输出按线性下标扫描顺序，确定性。
    /// </summary>
    public static unsafe class NavClusterGraphBuilder
    {
        /// <summary>构建产物计数。</summary>
        public struct Counts
        {
            /// <summary>簇节点数（= Label 返回值）。</summary>
            public int Nodes;

            /// <summary>有向门户数（每个无向门户两侧各一条）。</summary>
            public int Portals;

            /// <summary>无向邻接边数上界（= Portals / 2）。</summary>
            public int EdgesBound;
        }

        /// <summary>
        /// 统计门户 / 边上界。节点数由调用方从 <see cref="NavTileLocalLabeler.Label"/> 获得。
        /// </summary>
        /// <param name="grid">网格。</param>
        /// <param name="voxelNodes">每体素簇 id（<see cref="NavTileLocalLabeler"/> 产出）。</param>
        /// <param name="regionIds">每体素全局区域 id（门户同区域校验）。</param>
        /// <param name="nodeCount">簇节点数。</param>
        public static Counts Count(
            in NavGrid grid,
            int* voxelNodes,
            int* regionIds,
            int nodeCount)
        {
            Counts counts = default;
            counts.Nodes = nodeCount;
            int3 dims = grid.Dimensions;

            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int3 voxel = new(x, y, z);
                int index = grid.VoxelIndex(voxel);
                int node = voxelNodes[index];
                if (node < 0) continue;

                counts.Portals += CountPortalAt(grid, voxelNodes, regionIds, x + 1, y, z, index, node)
                    + CountPortalAt(grid, voxelNodes, regionIds, x, y + 1, z, index, node)
                    + CountPortalAt(grid, voxelNodes, regionIds, x, y, z + 1, index, node);
            }

            counts.EdgesBound = counts.Portals / 2;
            return counts;
        }

        /// <summary>
        /// 填充簇图数组。阶段：①节点表 ②每节点正 / 反向门户计数 + 前缀和
        /// ③发射有向门户 ④无向边去重 ⑤边门户紧凑化。
        /// </summary>
        /// <param name="grid">网格。</param>
        /// <param name="voxelNodes">每体素簇 id。</param>
        /// <param name="regionIds">每体素全局区域 id。</param>
        /// <param name="nodeCount">簇节点数。</param>
        /// <param name="nodePortalCounts">工作数组，长度 ≥ 2 × 节点数。</param>
        /// <param name="nodes">输出节点数组。</param>
        /// <param name="portals">输出门户数组（按节点分组）。</param>
        /// <param name="edges">输出边数组（长度 ≥ Counts.EdgesBound）。</param>
        /// <param name="edgeKeys">边去重工作数组，长度 ≥ EdgesBound。</param>
        /// <param name="edgePortalCounts">边去重工作数组，长度 ≥ EdgesBound。</param>
        /// <param name="edgePortals">输出边门户紧凑数组（长度 ≥ 有向门户数 / 2）。</param>
        /// <returns>精确边数。</returns>
        public static int Build(
            in NavGrid grid,
            int* voxelNodes,
            int* regionIds,
            int nodeCount,
            int* nodePortalCounts,
            NavClusterNode* nodes,
            NavPortal* portals,
            NavClusterEdge* edges,
            long* edgeKeys,
            int* edgePortalCounts,
            NavPortal* edgePortals)
        {
            int3 dims = grid.Dimensions;

            // ① 节点表：TileIndex 以 -1 标记未填充；RegionId 取首个体素的全局区域。
            for (int n = 0; n < nodeCount; n++)
            {
                nodes[n].TileIndex = -1;
                nodes[n].RegionId = -1;
                nodes[n].PortalStart = 0;
                nodes[n].PortalCount = 0;
            }

            // ② 正 / 反向门户计数（配额分离保证 Emit 不越界）。
            int* forwardCounts = nodePortalCounts;
            int* reverseCounts = nodePortalCounts + nodeCount;
            for (int n = 0; n < nodeCount * 2; n++) nodePortalCounts[n] = 0;

            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int3 voxel = new(x, y, z);
                int index = grid.VoxelIndex(voxel);
                int node = voxelNodes[index];
                if (node < 0) continue;

                if (nodes[node].TileIndex < 0)
                {
                    nodes[node].TileIndex = grid.VoxelToTileIndex(voxel);
                    nodes[node].RegionId = regionIds[index];
                }

                CountPortalSides(grid, voxelNodes, regionIds, x + 1, y, z, index, node, forwardCounts, reverseCounts);
                CountPortalSides(grid, voxelNodes, regionIds, x, y + 1, z, index, node, forwardCounts, reverseCounts);
                CountPortalSides(grid, voxelNodes, regionIds, x, y, z + 1, index, node, forwardCounts, reverseCounts);
            }

            int portalTotal = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                nodes[n].PortalStart = portalTotal;
                int total = forwardCounts[n] + reverseCounts[n];
                portalTotal += total;
                nodePortalCounts[n] = 0; // 转为组内游标
            }

            // ③ 发射有向门户。
            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int index = grid.VoxelIndex(new int3(x, y, z));
                int node = voxelNodes[index];
                if (node < 0) continue;

                EmitPortal(grid, voxelNodes, regionIds, x + 1, y, z, index, node, nodePortalCounts, nodes, portals);
                EmitPortal(grid, voxelNodes, regionIds, x, y + 1, z, index, node, nodePortalCounts, nodes, portals);
                EmitPortal(grid, voxelNodes, regionIds, x, y, z + 1, index, node, nodePortalCounts, nodes, portals);
            }

            // ④ 无向边去重。
            int edgeCount = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                for (int p = 0; p < nodes[n].PortalCount; p++)
                {
                    ref readonly NavPortal portal = ref portals[nodes[n].PortalStart + p];
                    int a = math.min(n, portal.OtherCluster);
                    int b = math.max(n, portal.OtherCluster);
                    long key = ((long)a << 32) | (uint)b;

                    int found = -1;
                    for (int e = 0; e < edgeCount; e++)
                    {
                        if (edgeKeys[e] == key) { found = e; break; }
                    }

                    if (found < 0)
                    {
                        edgeKeys[edgeCount] = key;
                        edgePortalCounts[edgeCount] = 1;
                        edges[edgeCount] = new NavClusterEdge
                        {
                            ClusterA = a,
                            ClusterB = b,
                        };
                        edgeCount++;
                    }
                    else
                    {
                        edgePortalCounts[found]++;
                    }
                }
            }

            // ⑤ 边门户紧凑化：每条边的门户在 edgePortals 中连续存放。
            int cursor = 0;
            for (int e = 0; e < edgeCount; e++)
            {
                edges[e].PortalStart = cursor;
                int a = edges[e].ClusterA;
                int remaining = edgePortalCounts[e];
                for (int p = 0; p < nodes[a].PortalCount && remaining > 0; p++)
                {
                    ref readonly NavPortal portal = ref portals[nodes[a].PortalStart + p];
                    if (math.min(a, portal.OtherCluster) != edges[e].ClusterA
                        || math.max(a, portal.OtherCluster) != edges[e].ClusterB)
                        continue;
                    edgePortals[cursor++] = portal;
                    edges[e].PortalCount++;
                    remaining--;
                }
                edges[e].Weight = edges[e].PortalCount;
            }

            return edgeCount;
        }

        private static int CountPortalAt(
            in NavGrid grid,
            int* voxelNodes,
            int* regionIds,
            int nx, int ny, int nz,
            int index, int node)
        {
            int3 n = new(nx, ny, nz);
            if (!grid.IsInside(n)) return 0;
            int neighbor = grid.VoxelIndex(n);
            if (voxelNodes[neighbor] < 0) return 0;
            if (voxelNodes[neighbor] == node) return 0;
            if (regionIds[neighbor] != regionIds[index]) return 0;
            if (grid.VoxelToTileIndex(n) == grid.VoxelToTileIndex(grid.VoxelCoord(index))) return 0;
            // 同区域、跨 tile、不同局部簇：双向各一条有向门户。
            return 2;
        }

        private static void CountPortalSides(
            in NavGrid grid,
            int* voxelNodes,
            int* regionIds,
            int nx, int ny, int nz,
            int index, int node,
            int* forwardCounts,
            int* reverseCounts)
        {
            int3 n = new(nx, ny, nz);
            if (!grid.IsInside(n)) return;
            int neighbor = grid.VoxelIndex(n);
            int neighborNode = voxelNodes[neighbor];
            if (neighborNode < 0 || neighborNode == node) return;
            if (regionIds[neighbor] != regionIds[index]) return;
            if (grid.VoxelToTileIndex(n) == grid.VoxelToTileIndex(grid.VoxelCoord(index))) return;

            forwardCounts[node]++;
            reverseCounts[neighborNode]++;
        }

        private static void EmitPortal(
            in NavGrid grid,
            int* voxelNodes,
            int* regionIds,
            int nx, int ny, int nz,
            int index, int node,
            int* nodePortalCursors,
            NavClusterNode* nodes,
            NavPortal* portals)
        {
            int3 n = new(nx, ny, nz);
            if (!grid.IsInside(n)) return;
            int neighbor = grid.VoxelIndex(n);
            int neighborNode = voxelNodes[neighbor];
            if (neighborNode < 0 || neighborNode == node) return;
            if (regionIds[neighbor] != regionIds[index]) return;
            if (grid.VoxelToTileIndex(n) == grid.VoxelToTileIndex(grid.VoxelCoord(index))) return;

            AppendPortal(nodes, portals, nodePortalCursors, node, index, neighbor, neighborNode);
            AppendPortal(nodes, portals, nodePortalCursors, neighborNode, neighbor, index, node);
        }

        private static void AppendPortal(
            NavClusterNode* nodes,
            NavPortal* portals,
            int* nodePortalCursors,
            int node, int voxelA, int voxelB, int otherCluster)
        {
            int cursor = nodePortalCursors[node]++;
            portals[nodes[node].PortalStart + cursor] = new NavPortal
            {
                VoxelA = voxelA,
                VoxelB = voxelB,
                OtherCluster = otherCluster,
            };
            nodes[node].PortalCount = nodePortalCursors[node];
        }
    }
}
