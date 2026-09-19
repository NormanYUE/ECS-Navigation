# Changelog

All notable changes to Ember Navigation.

[English](CHANGELOG_EN.md)

## [0.2.21] — 搜索也留净空余量，路径不再贴着弯角切

### Fixed

- **直角弯的最短路贴着弯角切过去，代理在禁区边缘反复进出、抖动。**

  2.0.16 起平滑用「半径 × 1.6」的偏好人等级，但**搜索仍按半径**。于是有些情形下
  搜出来的最短路本身就在贴着弯角 —— 实测某直角弯：路径段最低净空 0.36 米，
  而代理半径 0.30 米，只剩 **6 厘米**余量。平滑拉不开它（拉绳只能省点、不能改路），
  于是 0.2.17 的两趟扫描退回半径等级，路径就这样贴了过去。

  贴到禁区边缘的后果是代理在「可走 / 不可走」两个体素之间来回跨 —— 表现就是
  卡在弯角反复进出、抖动。**它的方向与墙的贴墙推力还正好相反**，两者互相顶。

  修复：**搜索也先用偏好等级，搜不到再退回半径等级**（保留完备性）。
  有余量时走有余量的路，窄到只有半径能过时才将就。

  仍未覆盖：共用流场的等级写死为 0（非障碍物即可走），流场路径照旧贴墙；
  它一份场服务多种半径的代理，等级该取谁是个设计问题，未在本版处理。

## [0.2.20] — 路径跟随的前瞻点先投影到折线上

### Fixed

- **代理偏离路径时，前瞻点会切过弯角 —— 表现为顶着墙角原地磨、来回抖。**

  `NavPathFollower.LookAhead` 原先从**代理的实际位置**起步沿折线前进。但代理并不总在折线上：
  避障与分离力随时把它推开。于是「起点」是个偏离点，而第一个航点可能在十几米之外 ——
  从偏离点朝它直线前进，方向与路径无关，中间隔着一堵墙也照走不误。

  修复：**先把代理位置投影到折线上（取最近的那一段），再从投影点沿折线前进**。
  投影段取最近而非第一段：折线可能自交，取第一段会把投影点甩到代理身后。

  行为上有一处**刻意的语义变更**：「代理 → 折线起点」那一段不再计入前瞻里程。
  那段不是路径，算进去等于让代理朝一个偏离点直冲 —— 正是这个 bug 本身。
  既有用例 `LookAhead_CrossesWaypointBoundary` 的期望值已按新语义更正。

## [0.2.19] — 场景 Gizmos 防花屏

### Fixed

- **Scene 视图偶发整屏花屏（与 Collision 1.0.6 同一病根）。**

  导航 Gizmo 绘制器同样存在「非 Repaint 事件发 GL」与「坏坐标入批」两个污染源：
  代理位置 / 速度、寻路目标点、航点缓冲现在逐一做有限性检查，入口加 Repaint 门控。
  `DrawArrow` 原 `lengthsq < 1e-6` 守卫对 NaN 比较恒为 false，拦不住坏速度，一并修复。

## [0.2.18] — 二维 ORCA 的约束轴差了一个 90°

### Fixed

- **侧面的墙会把前进方向堵死，代理期望速度有值却纹丝不动（速度被解成零）。**

  `NavOrcaLine` 的半平面是 `dot(v, Normal) >= Offset`，而 `Normal = cross(planeNormal, direction)`
  —— 交给 `FromDirectionPoint` 的 `direction` 是**边界方向**，比「背离障碍的法线」多一个平面内 90°。
  RVO2 原版写的正是 `line.direction = (unitW.y, -unitW.x)`。

  二维求解器在「相对速度落在切锥外」与「已重叠」这两条分支里把 `unitW` 直接当 `direction` 传了，
  半平面因此转了 90°，卡到与障碍**垂直**的那个轴上。更糟的是这条分支下 `Offset` 恰好为 0
  （边界过原点），线性规划把期望速度投影上去的结果正好落回原点 —— 速度归零。

  症状极具迷惑性：距离场显示障碍在**侧面**（沿前进方向投影 0.00 米、侧向 2.24 米），
  几何上完全不该挡住前进，代理却原地钉死；把 `TimeHorizonObst` 置 0（不生成静态障碍约束）
  立即解冻 —— 因为问题从来不是约束**该不该**生成，而是它生成错了轴。

  三维版（`NavOrcaMath3D`）用的是 `normal = unitW`，本来就是对的，未受影响。

  同类问题在「已重叠」分支里也各有一份，一并修正。既有用例
  `CoincidentAgents_CollisionBranchEscapesAlongRelativeVelocity` 原先锁的是转错 90° 的那个轴，
  已按 RVO2 语义更正为分离轴。

## [0.2.17] — 平滑的净空是偏好，不再是门槛

### Fixed

- **A\* 明明搜到了路，请求却被判 `Failed` —— 0.2.16 引入的回归。**

  `PullString` 要求「线段上每个采样体素的净空 ≥ `requiredLevel`」才算通视，
  而 0.2.16 把平滑的 `requiredLevel` 从**半径**提到了**半径 × 1.6**，搜索仍按半径。
  两个等级一旦不同，只要 A\* 路径上有任何一个体素的净空落在两者之间
  （半径 0.3 时即 0.30~0.48 米，一条稍收窄的走廊就够），
  通视就到处不成立 → `furthest < 0` → 返回 -1 → 请求判 `Failed`。
  0.2.16 之前两个等级相同，路径体素**必然**通过，所以这条路走不通是新出现的问题。

  症状：队长卡在某处不动，换一个目标格仍然失败，重试多少遍都无效 ——
  而两端坐标在即时失败判定里（越界 / 命中占据 / 区域不同）**全部合法**，
  日志上看不出任何异常。

  修复：**平滑改成两趟扫描**。第一趟按偏好等级（半径 × 1.6）找最远可抄近道的点，
  够不着就退到第二趟按路径成立等级（= A\* 的等级）再找一次。
  于是偏好等级只影响「跳多远」，不再影响路径成不成立：
  走廊收窄时平滑退化成「少省几个点」，而不是把整条请求判失败。

## [0.2.16] — 拉绳平滑留出净空余量

### Fixed

- **路径贴到「刚好可走」的临界线上，代理在墙边前进/后退反复。**

  `PullString` 的净空判据原先是**恰好等于代理半径**的量化等级，于是平滑后的路径可以贴到
  距墙一个半径的位置 —— 那正是可走判定的边界。代理稍有偏差就出界，而距离场是量化的
  （8 位 / MaxBakeRadius 4 米 ≈ 0.016 米一格），梯度在相邻体素之间会翻向，
  ORCA 的静态障碍约束随之来回翻，表现为原地进退抖动、过直角弯时尤其明显。

  修复：**平滑用半径 × 1.6 的等级，搜索（A*）仍用半径本身**。搜索不收紧，
  否则本来就窄的通道会直接搜不到路；平滑收紧只是让路径离墙远一点，
  拉不动时保留更多航点而已。

## [0.2.15] — 平面代理的航点落在代理自己的运动平面上

### Fixed

- **地面代理拿到的航点写在网格的常数平面上，与代理所在平面不重合时路径跟随直接输出零期望速度。**

  `grid.VoxelToWorld` 给出的第三个分量是**烘焙时选定的那一层**（本工程网格原点 z = -4，
  即 z ≈ -3.75），与代理实际所在的平面（z = 0）差了好几米。
  `NavPathFollower` 以「代理在不在路径上」为前提，偏离时返回零速度；
  于是请求状态是 `Ready`、航点数也在，代理却一步不动。

  这个缺陷先前被另一个缺陷掩盖着：在 0.2.13 把地面代理的速度压回运动平面之前，
  代理会一路沉到网格那一层，恰好与航点同平面，路径跟随反而正常。

  修复：写航点时按运动平面法线把点投影到**过代理当前位置**的那个平面
  （`point − n·dot(point − origin, n)`）。三维烘焙不受影响（法线为零，`planar` 为假）。
  维度口径与 `NavDynamicObstacleSystem` 一致，从网格形状反推。

## [0.2.14] — 新增导航位置投影系统

### Added

- **`NavProjectionSystem`：把越出可行走区的代理拉回最近的可行走体素。**

  ORCA 是软约束 —— 它求「尽量不撞」的速度，不保证结果落在可行走区内。人挤人时编队
  外圈的代理会被持续挤进墙边的间隔不足带甚至墙里，之后一直卡住：起点不可走时
  `NavAStar.Begin` 的可走性校验直接让寻路失败，而失败不会被重试。

  实测一个 50 人编队推进到最后一段时：51 个代理里 45 可走、**5 个在间隔不足带、
  1 个在墙里**，全部集中在编队外圈（编队半宽 ±3.925 米 > 道路可用半宽 ±3.25 米）。

  逐环向外找最近可走体素（上限 4 环），位移最小，因此不会把代理甩到墙的另一侧；
  只改写网格铺开的那两个轴，第三个轴保持不变（否则会把代理挪到网格的常数平面上）。

  **注册位置**：必须在**积分之后**且同一 tick 内，包内不放进任何系统组 ——
  各家的积分系统不同，由消费方自行注册：
  `<c>ticker.Register&lt;NavProjectionSystem&gt;();</c>`

## [0.2.13] — 地面代理的速度压回运动平面

### Fixed

- **地面代理会一路沉到导航烘焙平面，z 与游戏平面错开数米。**

  期望速度来自路径跟随 `NavPathFollower`，而它的航点是 `NavGrid.VoxelToWorld` 的产物、
  带**烘焙平面**的 z。`NavLinearProgram2D` 在无约束时直接返回期望速度
  （平面法线只用于构造约束线），于是那个指向烘焙平面的分量原样进了积分。

  实测一个 2D 关卡（网格原点 z = -4）：51 个代理里 **30 个的 z 停在 -3.75**、20 个在
  漂移途中，只有 1 个还在 0。后果不只是位置漂移 —— 代理的碰撞体、调试可视化
  全部跟着错开，看起来像「和道路对不上」。

  修复：`NavAgentJob` 求解后按运动平面压平结果（`NavPlane.Flatten`）。
  飞行代理与三维烘焙不受影响（平面法线为零，`planar` 为假）。

## [0.2.12] — 2D 网格按设定平面绘制

### Fixed

- **导航可视化画在烘焙平面而不是游戏平面上，透视视角下整体偏移。**

  本工程网格原点 z = -4（烘焙平面 z ≈ -3.75），而道路摆在 z = 0；
  透视场景视图里两者会错开一大截，看上去「形状像路、位置却偏移很多」。

  2D 网格（`Grid.Dimensions.z == 1`）的网格、代理、航点、目标点现在统一画到
  新增的 `DrawPlaneZ`（默认 0）上；调试窗口里可调，统计面板同时显示烘焙平面 Z
  以便发现两者不一致。

## [0.2.11] — HPA* 失败后回退全图 A*

### Fixed

- **HPA* 无解而全图有解时，请求被判 `Failed`（且不再重试），单位走到某格后集体站死。**

  簇图与门户是烘焙期按**无半径边界**的可走性建出来的，而请求搜索按
  `Context.RequiredLevel`（由 `NavAgent.Radius` 换算）过滤体素。于是存在这种局面：
  全图 A* 有解 —— 连通与净空都够 —— 但 HPA* 必须穿过某个净空不足的门户，
  簇内搜索穷尽后返回 `SegmentSearchFailed`。

  实测：一个 50 人编队推进到第 10 格时 50 条请求全部 `Failed`；重新置 `Pending` 后
  11 条成功、**39 条仍然失败**，而按同一净空口径做的并查集证明这些起终点之间
  存在净空 0.753 米的通路（要求 0.5）。

  HPA* 的定位是加速，因此失败时回退一次全图 A*（`FindPathExhaustive`，
  `RestrictNode = -1`）把完备性补回来 —— 比把代理半径耦合进烘焙期的门户数据结构
  更小、也不易出错。只有失败请求才会付这一次全图搜索。新增 `PathStatus.FallbackSuccess`
  用于区分「HPA* 直达」与「回退成功」。

## [0.2.10] — 网格改为可走/不可走二元配色

### Changed

- **体素网格从「四态线框立方体」改成「绿色空心 = 可走 / 红色实心 = 不可走」。**

  原先四态（占据 / 距离不足 / 孤立 / 可走）都是低透明度线框，糊成一片，看不出哪里能走。
  现在判据是二元的 `IsWalkable`（与寻路同一个函数），可走只描绿色边、不可走填红：
  通行走廊的边界一眼可辨，墙外那圈「距离不足」也被算作不可走，画的正是代理实际走不了的地方。

  逐格画的是平面方格（`DrawSolidRectangleWithOutline`）而不是立方体 ——
  2D 网格只有一层体素，有线框才会显示成面。四点坐标走复用缓冲，不逐格分配。

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
