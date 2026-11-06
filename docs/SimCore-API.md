# SimCore 接口契约（Gate 0 · 冻结）

> 版本：**v1.0（FROZEN）**
> 日期：2026-10-10
> 任务：技术方案03 §5 · **T0.4 接口冻结与契约文档 —— Gate 0 ★**（角色：架构负责人，由项目负责人复核）
> 依据：技术方案03 §3 全节（§3.1 SimCore public API、§3.2 `.spmap` 顶层结构）；T0.2 交付物（`SimCore/Model/*`）；T0.3 交付物（`SimCore/Io/*` + `docs/spmap-schema.md`）；技术方案01 §3；技术方案02 §2；游戏设计v2 §6/§8/§9/§10/§12/§13
> 状态：**已冻结**。冻结后所有人（含 AI）只能**调用**、不得**修改**。任何改动走 §11 变更流程，由项目负责人审批。

---

## 0. 冻结声明

### 0.1 本契约是什么

本文件是《信号规划师》**仿真核（SimCore）与表现层之间唯一的接口真相**。它把技术方案03 §3.1 的"清单形态"API 与 §3.2 的 `.spmap` 顶层结构**定稿为可实现的签名、语义与前置条件**，覆盖 M1~M4 全部任务卡所需的每一个符号。

Gate 0 的产出是**两份文档**：

| 文档 | 内容 | 状态 |
| --- | --- | --- |
| `docs/SimCore-API.md`（本文件） | SimCore 全部 public API 的签名/语义/前置条件 + 覆盖矩阵 | **FROZEN v1.0** |
| `docs/spmap-schema.md` | `.spmap` 数据格式规范 v1（字段表/枚举/校验码表） | **FROZEN v1.0**（本契约 §8 引其为组成部分） |

### 0.2 已交付、已冻结、可直接调用的实现

| 命名空间 | 交付任务 | 状态 |
| --- | --- | --- |
| `SimCore.Model` | T0.2 | 已实现、已验证（21 项测试中 9 项） |
| `SimCore.Io` | T0.3 | 已实现、已验证（40 项测试中 19 项） |
| `SimCore.Geometry` / `SimCore.Sim` / `SimCore.Metrics` / `SimCore.Scoring` | M1 / M2（**待实现**） | 本契约**冻结其签名与语义**，实现由对应卡完成 |

> 说明：本任务是**只产出文档、不写代码**。上述"待实现"部分的签名由本契约**先行冻结**——这正是"接口先行"的意义：M1/M3 在 Gate 0 通过后可**并行开工**（技术方案03 §2 并行策略）。

### 0.3 冻结的三条硬边界

1. **不得新增 SimCore 之外的依赖**：SimCore 内不得出现任何 Godot 命名空间，不得新增任何第三方包（铁律 1，由 `ScaffoldTests.A3_*` 与 `Guard_SimCore_Source_Has_No_Godot` 静态守卫）。
2. **不得使用系统随机数**：任何随机性必须经 `Seed` 注入、可复现（铁律 2，由 `Guard_SimCore_Source_Has_No_System_Randomness` 守卫）。
3. **不得静默改接口**：改 §11 流程走审批。

---

## 1. 全局约定

### 1.1 程序集与目标框架

| 项 | 值 |
| --- | --- |
| 程序集名 | `SimCore` |
| 目标框架 | `net8.0` |
| 依赖 | 零 `PackageReference`、零 `ProjectReference`（叶子类库） |
| 可空性 | `Nullable=enable`；内部使用 `ImplicitUsings` |
| 构建 | `TreatWarningsAsErrors=true`（"有警告即失败"，对应验收 A） |

### 1.2 依赖方向（无环，冻结）

```
        Model
       /     \
 Geometry     Io          （Geometry → Model；Io → Model）
       \     /  \
          Sim             （Sim → Geometry、Sim → Io）
           |
        Metrics           （Metrics → Sim、Metrics → Model）
           |
        Scoring           （Scoring → Io、Scoring → Metrics）
```

- **Io 不依赖 Sim / Metrics / Scoring / Geometry**（关卡装配层是干净的数据层）。
- **Sim 依赖 Io**：仿真核消费关卡装配层（`Level` 携带 `Network` 与三类需求）。这是 Gate 0 的**明确裁定**（§10 D9）——`§3.1` 的 `SimEngine(network, config)` 定稿为可接收 `Level` 的重载。
- **Metrics 依赖 Sim**：指标从仿真核产出的 `SimEvent` 流累计。
- **Sim 不得反向依赖 Metrics / Scoring**：仿真核不计算评分。`SimState` **不含**指标（避免反向依赖）。
- 无环，编译期即可保证。

### 1.3 通用语义约定

| 约定 | 规定 |
| --- | --- |
| 不可变性 | 所有 Model / Io 数据载体为 `init` 只读；`IReadOnlyList<T>` 暴露集合；`CompiledNetwork` 与 `SimState` 为只读快照 |
| 确定性 | 凡输出集合（邻接表、事件、校验结论、相位建议）一律**排序去重、顺序固定、可复现**；不得依赖字典遍历顺序（铁律 2） |
| 单位 | 长度=米；时间=秒；速度=米/秒；流率=辆(人)/秒；坐标=米 |
| 枚举序列化 | `.spmap` 中枚举按**名称字符串**（如 `"Signalized"`、`"Driving"`、`"Fixed"`、`"Independent"`） |
| 参数校验 | public 方法对 `null` / 越界一律**抛异常**（`ArgumentNullException` / `ArgumentException` / `ArgumentOutOfRangeException`），不静默吞掉 |
| 只读职责 | SimCore 任何方法**不得**修改传入的 `Level` / `Network`（校验器只读，由 `SpmapTests.C3_*` 保证） |

### 1.4 表现层（GDScript）接入约定

- 表现层（`Game/`、`Editor/`）**只读** `SimState` 快照与 Io/Scoring 的返回值，**不得**写任何业务逻辑（铁律 4）。
- `Game`、`Editor` 是 **Godot .NET 工程**，`project.godot` 已声明 `"C#"` 特性且 `*.csproj` 引用 `SimCore/SimCore.csproj`（由 `ScaffoldTests.C4_*` 保证）。
- GDScript 通过工程程序集内的**薄适配层**（C#，无业务逻辑）访问 SimCore；本契约中标注 **`[表现层]`** 的成员即为适配层需要暴露的符号面。
- 所有 `[表现层]` 成员均提供**原始类型友好**签名（`int`/`double`/`bool`/数组），避免 GDScript 侧触碰复杂泛型。
- **约定**：代码块中的 `[表现层]` 是**文档标注**（不是 C# 语法、也不是特性），仅用于标示"该成员需由表现层适配层暴露"；落地实现时直接书写普通成员即可（如 `[表现层] public static Level Load(...)` 在代码中即 `public static Level Load(...)`）。

---

## 2. `SimCore.Model`（已冻结实现 · T0.2）

> 位置：`SimCore/Model/*.cs`。纯数据载体，**零仿真逻辑**。所有类型为 `sealed`，属性为 `init`。

### 2.1 枚举

```csharp
namespace SimCore.Model;

public enum LaneType     { Driving, Bus, Biking, Sidewalk, SharedLeftTurn, Construction, Parking }
public enum TurnType     { Straight, Left, Right, UTurn, Crosswalk }
public enum TurnPriority { Banned, Yield, Protected }
public enum NodeType     { Signalized, Unsignalized, Origin, Destination }
public enum StageType    { Fixed, Variable }
```

| 枚举 | 用于 `.spmap` | 冻结要点 |
| --- | --- | --- |
| `LaneType` | `lanes[].type` | 成员名与顺序**不得改名/增删/调序**（顺序即数值） |
| `TurnType` | `turns[].type` | 同上；`Crosswalk` 供行人过街 |
| `TurnPriority` | **不写入文件** | 由 `Stage.PriorityOf(turnId)` 从相位派生 |
| `NodeType` | `nodes[].type` | `Origin`/`Destination` 为需求源汇 |
| `StageType` | `stages[].type` | 支撑 §3.2 的强类型载体（§10 D4 裁定：冻结） |

### 2.2 `Node`

| 成员 | 签名 | 语义 | 前置 |
| --- | --- | --- | --- |
| `Id` | `int Id { get; init; }` | 节点 id（`nodes[].id`） | 编译期要求同集合 0..N-1 连续唯一 |
| `Type` | `NodeType Type { get; init; }` | 节点类型（`nodes[].type`） | — |
| `X` / `Y` | `double X/Y { get; init; }` | 网格坐标（米，`nodes[].x/y`） | — |
| `Offset` | `double Offset { get; init; }` | 相位差（秒，`signals[].offset`） | 非信号节点默认 0 |
| `SignalPlans` | `IReadOnlyList<SignalPlan> SignalPlans { get; init; }` | 配时方案集（`signals[].plans[]`） | 默认空集合 |
| `IsSignalized` | `bool IsSignalized { get; }` | `Type == NodeType.Signalized` | 派生只读 |

> `signals[]` **以 node 为主键**，其属性直接挂在 `Node` 上（不新增 `TrafficSignal` 类型）——见 §7.4 规范形与 §10 D7。

### 2.3 `Lane`

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `Id` | `int Id { get; init; }` | 车道 id（`lanes[].id`），编译期 0..N-1 连续唯一 |
| `Road` | `int Road { get; init; }` | 所属道路 id（`lanes[].road`），供进口道聚合 |
| `Type` | `LaneType Type { get; init; }` | 车道类型（`lanes[].type`） |
| `FromNode` | `int FromNode { get; init; }` | 起点节点 id（`lanes[].from_node`） |
| `ToNode` | `int ToNode { get; init; }` | 终点节点 id（`lanes[].to_node`）；车辆沿 `FromNode → ToNode` 行驶 |
| `Length` | `double Length { get; init; }` | 长度（米，`lanes[].length`），须 > 0 |
| `SpeedLimit` | `double SpeedLimit { get; init; }` | 限速（米/秒，`lanes[].speed_limit`），默认 13.89 |
| `LaneIndex` | `int LaneIndex { get; init; }` | 同向车道序号（`lanes[].lane_index`），渲染横向偏移用 |

### 2.4 `Turn`

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `Id` | `int Id { get; init; }` | 转向 id（`turns[].id`），编译期 0..N-1 连续唯一 |
| `SrcLane` | `int SrcLane { get; init; }` | 源车道 id（`turns[].src_lane`） |
| `DstLane` | `int DstLane { get; init; }` | 目标车道 id（`turns[].dst_lane`） |
| `Node` | `int Node { get; init; }` | 所属路口节点 id（`turns[].node`） |
| `Type` | `TurnType Type { get; init; }` | 转向类型（`turns[].type`） |
| `Leg` | `int Leg { get; init; }` | 分段过街的第几段（`turns[].leg`，可选，缺省 `-1` = 一次走完）；`0` = 路缘→岛、`1` = 岛→对岸（2026-10-24 新增） |
| `IslandId` | `int IslandId { get; init; }` | 所属安全岛 id（`turns[].island`，可选，缺省 `-1`）；与 `Leg` 成对（2026-10-24 新增） |

> 几何语义（由 `SpmapValidator` 校验，非 Model 层）：`SrcLane.ToNode == Node` 且 `DstLane.FromNode == Node`。**转向优先级不静态存储**，由相位派生（§2.6）。
> `.spmap` 的可选字段 `turns[].ratio` 挂在关卡层 `Level.TurnRatios`（§7.5），**不在 `Turn` 上**。

### 2.5 `Movement`（编译派生）

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `Id` | `int Id { get; }` | 分组 id（0..M-1，按 `(Node, Road, Type)` 字典序稳定分配） |
| `Node` | `int Node { get; }` | 所属路口节点 id |
| `Road` | `int Road { get; }` | 进口道路 id（取自各 `Turn` 源车道的 `Lane.Road`） |
| `Type` | `TurnType Type { get; }` | 该流统一转向类型 |
| `TurnIds` | `IReadOnlyList<int> TurnIds { get; }` | 包含的转向 id 集合（升序去重） |
| 构造 | `Movement(int id, int node, int road, TurnType type, IReadOnlyList<int> turnIds)` | — |

> 分组口径 = 同一路口 + 同一进口道路 + 同一转向类型（借 A/B Street `MovementID`）。**`.spmap` 无 movements 表**，`Movement` 是 `CompiledNetwork.Compile` 的派生产物。§10 D5 裁定：冻结。

### 2.6 `Stage` / `SignalPlan`

**`Stage`（相位）**

| 成员 | 签名 | 语义 | 前置 |
| --- | --- | --- | --- |
| `Type` | `StageType Type { get; init; }` | 配时类型（`stages[].type`） | 默认 `Fixed` |
| `Duration` | `double Duration { get; init; }` | **绿灯时长**（秒，`stages[].duration`） | 须 > 0（校验器报 `stage_duration_non_positive`） |
| `ProtectedTurnIds` | `IReadOnlyList<int> ProtectedTurnIds { get; init; }` | 保护放行转向集合（`stages[].protected`） | 默认空 |
| `PermittedTurnIds` | `IReadOnlyList<int> PermittedTurnIds { get; init; }` | 允许（须让行）转向集合（`stages[].permitted`） | 默认空 |
| `PriorityOf` | `TurnPriority PriorityOf(int turnId)` | 保护集合内→`Protected`；允许集合内→`Yield`；否则→`Banned` | 纯查表，无副作用 |

> **`duration` = 绿灯时长**（Gate 0 口径，§10 D8）。黄灯 / 全红 / 绿闪**不写入** `.spmap`，由 `SignalTiming` 依清空时间估算（§4.7）。红灯 = 周期 − 本相位绿灯。

**`SignalPlan`（配时方案）**

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `StartTime` | `double StartTime { get; init; }` | 方案生效起始时刻（秒，`plans[].start_time`） |
| `Stages` | `IReadOnlyList<Stage> Stages { get; init; }` | 相位序列（`plans[].stages[]`），默认空 |

> 一个节点可有多个方案，按时段切换（时变配时）。"当前生效方案"的选择属仿真期逻辑（`SignalStateMachine`）。

### 2.7 `Network`

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `Nodes` | `IReadOnlyList<Node> Nodes { get; init; }` | 节点集合（`nodes[]`） |
| `Lanes` | `IReadOnlyList<Lane> Lanes { get; init; }` | 车道集合（`lanes[]`） |
| `Turns` | `IReadOnlyList<Turn> Turns { get; init; }` | 转向集合（`turns[]`） |
| `Islands` | `IReadOnlyList<Island> Islands { get; init; }` | 行人安全岛（`islands[]`，可选，缺省空；**2026-10-24 新增**） |
| `Compile` | `CompiledNetwork Compile()` | 编译为扁平结构（等价 `CompiledNetwork.Compile(this)`） |

**`Compile()` 异常（前置违反时抛出）**

| 异常 | 触发 |
| --- | --- |
| `ArgumentException` | 节点/车道/转向的 id 非 0..N-1 连续或存在重复 |
| `ArgumentOutOfRangeException` | 车道/转向引用了越界的节点或车道 id |

### 2.8 `CompiledNetwork`（拓扑编译产物，只读扁平结构）

> **一切按 id 直寻址**：实体下标即 id；图邻居在编译期算好、排序去重，与输入顺序无关（确定性）。§10 D3 裁定：冻结为 Model 的正式类型。

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `Compile` | `static CompiledNetwork Compile(Network network)` | 编译入口；`network==null`→`ArgumentNullException` |
| `Nodes` / `Lanes` / `Turns` | `IReadOnlyList<Node/Lane/Turn> { get; }` | 下标 = id |
| `Movements` | `IReadOnlyList<Movement> Movements { get; }` | 下标 = 交通流 id |
| `NodeCount` / `LaneCount` / `TurnCount` / `MovementCount` | `int { get; }` | 数量 |
| `GetNode` / `GetLane` / `GetTurn` | `Node/Lane/Turn GetX(int id)` | 按 id 取；越界→`ArgumentOutOfRangeException` |
| `OutgoingTurns` | `IReadOnlyList<int> OutgoingTurns(int laneId)` | 以该车道为源的转向 id（升序） |
| `IncomingTurns` | `IReadOnlyList<int> IncomingTurns(int laneId)` | 以该车道为目标的转向 id（升序） |
| `DownstreamLanes` | `IReadOnlyList<int> DownstreamLanes(int laneId)` | 下游车道 id（经转向可达，升序去重） |
| `UpstreamLanes` | `IReadOnlyList<int> UpstreamLanes(int laneId)` | 上游车道 id（升序去重） |
| `TurnsAtNode` | `IReadOnlyList<int> TurnsAtNode(int nodeId)` | 节点转向集合（升序） |
| `MovementsAtNode` | `IReadOnlyList<int> MovementsAtNode(int nodeId)` | 节点交通流集合（升序） |
| `IncomingLanes` | `IReadOnlyList<int> IncomingLanes(int nodeId)` | 进口车道（`Lane.ToNode == node`，升序） |
| `OutgoingLanes` | `IReadOnlyList<int> OutgoingLanes(int nodeId)` | 出口车道（`Lane.FromNode == node`，升序） |

> 术语：车道"下游"= 经转向可达的目标车道；"上游"= 可经转向驶入本车道的源车道。节点的"进口/出口车道"基于几何（`ToNode`/`FromNode`）。

---

## 3. `SimCore.Geometry`（M1 · 待实现，签名冻结）

> 位置：`SimCore/Geometry/*.cs`。几何层只依赖 `SimCore.Model`，**不含仿真状态**。

### 3.1 `ConflictMatrix` —— 冲突矩阵（T1.3）

```csharp
namespace SimCore.Geometry;

public sealed class ConflictMatrix
{
    public static ConflictMatrix From(Network network);            // §3.1 字面
    public static ConflictMatrix From(CompiledNetwork network);    // 便利重载

    public bool AreConflicting(Turn a, Turn b);                    // §3.1 字面（实例成员）
    [表现层] public bool AreConflicting(int turnIdA, int turnIdB);  // 原始类型友好
    public IReadOnlyList<int> ConflictsOf(int turnId);              // 升序、去重
}
```

**`AreConflicting(a, b)` 判定（有序规则，逐条对应 T1.3 验收 a）**

| # | 条件 | 结论 |
| --- | --- | --- |
| 1 | `a.Id == b.Id` | `false`（同一条转向不自冲突） |
| 2 | `a.SrcLane == b.SrcLane` | `false`（**同起点不冲突**） |
| 3 | `a.DstLane == b.DstLane` | `true`（**同终点冲突**：汇入同一目标车道） |
| 4 | 两条转向中心线折线**几何相交** | `true`（**几何相交冲突**） |
| 5 | 其余 | `false` |

- 语义：冲突是**对称**关系（`AreConflicting(a,b) == AreConflicting(b,a)`）。
- 前置：`a`/`b` 必须来自本矩阵编译所用的同一路网；`turnId` 越界→`ArgumentOutOfRangeException`。
- 确定性：`From` 两次编译同一路网，`ConflictsOf` 结果逐项相等（铁律 2）。

### 3.2 `TurnGenerator` —— 转向自动生成（T4.2）

```csharp
namespace SimCore.Geometry;

public static class TurnGenerator
{
    [表现层] public static Turn[] FromGeometry(Network network);   // §3.1 字面
}
```

- **语义**：给定路网几何，自动推导全部转向并**判定类型**：
  - 机动车转向：对每个节点，取"进口车道（`ToNode==node`，类型 ∈ `{Driving, Bus, Biking, SharedLeftTurn}`）× 出口车道（`FromNode==node`，可通行类型）"，按两段方向向量夹角分类为 `Straight / Left / Right / UTurn`（阈值角度由实现固定并单测）。
  - 人行横道转向：人行道（`Sidewalk`）之间的连接生成 `Crosswalk` 转向，供行人需求引用。
- **前置**：`network != null`；否则 `ArgumentNullException`。
- **输出约定**：返回的 `Turn[]` 依序编号（`id` 从 0 连续）；**确定性**（同一路网两次调用结果逐项相等）。合并入既有路网时，由调用方负责 id 基址与去重。
- **对应验收**：T4.2 a) 新增道路后转向自动出现且类型正确。

### 3.3 `PhaseAutoGenerator` —— 相位方案自动生成（T4.2）

```csharp
namespace SimCore.Geometry;

public static class PhaseAutoGenerator
{
    [表现层] public static SignalPlan[] Suggest(Network network, int nodeId);  // §3.1 字面
}
```

- **语义**：为指定路口自动生成相位方案**候选集**，**至少包含 2 阶段方案与 4 阶段方案各一套**（对应 T4.2 c）。
  - 分组依据：`Movement`（进口道级转向组）与 `ConflictMatrix`——把互不冲突的 `Movement` 并入同一 `Stage` 的 `protected`，冲突者分入不同 `Stage`。
  - 每个 `Stage` 的 `Duration` 给出一个**固定配时建议**（可直接被玩家在基础模式微调）。
- **前置**：`network != null`；`nodeId` 指向存在的节点且该节点有进口车道；否则抛异常。
- **输出**：`SignalPlan[]`（≥2 套候选，顺序稳定：先 2 阶段、后 4 阶段）。
- **与 `ConflictMatrix` 同源**：编辑器"自动推导"与运行期门控使用**同一套冲突判定**，不得重复实现（T4.2 d）。

---

## 4. `SimCore.Sim`（M1 · 待实现，签名冻结）

> 位置：`SimCore/Sim/*.cs`。仿真核依赖 `Model`、`Geometry`、`Io`。

### 4.1 `SimConstants` —— 全局仿真常量（集中定义，供各卡引用）

> 对应 T1.1 ④ "饱和流率、起步损失时间等常量集中定义"。

```csharp
namespace SimCore.Sim;

public static class SimConstants
{
    public const double SaturationFlowPerLane = 1800d / 3600d;  // 饱和流率 ≈ 0.5 辆/秒/车道（T1.1 b，误差 ±5%）
    public const double StartUpLostTime       = 2.0d;           // 起步损失时间（秒，T1.1 d）
    public const double VehicleLength         = 4.5d;           // 标准车长（米）
    public const double BusLength             = 12.0d;          // 公交加长（米，T3.1 b）
    public const double TruckLength           = 7.0d;           // 货车车长（米，T4.3 S1b）
    public const double CarSpeedFactor        = 1.0d;           // 小汽车速度系数（× 车道限速，T4.3 S1b）
    public const double TruckSpeedFactor      = 0.85d;          // 货车速度系数（× 车道限速，T4.3 S1b）
    public const double BusSpeedFactor        = 0.95d;          // 公交速度系数（× 车道限速，T4.3 S1b）
    public const double MinHeadway            = 1.5d;           // 最小车头间距（米）
    public const double PedestrianSpeed       = 1.4d;           // 行人过街速度（米/秒，T1.4 c）
    public const double BicycleStartDelay     = 1.0d;           // 混行非机动车起步附加延迟（秒，T1.5 c）
    public const double SpillbackOccupancy    = 0.9d;           // 溢出阈值：首车道占有率 > 0.9（T2.1 e）
}
```

> 这些常量是**全项目唯一真相**；任何卡不得就地硬编码同义数字。若需按关卡覆盖，走变更流程（§11）。

### 4.2 `SimConfig` —— 仿真配置

```csharp
namespace SimCore.Sim;

public sealed class SimConfig
{
    public double Dt                   { get; init; } = 0.1d;    // 固定仿真步长（秒），> 0
    public int    SpeedMultiplier      { get; init; } = 1;       // 初始加速档 ∈ {1,2,4,8,16,32}
    public bool   Deterministic        { get; init; } = true;    // 确定性模式（默认开）
    public long   Seed                 { get; init; }            // 需求注入种子（禁止系统随机）
    public double Duration             { get; init; } = 600d;    // 单局仿真时长（秒）
    public SignalTiming Timing         { get; init; } = SignalTiming.Default; // 黄/全红/绿闪
    public int    QueueCapacityPerLane { get; init; } = 20;      // 每车道排队容量（辆），溢出判据
}
```

> 对应 §3.1 `SimConfig { dt, speedMultiplier, deterministic, ... }`；`...` 定稿为 `Seed / Duration / Timing / QueueCapacityPerLane`。

### 4.3 `SimEngine` —— 仿真主循环（T1.6 核心）

```csharp
namespace SimCore.Sim;

public sealed class SimEngine
{
    public SimEngine(CompiledNetwork network, SimConfig config);   // §3.1 字面形：SimEngine(network, config)
    public SimEngine(Level level, SimConfig config);               // 关卡直通形（装配层；§10 D9）

    public SimConfig        Config          { get; }
    public CompiledNetwork  Network         { get; }
    public int              SpeedMultiplier { get; set; }   // 运行时可改（换挡/暂停）；只改步数不改逻辑
    public double           SimTime         { get; }        // 已仿真时长（秒）
    public bool             IsFinished      { get; }        // SimTime >= Config.Duration

    public void      Step();                       // 推进一个固定步长 Dt
    public void      Step(double dt);              // §3.1 字面；前置：dt == Config.Dt
    public void      Advance(double realSeconds);  // [表现层] 每帧调用，按 SpeedMultiplier 推进整数步
    public void      Reset(long seed);             // 复位到 t=0 并换种子（§3.1 Reset(seed)）
    public SimState  Snapshot();                   // [表现层] 只读快照
    public ulong     StateHash();                  // §3.1/T1.6 d：状态哈希（确定性回归用）
    public IReadOnlyList<SimEvent> DrainEvents();  // 取出并清空自上次调用以来的事件（顺序确定）
    public void      ApplyPlan(int nodeId, SignalPlan plan);  // 配时修改：下一周期生效（T1.2 c）
}
```

| 方法 | 语义 | 前置 / 异常 |
| --- | --- | --- |
| `SimEngine(CompiledNetwork, SimConfig)` | 以编译路网建引擎；**无需求注入**（供 T1.1~T1.6 单测直接放车） | `network==null`→`ArgumentNullException` |
| `SimEngine(Level, SimConfig)` | 内部 `level.Network.Compile()`，并按 `Demand/Pedestrians/Bicycles` 建立需求时刻表 | `level==null`→`ArgumentNullException` |
| `Step()` | 推进**恰好** `Config.Dt` 秒；步长用**固定累加器**（避免浮点漂移，T1.6 ④） | 已 `IsFinished` 时为空操作 |
| `Step(dt)` | §3.1 兼容形 | `dt != Config.Dt`→`ArgumentException`（固定步长不变式） |
| `Advance(realSeconds)` | 推进 `round(realSeconds * SpeedMultiplier / Dt)` 步；**不改仿真逻辑**（T3.2 ④） | `realSeconds < 0`→`ArgumentOutOfRangeException` |
| `Reset(seed)` | 复位到 `t=0`，清空状态与事件，换种子 | — |
| `Snapshot()` | 返回当前只读快照（供渲染） | 每次调用返回**新的不可变**对象 |
| `StateHash()` | 返回当前状态哈希；**同 seed 两次运行、1× 与 32× 到同一仿真时刻，哈希相同**（T1.6 a/b） | — |
| `DrainEvents()` | 返回并清空累计的 `SimEvent`；顺序确定、可复现 | — |
| `ApplyPlan(nodeId, plan)` | 提交新配时，**下一周期生效**，不打断当前相位（T1.2 c） | `nodeId` 非信号路口→`ArgumentException` |

**确定性保证（T1.6）**：全程无 `NaN/Inf`；不得依赖字典遍历顺序；步进顺序固定；`StateHash()` 覆盖位置/速度/相位/排队等全部可影响结果的状态。

### 4.4 `SimState` 与快照类型（只读，`[表现层]`）

```csharp
namespace SimCore.Sim;

public enum VehicleKind     { Car, Bus, Truck }                // 渲染区分（T3.1 b；Truck 于 T4.3 S1b 末尾追加）
public enum SignalPhaseState { Green, Yellow, AllRed, GreenFlash }

public readonly record struct VehicleSnapshot   (int Id, int LaneId, double Position, double Speed, bool Stopped, VehicleKind Kind);
public readonly record struct PedestrianSnapshot(int Id, int CrosswalkTurnId, double Position, bool Waiting);
public readonly record struct BicycleSnapshot   (int Id, int LaneId, double Position, BicycleMode Mode, bool Stopped);
public readonly record struct SignalSnapshot    (int NodeId, int PlanIndex, int StageIndex, SignalPhaseState Phase, double TimeInPhase, double PhaseRemaining);
public readonly record struct LaneSnapshot      (int LaneId, int QueueLength, double Occupancy);

public sealed class SimState
{
    public double SimTime                          { get; }
    public IReadOnlyList<VehicleSnapshot>    Vehicles    { get; }
    public IReadOnlyList<PedestrianSnapshot> Pedestrians { get; }
    public IReadOnlyList<BicycleSnapshot>    Bicycles    { get; }
    public IReadOnlyList<SignalSnapshot>     Signals     { get; }   // 停止线颜色由 Phase 决定（T3.1 a）
    public IReadOnlyList<LaneSnapshot>       Lanes       { get; }   // 排队/密度（T3.1/T3.4）
    public bool  Collision                         { get; }   // 本局是否已发生碰撞（T2.2 c）
    public ulong Hash                              { get; }   // == StateHash()
}
```

| 快照 | 字段语义 |
| --- | --- |
| `VehicleSnapshot.Position` | 沿 `LaneId` 从起点算起的距离（米）；渲染用 `nodes[from/to]` 的坐标插值 |
| `VehicleSnapshot.Stopped` | 当前是否处于停车状态（速度≈0） |
| `SignalSnapshot.Phase` | 绿 / 黄 / 全红 / 绿闪 → 停止线横条颜色（T3.1 a） |
| `LaneSnapshot.QueueLength` | 该车道排队车辆数 |
| `LaneSnapshot.Occupancy` | 该车道占有率 ∈ [0,1]；> `SimConstants.SpillbackOccupancy` 即溢出 |

> **`SimState` 刻意不含指标（R/D/P/Q）**：指标由 `Metrics` 层从事件流累计（§1.2 依赖方向）。表现层 HUD 取指标的路径见 §6.4。

### 4.5 `SignalStateMachine` —— 信号状态机（T1.2）

```csharp
namespace SimCore.Sim;

public sealed class SignalStateMachine
{
    public SignalStateMachine(Node node, SignalTiming timing);

    public int                NodeId         { get; }
    public int                PlanIndex      { get; }   // 当前生效方案（按 SimTime 选时段）
    public int                StageIndex     { get; }
    public SignalPhaseState   Phase          { get; }   // Green / Yellow / AllRed / GreenFlash
    public double             TimeInPhase    { get; }
    public double             PhaseRemaining { get; }
    public double             CycleLength    { get; }   // Σ(绿灯) + Σ(黄 + 全红 + 绿闪)
    public Stage              CurrentStage   { get; }

    public void SyncToTime(double simTime);              // 依 node.Offset 对齐初始相位（T1.2 d）
    public void ApplyPlan(SignalPlan plan);              // 下一周期生效（T1.2 c）
    public void Tick(double dt);                         // 推进一个步长
    public TurnPriority PriorityOf(int turnId);          // 当前相位下该转向优先级（透传 CurrentStage）
}
```

| 语义要点 | 说明 |
| --- | --- |
| 相位推进 | `绿 → 黄 → 全红 → 下一相位`；黄/全红期间放行掩码为空（全红清空，T1.2 b） |
| 黄 / 全红 / 绿闪时长 | 取自 `SignalTiming`；**不写入 `.spmap`** |
| `Phase` 掩码 | `Green`/`GreenFlash` 期间按 `CurrentStage.PriorityOf` 放行；`Yellow`/`AllRed` 期间全部 `Banned` |
| 改配时 | 仅当运行中修改时"下一周期生效"，不打断当前相位（T1.2 c） |
| `offset` | `SyncToTime` 按 `node.Offset` 平移初始相位（T1.2 d） |

### 4.6 `IntersectionGate` —— 路口门控（T1.3）

```csharp
namespace SimCore.Sim;

public sealed class IntersectionGate
{
    public static IntersectionGate Build(CompiledNetwork network, ConflictMatrix conflicts);

    public bool IsTurnOpen(int nodeId, int turnId, Stage stage);   // 相位掩码：Protected/Yield→true；Banned→false
    public bool HasProtectedConflict(int nodeId, Stage stage);     // 相位内 protected 集合存在冲突对→true（告警，T1.3 b）
}
```

- **语义**：门控 = "相位掩码 ∧ 冲突仲裁 ∧ 目标车道容量"。`SimEngine` 内部对每辆车做进入决策：仅当 `IsTurnOpen` 为真、且与已放行的 `Protected` 流向不冲突（`Yield` 须让行 `Protected`，T1.3 d）、且**目标车道有空位**（T1.3 e）时准入。
- **前置**：`Build` 的 `network`/`conflicts` 非 null，且二者同源（同一路网）；否则抛异常。
- **对应验收**：T1.3 b) 相位内两个 `Protected` 且冲突的转向 → `HasProtectedConflict` 报冲突（供 T3.3 c 的红色警示，编辑器/UI 不得自行判定）。
- **准入判定为 `internal`**：完整门控语义 = 相位掩码 ∧ 冲突仲裁 ∧ 目标车道容量；其中"逐车进入决策"落地为 `internal bool CanEnter(int nodeId, int turnId, Stage stage, IReadOnlyList<int>? occupiedTurnIds, bool targetLaneHasSpace)`（仅 `SimCore` 程序集内可见；单测经 `InternalsVisibleTo` 访问），**刻意不进冻结 public 面**。若编辑器 / 表现层需直接调用，走 §11 变更流程提升为 public。（2026-10-10 M1 审查补认）

### 4.7 `SignalTiming` —— 清空时间与配时建议

```csharp
namespace SimCore.Sim;

public sealed class SignalTiming
{
    public static SignalTiming Default { get; }                        // = new(3.0, 2.0, 0.0)

    public SignalTiming(double yellowTime, double allRedTime, double greenFlashTime = 0d);
    public double YellowTime     { get; }
    public double AllRedTime     { get; }
    public double GreenFlashTime { get; }

    [表现层] public static double ClearanceTime(CompiledNetwork network, int nodeId);      // 清空时间建议（秒）
    [表现层] public static double SuggestedYellow(double approachSpeed, double reactionTime, double decel);
    [表现层] public static double SuggestedAllRed(CompiledNetwork network, int nodeId, double crossingSpeed);
}
```

- **语义**：依路口宽度 / 过街距离估算黄灯与全红建议值（§3.2 附注：`SignalTiming` 按 `ClearanceTime` 估算）。
- **前置**：`yellowTime ≥ 0`、`allRedTime ≥ 0`、`greenFlashTime ≥ 0`；否则 `ArgumentOutOfRangeException`。
- **对应验收**：T3.3 d) "建议清空时间由 SimCore 计算"——UI 必须调用本类型，不得自算。

### 4.8 `DemandGenerator` —— 需求生成器（T1.7）

```csharp
namespace SimCore.Sim;

public enum DemandKind { Driving, Pedestrian, Bicycle }

public sealed class DemandGenerator
{
    public DemandGenerator(Level level, long seed);

    public double RateAt(DemandKind kind, int targetId, double time);                       // 分段常值查表（辆/秒）
    public int    DrawArrivals(DemandKind kind, int targetId, double time, double dt);       // 本步到达数（确定性）
    public void   Reset(long seed);
}
```

| 语义要点 | 说明 |
| --- | --- |
| 三类曲线（恒定/时变/脉冲） | **统一**由 `(time_points, flow_rates)` 分段常值表达；**脉冲 = 中段高、两侧 0**（Gate 0 口径） |
| `RateAt` | 返回 `time` 所在时段的流率；`time` 早于首个 `time_points` → 0 |
| `DrawArrivals` | 由 `(Seed, kind, targetId, time)` **确定性**决定，**不得**用系统随机数（铁律 2） |
| `targetId` | `Driving`→车道 id；`Pedestrian`→`Crosswalk` 转向 id；`Bicycle`→非机动车道 id |
| 前置 | `time`/`dt` 非负；否则抛异常 |

- **对应验收**：T1.7 a) 恒定流率误差 <1%；b) 时变按 `time_points` 切换；c) 脉冲只在中段生效；d) 同 seed 注入序列完全一致。

### 4.9 `SimEvent` —— 仿真事件（跨 `Sim` / `Metrics` 契约）

```csharp
namespace SimCore.Sim;

public enum SimEventKind { DemandArrived, VehiclePassed, VehicleStopped, QueueSample, Collision, Spillback }

public readonly record struct SimEvent(SimEventKind Kind, double Time, int LaneId, int VehicleId, double Value);
```

| 字段 | 语义 |
| --- | --- |
| `Kind` | 事件种类；`Collision` 由门控在检出碰撞时发出 |
| `Time` | 仿真时刻（秒） |
| `LaneId` | 相关车道（`Collision` 时为冲突转向的源车道；无车道时 = -1） |
| `VehicleId` | 相关车辆（无则 = -1） |
| `Value` | 事件的量（如 `QueueSample` 的排队长度、`Spillback` 的占有率） |

- `SimEngine.DrainEvents()` 产出的事件序列**顺序确定、可复现**。
- `Metrics` 层只依赖 `SimEvent`（§1.2：Metrics → Sim），**不从 `SimState` 反查指标**。

---

## 5. `SimCore.Metrics`（M2 · 待实现，签名冻结）

> 位置：`SimCore/Metrics/*.cs`。依赖 `Sim`（`SimEvent`）与 `Model`。

### 5.1 `TrafficClass` / `ApproachMetrics` / `ClassMetrics`

```csharp
namespace SimCore.Metrics;

public enum TrafficClass { Driving, Pedestrian, Bicycle }

public readonly record struct ClassMetrics(TrafficClass Class, double R, double D, double P, double QNormalized);

public readonly record struct ApproachMetrics(int NodeId, int RoadId, double Delay, int QueueLength, double R, double P);
```

- `ClassMetrics`：三类交通各自的指标（用于 T2.1 d 的**木桶**与归因）。
- `ApproachMetrics`：逐**进口道**统计（供 T2.2 d "定位最差进口" 与 T3.4 c 的归因句）。`RoadId` 取自 `Lane.Road`。

### 5.2 `Metrics` —— 指标快照

```csharp
namespace SimCore.Metrics;

public sealed class Metrics
{
    public double R { get; }   // 通行服务率 ∈ [0,1]（越大越好）
    public double D { get; }   // 平均延误（秒，越小越好）
    public double P { get; }   // 平均停车次数（行人不计，越小越好）
    public double Q { get; }   // 归一后最长排队 ∈ [0,1]，三类取最低（木桶，越小越好）
    public bool   Spillback { get; }              // 是否触发排队溢出（首车道占有率 > 0.9）
    public bool   Collision { get; }              // 本局是否发生碰撞（T2.2 c）
    public IReadOnlyList<int> CollisionTurns { get; }        // 碰撞涉及的转向 id（标出冲突相位）
    public IReadOnlyList<ClassMetrics>    Classes   { get; } // 机动车/行人/非机动车
    public IReadOnlyList<ApproachMetrics> Approaches { get; }// 逐进口
}
```

> §3.1 记作 `Metrics{R,D,P,Q,spillback}`；本契约**最小新增** `Collision / CollisionTurns / Classes / Approaches`，用于满足 T2.2 c/d。仅新增、不改既有字段语义。

**四项指标口径（对应 T2.1 验收，**不得自创定义**）**

| 指标 | 口径 |
| --- | --- |
| `R` | 通行服务率 = 通过数 / 需求数（分类加权：机动车/行人/非机动车） |
| `D` | 平均延误 = 实际通行时间 − 自由流通行时间的均值（秒） |
| `P` | 平均停车次数；**仅统计机动车与非机动车，行人不计**（T2.1 c） |
| `Q` | 各车道排队按**容量百分比归一**后，取**机动车 / 行人 / 非机动车三类的最低**（木桶） |

### 5.3 `MetricsCollector` —— 指标累计器

```csharp
namespace SimCore.Metrics;

public sealed class MetricsCollector
{
    public MetricsCollector(CompiledNetwork network);

    public void    Record(in SimEvent e);                       // §3.1 Record(...)
    public void    RecordRange(IReadOnlyList<SimEvent> events); // 批量（消费 SimEngine.DrainEvents()）
    public Metrics Snapshot();                                  // §3.1 Snapshot() -> Metrics
    public void    Reset();
}
```

- **语义**：逐事件累计，`Snapshot()` 返回当前累计指标（只读、可重复调用）。
- **前置**：`Record` 的 `LaneId` 越界→忽略该事件并保持确定性（或按实现抛异常——二者取一，由 M2 在单测中固定并各卡遵守）；`network==null`→`ArgumentNullException`。
- **对应验收**：T2.1 a)~e) 全部由本类型单测覆盖。

### 5.4 `[表现层]` 实时指标取用路径（供 T3.4 HUD）

```
每帧：  engine.DrainEvents()  →  collector.RecordRange(events)  →  collector.Snapshot()  →  HUD 数值
结算：  Scorer.Evaluate(level, collector.Snapshot()) → Score → 星级/分解/归因
```

> 该路径是**表现层取指标的唯一方式**；`SimState` 不含指标（§4.4）。

---

## 6. `SimCore.Scoring`（M2 · 待实现，签名冻结）

> 位置：`SimCore/Scoring/*.cs`。依赖 `Io`（`Level`、`GameRules`）与 `Metrics`。

### 6.1 `MetricKind` / `ScoreBreakdown` / `Score`

```csharp
namespace SimCore.Scoring;

public enum MetricKind { R, D, P, Q }

public sealed class ScoreBreakdown
{
    public double R { get; }   // ∈ [0,1]：R 越大越好（b→0分，g→1分）
    public double D { get; }   // ∈ [0,1]：D 越小越好（b→0分，g→1分）
    public double P { get; }   // ∈ [0,1]：P 越小越好
    public double Q { get; }   // ∈ [0,1]：Q 越小越好
    public double Total { get; } // = Σ w_i * s_i，权重取自 game_rules.weights
}

public sealed class Score
{
    public double Total        { get; }   // ∈ [0,1] 加权总分
    public int    Stars        { get; }   // 0..3
    public ScoreBreakdown Breakdown { get; }
    public bool   HasCollision { get; }              // 碰撞 → 0 星（T2.2 c）
    public IReadOnlyList<int> CollisionTurns { get; }// 标出的冲突转向
    public int    WorstApproachNode { get; }         // 最差进口所在节点（-1 表示无）
    public int    WorstApproachRoad { get; }         // 最差进口道路 id（-1 表示无）
    public MetricKind WorstMetric { get; }           // 最弱项
}
```

### 6.2 `Scorer`

```csharp
namespace SimCore.Scoring;

public static class Scorer
{
    [表现层] public static Score Evaluate(Level level, Metrics metrics);          // §3.1 字面
    [表现层] public static string[] Attribution(Score previous, Score current);   // §3.1 字面（归因文案）
}
```

**`Evaluate` 语义**

| 项 | 规则 |
| --- | --- |
| 单项分（R） | `R ≤ b → 0`；`R ≥ g → 1`；其间**线性插值**（越大越好） |
| 单项分（D/P/Q） | `D ≥ b → 0`；`D ≤ g → 1`；其间**线性插值**（越小越好） |
| 边界 | `=g` → 1 分；`=b` → 0 分（T2.2 b） |
| 越界 | 单项分一律 `clamp` 到 `[0,1]`（T2.2 a） |
| 权重 / 锚点 | **全部从 `level.GameRules` 读取**，不得硬编码（T2.2 ④） |
| 碰撞 | `metrics.Collision` 为真 → `Total` 记 0、`Stars = 0`，并在 `CollisionTurns` 标出冲突转向（T2.2 c） |
| 星级 | 见下表（Gate 0 冻结默认，§10 D11） |
| `WorstApproach*` | 取 `metrics.Approaches` 中延误/排队最劣者（`Level` 用于回填节点语义，T2.2 d） |

**星级阈值（冻结默认；仅当需要按关卡配置时才属 schema 扩展）**

| `Total` 区间 | `Stars` |
| --- | --- |
| `> 1e-9 且 ≥ 0.95` | 3 ★ |
| `≥ 0.80` | 2 ★ |
| `≥ 0.60` | 1 ★ |
| `< 0.60` 或碰撞 | 0 ★ |

> **口径来源**：《游戏设计v2》§6.4 为 `≥95 / ≥80 / ≥60`（0–100 制）；本契约 `Score.Total` 定为 `[0,1]`，故阈值等价换算为 `0.95 / 0.80 / 0.60`。此换算关系**由 2026-10-10 老板裁定确认**（见 §10 D11），实现时**必须与设计文档一致，不得放宽**。

**`Attribution(prev, cur)` 语义**

- 返回**归因文案数组**（每项一句中文），至少包含：①最差进口 + 最弱项的定位句；②与上一局的差值句（`cur - prev`）；③碰撞说明句（若 `HasCollision`）。
- **前置**：`prev`/`cur` 非 null；否则 `ArgumentNullException`。首局无 `prev` 时由调用方传 `Evaluate(level, 零指标)` 或使用 `Score` 的空构造（由 M2 在单测中固定）。
- **`[表现层]`**：T3.4 c 要求 UI **只展示**这里产出的文案，不得自行拼句。

---

## 7. `SimCore.Io`（已冻结实现 · T0.3）

> 位置：`SimCore/Io/*.cs`。依赖 `Model`。**只做格式转换与校验，不含仿真逻辑。**

### 7.1 `Spmap` —— 读写入口

```csharp
namespace SimCore.Io;

public static class Spmap
{
    public const int    CurrentFormatVersion = 1;
    public const string FileExtension        = ".spmap";

    [表现层] public static Level Load(string path);                 // §3.1 字面
    [表现层] public static void  Save(Level level, string path);    // §3.1 字面
}
```

| 成员 | 语义 | 前置 / 异常 |
| --- | --- | --- |
| `Load` | 读 `.spmap` 为 `Level`（UTF-8，枚举按名） | `path` 空→`ArgumentException`；非法 JSON/枚举名→`JsonException`；IO 失败→`IOException` |
| `Save` | 把 `Level` 写为 `.spmap`（UTF-8 无 BOM，缩进 2 空格） | `level==null`→`ArgumentNullException`；`path` 空→`ArgumentException` |

### 7.2 `SpmapValidator` —— 校验器（游戏与编辑器同源共用）

```csharp
namespace SimCore.Io;

public static class SpmapValidator
{
    [表现层] public static ValidationError[] Validate(Level level);   // §3.1 字面
}
```

- **语义**：逐表遍历，返回**全部**错误与警告；**顺序固定、可复现**；**只读**，不修改传入 `Level`。
- **前置**：`level == null` → `ArgumentNullException`。
- **输出**：`ValidationError[]`，空数组 = 合法。
- **完整错误码表**：见 `docs/spmap-schema.md` §7.1（20 个稳定 `code`）。
- **对应验收**：T0.3 b) 已交付；T4.4 导出前必跑、Error 一律禁止导出；T4.2 b) 实时红字提示。

### 7.3 `ValidationError` / `ValidationSeverity`

```csharp
namespace SimCore.Io;

public enum ValidationSeverity { Warning, Error }

public sealed class ValidationError
{
    public ValidationError(string code, string message, string path,
                           ValidationSeverity severity = ValidationSeverity.Error);
    public string Code     { get; }   // 稳定错误码（小写下划线）
    public string Message  { get; }   // 中文说明
    public string Path     { get; }   // JSON 位置，如 turns[7].src_lane
    public ValidationSeverity Severity { get; }
    public bool IsError    { get; }   // Severity == Error
    public override string ToString(); // "[Error] code @ path: message"
}
```

### 7.4 `Level` / `LevelMeta` / `TurnRatio` —— 关卡装配层

```csharp
namespace SimCore.Io;

public sealed class Level
{
    public int       FormatVersion { get; init; } = Spmap.CurrentFormatVersion;
    public LevelMeta Meta          { get; init; } = new();
    public Network   Network       { get; init; } = new();     // nodes / lanes / turns（+ 节点信号属性）
    public IReadOnlyList<TurnRatio>         TurnRatios  { get; init; } = Array.Empty<TurnRatio>();
    public IReadOnlyList<DemandPlan>        Demand      { get; init; } = Array.Empty<DemandPlan>();
    public IReadOnlyList<PedestrianDemand>  Pedestrians { get; init; } = Array.Empty<PedestrianDemand>();
    public IReadOnlyList<BicycleDemand>     Bicycles    { get; init; } = Array.Empty<BicycleDemand>();
    public GameRules GameRules { get; init; } = new();

    public double TurnRatioOf(int turnId);   // 无双向匹配→1.0
}

public sealed class LevelMeta
{
    public string Name   { get; init; } = string.Empty;   // meta.name
    public string Author { get; init; } = string.Empty;   // meta.author
    public int    Chapter { get; init; }                  // meta.chapter，1 起
    public int    Index   { get; init; }                  // meta.index，1 起
}

public readonly record struct TurnRatio(int Turn, double Ratio);   // turns[].ratio，须 ∈ (0,1]
```

**信号落点（规范形，冻结）**：`.spmap` 的 `signals[]`（以 `node` 为主键）↔ 内存的 `Node.Offset` / `Node.SignalPlans`。

- **写**：某节点满足「`IsSignalized` 或 `Offset ≠ 0` 或 `SignalPlans` 非空」三者之一，即写出一条 `signals[]`；否则不写。
- **读**：按 `node` id 回挂到同 id 节点；同节点多条记录**后者覆盖**（非法输入，校验器另报）。
- 由此保证：任意合法关卡 `保存 → 加载` 后**逐字段等价**（T0.3 验收 a）。

### 7.5 三类需求与 `BicycleMode`

```csharp
namespace SimCore.Io;

public sealed class DemandPlan                           // demand[]
{
    public int Lane { get; init; }
    public IReadOnlyList<double> TimePoints { get; init; } = Array.Empty<double>();  // 严格递增
    public IReadOnlyList<double> FlowRates  { get; init; } = Array.Empty<double>();  // 与 TimePoints 等长、非负
}

public sealed class PedestrianDemand                     // pedestrians[]
{
    public int CrosswalkTurn { get; init; }              // 须为 Crosswalk 转向
    public IReadOnlyList<double> TimePoints { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> FlowRates  { get; init; } = Array.Empty<double>();
}

public enum BicycleMode { Independent, Mixed }           // §10 D6 裁定：冻结

public sealed class BicycleDemand                        // bicycles[]
{
    public int Lane { get; init; }
    public BicycleMode Mode { get; init; } = BicycleMode.Independent;
    public IReadOnlyList<double> TimePoints { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> FlowRates  { get; init; } = Array.Empty<double>();
}
```

> 三类曲线均为**分段常值时变曲线**：`time_points` 严格递增、`flow_rates` 非负且与 `time_points` 等长。恒定/时变/脉冲统一用该结构表达（脉冲 = 中段高、两侧 0）。

### 7.6 `GameRules` / `MetricAnchors` / `MetricAnchorValue` / `MetricWeights`

```csharp
namespace SimCore.Io;

public sealed class GameRules                            // game_rules
{
    public MetricAnchors Anchors  { get; init; } = new();
    public MetricWeights Weights  { get; init; } = MetricWeights.Default;
    public JsonNode?     Events   { get; init; } = new JsonArray();   // 原样承载任意 JSON
    public JsonNode?     Tutorial { get; init; } = new JsonArray();   // 原样承载任意 JSON
}

public readonly record struct MetricAnchorValue(double Bottom, double Good)   // [b, g]
{
    public static MetricAnchorValue Unset { get; }
    public bool IsUnset { get; }                          // b==0 且 g==0
}

public sealed class MetricAnchors
{
    public MetricAnchorValue R { get; init; }   // 越大越好（g ≥ b）
    public MetricAnchorValue D { get; init; }   // 越小越好（g ≤ b）
    public MetricAnchorValue P { get; init; }
    public MetricAnchorValue Q { get; init; }
    public bool AllUnset { get; }
}

public sealed class MetricWeights
{
    public double R { get; init; }   // 默认 0.4
    public double D { get; init; }   // 默认 0.2
    public double P { get; init; }   // 默认 0.2
    public double Q { get; init; }   // 默认 0.2
    public static MetricWeights Default { get; }
    public double Sum { get; }
}
```

- `anchors.{R,D,P,Q}` 为二元数组 `[b, g]`：`b` = 底线值（0 分）、`g` = 优秀值（满分）。
- `events` / `tutorial` **不臆造字段**，以 `JsonNode` 原样无损往返。
- 依赖方向：`Scoring` 消费 `Anchors/Weights`；`Sim` **不消费** `game_rules`。

---

## 8. `.spmap` 数据格式（Gate 0 组成）

`.spmap` 的完整规范见 **`docs/spmap-schema.md` v1.0**（随本契约一并冻结）。要点：

| 项 | 规定 |
| --- | --- |
| 顶层 10 键 | `format_version / meta / nodes / lanes / turns / signals / demand / pedestrians / bicycles / game_rules` |
| 字段命名 | 小写下划线，与技术方案03 §3.2 **逐字一致** |
| 编码 | UTF-8（无 BOM）；缩进 2 空格 |
| 版本 | `format_version = 1`；不匹配 → `format_version_unsupported` |
| `stages[].duration` | **绿灯时长** > 0；黄/全红/绿闪不写入文件（由 `SignalTiming` 估算） |
| `turns[].ratio` | **可选**扩展，缺省 1.0；`(0,1]`，值为 1.0 不写入 |
| `bicycles[].mode` | `Independent` / `Mixed` |
| `events` / `tutorial` | 原样承载任意 JSON |
| 校验 | 20 个稳定错误码，见 schema §7.1 |

> 本契约与 schema **不得分叉**：若将来改 schema，两文档走同一次 §11 变更。

---

## 9. 覆盖矩阵（验收 a 的证据：M1~M4 每张卡都能找到所需方法）

> 每行给出：任务卡 → 该卡**必须用到**的契约符号 → 契约位置。**全部命中即满足验收 a**。

### 9.1 M1 · 仿真核

| 卡 | 所需符号 | 契约位置 |
| --- | --- | --- |
| **T1.1** 车辆状态机 + 车道队列 + 跟驰 | `Lane / Turn / Network`；`SimEngine`；`SimConstants`（`SaturationFlowPerLane`/`StartUpLostTime`/`VehicleLength`/`MinHeadway`）；`VehicleSnapshot`；`LaneSnapshot`；`SimEventKind.VehicleStopped`；`SimEventKind.QueueSample` | §2.3/2.4/2.7、§4.1、§4.3、§4.4、§4.9 |
| **T1.2** 信号状态机与相位推进 | `SignalStateMachine`；`SignalPhaseState`；`SignalTiming`；`Stage`；`SignalPlan`；`SimEngine.ApplyPlan`；`Node.Offset` | §4.5、§4.7、§4.3、§2.2、§2.6 |
| **T1.3** 冲突矩阵 + 路口门控（核心） | `ConflictMatrix`（`From`/`AreConflicting`/`ConflictsOf`）；`IntersectionGate`（`IsTurnOpen`/`HasProtectedConflict`）；`SimEventKind.Collision`；`SimState.Collision`；`TurnPriority` | §3.1、§4.6、§4.9、§4.4、§2.1 |
| **T1.4** 行人模型（等齐过街） | `PedestrianDemand`；`PedestrianSnapshot`；`DemandKind.Pedestrian`；`TurnType.Crosswalk`；`SimConstants.PedestrianSpeed`；`IntersectionGate` | §7.5、§4.4、§4.8、§2.1、§4.1、§4.6 |
| **T1.5** 非机动车模型（独立/混行） | `BicycleMode`；`BicycleDemand`；`BicycleSnapshot`；`DemandKind.Bicycle`；`SimConstants.BicycleStartDelay` | §7.5、§4.4、§4.8、§4.1 |
| **T1.6** 确定性步进 + 加速一致性（核心） | `SimEngine`（`Step`/`Advance`/`Reset`/`Snapshot`/`StateHash`）；`SimConfig`（`Dt`/`Deterministic`/`Seed`/`SpeedMultiplier`）；`SimState.Hash` | §4.3、§4.2、§4.4 |
| **T1.7** 需求生成器（恒定/时变/脉冲） | `DemandGenerator`（`RateAt`/`DrawArrivals`）；`DemandKind`；`DemandPlan` | §4.8、§7.5 |

### 9.2 M2 · 评分

| 卡 | 所需符号 | 契约位置 |
| --- | --- | --- |
| **T2.1** 指标统计 R/D/P/Q + 溢出 | `MetricsCollector`（`Record`/`RecordRange`/`Snapshot`）；`Metrics`（`R/D/P/Q/Spillback/Classes/Approaches`）；`SimEvent`；`SimEventKind`；`SimConstants.SpillbackOccupancy` | §5.3、§5.2、§4.9、§4.1 |
| **T2.2** 评分公式 + 星级 + 上局归因（Gate 2 ★） | `Scorer.Evaluate`；`Scorer.Attribution`；`Score`；`ScoreBreakdown`；`MetricKind`；`MetricAnchors`；`MetricWeights`；`Metrics.Collision/CollisionTurns/Approaches` | §6.2、§6.1、§7.6、§5.2 |

### 9.3 M3 · 游戏表现层（GDScript）

| 卡 | 所需符号（`[表现层]`） | 契约位置 |
| --- | --- | --- |
| **T3.1** 渲染层（极简几何色块） | `SimState` 全部快照；`Lane`/`Node` 几何；`SignalPhaseState` | §4.4、§2.2/2.3、§4.4 |
| **T3.2** 相机/输入/时间控制（1~6 档、暂停） | `SimEngine.SpeedMultiplier`；`SimEngine.Advance`；`SimEngine.IsFinished` | §4.3 |
| **T3.3** 相位控制面板（基础/高级） | `ConflictMatrix.AreConflicting`；`IntersectionGate.HasProtectedConflict`；`SignalTiming.ClearanceTime`；`SimState.Signals`；`SimEngine.ApplyPlan` | §3.1、§4.6、§4.7、§4.4、§4.3 |
| **T3.4** HUD + 结算界面 | `MetricsCollector`/`Metrics`（实时四项）；`Scorer.Evaluate`；`Scorer.Attribution`；`Score` | §5.3/5.4、§6.2、§6.1 |
| **T3.5** 关卡流程 + 存档 + 解锁 | `Spmap.Load/Save`；`Level.Meta`；`LevelMeta.Chapter/Index` | §7.1、§7.4 |
| **T3.6** 反馈特效与音效（Gate 3 ★） | `SimState`（车辆位置 / 信号相位，供绿波/排队动效）；`SimConstants` | §4.4、§4.1 |

### 9.4 M4 · 地图编辑器（GDScript + 复用 SimCore）

| 卡 | 所需符号（`[表现层]`） | 契约位置 |
| --- | --- | --- |
| **T4.1** 编辑器骨架 + 网格几何绘制 | `Network`/`Node`/`Lane`/`Turn`；`Spmap.Save`（中间态） | §2、§7.1 |
| **T4.2** 自动推导 + 实时校验（核心） | `TurnGenerator.FromGeometry`；`PhaseAutoGenerator.Suggest`；`ConflictMatrix`；`SpmapValidator.Validate` | §3.2、§3.3、§3.1、§7.2 |
| **T4.3** 需求配置 + 试玩直通 | `SimEngine(Level, SimConfig)`；`DemandPlan`/`PedestrianDemand`/`BicycleDemand`；`BicycleMode` | §4.3、§7.5 |
| **T4.4** `.spmap` 导入导出（Gate 4 ★） | `Spmap.Load/Save`；`SpmapValidator.Validate`；`Spmap.CurrentFormatVersion` | §7.1、§7.2、§7.1 |

### 9.5 M5 / M6（顺带覆盖，非本 Gate 门禁）

| 卡 | 所需符号 | 契约位置 |
| --- | --- | --- |
| **T5.1** 关卡生成器 | `Spmap.Save`；`SpmapValidator.Validate` | §7.1、§7.2 |
| **T5.2** LightSim 离线校准器 | `Spmap.Load`；`GameRules`；`MetricAnchors` | §7.1、§7.6 |
| **T6.1 / T6.2** 集成与打包 | `Spmap.CurrentFormatVersion`（版本一致性） | §7.1 |

> **结论**：M1~M4 全部 **19 张卡**（M1 7 + M2 2 + M3 6 + M4 4）的所需符号**均在契约中可查**，无遗漏。

---

## 10. Gate 0 裁定记录（对 T0.2/T0.3 待裁定项的裁决）

| 编号 | 事项 | 裁定 | 依据 |
| --- | --- | --- | --- |
| **D1** | SimCore 目标框架 / 依赖 | `net8.0`；零 Godot、零第三方 | 铁律 1；§1.1 |
| **D2** | 枚举名与顺序 | 与 §3.1 **逐字逐序**，不得改 | §2.1 |
| **D3** | `CompiledNetwork` 是否为正式类型 | **接受**：Model 的编译产物类型，签名冻结 | §2.8 |
| **D4** | `StageType { Fixed, Variable }` | **接受**：冻结枚举（支撑 `stages[].type`） | §2.1 |
| **D5** | `Movement` 语义（`(Node, Road, Type)` 分组） | **确认**：编译派生，不写入 `.spmap` | §2.5 |
| **D6** | `BicycleMode { Independent, Mixed }` | **接受**：`Mixed` 依游戏设计v2 §9 补 | §7.5 |
| **D7** | 信号落点（`signals[]` ↔ `Node.Offset/SignalPlans`） | **确认**：不新增 `TrafficSignal` 类型，规范形互转 | §7.4 |
| **D8** | `stages[].duration` 口径 | **确认**：= 绿灯时长；黄/全红/绿闪不入文件 | §2.6、§4.7 |
| **D9** | `SimEngine` 入参：`network` 还是 `level` | **两者并存**：`(CompiledNetwork, SimConfig)` 为字面形，`(Level, SimConfig)` 为装配层直通形；确立 `Sim → Io` 依赖边 | §4.3、§1.2 |
| **D10** | `turns[].ratio` 可选扩展 | **接受**：向后兼容（缺省 1.0、1.0 不写入）；理由 = 技术方案01 §3.6 + T0.3 验收 b 要求可检出"转向比例越界" | §7.4 |
| **D11** | 星级阈值 | **冻结默认**：3★≥0.95、2★≥0.80、1★≥0.60、否则 0★；碰撞=0★。**（2026-10-10 老板裁定：对齐《游戏设计v2》§6.4 的 95/80/60，修正初版误写的 0.85/0.60/0.30）** | §6.2、设计v2 §6.4 |
| **D12** | `Metrics` 新增字段（`Collision/CollisionTurns/Classes/Approaches`） | **接受**：满足 T2.2 c/d 与 T2.1 d 木桶所必需，仅新增不改语义 | §5.2 |
| **D13** | `Level/LevelMeta/TurnRatio/DemandPlan/PedestrianDemand/BicycleDemand/GameRules/MetricAnchors/MetricAnchorValue/MetricWeights/ValidationError/ValidationSeverity` 类型名 | **接受并冻结**：§3.1 未逐一列名，按 §3.2 语义最小新增 | §7 |
| **D14** | 新增 `SimCore.Sim` 协同类型（`SimConstants/SignalStateMachine/IntersectionGate/SignalTiming/DemandGenerator/SimEvent/SimState` 及快照）与 `SimCore.Geometry` 全量、`SimCore.Metrics`、`SimCore.Scoring` 全部签名 | **接受并冻结**：M1~M4 所需方法在此成文 | §3~§6、§9 |

> 以上裁定全部**只做"命名/语义定稿"，未改动 T0.2/T0.3 已交付代码**（仅新增文档）。若复核认为某项应改，走 §11 变更流程。

---

## 11. 变更流程（冻结后）

1. 提出"变更请求"：**改什么 / 为什么 / 影响哪些卡**（技术方案03 §8）。
2. 由**项目负责人审批**。
3. 通过后，**同一次**统一更新 `docs/SimCore-API.md` 与 `docs/spmap-schema.md`（若涉 schema），并通知所有依赖方的任务卡。
4. **不允许静默改接口**；集成阶段发现缺陷时，集成 AI 只写适配层并向上报告（技术方案03 §7）。

### 11.1 变更记录

| 日期 | 变更 | 起因 | 影响 |
| --- | --- | --- | --- |
| 2026-10-10 | D11 星级阈值 `0.85/0.60/0.30` → **`0.95/0.80/0.60`** | 老板裁定：对齐《游戏设计v2》§6.4 | §6.2 星级表、§10 D11、T0.4 报告；M2 未开工，零代码影响 |
| 2026-10-10 | §4.6 补认 `internal CanEnter`（不进 public 面）；附 B 补注 **10 个 M1 内部实现类型统一降为 `internal`** | M1 审查（`docs/T1-审查报告-M1.md` 问题 2/3） | 仅可见性收敛 + 文档补录；`dotnet build` 0 警告、`dotnet test` 173/173 不变 |
| 2026-10-20 | ① `lanes[]` 增可选 `lat_index`（`Lane.LatIndex`，缺省 `-1` = 未标、不写文件）；② `game_rules` 增可选 `vehicle_mix`（枚举 `VehicleMix`，缺省 `null` = `CarOnly`、不写文件） | T4.3 S1：`lane_index` 已被 `ConflictMatrix`/`Engine` 用作仿真几何输入（改语义会静默改变行人过街距离 3.5→7.0m），渲染排位需独立字段；车型构成需可校验落点（`docs/T4.3-编辑器补强执行计划.md`） | 纯增量、均可缺省；`01-1` 示例与全部旧关卡往返逐字段不变；`SpmapTests` 276 → **281 全绿**；`T1.x/T2.x` 基准零变化 |
| 2026-10-20 | ③ `VehicleKind` 末尾追加 `Truck`（`Car=0`、`Bus=1` 不变）；④ `SimConstants` 增 `TruckLength=7.0`、`Car/Truck/BusSpeedFactor=1.00/0.85/0.95`；⑤ 队列容量改吃「期望车头间距」，目标速度乘车型系数（内部 `internal VehicleMixRules`） | T4.3 S1b：设计 §9「货车——加减速更低、清空时间更长」的最小落地；车型只影响「车长 → 容量」「系数 → 行程速度」，不动饱和流率、放行掩码与路由 | 默认 `CarOnly` 期望间距恰为 6.0、系数 1.0 → **与改动前逐位一致**；`dotnet test` 281 → **296 全绿**（新增 `VehicleMixTests` 15 条 + `VehicleTests.E2` 枚举断言更新为 `Car/Bus/Truck`） |
| 2026-10-24 | ① `Model.Turn` 增 `Leg` / `IslandId`（缺省 `-1`）；② `Model.Network` 增 `Islands`；③ 新增 `Model.Island`（`Id/X/Y/Capacity`）；④ `spmap-schema.md` 增可选顶层 `islands[]`、`turns[].leg`、`turns[].island` 与 8 个校验码 | E92 G2「二次过街 / 中央安全岛」：要让两段能被**不同相位分别放行**，必须是两条独立 `Crosswalk` 转向；方案经老板 2026-10-24 选 C（真做两段相位） | 纯增量、全部可缺省：无 `islands`/`leg` 的关卡往返**逐字节不变**；`dotnet test` 298 → **313 全绿**（新增 `IslandTests` 15 条）。**本批只做数据层**，行人两段推进（`PedestrianCrossing`）与编辑器 UI 在后续批次 |
| 2026-10-25 | ① `PedestrianState` 在 `Crossing` 与 `Crossed` 之间**插入** `OnIsland`（`Waiting=0`、`Crossing` 序数不变）；② 新增 public 枚举 `PedestrianLegPlace { AtOrigin=0, OnIsland=1, AtDestination=2 }`；③ `Pedestrian` 新增 public 成员：五参构造、`IslandId` / `IslandPosition` / `IsTwoLeg` / `FirstLegDistance` / `SecondLegDistance` / `Place` / `MinGreenTimeForFirstLeg` / `IsOnIsland` / `IslandArriveTime` / `IsOnSecondLeg`。（`PedestrianCrossing` 是 `internal`，其新增成员**不进** public 面） | E92 G2 · N6a2「行人两段推进」：前段走到岛 → 岛上等 → 后段放行 → 走完；岛容量有限，前段途中的人也算占位 | **纯增量**：一次走完的过街道（`Leg = -1`）行为与改动前逐位一致（老四参构造保留、`IslandId = -1`）；`dotnet test` 313 → **330 全绿**（新增 `TwoLegPedestrianTests` 17 条）。⚠ **`PedestrianSnapshot` 刻意未加 `LegIndex` / `OnIsland`** —— HUD 当前无消费方，加了只会撑大 §4.4 冻结面；真需要时另走一次审批。⚠ 注意 `OnIsland` **插在 `Crossing` 与 `Crossed` 之间**（不是追加末尾），`Crossed` 的序数因此 +1：全库检索确认当前只有符号比较（`PedestrianState.Crossed` 共 2 处，均在测试里），**无序数强转依赖**；将来若有人写 `(int)State >= 2` 之类会踩 |
| 2026-10-20 | ⑥ `VehicleMix` 末尾追加 `CarBusMix`（小汽车 92% / 公交 8%，期望间距 6.60 m、容量系数 0.91） | 老板追加需求：需要「只有小汽车 + 少量公交」的城市常规断面；现有 `BusHeavy`（公交 20%）对通过性影响过大（容量 −23%） | 纯末尾追加，`CarOnly/CityMix/FreightHeavy/BusHeavy` 序数与名字不变，旧文件解析不受影响；`dotnet test` 296 → **298 全绿**（`A1` / `A3` 各加一条预设） |

---

## 附 A. 枚举速查（`.spmap` 序列化名）

| 枚举 | 取值（按序） | 出现位置 |
| --- | --- | --- |
| `NodeType` | `Signalized, Unsignalized, Origin, Destination` | `nodes[].type` |
| `LaneType` | `Driving, Bus, Biking, Sidewalk, SharedLeftTurn, Construction, Parking` | `lanes[].type` |
| `TurnType` | `Straight, Left, Right, UTurn, Crosswalk` | `turns[].type` |
| `StageType` | `Fixed, Variable` | `stages[].type` |
| `BicycleMode` | `Independent, Mixed` | `bicycles[].mode` |
| `VehicleMix` | `CarOnly, CityMix, FreightHeavy, BusHeavy, CarBusMix` | `game_rules.vehicle_mix`（可选，缺省不写 = `CarOnly`） |
| `TurnPriority` | `Banned, Yield, Protected` | **不写入**（由相位派生） |
| `ValidationSeverity` | `Warning, Error` | 校验结论 |
| `SignalPhaseState` | `Green, Yellow, AllRed, GreenFlash` | 运行期（不入文件） |
| `VehicleKind` | `Car, Bus, Truck` | 运行期（不入文件） |
| `DemandKind` | `Driving, Pedestrian, Bicycle` | 运行期（不入文件） |
| `TrafficClass` | `Driving, Pedestrian, Bicycle` | 运行期（不入文件） |
| `MetricKind` | `R, D, P, Q` | 运行期（不入文件） |
| `SimEventKind` | `DemandArrived, VehiclePassed, VehicleStopped, QueueSample, Collision, Spillback` | 运行期（不入文件） |

## 附 A2. `Island`（2026-10-24 新增，`SimCore.Model`）

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `Id` | `int Id { get; init; }` | 安全岛 id（`islands[].id`，关卡内唯一） |
| `X` / `Y` | `double X/Y { get; init; }` | 中心点（世界米，与 `Node` 同坐标系；只给渲染/几何校验用，不进仿真） |
| `Capacity` | `int Capacity { get; init; }` | 可同时容纳行人数（`≥ 1`） |

> 用途：承载"行人二次过街"的中央等待区（游戏设计v2 §5.3「两段式行人相位」）。
> 分段过街 = 两条 `Crosswalk` 转向共用同一对 `SrcLane/DstLane`、带同一个 `IslandId`、用 `Leg` 区分前后段。

## 附 B. SimCore public 类型总览（冻结面）

| 命名空间 | 类型 |
| --- | --- |
| `SimCore` | `SimCoreInfo`（骨架自检，**不属冻结接口**） |
| `SimCore.Model` | 枚举 `LaneType/TurnType/TurnPriority/NodeType/StageType`；`Node/Lane/Turn/Island/Movement/Stage/SignalPlan/Network/CompiledNetwork` |
| `SimCore.Geometry` | `ConflictMatrix/TurnGenerator/PhaseAutoGenerator` |
| `SimCore.Sim` | `SimConstants/SimConfig/SimEngine/SimState` + 快照 `VehicleSnapshot/PedestrianSnapshot/BicycleSnapshot/SignalSnapshot/LaneSnapshot`；`SignalStateMachine/IntersectionGate/SignalTiming/DemandGenerator/SimEvent`；枚举 `VehicleKind/SignalPhaseState/DemandKind/SimEventKind` |
| `SimCore.Metrics` | `MetricsCollector/Metrics`；`ClassMetrics/ApproachMetrics`；枚举 `TrafficClass` |
| `SimCore.Scoring` | `Scorer/Score/ScoreBreakdown`；枚举 `MetricKind` |
| `SimCore.Io` | `Spmap/SpmapValidator/Level/LevelMeta/TurnRatio/DemandPlan/PedestrianDemand/BicycleDemand/GameRules/MetricAnchors/MetricAnchorValue/MetricWeights/ValidationError`；枚举 `BicycleMode/VehicleMix/ValidationSeverity` |

> **M1 内部实现类型（`internal`，非冻结面，可自由重构）**：仿真核的微观实现类型**不属于契约面**，已按 2026-10-10 M1 审查统一降为 `internal` —— `CarFollowing`、`Queue`、`Vehicle`、`VehicleState`、`Pedestrian`、`PedestrianState`、`PedestrianCrossing`、`Bicycle`、`BicycleState`、`BicycleFlow`；T4.3 S1b 新增 `VehicleMixRules`（同属 internal）。
> 表现层 / 编辑器**只读 `SimState` 快照**，不依赖这些类型；如需对它们编程，走 §11 变更流程（提升可见性或改经快照）。**上表即 SimCore 的完整 public 面；表外一切类型均为 `internal`。**
