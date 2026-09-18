# Changelog

All notable changes to Ember Navigation.

[中文](CHANGELOG.md)

## [0.2.12] — Draw 2D grids on a configurable plane

### Fixed

- **The navigation visualization drew on the bake plane rather than the gameplay plane, so it
  appeared offset in perspective views.**

  Here the grid origin is z = -4 (bake plane z ≈ -3.75) while the road sits at z = 0; in a
  perspective scene view the two separate by a wide margin, reading as "same shape as the road,
  but shifted far away".

  For 2D grids (`Grid.Dimensions.z == 1`) the mesh, agents, waypoints and targets are now all drawn
  on a new `DrawPlaneZ` setting (default 0). The debug window exposes it and also reports the bake
  plane's Z so a mismatch is visible.

## [0.2.11] — Fall back to a full-grid A* when HPA* fails

### Fixed

- **When HPA* had no solution but a full-grid search did, the request was marked `Failed`
  (never retried) and agents froze en masse at some cell.**

  The cluster graph and its portals are built at bake time from a **radius-free** walkability test,
  while request searches filter voxels by `Context.RequiredLevel` (derived from `NavAgent.Radius`).
  That leaves a gap: a full-grid A* has a solution — connectivity and clearance are both
  sufficient — but HPA* must cross a portal whose clearance is too tight, so the in-cluster search
  exhausts and returns `SegmentSearchFailed`.

  Measured: a 50-unit formation advancing to cell 10 had all 50 requests `Failed`; after re-arming
  them to `Pending`, 11 succeeded and **39 failed again** — while a union-find over the same
  clearance rule proved a route with 0.753 m of clearance exists between those endpoints
  (0.5 required).

  HPA* exists to be fast, so on failure the search now falls back to a full-grid A*
  (`FindPathExhaustive`, `RestrictNode = -1`), restoring completeness. That is smaller and less
  error-prone than coupling the agent radius into the bake-time portal structures. Only failed
  requests pay for the extra search. `PathStatus.FallbackSuccess` distinguishes a fallback hit from
  a direct HPA* hit.

## [0.2.10] — Binary walkable/blocked coloring for the voxel grid

### Changed

- **The voxel grid switched from four-state wireframe cubes to "green outline = walkable /
  red solid = not walkable".**

  All four states (occupied / insufficient clearance / isolated / walkable) used to be low-alpha
  wireframes and blurred into one mass with no readable boundary. The test is now the binary
  `IsWalkable` (the same function pathfinding uses): walkable cells get a green outline, everything
  else is filled red — so the clearance ring around walls, which agents genuinely cannot enter,
  reads as blocked too.

  Cells are drawn as flat rectangles (`DrawSolidRectangleWithOutline`) rather than cubes, because a
  2D grid has a single voxel layer and only a face shows. The four corners come from a reused
  buffer, not a per-cell allocation.

## [0.2.9] — Navigation debug window and scene visualization

### Added

- **`Ember/Navigation/调试窗口` (Debug Window): per-layer toggles plus voxel / region / request stats.**

  The stats panel reports a request-status histogram (none / pending / in-progress / ready / failed)
  and four-way voxel counts (occupied / isolated / insufficient clearance / walkable). Those two
  numbers answer "why is this agent not moving": a large `Failed` count usually means the target
  landed inside a wall or the request was issued before navigation data was ready, while a
  collapsed walkable count means the navigable area has been fragmented.

  Failures are called out explicitly: `NavRequestStatus.Failed` is never retried by any system.

- **Scene view visualization extended to three layers** (previously walkable voxels only):

  | Layer | Content |
  | --- | --- |
  | Voxel grid | occupied / insufficient clearance / isolated / walkable, optionally colored by region |
  | Agents | agent radius disc + current velocity + ORCA desired velocity (divergence means avoidance) |
  | Paths | waypoint polyline (from the current index) + the current pathfinding target |

### Changed

- Manager resolution is now `NavBakeContext.Manager ?? ECSManager.Active`, so consumers need no
  registration line.
- `NavGizmoDrawer` toggles and budgets moved to `NavDebugSettings` (persisted in EditorPrefs),
  shared by the window and the scene drawer; default mesh budget raised from 4000 to 16000.

## [0.2.8] — Two Editor-side reference fixes for the source package

### Fixed

- **`Ember.Navigation.Editor.asmdef` now references `Ember.Collision.Runtime`.**

  Both `NavBakeWindow` and `NavAnnotationVolumeEditor` in the editor assembly use `Ember.Collision`,
  but the asmdef's references list did not include it. In the DLL era the editor assembly was a
  precompiled binary with Collision's symbols baked in, so this never surfaced; once the repository
  root became the UPM package root and Unity compiled the sources, the missing reference became a
  hard `CS0234`.

- **The bake window now follows Collision 1.0.0's raw-pointer snapshot API.**

  Collision 1.0.0 changed public snapshots from `NativeArray` to raw pointers (an array built by
  `ConvertExistingDataToNativeArray` has a `default` `m_Safety`, so indexing one taken by value
  fails), splitting `VertexPool` into `VertexPoolPtr` (`long`) and `VertexCount` (`int`). That
  change updated `NavRuntimeBake` but missed `NavBakeWindow`; the source-package layout surfaced it
  as `CS1061` at compile time.

## [0.2.7] — Package repository moved to ECS-Navigation.git

### Changed

- **The package repository moved from `Ember-Navigation.git` to `ECS-Navigation.git`, and the repository
  root is now the UPM package root.**

  The source repository and the package repository merged into one: the source repository went
  public and is now itself the UPM package, while the separate package repository was deleted.
  One codebase, one history — no sync step, no chance of the copy drifting.

  The layout follows Unity's package conventions:

  | Before | After | Why |
  | --- | --- | --- |
  | `src/` | `Runtime/` | pairs with `Ember.Navigation.Runtime.asmdef` |
  | `libs/` | `Libs~/` | the `~` suffix makes Unity ignore it; otherwise `Unity.Burst.dll` is imported as a package plugin and collides with `com.unity.burst` |
  | `tests/` | `tests/` (now with an asmdef) | `defineConstraints = UNITY_INCLUDE_TESTS`, otherwise the tests get compiled into the package |
  | the package repo's `package.json` / README / CHANGELOG / LICENSE | repository root | — |

  Every asset now has a `.meta`. Unity **silently ignores** assets without one inside an immutable
  package folder, emitting only a console warning — a source package has hundreds of assets, and
  each missing `.meta` is a missing file.

  **Consumers must update their manifest URL**:

  ```
  - https://github.com/NormanYUE/Ember-Navigation.git
  + https://github.com/NormanYUE/ECS-Navigation.git
  ```

  Delete `Library/PackageCache` afterwards, or UPM will not re-resolve.

- Dependencies raised: com.ember.ecs 1.13.0, com.ember.core 2.1.4, com.ember.collision 1.0.4

## [0.2.6] — Ships as a source package (no more precompiled DLL)

### Changed

- **The package now carries `Runtime/**/*.cs` instead of `Runtime/*.dll`.**

  **Why**: `[BurstCompile]` inside a precompiled DLL is never processed by Unity's Burst ILPP —
  that pipeline only runs over assemblies Unity itself compiles. In practice 22 jobs (Collision 19,
  Navigation 1, Core 2) reported `not a known Burst entry point` at runtime and fell back to the
  managed path. As a source package Unity compiles them, and the pipeline applies.

  **Also fixed by this**:
  - Unity-side symbols such as `#if ENABLE_UNITY_COLLECTIONS_CHECKS` are **actually defined** when
    compiling source, so a block inside such a guard can no longer be dead code (that is exactly how
    `Ember.Collision` shipped a broken pipeline)
  - No more hand-maintained `.meta` files in the package repo (the root cause of the 0.3.1 / 0.3.2
    incidents)
  - Consumers get readable, debuggable, steppable source

  **No consumer change required**: the package URL is unchanged and so is the public API.

- `Runtime/Ember.navigation.Runtime.asmdef` now points `precompiledReferences` at **`Ember.dll`**
  (the framework still ships precompiled) instead of its own DLL. `references` explicitly lists the
  Unity-side dependencies (`Unity.Collections` / `Unity.Mathematics` / `Unity.Burst`) and the
  upstream Ember assemblies.
- Added the missing `com.unity.collections` dependency — a DLL package compiled against dotnet-side
  `libs/Unity.Collections.dll` never had to declare it; a source package must.
- Dependencies raised: com.ember.ecs 1.13.0, com.ember.core 2.1.3, com.ember.collision 1.0.3

### Notes

- Sync tool: `Ember.Framework/tools/deploy-package-sources.py` (source repo → package repo, with
  deterministic `.meta` generation). See the root `CLAUDE.md` for the release flow.
- Not verified: Unity runtime. The cross-package precompiled reference (`Ember.dll`) in the asmdef
  still needs to resolve inside Unity.

## [0.2.5] — Fix: five buffers never had their length set

### Fixed

- **`FlowSlots`, `Distances`, `HeapCosts`, `HeapVoxels` and the waypoint buffer were all created
  with a capacity but never given a length.**

  `World.CreateBuffer<T>(initialCapacity)` sets only the **capacity**; the logical length is **0**.
  None of those five sites called `ResizeBuffer`, yet every consumer takes `UnsafePtr` and indexes
  by count — the memory is allocated to capacity, so **nothing ever threw**. Each was one step away
  from silently reading an empty span. Collision's equivalent (`DiagnosticFlags`) did throw; see
  Collision 1.0.2.

  All five now use `World.CreateSizedBuffer<T>(n)` (new in Ember 1.13.0), which sets the length as
  it creates.

- **`NavWorldView.FlowSlots()` now checks the length**: it returns `null` when the span from
  `GetBuffer` is shorter than `FlowSlotCount`, instead of handing out a pointer derived from an
  empty span.

### Changed

- Dependencies raised to `com.ember.ecs` 1.13.0, `com.ember.core` 2.1.2 and
  `com.ember.collision` 1.0.2.

### Notes

- Not verified: Unity runtime. Compilation and all 89 tests pass on the CLI.

## [0.2.4] — Fix: ORCA cross-element reads need [NativeDisableParallelForRestriction]

### Fixed

- **`NavAgentJob` no longer throws `IndexOutOfRangeException: Index N is out of restricted
  IJobParallelFor range`.**

  `IJobParallelFor` restricts every `NativeArray` field to `[index, index]` by default. This job
  reads across elements by neighbour index, so four fields gained
  `[NativeDisableParallelForRestriction]`:

  | Field | Access site |
  | --- | --- |
  | `NeighborIndices` | `NeighborIndices[neighborOffset + i]` |
  | `Positions` / `Velocities` / `Radii` | `Positions[other]` / `Velocities[other]` / `Radii[other]` |

  The remaining fields are only accessed at `index` (`Modes` / `MaxSpeeds` / `Preferred` /
  `NewVelocities` / `NeighborCounts`), or only through raw pointers (`NeighborDists` /
  `NeighborDistances` / `Lines` / `Scratch` — raw pointers get no range check), and need no
  attribute.

  After the previous round changed `GetUnsafePtr` to `GetUnsafeReadOnlyPtr` at the top of the
  method, the failure point moved down to the indexer — which is why this only surfaced now: it is
  the next restriction in the same method.

### Changed

- Dependency `com.ember.collision` raised from 1.0.0 to **1.0.1** (which turns the silent null
  pointer on a capacity shortfall into a throw naming the accessor).

### Notes

- Not verified: Unity runtime. The CLI verifies compilation and all 89 tests.

## [0.2.3] — Fix: write pointer on ORCA read-only arrays; follow Collision 1.0.0

### Fixed

- **`NavAgentJob` no longer throws `declared as [ReadOnly] in the job, but you are writing to it`.**

  The job called `GetUnsafePtr()` on six `[ReadOnly]` fields
  (`CellStarts` / `CellCounts` / `SortedAgents` / `Positions` / `Radii` / `NeighborDists`).
  `GetUnsafePtr` goes through `AtomicSafetyHandle.CheckWriteAndThrow`, and the job system has
  already made those fields' handles read-only, so the throw is unconditional. Switched to
  `GetUnsafeReadOnlyPtr()` — available since Collections 2.x, confirmed present in both the 2.4.3
  and 6.6.0 assemblies.

  This is purely a coding mistake, unrelated to the Unity or Collections version.

### Changed

- Dependency `com.ember.collision` raised from 0.3.2 to **1.0.0**. That release turned the body
  snapshot and both query entry points from `NativeArray` into raw pointers (see the Collision
  changelog: a `NativeArray` built over foreign memory always has a `default` handle). Both
  consumers here were updated:
  - `NavRuntimeBake.GatherColliders` now takes raw pointers
  - `NavDynamicObstacleSystem` now takes raw pointers; the snapshot copy uses
    `UnsafeUtility.MemCpy` instead of `NativeArray.Copy`
- **This package's own public API is unchanged** (`NavRuntimeBake.GatherColliders` keeps its
  signature).

### Notes

- Not verified: Unity runtime. The CLI verifies compilation and all 89 tests.

## [0.2.2] — Fix: unregistered component type when systems are constructed before the World

### Fixed
- **Registering `NavSystemGroup` / `NavAvoidanceSystemGroup` in the documented assembly order no longer
  throws `unregistered component type`.**

  `SystemTicker.Register` **constructs systems immediately**, and in the documented example that happens
  before `ECSManager.Start()` — and therefore before the `World` exists. Component type registration,
  however, only happened inside the `World` constructor. Any system whose field initializer builds an
  `EntityQuery` (`ComponentMask.With<T>()` reads `ComponentTypeRegistry` on the spot) therefore threw on
  the first run in a fresh AppDomain.

  Affected: `NavFlowFieldSystem`, `NavRequestSystem`, `NavSteeringSystem`, `NavAgentSystem`,
  `NavPathSystem`. Their query fields lost the initializer and `readonly` and are now built in
  `OnCreate()`, which `SystemTicker.Init` calls after the `World` is constructed; `BuildAccess` runs
  right after, and `DeclareAccess` does not depend on those fields.

### Changed
- Dependencies raised to `com.ember.ecs` 1.12.0, `com.ember.core` 2.1.1 and `com.ember.collision` 0.3.2
  — all three fix the same defect, and UPM resolves exact versions, so not following the bump means not
  getting the fix.

## [0.2.1] — Depends on Ember.Collision 0.3.1

### Fixed

- Raised the `com.ember.collision` dependency from 0.3.0 to 0.3.1. UPM resolves dependencies to
  **exact versions**, and 0.3.0 shipped without `.meta` files — Unity then ignores its
  `Ember.Collision.dll`, so `Ember.Navigation.dll` fails with
  `Unable to resolve reference 'Ember.Collision'` and the whole assembly stays unloaded.

## [0.2.0] — Editor tooling ships with the package

### Added

- **`Editor/Ember.Navigation.Editor.dll`**: the bake window, `NavAnnotationVolume` scene editing and
  navmesh visualisation now ship with the package. 0.1.0 carried the runtime DLL only, so it could not
  bake navigation data at all.

  Unity treats a folder named `Editor` specially — DLLs under it go into the editor-only assembly set
  automatically, so no asmdef is needed. The layout mirrors `Ember.Package/Editor/`.
  The DLL's `platformData` explicitly disables `Any` and enables only `Editor`, so the runtime never
  loads it by accident.

### Notes

- The editor DLL is compiled against Unity 2022.3's `UnityEditor`. Every API it uses
  (`EditorWindow` / `Handles` / `MenuItem` / `EditorPrefs` / `AssetDatabase` / `SerializedProperty`)
  exists in both 2022 and 6, so the older target is the safe build choice.

## [0.1.0] — First release: baking, hierarchical pathfinding and two-tier ORCA

### Added

**Baking (one pipeline shared by offline and runtime)**
- `NavVoxelizer`: per-voxel minimum signed distance to every collider (via `ShapeQuery.ClosestPoint`).
  Distance-field precision drives walkability, wall-hugging normals and path-smoothing clearance.
- `NavRegionLabeler` labels globally connected regions; `NavTileLocalLabeler` labels locally connected
  components inside each tile — **the HPA\* node granularity is a tile-local connected component, not a
  (tile, global region) pair**: the latter is not guaranteed to be connected inside a tile, so the
  intra-cluster A\* could never reach a portal on the far side.
- `NavClusterGraphBuilder` builds the cluster graph and bidirectional portals.
- `NavBaker` bakes in two phases (Plan sizes everything, Bake fills it); `NavBlobWriter` / `NavBlobReader`
  handle the 8-byte-aligned segmented blob (magic `NAVB`, version-checked).
- The distance field is stored quantized to levels (8-bit 0–255 / 16-bit 0–65535) with full scale at
  `MaxBakeRadius`.

**Pathfinding**
- `NavFlowFieldSolver`: multi-source Dijkstra flow field, advanced across frames, lazy-deletion heap,
  ties broken by ascending voxel index for determinism.
- `NavAStar` for intra-cluster voxel search; `NavHpaPathfinder` for hierarchical search (cluster-level
  plus intra-cluster connections).
- `NavPathSmoother`: string pulling by sampling the distance field at half-voxel steps instead of
  casting rays per segment.
- `NavDistanceField`: sampling and central-difference gradient — the geometry source for static-obstacle
  constraints, needing neither shape queries nor rays against obstacles.

**Avoidance**
- `NavOrcaMath` / `NavOrcaMath3D` build constraints with RVO2 / RVO2-3D semantics: 2D uses the cut-off
  circle and the two tangent lines, 3D uses the cone's tangent plane. Static obstacles take full
  responsibility, agents split it evenly.
- `NavLinearProgram2D` ports RVO2's `linearProgram1/2/3` (incremental half-plane intersection plus a
  projection-line fallback that always preserves static-obstacle constraints).
- `NavLinearProgram3D` uses exact vertex enumeration: the boundary of the convex feasible region consists
  of plane patches, sphere patches and their intersections, so the optimum is always in the enumerated
  set — deterministic and provably optimal.
- `NavNeighborGrid`: uniform-grid neighbour query (counting-sort table build, reusing `BlockScan`).
  The scan radius is derived from the actual query radius, and 2D mode pins the inactive axis at build time.

**Systems**
- Fixed ticker (`NavAvoidanceSystemGroup`): `NavSteeringSystem` (path following → desired velocity) and
  `NavAgentSystem` (neighbour query + ORCA → `LinearVelocity`).
- Variable ticker (`NavSystemGroup`): `NavDynamicObstacleSystem`, `NavRequestSystem`, `NavFlowFieldSystem`,
  `NavPathSystem`.
- The dual budget is split in two: a count cap at request scheduling and a time cap during path solving,
  whichever is reached first.
- The flow-field cache is keyed by target voxel, evicted LRU, advanced across frames, and invalidated
  automatically when the distance-field epoch changes.

**Runtime**
- `NavRuntimeBake` bakes static colliders from the collision world's current-frame snapshot rather than
  rescanning the scene.
- `NavDynamicObstacleSystem` performs local invalidation for dynamic obstacles: only the affected voxel
  block's distance field and occupancy bits are recomputed.
- `NavBakedAsset` / `NavBakedAssetLoader` store the blob as a ScriptableObject and load it at runtime.

**Editor** (source is included in the package; pull it into your Unity project yourself)
- Bake window (bakes from the running collision world in Play mode and saves the artifact).
- `NavAnnotationVolume`: an axis-aligned AABB that overrides walkability or scales path cost, draggable
  in the scene view.
- Navmesh visualisation: the sampling stride is derived from a target draw budget so million-voxel grids
  do not stall the editor.

### Notes

- Depends on `com.ember.ecs` 1.11.0, `com.ember.core` 2.1.0, `com.ember.collision` 0.3.0.
- **The systems and jobs are not verified under the CLI** — they need the Unity runtime and must be
  exercised in the Unity Test Runner. The algorithm cores (geometry, ORCA, both linear programs, the
  neighbour grid, path following, the distance field) do have pure-logic test coverage.
- The distance-field solvers currently interpret 8-bit levels only; 16-bit needs the level width
  parameterised in the solvers first.
- Annotation volumes are axis-aligned world AABBs; rotation does not participate in the test. Stack
  several axis-aligned blocks when you need a slanted region.
