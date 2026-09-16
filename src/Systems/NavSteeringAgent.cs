using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 单个代理的路径跟随输入 / 输出（稠密数组元素，Job 内并行处理）。
    ///
    /// 航点存于 World 托管 buffer，Job 内只能拿裸指针 —— 与碰撞管线的
    /// <c>ChunkInfo</c> 同一手法：串行外壳取指针，Job 只读。
    /// </summary>
    public struct NavSteeringAgent
    {
        /// <summary>航点数组基址（<c>float3</c>）；0 表示无路径。</summary>
        [NativeDisableUnsafePtrRestriction] public long WaypointPtr;

        /// <summary>航点数量。</summary>
        public int WaypointCount;

        /// <summary>当前航点下标（Job 内原地推进）。</summary>
        public int CurrentIndex;

        /// <summary>代理当前位置（只读快照）。</summary>
        public float3 Position;

        /// <summary>期望速度大小（通常为代理最大速度）。</summary>
        public float Speed;

        /// <summary>航点到达半径。</summary>
        public float ArriveRadius;

        /// <summary>沿折线的前瞻距离。</summary>
        public float LookAheadDistance;

        /// <summary>本帧期望速度（Job 输出，串行侧写回组件）。</summary>
        public float3 DesiredVelocity;
    }
}
