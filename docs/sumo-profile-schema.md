# `.sumo.json` 专业档格式 v1

> 状态：**编辑器私有格式**（2026-11-03 起，S0 落地）
> 归属：`Editor/scripts/app/sumo_profile.gd`（读写实现）、`docs/kb/editor/E10-SUMO专业模式.md`（用法与现状）
> **不是冻结契约**：它不进 `.spmap`、SimCore 不读、游戏不读。但**改结构必须同批改本文**，
> 否则下一个人只能从代码里猜字段含义。

---

## 1. 为什么单独一个文件

`.spmap`（`docs/spmap-schema.md`）是冻结契约，被 SimCore 与游戏共用。而 SUMO 专属的东西
（`accel` / `sigma` / `carFollowModel` / `linkIndex` 灯态 / `detector-gap`）对游戏侧仿真毫无意义——
它有自己的跟驰模型。塞进关卡格式只会污染契约，还要动 `SpmapTests.E5` 的逐字节承诺。

于是沿用 `.spdeco.json` 的路子：**同目录同名旁挂**。

```
关卡/01-1.spmap          ← 冻结契约（SimCore / 游戏读）
关卡/01-1.spdeco.json    ← 装饰（编辑器私有，已有）
关卡/01-1.sumo.json      ← 本文件：SUMO 专业档（编辑器私有，导出器读）
```

成对规则：**存盘时一起写、自动保存时一起写、打开时自动配对载入**。
专业档为空时**删除旧文件**（否则用户清空专业内容后，下一次导出还会读到旧配时）。

---

## 2. 文件约定

| 项 | 规定 |
| --- | --- |
| 扩展名 | `.sumo.json`（与关卡同名同目录） |
| 编码 | UTF-8（无 BOM），2 空格缩进 |
| 版本 | `format_version`（当前 `1`）。不匹配时**告警后尽力读取**，不拒绝加载 |
| 字段命名 | 一律小写下划线 |
| **缺省不写** | 空表整段不写；默认值不写（与 `.spmap` 的 `islands[]` / `lat_index` 同口径） |
| 顺序稳定 | 同一输入导出两次逐字节一致（所有数组按 id 升序写） |
| 诊断字段 | `orphans` 是**运行时**诊断，**永不进文件** |

---

## 3. 顶层结构

```jsonc
{
  "format_version": 1,
  "config":        { "begin": 0, "end": 3600, "step_length": 1.0, "seed": 42, "teleport": 300 },
  "vehicle_types": [ /* 车型库（全局定义） */ ],
  "lane_mix":      { /* 按进口配的车型构成 */ },
  "tls":           [ /* 信号方案（逐灯态） */ ],
  "flows":         [ /* 机动车需求 */ ],
  "person_flows":  [ /* 行人需求 */ ],
  "bike_flows":    [ /* 自行车需求 */ ],
  "additional":    [ /* 检测器 / 公交站 / 停车场 */ ],
  "bus_lines":     [ /* 公交线路（路线 + 发车间隔 + 停靠站） */ ],
  "overrides":     { "junctions": [], "lanes": [], "connections": [] },
  "netconvert":    { "no_turnarounds": true }
}
```

| 键 | 必填 | 说明 | 生效批次 |
| --- | --- | --- | --- |
| `format_version` | ✅ | 当前 `1` | S0 |
| `config` | ⬜ | 仿真运行配置（写进 `.sumocfg`） | S0（读）/ S5（用） |
| `vehicle_types` | ⬜ | 车型库（全局定义：车长 / 限速 / 加减速 / 风格） | S2 |
| `lane_mix` | ⬜ | **按进口**配的车型构成，键是字符串化车道 id | S2b |
| `tls` | ⬜ | 信号方案，**逐 turn 灯态** | S1 |
| `flows` | ⬜ | 机动车需求（时段 + 流量 + 车型） | S2 |
| `person_flows` | ⬜ | 行人需求（走人行道 + 人行横道） | S2 |
| `bike_flows` | ⬜ | 自行车需求（走 `Biking` 车道） | S2 |
| `additional` | ⬜ | 检测器 / 公交站 / 停车场 | S3 |
| `overrides` | ⬜ | 对 netconvert 产物的属性级覆盖 | S4 |
| `netconvert` | ⬜ | netconvert 选项 | S4 |

---

## 4. **id 口径（最容易埋雷的地方）**

**文件里所有的 `node` / `lane` / `turn` 都是 `.spmap` 的 id（即 `Doc.renumber()` 之后的文件 id）。**

| 时机 | 翻译方向 | 用哪张表 | 代码 |
| --- | --- | --- | --- |
| 存盘（写 `.sumo.json`） | 实时 id → 文件 id | `fwd_node` / `fwd_lane` / `fwd_turn` | `SumoProfile.remapped(fwd_*)` |
| 载入（读 `.sumo.json`） | 文件 id → 实时 id | `inv_node` / `inv_lane` / `inv_turn` | `SumoProfile.remapped(inv_*)` |

两个方向走同一个 `remapped()`，避免"一边翻译一边忘"。

**翻译失败的条目不丢**：整条移进 `orphans`（`{table, index, id, reason}`），
由校验器报 Warning；用户删了车道不会连带把配时静默吃掉。

**载入时机**：必须在 `EditorApp._cached_temp_spmap()` **之后**——
反查表 `_inv_*` 是在那里建好的（`load_sumo_profile_for()` 有注释）。

---

## 5. 各表字段

### 5.1 `config`（S5，2026-11-06 落地）

写进 `<base>.sumocfg` 的五行。**与默认值相同的字段不写**（`SumoRunModel.set_value` 会删键），
整段全是默认值时 `config` 整个不出现 —— 缺省值以 `SumoProfile.DEFAULT_CONFIG` 为准。

| 字段 | 类型 | 默认 | 落点 | 说明 |
| --- | --- | --- | --- | --- |
| `begin` | number | 0 | `<time><begin>` | 开始时刻（秒） |
| `end` | number | 3600 | `<time><end>` | 结束时刻（秒）——「▶ 试跑」就跑这么久 |
| `step_length` | number | 1.0 | `<time><step-length>` | 步长（秒），0.1 ~ 10 |
| `seed` | int | 42 | `<random_number><seed>` | 随机种子（**复盘必需**） |
| `teleport` | number | 300 | `<processing><time-to-teleport>` | 堵死多少秒后传送，0 = 不等待 |

> ★ **`seed` 必须写成整数**（`"seed": 7`，不是 `7.0`）。C# 侧虽然已经用 `double?` 接，
> 但 `System.Text.Json` 的严格模式对整型字段遇到 `7.0` 会**抛异常并丢掉整份 config**
> （E10 §8-28）。面板侧已强制存 int。

### 5.2 `vehicle_types[]`（S2）

| 字段 | 说明 |
| --- | --- |
| `id` | 车型 id（车流引用它） |
| `vclass` | `passenger` / `bus` / `truck` / `bicycle` / `pedestrian` / `emergency` |
| `length` / `max_speed` / `accel` / `decel` / `sigma` / `min_gap` | 物理参数 |
| `color` / `gui_shape` | 观感（`"r,g,b"` / SUMO guiShape） |
| `share` | **全局默认比例**（`lane_mix` 没给该进口时用它；<=0 = 不参与） |
| `style` | `careful` / `normal` / `aggressive`——**界面上给这个，物理参数由映射表推**（技术方案06 §7.2） |

风格 → 参数（三者叠加在预设之上，其余字段沿用预设）：

| `style` | `sigma` | `tau` | `speedFactor` |
| --- | --- | --- | --- |
| `careful` | 0.2 | 1.4 | 0.85 |
| `normal` | 0.5 | 1.0 | 1.00 |
| `aggressive` | 0.9 | 0.7 | 1.15 |

> **本段为空 = 跟随关卡**：按 `game_rules.vehicle_mix` 预设展开成 1~3 条 vType
> （`CarOnly` 1 条 / `CityMix` 3 条 / `FreightHeavy` 2 条 …）。
> 只有想单独调某类车的风格、或加自行车/应急车时才需要写本段。
> `share` 是同车流内的相对比例，导出时归一化。

### 5.3 `tls[]`（S1）

```jsonc
{ "node": 0, "programs": [
  { "id": "0", "type": "static", "offset": 0, "activate_at": 0,
    "params": { "max-gap": 3.0, "detector-gap": 2.0, "passing-time": 2.0 },
    "phases": [
      { "duration": 30, "name": "南北直行", "min_dur": 10, "max_dur": 60,
        "states": { "12": "G", "13": "g", "14": "r" } }   // key = **turn id**（JSON 键只能是字符串）
    ] } ] }
```

- `type`：`static` / `actuated` / `delay_based`
- `states` 的键是 **turn id**，值是 SUMO 灯态 `G / g / y / r / o`
- `states` **只存非红**：缺省 = 红（`r`）。导出时缺的位补 `r`，文件小一半。
- **不存 `linkIndex`**：linkIndex 由 netconvert 分配，几何一改就重排；turn id 在 `Doc` 里稳定。
  导出时按探针结果（`SumoProbe`）把它翻译成 linkIndex。
- 每跳灯**至少要有一次绿灯**，否则 SUMO 报 `Missing green phase`。

**导出时写到哪**（2026-11-04 调整，`SumoProfileTls` 统一执行）：

| 该路口方案数 | 目标文件 | `programID` |
| --- | --- | --- |
| **1 套** | `net.xml`（直接替换 `<tlLogic>`） | `0`（与网络原来一致） |
| **≥2 套** | `<base>.tll.xml`（additional） | `pro0` / `pro1` … |

> 单套必须内嵌：netedit 打开 net.xml 只显示网络自带的那套，additional 里的第二套在
> 信号灯编辑器里默认看不到（2026-11-04 老板实测）。

其余硬规则（两种都适用）：

| 规则 | 原因 |
| --- | --- |
| 多套时 `programID` 加 `pro` 前缀 | SUMO 见到与 net.xml 相同 `(id, programID)` 会报 `Another logic with id '0' and programID '0' exists` 并**退出** |
| state 串长度必须等于该 tlLogic 的 link 数 | 长度不符 SUMO 拒绝加载 |
| 有灯位全程没绿 → 第 1 相位自动补 `G` 并告警 | 否则文件根本跑不起来（宁可灯序不完美） |
| 多套方案 → 追加 `<WAUT>`，按 `activate_at` 切换 | 分时段配时的载体 |

### 5.3b `lane_mix{}`（S2b）

**按进口（车道）配的车型构成**。键是**字符串化的车道 id**，值是 `[{id, share}]`：

```jsonc
"lane_mix": {
  "0": [{"id": "car", "share": 60}, {"id": "truck", "share": 40}],
  "1": [{"id": "car", "share": 100}]
}
```

| 规则 | 说明 |
| --- | --- |
| 三级回落 | `lane_mix[lane]` → `vehicle_types[].share > 0` → `game_rules.vehicle_mix` |
| 悬空引用 | `id` 在 `vehicle_types` 里不存在时**该行被忽略**；整条空了下落到下一级 |
| `share` | 相对权重，导出时归一化，不必凑满 100 |
| 键的类型 | JSON 对象键只能是字符串；读回来要转 int，`remapped()` 要按车道 id 翻译 |

> 编辑器在用户第一次改某个进口的比例时，会把"当前生效的构成"固化成 `lane_mix` 的一项，
> **并把引用到的车型补进 `vehicle_types`**（否则会变成悬空引用）。

### 5.4 `flows[]` / `person_flows[]` / `bike_flows[]`（S2）

| 字段 | 适用 | 说明 |
| --- | --- | --- |
| `id` | 全部 | 唯一 |
| `lane` | 机动车 / 自行车 | 出发车道（`bike_flows` 必须是 `Biking`） |
| `crosswalk_turn` | 行人 | 必须指向 `leg=0` 的 `Crosswalk` 转向 |
| `vtype` | 全部 | 车型 id |
| `segments[]` | 全部 | `{begin, end, veh_per_hour}`，按时段分段（对应 `.spmap` 的分段常值曲线） |
| `depart_lane` / `depart_speed` | 机动车 | 默认 `best` / `max`（界面上不出现） |
| `route_mode` | 机动车 | `ratio`（按转率枚举，默认）/ `shortest` |

> **三张表全空 = 从 `.spmap` 自动派生**：读 `demand` / `bicycles` / `pedestrians` 曲线，
> `flow_rates`（辆/秒）× 3600 → `vehsPerHour`，每段一个 `<flow>`。
> 填了任一张表就以专业档为准（不再读 `.spmap` 的对应曲线）。

**转率从哪来**：不在专业档里，而是 `.spmap` 的 `turns[].ratio`（契约可选字段，缺省 1.0）。
**值就是权重**，导出时按进口归一化 —— 没有"全 1.0 = 未配置"的哨兵，
所以 1:1:1 能被表达。

默认值由编辑器**实体写进 `ratio`**：

| 时机 | 值 |
| --- | --- |
| 该进口第一条转向 | `1.0` |
| 后续新建的转向 | 类型默认 / 10：直行 0.6 / 右转 0.25 / 左转 0.15 / 掉头 0.02 |
| 导入旧文件（turns 里**没有** `ratio` 键）且该进口 ≥2 条 | 按 `6 : 2.5 : 1.5` 归一化填入 |
| 导入已带 `ratio` 的文件 | 原样保留（1:1:1 不会被改写） |

要求落在 `(0, 1]`（`turn_ratio_out_of_range` 是 Error）。
**掉头默认 0.02 不是 0**：填 0 会越界；要禁止该转向请删转向。

### 5.5 `additional[]`（S3，2026-11-05 落地）

检测设施。一条记录 = 一个 SUMO additional 元素。

```jsonc
"additional": [
  {"id":"e1_0_1",   "kind":"e1",       "lane":0, "pos":90,  "freq":60},
  {"id":"e2_0_1",   "kind":"e2",       "lane":0, "pos":160, "length":20, "freq":60},
  {"id":"bus_stop_0_1", "kind":"bus_stop","lane":0, "start":142.5, "end":167.5,
   "lines":"bus", "duration":20, "name":"市政府站"},
  {"id":"parking_0_1",  "kind":"parking", "lane":0, "start":140, "end":160, "capacity":5}
]
```

| 字段 | 用于 | 说明 |
| --- | --- | --- |
| `id` | 全部 | SUMO 元素 id。缺省自动生成 `<kind>_<lane>_<n>`；重复时自动加后缀 |
| `kind` | 全部 | `e1` 断面流量 / `e2` 排队 / `bus_stop` 公交站 / `parking` 停车场 |
| `lane` | 全部 | 所在车道（**spmap 车道 id**，存盘时按 `fwd_lane` 重映射） |
| `pos` | e1 / e2 | 相对**车道起点**的米数。**只要求 `0 ≤ pos < 车道长`，与 `length` 无关**（实测） |
| `length` | e2 | 检测区长度（米），默认 20。检测区是 `[pos-length, pos]`，`pos` 是**末端** |
| `freq` | e1 / e2 | 输出频率（秒），默认 60 |
| `start` / `end` | bus_stop / parking | 站台/车位起止（米，车道起点算），`end - start ≥ 1` |
| `lines` | bus_stop | 停靠线路名，默认 `bus`。**必须与车辆 flow 的 `line` 对上**，否则车经过也不停 |
| `duration` | bus_stop | 停靠时长（秒），默认 20。导出时写进 flow 的 `<stop duration=...>` |
| `capacity` | parking | 路边车位数（`roadsideCapacity`），默认 5 |
| `name` | bus_stop / parking | 显示名。填了之后编辑器里勾停靠站会显示它（否则是「站 N · 车道#X · 距离」） |

**检测器输出文件不在这里配**：所有检测器统一写到 `<base>.det.xml`（相对路径）。
让每个检测器各写一个文件会把导出目录炸开，而且用户也记不住哪个文件是谁。

**位置口径**：文件里存的是 `pos`（从**车道起点**算，SUMO 的口径）；
编辑器界面给的是「离路口多远」，换算在 `SumoFacilitiesModel.pos_from_road` 做一处。

> ⚠️ **`<busStop>` 本身不会让车停。** SUMO 里车要停靠，必须车辆的 route 里写
> `<stop busStop="..." duration="..."/>`。导出时由 `SumoRouWriter` 自动补：
> 只有当**该车流的车型全是 `vClass=bus`**时才插 stop（flow 共享 route，混型时插了
> 私家车也会停）。站台只经过部分候选路线时也不插（会报 `busStop not on route`），
> 改为给一条警告。

### 5.5b `bus_lines[]`（S3b）

**公交线路**。一条记录 = 一趟车跑哪条路线、多久发一班、停哪些站。

```jsonc
"bus_lines": [
  {
    "id": "bus_1",
    "line": "1",              // 线路名（写进 flow 的 line）
    "lane": 0,                // 起点进口车道（spmap 车道 id，存盘按 fwd_lane 重映射）
    "headway": 300,           // 发车间隔（秒）
    "begin": 0, "end": 3600,  // 运营时段
    "stops": ["bus_stop_0_1", "bus_stop_2_1"]
  }
]
```

| 字段 | 说明 |
| --- | --- |
| `id` | SUMO flow id（车辆 id 会是 `bus_1.0`、`bus_1.1`…） |
| `line` | 线路名。**与公交站的 `lines` 无关** —— 停靠由 `stops` 显式指定 |
| `lane` | 起点进口。路线**不让用户画**：按这条进口的转率枚举，取权重最大的那条 |
| `headway` | 发车间隔（秒），默认 300。导出成 `<flow period=...>` |
| `begin` / `end` | 运营时段，默认 0~3600 |
| `stops` | 停靠的公交站 id，**按顺序**。不在该路线上的会被过滤并告警 |

**为什么用 `<flow period>` 而不是一辆辆 `<vehicle>`**：用户要的是"间隔"，
`period` 就是它，车辆 id 由 SUMO 自动编号。代价是 `<stop until="绝对时刻">` 用不了
（那要求每辆车不同的时间），所以停靠一律 `duration`。

**站被删掉时**，编辑器会把所有线路里对它的引用一并清掉（不留悬空引用）。

> ★ 有 `bus_lines` 时，**不再**给需求派生的车流自动插 stop ——
> 否则同一条路线上会冒出两拨公交（一拨来自 demand 车流、一拨来自线路）。

### 5.6 `overrides`（S4，2026-11-06 落地）

对 netconvert 产物的**属性级轻改**。三张表都是"**空值 = 删行**"：用户把某项改回"跟随"
时整行从文件里消失，不留 `{"lane": 3, "access": ""}` 这种空壳（导出端只看"有没有这一行"）。

| 子键 | 字段 | 说明 |
| --- | --- | --- |
| `junctions[]` | `node` | 节点 id |
| | `type` | `priority`（优先让行）/ `right_before_left`（全向停）/ `unregulated`（无控制） |
| `lanes[]` | `lane` | 车道 id |
| | `access` | `bus`（仅公交）/ `bicycle`（仅自行车）/ `pedestrian`（仅行人） |
| | `speed` | 限速覆盖（m/s，>0 才写） |
| | `width` | 车道宽度覆盖（m，>0 才写） |
| `connections[]` | `turn` | 转向 id |
| | `access` | `bus`（仅公交）/ `none`（禁止机动车通行） |
| | `speed` | 限速覆盖（m/s，>0 才写） |

**写进 net.xml 的样子**

| 覆盖 | 写入位置 | 注意 |
| --- | --- | --- |
| `junctions[].type` | `<junction type>` | 信号路口**拒绝**（tlLogic 还在，改了配时静默失效） |
| `lanes[].access` | `<lane allow>`，**同时删掉 `disallow`** | 两者并存时 SUMO 会忽略 disallow 并告警 |
| `lanes[].speed` / `width` | `<lane speed>` / `<lane width>` | 原样写，不换算 |
| `connections[].access` | `<connection allow>`（`none` → `allow="pedestrian"`） | **绝不删 `<connection>`**：删了 SUMO 段错误退出 |
| `connections[].speed` | `<connection speed>` | |

### 5.7 `netconvert`（S4，2026-11-06 落地）

只露 4 个常用开关（技术方案06 §6.2）。**只写与默认值不同的那些**，全默认时整个段不写。

| 字段 | 默认 | netconvert 参数 | 说明 |
| --- | --- | --- | --- |
| `no_turnarounds` | `true` | `--no-turnarounds true` | 禁止掉头（默认开，与 2026-11 之前一致） |
| `no_internal_links` | `false` | `--no-internal-links true` | 不生成路口内部车道（**link 数不变**，灯位编号不受影响） |
| `lefthand` | `false` | `--lefthand true` | 左侧通行 |
| `junctions_join` | `false` | `--junctions.join true` | 合并相邻路口（★ 会改路口 id，已配的专业配时可能对不上） |

> 车道宽不在这里：它由导出面板的 `laneWidthM` 决定（写进 OpenDRIVE，默认 3.5 m）。

---

## 6. 与导出产物的关系

```
.sumo.json ─┬─→ <base>.net.xml   单套信号方案**内嵌**于此（programID=0，netedit 里可见可改）
            ├─→ <base>.tll.xml   仅当某路口有 ≥2 套方案（WAUT），programID 加 pro 前缀
            ├─→ <base>.rou.xml   车型 + 路线 + 车流（A7 实测：车辆三件套只能在这里）
            ├─→ <base>.add.xml   检测器 / 公交站 / 停车场                       [S3]
            ├─→ <base>.sumocfg   运行配置（全部相对路径，整夹可搬移）           [S5]
            └─→ overrides 非空时才轻改 net.xml                                 [S4]
```

**基础模式不产生 4 个附加文件**，只出 `.net.xml`（行为与 2026-11 之前一致）。
