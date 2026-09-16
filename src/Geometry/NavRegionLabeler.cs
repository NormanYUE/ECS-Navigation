using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 连通区域标记：对可行走体素（未占据）做 union-find，
    /// 输出稠密 region id（0..R-1，按首次发现顺序）与区域数。
    /// 确定性：按线性下标顺序遍历、只与已访问邻居合并，同输入必同输出。
    /// </summary>
    public static unsafe class NavRegionLabeler
    {
        /// <summary>
        /// 标记连通区域。
        /// </summary>
        /// <param name="grid">网格。</param>
        /// <param name="connectivity">邻接模板：2D 用 4 或 8，3D 用 6 或 26。</param>
        /// <param name="occupancy">占据位集（长度 = ceil(VoxelCount / 8)）。</param>
        /// <param name="parent">工作数组（union-find 父指针），长度 = VoxelCount。</param>
        /// <param name="regionIds">输出：每体素 region id（占据格为 -1），长度 = VoxelCount。</param>
        /// <returns>区域数 R。</returns>
        public static int Label(
            in NavGrid grid,
            int connectivity,
            byte* occupancy,
            int* parent,
            int* regionIds)
        {
            long voxelCount = grid.VoxelCount;
            for (long i = 0; i < voxelCount; i++)
            {
                bool occupied = (occupancy[i >> 3] & (1u << (int)(i & 7))) != 0;
                parent[i] = occupied ? -1 : (int)i;
                regionIds[i] = -1;
            }

            int3 dims = grid.Dimensions;
            bool diagonal = connectivity == 8 || connectivity == 26;

            // 只与「已访问」邻居（负方向偏移）合并，保证遍历顺序确定。
            for (int z = 0; z < dims.z; z++)
            for (int y = 0; y < dims.y; y++)
            for (int x = 0; x < dims.x; x++)
            {
                int index = grid.VoxelIndex(new int3(x, y, z));
                if (parent[index] < 0) continue;

                UnionNeighbor(grid, parent, dims, x - 1, y, z, index);
                UnionNeighbor(grid, parent, dims, x, y - 1, z, index);
                UnionNeighbor(grid, parent, dims, x, y, z - 1, index);

                if (diagonal)
                {
                    UnionNeighbor(grid, parent, dims, x - 1, y - 1, z, index);
                    UnionNeighbor(grid, parent, dims, x + 1, y - 1, z, index);
                    UnionNeighbor(grid, parent, dims, x - 1, y, z - 1, index);
                    UnionNeighbor(grid, parent, dims, x + 1, y, z - 1, index);
                    UnionNeighbor(grid, parent, dims, x, y - 1, z - 1, index);
                    UnionNeighbor(grid, parent, dims, x, y + 1, z - 1, index);
                    UnionNeighbor(grid, parent, dims, x - 1, y - 1, z - 1, index);
                    UnionNeighbor(grid, parent, dims, x + 1, y - 1, z - 1, index);
                    UnionNeighbor(grid, parent, dims, x - 1, y + 1, z - 1, index);
                    UnionNeighbor(grid, parent, dims, x + 1, y + 1, z - 1, index);
                }
            }

            // 第二次遍历：压缩路径并分配稠密 id。
            int nextId = 0;
            for (long i = 0; i < voxelCount; i++)
            {
                if (parent[i] < 0) continue;
                int root = Find(parent, (int)i);
                if (root == i)
                {
                    regionIds[i] = nextId;
                    nextId++;
                }
            }

            // 根 → id 建立后回填非根节点。
            for (long i = 0; i < voxelCount; i++)
            {
                if (parent[i] < 0) continue;
                int root = Find(parent, (int)i);
                regionIds[i] = regionIds[root];
            }

            return nextId;
        }

        private static void UnionNeighbor(
            in NavGrid grid,
            int* parent,
            int3 dims,
            int x, int y, int z,
            int index)
        {
            if (x < 0 || y < 0 || z < 0 || x >= dims.x || y >= dims.y || z >= dims.z)
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
            // 总是挂到较小下标，保证根确定性。
            if (ra < rb) parent[rb] = ra;
            else parent[ra] = rb;
        }

        private static int Find(int* parent, int i)
        {
            int root = i;
            while (parent[root] != root)
                root = parent[root];
            // 路径压缩（路径减半）。
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
