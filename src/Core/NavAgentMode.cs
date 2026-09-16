namespace Ember.Navigation
{
    /// <summary>导航代理的运动模式：地面（2D ORCA，水平面求解）或飞行（3D ORCA）。</summary>
    public enum NavAgentMode : byte
    {
        /// <summary>地面代理：ORCA 在水平面做 2D 线性规划，垂直轴由分离力 / 高度层处理。</summary>
        Ground = 0,

        /// <summary>飞行代理：真 3D 速度锥 + 3D 线性规划。</summary>
        Flying = 1,
    }
}
