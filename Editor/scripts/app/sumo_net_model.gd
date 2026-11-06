class_name SumoNetModel
extends RefCounted

## SUMO 网络覆盖层（S4）的纯数据操作层。
##
## **零信号、零 UI 依赖**，与 `SumoFacilitiesModel` / `SumoTlsModel` 同口径：
## 面板只调这里，这里只读写 `SumoProfile.overrides` 与 `SumoProfile.netconvert`。
##
## ## 三张表（S0 就预留了形状，S4 才真正启用）
## ```
## overrides.junctions  = [{node, type}]                  路口控制方式
## overrides.lanes      = [{lane, access?, speed?, width?}] 车道允许车型 / 限速 / 宽度
## overrides.connections= [{turn, access?, speed?}]        连接允许车型 / 限速
## ```
##
## ## ★ 为什么"空值 = 删行"
## 缺省不写（与 `.spmap` / `.spdeco.json` 同口径）：用户把某一项改回"跟随类型"时，
## 整行**从文件里消失**，而不是留一个 `{"lane": 3, "access": ""}`。这样
## ① 导出端只看"有没有这一行"就够了，不用判空串；
## ② 专业档不会因为打开过一次面板就冒出一堆空行（污染 diff）。
##
## ## ★ id 口径
## 这里拿到/写出的 id 都是 **live id**（当前 `Doc` 的 id）。存盘时
## `SumoProfile.remapped()` 会翻译成文件 id —— 本文件**不参与** id 翻译。
##
## ## ★ 允许车型只给 4 档，不暴露 SUMO 的 30 多种 vClass
## 技术方案06 §6.2：「隐藏 SUMO 内部概念」。真要更细的粒度，去 netedit 改。

## 车道的 `access` 取值。空串 = 不覆盖（跟随车道类型自身的能力）。
const ACCESS_FOLLOW := ""
const ACCESS_BUS := "bus"
const ACCESS_BIKE := "bicycle"
const ACCESS_PED := "pedestrian"

const LANE_ACCESS := [ACCESS_FOLLOW, ACCESS_BUS, ACCESS_BIKE, ACCESS_PED]
const LANE_ACCESS_LABELS := ["跟随车道类型", "仅公交", "仅自行车", "仅行人"]

## 连接的 `access` 取值。`none` = 不允许任何车辆走这条连接。
## ★ 实测（2026-11-06）：**不能靠删 `<connection>` 实现禁用** —— SUMO 会直接段错误退出
##   （junction 的 request/foes 表与连接强耦合）。改成给连接写 `allow="pedestrian"`
##   ：机动车与自行车不再走它，结构不动，SUMO 跑得通。
const TURN_ACCESS := [ACCESS_FOLLOW, ACCESS_BUS, "none"]
const TURN_ACCESS_LABELS := ["跟随进口车道", "仅公交", "禁止机动车通行"]

## 路口控制方式。空串 = 不覆盖（netconvert 按路数自己决定）。
## 信号路口**不许**改（改了 tlLogic 还在但 junction 不再受它控制，配时静默失效）。
const JUNCTION_TYPES := [ACCESS_FOLLOW, "priority", "right_before_left", "unregulated"]
const JUNCTION_TYPE_LABELS := ["自动（按路数）", "优先让行", "全向停车", "无控制"]

## netconvert 开关。键名即 `.sumo.json` 的 `netconvert` 段的键。
## ★ 默认值必须让**基础模式产物逐字节不变**：`no_turnarounds` 一直是硬编码 true。
const NETCONVERT_KEYS := ["no_turnarounds", "no_internal_links", "lefthand", "junctions_join"]
const NETCONVERT_LABELS := {
	"no_turnarounds": "禁止掉头",
	"no_internal_links": "不生成内部车道",
	"lefthand": "左侧通行",
	"junctions_join": "合并相邻路口",
}
const NETCONVERT_TIPS := {
	"no_turnarounds": "路口不再生成掉头连接。默认开（与既有导出一致）。",
	"no_internal_links": "路口内不再生成内部车道。link 数不变，灯位编号不受影响。",
	"lefthand": "按左侧通行规则重新生成整个路网（英国/日本式）。",
	"junctions_join": "距离很近的两个路口合并成一个。★ 会改变路口 id，已配的专业配时可能对不上。",
}
const NETCONVERT_DEFAULTS := {
	"no_turnarounds": true,
	"no_internal_links": false,
	"lefthand": false,
	"junctions_join": false,
}

## 表名 → 外键字段（改动这里等于改 schema，必须同批改 `docs/sumo-profile-schema.md`）
const KEY_JUNCTION := "node"
const KEY_LANE := "lane"
const KEY_TURN := "turn"


# ============================================================
# 车道
# ============================================================

static func _lanes(profile: SumoProfile) -> Array:
	if profile == null:
		return []
	var raw = profile.overrides.get("lanes", [])
	return raw if raw is Array else []


static func lane_row(profile: SumoProfile, lane_id: int) -> Dictionary:
	for row in _lanes(profile):
		if int(row.get(KEY_LANE, -1)) == lane_id:
			return row
	return {}


## 车道有没有被覆盖（面板据此决定"有没有东西可清"）
static func has_lane(profile: SumoProfile, lane_id: int) -> bool:
	return not lane_row(profile, lane_id).is_empty()


## 改一个车道字段。value 传空串 / 0 / 0.0 = **删掉这个字段**；
## 整行没有字段了就把行也删掉。返回是否真的变了（没变别占一步撤销）。
static func set_lane_field(profile: SumoProfile, lane_id: int, key: String, value) -> bool:
	if profile == null:
		return false
	if not key in ["access", "speed", "width"]:
		return false

	var rows := _lanes(profile)
	var index := -1
	for i in rows.size():
		if int(rows[i].get(KEY_LANE, -1)) == lane_id:
			index = i
			break

	var empty := _is_blank(value)
	if index < 0:
		if empty:
			return false                 # 本来就没这一行、又要设空 → 什么都没变
		rows.append({KEY_LANE: lane_id, key: value})
		profile.overrides["lanes"] = rows
		return true

	var row: Dictionary = rows[index]
	var old = row.get(key, null)
	if empty:
		if not row.has(key):
			return false
		row.erase(key)
	else:
		if old != null and _same(old, value):
			return false
		row[key] = value

	if row.size() <= 1:                  # 只剩外键了 → 整行去掉，文件里不留空壳
		rows.remove_at(index)
	if rows.is_empty():
		profile.overrides.erase("lanes")
	else:
		profile.overrides["lanes"] = rows
	return true


## 清掉一条车道的全部覆盖
static func clear_lane(profile: SumoProfile, lane_id: int) -> bool:
	var rows := _lanes(profile)
	var before := rows.size()
	rows = rows.filter(func(r): return int(r.get(KEY_LANE, -1)) != lane_id)
	if rows.size() == before:
		return false
	profile.overrides["lanes"] = rows
	if rows.is_empty():
		profile.overrides.erase("lanes")
	return true


# ============================================================
# 连接
# ============================================================

static func _connections(profile: SumoProfile) -> Array:
	if profile == null:
		return []
	var raw = profile.overrides.get("connections", [])
	return raw if raw is Array else []


static func connection_row(profile: SumoProfile, turn_id: int) -> Dictionary:
	for row in _connections(profile):
		if int(row.get(KEY_TURN, -1)) == turn_id:
			return row
	return {}


static func has_connection(profile: SumoProfile, turn_id: int) -> bool:
	return not connection_row(profile, turn_id).is_empty()


static func set_connection_field(
		profile: SumoProfile, turn_id: int, key: String, value) -> bool:
	if profile == null:
		return false
	if not key in ["access", "speed"]:
		return false

	var rows := _connections(profile)
	var index := -1
	for i in rows.size():
		if int(rows[i].get(KEY_TURN, -1)) == turn_id:
			index = i
			break

	var empty := _is_blank(value)
	if index < 0:
		if empty:
			return false
		rows.append({KEY_TURN: turn_id, key: value})
		profile.overrides["connections"] = rows
		return true

	var row: Dictionary = rows[index]
	var old = row.get(key, null)
	if empty:
		if not row.has(key):
			return false
		row.erase(key)
	else:
		if old != null and _same(old, value):
			return false
		row[key] = value

	if row.size() <= 1:
		rows.remove_at(index)
	if rows.is_empty():
		profile.overrides.erase("connections")
	else:
		profile.overrides["connections"] = rows
	return true


static func clear_connection(profile: SumoProfile, turn_id: int) -> bool:
	var rows := _connections(profile)
	var before := rows.size()
	rows = rows.filter(func(r): return int(r.get(KEY_TURN, -1)) != turn_id)
	if rows.size() == before:
		return false
	profile.overrides["connections"] = rows
	if rows.is_empty():
		profile.overrides.erase("connections")
	return true


# ============================================================
# 路口
# ============================================================

static func _junctions(profile: SumoProfile) -> Array:
	if profile == null:
		return []
	var raw = profile.overrides.get("junctions", [])
	return raw if raw is Array else []


static func junction_row(profile: SumoProfile, node_id: int) -> Dictionary:
	for row in _junctions(profile):
		if int(row.get(KEY_JUNCTION, -1)) == node_id:
			return row
	return {}


## 路口当前的控制方式（空串 = 自动）
static func junction_type(profile: SumoProfile, node_id: int) -> String:
	return String(junction_row(profile, node_id).get("type", ""))


## 改路口控制方式。传空串 = 回到自动（删行）。
static func set_junction_type(profile: SumoProfile, node_id: int, type: String) -> bool:
	if profile == null:
		return false
	if not JUNCTION_TYPES.has(type):
		return false

	var rows := _junctions(profile)
	var index := -1
	for i in rows.size():
		if int(rows[i].get(KEY_JUNCTION, -1)) == node_id:
			index = i
			break

	if type == ACCESS_FOLLOW:
		if index < 0:
			return false
		rows.remove_at(index)
		profile.overrides["junctions"] = rows
		if rows.is_empty():
			profile.overrides.erase("junctions")
		return true

	if index >= 0:
		if String(rows[index].get("type", "")) == type:
			return false
		rows[index]["type"] = type
	else:
		rows.append({KEY_JUNCTION: node_id, "type": type})
	profile.overrides["junctions"] = rows
	return true


# ============================================================
# netconvert 开关
# ============================================================

## 当前开关值（没配过 → 用 NETCONVERT_DEFAULTS）
static func netconvert_option(profile: SumoProfile, key: String) -> bool:
	if profile != null and profile.netconvert.has(key):
		return bool(profile.netconvert[key])
	return bool(NETCONVERT_DEFAULTS.get(key, false))


## 改开关。改回默认值时**删键**，让文件里只留"与默认不同"的那些。
static func set_netconvert_option(profile: SumoProfile, key: String, on: bool) -> bool:
	if profile == null or not NETCONVERT_KEYS.has(key):
		return false
	var was := netconvert_option(profile, key)
	if was == on:
		return false
	var def := bool(NETCONVERT_DEFAULTS.get(key, false))
	if on == def:
		profile.netconvert.erase(key)
	else:
		profile.netconvert[key] = on
	return true


## 有任何一个开关偏离默认（面板显示"已改 N 项"用）
static func netconvert_changed_count(profile: SumoProfile) -> int:
	var n := 0
	for key in NETCONVERT_KEYS:
		if netconvert_option(profile, key) != bool(NETCONVERT_DEFAULTS.get(key, false)):
			n += 1
	return n


# ============================================================
# 汇总 / 兜底
# ============================================================

## 覆盖层里一共有多少条（导出摘要 / 面板引导用）
static func total_count(profile: SumoProfile) -> int:
	return _junctions(profile).size() + _lanes(profile).size() + _connections(profile).size()


## 车道/连接/路口被删掉后，清掉指向它的孤儿行。
## ★ 正常路径走 `remapped()` 的 orphan 机制（**不静默丢数据**）；
##   这个函数只给"用户主动清理"按钮用。
static func prune(doc: Doc, profile: SumoProfile) -> int:
	if profile == null or doc == null:
		return 0
	var removed := 0
	var rows_l := _lanes(profile)
	var kept_l := rows_l.filter(func(r): return not doc.find_lane(int(r.get(KEY_LANE, -1))).is_empty())
	removed += rows_l.size() - kept_l.size()
	if kept_l.is_empty():
		profile.overrides.erase("lanes")
	else:
		profile.overrides["lanes"] = kept_l

	var rows_c := _connections(profile)
	var kept_c := rows_c.filter(func(r): return not doc.find_turn(int(r.get(KEY_TURN, -1))).is_empty())
	removed += rows_c.size() - kept_c.size()
	if kept_c.is_empty():
		profile.overrides.erase("connections")
	else:
		profile.overrides["connections"] = kept_c

	var rows_j := _junctions(profile)
	var kept_j := rows_j.filter(func(r): return not doc.find_node(int(r.get(KEY_JUNCTION, -1))).is_empty())
	removed += rows_j.size() - kept_j.size()
	if kept_j.is_empty():
		profile.overrides.erase("junctions")
	else:
		profile.overrides["junctions"] = kept_j

	return removed


# ============================================================
# 内部
# ============================================================

static func _is_blank(value) -> bool:
	if value == null:
		return true
	if value is String:
		return (value as String).strip_edges() == ""
	if value is float or value is int:
		return float(value) <= 0.0
	return false


static func _same(a, b) -> bool:
	if a is String or b is String:
		return String(a) == String(b)
	if (a is float or a is int) and (b is float or b is int):
		return absf(float(a) - float(b)) < 0.0005
	return a == b
