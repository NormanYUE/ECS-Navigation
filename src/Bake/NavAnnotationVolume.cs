using Ember.Collision;
using UnityEngine;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 标注体：在场景里圈出一块区域，覆盖可行走判定或调整寻路代价。
    ///
    /// 做成场景组件而不是 ECS 组件 —— 标注是<b>烘焙期输入</b>，不是运行时状态：
    /// 它只需要在编辑器和烘焙那一刻存在，烘完就固化进距离场与代价层，
    /// 运行时再留一份纯属浪费每帧遍历。
    ///
    /// 范围是<b>轴对齐</b>世界 AABB（<see cref="Aabb"/> 本身即 AABB 语义）：
    /// 组件可以旋转，但只取其位置，旋转不参与判定 —— 需要斜置区域时叠几个轴对齐块。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NavAnnotationVolume : MonoBehaviour
    {
        /// <summary>可行走覆盖模式。</summary>
        public enum WalkableMode : byte
        {
            /// <summary>不覆盖，只改代价。</summary>
            None = 0,

            /// <summary>强制可行走（覆盖距离场的不可走判定）。</summary>
            ForceWalkable = 1,

            /// <summary>强制阻挡。</summary>
            ForceBlocked = 2,
        }

        [Tooltip("相对本物体位置的中心偏移（米）。")]
        [SerializeField] private Vector3 m_Center = Vector3.zero;

        [Tooltip("区域尺寸（米，轴对齐）。")]
        [SerializeField] private Vector3 m_Size = new(10f, 2f, 10f);

        [Tooltip("可行走覆盖：不覆盖 / 强制可行走 / 强制阻挡。")]
        [SerializeField] private WalkableMode m_Walkable = WalkableMode.None;

        [Tooltip("区域代价乘数：1 默认，0.5 快，3 慢。")]
        [SerializeField] private float m_CostMultiplier = 1f;

        /// <summary>可行走覆盖模式。</summary>
        public WalkableMode Walkable => m_Walkable;

        /// <summary>区域代价乘数。</summary>
        public float CostMultiplier => m_CostMultiplier;

        /// <summary>世界空间 AABB（编辑器 Gizmo 与烘焙共用同一口径）。</summary>
        public Aabb Bounds
        {
            get
            {
                float3 center = (float3)(transform.position + m_Center);
                float3 half = (float3)m_Size * 0.5f;
                return new Aabb(center - half, center + half);
            }
        }

        /// <summary>转成烘焙标注条目。</summary>
        public NavBakeAnnotation ToAnnotation() => new()
        {
            Region = Bounds,
            WalkableOverride = (byte)m_Walkable,
            CostMultiplier = m_CostMultiplier,
        };
    }
}
