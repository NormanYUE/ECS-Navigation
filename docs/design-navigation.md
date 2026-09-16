# Ember.Navigation 设计文档

Ember 框架 + Ember.Core + Ember.Collision 之上的 2D/3D 导航模块。目标：高性能、海量实体、
单位间避碰（ORCA）与自动避障，支持编辑器内烘焙与运行时烘焙。

**2D/3D 统一**：维度由 `NavConfig.Dimension` 决定，不维护两套代码路径。2D 通过三处特化获得快速路径，
而非分叉。

---

## 1. 定位与目标

### 1.1 要解决的问题

- 海量实体（1k – 100k）的全局寻路与局部避障
- 静态障碍的自动避让与动态障碍的推挤响应
- 导航数据的编辑器内可编辑烘焙，同一套算法同时服务运行前与运行时烘焙

### 1.2 明确不做

- 物理响应（碰撞求解、冲量、摩擦）—— 属 Ember.Collision P4，本模块不越界
- NavMesh 三角面级烘焙（Recast 风格的区域划分 + 轮廓提取 + 凸多边形化）—— 本模块以体素为
  权威表示，不做多边形提取
- 承担碰撞检测职责 —— 本模块只消费几何事实，不产出接触流形

### 1.3 与 Ember.Collision 的关系

**分层解耦**。Ember.Collision 提供几何事实（形状定义、精确查询），Ember.Navigation 独占导航语义
（体素数据、寻路、ORCA、转向、请求调度、标注层、编辑器工具）。

导航**不使用**碰撞模块的 pair / manifold 流水线 —— 那是碰撞检测语义，与导航无共享算法。
导航宽相自建，但复用碰撞模块已公开的算法内核（`RadixSort32` / `BlockScan` / `MortonCoder` /
`BvhBuilder`）。

详见第 6 节依赖方能力清单与第 10 节复用关系对照。

---

## 2. 模块边界与数据流

### 2.1 程序集划分

| 程序集 | 内容 | 依赖 |
|---|---|---|
| `Ember.Navigation`（netstandard2.1 库） | 纯算法核心 + ECS 集成 | Ember、Ember.Core、Ember.Collision、Unity.Mathematics、Unity.Collections、Unity.Burst |
| `Ember.Navigation.Package/Runtime` | asmdef 引用上述库 | 同上 |
| `Ember.Navigation.Package/Editor` | 编辑器窗口、Gizmo、场景绘制 | + `Ember.Editor` |

库结构与 Ember.Collision 同构，命名空间统一 `Ember.Navigation`：

```
src/Core/       组件与配置
src/Geometry/   纯算法内核 —— 裸指针静态函数，Job 只做薄包装
src/Bake/       烘焙核心（Editor 与 Runtime 共用同一条路径）
src/World/      NavWorld 单例 + NavWorldView
src/Systems/    系统与 Job
src/Editor/     编辑器工具（仅 Unity 包侧）
```

### 2.2 核心不变式

**算法活在裸指针静态函数里，Job 只做薄包装。**

原因与 Ember.Collision 相同：纯 .NET CLI 无法分配 `NativeArray`，若算法只存在于 Job 内，
最容易出隐蔽 bug 的逻辑在 Unity 之外完全无法验证。碰撞模块靠这条换来 165 项可执行测试，
导航沿用。

其余继承的不变式：

- 容量必须在调度 Job 之前一次性决定；**Job 执行期严禁扩容**（`BufferStore` 扩容走
  「另分配 + 拷贝」，会搬移全部 range 地址）
- 并行输出用「计数 → 前缀和 → 散布」两段式，替代 `NativeList` 竞争追加：无锁、无原子、零分配
- 输出顺序只取决于确定性排序，因此同输入必得同输出
- 禁止跨 tick 挂起 Job（`WorldSafety` 为 internal，跨 tick 会破坏框架的结构变更保护）

### 2.3 运行时数据流

```
寻路层（低频，分帧预算）
  群体目标 → 流场          ┐
  个体目标 → 分层 A* → 平滑 ┘→ NavPath（buffer）
                                   ↓
转向层（每帧）                  路径跟随 → 期望速度
                                   ↓
避障层（固定步长，Job·Burst）   邻居查询（自建均匀网格）
                                ORCA 求解（地面 2D / 飞行 3D）
                                   ↓
                                LinearVelocity
                                   ↓
积分层（固定步长）              MovementSystem → LocalTransform
```

**关键解耦：ORCA 只消费「期望速度」。**

它不关心期望速度从哪来 —— 路径跟随产出的，或游戏逻辑直接给出的。因此无路径的直冲单位
（追击、逃跑、散兵）走同一条避障路径，不需要伪造路径。

### 2.4 烘焙数据流 —— 一条核心，两侧薄壳

```
[Editor 烘焙]
  场景 Collider（复用 Ember.Collision.ShapeType / ShapeParams）
  + 标注层（可行走标记 / 区域代价 / off-mesh link）
        ↓ NavBaker（纯算法，零 UnityEditor 依赖）
  导航数据 blob

[Runtime 加载]  blob → 单次写入 World 托管 buffer → NavWorld 单例

[Runtime 烘焙]  运行时 Collider 列表 → 同一条 NavBaker → 热替换 NavWorld
```

编辑器壳只负责「从场景收集 Collider + 读标注 asset」，运行时壳只负责「从碰撞模块的 body 快照
收集 Collider」。烘焙算法本体一份代码、一份测试。

### 2.5 双维度统一 + 2D 快速路径

体素格是唯一权威表示。维度只改变三个参数：

| 参数 | 2D | 3D |
|---|---|---|
| `Dim` | 2 | 3 |
| 邻接模板 | 4 / 8 邻接 | 6 / 26 邻接 |
| 轴映射 | XY 或 XZ（复用 `CollisionDimension` 语义） | XYZ |

2D 快速路径落在三处特化，而非分叉：

1. **寻路**：2D 均匀代价时用 JPS（跳点搜索），格子平面天然适配；3D 退化为分层 A*
2. **距离场**：2D 是单层 8SSEDT，内存与计算量降一个维度
3. **ORCA**：地面求解器本身就是 2D 线性规划；2D 场景是它的退化情形

---

## 3. 烘焙与数据格式

### 3.1 输入

**几何输入**：Collider 快照列表 —— 形状（复用 `ShapeType` / `ShapeParams`）、世界变换、`CollisionFilter`
层、静态标记。

**标注层**（Editor asset，手工编辑的只有这一层）：

- 可行走覆盖标记（推翻体素化的自动判定）
- 区域代价乘数（沼泽 3x、公路 0.5x）
- off-mesh link（跳跃点、传送门、门）
- 参与烘焙的层掩码

### 3.2 核心决定：距离场，一份数据支持任意代理半径

体素化的直接产物是「占据格」。但可行走性依赖代理半径 —— 半径 0.5 的代理过不去 0.6 宽的缝隙。
常规做法是按半径烘焙多份导航网格，内存与烘焙时间翻倍。

改为在烘焙时对每个体素计算「到最近障碍的距离」并量化存储。运行时判定：

```
可通行(体素 v, 代理半径 r)  ⟺  distanceField[v] >= r
```

一份数据支持任意半径，切换半径零成本。该距离场同时一物三用：

| 用途 | 说明 |
|---|---|
| 动态侵蚀 | 上述判定，替代多份烘焙 |
| ORCA 障碍约束 | 贴墙单位需要「到墙距离 + 墙法线」，距离场梯度直接给出 |
| 路径平滑 | 拉绳时查安全距离，替代逐段射线检测 —— 主线程热点直接消失 |

### 3.3 体素化

**分块稀疏**：世界切成 tile（默认 32³ 体素），只存非空 tile。2D 场景 tile 退化为单层。
大世界内存这才成立。

**算法**（`NavBaker` 核心，纯函数）：

```
对每个 Collider：
  世界 AABB → 覆盖体素范围（保守扩张）
  → 逐体素计算 shape → point 最近点距离 → 写距离场（取 min）
  → 距离场 <= 0 的格标记为占据

应用标注层：
  可行走覆盖标记 → 改占据位
  代价层 → 写每体素代价乘数
  off-mesh link → 写 link 表

后处理：
  分块 → 连通区域标记（region id，用于不可达快速判定）
  → 分层簇图构建（见 3.4）
```

体素化依赖 `shape → point` 最近点距离查询，见第 6 节依赖项 **N1**。

### 3.4 分层簇图

烘焙时把体素按连通性聚成**簇**，提取**门户**（簇间连通处的体素），构建簇图。这是 HPA* 的结构，
预计算一次，永久复用。

- 簇划分：按 tile 聚类，跨 tile 合并同区域连通块
- 门户：簇边界上双向可通行的体素对
- 簇间邻接：门户构成的加权边，权重 = 簇内穿越代价

簇图节点数比体素数少约两个数量级，这是分层 A* 的成本来源。

### 3.5 烘焙产物布局

| 段 | 内容 | 用途 |
|---|---|---|
| `Grid` | 体素大小、原点、维度、tile 尺寸、tile 索引表 | 一切的地基 |
| `Occupancy` | 稀疏分块位集 | 占据判定 |
| `Distance` | 每体素量化距离（8/16 位） | 侵蚀 / 贴墙 / 平滑 |
| `Region` | 连通区域 id | 不可达判定、目标验证 |
| `Cost` | 代价乘数（量化） | 加权寻路 |
| `ClusterGraph` | 簇划分 + 簇边界节点 + 簇间邻接 | 分层 A* |
| `Links` | off-mesh link 双向表 | 跨接缝连通 |
| `Meta` | 版本、烘焙半径上限、包围盒 | 兼容性与校验 |

段之间以偏移表索引，**磁盘上是一个连续 blob**（便于一次性读取、校验与版本检查），
加载后按段拆入各自类型的运行时 buffer（见 3.6）。

### 3.6 运行时表示

全部存 World 托管 buffer（`BufferHandle` + `BufferSpan`），`NavWorld` 单例持句柄，`NavWorldView`
操作 —— 与 `CollisionWorld` / `SpatialTree` 完全同一资源模型：无需 Dispose，随 `World.Dispose` 释放。

**blob 段与运行时 buffer 是一对一映射**：每个段按其元素类型建一个 typed buffer
（`Occupancy` → `byte`，`Distance` → `byte` 或 `ushort`，`Region` → `int`，`Cost` → `byte`，
`ClusterGraph` → 若干结构体 buffer）。不做「一个 byte buffer 全装下再手工解析」——
typed buffer 让 Job 内直接按类型取指针，省掉全部运行时解包。

加载 = 每段一次 `ResizeBuffer` + 一次 `MemCpy`（依赖第 6 节依赖项 **F1**；F1 未提供时走降级
路径，见 6.4）。`Meta` 段在拆段前校验版本与维度，不匹配直接拒绝加载并报错。

运行时重烘焙 = 新数据写入 → 原子换句柄，旧数据下一帧回收。**热替换不打断现有寻路**：正在使用旧
数据的系统在本帧继续读完，句柄在帧末切换。

---

## 4. 寻路结构

两条腿跑在同一份体素与同一份簇图上，只是产出的东西不同。

### 4.1 流场层（群体共享目标）

```
目标（点 / 区域 / 点集）
  → 多源 Dijkstra（加权，吃代价层）→ 距离场
  → 梯度场（每体素指向代价下降最快的邻格）
  → 实体只需查自己所在体素 → 期望速度
```

- **增量与分帧**：大网格一次 Dijkstra 会超帧预算 → 按 tile 分帧推进。未完成的流场标记 `Pending`，
  实体沿用旧流场或直冲。
- **缓存池**：按目标 key 索引，LRU 淘汰。同一目标的 10000 个单位只算一次 —— **寻路成本与实体数
  解耦**。
- **失效**：动态障碍只让覆盖的 tile 失效，局部重算。
- **2D 快速路径**：单层格，Dijkstra 退化为 2D 数组扫描。

### 4.2 分层 A* 层（个体目标）

```
簇间 A*（簇图 + 门户，节点数少两个数量级）
  → 簇序列
  → 簇内 A* 连接门户（小范围，成本低）
  → 完整体素路径
  → 漏斗平滑（拉绳，用距离场查安全距离）
  → NavPath 航点表
```

路径存储采用 **`NavWorld` 侧句柄表**：每个代理持一个 `BufferHandle` 指向其航点数组。

选择理由：`IBufferElement` 的实体绑定存储（`BufferElementStore<T>`）底层是托管 `List<T>`，
Job 内无法取得裸指针；而 `NavWorld` 侧句柄走 `BufferStore<T>`，可在调度前取 `BufferSpan.UnsafePtr`
交给 Job，与模块整体资源模型一致。

代理的路径句柄在请求完成时由串行侧写入，句柄表随代理生命周期回收。

### 4.3 请求调度 —— 海量实体的命门

`NavRequest` 进优先队列（带优先级），调度系统每帧取预算内的请求：

- **双限预算**：数量上限 + 时间上限（吃 `WorldTime`），二者先到为准
- **优先级**：战斗单位 > 巡逻 > 闲逛；无路径单位不占预算
- **即时失败**：目标的 region id 与自身不同且无 link 可达 → 立刻判失败，不进搜索
- **异步语义**：请求提交后路径在后续帧完成。等待期间实体沿用旧路径或直冲 + ORCA ——
  **不阻塞、不卡帧**

### 4.4 路径跟随与转向

```
NavPath（航点表） → 路径跟随（当前航点推进 + 到达判定）
                  → 转向前瞻（拉绳，距离场查安全距离）
                  → 期望速度（desired velocity）
```

输出只是期望速度。无路径单位的期望速度直接来自游戏逻辑，与路径单位汇入同一条下游管线。

### 4.5 2D 快速路径特化

| 环节 | 2D | 3D |
|---|---|---|
| 流场 | 单层数组扫描 | 3D 距离场 + 分帧 |
| 个体寻路 | 均匀代价走 JPS | 分层 A* |
| 平滑 | 2D 拉绳 | 漏斗算法（3D 门户） |

代价层非均匀时 2D 退回 A*（JPS 要求均匀代价格）—— 这是唯一的分叉判据，其余全部同码。

---

## 5. ORCA 双层求解器

### 5.1 邻居查询 —— 自建，不用碰撞宽相

ORCA 每代理要的是**自己的邻居列表**（半径和 + 时间视界内），不是全局相交 pair 集合。碰撞宽相是
「相交 pair」语义，模型不匹配；且代理每帧移动，是导航的一等公民。

改为**均匀网格哈希**（RVO2 的 kd-tree 在 ECS 里的并行等价物）：

```
清桶 → 并行 scatter 代理到桶（单元边长 = 2 × 最大代理半径）
     → 每代理并行查 3×3（2D）/ 3×3×3（3D）桶 → 邻居列表
```

两段式（计数 → 前缀和 → 散布）复用已公开的 `RadixSort32` / `BlockScan` 内核，**无需依赖方改动**。

### 5.2 快照式并行 —— 无锁与确定性

代理互相约束天然是图问题。两种解法：

| | 快照式（本设计采用） | 迭代 / 图着色式 |
|---|---|---|
| 并行性 | 完全并行，每代理独立求解 | 需分组或迭代屏障 |
| 确定性 | 是（读同一帧快照） | 依赖分组顺序 |
| 竞争 | 无锁、无原子 | 需要同步 |
| 避让质量 | ORCA 约束本身对称，实践足够 | 略优，成本高 |

**快照式**：所有代理读同一帧的位置与速度快照，各自独立求解，写自己的新速度。双缓冲速度数组
（读旧写新），零竞争、零原子、结果确定。

### 5.3 统一约束构建，只分线性规划维度

```
共享前半：相对位置 / 相对速度 / 半径和 / 时间视界 τ → ORCA 半平面集合
分歧后半：2D 线性规划  vs  3D 线性规划
```

静态障碍约束**走距离场，不走碰撞几何** —— 3.2 节的距离场在此兑现：梯度给出最近障碍方向与距离，
直接构造半平面。无需对障碍做形状查询，无需射线。

### 5.4 地面求解器 = 2D，飞行求解器 = 3D

- **地面**：在水平面做 2D ORCA（邻居投影到平面），垂直轴交给分离力 / 重力 / 高度层
- **飞行**：真 3D 速度锥 + 3D 线性规划

实体以 `NavAgentMode` 标记：

```csharp
enum NavAgentMode : byte { Ground, Flying }
```

两个独立 Job 并行跑各自的代理集合，互不干扰，共享邻居查询与约束构建前半。
**2D 场景永不实例化 3D 求解器。**

### 5.5 时间步

ORCA 的时间视界预测要求稳定步长 —— 避障系统注册在固定步长 ticker（`fixedIdx`）上。
可变步长的 update ticker 只跑全局寻路（第 7 节）。

---

## 6. 依赖方能力清单

> 本节是**设计视角**的清单，说明导航依赖什么以及为什么。
> 面向依赖方的**可转发交付文档**（含完整的现状证据、需求形态代码、验收标准）见
> `docs/requirements/` 下的三份独立文档。若两者不一致，以本节为准并同步更新交付文档。

### 6.1 Ember.Collision

#### N1 · 公开 shape → point 最近点 / 距离查询 — P0，体素化必需

**现状**：`NarrowphaseMath.cs` 内 `ClosestPointOnSegment`(:1262)、`ClosestPointsOnSegments`(:1271)、
`ClosestSegmentAabb`(:1332)、`TrySegmentAabb`(:1378)、`PointAabbDistanceSq`(:1414) 全是 `private`，
且没有「形状 → 点」的顶层入口。

**需求形态**：

```csharp
public static class ShapeQuery
{
    public static float ClosestPoint(
        in Collider collider, in BodyPose pose, float3 point,
        out float3 closest, out float3 normal);   // 返回距离；内部返回负值表示点在形状内
}
```

覆盖全部 7 种 `ShapeType`，Burst 兼容纯静态函数。

**动机**：体素化逐格计算到每个 Collider 的距离，距离场精度直接决定导航质量（可行走判定、
贴墙法线、路径平滑安全距离全部由它派生）。

**工作量**：提炼已有 private 函数 + 补形状外壳。**非新写几何**。

**验收**：每种形状的解析解对拍测试（球 / 盒 / 胶囊 / 圆 / 盒2D / 胶囊2D / 凸多边形），
覆盖点在形状内、外、边界三种情形。

#### N2 · 公开 body 快照读取 — P0，运行时烘焙必需

**现状**：`CollisionWorld.BodyPoses` / `BodyColliders` / `BodyFilters` / `BodyBounds` 全 `internal`；
`CollisionWorldView` 对应数组也是 `internal`。

**需求形态**（二选一）：

```csharp
// 方案 A：公开只读访问器
public readonly int BodyCount { get; }
public readonly NativeArray<BodyPose> BodyPoses { get; }
public readonly NativeArray<Collider> BodyColliders { get; }
public readonly NativeArray<CollisionFilter> BodyFilters { get; }

// 方案 B：批量导出
public bool ExportSnapshot(ref NativeList<Collider> colliders,
                           ref NativeList<BodyPose> poses,
                           ref NativeList<CollisionFilter> filters);
```

**动机**：运行时烘焙需要读取当前帧全部 Collider 的形状、位姿与层。

**附带条件**：文档写明仅 `QueryReady` 后有效，且只读、不可缓存跨帧。

#### N3 · 精确 ray vs shape 查询 — P1，可降级

**现状**：`RaycastAabb` 自述「不执行精确 shape cast」；`CollisionQueryMath` 只有 `TryRayAabb`。

**需求形态**：

```csharp
public static bool Raycast(
    in Collider collider, in BodyPose pose,
    float3 origin, float3 direction, float maxDistance,
    out float distance, out float3 point, out float3 normal);
```

**动机**：动态障碍与视线检测。静态障碍导航走距离场，但**动态障碍不在距离场里**。

**可降级**：先用 AABB 版本跑通，精确版后续补。7 种形状的 ray 相交都是初等几何。

### 6.2 Ember.Core

#### E1 · 移动积分系统 — P1

**现状**：`LinearVelocity` / `AngularVelocity` 组件存在，`README.md` 写明「由移动系统积分到
`LocalTransform.Position`」，但 **`Ember.Core/src/Systems/` 中没有该系统**（只有 Culling /
Spatial / Presentation）。

**需求形态**：`MovementSystem` —— Job·Burst，读 `LinearVelocity` / `AngularVelocity` /
`WorldTime`，写 `LocalTransform`，带 `None<Static>` 与 `None<Disabled>`。

**动机**：ORCA 输出落到速度后需要积分。放在通用组件库比导航私有实现好 —— 子弹、特效、非导航
实体共用，职责干净（导航只负责写 `LinearVelocity`，不负责积分）。

### 6.3 Ember 框架

#### F1 · Buffer 批量写入 / 长度设置 — P0 但可降级

**现状**：公开 API 全集（`Ember/src/World/World.Buffer.cs`）只有 `CreateBuffer`、`DestroyBuffer`、
`GetBuffer`、`GetBufferLength`、`AddBufferElement`（单个）、`SetBufferElement`（校验
`index < Length`，未 `Add` 过即抛）、`RemoveBufferElementAtSwapBack`、`ClearBuffer`。

`BufferStore<T>` 是 `internal sealed class`，`BufferRange.Capacity` 是 `internal` 字段 ——
外部拿不到容量，即使拿到 `UnsafePtr` 也无从知道可写多长。没有 `Resize` / `SetLength` / 批量写入。

`CreateBuffer(capacity)` 后 `Length == 0`，`GetSpan().Length` 也是 0。唯一的填充途径是逐元素 `Add`。

**两个既有模块已在踩同一坑**：

```csharp
// Ember.Core/src/Spatial/Index/SpatialTreeView.cs:246
while (m_World.GetBufferLength<T>(handle) < length)
    m_World.AddBufferElement<T>(handle, default);

// Ember.Collision/src/World/CollisionWorldView.cs:184-192  Grow<T>
for (int i = current; i < target; i++)
    m_World.AddBufferElement<T>(handle, default);
```

**代价量化**：`Add` 每次 = `GetRange`（校验 + 数组访问）+ `EnsureRangeCapacity` + 写值 + 回写
`m_Ranges`，内联后约 5–10ns/次。

| 规模 | 元素数 | 逐元素 Add |
|---|---|---|
| 小世界 2D | 100 万 | 5–10 ms — 可接受 |
| 中世界 3D | 1000 万 | 50–100 ms — 卡顿可见 |
| 大世界 3D（512×128×512） | 3350 万 | 200–350 ms — 不可接受 |

叠加运行时热重烘焙（每次改动重灌），成本直接落在帧上。

**需求形态**：

```csharp
// Ember/src/Buffer/BufferStore.cs
public void Resize(BufferHandle handle, int length)
{
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    BufferRange range = GetRange(handle);
    EnsureRangeCapacity(ref range, length);
    if (length > range.Length)
    {
        int clearStart = range.Start + range.Length;
        for (int i = clearStart; i < range.Start + length; i++) m_Values[i] = default;
    }
    range.Length = length;
    m_Ranges[handle.RangeIndex] = range;
#if EMBER_ENABLE_SAFETY_CHECKS
    InvalidateSpans();   // 扩容会搬移地址，旧 span 必须失效
#endif
}

// Ember/src/World/World.Buffer.cs
public void ResizeBuffer<T>(BufferHandle handle, int length) where T : unmanaged
    => GetOrCreateBufferStore<T>().Resize(handle, length);
```

调用侧：

```csharp
world.ResizeBuffer<byte>(handle, distanceFieldSize);   // 一次
var span = world.GetBuffer<byte>(handle);              // Length 已正确
UnsafeUtility.MemCpy(span.UnsafePtr, blobPtr, bytes);  // 一次
```

**约束**：`Resize` 可能扩张，走「另分配 + 拷贝」，会搬移全部 range 地址 —— 必须在调度任何 Job
之前调用，与 `EnsureCapacity` 同一条规则。

**附带收益**：`SpatialTreeView.cs:246` 与 `CollisionWorldView.cs:184` 两处循环可各换成一次
`ResizeBuffer` + span 填充。

### 6.4 F1 的降级路径

若依赖方不提供 F1，导航可自管 `NativeArray<T>` 存导航数据，memcpy 灌入。代价：

- 违背模块既有资源模型 —— 碰撞模块与 `SpatialTree` 都选了「World 托管 buffer，无需 Dispose」，
  导航自管意味着要手写 Dispose、处理热重载释放、World 先销毁会泄漏
- `NavWorldView` 需要双套代码（buffer 版 / NativeArray 版）

**结论**：F1 缺失不会卡死导航，只是资源模型分叉。据此可排优先级。

**默认立场**：按 F1 提供设计（`NavWorldView` 只维护 buffer 一套代码）。降级路径仅在 F1 排期
明确落空时启用，且启用时应同步评估是否值得改为自管 —— 若最终仍需 F1，双套代码的返工成本高于
等待。P2 阶段（`NavWorld` 资源模型）之前必须定下，P2 之后切换代价显著上升。

### 6.5 明确不需要依赖方做的

防止过度设计，以下项目导航全部自建或不碰：

- 不改碰撞宽相语义（ORCA 邻居用自建均匀网格，不是 pair 集合）
- 不用碰撞的 pair / manifold 流水线
- 不公开 `NarrowphaseMath` 的流形逻辑
- 不要求 `SpatialTree` 并行化（导航不用它做热路径；gameplay 查询仍可用）
- 不要求 `ContactGraphColoring` 改造（已 public，直接吃）
- 不要求框架提供自调度 Job 便利 API（Ember.Collision 已验证可行，导航照抄）

### 6.6 优先级汇总

| 编号 | 依赖方 | 内容 | 优先级 |
|---|---|---|---|
| N1 | Ember.Collision | shape → point 最近点查询 | P0 阻断 |
| N2 | Ember.Collision | body 快照读取 | P0 阻断 |
| F1 | Ember | Buffer 批量写入 | P0 可降级 |
| N3 | Ember.Collision | 精确 ray vs shape | P1 可降级 |
| E1 | Ember.Core | 移动积分系统 | P1 |

---

## 7. 系统管线与注册顺序

导航系统横跨两个 ticker，原因是全局寻路与局部避障的时间尺度不同。

### 7.1 固定步长 ticker（`fixedIdx`）

```
NavSteeringSystem     Job·Burst   路径跟随 → 期望速度
NavAgentSystem        串行外壳 + 并行 Job   邻居查询 + ORCA → LinearVelocity
MovementSystem        Job·Burst   （Ember.Core，依赖项 E1）LinearVelocity → LocalTransform
```

ORCA 的时间视界预测要求稳定步长，因此整条避障链注册在固定步长上，封装为
`NavAvoidanceSystemGroup`。

### 7.2 可变步长 ticker（`updateIdx`）

```
NavRequestSystem      串行   收集寻路请求 + 双限预算调度
NavFlowFieldSystem    串行外壳 + 并行 Job   流场分帧推进
NavPathSystem         串行外壳 + 并行 Job   分层 A* + 漏斗平滑
```

封装为 `NavSystemGroup`。

### 7.3 接入方式

业务侧两行接入，两个组分别挂到不同 ticker：

```csharp
manager.GetTicker(updateIdx).Register<NavSystemGroup>();
manager.GetTicker(fixedIdx).Register<NavAvoidanceSystemGroup>();
```

`MovementSystem`（依赖项 E1）注册在 `NavAvoidanceSystemGroup` 之后 —— 同一 ticker 内注册顺序
决定依赖方向，避障先写 `LinearVelocity`，积分后读。若业务已有自己的积分系统，可不注册 E1 而
自行接管。

### 7.4 依赖的框架管线

- 碰撞体获得视锥剔除与空间索引：`CollisionBroadphaseSystem` 写回 `BoundingVolume` 后，
  `WorldBoundsSystem` / `SpatialIndexSystem` 自动接管（碰撞模块已完成此接线，导航零改动）
- 导航代理的表现层：走 `PresentationSystemGroup`，同样零改动

---

## 8. 测试策略

沿用 Ember.Collision 的分层：可执行纯逻辑测试在 CLI，Unity 宿主验收 Burst 与性能。

### 8.1 纯逻辑单测（CLI 可执行）

**对拍类断言**（最有价值的一类）：

| 被测 | 对拍对象 |
|---|---|
| 体素化 | 暴力逐点采样的占据集合 |
| 距离场 | 暴力逐格到全部 Collider 的最小距离 |
| 流场梯度 | 暴力 Dijkstra 的距离值 |
| 分层 A* 路径代价 | 单层 A* 的路径代价（应相等或更优） |
| 漏斗平滑 | 平滑后路径段与障碍无交（用距离场验证） |
| ORCA 约束构建 | 参考实现（RVO2 语义）逐约束对拍 |
| 2D 线性规划 | 暴力顶点枚举的最优解 |

**性质类断言**：

- 簇图连通性与体素连通性一致（不漏簇、不重边）
- 门户双向可达
- 距离场单调性：穿过障碍时值不连续增大
- ORCA 求解结果满足全部约束（半平面内）且最接近期望速度
- 确定性：同输入两次运行结果逐位一致

**每个形状对都必须有自己的解析解测试** —— 这条从碰撞模块继承：未经验证的几何代码会静默产生
错误距离/法线，在运行时只表现为「手感不对」，无法通过编译门或集成测试发现。

### 8.2 Unity 宿主验收（CLI 不可执行）

依赖 `NativeArray` 的端到端与性能验收，纯 CLI 下必须 `Assert.Ignore`（与 Ember 框架自身约定一致）：

- 帧预算：1k / 10k / 100k 代理的寻路 + ORCA 耗时
- **每帧 0 分配断言**
- Burst 编译产物有效性
- 实际并行度
- IL2CPP 表现
- 运行时烘焙的耗时与卡顿

### 8.3 测试基础设施

`NavBaker` 是纯函数，输入是 Collider 列表 + 标注层，输出是 blob —— 天然可测，且
**边界情形（退化体素、零尺寸形状、超大世界）全部在 CLI 覆盖**，不依赖 Unity。

---

## 9. 分期路线

| 阶段 | 内容 | 依赖 |
|---|---|---|
| P0 | 骨架、组件、配置、csproj、测试工程 | — |
| P1 | 烘焙核心：体素化 + 距离场 + 连通区域 + 簇图（纯算法 + CLI 测试） | N1 |
| P2 | `NavWorld` / `NavWorldView` 资源模型 + blob 加载 + 热替换 | F1（或降级） |
| P3 | 流场层：多源 Dijkstra + 梯度 + 缓存池 + 分帧 | P1、P2 |
| P4 | 分层 A* + 请求调度 + 路径跟随 + 漏斗平滑 | P3 |
| P5 | 均匀网格邻居查询 + 地面 ORCA（2D LP） | P4、E1 |
| P6 | 飞行 ORCA（3D LP） | P5 |
| P7 | Unity 编辑器：标注层编辑、烘焙窗口、Gizmo 可视化 | P1 |
| P8 | 运行时烘焙 API + 动态障碍局部失效 | P2、P5 |
| P9 | 优化：SIMD、增量、簇图压缩 | 全部 |

P7 的编辑器工具可以与 P3–P6 并行推进（依赖面只在 P1）。

---

## 10. 复用关系对照

### 10.1 复用（Ember / Ember.Core / Ember.Collision）

| 来源 | 复用内容 |
|---|---|
| Ember | ECS 存储与查询、`JobSystem<TJob>`、自调度 Job 模式、ECB、`SystemGroup`、单例组件、`BufferStore` / `BufferHandle`、`[assembly: EmberJobCompilation(Burst)]` |
| Ember.Collision | `Collider` / `ShapeType` / `ShapeParams`（形状定义）、`BodyPose`、`CollisionFilter`、`CollisionDimension`、`Aabb`、`RadixSort32`、`BlockScan`、`MortonCoder`、`BvhBuilder`、`BvhNode`、`ShapeBoundsMath`、`ContactGraphColoring`、`CollisionQueryMath.TryRayAabb`、`CollisionWorld` / `CollisionWorldView` 的资源模型范式、`CollisionSystemGroup` 的系统组范式、`CollisionSetupSystem` 的补组件范式 |
| Ember.Core | `LocalTransform` / `LocalToWorld`、`LinearVelocity` / `AngularVelocity`、`BoundingVolume` / `WorldBounds` / `WorldBoundsSystem`、`SpatialTree` / `SpatialTreeView`（gameplay 查询）、`SpatialSystemGroup`、`PresentationSystemGroup`、`Static` / `Disabled` / `Prefab` 标签、`WorldTime` / `Age` / `Lifetime`、`GlobalRandom`、`CameraFrustum` / `VisibilityState` / `InView` |

### 10.2 不复用（附理由）

| 项 | 理由 |
|---|---|
| 碰撞宽相 pair 流水线 | 语义是「相交 pair」，ORCA 要「每代理邻居列表」，模型不匹配 |
| 碰撞窄相 manifold | 导航不需要接触流形 |
| `PairQuery` / `AllPairsPartitioner` | `Action` 委托，热路径禁用 |
| `SpatialTree` 当热路径宽相 | 串行、延迟一帧、结果容器为 `NativeList`；保留用途为 gameplay 查询与测试 oracle |
| 多份按半径烘焙的导航网格 | 距离场方案一份数据支持任意半径 |

---

## 11. 未决问题

1. **体素大小、tile 尺寸与距离场量化位宽** —— 需在 Unity 宿主做内存/精度权衡实验后确定。
   初步：体素 0.25–0.5m，tile 32³，距离场 8 位量化（精度 1/255 × 体素尺寸，需验证是否满足
   贴墙法线质量；不足则升 16 位）。
2. **多导航数据实例** —— 是否需要同时加载多份 blob（如分层世界、室内外分离）。当前设计是单例
   `NavWorld`；若需要，改为句柄表。
3. **动态障碍进入 ORCA 的方式** —— 当前设计是把动态障碍当作零速度代理参与均匀网格。需要验证
   高速动态障碍（车辆）的推挤质量。
4. **分帧预算默认值** —— 数量上限与时间上限的默认值需实测后确定。
5. **簇图的内存占用** —— 超大世界的簇图规模需实测；可能需要在 P9 引入压缩。
6. **HybridCLR 热更新** —— 导航 Job 是否需要 `BurstHotUpdate` 策略与版本盐。当前按普通
   `Burst` 策略设计。
7. **导航数据版本兼容** —— blob 的 `Meta` 段带版本号，但迁移策略未定（重新烘焙 vs 兼容读取）。

---

## 12. 构建

```bash
DOTNET=/Users/norman/.dotnet/dotnet

$DOTNET build Ember.Navigation.csproj -c Release
$DOTNET test tests/Ember.Navigation.Tests/Ember.Navigation.Tests.csproj -c Release
```

MSBuild 可覆盖属性，与 Ember.Collision 一致：`EmberFrameworkDir` / `EmberRuntimeDll` /
`EmberCoreDll` / `EmberCollisionDll` / `EmberGeneratorDll`。
