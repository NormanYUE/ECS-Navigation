using Ember;

namespace Ember.Navigation
{
    /// <summary>
    /// 路径状态组件：代理当前持有的路径（航点表）进度。
    /// 航点数据存 <c>NavWorld</c> 侧句柄表（<see cref="BufferHandle"/> 指向
    /// World 托管 buffer 中的航点数组），随代理生命周期回收。
    /// </summary>
    public struct NavPathState : IDataComponent
    {
        /// <summary>航点数组句柄（<c>NavWorld</c> 侧句柄表项）。</summary>
        public BufferHandle Waypoints;

        /// <summary>航点数量。</summary>
        public int WaypointCount;

        /// <summary>当前目标航点下标（路径跟随推进）。</summary>
        public int CurrentIndex;

        /// <summary>路径代际：重烘焙 / 重新寻路后旧句柄即失效，以此辨别。</summary>
        public int Generation;
    }
}
