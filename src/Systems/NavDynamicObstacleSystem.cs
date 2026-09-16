using Ember.Core;
using Ember.Collision;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 动态障碍局部失效：监视碰撞快照里的非静态碰撞体，位姿变化时只重算受影响的
    /// 那块距离场与占据位，并推进 <see cref="NavWorld.FieldEpoch"/>。
    ///
    /// <b>为什么不全量重烘焙</b>：全量烘焙的代价随地图规模走，而一个移动箱子
    /// 真正影响的只有它周围 <c>MaxBakeRadius</c> 半径内那点体素。距离场是逐体素
    /// 独立量，局部重算在语义上与全量一致。
    ///
    /// <b>失效范围取保守外接球</b>：形状的世界 AABB 用外接球代替，宁可多算一圈
    /// 也不能漏 —— 漏算的体素会留下过期距离，表现为代理贴着看不见的墙。
    ///
    /// <b>波及面</b>：距离场变了，路面标号、簇图、流场、已有路径全部可能失效。
    /// 本系统只负责把距离场与占据位改对，并推进 FieldEpoch 让流场缓存自行作废；
    /// 已有路径的重请求由业务侧决定（本模块的导航代理本就允许在旧路径上继续走）。
    /// </summary>
    public sealed class NavDynamicObstacleSystem : SystemBase
    {
        private NativeArray<BodyPose> m_PreviousPoses;
        private NativeArray<Collider> m_PreviousColliders;
        private NativeArray<byte> m_PreviousFlags;
        private int m_PreviousCount;

        protected override void DeclareAccess(AccessBuilder access) => access
            .Read<NavConfig>()
            .Write<NavWorld>();

        public override void OnDestroy()
        {
            Dispose(ref m_PreviousPoses);
            Dispose(ref m_PreviousColliders);
            Dispose(ref m_PreviousFlags);
            base.OnDestroy();
        }

        protected override void OnTick(SystemContext ctx)
        {
            World world = ctx.World;
            if (!world.TryGetNavWorld(out NavWorldView navView) || !navView.IsReady) return;
            if (!world.TryGetCollisionWorld(out CollisionWorldView collision) || !collision.IsQueryReady)
                return;

            NavWorld state = navView.StateSnapshot;
            if (state.DistanceBits != 8) return;

            NativeArray<BodyPose> poses = collision.BodyPoses;
            NativeArray<Collider> colliders = collision.BodyColliders;
            NativeArray<byte> flags = collision.BodyFlags;
            NativeArray<float3> vertexPool = collision.VertexPool;

            if (!TryCaptureSnapshot(poses, colliders, flags))
            {
                // 快照规模变了（实体增删）：本轮不做增量，直接以新快照为准。
                // 地图结构变动应由业务侧触发一次运行时烘焙。
                return;
            }

            unsafe
            {
                byte* distance = (byte*)world.GetBuffer<byte>(state.Distance).UnsafePtr;
                byte* occupancy = (byte*)world.GetBuffer<byte>(state.Occupancy).UnsafePtr;
                float3* vertices = vertexPool.Length > 0
                    ? (float3*)vertexPool.GetUnsafePtr()
                    : null;

                bool changed = false;
                for (int i = 0; i < poses.Length; i++)
                {
                    if ((flags[i] & CollisionBody.StaticBit) != 0) continue;
                    if (!Moved(i, poses[i])) continue;

                    UpdateRegion(navView.Grid, state, poses, colliders, vertices, vertexPool.Length,
                        i, m_PreviousPoses[i], distance, occupancy);
                    changed = true;
                }

                if (changed) navView.BumpFieldEpoch();
            }
        }

        /// <summary>快照规模未变则记下新值并返回 true；规模变化时按新规模重建并返回 false。</summary>
        private bool TryCaptureSnapshot(
            NativeArray<BodyPose> poses, NativeArray<Collider> colliders, NativeArray<byte> flags)
        {
            if (m_PreviousCount == poses.Length
                && m_PreviousPoses.IsCreated
                && m_PreviousPoses.Length == poses.Length)
            {
                NativeArray<BodyPose>.Copy(poses, m_PreviousPoses);
                NativeArray<Collider>.Copy(colliders, m_PreviousColliders);
                NativeArray<byte>.Copy(flags, m_PreviousFlags);
                return true;
            }

            Dispose(ref m_PreviousPoses);
            Dispose(ref m_PreviousColliders);
            Dispose(ref m_PreviousFlags);

            m_PreviousCount = poses.Length;
            m_PreviousPoses = new NativeArray<BodyPose>(poses.Length, Allocator.Persistent);
            m_PreviousColliders = new NativeArray<Collider>(poses.Length, Allocator.Persistent);
            m_PreviousFlags = new NativeArray<byte>(poses.Length, Allocator.Persistent);

            NativeArray<BodyPose>.Copy(poses, m_PreviousPoses);
            NativeArray<Collider>.Copy(colliders, m_PreviousColliders);
            NativeArray<byte>.Copy(flags, m_PreviousFlags);
            return false;
        }

        private bool Moved(int index, in BodyPose current) =>
            math.distancesq(current.Position, m_PreviousPoses[index].Position) > 1e-8f
            || math.abs(math.dot(current.Rotation, m_PreviousPoses[index].Rotation)) < 0.9999999f
            || math.abs(current.Scale - m_PreviousPoses[index].Scale) > 1e-6f;

        /// <summary>
        /// 重算「旧位姿 ∪ 新位姿」外扩一个烘焙半径后的体素块：逐格取到全部碰撞体的
        /// 最小有符号距离，重新量化并更新占据位。
        /// </summary>
        private static unsafe void UpdateRegion(
            in NavGrid grid,
            in NavWorld state,
            NativeArray<BodyPose> poses,
            NativeArray<Collider> colliders,
            float3* vertices,
            int vertexPoolLength,
            int movedIndex,
            in BodyPose previousPose,
            byte* distance,
            byte* occupancy)
        {
            Collider moved = colliders[movedIndex];
            float influence = math.max(state.MaxBakeRadius, grid.VoxelSize);

            // 旧位姿与新位姿各算一块外接范围，取并集 —— 只算新位置会在障碍移走后
            // 把旧位置那圈过期距离留在场里。
            Aabb region = AffectedBounds(grid, moved, vertices, vertexPoolLength, poses[movedIndex], influence);
            region.Encapsulate(AffectedBounds(
                grid, moved, vertices, vertexPoolLength, previousPose, influence));

            int3 min = grid.WorldToVoxelOnGrid(region.Min);
            int3 max = grid.WorldToVoxelOnGrid(region.Max);
            min = math.max(min, int3.zero);
            max = math.min(max, grid.Dimensions - 1);

            int levels = state.DistanceBits == 16 ? 65535 : 255;
            float maxRadius = math.max(state.MaxBakeRadius, 1e-6f);
            CollisionDimension dimension = DimensionOf(grid);

            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var voxel = new int3(x, y, z);
                float3 world = grid.VoxelToWorld(voxel);

                float best = float.MaxValue;
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (!Intersects(colliders[i], vertices, vertexPoolLength, poses[i], world, influence))
                        continue;
                    float signed = ShapeQuery.ClosestPoint(colliders[i], poses[i], dimension, world,
                        vertices, vertexPoolLength, out _, out _);
                    if (signed < best) best = signed;
                }

                if (best == float.MaxValue) best = influence;

                long index = grid.VoxelIndex(voxel);
                float clamped = math.clamp(best, 0f, maxRadius);
                distance[index] = (byte)math.round(clamped / maxRadius * levels);
                SetOccupied(occupancy, index, best <= 0f);
            }
        }

        private static CollisionDimension DimensionOf(in NavGrid grid) =>
            grid.Dimensions.z == 1 ? CollisionDimension.XY
            : grid.Dimensions.y == 1 ? CollisionDimension.XZ
            : CollisionDimension.XYZ;

        /// <summary>形状外接球 + 影响半径构成的保守 AABB。</summary>
        private static unsafe Aabb AffectedBounds(
            in NavGrid grid, in Collider collider, float3* vertices, int vertexPoolLength,
            in BodyPose pose, float influence)
        {
            float radius = BoundingRadius(collider, vertices, vertexPoolLength) * math.abs(pose.Scale)
                + influence + grid.VoxelSize;
            return Aabb.FromCenterExtents(pose.Position, new float3(radius));
        }

        /// <summary>
        /// 形状外接球半径（局部空间）。宁可取大：漏算的体素会留下过期距离，
        /// 表现为代理贴着看不见的墙。
        /// </summary>
        private static unsafe float BoundingRadius(in Collider collider, float3* vertices, int vertexPoolLength)
        {
            switch (collider.Type)
            {
                case ShapeType.Sphere:
                case ShapeType.Circle:
                    return math.length(collider.Params.Center) + math.abs(collider.Params.Radius);

                case ShapeType.Capsule:
                case ShapeType.Capsule2D:
                    return math.length(collider.Params.Center)
                        + math.abs(collider.Params.HalfHeight)
                        + math.abs(collider.Params.Radius);

                case ShapeType.Box:
                case ShapeType.Box2D:
                    return math.length(collider.Params.Center) + math.length(collider.Params.Extents);

                case ShapeType.Polygon2D:
                {
                    int start = collider.Params.VertexStart;
                    int count = collider.Params.VertexCount;
                    if (vertices == null || start < 0 || start > vertexPoolLength - count)
                        return math.length(collider.Params.Center);

                    float farthest = 0f;
                    for (int i = 0; i < count; i++)
                        farthest = math.max(farthest,
                            math.length(collider.Params.Center + vertices[start + i]));
                    return farthest;
                }

                default:
                    return 0f;
            }
        }

        /// <summary>碰撞体是否可能影响到该世界点（外接球 + 影响半径的粗筛）。</summary>
        private static unsafe bool Intersects(
            in Collider collider, float3* vertices, int vertexPoolLength,
            in BodyPose pose, float3 world, float influence)
        {
            float radius = BoundingRadius(collider, vertices, vertexPoolLength) * math.abs(pose.Scale)
                + influence;
            return math.distancesq(world, pose.Position) <= radius * radius;
        }

        private static unsafe void SetOccupied(byte* occupancy, long voxelIndex, bool occupied)
        {
            long byteIndex = voxelIndex >> 3;
            byte mask = (byte)(1u << (int)(voxelIndex & 7));
            if (occupied) occupancy[byteIndex] |= mask;
            else occupancy[byteIndex] &= (byte)~mask;
        }

        private static void Dispose<T>(ref NativeArray<T> array) where T : unmanaged
        {
            if (array.IsCreated) array.Dispose();
            array = default;
        }
    }
}
