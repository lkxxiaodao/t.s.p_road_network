class_name SumoProfile
extends RefCounted

## SUMO 专业档（`<关卡>.sumo.json`）—— **编辑器私有，不进 `.spmap`，SimCore 不读**。
##
## 为什么单独一个文件（技术方案06 §3 D1）：
##   ① `.spmap` 是冻结契约，改它要动 SimCore 与 `SpmapTests.E5` 的逐字节承诺；
##   ② SUMO 的 `accel/sigma/carFollowModel/linkIndex 灯态` 对游戏侧仿真毫无意义；
##   ③ 同目录旁挂已有先例（`.spdeco.json`），自动保存/带配对的工具链现成。
##
## ★ 零信号：内容变更只走 `EditorApp.content_changed`（与 `Doc` 同口径）。
## ★ 只存"专业模式专属"的东西。基础模式已有的（路网/转向/相位）不在这里重复存。
##
## ## id 口径（最容易埋雷的地方）
## 文件里存的 id 一律是 **`.spmap` 的 id**（即 `Doc.renumber()` 之后的文件 id）：
##   - **存盘**：live id → 文件 id（用 `fwd_*` 映射，`remapped()` 传 fwd）
##   - **载入**：文件 id → live id（用 `inv_*` 映射，`remapped()` 传 inv）
## 两个方向走同一个 `remapped()`，避免"一边翻译一边忘"。
## 翻译失败**不丢数据**：条目落进 `orphans` 并计数，由校验器报 Warning（技术方案06 §9）。

const FORMAT_VERSION := 1

## `config` 的缺省值。★ `SumoRunModel.DEFAULTS` 与 `.sumocfg` 的生成端都以它为准 ——
## 三处各存一份迟早分叉（面板显示 3600、导出却是别的数）。
const DEFAULT_CONFIG := {
	"begin": 0.0,
	"end": 3600.0,
	"step_length": 1.0,
	"seed": 42,
	"teleport": 300.0,
}

## 各表的"外键字段名"。改动这里等于改 schema，必须同批改 `docs/sumo-profile-schema.md`。
const KEY_TLS := "node"
const KEY_FLOW := "lane"
const KEY_PERSON := "crosswalk_turn"
const KEY_BIKE := "lane"
const KEY_ADDITIONAL := "lane"

var config: Dictionary = {}
## 全局车型库（定义：长度 / 限速 / 加减速 / 风格）。**不含"哪个进口用多少"**。
var vehicle_types: Array = []
## 按进口（车道）配的车型构成：`{ lane_id: [ {id, share}, ... ] }`。
## ★ 缺省 = 跟随 `vehicle_types[].share`，再缺省 = 跟随 `game_rules.vehicle_mix` 预设。
var lane_mix: Dictionary = {}
var tls: Array = []
var flows: Array = []
var person_flows: Array = []
var bike_flows: Array = []
var additional: Array = []
## 公交线路（S3b）：`{id, line, lane, headway, begin, end, stops:[站 id...]}`。
## ★ `stops` 是**对 `additional[]` 里公交站 id 的引用**（字符串）。
##   站被删掉后这里会变成悬空引用 —— 导出端过滤并告警，不崩。
var bus_lines: Array = []
var overrides: Dictionary = {}
var netconvert: Dictionary = {}

## 每次内容变化 +1。给 `content_signature()` 与面板指纹用（比 stringify 便宜得多）。
var revision: int = 0

## 载入时 id 翻译失败的条目：`{table, index, id, reason}`。**不丢数据，只标记**。
var orphans: Array = []


static func defaults() -> SumoProfile:
	var p := SumoProfile.new()
	p.config = DEFAULT_CONFIG.duplicate(true)
	return p


## 空档（新建关卡 / 基础模式）：只有默认 config，其余全空
static func empty() -> SumoProfile:
	return defaults()


# ============================================================
# 状态
# ============================================================

## config 是否偏离默认（任一字段与 DEFAULT_CONFIG 不同）。
## ★ S5 踩过：`has_content()` 原来完全不看 config，于是"只改了运行时长"时返回 false →
##   导出把 `sumoProfile` 传成空串 → C# 用默认值写 sumocfg（用户改了 240，跑出来还是 3600）。
func config_differs_from_default() -> bool:
	if config.is_empty():
		return false
	for key in DEFAULT_CONFIG.keys():
		var def := float(DEFAULT_CONFIG[key])
		if not is_equal_approx(float(config.get(key, def)), def):
			return true
	return false


## 有专业数据吗？（只有默认 config 不算）
func has_content() -> bool:
	if config_differs_from_default():
		return true
	if not vehicle_types.is_empty():
		return true
	if not lane_mix.is_empty():
		return true
	if not tls.is_empty():
		return true
	if not flows.is_empty():
		return true
	if not person_flows.is_empty():
		return true
	if not bike_flows.is_empty():
		return true
	if not additional.is_empty():
		return true
	if not bus_lines.is_empty():
		return true
	if not overrides.is_empty():
		return true
	if not netconvert.is_empty():
		return true
	return false


## 内容变了（面板改完调一次，让指纹/签名跟上）
func touch() -> void:
	revision += 1


## 深拷贝。**撤销栈必须用它**：`Doc.snapshot()` 存的是值拷贝，
## 如果这里只存对象引用，之后任何一次编辑都会把历史快照一起改掉。
func clone() -> SumoProfile:
	var c := SumoProfile.new()
	c.config = config.duplicate(true)
	c.vehicle_types = vehicle_types.duplicate(true)
	c.lane_mix = lane_mix.duplicate(true)
	c.tls = tls.duplicate(true)
	c.flows = flows.duplicate(true)
	c.person_flows = person_flows.duplicate(true)
	c.bike_flows = bike_flows.duplicate(true)
	c.additional = additional.duplicate(true)
	c.bus_lines = bus_lines.duplicate(true)
	c.overrides = overrides.duplicate(true)
	c.netconvert = netconvert.duplicate(true)
	c.revision = revision
	c.orphans = orphans.duplicate(true)
	return c


# ============================================================
# 序列化
# ============================================================

## schema 形状：**空表整段不写**（与 `.spdeco.json`、`.spmap` 的"缺省不写"同口径）
func to_dict() -> Dictionary:
	var out := {"format_version": FORMAT_VERSION}
	if config_differs_from_default():
		out["config"] = config.duplicate(true)
	if not vehicle_types.is_empty():
		out["vehicle_types"] = vehicle_types.duplicate(true)
	if not lane_mix.is_empty():
		var mix_out := {}
		for lane_id in lane_mix.keys():
			mix_out[str(int(lane_id))] = (lane_mix[lane_id] as Array).duplicate(true)
		out["lane_mix"] = mix_out
	if not tls.is_empty():
		out["tls"] = tls.duplicate(true)
	if not flows.is_empty():
		out["flows"] = flows.duplicate(true)
	if not person_flows.is_empty():
		out["person_flows"] = person_flows.duplicate(true)
	if not bike_flows.is_empty():
		out["bike_flows"] = bike_flows.duplicate(true)
	if not additional.is_empty():
		out["additional"] = additional.duplicate(true)
	if not bus_lines.is_empty():
		out["bus_lines"] = bus_lines.duplicate(true)
	if not overrides.is_empty():
		out["overrides"] = overrides.duplicate(true)
	if not netconvert.is_empty():
		out["netconvert"] = netconvert.duplicate(true)
	return out


## 内容签名（撤销栈判"这一步到底改没改"、面板判指纹都用它）
func signature() -> String:
	return JSON.stringify(to_dict())


static func equal(a: SumoProfile, b: SumoProfile) -> bool:
	if a == null or b == null:
		return a == b
	return a.signature() == b.signature()


## 从 `.sumo.json` 文本解析；形状不对返回 null（**不抛异常、不半途造半个对象**）
static func from_json_text(txt: String) -> SumoProfile:
	var parsed = JSON.parse_string(txt)
	if not (parsed is Dictionary):
		push_warning("专业档：顶层不是 JSON 对象，已忽略")
		return null
	var d: Dictionary = parsed
	var ver := int(d.get("format_version", FORMAT_VERSION))
	if ver != FORMAT_VERSION:
		push_warning("专业档：format_version=%d 不受支持（本编辑器仅支持 %d），按当前版本尽力读取" % [
			ver, FORMAT_VERSION])

	var p := SumoProfile.defaults()
	var cfg := _as_dict(d.get("config"))
	if not cfg.is_empty():
		p.config = cfg
	p.vehicle_types = _as_rows(d.get("vehicle_types"), "vehicle_types")
	# lane_mix 的键是**字符串化的车道 id**（JSON 对象的键只能是字符串）
	var raw_mix := _as_dict(d.get("lane_mix"))
	for k in raw_mix.keys():
		var rows := _as_rows(raw_mix[k], "lane_mix[%s]" % str(k))
		if not rows.is_empty():
			p.lane_mix[str(int(str(k)))] = rows
	p.tls = _as_rows(d.get("tls"), "tls")
	p.flows = _as_rows(d.get("flows"), "flows")
	p.person_flows = _as_rows(d.get("person_flows"), "person_flows")
	p.bike_flows = _as_rows(d.get("bike_flows"), "bike_flows")
	p.additional = _as_rows(d.get("additional"), "additional")
	p.bus_lines = _as_rows(d.get("bus_lines"), "bus_lines")
	p.overrides = _as_dict(d.get("overrides"))
	p.netconvert = _as_dict(d.get("netconvert"))
	p._normalize_ints()
	return p


## 顶层表：必须是"对象数组"，不是数组或混进非对象就整表丢掉并告警（宁缺勿炸）
static func _as_rows(v, table: String) -> Array:
	if v == null:
		return []
	if not (v is Array):
		push_warning("专业档：%s 不是数组，已忽略" % table)
		return []
	var out: Array = []
	var raw: Array = v
	for i in raw.size():
		if not (raw[i] is Dictionary):
			push_warning("专业档：%s[%d] 不是对象，已跳过" % [table, i])
			continue
		out.append((raw[i] as Dictionary).duplicate(true))
	return out


static func _as_dict(v) -> Dictionary:
	if v is Dictionary:
		return (v as Dictionary).duplicate(true)
	return {}


## JSON 出来的数字统一成 int/float：id 必须是 int，否则查表对不上
func _normalize_ints() -> void:
	config["seed"] = int(config.get("seed", 42))
	for row in tls:
		row["node"] = int(row.get("node", -1))
		for program in row.get("programs", []):
			for phase in program.get("phases", []):
				var states: Dictionary = phase.get("states", {})
				var fixed := {}
				for k in states.keys():
					fixed[int(k)] = String(states[k])
				phase["states"] = fixed
	for row in flows:
		row["lane"] = int(row.get("lane", -1))
	for key in lane_mix.keys():
		for row in lane_mix[key]:
			row["share"] = float(row.get("share", 0.0))
	for row in person_flows:
		row["crosswalk_turn"] = int(row.get("crosswalk_turn", -1))
	for row in bike_flows:
		row["lane"] = int(row.get("lane", -1))
	for row in bus_lines:
		row["lane"] = int(row.get("lane", -1))
		row["headway"] = float(row.get("headway", 300.0))
		row["begin"] = float(row.get("begin", 0.0))
		row["end"] = float(row.get("end", 3600.0))
		if row.has("stops"):
			var cleaned: Array = []
			for sid in _as_array(row["stops"]):
				cleaned.append(String(sid))
			row["stops"] = cleaned
	for row in additional:
		if row.has("lane"):
			row["lane"] = int(row["lane"])
		# 数值字段统一成数值（JSON 读回来可能是 int，面板要 float）
		for key in ["pos", "length", "freq", "start", "end", "duration"]:
			if row.has(key):
				row[key] = float(row[key])
		if row.has("capacity"):
			row["capacity"] = int(row["capacity"])


# ============================================================
# id 重映射（存盘用 fwd，载入用 inv）
# ============================================================

## 造一份"id 已翻译"的副本（**不动自己**）。
## map_node / map_lane / map_turn 的语义由调用方决定：
##   存盘传 `fwd_*`（live → 文件），载入传 `inv_*`（文件 → live）。
## 翻译不到的条目进 `out.orphans`，**不丢数据**。
func remapped(map_node: Dictionary, map_lane: Dictionary, map_turn: Dictionary) -> SumoProfile:
	var out := SumoProfile.new()
	out.config = config.duplicate(true)
	out.netconvert = netconvert.duplicate(true)
	# ★ 车型库没有外键（车流用 id 字符串引用它，不涉及 live/file id 翻译）→ 原样深拷贝。
	#   2026-11-05 补：S0 写这个方法时 vehicle_types 还没启用，漏了它 → 导出的 rou.xml
	#   一直用 vehicle_mix 预设，自定义车型被静默忽略（教训：**新增专业档字段必须同步这里**）。
	out.vehicle_types = vehicle_types.duplicate(true)
	# ★ lane_mix 的**键**是车道 id → 必须跟 map_lane 一起翻译（S2 踩过"新增字段忘同步"的坑）
	for key in lane_mix.keys():
		var live := int(key)
		if not map_lane.has(live):
			out.orphans.append({"table": "lane_mix", "index": -1, "id": live,
				"reason": "车道在当前关卡里不存在"})
			continue
		out.lane_mix[str(int(map_lane[live]))] = (lane_mix[key] as Array).duplicate(true)

	out.tls = _remap_tls(map_node, map_turn, out)
	out.flows = _remap_rows(flows, KEY_FLOW, map_lane, out, "flows")
	out.person_flows = _remap_rows(person_flows, KEY_PERSON, map_turn, out, "person_flows")
	out.bike_flows = _remap_rows(bike_flows, KEY_BIKE, map_lane, out, "bike_flows")
	# additional 的 lane 是可选的：-1 / 缺省 = 不绑车道
	out.additional = _remap_rows(additional, KEY_ADDITIONAL, map_lane, out, "additional", true)
	# 公交线路：lane 要翻译；stops 里的公交站 id 是**字符串、不随存盘变**，原样带过去
	out.bus_lines = _remap_rows(bus_lines, "lane", map_lane, out, "bus_lines")

	var ov := {}
	var juncs := _remap_rows(_as_array(overrides.get("junctions")), "node", map_node, out, "overrides.junctions")
	var lanes := _remap_rows(_as_array(overrides.get("lanes")), "lane", map_lane, out, "overrides.lanes")
	var conns := _remap_rows(_as_array(overrides.get("connections")), "turn", map_turn, out, "overrides.connections")
	if not juncs.is_empty():
		ov["junctions"] = juncs
	if not lanes.is_empty():
		ov["lanes"] = lanes
	if not conns.is_empty():
		ov["connections"] = conns
	out.overrides = ov

	out.revision = revision
	return out


## 信号方案：除了 node，相位里的 states 是"turn id → 灯态"，键也要翻译
func _remap_tls(map_node: Dictionary, map_turn: Dictionary, out: SumoProfile) -> Array:
	var result: Array = []
	for i in tls.size():
		var row: Dictionary = (tls[i] as Dictionary).duplicate(true)
		var raw_node := int(row.get(KEY_TLS, -1))
		if not map_node.has(raw_node):
			out.orphans.append({"table": "tls", "index": i, "id": raw_node, "reason": "节点在当前关卡里不存在"})
			continue
		row[KEY_TLS] = int(map_node[raw_node])

		var programs: Array = []
		for program in _as_array(row.get("programs")):
			var prog: Dictionary = (program as Dictionary).duplicate(true)
			var phases: Array = []
			for phase in _as_array(prog.get("phases")):
				var ph: Dictionary = (phase as Dictionary).duplicate(true)
				var states: Dictionary = ph.get("states", {})
				var fixed := {}
				for raw_turn in states.keys():
					var tid := int(raw_turn)
					if not map_turn.has(tid):
						out.orphans.append({"table": "tls.states", "index": i, "id": tid,
							"reason": "转向在当前关卡里不存在"})
						continue
					fixed[int(map_turn[tid])] = String(states[raw_turn])
				ph["states"] = fixed
				phases.append(ph)
			prog["phases"] = phases
			programs.append(prog)
		row["programs"] = programs
		result.append(row)
	return result


func _remap_rows(rows: Array, key: String, id_map: Dictionary, out: SumoProfile,
		table: String, optional: bool = false) -> Array:
	var result: Array = []
	for i in rows.size():
		var row: Dictionary = (rows[i] as Dictionary).duplicate(true)
		if not row.has(key):
			out.orphans.append({"table": table, "index": i, "id": -1, "reason": "缺少 %s" % key})
			continue
		var raw := int(row[key])
		if optional and raw < 0:
			result.append(row)                # 可选外键：-1 = 没绑，原样保留
			continue
		if not id_map.has(raw):
			out.orphans.append({"table": table, "index": i, "id": raw, "reason": "id 在当前关卡里不存在"})
			continue
		row[key] = int(id_map[raw])
		result.append(row)
	return result


static func _as_array(v) -> Array:
	if v is Array:
		return v
	return []
