# Ember Navigation 使用手册

Ember ECS 框架的导航包：导航网格烘焙、分层寻路与双层 ORCA 避障。

- 基于 Ember 的 Job 调度与 Burst，热路径零分配
- 距离场表述（一次烘焙支持任意代理半径：`distanceField[v] >= r` 即可行走）
- 2D / 3D 统一：地面走平面内二维求解，飞行走真三维

## 安装

`manifest.json` 里加依赖：

```json
"com.ember.navigation": "https://github.com/NormanYUE/Ember-Navigation.git"
```

依赖 `com.ember.ecs` 1.11.0、`com.ember.core` 2.1.0、`com.ember.collision` 0.3.0。

> **Unity 2022 在 Apple Silicon + CLT 27.0 上拉不下来**：UPM 辅助进程是 x86_64，
> 而新 CLT 的 `libxcrun` 只剩 arm64。装转发脚本或改用 Unity 6，详见仓库 issue。

## 接入

导航代理需要一组固定的组件，**创建时一次挂齐**（导航系统不中途增删组件）：

```csharp
var entity = world.CreateEntity();
world.AddComponent(entity, new LocalTransform(position, quaternion.identity, 1f));
world.AddComponent(entity, new LinearVelocity(float3.zero));   // Ember.Core
world.AddComponent(entity, new NavAgent(radius: 0.5f, maxSpeed: 3.5f, mode: NavAgentMode.Ground));
world.AddComponent(entity, new NavRequest { Target = target, Status = NavRequestStatus.None });
world.AddComponent(entity, new NavPathState());
world.AddComponent(entity, new NavDesiredVelocity());
```

系统组挂两个 ticker：

```csharp
manager.GetTicker(updateIdx).Register<NavSystemGroup>();          // 寻路：变步长
manager.GetTicker(fixedIdx).Register<NavAvoidanceSystemGroup>();  // 避障：定步长
manager.GetTicker(fixedIdx).Register<MotionSystemGroup>();        // 积分，须在避障之后
```

ORCA 的时间视界预测要求稳定步长，所以避障链跑定步长。积分用 `Ember.Core` 的
`MotionSystemGroup`；业务已有自己的积分系统时不注册它，自行接管即可。

## 烘焙

**离线**：编辑器窗口 `Ember/Navigation/烘焙窗口`（随包的 `Editor/` 程序集提供）。烘焙在 Play 模式进行 ——
碰撞体在 ECS 世界里，编辑模式没有那个世界。产物存成 `NavBakedAsset`。

**运行时**：

```csharp
using var workspace = new NavRuntimeBakeWorkspace();
var colliders = new NativeArray<NavBakeCollider>(capacity, Allocator.Temp);

int count = NavRuntimeBake.GatherColliders(world, staticOnly: true,
    (NavBakeCollider*)colliders.GetUnsafePtr(), colliders.Length);

NavRuntimeBake.BakeAndLoad(world, input, (NavBakeCollider*)colliders.GetUnsafePtr(), count,
    vertexPool, vertexPoolLength, workspace);
```

**只烘焙静态碰撞体**：动态障碍进烘焙会把会动的东西固化进距离场，
它们由 `NavDynamicObstacleSystem` 局部失效处理。

烘焙参数要与 `NavConfig` 对齐 —— 两处不一致会让运行期求解器按错误的连通度展开邻居。

## 导航标注

场景里挂 `NavAnnotationVolume`，圈出轴对齐世界 AABB：

| 模式 | 作用 |
| --- | --- |
| `ForceWalkable` | 覆盖距离场的不可走判定 |
| `ForceBlocked` | 强制阻挡 |
| `None` | 只改代价乘数（0.5 快，3 慢） |

范围只取位置不取旋转 —— `Aabb` 本身是轴对齐语义，需要斜置区域时叠几个轴对齐块。

## 运行时查询

流场梯度可直接查，不必等寻路：

```csharp
if (world.TryGetNavWorld(out var nav) && nav.IsReady)
{
    // 体素级查询
    bool walkable = nav.IsWalkable(voxel, agentRadius);
    int region = nav.RegionAt(voxel);
    bool blocked = nav.IsOccupied(voxel);
}
```

## 调参（NavConfig 单例）

| 字段 | 说明 |
| --- | --- |
| `Dimension` | XY / XZ 为 2D，XYZ 为 3D |
| `VoxelSize` | 体素边长（米），初步 0.25–0.5，须实机校准 |
| `MaxBakeRadius` | 烘焙支持的最大代理半径，距离场满量程 |
| `RequestBudgetPerFrame` / `RequestBudgetMs` | 双限预算：数量与时间，先到为准 |
| `TimeHorizon` / `TimeHorizonObst` | ORCA 时间视界（代理间 / 静态障碍） |
| `MaxNeighbors` | 每代理参与 ORCA 的最大邻居数 |
| `FlowFieldCacheSize` / `FlowFieldPopBudget` | 流场缓存槽位数与每帧总弹出预算 |
| `ArriveRadius` / `LookAheadDistance` | 路径跟随的到达半径与前瞻距离 |

## 已知边界

- **系统层与 Job 层未在 CLI 下验证** —— 依赖 Unity 运行时，需在 Unity Test Runner 中执行。
  算法内核有纯逻辑测试覆盖。
- 距离场求解当前按 8 位等级解释；16 位需要先把求解器的等级宽度参数化。
- `NavHpaPathfinder` 的 `LastStatus` / `DebugLastSegment*` 是静态状态，
  多 World 并行时会互相覆盖；并行化前需要搬进 `Context`。
- 编辑器工具（烘焙窗口、标注体编辑、网格可视化）以 `Editor/Ember.Navigation.Editor.dll` 随包提供，
  无需手动拷贝源码。

## 说明

- 版本号使用纯 `a.b.c`。
- 本仓库只存放编译产物与文档，源码在私有仓库维护。
