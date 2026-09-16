using System;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// blob 序列化：把烘焙产物各段写入连续字节缓冲（磁盘格式）。
    /// 布局：头部（Meta）+ 按 8 字节对齐的各段；偏移表在头部内。
    /// </summary>
    public static unsafe class NavBlobWriter
    {
        /// <summary>烘焙产物的段尺寸（字节）。</summary>
        public struct SegmentSizes
        {
            /// <summary>Meta 段（头部）大小。</summary>
            public long Meta;

            /// <summary>占据位集大小。</summary>
            public long Occupancy;

            /// <summary>距离场大小。</summary>
            public long Distance;

            /// <summary>区域 id 大小。</summary>
            public long Region;

            /// <summary>代价大小。</summary>
            public long Cost;

            /// <summary>簇节点大小。</summary>
            public long ClusterNodes;

            /// <summary>门户大小。</summary>
            public long ClusterPortals;

            /// <summary>边大小。</summary>
            public long ClusterEdges;

            /// <summary>边门户紧凑表大小。</summary>
            public long EdgePortals;

            /// <summary>link 大小。</summary>
            public long Links;

            /// <summary>每体素簇 id 大小。</summary>
            public long VoxelNodes;

            /// <summary>blob 总大小。</summary>
            public long Total;
        }

        /// <summary>按体素数与簇图计数计算段尺寸。</summary>
        /// <param name="voxelCount">体素数。</param>
        /// <param name="distanceBits">量化位宽（8 或 16）。</param>
        /// <param name="clusterNodeCount">簇节点数。</param>
        /// <param name="portalCount">有向门户数。</param>
        /// <param name="clusterEdgeCount">无向边数。</param>
        /// <param name="linkCount">off-mesh link 数。</param>
        public static SegmentSizes ComputeSizes(
            long voxelCount,
            int distanceBits,
            int clusterNodeCount,
                int portalCount,
            int clusterEdgeCount,
            int linkCount)
        {
            int distanceElement = distanceBits == 16 ? 2 : 1;
            var sizes = new SegmentSizes
            {
                Meta = sizeof(NavBlobHeader),
                Occupancy = (voxelCount + 7) / 8,
                Distance = voxelCount * distanceElement,
                Region = voxelCount * sizeof(int),
                Cost = voxelCount,
                ClusterNodes = (long)clusterNodeCount * sizeof(NavClusterNode),
                ClusterPortals = (long)portalCount * sizeof(NavPortal),
                ClusterEdges = (long)clusterEdgeCount * sizeof(NavClusterEdge),
                EdgePortals = (long)portalCount / 2 * sizeof(NavPortal),
                Links = (long)linkCount * sizeof(NavOffMeshLink),
                VoxelNodes = voxelCount * sizeof(int),
            };
            sizes.Total = Total(sizes);
            return sizes;
        }

        /// <summary>段尺寸 → 对齐后总字节数。</summary>
        public static long Total(SegmentSizes sizes)
        {
            long total = Align8(sizes.Meta);
            total += Align8(sizes.Occupancy);
            total += Align8(sizes.Distance);
            total += Align8(sizes.Region);
            total += Align8(sizes.Cost);
            total += Align8(sizes.ClusterNodes);
            total += Align8(sizes.ClusterPortals);
            total += Align8(sizes.ClusterEdges);
            total += Align8(sizes.EdgePortals);
            total += Align8(sizes.Links);
            total += Align8(sizes.VoxelNodes);
            return total;
        }

        /// <summary>
        /// 写入 blob。
        /// </summary>
        /// <param name="grid">网格（体素尺寸需与 destination 容量匹配）。</param>
        /// <param name="input">烘焙参数。</param>
        /// <param name="bounds">世界包围盒。</param>
        /// <param name="distance">原始距离场（米）。</param>
        /// <param name="occupancy">占据位集。</param>
        /// <param name="costs">每体素代价乘数（float）。</param>
        /// <param name="regionIds">每体素区域 id。</param>
        /// <param name="regionCount">区域数。</param>
        /// <param name="nodes">簇节点。</param>
        /// <param name="portals">簇门户。</param>
        /// <param name="edges">簇边。</param>
        /// <param name="edgeCount">边数。</param>
        /// <param name="edgePortals">边门户紧凑表。</param>
        /// <param name="links">off-mesh link。</param>
        /// <param name="linkCount">link 数。</param>
        /// <param name="destination">目标缓冲（长度 ≥ Total）。</param>
        /// <returns>写入字节数。</returns>
        public static long Write(
            in NavGrid grid,
            in NavBakeInput input,
            in Ember.Collision.Aabb bounds,
            float* distance,
            byte* occupancy,
            float* costs,
            int* regionIds,
            int* voxelNodes,
            int regionCount,
            NavClusterNode* nodes,
            int nodeCount,
            NavPortal* portals,
            int portalCount,
            NavClusterEdge* edges,
            int edgeCount,
            NavPortal* edgePortals,
            NavOffMeshLink* links,
            int linkCount,
            byte* destination)
        {
            long voxelCount = grid.VoxelCount;
            SegmentSizes sizes = ComputeSizes(voxelCount, input.DistanceBits,
                nodeCount, portalCount, edgeCount, linkCount);

            var header = new NavBlobHeader
            {
                MagicValue = NavBlobHeader.Magic,
                Version = NavBlobHeader.CurrentVersion,
                Dimension = input.Dimension,
                DistanceBits = input.DistanceBits,
                VoxelSize = input.VoxelSize,
                MaxBakeRadius = input.MaxBakeRadius,
                TileSize = input.TileSize,
                Origin = grid.Origin,
                Dimensions = grid.Dimensions,
                Bounds = bounds,
                RegionCount = regionCount,
                ClusterNodeCount = nodeCount,
                PortalCount = portalCount,
                ClusterEdgeCount = edgeCount,
                LinkCount = linkCount,
            };

            long* offsets = stackalloc long[(int)NavBlobSegment.Count];
            long cursor = Align8(sizeof(NavBlobHeader));
            offsets[(int)NavBlobSegment.Meta] = 0;

            cursor = PlanSegment(offsets, (int)NavBlobSegment.Occupancy, cursor, sizes.Occupancy);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.Distance, cursor, sizes.Distance);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.Region, cursor, sizes.Region);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.Cost, cursor, sizes.Cost);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.ClusterNodes, cursor, sizes.ClusterNodes);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.ClusterPortals, cursor, sizes.ClusterPortals);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.ClusterEdges, cursor, sizes.ClusterEdges);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.EdgePortals, cursor, sizes.EdgePortals);
            cursor = PlanSegment(offsets, (int)NavBlobSegment.Links, cursor, sizes.Links);
            PlanSegment(offsets, (int)NavBlobSegment.VoxelNodes, cursor, sizes.VoxelNodes);

            for (int s = 0; s < (int)NavBlobSegment.Count; s++)
            {
                header.SegmentOffsets[s] = offsets[s];
                header.SegmentLengths[s] = SegmentLength(s, sizes);
            }

            // 写头部。
            byte* p = destination;
            *(NavBlobHeader*)p = header;

            // Occupancy：位集原样拷贝。
            byte* occupancyDst = p + offsets[(int)NavBlobSegment.Occupancy];
            for (long i = 0; i < sizes.Occupancy; i++) occupancyDst[i] = occupancy[i];

            // Distance：量化。
            if (input.DistanceBits == 16)
            {
                ushort* dst = (ushort*)(p + offsets[(int)NavBlobSegment.Distance]);
                for (long i = 0; i < voxelCount; i++)
                    dst[i] = Quantize16(distance[i], input.MaxBakeRadius);
            }
            else
            {
                byte* dst = p + offsets[(int)NavBlobSegment.Distance];
                for (long i = 0; i < voxelCount; i++)
                    dst[i] = Quantize8(distance[i], input.MaxBakeRadius);
            }

            // Region。
            int* regionDst = (int*)(p + offsets[(int)NavBlobSegment.Region]);
            for (long i = 0; i < voxelCount; i++) regionDst[i] = regionIds[i];

            // Cost：量化（85 = 1.0x，clamp 到 0.25x–3.0x）。
            byte* costDst = p + offsets[(int)NavBlobSegment.Cost];
            for (long i = 0; i < voxelCount; i++)
            {
                float clamped = math.clamp(costs[i], 0.25f, 3f);
                costDst[i] = (byte)math.round(clamped * 85f);
            }

            WriteSegment(p + offsets[(int)NavBlobSegment.ClusterNodes], nodes, nodeCount * sizeof(NavClusterNode));
            WriteSegment(p + offsets[(int)NavBlobSegment.ClusterPortals], portals, portalCount * sizeof(NavPortal));
            WriteSegment(p + offsets[(int)NavBlobSegment.ClusterEdges], edges, edgeCount * sizeof(NavClusterEdge));
            WriteSegment(p + offsets[(int)NavBlobSegment.EdgePortals], edgePortals,
                sizes.EdgePortals);
            WriteSegment(p + offsets[(int)NavBlobSegment.Links], links, linkCount * sizeof(NavOffMeshLink));

            WriteSegment(p + offsets[(int)NavBlobSegment.VoxelNodes], voxelNodes, sizes.VoxelNodes);

            return sizes.Total;
        }

        /// <summary>距离 → 8 位量化（MaxValue 饱和到 255，负值到 0）。</summary>
        public static byte Quantize8(float distance, float maxRadius)
        {
            if (distance >= maxRadius) return 255;
            if (distance <= 0f) return 0;
            return (byte)math.round(distance / maxRadius * 255f);
        }

        /// <summary>距离 → 16 位量化。</summary>
        public static ushort Quantize16(float distance, float maxRadius)
        {
            if (distance >= maxRadius) return 65535;
            if (distance <= 0f) return 0;
            return (ushort)math.round(distance / maxRadius * 65535f);
        }

        private static long SegmentLength(int segment, SegmentSizes sizes)
        {
            return segment switch
            {
                (int)NavBlobSegment.Meta => sizes.Meta,
                (int)NavBlobSegment.Occupancy => sizes.Occupancy,
                (int)NavBlobSegment.Distance => sizes.Distance,
                (int)NavBlobSegment.Region => sizes.Region,
                (int)NavBlobSegment.Cost => sizes.Cost,
                (int)NavBlobSegment.ClusterNodes => sizes.ClusterNodes,
                (int)NavBlobSegment.ClusterPortals => sizes.ClusterPortals,
                (int)NavBlobSegment.ClusterEdges => sizes.ClusterEdges,
                (int)NavBlobSegment.EdgePortals => sizes.EdgePortals,
                (int)NavBlobSegment.Links => sizes.Links,
                (int)NavBlobSegment.VoxelNodes => sizes.VoxelNodes,
                _ => 0,
            };
        }

        private static long PlanSegment(long* offsets, int segment, long cursor, long size)
        {
            offsets[segment] = cursor;
            return cursor + Align8(size);
        }

        private static long Align8(long value) => (value + 7) & ~7L;

        private static void WriteSegment<T>(byte* destination, T* source, long bytes)
            where T : unmanaged
        {
            Buffer.MemoryCopy(source, destination, bytes, bytes);
        }
    }
}
