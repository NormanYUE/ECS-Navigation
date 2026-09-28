# Ember 框架需求：Buffer 批量写入 / 长度设置

> 来源：Ember.Navigation 模块 ｜ 优先级：P0，**但可降级**

## 背景

Ember.Navigation 是规划中的 2D/3D 导航模块，提供海量实体寻路、ORCA 单位间避碰与自动避障，
支持编辑器内烘焙与运行时烘焙。

导航采用与 `CollisionWorld` / `SpatialTree` **完全相同的资源模型** —— 全部运行时数据存
World 托管的 buffer（`BufferHandle` + `BufferSpan`），单例组件持句柄，无 Dispose，随
`World.Dispose` 自动释放。

该模型要求把几十 MB 量级的烘焙数据一次性灌入 buffer。当前的 Buffer API 不支持这一点。

## 需求总表

| 编号 | 内容 | 优先级 |
|---|---|---|
| [F1](#f1--buffer-批量写入--长度设置) | Buffer 批量写入 / 长度设置 | P0 可降级 |

---

## F1 · Buffer 批量写入 / 长度设置

**优先级：P0，但可降级**（降级路径见文末）

### 现状

`Ember/src/World/World.Buffer.cs` 的公开 API 全集：

```csharp
CreateBuffer<T>(initialCapacity)        // 分配 capacity，但 Length = 0
DestroyBuffer<T>
GetBuffer<T>                            // 返回 BufferSpan<T>
GetBufferLength<T>
ClearBuffer<T>
AddBufferElement<T>(handle, value)      // 单个元素
SetBufferElement<T>(handle, index, …)   // 校验 index < Length，未 Add 过即抛异常
RemoveBufferElementAtSwapBack<T>
```

`BufferStore<T>` 是 `internal sealed class`；`BufferRange.Capacity` 是 `internal` 字段：

```csharp
internal struct BufferRange
{
    public int Start;
    public int Length;
    public int Capacity;     // 外部不可见
    public int Version;
    public byte Allocated;
}
```

由此产生两个后果：

1. **外部拿不到容量** —— 即使通过 `BufferSpan<T>.UnsafePtr` 拿到了裸指针，也无从得知可写多长
2. **`CreateBuffer(capacity)` 后 `Length == 0`**，`GetSpan().Length` 同样是 0 —— 填充 buffer 的
   唯一途径是逐元素 `Add`

### 上游两个模块已在踩同一坑

```csharp
// Ember.Core/src/Spatial/Index/SpatialTreeView.cs:246
while (m_World.GetBufferLength<T>(handle) < length)
    m_World.AddBufferElement<T>(handle, default);
```

```csharp
// Ember.Collision/src/World/CollisionWorldView.cs:184-192   Grow<T>
for (int i = current; i < target; i++)
    m_World.AddBufferElement<T>(handle, default);
```

两处都是「逐元素循环 Add 默认值」来把 buffer 撑到目标长度。

### 代价量化

`Add` 每次的开销 = `GetRange`（校验 + 数组访问）+ `EnsureRangeCapacity` + 写值 +
回写 `m_Ranges`。内联后约 5–10 ns/次。

| 规模 | 元素数 | 逐元素 Add 总耗时 |
|---|---|---|
| 小世界 2D | 100 万 | 5–10 ms — 可接受 |
| 中世界 3D | 1000 万 | 50–100 ms — 卡顿可见 |
| 大世界 3D（512×128×512 体素） | 3350 万 | 200–350 ms — 不可接受 |

导航的体素距离场在 3D 大世界下正落在最后一档。若叠加**运行时热重烘焙**（玩家建造、地形破坏后
重灌导航数据），成本直接落在帧上。

### 需求形态

```csharp
// Ember/src/Buffer/BufferStore.cs
public void Resize(BufferHandle handle, int length)
{
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    BufferRange range = GetRange(handle);
    EnsureRangeCapacity(ref range, length);
    if (length > range.Length)
    {
        // 新增区清零，避免把未初始化内存交给调用方
        int clearStart = range.Start + range.Length;
        for (int i = clearStart; i < range.Start + length; i++) m_Values[i] = default;
    }
    range.Length = length;
    m_Ranges[handle.RangeIndex] = range;
#if EMBER_ENABLE_SAFETY_CHECKS
    InvalidateSpans();   // 扩容会搬移地址，旧 span 必须失效
#endif
}
```

```csharp
// Ember/src/World/World.Buffer.cs
public void ResizeBuffer<T>(BufferHandle handle, int length) where T : unmanaged
    => GetOrCreateBufferStore<T>().Resize(handle, length);
```

调用侧变为两步：

```csharp
world.ResizeBuffer<byte>(handle, distanceFieldSize);   // 一次调用，定长度
var span = world.GetBuffer<byte>(handle);              // Length 已正确
UnsafeUtility.MemCpy(span.UnsafePtr, blobPtr, bytes);  // 一次拷贝
```

### 关键约束（须写进 API 文档）

`Resize` 可能扩张，而 `BufferStore` 的扩容走「另分配 + 拷贝」，会搬移**全部** range 的地址。
因此 **`Resize` 必须在调度任何 Job 之前调用** —— 这与 `CollisionWorldView.EnsureCapacity` 是
同一条既有规则。

### 附带收益

`SpatialTreeView.cs:246` 与 `CollisionWorldView.cs:184` 两处循环可各换成一次 `ResizeBuffer` +
span 填充，省掉逐元素调用开销。碰撞模块的扩容路径尤其受益 —— 它的 `Grow<T>` 是从 `current`
到 `target` 全量循环。

### 验收标准

- `Resize` 的增大、缩小、等长三种情形行为正确
- 扩容后旧 `BufferSpan` 失效（`EMBER_ENABLE_SAFETY_CHECKS` 下抛异常，与既有 span 失效语义一致）
- 新增区已清零
- 与 `Add` 混用的语义明确（`Resize` 之后 `Add` 应从新 `Length` 继续追加）
- 不需要 `Resize` 的既有调用路径行为不变
- 不引入每帧分配

---

## 降级路径（若决定不实现本条）

导航可以绕开 F1：自管 `NativeArray<T>` 存导航数据，memcpy 灌入，自己负责生命周期。

代价：

1. **资源模型分叉** —— 碰撞模块与 `SpatialTree` 都选择了「World 托管 buffer，无需 Dispose」，
   导航自管意味着要手写 `Dispose`、处理热重载释放、World 先销毁会泄漏
2. **双套代码** —— `NavWorldView` 需要同时维护 buffer 版与 `NativeArray` 版，或彻底放弃 buffer 版

**结论**：F1 缺失不会让导航卡死，只是资源模型与上游不一致。据此可排优先级。

**决策时点**：导航的 P2 阶段（`NavWorld` 资源模型定型）之前必须定下。P2 之后从一套模型切到
另一套的返工成本显著上升。

---

## 明确不需要本模块做的

- **不需要改动现有 Buffer API 的语义** —— `Add` / `Set` / `Remove` 等保持现状，`Resize` 是纯增量
- **不需要提供流式 / 分块写入 API** —— 导航的加载是一次性 memcpy，不需要流式
- **不需要为 Buffer 提供序列化能力** —— 导航的 blob 序列化在导航模块内完成

## 与其它模块需求的关系

导航模块共提出五项需求，分属三个上游模块：

| 模块 | 需求 | 文档 |
|---|---|---|
| Ember.Collision | N1 shape→point 查询、N2 body 快照、N3 精确 raycast | `ember-collision.md` |
| Ember.Core | E1 移动积分系统 | `ember-core.md` |
| Ember | F1 Buffer 批量写入 | 本文档 |

**建议实现时机**：F1 是导航 P2（`NavWorld` 资源模型 + blob 加载）的前置条件。
若 F1 与 N1 / N2 同期排入，导航的烘焙与加载两条前置链可同时解锁。
