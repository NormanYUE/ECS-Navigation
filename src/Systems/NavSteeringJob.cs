using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 路径跟随作业（每个代理一次并行执行）：推进航点并算出期望速度。
    ///
    /// 每个代理只碰自己的元素与只读的航点数组，无竞争，结果与调度顺序无关。
    /// </summary>
    [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    public struct NavSteeringJob : IJobParallelFor
    {
        public NativeArray<NavSteeringAgent> Agents;

        public void Execute(int index)
        {
            unsafe
            {
                NavSteeringAgent agent = Agents[index];
                NavPathFollower.Step(
                    (float3*)agent.WaypointPtr,
                    agent.WaypointCount,
                    agent.Position,
                    agent.Speed,
                    agent.ArriveRadius,
                    agent.LookAheadDistance,
                    ref agent.CurrentIndex,
                    out float3 desired);

                agent.DesiredVelocity = desired;
                Agents[index] = agent;
            }
        }
    }
}
