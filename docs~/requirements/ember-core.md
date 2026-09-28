# Ember.Core 需求：移动积分系统

> 来源：Ember.Navigation 模块 ｜ 优先级：P1（可降级，导航可自建）

## 背景

Ember.Navigation 是规划中的 2D/3D 导航模块，提供海量实体寻路、ORCA 单位间避碰与自动避障。

导航的移动链是：

```
寻路 → 路径跟随 → 期望速度 → ORCA 避障求解 → LinearVelocity → 【积分】→ LocalTransform
```

末段「积分」这一步落在 Ember.Core 的职责范围内 —— 本模块已定义 `LinearVelocity` 与
`AngularVelocity` 组件，但缺少与之配套的积分系统。

## 需求总表

| 编号 | 内容 | 优先级 |
|---|---|---|
| [E1](#e1--移动积分系统) | 移动积分系统 | P1 |

---

## E1 · 移动积分系统

**优先级：P1** —— 可降级（导航可自建私有积分系统），但建议放在通用层

### 现状

`Ember.Core` 已定义两个速度组件：

| 组件 | 说明 |
|---|---|
| `LinearVelocity` | 线速度（米/秒） |
| `AngularVelocity` | 角速度，轴角向量表示（方向为旋转轴，模长为弧度/秒） |

`README.md` 的组件清单中明确写道：

> `LinearVelocity` | 线速度（米/秒），**由移动系统积分到 `LocalTransform.Position`**

但 **`Ember.Core/src/Systems/` 中并不存在这个移动系统**。该目录当前只有：

```
src/Systems/
├── Culling/         视锥剔除
├── Spatial/         空间索引与包围盒
└── Presentation/    GameObject 表现层
```

组件定义了，积分系统缺失。

### 需求形态

`MovementSystem`：

- 继承 `JobSystem<TJob>`，Burst 编译
- 读 `LinearVelocity` / `AngularVelocity` / `WorldTime`
- 写 `LocalTransform`
  - 位置：`Position += LinearVelocity.Value * dt`
  - 旋转：按角速度轴角积分（方向为旋转轴，模长为弧度/秒）
- 查询附带 `None<Static>` 与 `None<Disabled>`
- 归属一个系统组（如新建 `MotionSystemGroup`），或作为独立系统供业务自行注册

时间来源使用 `WorldTime.DeltaTime`（遵守 `TimeScale`），与 `Ember.Core` 既有的
`WorldTime` 单例语义保持一致。

### 动机

导航的 ORCA 求解输出最终要落到速度，再积分到变换。这条积分链放在通用组件库比放在导航模块内好：

- **共用** —— 子弹、特效、抛射物、非导航实体共用同一套积分，不必各自实现
- **职责干净** —— 导航只负责**写** `LinearVelocity`，不负责积分语义；导航模块不需要知道
  变换如何被更新，也不需要与其它写 `LocalTransform` 的系统争夺所有权
- **不破坏既有约定** —— `README.md` 已经承诺了该系统的存在

### 可降级

导航可以自建一个私有的积分系统。代价：

- 与 `Ember.Core` 的 `LinearVelocity` / `AngularVelocity` 语义绑定，但实现游离在组件库之外
- 使用导航的工程若同时有非导航实体需要积分，会出现两套积分逻辑
- `README.md` 中「由移动系统积分」的说明继续悬空

因此建议放在 `Ember.Core`，但这不是阻断项。

### 验收标准

- 匀速直线运动：位移 = 速度 × 时间，含 `TimeScale` 缩放
- 匀加速（外部每帧改速度）：结果符合逐帧积分的期望值
- 角速度积分：绕指定轴的旋转角度正确，模长为零时旋转不变
- `Static` 与 `Disabled` 实体不被改动
- 与 `WorldTime.DeltaTime` / `TimeScale` / `UnscaledDeltaTime` 语义一致
- Burst 编译通过
- 稳态零分配

---

## 明确不需要本模块做的

- **不需要修改 `LinearVelocity` / `AngularVelocity` 的现有定义** —— 导航直接使用
- **不需要 `SpatialTree` 并行化** —— 导航的热路径邻居查询使用自建的均匀网格哈希，
  不依赖 `SpatialTree`；现有 gameplay 查询用途不受影响
- **不需要导航专用组件** —— 导航的组件全部定义在 `Ember.Navigation` 内

## 与其它模块需求的关系

导航模块共提出五项需求，分属三个上游模块：

| 模块 | 需求 | 文档 |
|---|---|---|
| Ember.Collision | N1 shape→point 查询、N2 body 快照、N3 精确 raycast | `ember-collision.md` |
| Ember.Core | E1 移动积分系统 | 本文档 |
| Ember | F1 Buffer 批量写入 | `ember-framework.md` |

**建议实现时机**：E1 是导航 P5（地面 ORCA）的前置条件。在此之前导航处于烘焙与寻路阶段，
尚未产出速度，因此 E1 的排期压力低于 Ember.Collision 的 N1 / N2 与 Ember 的 F1。
