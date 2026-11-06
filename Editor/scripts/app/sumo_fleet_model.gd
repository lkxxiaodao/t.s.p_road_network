class_name SumoFleetModel
extends RefCounted

## 专业模式「车队」数据层：**车型库（全局定义）+ 进口车型构成 + 转率**（纯函数，零 UI、零信号）。
##
## ## 三层结构（2026-11-05 按老板要求重构）
##
## ```
## ① 车型库 vehicle_types[]   —— 全局：车长 / 限速 / 加减速 / 风格 / 颜色
## ② 进口构成 lane_mix{}       —— 每个进口（车道）各配一套比例，引用①的 id
## ③ 兜底 vehicle_mix          —— 关卡级预设（.spmap 的 game_rules，SimCore 也读它）
## ```
##
## 解析顺序（编辑器显示与 C# 导出**必须完全一致**）：
## `lane_mix[lane]` → `vehicle_types[].share` → `game_rules.vehicle_mix` 展开。
##
## ## 转率
## 落在 `.spmap` 的 `turns[].ratio`（冻结合约里的**可选字段**，缺省 1.0，≠1.0 才落盘）。
## 按进口车道看：一条进口有 ≥2 条出口转向时才有配比的意义。

## 6 个预设（与 C# 的 `SumoVehicleTypes.Presets` 一一对应，改动要同批）
const PRESETS := ["car", "bus", "truck", "bicycle", "pedestrian", "emergency"]
const PRESET_LABELS := ["小汽车", "公交", "货车", "自行车", "行人", "应急"]
const PRESET_VCLASS := {
	"car": "passenger", "bus": "bus", "truck": "truck",
	"bicycle": "bicycle", "pedestrian": "pedestrian", "emergency": "emergency",
}
## 预设的默认物理参数（与 C# 侧同口径；弹窗里"换预设"会把这些填进去）
const PRESET_DEFAULTS := {
	"car": {"length": 4.5, "max_speed": 13.89, "accel": 2.6, "decel": 4.5, "min_gap": 2.5, "color": "0.85,0.85,0.90"},
	"bus": {"length": 12.0, "max_speed": 13.89, "accel": 1.2, "decel": 4.0, "min_gap": 3.0, "color": "0.20,0.50,0.90"},
	"truck": {"length": 8.0, "max_speed": 11.11, "accel": 1.3, "decel": 4.0, "min_gap": 3.0, "color": "0.60,0.60,0.60"},
	"bicycle": {"length": 1.8, "max_speed": 5.0, "accel": 1.2, "decel": 3.0, "min_gap": 1.0, "color": "0.20,0.80,0.40"},
	"pedestrian": {"length": 0.5, "max_speed": 1.4, "accel": 1.5, "decel": 3.0, "min_gap": 0.5, "color": "0.90,0.70,0.30"},
	"emergency": {"length": 5.0, "max_speed": 22.22, "accel": 3.5, "decel": 6.0, "min_gap": 2.5, "color": "0.90,0.20,0.20"},
}

const STYLES := ["careful", "normal", "aggressive"]
const STYLE_LABELS := ["保守", "标准", "激进"]
const STYLE_TIPS := [
	"sigma 0.2 / 车头时距 1.4 s / 速度 ×0.85",
	"sigma 0.5 / 车头时距 1.0 s / 速度 ×1.0",
	"sigma 0.9 / 车头时距 0.7 s / 速度 ×1.15",
]

## 关卡预设 → 三类车份额（千分位）。★ 与 `SimCore.Sim.VehicleMixRules.Shares` 同口径。
const MIX_SHARES := {
	"CarOnly": [1000, 0, 0],
	"CityMix": [850, 120, 30],
	"FreightHeavy": [550, 450, 0],
	"BusHeavy": [700, 100, 200],
	"CarBusMix": [920, 0, 80],
}


# ============================================================
# ① 车型库（全局）
# ============================================================

static func has_custom_types(profile: SumoProfile) -> bool:
	return profile != null and not profile.vehicle_types.is_empty()


static func types_of(profile: SumoProfile) -> Array:
	return [] if profile == null else profile.vehicle_types


static func find_type(profile: SumoProfile, type_id: String) -> Dictionary:
	if profile == null:
		return {}
	for t in profile.vehicle_types:
		if String(t.get("id", "")) == type_id:
			return t
	return {}


## 新建一条车型：id 自动取 `type1` / `type2`…（避开已用的），默认参数取小汽车
static func add_type(profile: SumoProfile) -> int:
	if profile == null:
		return -1
	var used: Dictionary = {}
	for t in profile.vehicle_types:
		used[String(t.get("id", ""))] = true
	var n := 1
	while used.has("type%d" % n):
		n += 1

	var row := {"id": "type%d" % n, "preset": "car", "style": "normal"}
	for k in PRESET_DEFAULTS["car"]:
		row[k] = PRESET_DEFAULTS["car"][k]
	profile.vehicle_types.append(row)
	return profile.vehicle_types.size() - 1


static func remove_type(profile: SumoProfile, index: int) -> void:
	if profile == null or index < 0 or index >= profile.vehicle_types.size():
		return
	profile.vehicle_types.remove_at(index)


## 改车型字段。`preset` 变了会把该预设的默认物理参数**填进来**（用户随后可再微调）。
## ★ 返回 true = 真的改了（调用方据此决定要不要占一步撤销）。
static func set_type_field(profile: SumoProfile, index: int, key: String, value: Variant) -> bool:
	if profile == null or index < 0 or index >= profile.vehicle_types.size():
		return false
	var t: Dictionary = profile.vehicle_types[index]

	if key == "preset":
		var preset := String(value)
		if String(t.get("preset", "")) == preset:
			return false
		t["preset"] = preset
		var d: Dictionary = PRESET_DEFAULTS.get(preset, PRESET_DEFAULTS["car"])
		for k in d:
			t[k] = d[k]
		return true

	if key in ["length", "max_speed", "accel", "decel", "min_gap", "share"]:
		var v := float(value)
		if key != "share":
			v = maxf(0.01, v)
		else:
			v = maxf(0.0, v)
		if absf(float(t.get(key, 0.0)) - v) < 0.001:
			return false
		t[key] = v
		return true

	if key == "id":
		var new_id := String(value).strip_edges()
		if new_id == "" or new_id == String(t.get("id", "")):
			return false
		# id 是引用键：重名会让两个车型指向同一条，宁可不改
		for i in profile.vehicle_types.size():
			if i != index and String((profile.vehicle_types[i] as Dictionary).get("id", "")) == new_id:
				return false
		t["id"] = new_id
		return true

	if String(t.get(key, "")) == String(value):
		return false
	t[key] = value
	return true


# ============================================================
# ② 进口（车道）车型构成
# ============================================================

## 该进口自己配的构成（空 = 没配过，往下回落）
static func mix_of(profile: SumoProfile, lane_id: int) -> Array:
	if profile == null:
		return []
	return profile.lane_mix.get(str(lane_id), [])


## **最终生效**的构成：`lane_mix[lane]` → `vehicle_types[].share` → `vehicle_mix` 预设。
## 返回 `[{id, share}]`，share 未归一化（C# 侧会归一化）。
static func effective_mix(profile: SumoProfile, lane_id: int, vehicle_mix: String) -> Array:
	if profile == null:
		return _preset_mix(vehicle_mix)

	var own: Array = mix_of(profile, lane_id)
	if not own.is_empty():
		# 过滤掉库里已经没有的车型（删车型后留下的悬空引用）
		var out: Array = []
		for row in own:
			var tid := String(row.get("id", ""))
			if find_type(profile, tid).is_empty():
				continue
			out.append({"id": tid, "share": float(row.get("share", 0.0))})
		if not out.is_empty():
			return out
		return _preset_mix(vehicle_mix)

	if not profile.vehicle_types.is_empty():
		var from_lib: Array = []
		for t in profile.vehicle_types:
			var share := float(t.get("share", 0.0))
			if share > 0.0:
				from_lib.append({"id": String(t.get("id", "")), "share": share})
		if not from_lib.is_empty():
			return from_lib

	return _preset_mix(vehicle_mix)


## 关卡预设展开成 `[{id, share}]`
static func _preset_mix(vehicle_mix: String) -> Array:
	var shares: Array = MIX_SHARES.get(vehicle_mix, MIX_SHARES["CarOnly"])
	var keys := ["car", "truck", "bus"]
	var out: Array = []
	for i in 3:
		if int(shares[i]) <= 0:
			continue
		out.append({"id": keys[i], "share": float(shares[i]) / 10.0})
	return out


## 该进口是否"自己配过"（不是回落来的）
static func has_own_mix(profile: SumoProfile, lane_id: int) -> bool:
	return not mix_of(profile, lane_id).is_empty()


## 把"当前的生效构成"固化成这个进口自己的配置（用户第一次改比例时自动调）。
## ★ 同时保证引用的车型都进了库：否则导出时会被当"悬空引用"过滤掉，
##   表现为"改了份额，导出的车反而没了"。
static func start_own_mix(profile: SumoProfile, lane_id: int, vehicle_mix: String) -> void:
	if profile == null or has_own_mix(profile, lane_id):
		return
	var rows: Array = effective_mix(profile, lane_id, vehicle_mix)
	for r in rows:
		var tid := String(r.get("id", ""))
		if tid == "" or not find_type(profile, tid).is_empty():
			continue
		var row := {"id": tid, "preset": tid, "style": "normal"}
		var d: Dictionary = PRESET_DEFAULTS.get(tid, PRESET_DEFAULTS["car"])
		for k in d:
			row[k] = d[k]
		profile.vehicle_types.append(row)
	profile.lane_mix[str(lane_id)] = rows


static func reset_own_mix(profile: SumoProfile, lane_id: int) -> void:
	if profile != null:
		profile.lane_mix.erase(str(lane_id))


## 往该进口的构成里加一行（挑一个它还没用过的车型）
static func add_mix_row(profile: SumoProfile, lane_id: int, vehicle_mix: String) -> bool:
	if profile == null:
		return false
	start_own_mix(profile, lane_id, vehicle_mix)
	var rows: Array = mix_of(profile, lane_id)
	var used: Dictionary = {}
	for r in rows:
		used[String(r.get("id", ""))] = true

	var candidate := ""
	for t in profile.vehicle_types:
		var tid := String(t.get("id", ""))
		if not used.has(tid):
			candidate = tid
			break
	if candidate == "":
		# 库里全都用过了（或库是空的）→ 先建一条再挂上
		var idx := add_type(profile)
		if idx < 0:
			return false
		candidate = String((profile.vehicle_types[idx] as Dictionary).get("id", ""))
		if candidate == "":
			return false

	rows.append({"id": candidate, "share": 10.0})
	profile.lane_mix[str(lane_id)] = rows
	return true


static func remove_mix_row(profile: SumoProfile, lane_id: int, index: int) -> bool:
	if profile == null:
		return false
	var rows: Array = mix_of(profile, lane_id)
	if index < 0 or index >= rows.size():
		return false
	rows.remove_at(index)
	if rows.is_empty():
		profile.lane_mix.erase(str(lane_id))     # 删空 = 回到"跟随默认"
	else:
		profile.lane_mix[str(lane_id)] = rows
	return true


static func set_mix_type(profile: SumoProfile, lane_id: int, index: int, type_id: String) -> bool:
	if profile == null:
		return false
	var rows: Array = mix_of(profile, lane_id)
	if index < 0 or index >= rows.size():
		return false
	if String(rows[index].get("id", "")) == type_id:
		return false
	rows[index]["id"] = type_id
	profile.lane_mix[str(lane_id)] = rows
	return true


static func set_mix_share(profile: SumoProfile, lane_id: int, index: int, share: float) -> bool:
	if profile == null:
		return false
	var rows: Array = mix_of(profile, lane_id)
	if index < 0 or index >= rows.size():
		return false
	var v := maxf(0.0, share)
	if absf(float(rows[index].get("share", 0.0)) - v) < 0.001:
		return false
	rows[index]["share"] = v
	profile.lane_mix[str(lane_id)] = rows
	return true


static func mix_total(rows: Array) -> float:
	var sum := 0.0
	for r in rows:
		sum += float(r.get("share", 0.0))
	return sum


# ============================================================
# ③ 转率（按进口车道）
# ============================================================

static func ratio_of(doc: Doc, turn_id: int) -> float:
	if doc == null:
		return 1.0
	var t: Dictionary = doc.find_turn(turn_id)
	if t.is_empty():
		return 1.0
	return float(t.get("ratio", 1.0))


static func set_ratio(doc: Doc, turn_id: int, value: float) -> bool:
	if doc == null:
		return false
	var t: Dictionary = doc.find_turn(turn_id)
	if t.is_empty():
		return false
	var v: float = clampf(snappedf(value, 0.01), 0.01, 1.0)   # 契约要求 ratio ∈ (0, 1]
	if absf(float(t.get("ratio", 1.0)) - v) < 0.001:
		return false
	t["ratio"] = v
	return true


## 某条**进口车道**的机动车出口转向：`[{id, type, dst, ratio}]`
## ★ 只收机动车转向（人行横道 / 自行车不算），因为转率是给车流用的。
static func turn_entries(doc: Doc, lane_id: int) -> Array:
	if doc == null:
		return []
	var out: Array = []
	for t in doc.turns:
		if int(t.get("src_lane", -1)) != lane_id:
			continue
		var src: Dictionary = doc.find_lane(int(t.get("src_lane", -1)))
		var dst: Dictionary = doc.find_lane(int(t.get("dst_lane", -1)))
		if src.is_empty() or dst.is_empty():
			continue
		if not Doc.MOTOR_LANE_TYPES.has(String(src.get("type", ""))):
			continue
		out.append({
			"id": int(t["id"]),
			"type": String(t.get("type", "Straight")),
			"dst": int(dst["id"]),
			"ratio": float(t.get("ratio", 1.0)),
		})
	out.sort_custom(func(a, b): return int(a["id"]) < int(b["id"]))
	return out


## 某条车道是不是"进口"——有没有驶出它的机动车转向。
## 出口车道（只被别的转向指向）不该配车流，面板据此给提示。
static func is_entry_lane(doc: Doc, lane_id: int) -> bool:
	return not turn_entries(doc, lane_id).is_empty()


## 归一化后的百分比（面板显示用）：`[{id, type, pct}]`
## ★ 只算机动车转向；总和为 0 时按均分显示。
static func normalized_percent(doc: Doc, lane_id: int) -> Array:
	var turns := turn_entries(doc, lane_id)
	var total := 0.0
	for t in turns:
		total += float(t["ratio"])
	var out: Array = []
	for t in turns:
		var pct := 100.0 / float(turns.size()) if total <= 0.0 else float(t["ratio"]) / total * 100.0
		out.append({"id": int(t["id"]), "type": String(t["type"]), "pct": pct})
	return out
