using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// ORCA 半平面约束：允许的速度集合为 <c>dot(velocity, Normal) &gt;= Offset</c>。
    ///
    /// <see cref="Normal"/> 为单位向量；地面求解器下它位于导航平面内。
    /// <see cref="Offset"/> 是原点到边界直线的有向距离，故
    /// <c>Normal * Offset</c> 即边界上离原点最近的点。
    ///
    /// 与 RVO2 的 <c>(point, direction)</c> 表示等价：
    /// 取 <c>Normal = cross(planeNormal, direction)</c>、<c>Offset = dot(Normal, point)</c>，
    /// 则 RVO2 的判定 <c>det(direction, point - v) &gt; 0</c> 恰为 <c>dot(Normal, v) &lt; Offset</c>。
    ///
    /// 速度取 3D 而非 <c>float2</c>：约束构建与飞行（3D）求解器共用同一类型，
    /// 只有线性规划维度不同（见设计 §5.3）。
    /// </summary>
    public struct NavOrcaLine
    {
        /// <summary>单位法线。</summary>
        public float3 Normal;

        /// <summary>原点到边界的有向距离；允许域为 <c>dot(v, Normal) &gt;= Offset</c>。</summary>
        public float Offset;

        /// <summary>由 RVO2 的「方向 + 边界点」表示构造。</summary>
        public static NavOrcaLine FromDirectionPoint(float3 direction, float3 point, float3 planeNormal)
        {
            float3 normal = math.cross(planeNormal, direction);
            return new NavOrcaLine
            {
                Normal = normal,
                Offset = math.dot(normal, point),
            };
        }

        /// <summary>边界上的规范点（离原点最近的点）。</summary>
        public readonly float3 BoundaryPoint => Normal * Offset;

        /// <summary>RVO2 表示下的约束方向 <c>direction = cross(Normal, planeNormal)</c>。</summary>
        public readonly float3 Direction(float3 planeNormal) => math.cross(Normal, planeNormal);

        /// <summary>速度是否满足本约束。</summary>
        public readonly bool IsSatisfied(float3 velocity) => math.dot(Normal, velocity) >= Offset;

        /// <summary>违反量（&gt; 0 表示越界，数值等于沿法线的越界距离）。</summary>
        public readonly float Violation(float3 velocity) => Offset - math.dot(Normal, velocity);
    }
}
