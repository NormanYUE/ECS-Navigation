using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// ORCA 约束构建（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 语义与 RVO2 的 <c>Agent::insertAgentNeighbor</c> / <c>Agent::insertObstacleNeighbor</c>
    /// 一致：先在速度空间求出「相对速度必须逃离的切锥」外沿，再把责任量按
    /// <paramref name="responsibility"/> 分摊给本代理，得到半平面约束。
    ///
    /// 空间维度由 <paramref name="planeNormal"/> 控制：地面求解传入平面法线，
    /// 位置与速度先被压到平面内再构建；飞行求解传零向量（压平退化为恒等），
    /// 于是共用同一套构建代码，只有线性规划维度不同。
    /// </summary>
    public static class NavOrcaMath
    {
        private const float Epsilon = 1e-6f;

        /// <summary>代理半径之和为 0 时的切锥退化保护。</summary>
        private const float MinCombinedRadius = 1e-5f;

        /// <summary>代理间约束：避让责任对半承担。</summary>
        public static bool AgentConstraint(
            float3 position,
            float3 velocity,
            float radius,
            float3 neighborPosition,
            float3 neighborVelocity,
            float neighborRadius,
            float timeHorizon,
            float timeStep,
            float3 planeNormal,
            out NavOrcaLine line) =>
            Build(position, velocity, radius, neighborPosition, neighborVelocity, neighborRadius,
                timeHorizon, timeStep, 0.5f, planeNormal, out line);

        /// <summary>
        /// 静态障碍约束：障碍不动，避让责任全部由代理承担。
        /// 几何来自距离场 —— <paramref name="distance"/> 为代理中心到障碍表面的最近距离
        /// （外正内负），<paramref name="gradient"/> 为距离场梯度（单位向量，背离障碍）。
        /// 等价于与该障碍最近点（半径 0、速度 0）做一次代理约束。
        /// </summary>
        public static bool StaticConstraint(
            float3 position,
            float3 velocity,
            float radius,
            float distance,
            float3 gradient,
            float timeHorizon,
            float timeStep,
            float3 planeNormal,
            out NavOrcaLine line) =>
            Build(position, velocity, radius, position - gradient * distance, float3.zero, 0f,
                timeHorizon, timeStep, 1f, planeNormal, out line);

        /// <summary>通用构建：责任分摊由 <paramref name="responsibility"/> 指定。</summary>
        public static bool Build(
            float3 position,
            float3 velocity,
            float radius,
            float3 neighborPosition,
            float3 neighborVelocity,
            float neighborRadius,
            float timeHorizon,
            float timeStep,
            float responsibility,
            float3 planeNormal,
            out NavOrcaLine line)
        {
            line = default;
            if (timeHorizon <= 0f || timeStep <= 0f) return false;

            // 平面法线为零即三维模式：二维的切线分支依赖平面内旋转 90°，三维用切平面解析式。
            if (math.lengthsq(planeNormal) <= Epsilon * Epsilon)
                return NavOrcaMath3D.Build(position, velocity, radius, neighborPosition, neighborVelocity,
                    neighborRadius, timeHorizon, timeStep, responsibility, out line);

            float3 relativePosition = NavPlane.Flatten(neighborPosition - position, planeNormal);
            float3 relativeVelocity = NavPlane.Flatten(velocity - neighborVelocity, planeNormal);

            float combinedRadius = radius + neighborRadius;
            if (combinedRadius < MinCombinedRadius) combinedRadius = MinCombinedRadius;
            float combinedRadiusSq = combinedRadius * combinedRadius;
            float distSq = math.lengthsq(relativePosition);

            float3 direction;
            float3 escape;

            if (distSq > combinedRadiusSq)
            {
                // 未碰撞：相对速度落在切锥外时沿圆锥外沿逃离，否则退化为沿切线逃离。
                float invTimeHorizon = 1f / timeHorizon;
                float3 offset = relativeVelocity - invTimeHorizon * relativePosition;
                float offsetLengthSq = math.lengthsq(offset);
                float projection = math.dot(offset, relativePosition);

                if (projection < 0f && projection * projection > combinedRadiusSq * offsetLengthSq)
                {
                    float offsetLength = math.sqrt(offsetLengthSq);
                    float3 unitOffset = offsetLength > Epsilon ? offset / offsetLength : Fallback(planeNormal);
                    // 约束的法线取 unitOffset（背离障碍），而 direction 是**边界方向**，
                    // 两者差一个平面内 90°（见 NavOrcaLine.FromDirectionPoint：
                    // Normal = cross(planeNormal, direction)）。直接拿 unitOffset 当 direction
                    // 等于把半平面转了 90°，卡到与障碍垂直的那个轴上 ——
                    // 侧面的墙会把前进方向堵死，且边界过原点时线性规划的结果恰好落回原点（速度归零）。
                    direction = -NavPlane.Rotate90(unitOffset, planeNormal);
                    escape = (combinedRadius * invTimeHorizon - offsetLength) * unitOffset;
                }
                else
                {
                    // 相对速度落在切锥内：改为沿锥的切线逃离。两条切线的选取由
                    // det(relativePosition, offset) 的符号决定，与 RVO2 的两个分支一一对应。
                    float leg = math.sqrt(distSq - combinedRadiusSq);
                    float3 rotation = NavPlane.Rotate90(relativePosition, planeNormal);
                    direction = NavPlane.Determinant(relativePosition, offset, planeNormal) > 0f
                        ? (relativePosition * leg + rotation * combinedRadius) / distSq
                        : -(relativePosition * leg - rotation * combinedRadius) / distSq;
                    escape = math.dot(relativeVelocity, direction) * direction - relativeVelocity;
                }
            }
            else
            {
                // 已碰撞：按当前步长把相对速度推到脱离重叠所需的最短方向。
                float invTimeStep = 1f / timeStep;
                float3 offset = relativeVelocity - invTimeStep * relativePosition;
                float offsetLength = math.length(offset);
                float3 unitOffset = offsetLength > Epsilon ? offset / offsetLength : Fallback(planeNormal);
                // 同上：direction 是边界方向，比法线少一个 90°。
                direction = -NavPlane.Rotate90(unitOffset, planeNormal);
                escape = (combinedRadius * invTimeStep - offsetLength) * unitOffset;
            }

            line = NavOrcaLine.FromDirectionPoint(
                direction, velocity + responsibility * escape, planeNormal);
            return true;
        }

        /// <summary>退化方向（相对量恰为零向量）时的确定性兜底：取平面内固定轴。</summary>
        private static float3 Fallback(float3 planeNormal)
        {
            float3 candidate = new float3(1f, 0f, 0f);
            float3 flattened = NavPlane.Flatten(candidate, planeNormal);
            if (math.lengthsq(flattened) > Epsilon * Epsilon) return math.normalize(flattened);

            float3 second = NavPlane.Flatten(new float3(0f, 1f, 0f), planeNormal);
            if (math.lengthsq(second) > Epsilon * Epsilon) return math.normalize(second);

            return math.normalize(NavPlane.Flatten(new float3(0f, 0f, 1f), planeNormal));
        }
    }
}
