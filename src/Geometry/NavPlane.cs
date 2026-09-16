using Ember.Collision;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 导航平面纯数学工具（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 2D 模式（XY / XZ）下，地面 ORCA 把位置与速度压到平面内再求解：
    /// 无效轴分量直接丢弃，平面法线即该无效轴的单位向量。
    /// <see cref="CollisionDimension.XYZ"/> 的法线为零向量，
    /// <see cref="Flatten"/> 与 <see cref="Determinant"/> 相应退化为恒等与零。
    /// </summary>
    internal static class NavPlane
    {
        /// <summary>平面法线（无效轴单位向量）；XYZ 返回零向量。</summary>
        public static float3 Normal(CollisionDimension dimension)
        {
            switch (dimension)
            {
                case CollisionDimension.XY:
                    return new float3(0f, 0f, 1f);
                case CollisionDimension.XZ:
                    return new float3(0f, 1f, 0f);
                default:
                    return float3.zero;
            }
        }

        /// <summary>丢弃法线方向分量，把向量压到平面内。</summary>
        public static float3 Flatten(float3 vector, float3 planeNormal) =>
            vector - planeNormal * math.dot(vector, planeNormal);

        /// <summary>
        /// 平面内的叉积标量（2D 的 det）。3D 模式法线为零向量，恒返回 0 ——
        /// 该模式不走平面求解路径，不会用到此值。
        /// </summary>
        public static float Determinant(float3 a, float3 b, float3 planeNormal) =>
            math.dot(math.cross(a, b), planeNormal);

        /// <summary>
        /// 平面内旋转 90°：<c>cross(planeNormal, vector)</c>。
        /// 即 RVO2 的 <c>(-v.y(), v.x())</c>，切锥外沿与线性规划回退都用它取垂线。
        /// </summary>
        public static float3 Rotate90(float3 vector, float3 planeNormal) =>
            math.cross(planeNormal, vector);
    }
}
