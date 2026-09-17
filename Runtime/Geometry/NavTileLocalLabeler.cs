using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// tile 内局部连通分量标记：每个 tile 独立 union-find（8/26 邻接），
    /// 输出全局化簇 id（id = tilePrefix[tile] + 局部标号）写入每体素。
    /// 这是簇图的真实节点定义 —— 「tile × 全局区域」的交集在 tile 内未必连通，
    /// 局部分量才是 HPA* 簇内 A* 可达性的正确粒度。
    /// 确定性：按线性下标扫描，根取下标较小者。
    /// </summary>
    public static unsafe class NavTileLocalLabeler
    {
        /// <summary>
        /// 标记局部簇。
        /// </summary>
        /// <param name="grid">网格。</param>
        /// <param name="connectivity">邻接模板。</param>
        /// <param name="occupancy">占据位集。</param>
        /// <param name="parent">工作缓冲（union-find），长度 = VoxelCount；调用前无需初始化。</param>
        /// <param name="tileOffsets">工作缓冲（每 tile 的簇 id 前缀），长度 = TileCount。</param>
        /// <param name="voxelNodes">输出：每体素全局簇 id（占据格 -1）。</param>
        /// <returns>簇总数。</returns>
        public static int Label(
            in NavGrid grid,
            int connectivity,
            byte* occupancy,
            int* parent,
            int* tileOffsets,
            int* voxelNodes)
        {
            int3 dims = grid.Dimensions;
            int tileSize = grid.TileSize;
            int3 tileCounts = grid.TileCounts;
            long tileCount = grid.TileCount;
            long voxelCount = grid.VoxelCount;

            for (long i = 0; i < voxelCount; i++)
            {
                bool occupied = (occupancy[i >> 3] & (1u << (int)(i & 7))) != 0;
                parent[i] = occupied ? -1 : (int)i;
                voxelNodes[i] = -1;
            }

            // Pass 1：每 tile 内 union（只与已访问邻居合并，顺序确定）。
            for (int tz = 0; tz < tileCounts.z; tz++)
            for (int ty = 0; ty < tileCounts.y; ty++)
            for (int tx = 0; tx < tileCounts.x; tx++)
            {
                int3 tileOrigin = new int3(tx, ty, tz) * tileSize;
                int3 tileMax = math.min(tileOrigin + tileSize, dims);

                for (int z = tileOrigin.z; z < tileMax.z; z++)
                for (int y = tileOrigin.y; y < tileMax.y; y++)
                for (int x = tileOrigin.x; x < tileMax.x; x++)
                {
                    int index = grid.VoxelIndex(new int3(x, y, z));
                    if (parent[index] < 0) continue;

                    UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x - 1, y, z, index);
                    UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x, y - 1, z, index);
                    UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x, y, z - 1, index);

                    if (connectivity == 8 || connectivity == 26)
                    {
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x - 1, y - 1, z, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x + 1, y - 1, z, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x - 1, y, z - 1, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x + 1, y, z - 1, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x, y - 1, z - 1, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x, y + 1, z - 1, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x - 1, y - 1, z - 1, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x + 1, y - 1, z - 1, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x - 1, y + 1, z - 1, index);
                        UnionNeighbor(grid, parent, dims, tileOrigin, tileMax, x + 1, y + 1, z - 1, index);
                    }
                }
            }

            // Pass 2：每 tile 的簇数 → 前缀偏移。
            int totalNodes = 0;
            for (int tz = 0; tz < tileCounts.z; tz++)
            for (int ty = 0; ty < tileCounts.y; ty++)
            for (int tx = 0; tx < tileCounts.x; tx++)
            {
                int tileIndex = grid.TileIndex(new int3(tx, ty, tz));
                tileOffsets[tileIndex] = totalNodes;

                int3 tileOrigin = new int3(tx, ty, tz) * tileSize;
                int3 tileMax = math.min(tileOrigin + tileSize, dims);
                int localCount = 0;

                for (int z = tileOrigin.z; z < tileMax.z; z++)
                for (int y = tileOrigin.y; y < tileMax.y; y++)
                for (int x = tileOrigin.x; x < tileMax.x; x++)
                {
                    int index = grid.VoxelIndex(new int3(x, y, z));
                    if (parent[index] < 0) continue;
                    if (Find(parent, index) == index)
                        localCount++;
                }

                totalNodes += localCount;
            }

            // Pass 3：根 → 簇 id（tile 前缀 + 局部序号），回填全部体素。
            // 根在 tile 内按线性下标升序出现 → 序号确定。
            for (int tz = 0; tz < tileCounts.z; tz++)
            for (int ty = 0; ty < tileCounts.y; ty++)
            for (int tx = 0; tx < tileCounts.x; tx++)
            {
                int tileIndex = grid.TileIndex(new int3(tx, ty, tz));
                int prefix = tileOffsets[tileIndex];

                int3 tileOrigin = new int3(tx, ty, tz) * tileSize;
                int3 tileMax = math.min(tileOrigin + tileSize, dims);
                int nextLocal = 0;

                // 先给根分配局部序号。
                for (int z = tileOrigin.z; z < tileMax.z; z++)
                for (int y = tileOrigin.y; y < tileMax.y; y++)
                for (int x = tileOrigin.x; x < tileMax.x; x++)
                {
                    int index = grid.VoxelIndex(new int3(x, y, z));
                    if (parent[index] < 0) continue;
                    if (Find(parent, index) == index)
                    {
                        voxelNodes[index] = prefix + nextLocal;
                        nextLocal++;
                    }
                }

                // 非根回填。
                for (int z = tileOrigin.z; z < tileMax.z; z++)
                for (int y = tileOrigin.y; y < tileMax.y; y++)
                for (int x = tileOrigin.x; x < tileMax.x; x++)
                {
                    int index = grid.VoxelIndex(new int3(x, y, z));
                    if (parent[index] < 0 || voxelNodes[index] >= 0) continue;
                    int root = Find(parent, index);
                    voxelNodes[index] = voxelNodes[root];
                }
            }

            return totalNodes;
        }

        private static void UnionNeighbor(
            in NavGrid grid,
            int* parent,
            int3 dims,
            int3 tileOrigin,
            int3 tileMax,
            int x, int y, int z,
            int index)
        {
            if (x < tileOrigin.x || y < tileOrigin.y || z < tileOrigin.z
                || x >= tileMax.x || y >= tileMax.y || z >= tileMax.z)
                return;
            int neighbor = grid.VoxelIndex(new int3(x, y, z));
            if (parent[neighbor] < 0) return;
            Union(parent, index, neighbor);
        }

        private static void Union(int* parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra;
            else parent[ra] = rb;
        }

        private static int Find(int* parent, int i)
        {
            int root = i;
            while (parent[root] != root)
                root = parent[root];
            while (parent[i] != root)
            {
                int next = parent[i];
                parent[i] = root;
                i = next;
            }
            return root;
        }
    }
}
