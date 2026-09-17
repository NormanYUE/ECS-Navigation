using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>off-mesh link（跳跃点 / 传送门 / 门），双向表。</summary>
    public struct NavOffMeshLink
    {
        /// <summary>起点世界坐标。</summary>
        public float3 Start;

        /// <summary>终点世界坐标。</summary>
        public float3 End;

        /// <summary>穿越代价（加权寻路用，0 = 默认按直线距离）。</summary>
        public float Cost;

        /// <summary>是否双向（0 = 仅 Start→End）。</summary>
        public byte Bidirectional;
    }
}
