using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 飞行 ORCA 的三维线性规划（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 目标：在速度球 <c>|v| &lt;= maxSpeed</c> 与全部半平面
    /// <c>dot(v, Normal) &gt;= Offset</c> 的交集内取离期望速度最近的点。
    ///
    /// 与二维版走增量式半平面求交不同，这里用<b>精确顶点枚举</b>：
    /// 凸可行域的边界由平面片、球面片及两者的交线构成，故最优解必是下列之一 ——
    /// 期望速度本身、各平面上的垂足、速度球面上沿期望方向点、平面与球面的交圆上最近点、
    /// 两平面交线上最近点、交线与球面的交点、三平面交点。
    /// 每代理约束数在邻居容量量级（≤ 十余条），枚举量 C(n,3) 很小，代价可接受，
    /// 且结果确定、可证最优 —— 不需要回退路径。
    /// </summary>
    public static unsafe class NavLinearProgram3D
    {
        private const float Epsilon = 1e-6f;

        /// <summary>判定「约束已满足」的容差，兼作结果自检阈值。</summary>
        private const float SatisfactionTolerance = 1e-5f;

        /// <summary>
        /// 求解。<returns>结果是否满足全部约束。</returns>
        /// </summary>
        public static bool Solve(
            NavOrcaLine* lines, int lineCount, float maxSpeed, float3 preferredVelocity,
            out float3 result)
        {
            float preferredLengthSq = math.lengthsq(preferredVelocity);
            result = preferredLengthSq > maxSpeed * maxSpeed
                ? preferredVelocity * (maxSpeed * math.rsqrt(preferredLengthSq))
                : preferredVelocity;

            if (lineCount <= 0 || maxSpeed <= 0f) return true;

            float3 best = result;
            float bestDistanceSq = IsFeasible(lines, lineCount, maxSpeed, result)
                ? math.distancesq(result, preferredVelocity)
                : float.MaxValue;
            bool found = bestDistanceSq < float.MaxValue;

            EnumeratePlanes(lines, lineCount, maxSpeed, preferredVelocity,
                ref best, ref bestDistanceSq, ref found);
            EnumeratePairs(lines, lineCount, maxSpeed, preferredVelocity,
                ref best, ref bestDistanceSq, ref found);
            EnumerateTriples(lines, lineCount, maxSpeed, preferredVelocity,
                ref best, ref bestDistanceSq, ref found);

            if (found) result = best;

            for (int i = 0; i < lineCount; i++)
                if (lines[i].Violation(result) > SatisfactionTolerance) return false;
            return true;
        }

        /// <summary>期望速度本身、各平面垂足、速度球面点、平面与球面交圆上的最近点。</summary>
        private static void EnumeratePlanes(
            NavOrcaLine* lines, int lineCount, float maxSpeed, float3 preferred,
            ref float3 best, ref float bestDistanceSq, ref bool found)
        {
            // 速度球面上沿期望方向的点
            float preferredLengthSq = math.lengthsq(preferred);
            if (preferredLengthSq > Epsilon * Epsilon)
                Consider(lines, lineCount, maxSpeed, preferred,
                    preferred * (maxSpeed * math.rsqrt(preferredLengthSq)),
                    ref best, ref bestDistanceSq, ref found);

            for (int i = 0; i < lineCount; i++)
            {
                NavOrcaLine line = lines[i];
                float distance = math.dot(preferred, line.Normal);
                Consider(lines, lineCount, maxSpeed, preferred,
                    preferred - (distance - line.Offset) * line.Normal,
                    ref best, ref bestDistanceSq, ref found);

                // 平面截速度球得圆：圆心是原点到平面的垂足，半径由球的截距给出。
                float radiusSq = maxSpeed * maxSpeed - line.Offset * line.Offset;
                if (radiusSq <= Epsilon * Epsilon) continue;

                float3 center = line.Normal * line.Offset;
                // 期望速度在平面内的投影（先补上偏移量），再减去圆心得径向。
                float3 projected = preferred - (distance - line.Offset) * line.Normal;
                float3 radial = projected - center;
                float radialLengthSq = math.lengthsq(radial);
                float3 direction = radialLengthSq > Epsilon * Epsilon
                    ? radial * math.rsqrt(radialLengthSq)
                    : AnyPerpendicular(line.Normal);

                Consider(lines, lineCount, maxSpeed, preferred,
                    center + direction * math.sqrt(radiusSq),
                    ref best, ref bestDistanceSq, ref found);
            }
        }

        /// <summary>两平面交线上离期望速度最近的点，以及交线与速度球的两个交点。</summary>
        private static void EnumeratePairs(
            NavOrcaLine* lines, int lineCount, float maxSpeed, float3 preferred,
            ref float3 best, ref float bestDistanceSq, ref bool found)
        {
            for (int i = 0; i < lineCount; i++)
            {
                for (int j = i + 1; j < lineCount; j++)
                {
                    NavOrcaLine a = lines[i];
                    NavOrcaLine b = lines[j];
                    float3 direction = math.cross(a.Normal, b.Normal);
                    float directionLengthSq = math.lengthsq(direction);
                    if (directionLengthSq <= Epsilon * Epsilon) continue;

                    float3 anchor = ClosestPointOnPlanePair(a, b, directionLengthSq);
                    float3 unitDirection = direction * math.rsqrt(directionLengthSq);
                    float along = math.dot(preferred - anchor, unitDirection);

                    Consider(lines, lineCount, maxSpeed, preferred,
                        anchor + along * unitDirection, ref best, ref bestDistanceSq, ref found);

                    // 交线与速度球的交点：(anchor + t·d)² = maxSpeed²
                    float projected = math.dot(anchor, unitDirection);
                    float constant = math.lengthsq(anchor) - maxSpeed * maxSpeed;
                    float discriminant = projected * projected - constant;
                    if (discriminant < 0f) continue;

                    float root = math.sqrt(discriminant);
                    Consider(lines, lineCount, maxSpeed, preferred,
                        anchor + (-projected - root) * unitDirection,
                        ref best, ref bestDistanceSq, ref found);
                    Consider(lines, lineCount, maxSpeed, preferred,
                        anchor + (-projected + root) * unitDirection,
                        ref best, ref bestDistanceSq, ref found);
                }
            }
        }

        /// <summary>三平面交点（凸多面体的角点）。</summary>
        private static void EnumerateTriples(
            NavOrcaLine* lines, int lineCount, float maxSpeed, float3 preferred,
            ref float3 best, ref float bestDistanceSq, ref bool found)
        {
            for (int i = 0; i < lineCount; i++)
            {
                for (int j = i + 1; j < lineCount; j++)
                {
                    for (int k = j + 1; k < lineCount; k++)
                    {
                        float3 a = lines[i].Normal;
                        float3 b = lines[j].Normal;
                        float3 c = lines[k].Normal;

                        float determinant = math.dot(a, math.cross(b, c));
                        if (math.abs(determinant) <= Epsilon) continue;

                        float3 point = (lines[i].Offset * math.cross(b, c)
                            + lines[j].Offset * math.cross(c, a)
                            + lines[k].Offset * math.cross(a, b)) / determinant;

                        Consider(lines, lineCount, maxSpeed, preferred, point,
                            ref best, ref bestDistanceSq, ref found);
                    }
                }
            }
        }

        /// <summary>两平面交线上离原点最近的点。</summary>
        private static float3 ClosestPointOnPlanePair(NavOrcaLine a, NavOrcaLine b, float directionLengthSq)
        {
            float alignment = math.dot(a.Normal, b.Normal);
            float determinant = 1f - alignment * alignment;
            if (math.abs(determinant) <= Epsilon) return float3.zero;

            float first = (a.Offset - b.Offset * alignment) / determinant;
            float second = (b.Offset - a.Offset * alignment) / determinant;
            return first * a.Normal + second * b.Normal;
        }

        /// <summary>更新最优可行解。</summary>
        private static void Consider(
            NavOrcaLine* lines, int lineCount, float maxSpeed, float3 preferred, float3 candidate,
            ref float3 best, ref float bestDistanceSq, ref bool found)
        {
            if (!IsFeasible(lines, lineCount, maxSpeed, candidate)) return;

            float distanceSq = math.distancesq(candidate, preferred);
            if (distanceSq >= bestDistanceSq) return;

            bestDistanceSq = distanceSq;
            best = candidate;
            found = true;
        }

        private static bool IsFeasible(
            NavOrcaLine* lines, int lineCount, float maxSpeed, float3 candidate)
        {
            if (math.lengthsq(candidate) > maxSpeed * maxSpeed + 1e-4f) return false;
            for (int i = 0; i < lineCount; i++)
                if (lines[i].Violation(candidate) > 1e-4f) return false;
            return true;
        }

        /// <summary>法线的任一单位垂线（交圆退化时的确定性兜底）。</summary>
        private static float3 AnyPerpendicular(float3 normal)
        {
            float3 axis = math.abs(normal.x) < 0.9f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f);
            float3 perpendicular = math.cross(normal, axis);
            float lengthSq = math.lengthsq(perpendicular);
            return lengthSq > Epsilon * Epsilon
                ? perpendicular * math.rsqrt(lengthSq)
                : new float3(0f, 0f, 1f);
        }
    }
}
