# Ember.Navigation 依赖方需求索引

Ember.Navigation 对三个上游模块共提出五项能力需求。按模块拆分为三份**独立可转发**的交付文档，
每份自带背景说明、现状证据、需求形态、验收标准，可单独交给对应模块的实现方。

## 交付文档

| 文档 | 上游模块 | 需求 | 优先级 |
|---|---|---|---|
| [`requirements/ember-collision.md`](requirements/ember-collision.md) | Ember.Collision | N1 shape → point 最近点查询<br>N2 body 快照读取<br>N3 精确 ray vs shape | P0 阻断<br>P0 阻断<br>P1 可降级 |
| [`requirements/ember-core.md`](requirements/ember-core.md) | Ember.Core | E1 移动积分系统 | P1 |
| [`requirements/ember-framework.md`](requirements/ember-framework.md) | Ember | F1 Buffer 批量写入 / 长度设置 | P0 可降级 |

**优先级含义**

- **P0 阻断** —— 不提供则导航对应阶段无法开工
- **P0 可降级** —— 不提供则导航走降级实现，代价见该文档；但必须在指定阶段前定下
- **P1 可降级** —— 可先用近似方案跑通，精确版后续补

## 建议实现顺序

1. **N1 + N2**（Ember.Collision）—— 解锁导航烘焙核心（P1）与运行时加载（P2）
2. **F1**（Ember）—— 解锁大世界数据加载；不实现则导航走降级路径，但必须在导航 P2 前定下
3. **E1**（Ember.Core）—— 解锁避障落地（导航 P5）
4. **N3**（Ember.Collision）—— 动态障碍精确检测，在导航 P8 之前补上即可

N1 + N2 与 F1 可并行推进，二者共同构成导航 P1–P2 的前置链。

## 相关文档

- [`design-navigation.md`](design-navigation.md) —— Ember.Navigation 完整设计文档。
  其中第 6 节是**设计视角**的需求清单（说明导航依赖什么、为什么），
  第 9 节是导航自身的分期路线（P0–P9）。
  若设计文档与本文档所指向的交付文档不一致，以设计文档为准并同步更新交付文档。
