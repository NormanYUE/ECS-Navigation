using System;
using Ember.Collision;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 烘焙核心 façade（一条核心，两侧薄壳 —— Editor 与 Runtime 共用）：
    /// 体素化 → 标注 → 连通区域 → 簇图 → blob 序列化。
    /// 纯算法：全部缓冲区由调用方提供（CLI 测试用堆 / 栈内存，运行时用 NativeArray）。
    /// 容量决策走两遍式：先 <see cref="Plan"/> 拿各阶段尺寸，再 <see cref="Bake"/> 填充。
    /// </summary>
    public static unsafe class NavBaker
    {
        /// <summary>烘焙计划（容量决策结果）。</summary>
        public struct PlanResult
        {
            /// <summary>网格（体素尺寸已按包围盒与体素大小取整）。</summary>
            public NavGrid Grid;

            /// <summary>体素数。</summary>
            public long VoxelCount;

            /// <summary>tile 数。</summary>
            public long TileCount;

            /// <summary>原始距离场缓冲字节数（float / 体素）。</summary>
            public long DistanceBytes;

            /// <summary>占据位集字节数。</summary>
            public long OccupancyBytes;

            /// <summary>代价数组字节数（float / 体素）。</summary>
            public long CostBytes;

            /// <summary>union-find 父指针字节数（int / 体素）。</summary>
            public long ParentBytes;

            /// <summary>区域 id 字节数（int / 体素）。</summary>
            public long RegionBytes;

            /// <summary>节点查找表字节数（int × TileCount × maxRegionCount）。</summary>
            public long NodeLookupBytes;

            /// <summary>簇图计数（边上界，精确边数以 Bake 返回值为准）。</summary>
            public NavClusterGraphBuilder.Counts ClusterCounts;

            /// <summary>blob 总字节数（按簇图计数上界）。</summary>
            public long BlobBytes;
        }

        /// <summary>
        /// 计划阶段：确定网格尺寸与全部缓冲大小。
        /// </summary>
        /// <param name="input">烘焙参数。</param>
        /// <param name="colliders">碰撞体数组。</param>
        /// <param name="colliderCount">碰撞体数量。</param>
        /// <param name="vertexPool">多边形顶点池。</param>
        /// <param name="vertexPoolLength">顶点池长度。</param>
        /// <param name="linkCount">off-mesh link 数量。</param>
        /// <param name="maxRegionCountUpperBound">区域数上界（调用方估计；仅影响节点查找表大小，默认 64）。</param>
        public static PlanResult Plan(
            in NavBakeInput input,
            NavBakeCollider* colliders,
            int colliderCount,
            float3* vertexPool,
            int vertexPoolLength,
            int linkCount,
            int maxRegionCountUpperBound = 64)
        {
            Aabb bounds = NavVoxelizer.ComputeBounds(in input, colliders, colliderCount,
                vertexPool, vertexPoolLength, input.MaxBakeRadius);

            // 体素尺寸：包围盒 / 体素边长向上取整；原点对齐到体素网格。
            int3 dims = math.max((int3)math.ceil((bounds.Max - bounds.Min) / input.VoxelSize), 1);
            if (input.Dimension == CollisionDimension.XY) dims.z = 1;
            else if (input.Dimension == CollisionDimension.XZ) dims.y = 1;

            var grid = new NavGrid
            {
                Origin = bounds.Min,
                VoxelSize = input.VoxelSize,
                Dimensions = dims,
                TileSize = input.TileSize,
            };

            long voxelCount = grid.VoxelCount;
            var counts = new NavClusterGraphBuilder.Counts
            {
                Nodes = (int)math.min(grid.TileCount * maxRegionCountUpperBound, voxelCount),
                Portals = (int)math.min(voxelCount * 6, int.MaxValue),
                EdgesBound = (int)math.min(voxelCount * 3, int.MaxValue),
            };

            var sizes = NavBlobWriter.ComputeSizes(voxelCount, input.DistanceBits,
                counts.Nodes, counts.Portals, counts.EdgesBound, linkCount);

            return new PlanResult
            {
                Grid = grid,
                VoxelCount = voxelCount,
                TileCount = grid.TileCount,
                DistanceBytes = voxelCount * sizeof(float),
                OccupancyBytes = (voxelCount + 7) / 8,
                CostBytes = voxelCount * sizeof(float),
                ParentBytes = voxelCount * sizeof(int),
                RegionBytes = voxelCount * sizeof(int),
                NodeLookupBytes = grid.TileCount * maxRegionCountUpperBound * sizeof(int),
                ClusterCounts = counts,
                BlobBytes = sizes.Total,
            };
        }

        /// <summary>
        /// 执行烘焙并写入 blob。
        /// </summary>
        /// <param name="plan"><see cref="Plan"/> 的结果。</param>
        /// <param name="input">烘焙参数（同 Plan）。</param>
        /// <param name="colliders">碰撞体数组。</param>
        /// <param name="colliderCount">碰撞体数量。</param>
        /// <param name="vertexPool">多边形顶点池。</param>
        /// <param name="vertexPoolLength">顶点池长度。</param>
        /// <param name="annotations">标注层（可 null）。</param>
        /// <param name="annotationCount">标注数量。</param>
        /// <param name="links">off-mesh link（可 null）。</param>
        /// <param name="linkCount">link 数量。</param>
        /// <param name="distance">距离场缓冲（≥ DistanceBytes）。</param>
        /// <param name="occupancy">占据位集缓冲（≥ OccupancyBytes）。</param>
        /// <param name="costs">代价缓冲（≥ CostBytes）。</param>
        /// <param name="parent">union-find 缓冲（≥ ParentBytes）。</param>
        /// <param name="regionIds">区域 id 缓冲（≥ RegionBytes）。</param>
        /// <param name="nodeLookup">节点查找表（≥ NodeLookupBytes，调用前全部置 -1）。</param>
        /// <param name="clusterScratch">簇图工作缓冲（节点门户计数，≥ 节点数 × sizeof(int)）。</param>
        /// <param name="nodes">簇节点输出（≥ ClusterCounts.Nodes）。</param>
        /// <param name="portals">簇门户输出（≥ ClusterCounts.Portals）。</param>
        /// <param name="edges">簇边输出（≥ ClusterCounts.EdgesBound）。</param>
        /// <param name="edgeKeys">边去重工作缓冲（≥ EdgesBound × sizeof(long)）。</param>
        /// <param name="edgePortalCounts">边去重工作缓冲（≥ EdgesBound × sizeof(int)）。</param>
        /// <param name="edgePortals">边门户紧凑输出（≥ Portals / 2）。</param>
        /// <param name="blob">blob 输出缓冲（≥ BlobBytes）。</param>
        /// <returns>写入 blob 的字节数。</returns>
        public static long Bake(
            in PlanResult plan,
            in NavBakeInput input,
            NavBakeCollider* colliders,
            int colliderCount,
            float3* vertexPool,
            int vertexPoolLength,
            NavBakeAnnotation* annotations,
            int annotationCount,
            NavOffMeshLink* links,
            int linkCount,
            float* distance,
            byte* occupancy,
            float* costs,
            int* parent,
            int* regionIds,
            int* nodeLookup,
            int* clusterScratch,
            NavClusterNode* nodes,
            NavPortal* portals,
            NavClusterEdge* edges,
            long* edgeKeys,
            int* edgePortalCounts,
            NavPortal* edgePortals,
            byte* blob)
        {
            NavVoxelizer.BakeDistanceField(in input, in plan.Grid, colliders, colliderCount,
                vertexPool, vertexPoolLength, distance, occupancy);

            if (annotations != null && annotationCount > 0)
            {
                NavVoxelizer.ApplyAnnotations(in plan.Grid, annotations, annotationCount,
                    distance, occupancy, costs);
            }

            int regionCount = NavRegionLabeler.Label(in plan.Grid, input.Connectivity,
                occupancy, parent, regionIds);

            // 区域数超过上界时节点查找表会越界 —— Plan 的上界是软估计，
            // 真实表长 = TileCount × regionCount，regionCount ≤ 体素数。
            // 调用方应给足 NodeLookupBytes；此处校验并 fail-fast。
            long requiredLookup = plan.TileCount * regionCount * sizeof(int);
            if (requiredLookup > plan.NodeLookupBytes)
            {
                throw new InvalidOperationException(
                    $"NavBaker.Bake: region count {regionCount} exceeds Plan upper bound; " +
                    $"node lookup needs {requiredLookup} bytes but only {plan.NodeLookupBytes} provided.");
            }

            var counts = NavClusterGraphBuilder.Count(in plan.Grid, regionIds, regionCount, nodeLookup);
            if (counts.Portals > plan.ClusterCounts.Portals || counts.EdgesBound > plan.ClusterCounts.EdgesBound)
            {
                throw new InvalidOperationException(
                    "NavBaker.Bake: cluster graph exceeds Plan upper bound; enlarge Plan parameters.");
            }

            int edgeCount = NavClusterGraphBuilder.Build(in plan.Grid, regionIds, regionCount,
                nodeLookup, clusterScratch, nodes, portals, edges, edgeKeys, edgePortalCounts, edgePortals);

            Aabb bounds = new(plan.Grid.Origin,
                plan.Grid.Origin + (float3)plan.Grid.Dimensions * plan.Grid.VoxelSize);

            return NavBlobWriter.Write(in plan.Grid, in input, in bounds,
                distance, occupancy, costs, regionIds, regionCount,
                nodes, counts.Nodes, portals, counts.Portals, edges, edgeCount, edgePortals,
                links, linkCount, blob);
        }
    }
}
