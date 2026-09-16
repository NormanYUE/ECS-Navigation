using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 路径跟随（工具类，静态豁免，Burst 兼容，无托管分配）。
    ///
    /// 输入一串航点（<c>float3</c>，由分层 A* + 拉绳平滑产出），输出<b>期望速度</b>。
    /// 不做避障 —— 避障是下游 ORCA 的职责，两者之间只传期望速度这一个量。
    ///
    /// 前瞻点取「沿折线自当前位置前进 <c>lookAheadDistance</c>」处的点，
    /// 而不是直接朝当前航点走：后者在拐角处会出现速度方向的硬折。
    /// </summary>
    public static unsafe class NavPathFollower
    {
        private const float Epsilon = 1e-6f;

        /// <summary>
        /// 推进航点并给出期望速度。
        ///
        /// 到达判定按 <paramref name="arriveRadius"/> 逐点推进；
        /// 终点进入到达半径即视为走完，返回 false 且期望速度归零。
        /// </summary>
        /// <param name="waypoints">航点数组（世界空间）。</param>
        /// <param name="waypointCount">航点数量。</param>
        /// <param name="position">代理当前位置。</param>
        /// <param name="speed">期望速度大小（通常为代理最大速度）。</param>
        /// <param name="arriveRadius">航点到达半径。</param>
        /// <param name="lookAheadDistance">沿折线的前瞻距离。</param>
        /// <param name="currentIndex">当前航点下标，原地推进。</param>
        /// <param name="desiredVelocity">期望速度；走完或参数退化时为零。</param>
        /// <returns>是否仍在路径上。</returns>
        public static bool Step(
            float3* waypoints,
            int waypointCount,
            float3 position,
            float speed,
            float arriveRadius,
            float lookAheadDistance,
            ref int currentIndex,
            out float3 desiredVelocity)
        {
            desiredVelocity = float3.zero;

            if (waypointCount <= 0 || waypoints == null) return false;
            if (currentIndex < 0) currentIndex = 0;
            if (currentIndex >= waypointCount) currentIndex = waypointCount - 1;

            float arriveRadiusSq = arriveRadius * arriveRadius;

            // 逐点推进。两条判据缺一不可：
            // ① 进入到达半径 —— 正常抵达；
            // ② 已越过该航点的切平面 —— 代理可能因避障或大前瞻而从未进入到达半径，
            //    只看半径会让它掉头回去找已经错过的航点。
            while (currentIndex < waypointCount - 1)
            {
                float3 current = waypoints[currentIndex];
                if (math.distancesq(position, current) <= arriveRadiusSq)
                {
                    currentIndex++;
                    continue;
                }

                float3 segment = waypoints[currentIndex + 1] - current;
                if (math.dot(position - current, segment) > 0f)
                {
                    currentIndex++;
                    continue;
                }

                break;
            }

            if (math.distancesq(position, waypoints[currentIndex]) <= arriveRadiusSq)
                return currentIndex < waypointCount - 1;

            if (speed <= 0f) return true;

            float3 target = LookAhead(waypoints, waypointCount, position, currentIndex,
                lookAheadDistance);

            float3 offset = target - position;
            float distanceSq = math.lengthsq(offset);
            if (distanceSq <= Epsilon * Epsilon) return true;

            desiredVelocity = offset * (speed * math.rsqrt(distanceSq));
            return true;
        }

        /// <summary>
        /// 沿折线自 <paramref name="position"/> 起前进 <paramref name="lookAheadDistance"/>
        /// 得到的点；不足则取终点。重复航点（零长线段）跳过。
        /// </summary>
        private static float3 LookAhead(
            float3* waypoints, int waypointCount, float3 position, int currentIndex,
            float lookAheadDistance)
        {
            if (lookAheadDistance <= 0f) return waypoints[currentIndex];

            float3 from = position;
            float remaining = lookAheadDistance;

            for (int i = currentIndex; i < waypointCount; i++)
            {
                float3 to = waypoints[i];
                float3 segment = to - from;
                float segmentLength = math.length(segment);

                if (segmentLength <= Epsilon) continue;
                if (segmentLength >= remaining)
                    return from + segment * (remaining / segmentLength);

                remaining -= segmentLength;
                from = to;
            }

            return waypoints[waypointCount - 1];
        }
    }
}
