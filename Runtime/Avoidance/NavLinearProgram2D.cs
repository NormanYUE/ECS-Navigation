using Ember.Collision;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 地面 ORCA 的线性规划求解器（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 目标：在 <c>|v| &lt;= maxSpeed</c> 与全部半平面约束
    /// <c>dot(v, Normal) &gt;= Offset</c> 的交集内，取离期望速度最近的速度。
    /// 结构对应 RVO2 的 <c>linearProgram1/2/3</c>：
    /// 增量式半平面求交（<see cref="SolveIncremental"/>），
    /// 不可行时用被支配约束的交线重建约束集再解（<see cref="SolveFallback"/>）。
    ///
    /// 约定：约束按「静态障碍在前」排列，<paramref name="obstacleLineCount"/> 标记其数量 ——
    /// 回退求解始终保留这些约束，避免为了贴近期望速度而放弃避障。
    /// </summary>
    public static unsafe class NavLinearProgram2D
    {
        private const float Epsilon = 1e-6f;

        /// <summary>判定「约束已满足」的容差，兼作结果自检阈值。</summary>
        private const float SatisfactionTolerance = 1e-5f;

        /// <summary>
        /// 求解。<paramref name="scratch"/> 为回退路径的临时缓冲，长度须 &gt;= <paramref name="lineCount"/>；
        /// 传 null 时不可行即直接返回 false（不做回退，结果仍是可行域内的次优解）。
        /// </summary>
        /// <returns>结果是否满足全部约束。</returns>
        public static bool Solve(
            NavOrcaLine* lines,
            int lineCount,
            int obstacleLineCount,
            float maxSpeed,
            float3 preferredVelocity,
            CollisionDimension dimension,
            NavOrcaLine* scratch,
            out float3 result)
        {
            float3 planeNormal = NavPlane.Normal(dimension);

            // 期望速度先截断到速度上限：无约束时代理也不得超速
            // （期望速度来自路径跟随，未必已按 MaxSpeed 收敛）。
            float preferredLengthSq = math.lengthsq(preferredVelocity);
            result = preferredLengthSq > maxSpeed * maxSpeed
                ? preferredVelocity * (maxSpeed * math.rsqrt(preferredLengthSq))
                : preferredVelocity;

            if (lineCount <= 0 || maxSpeed <= 0f) return true;

            int failed = SolveIncremental(lines, lineCount, maxSpeed, preferredVelocity, false,
                planeNormal, ref result);
            if (failed < lineCount && scratch != null)
                SolveFallback(lines, lineCount, obstacleLineCount, failed, maxSpeed,
                    planeNormal, scratch, ref result);

            for (int i = 0; i < lineCount; i++)
                if (lines[i].Violation(result) > SatisfactionTolerance) return false;
            return true;
        }

        /// <summary>
        /// 增量式半平面求交（RVO2 <c>linearProgram2</c>）：
        /// 逐条检查，违反则把该条作为新的活跃边界重解。
        /// 返回首个无法满足的约束下标；全部满足时返回 <paramref name="lineCount"/>。
        /// </summary>
        internal static int SolveIncremental(
            NavOrcaLine* lines, int lineCount, float radius, float3 preferred, bool directionOpt,
            float3 planeNormal, ref float3 result)
        {
            if (directionOpt)
            {
                // 该分支下 preferred 约定为单位向量，直接撑到速度上限。
                result = preferred * radius;
            }
            else
            {
                float preferredLengthSq = math.lengthsq(preferred);
                result = preferredLengthSq > radius * radius
                    ? preferred * (radius * math.rsqrt(preferredLengthSq))
                    : preferred;
            }

            for (int i = 0; i < lineCount; i++)
            {
                if (lines[i].IsSatisfied(result)) continue;

                float3 previous = result;
                if (!SolveOnLine(lines, i, radius, preferred, directionOpt, planeNormal, out result))
                {
                    result = previous;
                    return i;
                }
            }

            return lineCount;
        }

        /// <summary>
        /// 把候选速度限制到第 <paramref name="lineIndex"/> 条约束的边界上
        /// （RVO2 <c>linearProgram1</c>）：先用已处理约束在该直线上截出可行参数区间，
        /// 再取区间内最接近期望速度（或最贴近期望方向）的点。
        /// </summary>
        internal static bool SolveOnLine(
            NavOrcaLine* lines, int lineIndex, float radius, float3 preferred, bool directionOpt,
            float3 planeNormal, out float3 result)
        {
            result = float3.zero;
            NavOrcaLine line = lines[lineIndex];
            float3 boundary = line.BoundaryPoint;
            float3 direction = line.Direction(planeNormal);

            // 速度圆与该直线相交与否，只取决于直线到原点的距离。
            // boundary 取规范点（离原点最近的点），故 projection 恒为 0。
            float projection = math.dot(boundary, direction);
            float discriminant = projection * projection + radius * radius - math.lengthsq(boundary);
            if (discriminant < 0f) return false;

            float sqrtDiscriminant = math.sqrt(discriminant);
            float tLeft = -projection - sqrtDiscriminant;
            float tRight = -projection + sqrtDiscriminant;

            for (int i = 0; i < lineIndex; i++)
            {
                float3 otherDirection = lines[i].Direction(planeNormal);
                float denominator = NavPlane.Determinant(direction, otherDirection, planeNormal);
                float numerator = NavPlane.Determinant(
                    otherDirection, boundary - lines[i].BoundaryPoint, planeNormal);

                if (math.abs(denominator) <= Epsilon)
                {
                    // 平行：同向则约束已被覆盖，反向且不满足则本直线无可行段。
                    if (numerator < 0f) return false;
                    continue;
                }

                float t = numerator / denominator;
                if (denominator >= 0f) tRight = math.min(tRight, t);
                else tLeft = math.max(tLeft, t);

                if (tLeft > tRight) return false;
            }

            if (directionOpt)
            {
                result = math.dot(preferred, direction) > 0f
                    ? boundary + tRight * direction
                    : boundary + tLeft * direction;
                return true;
            }

            float position = math.dot(direction, preferred - boundary);
            if (position < tLeft) result = boundary + tLeft * direction;
            else if (position > tRight) result = boundary + tRight * direction;
            else result = boundary + position * direction;
            return true;
        }

        /// <summary>
        /// 不可行时的回退（RVO2 <c>linearProgram3</c>）：对每条被违反且违反量更大的约束，
        /// 用「它与更早约束的交线」重建一组投影约束，并在其中朝该约束的反法线方向优化。
        /// 静态障碍约束全程保留，故回退不会牺牲避障。
        /// </summary>
        internal static void SolveFallback(
            NavOrcaLine* lines, int lineCount, int obstacleLineCount, int beginLine, float radius,
            float3 planeNormal, NavOrcaLine* scratch, ref float3 result)
        {
            float distance = 0f;

            for (int i = beginLine; i < lineCount; i++)
            {
                float violation = lines[i].Violation(result);
                if (violation <= distance) continue;

                int projected = 0;
                for (int j = 0; j < obstacleLineCount && j < i; j++)
                    scratch[projected++] = lines[j];

                for (int j = obstacleLineCount; j < i; j++)
                {
                    float3 directionI = lines[i].Direction(planeNormal);
                    float3 directionJ = lines[j].Direction(planeNormal);
                    float determinant = NavPlane.Determinant(directionI, directionJ, planeNormal);

                    float3 point;
                    if (math.abs(determinant) <= Epsilon)
                    {
                        // 平行：同向则该约束被 i 支配，反向则取两条直线的中点。
                        if (math.dot(directionI, directionJ) > 0f) continue;
                        point = 0.5f * (lines[i].BoundaryPoint + lines[j].BoundaryPoint);
                    }
                    else
                    {
                        float numerator = NavPlane.Determinant(
                            directionJ, lines[i].BoundaryPoint - lines[j].BoundaryPoint, planeNormal);
                        point = lines[i].BoundaryPoint + (numerator / determinant) * directionI;
                    }

                    float3 direction = directionJ - directionI;
                    float lengthSq = math.lengthsq(direction);
                    if (lengthSq <= Epsilon * Epsilon) continue;
                    scratch[projected++] = NavOrcaLine.FromDirectionPoint(
                        direction * math.rsqrt(lengthSq), point, planeNormal);
                }

                float3 candidate = result;
                float3 target = NavPlane.Rotate90(lines[i].Direction(planeNormal), planeNormal);
                // 失败只可能是浮点误差 —— 此时保留候选解继续，与 RVO2 一致。
                SolveIncremental(scratch, projected, radius, target, true, planeNormal, ref candidate);

                distance = lines[i].Violation(candidate);
                result = candidate;
            }
        }
    }
}
