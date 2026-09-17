using Ember;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 期望速度组件：本帧代理「想去哪」。
    ///
    /// 由路径跟随写出（<c>NavSteeringSystem</c>），也允许业务侧直接写 ——
    /// 无路径单位的期望速度来自游戏逻辑，与路径单位汇入同一条下游管线。
    /// 消费方是 ORCA 求解（<c>NavAgentSystem</c>），它读期望速度、写
    /// <see cref="Ember.Core.LinearVelocity"/>。
    /// </summary>
    public struct NavDesiredVelocity : IDataComponent
    {
        /// <summary>期望速度（米/秒）。</summary>
        public float3 Value;

        public NavDesiredVelocity(float3 value) => Value = value;
    }
}
