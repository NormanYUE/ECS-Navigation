# Ember Navigation Manual

Navigation for the Ember ECS framework: navmesh baking, hierarchical pathfinding and two-tier ORCA avoidance.

- Built on Ember's job scheduling and Burst; zero allocation on hot paths
- Distance-field representation — one bake serves any agent radius (`distanceField[v] >= r` means walkable)
- Unified 2D / 3D: ground agents solve in the navigation plane, flying agents solve in true 3D

## Installation

Add the dependency to your `manifest.json`:

```json
"com.ember.navigation": "https://github.com/NormanYUE/Ember-Navigation.git"
```

Depends on `com.ember.ecs` 1.11.0, `com.ember.core` 2.1.0 and `com.ember.collision` 0.3.0.

> **Unity 2022 cannot pull this on Apple Silicon with CLT 27.0**: the UPM helper process is x86_64 while
> the new CLT ships an arm64-only `libxcrun`. Install a forwarding shim or move to Unity 6.

## Wiring up

A navigation agent needs a fixed component set, **all added at creation time** (navigation systems never
add or remove components mid-flight):

```csharp
var entity = world.CreateEntity();
world.AddComponent(entity, new LocalTransform(position, quaternion.identity, 1f));
world.AddComponent(entity, new LinearVelocity(float3.zero));   // Ember.Core
world.AddComponent(entity, new NavAgent(radius: 0.5f, maxSpeed: 3.5f, mode: NavAgentMode.Ground));
world.AddComponent(entity, new NavRequest { Target = target, Status = NavRequestStatus.None });
world.AddComponent(entity, new NavPathState());
world.AddComponent(entity, new NavDesiredVelocity());
```

Register the system groups on two tickers:

```csharp
manager.GetTicker(updateIdx).Register<NavSystemGroup>();          // pathfinding: variable step
manager.GetTicker(fixedIdx).Register<NavAvoidanceSystemGroup>();  // avoidance: fixed step
manager.GetTicker(fixedIdx).Register<MotionSystemGroup>();        // integration, after avoidance
```

ORCA's time-horizon prediction needs a stable step, so the avoidance chain runs on the fixed ticker.
Integration uses `Ember.Core`'s `MotionSystemGroup`; skip it if your project already has its own.

## Baking

**Offline**: the editor window `Ember/Navigation/烘焙窗口`. Baking runs in Play mode — the colliders live
in the ECS world, which does not exist in edit mode. The result is saved as a `NavBakedAsset`.

**Runtime**:

```csharp
using var workspace = new NavRuntimeBakeWorkspace();
var colliders = new NativeArray<NavBakeCollider>(capacity, Allocator.Temp);

int count = NavRuntimeBake.GatherColliders(world, staticOnly: true,
    (NavBakeCollider*)colliders.GetUnsafePtr(), colliders.Length);

NavRuntimeBake.BakeAndLoad(world, input, (NavBakeCollider*)colliders.GetUnsafePtr(), count,
    vertexPool, vertexPoolLength, workspace);
```

**Static colliders only**: baking dynamic obstacles would freeze moving geometry into the distance field.
They are handled by `NavDynamicObstacleSystem` through local invalidation instead.

Keep the bake parameters aligned with `NavConfig` — if the two disagree, the runtime solvers will expand
neighbours with the wrong connectivity.

## Annotations

Attach a `NavAnnotationVolume` in the scene to mark an axis-aligned world AABB:

| Mode | Effect |
| --- | --- |
| `ForceWalkable` | Overrides the distance field's blocked verdict |
| `ForceBlocked` | Forces blocked |
| `None` | Cost multiplier only (0.5 fast, 3 slow) |

Only the position participates; rotation does not. `Aabb` is axis-aligned by definition — stack several
axis-aligned blocks when you need a slanted region.

## Runtime queries

Flow-field gradients can be queried directly, without waiting for pathfinding:

```csharp
if (world.TryGetNavWorld(out var nav) && nav.IsReady)
{
    bool walkable = nav.IsWalkable(voxel, agentRadius);
    int region = nav.RegionAt(voxel);
    bool blocked = nav.IsOccupied(voxel);
}
```

## Tuning (the `NavConfig` singleton)

| Field | Meaning |
| --- | --- |
| `Dimension` | XY / XZ for 2D, XYZ for 3D |
| `VoxelSize` | Voxel edge in metres; start at 0.25–0.5 and calibrate on device |
| `MaxBakeRadius` | Largest supported agent radius; full scale of the distance field |
| `RequestBudgetPerFrame` / `RequestBudgetMs` | Dual budget: count and time, whichever comes first |
| `TimeHorizon` / `TimeHorizonObst` | ORCA time horizons (agent-agent / static obstacle) |
| `MaxNeighbors` | Maximum neighbours per agent in ORCA |
| `FlowFieldCacheSize` / `FlowFieldPopBudget` | Flow-field cache slots and per-frame pop budget |
| `ArriveRadius` / `LookAheadDistance` | Path-following arrival radius and look-ahead distance |

## Known limits

- **The systems and jobs are not verified under the CLI** — they need the Unity runtime and must be run in
  the Unity Test Runner. The algorithm cores do have pure-logic test coverage.
- The distance-field solvers currently interpret 8-bit levels only; 16-bit needs the level width
  parameterised in the solvers first.
- `NavHpaPathfinder`'s `LastStatus` / `DebugLastSegment*` are static state and will overwrite each other
  across multiple Worlds; move them into `Context` before parallelising.
- Editor tooling (bake window, annotation editing, navmesh visualisation) ships with the package as
  `Editor/Ember.Navigation.Editor.dll`; no source copying required.

## Notes

- Versions use plain `a.b.c`.
- This repository holds build artifacts and documentation only; the source lives in a private repository.
