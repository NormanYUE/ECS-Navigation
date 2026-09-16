using Ember;
using Ember.Collision;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 导航世界操作视图（轻量 struct：World + 单例实体），与
    /// <see cref="CollisionWorldView"/> 同一资源模型。
    /// 加载 = 每段一次 ResizeBuffer + 一次 MemCpy（需要 Ember ≥ 1.11.0）。
    ///
    /// 热替换语义：本次加载写入「备用槽或新 buffer」，旧当前句柄降级为备用槽
    /// （旧数据本帧仍可被在途系统读完）；备用槽在<b>下一次</b>加载时被复用或销毁
    /// （两代之前的旧数据，在途读取早已结束）。重烘焙不可能同帧发生两次。
    /// </summary>
    public struct NavWorldView
    {
        private readonly World m_World;
        private readonly Entity m_Owner;

        internal NavWorldView(World world, Entity owner)
        {
            m_World = world;
            m_Owner = owner;
        }

        /// <summary>只读快照（读取路径专用）。</summary>
        private readonly NavWorld State => m_World.GetComponent<NavWorld>(m_Owner);

        /// <summary>可变引用（写入路径专用）。</summary>
        private ref NavWorld MutableState => ref m_World.GetComponent<NavWorld>(m_Owner);

        /// <summary>数据是否已加载。</summary>
        public readonly bool IsReady => State.IsReady;

        /// <summary>当前数据代际。</summary>
        public readonly int Generation => State.Generation;

        /// <summary>重建网格描述（从单例标量）。</summary>
        public readonly NavGrid Grid => new()
        {
            Origin = new float3(State.OriginX, State.OriginY, State.OriginZ),
            VoxelSize = State.VoxelSize,
            Dimensions = new int3(State.DimX, State.DimY, State.DimZ),
            TileSize = State.TileSize,
        };

        /// <summary>
        /// 从 blob 加载（或热替换）全部段。必须在调度任何 Job 之前调用
        /// （Resize 会搬移地址）。blob 需先通过 <see cref="NavBlobReader.Validate"/>。
        /// </summary>
        public unsafe void LoadBlob(byte* blob)
        {
            var header = NavBlobReader.ReadHeader(blob);
            ref var state = ref MutableState;

            long voxelCount = (long)header.Dimensions.x * header.Dimensions.y * header.Dimensions.z;
            long occupancyBytes = (voxelCount + 7) / 8;
            int distanceElement = header.DistanceBits == 16 ? 2 : 1;
            long edgePortalCount = header.SegmentLengths[(int)NavBlobSegment.EdgePortals] / sizeof(NavPortal);

            state.Occupancy = LoadSegment<byte>(state.Occupancy, ref state.SpareOccupancy,
                blob, NavBlobSegment.Occupancy, occupancyBytes);
            state.Distance = LoadSegment<byte>(state.Distance, ref state.SpareDistance,
                blob, NavBlobSegment.Distance, voxelCount * distanceElement);
            state.Region = LoadSegment<int>(state.Region, ref state.SpareRegion,
                blob, NavBlobSegment.Region, voxelCount);
            state.Cost = LoadSegment<byte>(state.Cost, ref state.SpareCost,
                blob, NavBlobSegment.Cost, voxelCount);
            state.ClusterNodes = LoadSegment<NavClusterNode>(state.ClusterNodes, ref state.SpareClusterNodes,
                blob, NavBlobSegment.ClusterNodes, header.ClusterNodeCount);
            state.ClusterPortals = LoadSegment<NavPortal>(state.ClusterPortals, ref state.SpareClusterPortals,
                blob, NavBlobSegment.ClusterPortals, header.PortalCount);
            state.ClusterEdges = LoadSegment<NavClusterEdge>(state.ClusterEdges, ref state.SpareClusterEdges,
                blob, NavBlobSegment.ClusterEdges, header.ClusterEdgeCount);
            state.EdgePortals = LoadSegment<NavPortal>(state.EdgePortals, ref state.SpareEdgePortals,
                blob, NavBlobSegment.EdgePortals, edgePortalCount);
            state.Links = LoadSegment<NavOffMeshLink>(state.Links, ref state.SpareLinks,
                blob, NavBlobSegment.Links, header.LinkCount);

            state.OriginX = header.Origin.x;
            state.OriginY = header.Origin.y;
            state.OriginZ = header.Origin.z;
            state.VoxelSize = header.VoxelSize;
            state.DimX = header.Dimensions.x;
            state.DimY = header.Dimensions.y;
            state.DimZ = header.Dimensions.z;
            state.TileSize = header.TileSize;
            state.MaxBakeRadius = header.MaxBakeRadius;
            state.VoxelCount = voxelCount;
            state.RegionCount = header.RegionCount;
            state.ClusterNodeCount = header.ClusterNodeCount;
            state.PortalCount = header.PortalCount;
            state.ClusterEdgeCount = header.ClusterEdgeCount;
            state.LinkCount = header.LinkCount;
            state.DistanceBits = (byte)header.DistanceBits;
            state.Generation++;
            state.Ready = 1;
        }

        /// <summary>
        /// 单段加载：写入目标 = 备用槽（容量够，零分配）或新 buffer（旧备用槽销毁）；
        /// 写完后旧当前句柄降级为备用槽，保留一整个加载周期供在途读取。
        /// </summary>
        private unsafe BufferHandle LoadSegment<T>(
            BufferHandle current,
            ref BufferHandle spare,
            byte* blob,
            NavBlobSegment segment,
            long elementCount)
            where T : unmanaged
        {
            BufferHandle target;
            if (elementCount <= 0)
            {
                // 空段：不分配；旧备用销毁，旧当前降级为备用。
                if (!spare.IsNull) m_World.DestroyBuffer<T>(spare);
                target = BufferHandle.Null;
            }
            else
            {
                bool reuseSpare = !spare.IsNull && m_World.GetBufferLength<T>(spare) >= elementCount;
                if (reuseSpare)
                {
                    target = spare;
                }
                else
                {
                    if (!spare.IsNull)
                    {
                        m_World.DestroyBuffer<T>(spare);
                    }
                    target = m_World.CreateBuffer<T>((int)elementCount);
                }

                m_World.ResizeBuffer<T>(target, (int)elementCount);
                var span = m_World.GetBuffer<T>(target);
                byte* source = NavBlobReader.Segment(blob, segment);
                UnsafeUtility.MemCpy(span.UnsafePtr, source, elementCount * sizeof(T));
            }

            spare = current;
            return target;
        }

        // ---- 运行时查询（量化读取）----

        /// <summary>体素可通行判定：在界、未占据、距离场量化值 ≥ 半径量化值。</summary>
        public readonly bool IsWalkable(int3 voxel, float agentRadius)
        {
            NavWorld state = State;
            var grid = Grid;
            if (!grid.IsInside(voxel)) return false;
            long index = grid.VoxelIndex(voxel);

            var occupancy = m_World.GetBuffer<byte>(state.Occupancy);
            if ((occupancy[(int)(index >> 3)] & (byte)(1u << (int)(index & 7))) != 0) return false;

            int levels = state.DistanceBits == 16 ? 65535 : 255;
            float maxRadius = math.max(state.MaxBakeRadius, 1e-6f);
            int required = (int)math.round(math.clamp(agentRadius, 0f, maxRadius) / maxRadius * levels);

            if (state.DistanceBits == 16)
            {
                var distance = m_World.GetBuffer<ushort>(state.Distance);
                return distance[(int)index] >= required;
            }
            else
            {
                var distance = m_World.GetBuffer<byte>(state.Distance);
                return distance[(int)index] >= required;
            }
        }

        /// <summary>体素区域 id（越界 / 占据返回 -1）。</summary>
        public readonly int RegionAt(int3 voxel)
        {
            NavWorld state = State;
            var grid = Grid;
            if (!grid.IsInside(voxel)) return -1;
            long index = grid.VoxelIndex(voxel);
            var occupancy = m_World.GetBuffer<byte>(state.Occupancy);
            if ((occupancy[(int)(index >> 3)] & (byte)(1u << (int)(index & 7))) != 0) return -1;
            return m_World.GetBuffer<int>(state.Region)[(int)index];
        }

        /// <summary>占据判定（越界视为占据）。</summary>
        public readonly bool IsOccupied(int3 voxel)
        {
            NavWorld state = State;
            var grid = Grid;
            if (!grid.IsInside(voxel)) return true;
            long index = grid.VoxelIndex(voxel);
            var occupancy = m_World.GetBuffer<byte>(state.Occupancy);
            return (occupancy[(int)(index >> 3)] & (byte)(1u << (int)(index & 7))) != 0;
        }
    }
}
