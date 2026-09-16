using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 簇图构建（HPA* 结构，烘焙期一次构建永久复用）：
    /// 节点 = tile × 连通区域；门户 = 相邻 tile 边界上同区域双向可通行的体素对；
    /// 边 = 节点对归并（权重 = 门户数）。
    /// 两遍式：先 <see cref="Count"/> 拿精确数量（容量决策），再 <see cref="Build"/> 填充。
    /// 全部输出按线性下标扫描顺序，确定性。
    /// </summary>
    public static unsafe class NavClusterGraphBuilder
    {
        /// <summary>构建产物计数。</summary>
        public struct Counts
        {
            /// <summary>簇节点数。</summary>
            public int Nodes;

            /// <summary>有向门户数（每个无向门户两侧各一条）。</summary>
            public int Portals;

            /// <summary>无向邻接边数上界（= Portals / 2）。</summary>
            public int EdgesBound;
        }

        /// <summary>
        /// 统计节点 / 门户 / 边上界。工作数组调用前全部置 -1。
        /// </summary>
        /// <param name="grid">网格。</param>
        /// <param name="regionIds">每体素 region id（占据格 -1），长度 = VoxelCount。</param>
        /// <param name="regionCount">区域数 R。</param>
        /// <param name="nodeOfTileRegion">工作数组，长度 = TileCount × R。</param>
        public static Counts Count(
            in NavGrid grid,
            int* regionIds,
            int regionCount,
            int* nodeOfTileRegion)
        {
            Counts counts = default;
            int3 dims = grid.Dimensions;

            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int index = grid.VoxelIndex(new int3(x, y, z));
                int region = regionIds[index];
                if (region < 0) continue;

                int tile = grid.VoxelToTileIndex(new int3(x, y, z));
                int slot = tile * regionCount + region;
                if (nodeOfTileRegion[slot] < 0)
                    nodeOfTileRegion[slot] = counts.Nodes++;

                counts.Portals += CountPortalAt(grid, regionIds, x + 1, y, z, index, region, tile)
                    + CountPortalAt(grid, regionIds, x, y + 1, z, index, region, tile)
                    + CountPortalAt(grid, regionIds, x, y, z + 1, index, region, tile);
            }

            counts.EdgesBound = counts.Portals / 2;
            return counts;
        }

        /// <summary>
        /// 填充簇图数组。阶段：①节点表 ②每节点门户计数 + 前缀和定 PortalStart
        /// ③发射有向门户 ④无向边去重 ⑤边门户紧凑化（连续区间）。
        /// </summary>
        /// <param name="grid">网格。</param>
        /// <param name="regionIds">每体素 region id。</param>
        /// <param name="regionCount">区域数 R。</param>
        /// <param name="nodeOfTileRegion">工作数组（复用 Count 的结果，不再清零）。</param>
        /// <param name="nodePortalCounts">工作数组，长度 ≥ 节点数（门户计数 / 前缀和 / 游标三用）。</param>
        /// <param name="nodes">输出节点数组。</param>
        /// <param name="portals">输出门户数组（按节点分组）。</param>
        /// <param name="edges">输出边数组（长度 ≥ EdgesBound）。</param>
        /// <param name="edgeKeys">边去重工作数组，长度 ≥ EdgesBound。</param>
        /// <param name="edgePortalCounts">边去重工作数组，长度 ≥ EdgesBound。</param>
        /// <param name="edgePortals">输出边门户紧凑数组（长度 = 有向门户数）。</param>
        /// <returns>精确边数。</returns>
        public static int Build(
            in NavGrid grid,
            int* regionIds,
            int regionCount,
            int* nodeOfTileRegion,
            int* nodePortalCounts,
            NavClusterNode* nodes,
            NavPortal* portals,
            NavClusterEdge* edges,
            long* edgeKeys,
            int* edgePortalCounts,
            NavPortal* edgePortals)
        {
            int3 dims = grid.Dimensions;

            // ① 节点表（顺序 = 首次发现顺序，与 Count 一致）。
            int nodeCount = 0;
            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int index = grid.VoxelIndex(new int3(x, y, z));
                int region = regionIds[index];
                if (region < 0) continue;

                int tile = grid.VoxelToTileIndex(new int3(x, y, z));
                int slot = tile * regionCount + region;
                if (nodeOfTileRegion[slot] == nodeCount)
                {
                    nodes[nodeCount] = new NavClusterNode
                    {
                        TileIndex = tile,
                        RegionId = region,
                    };
                    nodeCount++;
                }
            }

            // ② 每节点门户计数 → 前缀和定 PortalStart。
            for (int n = 0; n < nodeCount; n++) nodePortalCounts[n] = 0;

            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int index = grid.VoxelIndex(new int3(x, y, z));
                int region = regionIds[index];
                if (region < 0) continue;

                int3 voxel = new(x, y, z);
                int tile = grid.VoxelToTileIndex(voxel);
                int node = nodeOfTileRegion[tile * regionCount + region];
                nodePortalCounts[node] += CountPortalAt(grid, regionIds, x + 1, y, z, index, region, tile)
                    + CountPortalAt(grid, regionIds, x, y + 1, z, index, region, tile)
                    + CountPortalAt(grid, regionIds, x, y, z + 1, index, region, tile);
            }

            int portalTotal = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                nodes[n].PortalStart = portalTotal;
                portalTotal += nodePortalCounts[n];
                nodePortalCounts[n] = 0; // 转为组内游标
            }

            // ③ 发射有向门户。
            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int index = grid.VoxelIndex(new int3(x, y, z));
                int region = regionIds[index];
                if (region < 0) continue;

                int3 voxel = new(x, y, z);
                int tile = grid.VoxelToTileIndex(voxel);
                int node = nodeOfTileRegion[tile * regionCount + region];

                EmitPortal(grid, regionIds, nodeOfTileRegion, regionCount, nodePortalCounts,
                    nodes, portals, x + 1, y, z, index, region, tile, node);
                EmitPortal(grid, regionIds, nodeOfTileRegion, regionCount, nodePortalCounts,
                    nodes, portals, x, y + 1, z, index, region, tile, node);
                EmitPortal(grid, regionIds, nodeOfTileRegion, regionCount, nodePortalCounts,
                    nodes, portals, x, y, z + 1, index, region, tile, node);
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
                for (int p = 0; p < nodes[a].PortalCount && edgePortalCounts[e] > 0; p++)
                {
                    ref readonly NavPortal portal = ref portals[nodes[a].PortalStart + p];
                    if (math.min(a, portal.OtherCluster) != edges[e].ClusterA
                        || math.max(a, portal.OtherCluster) != edges[e].ClusterB)
                        continue;
                    edgePortals[cursor++] = portal;
                    edges[e].PortalCount++;
                    edgePortalCounts[e]--;
                }
                edges[e].Weight = edges[e].PortalCount;
            }

            return edgeCount;
        }

        private static int CountPortalAt(
            in NavGrid grid,
            int* regionIds,
            int nx, int ny, int nz,
            int index, int region, int tile)
        {
            int3 n = new(nx, ny, nz);
            if (!grid.IsInside(n)) return 0;
            int neighbor = grid.VoxelIndex(n);
            if (regionIds[neighbor] != region) return 0;
            if (grid.VoxelToTileIndex(n) == tile) return 0;
            // 同区域、跨 tile：双向各一条有向门户。
            return 2;
        }

        private static void EmitPortal(
            in NavGrid grid,
            int* regionIds,
            int* nodeOfTileRegion,
            int regionCount,
            int* nodePortalCursors,
            NavClusterNode* nodes,
            NavPortal* portals,
            int nx, int ny, int nz,
            int index, int region, int tile, int node)
        {
            int3 n = new(nx, ny, nz);
            if (!grid.IsInside(n)) return;
            int neighbor = grid.VoxelIndex(n);
            if (regionIds[neighbor] != region) return;
            int neighborTile = grid.VoxelToTileIndex(n);
            if (neighborTile == tile) return;

            int neighborNode = nodeOfTileRegion[neighborTile * regionCount + region];

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
