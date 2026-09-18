# Changelog

All notable changes to Ember Navigation.

[English](CHANGELOG_EN.md)

## [0.2.9] — 新增导航调试窗口与场景可视化

### Added

- **`Ember/Navigation/调试窗口`：开关各图层，并显示体素 / 区域 / 请求统计。**

  统计面板给出请求状态直方图（无 / 待调度 / 搜索中 / 就绪 / 失败）与体素四态计数
  （占据 / 孤立 / 距离不足 / 可走）。这两组数字能直接回答「单位为什么不动」：
  大面积 `Failed` 通常意味着目标点落在墙里或请求发出时导航数据还没就绪，
  可走体素数骤降则说明可行走区域被切碎了。

  请求失败会被明确提示：`NavRequestStatus.Failed` 不会被任何系统重试。

- **场景视图可视化扩展为三层**（原先只有可行走体素）：

  | 图层 | 内容 |
  | --- | --- |
  | 体素网格 | 占据 / 距离不足 / 区域外孤立 / 可走 四态配色，可按连通区域着色 |
  | 代理 | 代理半径圆 + 当前速度 + ORCA 期望速度（两者背离即为正在避障） |
  | 路径 | 航点连线（从当前推进下标起画）+ 当前寻路目标点 |

### Changed

- 管理器解析改为 `NavBakeContext.Manager ?? ECSManager.Active`：业务侧不注册也能直接用。
- `NavGizmoDrawer` 的开关与预算迁移到 `NavDebugSettings`（EditorPrefs 持久化），
  窗口与场景绘制器共用同一份状态；网格绘制上限默认 4000 → 16000。

## [0.2.8] — 源码包形态下补两处 Editor 引用

### Fixed

- **`Ember.Navigation.Editor.asmdef` 补 `Ember.Collision.Runtime` 引用。**

  编辑器程序集里的 `NavBakeWindow` 与 `NavAnnotationVolumeEditor` 都 `using Ember.Collision`，
  而 asmdef 的 references 里没有它。DLL 时代编辑器程序集是预编译件、Collision 的符号烤在里面，
  所以从未暴露；改为「仓库根即包根」、由 Unity 编译源码之后，缺引用直接报 `CS0234`。

- **烘焙窗口跟随 Collision 1.0.0 的裸指针快照 API。**

  Collision 1.0.0 把公开快照从 `NativeArray` 改为裸指针（`ConvertExistingDataToNativeArray`
  造出的数组其 `m_Safety` 是 `default`，按值取出后索引会失败），`VertexPool` 随之拆成
  `VertexPoolPtr`(`long`) + `VertexCount`(`int`)。那轮改动同步了 `NavRuntimeBake`，漏了
  `NavBakeWindow`；源码包形态下编译报 `CS1061` 才暴露。

## [0.2.7] — 包仓库迁移到 ECS-Navigation.git

### Changed

- **包仓库由 `Ember-Navigation.git` 迁到 `ECS-Navigation.git`，仓库根即 UPM 包根。**

  源码库与包库合并成一个仓库：源码库转公开直接当 UPM 包，独立的 DLL/源码副本包库删除。
  从此一份代码一条历史，不再有「同步到另一个仓库」这一步，也没有副本漂移的可能。

  布局按 Unity 包约定整理：

  | 旧 | 新 | 说明 |
  | --- | --- | --- |
  | `src/` | `Runtime/` | 配 `Ember.Navigation.Runtime.asmdef` |
  | `libs/` | `Libs~/` | `~` 后缀让 Unity 忽略；否则 `Unity.Burst.dll` 会被当包内插件导入，与 `com.unity.burst` 撞名 |
  | `tests/` | `tests/`（加 asmdef） | `defineConstraints = UNITY_INCLUDE_TESTS`，否则测试代码会被编进包 |
  | 包库的 `package.json` / README / CHANGELOG / LICENSE | 仓库根 | — |

  包内每个资源都补了 `.meta`。Unity 对不可变包目录里没有 `.meta` 的资源**直接忽略**，
  只丢一条警告 —— 源码包资源上百个，漏一个就少一个文件。

  **消费方需改 manifest URL**：

  ```
  - https://github.com/NormanYUE/Ember-Navigation.git
  + https://github.com/NormanYUE/ECS-Navigation.git
  ```

  改完要删掉 `Library/PackageCache`，否则 UPM 不会重新解析。

- 依赖提升：com.ember.ecs 1.13.0、com.ember.core 2.1.4、com.ember.collision 1.0.4

## [0.2.6] — 改为源码包发布（不再发预编译 DLL）

### Changed

- **包内容由 `Runtime/*.dll` 改为 `Runtime/**/*.cs` 源码。**

  **为什么**：预编译 DLL 里的 `[BurstCompile]` 不会被 Unity 的 Burst ILPP 处理 ——
  那条流水线只跑 Unity 自己编译的程序集。实测 22 个 Job（Collision 19 / Navigation 1 /
  Core 2）在运行期报 `not a known Burst entry point`，退回托管路径。
  改成源码包后由 Unity 编译，这条流水线才成立。

  **附带解决**：
  - `#if ENABLE_UNITY_COLLECTIONS_CHECKS` 之类 Unity 侧符号在源码编译下**真正有定义**，
    不会再出现「包内那段其实是死代码」（`Ember.Collision` 曾因此把碰撞管线整个跑崩）
  - 不再需要在包库里维护 `.meta`（那是 0.3.1 / 0.3.2 两次事故的根源）
  - 交付的是可读、可调试、可步进的源码

  **消费方无需改动**：包库 URL 不变，公开 API 不变。

- `Runtime/Ember.navigation.Runtime.asmdef` 的 `precompiledReferences` 由指自己的 DLL
  改为 **`Ember.dll`**（框架仍以预编译形式提供）。`references` 显式列出 Unity 侧依赖
  （`Unity.Collections` / `Unity.Mathematics` / `Unity.Burst`）与上游 Ember 程序集。
- 补上 `com.unity.collections` 依赖声明 —— 之前是 DLL 包，编译期引用是 dotnet 侧的
  `libs/Unity.Collections.dll`，包本身不声明；改为源码包后必须声明。
- 依赖提升：com.ember.ecs 1.13.0、com.ember.core 2.1.3、com.ember.collision 1.0.3

### Notes

- 同步工具：`Ember.Framework/tools/deploy-package-sources.py`（源码库 → 包库，
  确定性生成 `.meta`）。发布流程见根目录 `CLAUDE.md`。
- 未验证：Unity 运行期。asmdef 的跨包预编译引用（`Ember.dll`）需要在 Unity 里确认能解析。

## [0.2.5] — 修复：5 个 buffer 从未设过长度

### Fixed

- **`FlowSlots`、`Distances`、`HeapCosts`、`HeapVoxels`、航点 buffer 原先都是「建了容量、
  从未设长度」。**

  `World.CreateBuffer<T>(initialCapacity)` 只设**容量**，逻辑长度是 **0**。这五处创建之后
  都没有 `ResizeBuffer`，消费方却是按数量取 `UnsafePtr` 再按下标索引 —— 底层内存按容量分配过，
  所以**一直没炸**，只是每一处都踩在「读不到就静默返回空 span」的边缘上。
  Collision 侧同类的 `DiagnosticFlags` 已经炸了（见 Collision 1.0.2），这五处是同类隐患。

  全部改用 `World.CreateSizedBuffer<T>(n)`（Ember 1.13.0 新增），创建即定长。

- **`NavWorldView.FlowSlots()` 补长度校验**：`GetBuffer` 回来的 span 长度小于
  `FlowSlotCount` 时返回 `null`，不再直接把空 span 的指针交出去。

### Changed

- 依赖提升至 `com.ember.ecs` 1.13.0、`com.ember.core` 2.1.2、`com.ember.collision` 1.0.2。

### Notes

- 未验证：Unity 运行期。CLI 侧编译与 89 项测试通过。

## [0.2.4] — 修复：ORCA 跨元素读需要 [NativeDisableParallelForRestriction]

### Fixed

- **`NavAgentJob` 不再抛 `IndexOutOfRangeException: Index N is out of restricted
  IJobParallelFor range`。**

  `IJobParallelFor` 默认把每个 `NativeArray` 字段限制在 `[index, index]`。本 Job 需要按
  邻居下标跨元素读，四个字段因此补上 `[NativeDisableParallelForRestriction]`：

  | 字段 | 访问点 |
  | --- | --- |
  | `NeighborIndices` | `NeighborIndices[neighborOffset + i]` |
  | `Positions` / `Velocities` / `Radii` | `Positions[other]` / `Velocities[other]` / `Radii[other]` |

  其余字段只按 `index` 访问（`Modes` / `MaxSpeeds` / `Preferred` / `NewVelocities` /
  `NeighborCounts`），或只经裸指针访问（`NeighborDists` / `NeighborDistances` /
  `Lines` / `Scratch` —— 裸指针不做范围检查），**都不需要**该 attribute。

  上一轮把方法开头的 `GetUnsafePtr` 改成 `GetUnsafeReadOnlyPtr` 之后，异常点从方法开头
  后移到了索引器这里，所以这一轮才暴露 —— 同一个方法里的下一个限制问题。

### Changed

- 依赖 `com.ember.collision` 由 1.0.0 提升至 **1.0.1**（该版本把容量不足时的静默空指针
  改为抛出并指名访问器）。

### Notes

- 未验证：Unity 运行期。CLI 侧已验证编译与全部 89 项测试。

## [0.2.3] — 修复：ORCA 只读数组的写指针；跟随 Collision 1.0.0

### Fixed

- **`NavAgentJob` 不再抛 `declared as [ReadOnly] in the job, but you are writing to it`。**

  该 Job 在六个 `[ReadOnly]` 字段上调了 `GetUnsafePtr()`
  （`CellStarts` / `CellCounts` / `SortedAgents` / `Positions` / `Radii` / `NeighborDists`）。
  `GetUnsafePtr` 内部走 `AtomicSafetyHandle.CheckWriteAndThrow`，而 Job 系统已把
  `[ReadOnly]` 字段的句柄置为只读，故必然抛。改用 `GetUnsafeReadOnlyPtr()` ——
  该 API 自 Collections 2.x 起即存在，在 2.4.3 与 6.6.0 两个实体里都已确认存在。

  这是纯写法问题，与 Unity / Collections 版本无关。

### Changed

- 依赖 `com.ember.collision` 由 0.3.2 提升至 **1.0.0**。该版本把 body 快照与两个查询入口
  从 `NativeArray` 改成裸指针（原因见 Collision 的 CHANGELOG：裸内存构造的 `NativeArray`
  句柄恒为 `default`）。本包的两处消费方同步改造：
  - `NavRuntimeBake.GatherColliders` 改吃裸指针
  - `NavDynamicObstacleSystem` 改吃裸指针，快照拷贝由 `NativeArray.Copy` 改为
    `UnsafeUtility.MemCpy`
- **本包自身的公开 API 未变**（`NavRuntimeBake.GatherColliders` 签名原样）。

### Notes

- 未验证：Unity 运行期。CLI 侧已验证编译与全部 89 项测试。

## [0.2.2] — 修复：系统构造早于 World 创建时抛未注册组件

### Fixed
- **按官方装配顺序注册 `NavSystemGroup` / `NavAvoidanceSystemGroup` 不再抛
  `unregistered component type`。**

  `SystemTicker.Register` 会**立即构造**系统，而它在官方示例里早于 `ECSManager.Start()`，
  也就早于 `World` 创建；但组件类型注册原本只发生在 `World` 构造函数里。系统的字段初始化器
  一旦构造 `EntityQuery`（`ComponentMask.With<T>()` 当场读 `ComponentTypeRegistry`），
  全新 AppDomain 的第一次运行就必然抛异常。

  受影响：`NavFlowFieldSystem`、`NavRequestSystem`、`NavSteeringSystem`、`NavAgentSystem`、
  `NavPathSystem`。五者的 query 字段去掉初始化器与 `readonly`，改在 `OnCreate()` 内构造 ——
  `OnCreate` 由 `SystemTicker.Init` 调用，晚于 `World` 构造；`BuildAccess` 紧随其后，
  `DeclareAccess` 不依赖这些字段。

### Changed
- 依赖 `com.ember.ecs` 提升至 1.12.0、`com.ember.core` 提升至 2.1.1、
  `com.ember.collision` 提升至 0.3.2 —— 三者都修了同一缺陷，UPM 按精确版本解析，不跟版本拿不到修复。

## [0.2.1] — 依赖指向 Ember.Collision 0.3.1

### Fixed

- 依赖 `com.ember.collision` 由 0.3.0 提升到 0.3.1。UPM 按**精确版本**解析依赖，
  0.3.0 包里缺 `.meta`，Unity 会忽略其中的 `Ember.Collision.dll`，
  于是 `Ember.Navigation.dll` 报 `Unable to resolve reference 'Ember.Collision'`、
  整个程序集不加载。

## [0.2.0] — 编辑器工具纳入包

### Added

- **`Editor/Ember.Navigation.Editor.dll`**：烘焙窗口、`NavAnnotationVolume` 场景编辑、
  导航网格可视化现在随包提供。0.1.0 只含运行时 DLL，装了也烘焙不出导航数据。

  Unity 对名为 `Editor` 的文件夹有特殊规则 —— 其下的 DLL 自动只进编辑器程序集，
  因此不需要 asmdef，与 `Ember.Package/Editor/` 同构。
  DLL 的 `platformData` 显式关掉 `Any`、只开 `Editor`，避免运行时加载到它。

### Notes

- 编辑器 DLL 针对 Unity 2022.3 的 `UnityEditor` 编译。所用 API
  （`EditorWindow` / `Handles` / `MenuItem` / `EditorPrefs` / `AssetDatabase` / `SerializedProperty`）
  在 2022 与 6 上均存在，故编译目标取较旧的那个。

## [0.1.0] — 首次发布：烘焙、分层寻路与双层 ORCA

### Added

**烘焙（离线与运行时共用一条管线）**
- `NavVoxelizer` 体素化：逐格取到全部碰撞体的最小有符号距离（基于 `ShapeQuery.ClosestPoint`），
  距离场精度直接决定可行走判定、贴墙法线与路径平滑安全距离。
- `NavRegionLabeler` 全图连通区域标记；`NavTileLocalLabeler` tile 内局部连通分量 ——
  **HPA\* 的节点粒度是「tile 内局部连通分量」，不是「tile × 全局区域」**：
  后者不保证 tile 内连通，簇内 A\* 会永远够不到另一侧的门户。
- `NavClusterGraphBuilder` 簇图与双向门户。
- `NavBaker` 两阶段烘焙（Plan 定尺寸 → Bake 填充）；`NavBlobWriter` / `NavBlobReader`
  8 字节对齐的段式 blob（魔数 `NAVB`，版本校验）。
- 距离场按等级量化存储（8 位 0–255 / 16 位 0–65535），满量程对应 `MaxBakeRadius`。

**寻路**
- `NavFlowFieldSolver` 多源 Dijkstra 流场（分帧推进，惰性删除堆，同代价按下标升序保证确定性）。
- `NavAStar` 簇内体素 A\*；`NavHpaPathfinder` 分层 A\*（簇间搜索 + 簇内连接）。
- `NavPathSmoother` 拉绳平滑：沿线段按半步长采样查距离场通视，替代逐段射线检测。
- `NavDistanceField` 采样与中心差分梯度 —— 静态障碍约束的几何来源，
  无需对障碍做形状查询或射线。

**避障**
- `NavOrcaMath` / `NavOrcaMath3D` 约束构建（RVO2 / RVO2-3D 语义）：
  二维走切锥外沿与两条切线，三维走圆锥切平面；静态障碍按全责、代理间各半责。
- `NavLinearProgram2D` 移植 RVO2 的 `linearProgram1/2/3`
  （增量半平面求交 + 不可行时的投影线回退，回退始终保留静态障碍约束）。
- `NavLinearProgram3D` 用精确顶点枚举：凸可行域边界由平面片、球面片及其交线构成，
  最优解必在枚举集内，结果确定且可证最优。
- `NavNeighborGrid` 均匀网格邻居查询（计数排序建表，复用 `BlockScan`）；
  扫描半径按实际查询半径换算，2D 模式在建表阶段钉住无效轴。

**系统**
- 固定步长（`NavAvoidanceSystemGroup`）：`NavSteeringSystem`（路径跟随 → 期望速度）、
  `NavAgentSystem`（邻居查询 + ORCA → `LinearVelocity`）。
- 可变步长（`NavSystemGroup`）：`NavDynamicObstacleSystem`、`NavRequestSystem`、
  `NavFlowFieldSystem`、`NavPathSystem`。
- 双限预算拆成两半：数量上限在请求调度，时间上限在寻路求解，二者先到为准。
- 流场缓存池按目标体素索引、LRU 淘汰、分帧推进；距离场代际变化时自动作废。

**运行时**
- `NavRuntimeBake` 从碰撞世界当前帧快照取静态碰撞体烘焙（不重扫场景）。
- `NavDynamicObstacleSystem` 动态障碍局部失效：只重算受影响体素块的距离场与占据位。
- `NavBakedAsset` / `NavBakedAssetLoader`：blob 存为 ScriptableObject，运行时加载。

**编辑器**（随包提供源码，需自行纳入 Unity 工程）
- 烘焙窗口（Play 模式从运行中的碰撞世界烘焙并保存产物）。
- `NavAnnotationVolume` 标注体：轴对齐 AABB 覆盖可行走判定或调整代价乘数，场景视图可拖拽。
- 导航网格可视化：按目标绘制量反推采样步长，避免百万级体素拖垮编辑器。

### Notes

- 依赖 `com.ember.ecs` 1.11.0、`com.ember.core` 2.1.0、`com.ember.collision` 0.3.0。
- **系统层与 Job 层未在 CLI 下验证** —— 依赖 Unity 运行时，需在 Unity Test Runner 中执行。
  算法内核（几何 / ORCA / 两个线性规划 / 网格 / 路径跟随 / 距离场）有纯逻辑测试覆盖。
- 距离场求解当前按 8 位等级解释；16 位需要先把求解器的等级宽度参数化。
- 标注体范围是轴对齐世界 AABB，旋转不参与判定；需要斜置区域时叠几个轴对齐块。
