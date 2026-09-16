using Ember;
using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>寻路请求状态。</summary>
    public enum NavRequestStatus : byte
    {
        /// <summary>无请求。</summary>
        None = 0,

        /// <summary>已提交，等待调度（预算内未处理）。</summary>
        Pending = 1,

        /// <summary>正在搜索（已占用本帧预算）。</summary>
        InProgress = 2,

        /// <summary>路径就绪（<see cref="NavPathState"/> 持有有效航点句柄）。</summary>
        Ready = 3,

        /// <summary>失败：目标不可达或参数非法。</summary>
        Failed = 4,
    }

    /// <summary>
    /// 寻路请求组件：实体把目标写在这里，<c>NavRequestSystem</c> 按双限预算调度。
    /// 提交后路径在后续帧完成 —— 等待期间实体沿用旧路径或直冲 + ORCA，不阻塞。
    /// </summary>
    public struct NavRequest : IDataComponent
    {
        /// <summary>目标世界坐标（点目标）。</summary>
        public float3 Target;

        /// <summary>请求状态（由请求系统推进，业务侧写 Target 后把状态置 Pending）。</summary>
        public NavRequestStatus Status;
    }
}
