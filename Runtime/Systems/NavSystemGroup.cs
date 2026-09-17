namespace Ember.Navigation
{
    /// <summary>
    /// 全局寻路系统组：请求调度 → 分层 A* + 拉绳。
    /// <code>manager.GetTicker(updateIdx).Register&lt;NavSystemGroup&gt;();</code>
    ///
    /// <b>挂在可变步长 ticker</b>：全局寻路与局部避障的时间尺度不同，
    /// 避障要稳定步长（<see cref="NavAvoidanceSystemGroup"/>），寻路不必。
    ///
    /// 组内注册顺序即管线顺序：动态障碍局部失效 → 按优先级与数量上限挑出本帧请求
    /// → 维护流场缓存 → 在时间上限内逐个求解。数量与时间两个上限合起来就是双限预算。
    ///
    /// <b>动态障碍系统读碰撞快照</b>，故碰撞宽相必须在本组之前跑完
    /// （同一 ticker 内先注册碰撞系统组）。快照未就绪时本组整帧跳过，不会读到半成品。
    ///
    /// 流场缓存已在本组维护，但 <see cref="NavPathSystem"/> 尚未改走它 ——
    /// 当前每个请求仍跑分层 A*，流场梯度可供业务侧直接查询
    /// （<c>NavWorldView.TryGetFlowNext</c>）。把它接成寻路的下游快速路径是下一步。
    /// </summary>
    public sealed class NavSystemGroup : SystemGroup
    {
        // 框架将基类无参 Configure 标记 Obsolete 以强制显式重写（未来大版本改为 abstract）；
        // 重写 Obsolete 成员触发 CS0672，此处属框架过渡期的预期用法，抑制之。
#pragma warning disable CS0672
        public override void Configure(SystemTicker ticker)
#pragma warning restore CS0672
        {
            ticker.Register<NavDynamicObstacleSystem>();
            ticker.Register<NavRequestSystem>();
            ticker.Register<NavFlowFieldSystem>();
            ticker.Register<NavPathSystem>();
        }
    }
}
