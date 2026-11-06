class_name SumoBusModel
extends RefCounted

## 公交线路（`bus_lines[]`）的纯数据操作层。
##
## **一条线路 = 起点进口 + 发车间隔 + 停靠哪些站**。
## 路线本身**不让用户画** —— 导出时按起点进口的转率枚举，取权重最大的那条
## （与"用户永不写路线"的原则一致）。站台不在该路线上的会被过滤掉并告警。
##
## 与 `additional[]` 里公交站的关系：`stops` 存的是**站 id 字符串**。
## 站被删后引用会悬空 —— 导出端过滤并告警，这里只负责读写。

## 默认发车间隔（秒）：5 分钟一班
const DEFAULT_HEADWAY := 300.0


# ============================================================
# 读
# ============================================================

## 从某条车道发出的线路
static func lines_of(profile: SumoProfile, lane_id: int) -> Array:
	var out: Array = []
	if profile == null:
		return out
	for row in profile.bus_lines:
		if int(row.get("lane", -1)) == lane_id:
			out.append(row)
	return out


static func all_of(profile: SumoProfile) -> Array:
	return [] if profile == null else profile.bus_lines


static func find_index(profile: SumoProfile, line_id: String) -> int:
	if profile == null:
		return -1
	for i in profile.bus_lines.size():
		if String(profile.bus_lines[i].get("id", "")) == line_id:
			return i
	return -1


static func find(profile: SumoProfile, line_id: String) -> Dictionary:
	var i := find_index(profile, line_id)
	return {} if i < 0 else profile.bus_lines[i]


## 全图的公交站（可选来停靠的那些）
static func stop_choices(profile: SumoProfile) -> Array:
	var out: Array = []
	if profile == null:
		return out
	for row in profile.additional:
		if String(row.get("kind", "")) == SumoFacilitiesModel.KIND_BUS_STOP:
			out.append(row)
	out.sort_custom(func(a, b) -> bool:
		return String(a.get("id", "")) < String(b.get("id", "")))
	return out


## 该线路的停靠站 id 列表（按用户配置的顺序；导出时会按路线重排）
static func stops_of(profile: SumoProfile, line_id: String) -> Array:
	var row := find(profile, line_id)
	return [] if row.is_empty() else (row.get("stops", []) as Array).duplicate()


static func has_stop(profile: SumoProfile, line_id: String, stop_id: String) -> bool:
	return stops_of(profile, line_id).has(stop_id)


# ============================================================
# 写
# ============================================================

## 加一条从该进口发出的线路。返回新 id（空串 = 没加成）。
static func add(profile: SumoProfile, lane_id: int) -> String:
	if profile == null:
		return ""
	var row := {
		"id": _next_id(profile),
		"line": _next_line_name(profile),
		"lane": lane_id,
		"headway": DEFAULT_HEADWAY,
		"begin": 0.0,
		"end": 3600.0,
		"stops": [],
	}
	profile.bus_lines.append(row)
	return String(row["id"])


static func remove(profile: SumoProfile, line_id: String) -> bool:
	var i := find_index(profile, line_id)
	if i < 0:
		return false
	profile.bus_lines.remove_at(i)
	return true


static func set_field(profile: SumoProfile, line_id: String, key: String, value) -> bool:
	var i := find_index(profile, line_id)
	if i < 0:
		return false
	var row: Dictionary = profile.bus_lines[i]
	var old = row.get(key, null)
	if old == value:
		return false
	row[key] = value
	return true


## 改数值字段并夹取。`headway` 最小 10 秒（再密就是堵车测试了）。
static func set_number(profile: SumoProfile, line_id: String, key: String, value: float) -> bool:
	var v := value
	match key:
		"headway":
			v = clampf(v, 10.0, 3600.0)
		"begin":
			v = clampf(v, 0.0, 86400.0)
		"end":
			v = clampf(v, 0.0, 86400.0)
		_:
			pass
	return set_field(profile, line_id, key, snappedf(v, 0.1))


## 勾选/取消一个停靠站。返回**是否真的变了**。
static func toggle_stop(profile: SumoProfile, line_id: String, stop_id: String) -> bool:
	var i := find_index(profile, line_id)
	if i < 0:
		return false
	var row: Dictionary = profile.bus_lines[i]
	var stops: Array = row.get("stops", [])
	if stops.has(stop_id):
		stops.erase(stop_id)
	else:
		stops.append(stop_id)
	row["stops"] = stops
	return true


## 站被删除时，把所有线路里对它的引用清掉（否则会留一堆悬空引用）
static func drop_stop(profile: SumoProfile, stop_id: String) -> int:
	if profile == null:
		return 0
	var touched := 0
	for row in profile.bus_lines:
		var stops: Array = row.get("stops", [])
		if stops.has(stop_id):
			stops.erase(stop_id)
			row["stops"] = stops
			touched += 1
	return touched


# ============================================================
# 内部
# ============================================================

static func _next_id(profile: SumoProfile) -> String:
	var used := {}
	for row in all_of(profile):
		used[String(row.get("id", ""))] = true
	var n := 1
	while used.has("bus_%d" % n):
		n += 1
	return "bus_%d" % n


## 线路名默认从 1 开始往上取，避开已用的（用户的站台 lines 要跟它对上）
static func _next_line_name(profile: SumoProfile) -> String:
	var used := {}
	for row in all_of(profile):
		used[String(row.get("line", ""))] = true
	var n := 1
	while used.has(str(n)):
		n += 1
	return str(n)
