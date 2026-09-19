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
        /// 从代理在折线上的**投影点**起，沿折线前进 <paramref name="lookAheadDistance"/>
        /// 得到的点；不足则取终点。重复航点（零长线段）跳过。
        ///
        /// <b>先投影再前进</b>，而不是从代理位置直接前进 —— 代理被避障/分离力推开后并不在
        /// 折线上，从那个偏离点朝下一个航点直线前进，方向会**切过弯角**：它盯着的是前方
        /// 十几米外的航点，而中间隔着一堵墙。表现就是顶着墙角原地磨、来回抖。
        /// 投影到折线上再前进，得到的方向才始终沿路径。
        ///
        /// 投影段取「离代理最近」的那一段（而不是第一段）：折线可能自交，
        /// 取第一段会把投影点甩到代理身后。
        /// </summary>
        private static float3 LookAhead(
            float3* waypoints, int waypointCount, float3 position, int currentIndex,
            float lookAheadDistance)
        {
            if (lookAheadDistance <= 0f) return waypoints[currentIndex];
            if (waypointCount < 2) return waypoints[waypointCount - 1];

            int lastSegment = waypointCount - 2;
            var projectedPoint = position;
            // 投影从**当前航点前一段**开始扫，而不是当前航点那一段：
            // currentIndex 是「正在赶往的那个航点」，代理通常还差一点没到它 ——
            // 只从它那一段扫，投影就被迫落在该段起点（= 那个航点），
            // 而前瞻又从该起点往下走，方向于是从侧面切出去、离开路径。
            //
            // 越界保护同下：末点上没有「下一段」，退到末段。
            var startSegment = math.clamp(currentIndex - 1, 0, lastSegment);
            float bestDistanceSq = float.MaxValue;

            for (int i = startSegment; i <= lastSegment; i++)
            {
                float3 a = waypoints[i];
                float3 segment = waypoints[i + 1] - a;
                float lengthSq = math.lengthsq(segment);
                if (lengthSq <= Epsilon * Epsilon) continue;

                float t = math.clamp(math.dot(position - a, segment) / lengthSq, 0f, 1f);
                float3 point = a + segment * t;
                float distanceSq = math.distancesq(position, point);
                if (distanceSq >= bestDistanceSq) continue;

                bestDistanceSq = distanceSq;
                projectedPoint = point;
                startSegment = i;
            }

            // 投影点落在 startSegment 上，先把它到该段末端的余量走完，再续后面的段。
            float3 segmentEnd = waypoints[startSegment + 1];
            float3 tail = segmentEnd - projectedPoint;
            float tailLength = math.length(tail);

            if (tailLength >= lookAheadDistance && tailLength > Epsilon)
                return projectedPoint + tail * (lookAheadDistance / tailLength);

            float remaining = lookAheadDistance - tailLength;
            float3 from = segmentEnd;

            for (int i = startSegment + 1; i < waypointCount; i++)
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
