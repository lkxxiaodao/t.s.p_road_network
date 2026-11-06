# `.spmap` 地图格式规范 v1

> 版本：v1.0
> 日期：2026-10-10
> 任务：技术方案03 §5 · T0.3（角色：格式与校验工程师）
> 依据：技术方案03 §3.2（顶层结构，唯一真相）、§3.1（`SimCore.Io` API）；技术方案01 §3.3/§3.6；游戏设计v2 §12/§13.2
> 实现：`SimCore/Io/*.cs`（C#，`System.Text.Json`）
> 状态：**已随 Gate 0 一并冻结（FROZEN，2026-10-10）**。本文档与技术方案03 §3.2 一致，并被 `docs/SimCore-API.md` §8 以引用方式纳入 Gate 0 冻结范围；后续变更走 `SimCore-API.md` §11 变更流程。

---

## 1. 概述

`.spmap` 是《信号规划师》的**关卡地图文件格式**：自定义扩展名，内容即"关卡 JSON + `format_version`"（游戏设计v2 §13.2）。
游戏与地图编辑器**共享同一份 schema 与同一套校验**（技术方案01 §5、游戏设计v2 §13.2/§13.3），只通过该文件耦合——游戏构建零编辑器代码。

一句话：**五张拓扑表（nodes/lanes/turns/signals/demand）+ 游戏扩展（pedestrians/bicycles/game_rules），全部用 id 互相引用。**

---

## 2. 文件约定

| 项 | 规定 |
| --- | --- |
| 扩展名 | `.spmap` |
| 编码 | UTF-8（无 BOM） |
| 内容 | 单个 JSON 对象（顶层） |
| 字段命名 | **一律小写下划线**，与技术方案03 §3.2 **逐字一致** |
| 缩进 | 建议 2 空格（读写实现写缩进 JSON；读不敏感） |
| 版本 | `format_version`（当前 = `1`）；版本不匹配由校验器报错，游戏/编辑器给出明确提示（技术方案03 T4.4-c） |

> **读写实现**：`SimCore.Io.Spmap`（`System.Text.Json`）。枚举一律按**名称字符串**序列化（如 `"Signalized"`、`"Driving"`、`"Fixed"`）。

---

## 3. 顶层结构总览（技术方案03 §3.2）

```jsonc
{
  "format_version": 1,
  "meta":  { "name": "...", "author": "...", "chapter": 1, "index": 1 },
  "nodes": [ { "id":0, "type":"Signalized", "x":0, "y":0 } ],
  "lanes": [ { "id":0, "road":0, "type":"Driving", "from_node":1, "to_node":0,
               "length":180, "speed_limit":13.89, "lane_index":0 } ],
  "turns": [ { "id":0, "src_lane":0, "dst_lane":4, "node":0, "type":"Straight" },
             { "id":12, "src_lane":8, "dst_lane":9, "node":0, "type":"Crosswalk", "leg":0, "island":0 },
             { "id":13, "src_lane":8, "dst_lane":9, "node":0, "type":"Crosswalk", "leg":1, "island":0 } ],
  "islands": [ { "id":0, "x":0, "y":0, "capacity":6 } ],
  "signals": [ { "node":0, "offset":0,
      "plans":[ { "start_time":0,
        "stages":[ { "protected":[0,1], "permitted":[7], "type":"Fixed", "duration":30 } ] } ] } ],
  "demand": [ { "lane":0, "time_points":[0,600,1200], "flow_rates":[0.3,0.5,0.2] } ],
  "pedestrians": [ { "crosswalk_turn":12, "time_points":[0], "flow_rates":[0.1] } ],
  "bicycles": [ { "lane":3, "mode":"Independent", "time_points":[0], "flow_rates":[0.15] } ],
  "game_rules": { "anchors": { "R":[0.8,1.0], "D":[60,20], "P":[2.5,1.2], "Q":[1.0,0.5] },
                  "weights": { "R":0.4,"D":0.2,"P":0.2,"Q":0.2 },
                  "events": [], "tutorial": [] }
}
```

| 键 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `format_version` | int | ✅ | 格式版本，当前 `1` |
| `meta` | object | ✅ | 关卡元数据 |
| `nodes` | array | ✅ | 节点表（路口 / 需求源汇） |
| `lanes` | array | ✅ | 车道表（有向路段） |
| `turns` | array | ✅ | 转向表（最小放行单元） |
| `islands` | array | ⬜ | **可选**：行人二次过街的中央安全岛；缺省不写（2026-10-24 新增） |
| `signals` | array | ✅ | 信号表（以 node 为主键的配时方案） |
| `demand` | array | ✅ | 机动车需求曲线 |
| `pedestrians` | array | ✅ | 行人过街需求 |
| `bicycles` | array | ✅ | 非机动车需求 |
| `game_rules` | object | ✅ | 游戏扩展规则（锚点 / 权重 / 意外元素 / 教学） |

---

## 4. 各表字段详解

### 4.1 `meta` — 关卡元数据（游戏设计v2 §12）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `name` | string | 关卡名 |
| `author` | string | 作者（UGC 制图者） |
| `chapter` | int | 章节号，1 起（如 1-1 即 chapter=1） |
| `index` | int | 关内序号，1 起（如 1-1 即 index=1） |

### 4.2 `nodes[]` — 节点

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | int | 节点 id，**0..N-1 连续且唯一** |
| `type` | enum | 见 §5：`Signalized / Unsignalized / Origin / Destination` |
| `x` | number | X 坐标（米） |
| `y` | number | Y 坐标（米） |

### 4.3 `lanes[]` — 车道（有向路段）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | int | 车道 id，**0..N-1 连续且唯一** |
| `road` | int | 所属道路 id；同一道路多车道共享，用于按"进口道"聚合（`Movement` 分组） |
| `type` | enum | 见 §5：`Driving / Bus / Biking / Sidewalk / SharedLeftTurn / Construction / Parking` |
| `from_node` | int | 起点节点 id |
| `to_node` | int | 终点节点 id（车辆沿 `from_node → to_node` 行驶） |
| `length` | number | 车道长度（米），**> 0** |
| `speed_limit` | number | 限速（米/秒），默认 `13.89`（≈50 km/h，技术方案01 §3.1） |
| `lane_index` | int | 同向车道序号（用于渲染横向偏移） |
| `lat_index` | int | **可选**（T4.3 S1）：横向排位序号，同向从道路中心线向外 `0,1,2…`，覆盖 `Driving/Bus/Biking/Sidewalk` 全部类型；缺省 `-1` = 未标（不写入，由编辑器读入后回填）。**只给渲染排布用，不参与任何仿真计算** |

> **`lane_index` 与 `lat_index` 的分工（不可混）**：
> `lane_index` 是**仿真几何输入**——`ConflictMatrix` 与 `Engine` 用 `(lane_index + 0.5) · 车道宽` 计算横向偏移与行人过街距离
> （现状：人行横道转向为 `Sidewalk → Sidewalk`，`lane_index = 0` 推出过街距离 3.5m）。**改它的语义会静默改变仿真结果**。
> `lat_index` 只解决"这条车道画在横向第几位"，同一横断面里 `Bus / Biking / Sidewalk` 也有独立序号，不会与 `Driving` 撞 0。

### 4.4 `turns[]` — 转向（原子连接）

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | int | ✅ | 转向 id，**0..N-1 连续且唯一** |
| `src_lane` | int | ✅ | 源车道 id（其 `to_node` 应为 `node`） |
| `dst_lane` | int | ✅ | 目标车道 id（其 `from_node` 应为 `node`） |
| `node` | int | ✅ | 所属路口节点 id |
| `type` | enum | ✅ | 见 §5：`Straight / Left / Right / UTurn / Crosswalk` |
| `ratio` | number | ⬜ | **可选**：占上游流量的比例，须落 `(0, 1]`；缺省 = `1.0`，值为 1.0 时不写入 |
| `leg` | int | ⬜ | **可选**（2026-10-24 新增，仅 `type=Crosswalk`）：分段过街的第几段，`0` = 路缘 → 安全岛、`1` = 安全岛 → 对岸；缺省不写 = 一次走完 |
| `island` | int | ⬜ | **可选**：`leg` 所属安全岛的 `islands[].id`；**必须与 `leg` 成对出现** |

> **关于 `ratio`（本任务对 §3.2 的唯一可选扩展，待 T0.4 裁定）**：技术方案01 §3.6 把"`turn_ratio` 不在 (0,1] → 转向比例非法"列为必须移植的校验规则，技术方案03 §3.2 的验收标准 b 也要求校验器能报出"转向比例越界"——因此格式必须能承载该比例。字段名沿用 §3.2 的小写下划线风格取 `ratio`（等价于 LightSim `Movement.turn_ratio`）。它是**向后兼容的可选项**：所有 `ratio` 均为 1.0 的地图与不含该字段的 §3.2 原始形态**逐字节等价**（见 §6.2）。

### 4.4b `islands[]` — 行人安全岛（**可选**，2026-10-24 新增）

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | int | ✅ | 安全岛 id，同一关卡内唯一 |
| `x` | number | ✅ | 中心点 X（世界米，与 `nodes[]` 同坐标系） |
| `y` | number | ✅ | 中心点 Y（世界米） |
| `capacity` | int | ✅ | 可同时容纳的行人数，**须 ≥ 1** |

**为什么要有它**：宽路口的一次过街常常一个行人相位走不完，现实做法是中央设安全岛，行人先过半幅、在岛上等，
再由第二个相位放行走完（游戏设计v2 §5.3 的「两段式行人相位」）。要让两段能被**不同相位分别放行**，
两段必须是**两条独立的 `turns[]`**（各自能被 `stages[].protected/permitted` 引用）；
「岛在哪、能站多少人」则由本表承载。

**写法要点**：

- 分段过街 = 两条 `type=Crosswalk` 的转向，**`src_lane` / `dst_lane` 完全相同**（几何上是同一条人行横道），
  用 `leg` 区分前段/后段，两条都带同一个 `island`。
- `leg` / `island` **必须成对**；只有 `Crosswalk` 允许带 `leg`；每个岛必须**正好两段**（缺一段校验器报错）。
- `pedestrians[].crosswalk_turn` 仍指向**前段（`leg=0`）**那条转向 —— 需求是"这条过街要过多少人"，
  后段由仿真从同一个岛去找。
- 旧关卡没有 `islands` 键、`turns[]` 里也没有 `leg` / `island` → **逐字节不变**（2026-10-24 前所有关卡都属这一类）。

### 4.5 `signals[]` — 信号（以 node 为主键）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `node` | int | 信号路口节点 id（须指向 `type = "Signalized"` 的节点） |
| `offset` | number | 相位差（秒），用于绿波偏移（技术方案01 §3.2d） |
| `plans[]` | array | 配时方案集，按时段切换（支持第四章时变配时） |

`plans[]`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `start_time` | number | 方案生效起始时刻（秒） |
| `stages[]` | array | 相位序列 |

`stages[]`（借 A/B Street `Stage`，技术方案02 §2.9）：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `protected` | int[] | 保护放行的转向 id 集合 |
| `permitted` | int[] | 允许（须让行）的转向 id 集合 |
| `type` | enum | 见 §5：`Fixed / Variable` |
| `duration` | number | **绿灯时长**（秒），**> 0** |

> **口径裁定（Gate 0）**：`duration` = **绿灯时长**。黄灯 / 全红 / 绿闪不写入 `.spmap`，由 `SignalTiming` 依 `ClearanceTime` 按路口宽度估算（技术方案03 §3.2 附注、游戏设计v2 §8.1）。因此**一个相位一个绿灯时长**，红灯 = 周期 − 本相位绿灯。

### 4.6 `demand[]` / `pedestrians[]` / `bicycles[]` — 三类需求

三者同构，均为**分段常值时变曲线**（技术方案01 §3.4）：`time_points` 为各时段起始时刻（秒，**严格递增**），`flow_rates` 为对应时段流率（非负），**两者等长**。
三类曲线（恒定 / 时变 / 脉冲）统一用该结构表达——**脉冲即"中段高、两侧 0"**（Gate 0 口径）。

| 表 | 键字段 | 其余字段 |
| --- | --- | --- |
| `demand[]` | `lane`（机动车道 id） | `time_points`, `flow_rates`（辆/秒） |
| `pedestrians[]` | `crosswalk_turn`（须为 `Crosswalk` 转向 id） | `time_points`, `flow_rates`（人/秒） |
| `bicycles[]` | `lane`（非机动车道 id） | `mode`, `time_points`, `flow_rates`（辆/秒） |

`bicycles[].mode`：见 §5（`Independent` 独立非机动车道 / `Mixed` 机非混行，游戏设计v2 §9）。

### 4.7 `game_rules` — 游戏扩展规则

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `anchors` | object | 四项指标的评分锚点，每项为二元数组 `[b, g]`（游戏设计v2 §6.3） |
| `weights` | object | 四项指标权重（默认 `R:0.4, D:0.2, P:0.2, Q:0.2`，游戏设计v2 §6.2） |
| `events` | array | 变体关"小意外"元素（游戏设计v2 §5.0）。**原样承载任意 JSON**，不规定字段 |
| `tutorial` | array | 教学步骤（游戏设计v2 §8.6）。**原样承载任意 JSON**，不规定字段 |
| `vehicle_mix` | enum | **可选**（T4.3 S1）：车型构成预设，见 §5 `VehicleMix`；缺省不写 = `CarOnly`。**关卡级、只许选预设，不许自定义比例** |

`anchors.{R,D,P,Q}` 为 `[b, g]`：`b` = 底线值（0 分）、`g` = 优秀值（满分）。
指标方向：`R` 越大越好（`g ≥ b`），`D/P/Q` 越小越好（`g ≤ b`）。

> `events` / `tutorial` 在 §3.2 中只给出空数组占位、未定义字段。本实现**不臆造字段**，以原始 JSON 节点原样读写，任何未来字段都能无损往返。

---

## 5. 枚举取值表

> 取值即 C# 枚举成员名（`SimCore.Model`），序列化为字符串。定义见 `SimCore/Model/Enums.cs`，与技术方案03 §3.1 逐字一致。

| 枚举 | 取值 | 用于 |
| --- | --- | --- |
| `NodeType` | `Signalized`, `Unsignalized`, `Origin`, `Destination` | `nodes[].type` |
| `LaneType` | `Driving`, `Bus`, `Biking`, `Sidewalk`, `SharedLeftTurn`, `Construction`, `Parking` | `lanes[].type` |
| `TurnType` | `Straight`, `Left`, `Right`, `UTurn`, `Crosswalk` | `turns[].type` |
| `StageType` | `Fixed`, `Variable` | `stages[].type` |
| `BicycleMode` | `Independent`, `Mixed` | `bicycles[].mode` |
| `VehicleMix` | `CarOnly`, `CityMix`, `FreightHeavy`, `BusHeavy`, `CarBusMix` | `game_rules.vehicle_mix`（可选） |

> `TurnPriority`（`Banned/Yield/Protected`）**不写入文件**：由 `Stage.PriorityOf(turnId)` 从 `protected/permitted` 派生（技术方案03 §3.1）。

---

## 6. 语义约定（读写实现必须遵守）

### 6.1 信号的落点（`signals[]` ↔ `Node.Offset/SignalPlans`）

技术方案03 §3.2 的 `signals[]` 以 `node` 为主键；而内存模型（T0.2 冻结）把信号属性直接挂在 `Node.Offset` / `Node.SignalPlans` 上。读写时按**规范形**互转：

- **写**：只要某节点满足「`type = Signalized` 或 `offset ≠ 0` 或已配相位」三者之一，就为它写出一条 `signals[]` 记录；否则不写。
- **读**：按 `node` id 把记录回挂到同 id 的节点；同一节点出现多条记录时**后者覆盖**（属非法输入，校验器另报）。

由此保证：**任意合法关卡 `保存 → 加载` 后逐字段等价**（验收 a）。

### 6.2 转向比例（`turns[].ratio`）

- 写：仅当关卡显式给出某转向的比例时才写（**值为 1.0 也照写**），未给出则不写。
- 读：出现该字段即记下，未出现则视为 `1.0`。
- 由此保证往返无损；全部为 1.0 的地图与 §3.2 原始形态逐字节等价。

### 6.3 id 连续性

`nodes / lanes / turns` 的 id 必须是 `0..N-1` 连续且唯一（拓扑编译 `CompiledNetwork` 按 id 直寻址的前提）。**读取时保持数组原顺序，不做重排**，以保证往返等价；不满足连续性由校验器报错。

### 6.4 两个可选字段的写法（T4.3 S1）

| 字段 | 写 | 读 |
| --- | --- | --- |
| `lanes[].lat_index` | 仅当 `≥ 0` 时写；`-1`（未标）**不写** | 缺省记 `-1`（Lane.LatIndex） |
| `game_rules.vehicle_mix` | 仅当显式设置时写；未设置**不写** | 缺省记 `null`，语义等同 `CarOnly` |

> 目的：**旧关卡加载 → 保存必须逐字段不变**（含 `docs/examples/01-1`）。编辑器在导入后自行给缺 `lat_index` 的车道回填，导出时必带。

---

## 7. 校验规则（`SpmapValidator.Validate(level)`）

规则借 LightSim `Network.validate()`（技术方案01 §3.6），并补齐本 schema 的引用完整性。**游戏与编辑器同源共用**（技术方案03 T4.2/T4.4、游戏设计v2 §13.3）。

### 7.1 错误码表

| 错误码 `code` | 级别 | 触发条件 |
| --- | --- | --- |
| `format_version_unsupported` | Error | `format_version` ≠ 当前版本 |
| `id_not_contiguous` | Error | `nodes/lanes/turns` 的 id 跳号或越界（非 `0..N-1`） |
| `duplicate_id` | Error | 同表内 id 重复 |
| `dangling_reference` | Error | 引用了不存在的 id（车道→节点、**转向→车道/节点**、相位→转向、需求→车道、行人→转向、非机动车→车道、比例→转向） |
| `orphan_node` | Error | **孤儿节点**：未连接任何车道 |
| `turn_node_mismatch` | Error | 转向与节点几何不一致（`src_lane.to_node ≠ node` 或 `dst_lane.from_node ≠ node`） |
| `turn_ratio_out_of_range` | Error | **转向比例越界**：`ratio ∉ (0, 1]` |
| `signalized_without_phases` | Error | **信号路口无相位**：`Signalized` 节点无方案、或方案无相位 |
| `stage_duration_non_positive` | Error | 相位绿灯时长 ≤ 0 |
| `invalid_lane_geometry` | Error/Warning | 车道 `length ≤ 0`（Error）或 `speed_limit ≤ 0`（Warning） |
| `pedestrian_turn_not_crosswalk` | Error | 行人需求指向的转向不是 `Crosswalk` |
| `bicycle_lane_not_biking` | Error | 非机动车需求指向的车道类型不符（`Independent`→须 `Biking`；`Mixed`→须 `Biking/Driving`） |
| `demand_profile_length_mismatch` | Error | `time_points` 与 `flow_rates` 长度不一致 |
| `time_points_not_ascending` | Error | `time_points` 非严格递增 |
| `flow_rate_negative` | Error | `flow_rates` 出现负值 |
| `signal_data_on_non_signal_node` | Warning | 非信号节点却带 `offset`/相位 |
| `anchors_unset` | Warning | `anchors` 四项均未标定 |
| `anchor_ordering_inverted` | Warning | 锚点方向相反（如 `R` 的 `g < b`） |
| `weights_invalid` | Error | 权重出现负值 |
| `weights_not_normalized` | Warning | 权重之和 ≠ 1.0 |
| `island_capacity_invalid` | Error | `islands[].capacity < 1`（0 容量的岛等于没有岛） |
| `crosswalk_leg_incomplete` | Error | `turns[].leg` 与 `turns[].island` 没有成对出现 |
| `crosswalk_leg_not_crosswalk` | Error | 非 `Crosswalk` 转向带了 `leg` |
| `crosswalk_leg_invalid` | Error | `leg` 不是 `0` / `1` |
| `crosswalk_island_missing` | Error | `turns[].island` 指向的岛不存在 |
| `crosswalk_leg_duplicated` | Error | 同一个岛的第 0 或第 1 段出现了两条以上 |
| `crosswalk_leg_missing` | Error | 岛只配了一段（缺前段 = 行人没入口；缺后段 = 行人卡在岛上） |
| `crosswalk_leg_mismatch` | Error | 同一个岛的两段 `src_lane`/`dst_lane` 不是同一对（跨的不是同一条人行横道） |

### 7.2 输出约定

- 返回 `ValidationError[]`；**顺序固定**（按表遍历），可复现（铁律 2）。
- 每条含：稳定 `Code`、中文 `Message`、JSON 位置 `Path`（如 `turns[7].src_lane`）、`Severity`。
- 编辑器据 `Path` 高亮、据 `Severity` 决定是否放行导出（Error 一律禁止导出，技术方案03 T4.4）。

### 7.3 与 LightSim `validate()` 的对应

| LightSim 检查 | 本 schema 对应 | 状态 |
| --- | --- | --- |
| 路段端点引用不存在的节点 | `dangling_reference`（`lanes[].from_node/to_node`） | ✅ |
| 孤儿节点（没连任何路） | `orphan_node` | ✅ |
| `turn_ratio` 不在 (0,1] | `turn_ratio_out_of_range`（`turns[].ratio`） | ✅ |
| 信号路口没有相位 | `signalized_without_phases` | ✅ |
| 引用不存在的 movement/phase | `dangling_reference`（`stages[].protected/permitted`） | ✅ |
| cell 物理量为正 | `invalid_lane_geometry`（`length`/`speed_limit`） | ✅（降维到车道） |

---

## 8. `SimCore.Io` API（技术方案03 §3.1）

```csharp
namespace SimCore.Io;

// 读写入口（§3.1）
public static class Spmap
{
    public const int CurrentFormatVersion = 1;
    public static Level Load(string path);              // §3.1: Spmap.Load(path)
    public static void  Save(Level level, string path); // §3.1: Spmap.Save(level, path)
}

// 校验器（§3.1）
public static class SpmapValidator
{
    public static ValidationError[] Validate(Level level); // §3.1: Validate(level) -> ValidationError[]
}

// 关卡内存模型（§3.2 顶层结构的载体；属 Io 层）
public sealed class Level
{
    public int FormatVersion { get; init; }          // format_version
    public LevelMeta Meta { get; init; }             // meta
    public Network Network { get; init; }            // nodes / lanes / turns（+ 节点信号属性）
    public IReadOnlyList<TurnRatio> TurnRatios { get; init; } // turns[].ratio（可选）
    public IReadOnlyList<DemandPlan> Demand { get; init; }        // demand[]
    public IReadOnlyList<PedestrianDemand> Pedestrians { get; init; } // pedestrians[]
    public IReadOnlyList<BicycleDemand> Bicycles { get; init; }   // bicycles[]
    public GameRules GameRules { get; init; }        // game_rules
}
```

其余配套类型：`LevelMeta`、`TurnRatio`、`DemandPlan`、`PedestrianDemand`、`BicycleDemand`、`BicycleMode`、`GameRules`、`MetricAnchors`、`MetricAnchorValue`、`MetricWeights`、`ValidationError`、`ValidationSeverity`。

**依赖方向**：`Model ← Io`（`Level` 复用 `SimCore.Model` 的 `Network/Node/Lane/Turn/Stage/SignalPlan`）。`Io` 之上是 `Scoring`（消费 `game_rules`）与 `Sim`（消费需求量）。Io 不引用 Godot、不含第三方依赖、不含系统随机数。

---

## 9. 示例

随交付提供一份**通过校验**的示例：`docs/examples/01-1-first-crossing.spmap`
（第一章 1-1「第一次通车」：单信号路口 4 进 4 出 + 非机动车道 + 人行横道，双相位，恒定低流量，含 `game_rules` 锚点与权重）。

单元测试 `SimCore.Tests/SpmapTests.cs` 会加载该文件并断言**零错误**，同时断言其"加载 → 保存 → 加载"往返无损。

---

## 10. 与冻结接口的对应 / 待裁定项

| 项 | 状态 |
| --- | --- |
| 顶层结构 = 技术方案03 §3.2 | 逐键一致（§3 总览、§4 详解、§6 语义） |
| `Spmap.Load/Save`、`SpmapValidator.Validate` | 与 §3.1 签名一致 |
| `Level` / `GameRules` / `MetricAnchors` / `MetricWeights` / `ValidationError` | §3.1 未逐一列名（§3.1 只到 `Spmap/SpmapValidator`），按 §3.2 语义最小新增，**待 T0.4 并入契约** |
| `turns[].ratio`（可选扩展） | §3.2 未含；为满足技术方案01 §3.6 + 验收 b 的"转向比例越界"而加，**待 T0.4 裁定** |
| `BicycleMode` 取值 `Independent/Mixed` | §3.2 仅示例 `Independent`；`Mixed` 依游戏设计v2 §9 补充，**待 T0.4 裁定** |
| `stages[].duration` 口径 | 绿灯时长（Gate 0 裁定），本文档与之一致 |
| `lanes[].lat_index` / `game_rules.vehicle_mix` | **2026-10-20 已裁定并实现（T4.3 S1）**：均为可选、缺省不写；见 §4.3 / §4.7 / §6.4。`VehicleKind` 追加 `Truck` 已于同日 S1b 落地（运行期枚举，**不进文件**，本 schema 无影响）；同日晚些时候 `VehicleMix` 追加第 5 个预设 `CarBusMix`（小汽车 92% / 公交 8%，**仅枚举加值**，schema 形状不变，旧文件按枚举名解析不受影响） |
| `docs/SimCore-API.md` | 本次任务时**尚不存在**；以技术方案03 §3.1/§3.2 为基准。Gate 0 冻结后，若契约改动本 schema，走 §8 变更流程 |
