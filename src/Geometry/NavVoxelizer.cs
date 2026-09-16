using Ember.Collision;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 体素化核心（纯算法，裸指针，Burst 兼容）：
    /// 烘焙碰撞体 → 原始距离场（米，未量化）+ 占据位集。
    /// 距离场取「到全部碰撞体的有符号距离最小值」；&lt;= 0 的格标记占据。
    /// 初始化后未被任何碰撞体覆盖的格距离为 <see cref="float.MaxValue"/>（量化时饱和到满量程）。
    /// </summary>
    public static unsafe class NavVoxelizer
    {
        /// <summary>
        /// 计算烘焙包围盒：全部碰撞体世界 AABB 的并集，向外扩张
        /// <paramref name="margin"/>（= MaxBakeRadius，距离场满量程）。
        /// </summary>
        public static Aabb ComputeBounds(
            in NavBakeInput input,
            NavBakeCollider* colliders,
            int colliderCount,
            float3* vertexPool,
            int vertexPoolLength,
            float margin)
        {
            Aabb bounds = Aabb.Empty;
            for (int i = 0; i < colliderCount; i++)
            {
                Aabb world = WorldBounds(in colliders[i].Collider, in colliders[i].Pose,
                    input.Dimension, vertexPool, vertexPoolLength);
                bounds.Min = math.min(bounds.Min, world.Min);
                bounds.Max = math.max(bounds.Max, world.Max);
            }

            if (bounds.IsEmpty)
                return Aabb.FromCenterExtents(float3.zero, new float3(margin));

            bounds.Min -= margin;
            bounds.Max += margin;
            return bounds;
        }

        /// <summary>单个碰撞体的世界 AABB（2D 无效轴半范围保持为零，不膨胀）。</summary>
        public static Aabb WorldBounds(
            in Collider collider,
            in BodyPose pose,
            CollisionDimension dimension,
            float3* vertexPool,
            int vertexPoolLength)
        {
            ShapeBoundsMath.LocalBounds(collider, dimension,
                collider.Type == ShapeType.Polygon2D ? vertexPool + collider.Params.VertexStart : null,
                out float3 center, out float3 extents);

            // 2D 时忽略顶点池越界检查（LocalBounds 内部处理 null）；顶点数防护。
            if (collider.Type == ShapeType.Polygon2D
                && (vertexPool == null
                    || collider.Params.VertexCount < 0
                    || collider.Params.VertexStart < 0
                    || collider.Params.VertexStart > vertexPoolLength - collider.Params.VertexCount))
            {
                extents = float3.zero;
            }

            float3 worldCenter = pose.TransformPoint(center);
            float3 bx = math.abs(math.mul(pose.Rotation, new float3(1f, 0f, 0f)));
            float3 by = math.abs(math.mul(pose.Rotation, new float3(0f, 1f, 0f)));
            float3 bz = math.abs(math.mul(pose.Rotation, new float3(0f, 0f, 1f)));
            float3 worldExtents = bx * (extents.x * pose.Scale)
                + by * (extents.y * pose.Scale)
                + bz * (extents.z * pose.Scale);
            return Aabb.FromCenterExtents(worldCenter, worldExtents);
        }

        /// <summary>
        /// 烘焙距离场与占据位集。
        /// </summary>
        /// <param name="input">烘焙参数。</param>
        /// <param name="grid">目标网格（Dimensions 已按范围取整）。</param>
        /// <param name="colliders">碰撞体数组。</param>
        /// <param name="colliderCount">碰撞体数量。</param>
        /// <param name="vertexPool">多边形顶点池。</param>
        /// <param name="vertexPoolLength">顶点池长度。</param>
        /// <param name="distance">输出：每体素有符号距离（米），长度 = VoxelCount。</param>
        /// <param name="occupancy">输出：占据位集，长度 = ceil(VoxelCount / 8)。</param>
        public static void BakeDistanceField(
            in NavBakeInput input,
            in NavGrid grid,
            NavBakeCollider* colliders,
            int colliderCount,
            float3* vertexPool,
            int vertexPoolLength,
            float* distance,
            byte* occupancy)
        {
            long voxelCount = grid.VoxelCount;
            for (long i = 0; i < voxelCount; i++)
                distance[i] = float.MaxValue;
            long byteCount = (voxelCount + 7) / 8;
            for (long i = 0; i < byteCount; i++)
                occupancy[i] = 0;

            for (int c = 0; c < colliderCount; c++)
            {
                BakeCollider(in input, in grid, in colliders[c], vertexPool, vertexPoolLength,
                    distance, occupancy);
            }
        }

        private static void BakeCollider(
            in NavBakeInput input,
            in NavGrid grid,
            in NavBakeCollider entry,
            float3* vertexPool,
            int vertexPoolLength,
            float* distance,
            byte* occupancy)
        {
            Aabb world = WorldBounds(in entry.Collider, in entry.Pose,
                input.Dimension, vertexPool, vertexPoolLength);

            // 覆盖范围：世界 AABB 再扩张满量程（超出量程的距离全部饱和，无需覆盖）。
            float3 min = world.Min - input.MaxBakeRadius;
            float3 max = world.Max + input.MaxBakeRadius;
            int3 lo = math.max(grid.WorldToVoxel(min), int3.zero);
            int3 hi = math.min(grid.WorldToVoxel(max), grid.Dimensions - 1);

            for (int z = lo.z; z <= hi.z; z++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int3 voxel = new(x, y, z);
                float3 point = grid.VoxelToWorld(voxel);
                // ShapeQuery 内部按 collider.Params.VertexStart 访问完整池，不要预偏移。
                float signedDistance = ShapeQuery.ClosestPoint(
                    in entry.Collider, in entry.Pose, input.Dimension, point,
                    vertexPool, vertexPoolLength,
                    out _, out _);

                long index = grid.VoxelIndex(voxel);
                if (signedDistance < distance[index])
                    distance[index] = signedDistance;
                if (signedDistance <= 0f)
                    occupancy[index >> 3] |= (byte)(1u << (int)(index & 7));
            }
        }

        /// <summary>
        /// 应用标注层：可行走覆盖推翻占据位，代价乘数写入独立代价数组。
        /// </summary>
        /// <param name="costs">输出：每体素代价乘数（1.0 默认），长度 = VoxelCount。</param>
        public static void ApplyAnnotations(
            in NavGrid grid,
            NavBakeAnnotation* annotations,
            int annotationCount,
            float* distance,
            byte* occupancy,
            float* costs)
        {
            long voxelCount = grid.VoxelCount;
            for (long i = 0; i < voxelCount; i++)
                costs[i] = 1f;

            for (int a = 0; a < annotationCount; a++)
            {
                ref readonly NavBakeAnnotation annotation = ref annotations[a];
                if (annotation.WalkableOverride == 0 && annotation.CostMultiplier <= 0f) continue;

                int3 lo = math.max(grid.WorldToVoxel(annotation.Region.Min), int3.zero);
                int3 hi = math.min(grid.WorldToVoxel(annotation.Region.Max), grid.Dimensions - 1);

                for (int z = lo.z; z <= hi.z; z++)
                for (int y = lo.y; y <= hi.y; y++)
                for (int x = lo.x; x <= hi.x; x++)
                {
                    long index = grid.VoxelIndex(new int3(x, y, z));
                    if (annotation.WalkableOverride == 1)
                    {
                        occupancy[index >> 3] &= (byte)~(1u << (int)(index & 7));
                        if (distance[index] <= 0f) distance[index] = float.Epsilon;
                    }
                    else if (annotation.WalkableOverride == 2)
                    {
                        occupancy[index >> 3] |= (byte)(1u << (int)(index & 7));
                    }

                    if (annotation.CostMultiplier > 0f)
                        costs[index] = annotation.CostMultiplier;
                }
            }
        }
    }
}
