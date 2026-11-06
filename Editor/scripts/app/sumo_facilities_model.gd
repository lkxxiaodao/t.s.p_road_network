class_name SumoFacilitiesModel
extends RefCounted

## SUMO 检测设施（`additional[]`）的纯数据操作层。
##
## **零信号、零 UI 依赖**，与 `SumoTlsModel` / `SumoFleetModel` 同口径：
## 面板只调这里，这里只读写 `SumoProfile.additional`。
##
## 四种设施：
##   - `e1` 断面流量：线圈，出「一段时间过了几辆车」
##   - `e2` 排队检测：一段检测区，出「排队长度 / 停车次数」
##   - `bus_stop` 公交站：站台 + 停靠时长
##   - `parking` 停车场：路边停车位
##
## ★ **位置口径**：文件里存 `pos`（从**车道起点**算，SUMO 的口径）；
##   界面上给的是「离路口多远」——换算只在本文件的 `*_from_road` 里做，
##   别在面板里各写一遍（两处各写一遍迟早不一致）。

const KIND_E1 := "e1"
const KIND_E2 := "e2"
const KIND_BUS_STOP := "bus_stop"
const KIND_PARKING := "parking"

const KINDS := [KIND_E1, KIND_E2, KIND_BUS_STOP, KIND_PARKING]
const KIND_LABELS := {
	KIND_E1: "断面流量",
	KIND_E2: "排队检测",
	KIND_BUS_STOP: "公交站",
	KIND_PARKING: "停车场",
}

## 各类型的默认参数（新增时用；之后用户可改）
const DEFAULTS := {
	KIND_E1: {"length": 0.0, "freq": 60.0, "duration": 0.0, "capacity": 0},
	KIND_E2: {"length": 20.0, "freq": 60.0, "duration": 0.0, "capacity": 0},
	KIND_BUS_STOP: {"length": 15.0, "freq": 60.0, "duration": 20.0, "capacity": 0},
	KIND_PARKING: {"length": 20.0, "freq": 60.0, "duration": 300.0, "capacity": 5},
}


# ============================================================
# 读
# ============================================================

## 某条车道上的全部设施（按 类型 → 位置 排序）
static func list_of(profile: SumoProfile, lane_id: int) -> Array:
	var out: Array = []
	if profile == null:
		return out
	for row in profile.additional:
		if int(row.get("lane", -1)) == lane_id:
			out.append(row)
	out.sort_custom(func(a, b) -> bool:
		var ka := KINDS.find(String(a.get("kind", "")))
		var kb := KINDS.find(String(b.get("kind", "")))
		if ka != kb:
			return ka < kb
		return float(a.get("pos", a.get("start", 0.0))) < float(b.get("pos", b.get("start", 0.0))))
	return out


## 全图的设施（导出前体检、画布绘制用）
static func all_of(profile: SumoProfile) -> Array:
	return [] if profile == null else profile.additional


static func count_of(profile: SumoProfile, kind: String) -> int:
	var n := 0
	for row in all_of(profile):
		if String(row.get("kind", "")) == kind:
			n += 1
	return n


static func find_index(profile: SumoProfile, facility_id: String) -> int:
	if profile == null:
		return -1
	for i in profile.additional.size():
		if String(profile.additional[i].get("id", "")) == facility_id:
			return i
	return -1


static func find(profile: SumoProfile, facility_id: String) -> Dictionary:
	var i := find_index(profile, facility_id)
	return {} if i < 0 else profile.additional[i]


# ============================================================
# 类型可用性
# ============================================================

## 这条车道能不能建检测器/公交站。
## 人行道与自行车道上建线圈没有意义（SUMO 里那两条 lane 不过机动车）。
static func supports_detector(lane_type: String) -> bool:
	return Doc.MOTOR_LANE_TYPES.has(lane_type)


## 公交站：能建在机动车道 / 公交道上。
## SUMO 允许建在人行道上（乘客在 sidewalk 等车），但我们的 net 里人行道与机动车道
## 是同一条 edge 的不同 lane，建在机动车道上「车停 + 人上」都能跑，口径统一。
static func supports_bus_stop(lane_type: String) -> bool:
	return Doc.MOTOR_LANE_TYPES.has(lane_type)


static func supports_parking(lane_type: String) -> bool:
	return Doc.MOTOR_LANE_TYPES.has(lane_type)


# ============================================================
# 位置换算：「离路口多远」 ↔ pos
# ============================================================

## 这条车道的路口端是不是 `to_node`。
## 判据：有**驶出**它的转向（`turn.src_lane == lane`）→ 车往 to_node 走，路口在 to_node。
## 出口车道（只有转向驶入）→ 路口在 from_node。
static func junction_at_to(doc: Doc, lane_id: int) -> bool:
	if doc == null:
		return true
	for t in doc.turns:
		if int(t.get("src_lane", -1)) == lane_id and t.get("leg", 0) != 1:
			return true
	return false


static func lane_length(doc: Doc, lane_id: int) -> float:
	if doc == null:
		return 0.0
	var lane: Dictionary = doc.find_lane(lane_id)
	return float(lane.get("length", 0.0))


## 界面上看到的「离路口 N 米」→ 文件里的 pos
static func pos_from_road(doc: Doc, lane_id: int, road_distance: float) -> float:
	var total := lane_length(doc, lane_id)
	var d: float = clampf(road_distance, 0.0, maxf(total, 0.0))
	return d if not junction_at_to(doc, lane_id) else maxf(total - d, 0.0)


## 文件里的 pos → 界面上显示的「离路口 N 米」
static func road_from_pos(doc: Doc, lane_id: int, pos: float) -> float:
	var total := lane_length(doc, lane_id)
	var p: float = clampf(pos, 0.0, maxf(total, 0.0))
	return p if not junction_at_to(doc, lane_id) else maxf(total - p, 0.0)


## 站台 / 停车区的起止也按「离路口」显示：给中心点 + 长度，换算成 start/end。
## 返回 `{"start": ..., "end": ...}`（已保证 end - start >= 1）。
static func span_from_road(doc: Doc, lane_id: int, road_distance: float, length_m: float) -> Dictionary:
	var total := lane_length(doc, lane_id)
	var half: float = maxf(length_m, 1.0) * 0.5
	var center_pos := pos_from_road(doc, lane_id, road_distance)
	var start: float = clampf(center_pos - half, 0.0, maxf(total - 1.0, 0.0))
	var end: float = clampf(start + maxf(length_m, 1.0), start + 1.0, maxf(total, start + 1.0))
	return {"start": snappedf(start, 0.1), "end": snappedf(end, 0.1)}


# ============================================================
# 写
# ============================================================

## 加一个设施。返回新条目的 id（空串 = 没加成）。
static func add(doc: Doc, profile: SumoProfile, lane_id: int, kind: String) -> String:
	if profile == null or not KINDS.has(kind):
		return ""
	var lane_length_m := lane_length(doc, lane_id)
	if lane_length_m <= 2.0:
		return ""

	var def: Dictionary = DEFAULTS.get(kind, {})
	var length_m: float = float(def.get("length", 0.0))
	# 默认位置：车道中部（离路口 30 m，但不超过车道的一半）
	var road_distance: float = minf(30.0, lane_length_m * 0.5)
	if length_m <= 0.0:
		length_m = 15.0

	var row := {
		"id": _next_id(profile, lane_id, kind),
		"kind": kind,
		"lane": lane_id,
		"name": "",
		"freq": float(def.get("freq", 60.0)),
		"duration": float(def.get("duration", 0.0)),
		"capacity": int(def.get("capacity", 0)),
	}

	if kind == KIND_E1:
		row["pos"] = snappedf(pos_from_road(doc, lane_id, road_distance), 0.1)
		row["length"] = 0.0
	elif kind == KIND_E2:
		row["length"] = float(def.get("length", 20.0))
		# e2 的 pos 是检测区**末端**，往前 length：中心点 + 半个 length，再夹进车道
		var center_pos := pos_from_road(doc, lane_id, road_distance)
		row["pos"] = snappedf(clampf(center_pos + float(row["length"]) * 0.5,
			0.0, maxf(lane_length_m - 0.1, 0.0)), 0.1)
	elif kind == KIND_BUS_STOP or kind == KIND_PARKING:
		var span := span_from_road(doc, lane_id, road_distance, length_m)
		row["start"] = span["start"]
		row["end"] = span["end"]
		if kind == KIND_BUS_STOP:
			row["lines"] = "bus"

	profile.additional.append(row)
	return String(row["id"])


static func remove(profile: SumoProfile, facility_id: String) -> bool:
	var i := find_index(profile, facility_id)
	if i < 0:
		return false
	profile.additional.remove_at(i)
	return true


## 改一个字段。返回**是否真的变了**（没变就别占一步撤销）。
static func set_field(profile: SumoProfile, facility_id: String, key: String, value) -> bool:
	var i := find_index(profile, facility_id)
	if i < 0:
		return false
	var row: Dictionary = profile.additional[i]
	var old = row.get(key, null)
	if old == null and value == null:
		return false
	if old != null and value != null and typeof(old) == typeof(value) and old == value:
		return false
	row[key] = value
	return true


## 改数值字段并做边界夹取（面板 SpinBox 的 settle 用）。
static func set_number(
	doc: Doc, profile: SumoProfile, facility_id: String, key: String, value: float) -> bool:
	var i := find_index(profile, facility_id)
	if i < 0:
		return false
	var row: Dictionary = profile.additional[i]
	var total := lane_length(doc, int(row.get("lane", -1)))

	var v := value
	match key:
		"pos":
			v = clampf(v, 0.0, maxf(total - 0.1, 0.0))
		"length":
			v = clampf(v, 1.0, maxf(total, 1.0))
		"freq":
			v = clampf(v, 1.0, 3600.0)
		"duration":
			v = clampf(v, 0.0, 3600.0)
		"capacity":
			v = clampf(v, 0.0, 999.0)
		_:
			pass
	return set_field(profile, facility_id, key, snappedf(v, 0.1))


## 改「离路口多远」（面板上的显示值）→ 写回 pos
static func set_road_distance(
	doc: Doc, profile: SumoProfile, facility_id: String, road_distance: float) -> bool:
	var row := find(profile, facility_id)
	if row.is_empty():
		return false
	var lane_id := int(row.get("lane", -1))
	var pos := pos_from_road(doc, lane_id, road_distance)
	return set_number(doc, profile, facility_id, "pos", pos)


## 改站台/停车区的中心「离路口多远」→ 同时平移 start/end（保持长度）
static func set_span_road_distance(
	doc: Doc, profile: SumoProfile, facility_id: String, road_distance: float) -> bool:
	var i := find_index(profile, facility_id)
	if i < 0:
		return false
	var row: Dictionary = profile.additional[i]
	var lane_id := int(row.get("lane", -1))
	var total := lane_length(doc, lane_id)
	var span := maxf(float(row.get("end", 0.0)) - float(row.get("start", 0.0)), 1.0)
	var center_pos := pos_from_road(doc, lane_id, road_distance)
	var start: float = clampf(center_pos - span * 0.5, 0.0, maxf(total - span, 0.0))
	var end: float = clampf(start + span, start + 1.0, maxf(total, start + 1.0))
	var changed := absf(start - float(row.get("start", 0.0))) > 0.001
	row["start"] = snappedf(start, 0.1)
	row["end"] = snappedf(end, 0.1)
	return changed


## 站台/停车区改长度 → 保持起点不变（往路口方向伸）
static func set_span_length(
	doc: Doc, profile: SumoProfile, facility_id: String, length_m: float) -> bool:
	var i := find_index(profile, facility_id)
	if i < 0:
		return false
	var row: Dictionary = profile.additional[i]
	var lane_id := int(row.get("lane", -1))
	var total := lane_length(doc, lane_id)
	var start: float = float(row.get("start", 0.0))
	var end: float = clampf(start + maxf(length_m, 1.0), start + 1.0, maxf(total, start + 1.0))
	if absf(end - float(row.get("end", 0.0))) < 0.001:
		return false
	row["end"] = snappedf(end, 0.1)
	return true


## 一条车道上的设施全删（车道被删时兜底；正常走重映射的 orphan）
static func remove_lane(profile: SumoProfile, lane_id: int) -> int:
	if profile == null:
		return 0
	var before := profile.additional.size()
	profile.additional = profile.additional.filter(
		func(r): return int(r.get("lane", -1)) != lane_id)
	return before - profile.additional.size()


# ============================================================
# 内部
# ============================================================

## id 要稳定、可读、不撞号：`<kind>_<lane>_<n>`
static func _next_id(profile: SumoProfile, lane_id: int, kind: String) -> String:
	var prefix := "%s_%d" % [kind, lane_id]
	var used := {}
	for row in all_of(profile):
		used[String(row.get("id", ""))] = true
	var n := 1
	while used.has("%s_%d" % [prefix, n]):
		n += 1
	return "%s_%d" % [prefix, n]
