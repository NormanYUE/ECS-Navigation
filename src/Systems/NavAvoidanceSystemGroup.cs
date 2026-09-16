namespace Ember.Navigation
{
    /// <summary>
    /// 避障系统组：路径跟随 → ORCA 求解，封装为一次注册。
    /// <code>manager.GetTicker(fixedIdx).Register&lt;NavAvoidanceSystemGroup&gt;();</code>
    ///
    /// <b>必须挂在固定步长 ticker</b>：ORCA 的时间视界预测要求稳定步长。
    ///
    /// 速度积分（<c>Ember.Core.MovementSystem</c>）由业务侧在其后单独注册 ——
    /// 同一 ticker 内注册顺序决定依赖方向，避障先写 <c>LinearVelocity</c>，积分后读：
    /// <code>
    /// manager.GetTicker(fixedIdx).Register&lt;NavAvoidanceSystemGroup&gt;();
    /// manager.GetTicker(fixedIdx).Register&lt;MotionSystemGroup&gt;();
    /// </code>
    /// 业务已有自己的积分系统时可不注册后者，自行接管。
    /// </summary>
    public sealed class NavAvoidanceSystemGroup : SystemGroup
    {
        // 框架将基类无参 Configure 标记 Obsolete 以强制显式重写（未来大版本改为 abstract）；
        // 重写 Obsolete 成员触发 CS0672，此处属框架过渡期的预期用法，抑制之。
#pragma warning disable CS0672
        public override void Configure(SystemTicker ticker)
#pragma warning restore CS0672
        {
            ticker.Register<NavSteeringSystem>();
            ticker.Register<NavAgentSystem>();
        }
    }
}
