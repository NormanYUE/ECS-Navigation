using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 三维 ORCA 约束构建（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 语义与 RVO2-3D 的 <c>Agent::insertAgentNeighbor</c> 一致：
    /// 相对速度落在切锥外时沿锥面投影，落在锥内时投影到圆锥切面，
    /// 已重叠时按当前步长沿最速脱离方向推开。
    ///
    /// 与二维版的差别在于「投影到圆锥切面」这一步：二维有左右两条切线，
    /// 三维是一条切平面，解析式不同 —— 故不能共用同一段代码。
    /// 返回的 <see cref="NavOrcaLine"/> 与二维同型，只有线性规划维度不同。
    /// </summary>
    public static class NavOrcaMath3D
    {
        private const float Epsilon = 1e-6f;

        /// <summary>半径和退化保护。</summary>
        private const float MinCombinedRadius = 1e-5f;

        /// <summary>三维约束构建；<paramref name="responsibility"/> 为责任分摊（代理 0.5、静态障碍 1）。</summary>
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
            out NavOrcaLine line)
        {
            line = default;
            if (timeHorizon <= 0f || timeStep <= 0f) return false;

            float3 relativePosition = neighborPosition - position;
            float3 relativeVelocity = velocity - neighborVelocity;

            float combinedRadius = radius + neighborRadius;
            if (combinedRadius < MinCombinedRadius) combinedRadius = MinCombinedRadius;
            float combinedRadiusSq = combinedRadius * combinedRadius;
            float distSq = math.lengthsq(relativePosition);

            float3 normal;
            float3 escape;

            if (distSq > combinedRadiusSq)
            {
                float invTimeHorizon = 1f / timeHorizon;
                float3 offset = relativeVelocity - invTimeHorizon * relativePosition;
                float offsetLengthSq = math.lengthsq(offset);
                float projection = math.dot(offset, relativePosition);

                if (projection < 0f && projection * projection > combinedRadiusSq * offsetLengthSq)
                {
                    // 落在切锥外：沿偏移方向直接推离。
                    float offsetLength = math.sqrt(offsetLengthSq);
                    float3 unitOffset = offsetLength > Epsilon ? offset / offsetLength : Fallback();
                    normal = unitOffset;
                    escape = (combinedRadius * invTimeHorizon - offsetLength) * unitOffset;
                }
                else
                {
                    // 落在切锥内：投影到圆锥切面。切点参数由二次方程给出。
                    float a = distSq;
                    float b = math.dot(relativePosition, relativeVelocity);
                    float denominator = distSq - combinedRadiusSq;
                    float c = math.lengthsq(relativeVelocity)
                        - math.lengthsq(math.cross(relativePosition, relativeVelocity))
                            / (denominator > Epsilon ? denominator : Epsilon);

                    float discriminant = b * b - a * c;
                    if (discriminant < 0f) return false;

                    float t = (b + math.sqrt(discriminant)) / a;
                    float3 tangentVelocity = relativeVelocity - t * relativePosition;
                    float tangentLength = math.length(tangentVelocity);
                    float3 unitTangent = tangentLength > Epsilon ? tangentVelocity / tangentLength : Fallback();

                    normal = unitTangent;
                    escape = (combinedRadius * t - tangentLength) * unitTangent;
                }
            }
            else
            {
                // 已重叠：按当前步长沿相对速度方向以最短距离脱离。
                float invTimeStep = 1f / timeStep;
                float3 offset = relativeVelocity - invTimeStep * relativePosition;
                float offsetLength = math.length(offset);
                float3 unitOffset = offsetLength > Epsilon ? offset / offsetLength : Fallback();
                normal = unitOffset;
                escape = (combinedRadius * invTimeStep - offsetLength) * unitOffset;
            }

            float3 point = velocity + responsibility * escape;
            line = new NavOrcaLine
            {
                Normal = normal,
                Offset = math.dot(normal, point),
            };
            return true;
        }

        /// <summary>退化方向（相对量恰为零向量）时的确定性兜底。</summary>
        private static float3 Fallback() => new(1f, 0f, 0f);
    }
}
