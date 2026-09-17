using Ember;

namespace Ember.Navigation
{
    /// <summary>
    /// 导航代理组件：参与转向 / 避障 / 寻路请求的实体标记与参数。
    /// 2D/3D 统一 —— 维度由 <see cref="NavConfig.Dimension"/> 决定，本组件不感知维度。
    /// </summary>
    public struct NavAgent : IDataComponent
    {
        /// <summary>代理半径（米）。烘焙距离场判定与 ORCA 约束共用。</summary>
        public float Radius;

        /// <summary>最大速度（米/秒）。ORCA 新速度截断上限。</summary>
        public float MaxSpeed;

        /// <summary>邻居查询半径（米）：与半径和比较取大者，见 RVO2 语义。</summary>
        public float NeighborDist;

        /// <summary>运动模式（地面 2D / 飞行 3D）。</summary>
        public NavAgentMode Mode;

        /// <summary>请求调度优先级：战斗 &gt; 巡逻 &gt; 闲逛，数值大者先调度。</summary>
        public byte Priority;

        /// <summary>ORCA 时间视界覆盖（秒）；&lt;= 0 时用 <see cref="NavConfig.TimeHorizon"/>。</summary>
        public float TimeHorizonOverride;

        /// <summary>构造导航代理。</summary>
        public NavAgent(float radius, float maxSpeed, NavAgentMode mode = NavAgentMode.Ground,
            float neighborDist = 0f, byte priority = 0, float timeHorizonOverride = 0f)
        {
            Radius = radius;
            MaxSpeed = maxSpeed;
            Mode = mode;
            NeighborDist = neighborDist;
            Priority = priority;
            TimeHorizonOverride = timeHorizonOverride;
        }
    }
}
