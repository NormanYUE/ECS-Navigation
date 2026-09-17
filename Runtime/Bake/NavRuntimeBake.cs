using Ember;
using Ember.Collision;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 运行时烘焙：从碰撞世界的当前帧快照取静态碰撞体，跑完整烘焙管线，
    /// 结果直接热替换进 <see cref="NavWorld"/>。
    ///
    /// 为什么从碰撞快照取而不是自己扫场景：碰撞模块已经把 Collider / 位姿 /
    /// 层 / 顶点池收拢成稠密数组（N2），且它自己处理了 Archetype 遍历与
    /// 多边形顶点池维护 —— 重扫一遍既是重复实现，也会和它的口径分叉。
    ///
    /// <b>只烘焙静态碰撞体</b>：动态障碍进烘焙会把会动的东西固化进距离场，
    /// 它们该由局部失效处理（见 <c>NavDynamicObstacleSystem</c>）。
    ///
    /// 全程在调用线程同步执行，必须在没有在途 Job 的时机调用 ——
    /// 热替换会重建段缓冲并搬移地址。
    /// </summary>
    public static unsafe class NavRuntimeBake
    {
        /// <summary>
        /// 从碰撞快照收集烘焙碰撞体。
        /// </summary>
        /// <param name="staticOnly">true 时只收静态碰撞体（运行时烘焙的常规用法）。</param>
        /// <returns>写入的碰撞体数量；超过 <paramref name="capacity"/> 时返回 -1。</returns>
        public static unsafe int GatherColliders(
            World world, bool staticOnly, NavBakeCollider* destination, int capacity)
        {
            if (!world.TryGetCollisionWorld(out CollisionWorldView view) || !view.IsQueryReady)
                return -1;

            var poses = (BodyPose*)view.BodyPosesPtr;
            var colliders = (Collider*)view.BodyCollidersPtr;
            var filters = (CollisionFilter*)view.BodyFiltersPtr;
            var flags = (byte*)view.BodyFlagsPtr;
            int bodyCount = view.BodyCount;

            int written = 0;
            for (int i = 0; i < bodyCount; i++)
            {
                byte bodyFlags = flags[i];
                if (staticOnly && (bodyFlags & CollisionBody.StaticBit) == 0) continue;
                if (written >= capacity) return -1;

                destination[written++] = new NavBakeCollider
                {
                    Collider = colliders[i],
                    Pose = poses[i],
                    Filter = filters[i],
                    Flags = bodyFlags,
                };
            }

            return written;
        }

        /// <summary>计划 + 烘焙，把 blob 写进工作区的 blob 缓冲。</summary>
        /// <returns>blob 实际字节数；失败返回 -1。</returns>
        public static long Bake(
            in NavBakeInput input,
            NavBakeCollider* colliders,
            int colliderCount,
            float3* vertexPool,
            int vertexPoolLength,
            NavRuntimeBakeWorkspace workspace,
            NavBakeAnnotation* annotations = null,
            int annotationCount = 0,
            NavOffMeshLink* links = null,
            int linkCount = 0)
        {
            NavBaker.PlanResult plan = NavBaker.Plan(
                in input, colliders, colliderCount, vertexPool, vertexPoolLength, linkCount);
            if (plan.BlobBytes <= 0) return -1;

            workspace.Ensure(in plan);
            return NavBaker.Bake(
                in plan, in input, colliders, colliderCount, vertexPool, vertexPoolLength,
                annotations, annotationCount, links, linkCount,
                (float*)workspace.Distance.GetUnsafePtr(),
                (byte*)workspace.Occupancy.GetUnsafePtr(),
                (float*)workspace.Costs.GetUnsafePtr(),
                (int*)workspace.Parent.GetUnsafePtr(),
                (int*)workspace.Region.GetUnsafePtr(),
                (int*)workspace.NodeLookup.GetUnsafePtr(),
                (int*)workspace.VoxelNodes.GetUnsafePtr(),
                (int*)workspace.ClusterScratch.GetUnsafePtr(),
                (NavClusterNode*)workspace.Nodes.GetUnsafePtr(),
                (NavPortal*)workspace.Portals.GetUnsafePtr(),
                (NavClusterEdge*)workspace.Edges.GetUnsafePtr(),
                (long*)workspace.EdgeKeys.GetUnsafePtr(),
                (int*)workspace.EdgeCounts.GetUnsafePtr(),
                (NavPortal*)workspace.EdgePortals.GetUnsafePtr(),
                (byte*)workspace.Blob.GetUnsafePtr());
        }

        /// <summary>计划 + 烘焙 + 热替换进 NavWorld（一步到位）。</summary>
        /// <returns>是否成功加载。</returns>
        public static bool BakeAndLoad(
            World world,
            in NavBakeInput input,
            NavBakeCollider* colliders,
            int colliderCount,
            float3* vertexPool,
            int vertexPoolLength,
            NavRuntimeBakeWorkspace workspace,
            NavBakeAnnotation* annotations = null,
            int annotationCount = 0,
            NavOffMeshLink* links = null,
            int linkCount = 0)
        {
            long bytes = Bake(in input, colliders, colliderCount, vertexPool, vertexPoolLength,
                workspace, annotations, annotationCount, links, linkCount);
            if (bytes <= 0) return false;

            byte* blob = (byte*)workspace.Blob.GetUnsafePtr();
            if (NavBlobReader.Validate(blob, input.Dimension) != NavBlobReader.Status.Ok) return false;

            world.GetNavWorld().LoadBlob(blob);
            return true;
        }
    }
}
