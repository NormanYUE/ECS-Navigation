using Ember;
using Ember.Collision;

namespace Ember.Navigation
{
    /// <summary>
    /// 导航配置（单例组件）。维度 / 体素 / tile / 预算 / ORCA 参数的权威来源。
    /// solver / slop / correction / sleep 字段为 Ember.Collision P4 求解器的预留参数，
    /// 求解器注册前不参与任何计算。
    /// </summary>
    public struct NavConfig : ISingletonComponent
    {
        /// <summary>导航维度：XY / XZ 为 2D（三处快速路径特化），XYZ 为 3D。</summary>
        public CollisionDimension Dimension;

        /// <summary>体素边长（米）。初步 0.25–0.5，须 Unity 宿主实测校准。</summary>
        public float VoxelSize;

        /// <summary>tile 边长（体素数，2 的幂推荐 32）。稀疏存储只建非空 tile。</summary>
        public int TileSize;

        /// <summary>距离场量化位宽：8 或 16。8 位精度 = 烘焙半径上限 / 255。</summary>
        public byte DistanceFieldBits;

        /// <summary>烘焙时支持的最大代理半径（米），距离场量化的满量程。</summary>
        public float MaxBakeRadius;

        /// <summary>每帧寻路请求数量上限（双限预算之一）。</summary>
        public int RequestBudgetPerFrame;

        /// <summary>每帧寻路时间上限毫秒（双限预算之二，吃 <c>WorldTime</c>）。</summary>
        public float RequestBudgetMs;

        /// <summary>流场缓存池容量（按目标 key 索引，LRU 淘汰）。</summary>
        public int FlowFieldCacheSize;

        /// <summary>ORCA 默认时间视界（秒）。</summary>
        public float TimeHorizon;

        /// <summary>ORCA 静态障碍时间视界（秒）。</summary>
        public float TimeHorizonObst;

        /// <summary>邻居网格单元边长（米，0 = 自动取 2 × 最大代理半径）。</summary>
        public float NeighborCellSize;

        /// <summary>每代理参与 ORCA 的最大邻居数。</summary>
        public int MaxNeighbors;

        /// <summary>路径跟随的航点到达半径（米）。</summary>
        public float ArriveRadius;

        /// <summary>路径跟随沿折线的前瞻距离（米），0 = 直冲当前航点。</summary>
        public float LookAheadDistance;

        /// <summary>新代理默认半径。</summary>
        public float DefaultRadius;

        /// <summary>新代理默认最大速度。</summary>
        public float DefaultMaxSpeed;

        // ---- 预留：碰撞求解器（Ember.Collision P4）参数，注册前不参与计算 ----

        /// <summary>【预留】顺序冲量迭代次数。</summary>
        public int SolverIterations;

        /// <summary>【预留】允许穿透余量（米）。</summary>
        public float SolverSlop;

        /// <summary>【预留】位置修正百分比（0–1）。</summary>
        public float SolverCorrectionPercent;

        /// <summary>【预留】睡眠速度阈值。</summary>
        public float SolverSleepThreshold;

        /// <summary>出厂默认值（2D XY、0.5m 体素、32³ tile、8 位距离场）。</summary>
        public static NavConfig Default => new()
        {
            Dimension = CollisionDimension.XY,
            VoxelSize = 0.5f,
            TileSize = 32,
            DistanceFieldBits = 8,
            MaxBakeRadius = 8f,
            RequestBudgetPerFrame = 8,
            RequestBudgetMs = 2f,
            FlowFieldCacheSize = 16,
            TimeHorizon = 2f,
            TimeHorizonObst = 4f,
            NeighborCellSize = 0f,
            MaxNeighbors = 8,
            ArriveRadius = 0.5f,
            LookAheadDistance = 1f,
            DefaultRadius = 0.5f,
            DefaultMaxSpeed = 3f,
            SolverIterations = 8,
            SolverSlop = 0.005f,
            SolverCorrectionPercent = 0.8f,
            SolverSleepThreshold = 0.1f,
        };
    }
}
