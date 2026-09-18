using Ember.Collision;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// ORCA 避障作业（每个代理一次并行执行）。
    ///
    /// <b>快照式并行</b>：所有代理读同一帧的位置与速度快照，各自独立求解，
    /// 写自己的新速度（双缓冲，读 <see cref="Velocities"/> 写 <see cref="NewVelocities"/>）。
    /// 因此无锁、无原子、结果与调度顺序无关，同输入必得同输出。
    ///
    /// 约束构建分两段（设计 §5.3）：代理间约束与静态障碍约束共用同一套
    /// <see cref="NavOrcaMath"/>；静态障碍的几何来自距离场，不做形状查询也不打射线。
    /// 静态障碍约束排在数组前部 —— 线性规划回退路径始终保留它们。
    ///
    /// 飞行代理（<see cref="NavAgentMode.Flying"/>）本轮原样透传速度，3D 求解器由 P6 接管。
    /// </summary>
    [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    public unsafe struct NavAgentJob : IJobParallelFor
    {
        // ---- 代理快照（稠密，下标 = 串行侧遍历序）----

        // 带 [NativeDisableParallelForRestriction] 的四个字段（本组三个 + 下面的 NeighborIndices）：
        // IJobParallelFor 默认把每个 NativeArray 字段限制在 [index, index]，而本 Job 会按邻居下标
        // 跨元素读（Positions[other] / Velocities[other] / Radii[other]、NeighborIndices[offset + i]），
        // 不加就抛 "Index N is out of restricted IJobParallelFor range"。
        // 其余字段只按 index 访问，或只经裸指针访问（裸指针不做范围检查），故不需要。

        [ReadOnly, NativeDisableParallelForRestriction] public NativeArray<float3> Positions;
        [ReadOnly, NativeDisableParallelForRestriction] public NativeArray<float3> Velocities;
        [ReadOnly] public NativeArray<float3> Preferred;
        [ReadOnly, NativeDisableParallelForRestriction] public NativeArray<float> Radii;
        [ReadOnly] public NativeArray<float> MaxSpeeds;
        [ReadOnly] public NativeArray<float> NeighborDists;
        [ReadOnly] public NativeArray<byte> Modes;

        /// <summary>新速度（双缓冲的写入侧，与 <see cref="Velocities"/> 不同数组）。</summary>
        public NativeArray<float3> NewVelocities;

        /// <summary>逐代理邻居数（诊断与测试用）。</summary>
        public NativeArray<int> NeighborCounts;

        // ---- 均匀网格（只读）----

        public int3 GridSize;
        public float3 GridOrigin;
        public float CellSize;
        public float MaxRadius;
        [ReadOnly] public NativeArray<int> CellStarts;
        [ReadOnly] public NativeArray<int> CellCounts;
        [ReadOnly] public NativeArray<int> SortedAgents;

        // ---- 逐代理工作区（长度 = 代理数 × 容量）----

        // 跨元素读，见上方说明。
        [NativeDisableParallelForRestriction] public NativeArray<int> NeighborIndices;
        public NativeArray<float> NeighborDistances;
        public NativeArray<NavOrcaLine> Lines;
        public NativeArray<NavOrcaLine> Scratch;

        /// <summary>每代理的邻居容量。</summary>
        public int MaxNeighbors;

        /// <summary>每代理的约束行容量（邻居 + 静态障碍）。</summary>
        public int MaxLines;

        // ---- ORCA 参数 ----

        public float TimeHorizon;
        public float TimeHorizonObst;
        public float TimeStep;
        public CollisionDimension Dimension;

        // ---- 静态障碍距离场；未加载时指针为 0，采样直接跳过 ----

        /// <summary>距离场基址（<c>byte</c> 或 <c>ushort</c>，按 <see cref="DistanceBits"/>）；0 = 无烘焙数据。</summary>
        [NativeDisableUnsafePtrRestriction] public long DistanceFieldPtr;

        public NavGrid ObstacleGrid;
        public byte DistanceBits;
        public float MaxBakeRadius;

        public void Execute(int index)
        {
            // 地面代理在导航平面内做二维求解；飞行代理（以及三维烘焙下的全部代理）走三维。
            float3 planeNormal = Modes[index] == (byte)NavAgentMode.Ground
                ? NavPlane.Normal(Dimension)
                : float3.zero;

            float3 position = Positions[index];
            float3 velocity = Velocities[index];
            float radius = Radii[index];

            int neighborOffset = index * MaxNeighbors;
            int lineOffset = index * MaxLines;

            int neighborCount = NavNeighborGrid.QueryNeighbors(
                GridSize,
                GridOrigin,
                CellSize,
                // 只读字段必须用 GetUnsafeReadOnlyPtr：GetUnsafePtr 会走 CheckWriteAndThrow，
                // 而 Job 系统已把 [ReadOnly] 字段的句柄置为只读，Editor 下直接抛
                // "has been declared as [ReadOnly] in the job, but you are writing to it"。
                (int*)CellStarts.GetUnsafeReadOnlyPtr(),
                (int*)CellCounts.GetUnsafeReadOnlyPtr(),
                (int*)SortedAgents.GetUnsafeReadOnlyPtr(),
                (float3*)Positions.GetUnsafeReadOnlyPtr(),
                (float*)Radii.GetUnsafeReadOnlyPtr(),
                (float*)NeighborDists.GetUnsafeReadOnlyPtr(),
                index,
                MaxNeighbors,
                MaxRadius,
                Dimension,
                (int*)NeighborIndices.GetUnsafePtr() + neighborOffset,
                (float*)NeighborDistances.GetUnsafePtr() + neighborOffset);

            NavOrcaLine* lines = (NavOrcaLine*)Lines.GetUnsafePtr() + lineOffset;
            int lineCount = 0;

            // 静态障碍优先入列：回退求解始终保留前 obstacleLineCount 条约束。
            int obstacleLineCount = 0;
            if (NavDistanceField.Sample(ObstacleGrid, (void*)DistanceFieldPtr,
                    DistanceBits == 16, MaxBakeRadius, position,
                    out float obstacleDistance, out float3 obstacleGradient)
                && math.lengthsq(obstacleGradient) > 0.25f
                && NavOrcaMath.StaticConstraint(position, velocity, radius, obstacleDistance,
                    obstacleGradient, TimeHorizonObst, TimeStep, planeNormal, out NavOrcaLine obstacleLine))
            {
                lines[lineCount++] = obstacleLine;
                obstacleLineCount = 1;
            }

            for (int i = 0; i < neighborCount && lineCount < MaxLines; i++)
            {
                int other = NeighborIndices[neighborOffset + i];
                if (!NavOrcaMath.AgentConstraint(position, velocity, radius,
                        Positions[other], Velocities[other], Radii[other],
                        TimeHorizon, TimeStep, planeNormal, out NavOrcaLine line))
                    continue;

                lines[lineCount++] = line;
            }

            bool planar = math.lengthsq(planeNormal) > 0f;
            float3 result;
            if (planar)
            {
                NavLinearProgram2D.Solve(lines, lineCount, obstacleLineCount, MaxSpeeds[index],
                    Preferred[index], Dimension, (NavOrcaLine*)Scratch.GetUnsafePtr() + lineOffset,
                    out result);
            }
            else
            {
                NavLinearProgram3D.Solve(lines, lineCount, MaxSpeeds[index], Preferred[index], out result);
            }

            // 地面代理：解出的速度必须落在运动平面内。
            //
            // 期望速度来自路径跟随，而航点是 grid.VoxelToWorld 的产物、带**烘焙平面**的 z
            // （本工程网格原点 z = -4），于是期望速度里混进一个指向该平面的分量。
            // 无约束时 NavLinearProgram2D 直接返回期望速度（平面法线只用于构造约束），
            // 代理便一路沉到烘焙平面上 —— 表现为代理 z 与游戏平面错开好几米。
            if (planar)
                result = NavPlane.Flatten(result, planeNormal);

            NewVelocities[index] = result;
            NeighborCounts[index] = neighborCount;
        }
    }
}
