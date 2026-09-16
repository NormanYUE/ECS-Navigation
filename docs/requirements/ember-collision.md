# Ember.Collision 需求：几何查询能力补全

> 来源：Ember.Navigation 模块 ｜ 优先级：N1、N2 为 P0 阻断，N3 为 P1 可降级

## 背景

Ember.Navigation 是规划中的 2D/3D 导航模块，提供海量实体寻路、ORCA 单位间避碰与自动避障。
其架构与 Ember.Collision 采取**分层解耦**：

- **Ember.Collision 提供几何事实** —— 形状定义、精确几何查询
- **Ember.Navigation 独占导航语义** —— 体素数据、寻路、ORCA、转向、请求调度、标注层、编辑器工具

导航**不使用**碰撞模块的 pair / manifold 流水线（那是碰撞检测语义，与导航无共享算法），
但已确定复用以下**已公开**的内核，无需任何改动：

`Collider` / `ShapeType` / `ShapeParams` / `BodyPose` / `CollisionFilter` / `CollisionDimension` /
`Aabb` / `RadixSort32` / `BlockScan` / `MortonCoder` / `BvhBuilder` / `BvhNode` /
`ShapeBoundsMath` / `ContactGraphColoring` / `CollisionQueryMath.TryRayAabb`，
以及 `CollisionWorld` / `CollisionWorldView` 的资源模型范式与系统组范式。

本文档只列出**需要碰撞模块新增或公开**的三项能力。

## 需求总表

| 编号 | 内容 | 优先级 |
|---|---|---|
| [N1](#n1--公开-shape--point-最近点--距离查询) | shape → point 最近点 / 距离查询 | P0 阻断 |
| [N2](#n2--公开-body-快照读取) | body 快照读取 | P0 阻断 |
| [N3](#n3--精确-ray-vs-shape-查询) | 精确 ray vs shape 查询 | P1 可降级 |

---

## N1 · 公开 shape → point 最近点 / 距离查询

**优先级：P0 阻断** —— 不提供则导航烘焙核心无法实现

### 现状

`src/Geometry/NarrowphaseMath.cs` 内部已有完整的最近点计算族，但**全部是 `private`**：

| 函数 | 行号 | 维度 |
|---|---|---|
| `ClosestSegmentAabb2D` | 746 | 2D |
| `TrySegmentAabb2D` | 792 | 2D |
| `PointAabbDistanceSq2D` | 807 | 2D |
| `ClosestPointOnSegment2D` | 810 | 2D |
| `ClosestPointsOnSegments2D` | 819 | 2D |
| `ClosestPointOnSegment` | 1262 | 3D |
| `ClosestPointsOnSegments` | 1271 | 3D |
| `ClosestSegmentAabb` | 1332 | 3D |
| `TrySegmentAabb` | 1378 | 3D |
| `PointAabbDistanceSq` | 1414 | 3D |

且没有「形状 → 点」的顶层入口。当前公开的几何 API 只有 `ShapeBoundsMath`（产 AABB）与
`CollisionQueryMath.TryRayAabb`（ray vs AABB），都无法满足需求。

### 需求形态

```csharp
public static class ShapeQuery
{
    /// <summary>
    /// 计算点到碰撞体表面的最近点。返回值：
    ///   点位于形状外部时为正距离；
    ///   点位于形状内部时为负距离（-穿透深度）；
    ///   点在表面上时为 0。
    /// normal 为从最近点指向查询点的单位向量；点在内部时指向最近的外表面方向。
    /// </summary>
    public static float ClosestPoint(
        in Collider collider,
        in BodyPose pose,
        float3 point,
        out float3 closest,
        out float3 normal);
}
```

要求：

- 覆盖全部 7 种 `ShapeType`：`Sphere` / `Box` / `Capsule` / `Circle` / `Box2D` / `Capsule2D` / `Polygon2D`
- Burst 兼容的纯静态函数，无托管分配
- 尊重 `CollisionDimension`（2D 时忽略无效轴）
- 2D 形状的 `closest` 与 `normal` 需按维度语义抬升到 3D 表示（可沿用 `NarrowphaseMath` 现有的
  `LiftPoint` / `LiftDirection` 模式）

### 动机

导航烘焙要把场景几何转换成**体素距离场**：对每个 Collider，遍历其世界 AABB 覆盖的全部体素，
逐格计算到该形状的最近点距离并取 min。

距离场的精度**直接决定导航质量** —— 以下三项全部由它派生：

| 派生用途 | 说明 |
|---|---|
| 可行走判定 | 代理半径 r 可通行的条件为 `distanceField[v] >= r`，一份数据支持任意半径 |
| ORCA 贴墙约束 | 需要「到墙距离 + 墙法线」构造避障半平面 |
| 路径平滑 | 拉绳时查安全距离，替代逐段射线检测 |

用 AABB 近似不可接受：精度损失会直接表现为代理卡在墙里或穿不过门口。

### 工作量

提炼已有 `private` 函数 + 补形状外壳。**非新写几何算法**。

### 验收标准

- 每种形状各一组解析解对拍测试，覆盖查询点位于形状**外部 / 内部 / 表面上**三种情形
- 2D 形状在 `CollisionDimension.XY` 与 `CollisionDimension.XZ` 下语义一致
- 退化输入行为明确：零半径球、零半范围盒、退化胶囊（halfHeight = 0）、顶点数 < 3 的凸多边形
- Burst 编译通过

---

## N2 · 公开 body 快照读取

**优先级：P0 阻断** —— 不提供则导航的运行时烘焙无法实现

### 现状

`CollisionWorld` 的稠密 body 数组全部是 `internal`：

```csharp
internal BufferHandle BodyEntities;   // Entity
internal BufferHandle BodyBounds;     // Aabb
internal BufferHandle BodyPoses;      // BodyPose
internal BufferHandle BodyColliders;  // Collider
internal BufferHandle BodyFilters;    // CollisionFilter
internal BufferHandle BodyFlags;      // byte
```

`CollisionWorldView` 上对应的 `BodyEntityArray` / `BodyPoseArray` / `BodyColliderArray` 等访问器
同样是 `internal`。外部模块当前**无法读取任何 body 数据** —— 公开查询只有 `OverlapAabb` 与
`RaycastAabb` 两个 AABB 级接口。

### 需求形态（二选一）

**方案 A · 公开只读访问器**

```csharp
public readonly int BodyCount { get; }
public readonly NativeArray<BodyPose> BodyPoses { get; }
public readonly NativeArray<Collider> BodyColliders { get; }
public readonly NativeArray<CollisionFilter> BodyFilters { get; }
```

**方案 B · 批量导出**

```csharp
public bool ExportSnapshot(
    ref NativeList<Collider> colliders,
    ref NativeList<BodyPose> poses,
    ref NativeList<CollisionFilter> filters);
```

方案 B 对调用方更友好（一次拷贝，不暴露内部存储）；方案 A 实现更简单。二者皆可，由实现方选择。

### 动机

导航支持**运行时烘焙**：在游戏运行中把动态生成的场景几何（程序化关卡、破坏后的地形、
玩家建造物）烘焙成导航数据。

该烘焙必须与编辑器烘焙走**同一个** `NavBaker` 核心，只是输入来源不同 —— 编辑器从场景
收集 Collider，运行时从碰撞世界读取当前帧的 body 快照。若快照不可读，运行时烘焙将被迫
重写一套几何采集路径，造成两条代码路径与两份测试。

### 附带条件

- 文档需写明：数据**仅在该帧宽相完整发布后（`QueryReady`）有效**
- 明确只读语义，调用方不得缓存跨帧
- 若采用方案 A，需说明返回的 `NativeArray` 的生命周期与失效条件

### 验收标准

- 快照内容与 `CollisionWorld` 内部状态一致（逐项对拍）
- `QueryReady` 为 false 时的行为明确（返回空 / 返回 false / 抛异常，择一并在文档写明）
- 不引入额外的每帧分配

---

## N3 · 精确 ray vs shape 查询

**优先级：P1 可降级** —— 可先用 AABB 近似跑通，精确版后续补

### 现状

`CollisionWorldView.RaycastAabb` 的文档明确写着「不执行精确 shape cast」—— 它走的是 body 的
AABB。`CollisionQueryMath` 里也只有 `TryRayAabb`（ray vs AABB）。

### 需求形态

```csharp
public static class ShapeQuery
{
    /// <summary>射线与单个形状的最近相交。direction 需归一化或由实现归一化。</summary>
    public static bool Raycast(
        in Collider collider,
        in BodyPose pose,
        float3 origin,
        float3 direction,
        float maxDistance,
        out float distance,
        out float3 point,
        out float3 normal);
}
```

### 动机

静态障碍的避让由导航的距离场承担，但**动态障碍不在距离场里** —— 移动的车辆、被推开的箱子、
临时出现的阻挡物都需要实时几何查询来检测视线遮挡与投掷路径。

### 可降级

先用现有 AABB 版本跑通，动态障碍按保守 AABB 处理。精确版后续补上即可 ——
7 种形状的 ray 相交都是初等几何（ray-sphere / ray-box / ray-capsule / ray-convex2D）。

### 验收标准

- 每种形状的解析解对拍测试
- 边界情形：起点位于形状内部、射线与形状相切、`direction` 退化为零向量、`maxDistance` 为 0
- 与 N1 同处 `ShapeQuery` 类，风格一致（Burst 兼容、无托管分配）

---

## 明确不需要本模块做的

为防止过度设计，以下项目导航全部自建或不碰：

- **不改宽相语义** —— ORCA 的邻居查询用导航自建的均匀网格哈希，不是全局 pair 集合；
  碰撞宽相的「相交 pair」语义与「每代理邻居列表」模型不匹配
- **不用 pair / manifold 流水线** —— 导航不需要接触流形
- **不公开 `NarrowphaseMath` 的流形逻辑** —— 只公开 N1 要求的最近点查询，`TryBuildManifold`
  系列保持现状
- **不要求 `ContactGraphColoring` 改造** —— 已 `public`，导航直接复用

## 与其它模块需求的关系

导航模块共提出五项需求，分属三个上游模块：

| 模块 | 需求 | 文档 |
|---|---|---|
| Ember.Collision | N1、N2、N3 | 本文档 |
| Ember.Core | E1 移动积分系统 | `ember-core.md` |
| Ember | F1 Buffer 批量写入 | `ember-framework.md` |

**建议实现顺序**：N1 + N2 优先 —— 它们是导航 P1（烘焙核心）与 P2（运行时数据加载）的前置条件。
N3 在导航 P8（运行时烘焙 + 动态障碍）之前补上即可。
