using Ember.Collision;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// ORCA 的均匀网格邻居查询（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 为什么不用碰撞宽相：ORCA 每代理要的是<b>自己的邻居列表</b>（半径和 + 时间视界内），
    /// 而宽相是「相交 pair」语义，模型不匹配。改为按代理位置建均匀网格哈希：
    /// 计数 → 前缀和 → 散布（计数排序），再按格邻域取候选。
    ///
    /// 2D 模式（XY / XZ）在建表阶段就把无效轴的格坐标钉到 0，
    /// 网格该轴维数恒为 1，邻域扫描自然退化为平面内搜索。
    ///
    /// 邻域半径按<b>实际查询半径</b>换算：<c>ceil(max(NeighborDist, Radius_i + maxRadius) / cellSize)</c>。
    /// 单元边长取 2 × 最大代理半径且 NeighborDist 不超过该值时，邻域即为 3×3（2D）/ 3×3×3（3D）；
    /// NeighborDist 更大时自动放大扫描范围，不会静默漏邻居。
    ///
    /// 计数与散布当前为串行（O(n)，相对 ORCA 求解可忽略），换取免原子、结果确定；
    /// 分块并行化是 P9 的优化项，不影响本类接口。
    /// </summary>
    public static unsafe class NavNeighborGrid
    {
        /// <summary>
        /// 由包围盒与单元边长求网格维度。2D 模式无效轴维数为 1；
        /// span 为 0 的轴维数也为 1。
        /// </summary>
        public static int3 ResolveGridSize(float3 min, float3 max, float cellSize,
            CollisionDimension dimension)
        {
            if (cellSize <= 0f) return new int3(1, 1, 1);

            float3 span = math.max(max - min, float3.zero);
            int3 size = (int3)math.ceil(span / cellSize) + 1;
            size = math.max(size, new int3(1, 1, 1));

            if (dimension == CollisionDimension.XZ) size.y = 1;
            else if (dimension == CollisionDimension.XY) size.z = 1;
            return size;
        }

        /// <summary>格总数。</summary>
        public static int CellCount(int3 gridSize) => gridSize.x * gridSize.y * gridSize.z;

        /// <summary>
        /// 世界点 → 格坐标。2D 模式无效轴钉到 0；越界钳到网格内，
        /// 保证每个代理都有归属格。
        /// </summary>
        public static int3 CellOf(float3 origin, float cellSize, int3 gridSize, float3 position,
            CollisionDimension dimension)
        {
            int3 cell = (int3)math.floor((position - origin) / cellSize);
            if (dimension == CollisionDimension.XZ) cell.y = 0;
            else if (dimension == CollisionDimension.XY) cell.z = 0;
            return math.clamp(cell, int3.zero, gridSize - 1);
        }

        /// <summary>格坐标 → 线性下标（x 最快，便于按 x 顺序扫描）。</summary>
        public static int Index(int3 gridSize, int3 cell) =>
            (cell.z * gridSize.y + cell.y) * gridSize.x + cell.x;

        /// <summary>当前代理数下网格的合理单元边长：2 × 最大代理半径（设计 §5.1）。</summary>
        public static float SuggestCellSize(float maxRadius) =>
            maxRadius > 0f ? 2f * maxRadius : 1f;

        /// <summary>
        /// 建表：计数 → 前缀和 → 散布。
        /// <paramref name="cellIndices"/> / <paramref name="cellCounts"/> / <paramref name="cellStarts"/> /
        /// <paramref name="cellCursors"/> 长度均须 &gt;= <see cref="CellCount"/>；
        /// <paramref name="blockTotals"/> 长度须 &gt;= 块数 + 1；<paramref name="sortedAgents"/> 长度须 &gt;= 代理数。
        /// </summary>
        /// <returns>代理总数。</returns>
        public static int Build(
            int3 gridSize,
            float3 origin,
            float cellSize,
            CollisionDimension dimension,
            float3* positions,
            int agentCount,
            int* cellIndices,
            int* cellCounts,
            int* cellStarts,
            int* cellCursors,
            int* blockTotals,
            int blockSize,
            int* sortedAgents)
        {
            int cellCount = CellCount(gridSize);
            Count(gridSize, origin, cellSize, dimension, positions, agentCount, cellIndices,
                cellCounts, cellCount);
            BlockScan.ScanSerial(cellCounts, cellStarts, cellCount, blockSize, blockTotals);
            Scatter(cellIndices, agentCount, cellStarts, cellCursors, cellCount, sortedAgents);
            return agentCount;
        }

        /// <summary>
        /// 阶段一：清零计数，统计每格代理数并写回每代理的格下标。
        /// <paramref name="cellCounts"/> 长度须 &gt;= <paramref name="cellCount"/>。
        /// </summary>
        public static void Count(
            int3 gridSize,
            float3 origin,
            float cellSize,
            CollisionDimension dimension,
            float3* positions,
            int agentCount,
            int* cellIndices,
            int* cellCounts,
            int cellCount)
        {
            for (int i = 0; i < cellCount; i++) cellCounts[i] = 0;

            for (int i = 0; i < agentCount; i++)
            {
                int cell = Index(gridSize, CellOf(origin, cellSize, gridSize, positions[i], dimension));
                cellIndices[i] = cell;
                cellCounts[cell]++;
            }
        }

        /// <summary>
        /// 阶段三：把代理下标散入各自格的连续区间。
        /// <paramref name="cellCursors"/> 为工作副本，长度须 &gt;= <paramref name="cellCount"/>。
        /// </summary>
        public static void Scatter(
            int* cellIndices,
            int agentCount,
            int* cellStarts,
            int* cellCursors,
            int cellCount,
            int* sortedAgents)
        {
            for (int i = 0; i < cellCount; i++) cellCursors[i] = cellStarts[i];

            // 逐代理顺序写入：同格内保持代理下标升序，故结果与输入顺序确定相关。
            for (int i = 0; i < agentCount; i++)
                sortedAgents[cellCursors[cellIndices[i]]++] = i;
        }

        /// <summary>
        /// 每代理的邻居列表：扫格邻域，保留最近的 <paramref name="maxNeighbors"/> 个。
        ///
        /// 邻居判据取 <c>distSq &lt; sqr(max(NeighborDist, Radius_i + Radius_j))</c>
        /// （RVO2 语义；<see cref="NavAgent.NeighborDist"/> 与半径和取大者）。
        /// 2D 模式下距离只比平面内分量，与约束构建的投影口径一致。
        /// </summary>
        /// <param name="maxRadius">全部代理中的最大半径，用于把扫描半径上界取成与邻居无关的量。</param>
        /// <returns>邻居个数（&lt;= <paramref name="maxNeighbors"/>），按距离升序写入结果数组。</returns>
        public static int QueryNeighbors(
            int3 gridSize,
            float3 origin,
            float cellSize,
            int* cellStarts,
            int* cellCounts,
            int* sortedAgents,
            float3* positions,
            float* radii,
            float* neighborDists,
            int agentIndex,
            int maxNeighbors,
            float maxRadius,
            CollisionDimension dimension,
            int* neighbors,
            float* neighborDistancesSq)
        {
            if (maxNeighbors <= 0 || cellSize <= 0f) return 0;

            float3 position = positions[agentIndex];
            float radius = radii[agentIndex];
            float neighborDist = neighborDists[agentIndex];
            float3 planeNormal = NavPlane.Normal(dimension);

            int3 center = CellOf(origin, cellSize, gridSize, position, dimension);
            int reach = (int)math.ceil(math.max(neighborDist, radius + maxRadius) / cellSize);
            int reachX = math.min(reach, gridSize.x);
            int reachY = math.min(reach, gridSize.y);
            int reachZ = math.min(reach, gridSize.z);

            int found = 0;
            for (int dz = -reachZ; dz <= reachZ; dz++)
            {
                int z = center.z + dz;
                if ((uint)z >= (uint)gridSize.z) continue;
                for (int dy = -reachY; dy <= reachY; dy++)
                {
                    int y = center.y + dy;
                    if ((uint)y >= (uint)gridSize.y) continue;
                    for (int dx = -reachX; dx <= reachX; dx++)
                    {
                        int x = center.x + dx;
                        if ((uint)x >= (uint)gridSize.x) continue;

                        int cell = Index(gridSize, new int3(x, y, z));
                        int start = cellStarts[cell];
                        int end = start + cellCounts[cell];
                        for (int slot = start; slot < end; slot++)
                        {
                            int other = sortedAgents[slot];
                            if (other == agentIndex) continue;

                            float3 delta = NavPlane.Flatten(positions[other] - position, planeNormal);
                            float distanceSq = math.lengthsq(delta);
                            float threshold = math.max(neighborDist, radius + radii[other]);
                            if (distanceSq >= threshold * threshold) continue;

                            Insert(neighbors, neighborDistancesSq, ref found, maxNeighbors,
                                other, distanceSq);
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>按距离升序插入，超出容量时丢弃最远者。</summary>
        private static void Insert(
            int* neighbors, float* neighborDistancesSq, ref int count, int capacity,
            int candidate, float distanceSq)
        {
            if (count == capacity && distanceSq >= neighborDistancesSq[capacity - 1]) return;

            int position = count < capacity ? count : capacity - 1;
            while (position > 0 && neighborDistancesSq[position - 1] > distanceSq)
            {
                neighborDistancesSq[position] = neighborDistancesSq[position - 1];
                neighbors[position] = neighbors[position - 1];
                position--;
            }

            neighborDistancesSq[position] = distanceSq;
            neighbors[position] = candidate;
            if (count < capacity) count++;
        }
    }
}
