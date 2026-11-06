class_name SumoCheckModel
extends RefCounted

## 专业档体检（S6）：**导出前**就能查的 5 条。
##
## 与 C# 侧 `SumoCheck`（导出后读 net.xml 查另外 4 条）分工：
## 那边要看真实的 link 表 / 车道长度，这边只看 `.spmap` + `.sumo.json` 两份数据。
##
## ## 返回格式与 SimCore 的校验条目**完全一致**
## `{Code, Severity, Message, Path}` —— 直接并进 `EditorApp.validation_error_items`，
## 于是「校验问题」浮窗里能列出来、点一条能定位（`locate_validation` 认 `nodes[]/lanes[]/turns[]`）。
##
## ## ★ Path 用**文件 id**
## `turns[12].sumo` 里的 12 是 renumber 之后的文件 id（与 SimCore 校验同口径）。
## 所以调用方必须在 `_export_json_with_inv()` **之后**调本类（那时 `_fwd_*` 才建好）。
##
## ## 五条规则
## | 码 | 级别 | 查什么 |
## | --- | --- | --- |
## | `sumo_vehicle_type_missing` | Error | 进口构成 / 车流引用了不存在的车型 id |
## | `sumo_demand_on_exit_lane` | Error | 需求挂在出口车道（那里没有驶出转向，车永远发不出来） |
## | `sumo_flow_out_of_range` | Warning | 车流时段超出「结束时刻」，那段车不会出现 |
## | `sumo_tls_without_signal` | Warning | 给非信号路口配了专业配时（导出的 net 里没这个灯） |
## | `sumo_orphan_entry` | Warning | 专业档里的条目指向已删除的车道 / 转向 |

const SEV_ERROR := "Error"
const SEV_WARNING := "Warning"


## 跑一遍。返回条目数组（没有问题时是空数组）。
static func run(doc: Doc, profile: SumoProfile, fwd_node: Dictionary,
		fwd_lane: Dictionary, fwd_turn: Dictionary) -> Array:
	var out: Array = []
	if doc == null or profile == null or not profile.has_content():
		return out
	_check_vehicle_types(profile, fwd_lane, out)
	_check_demand_direction(doc, fwd_lane, out)
	_check_flow_segments(profile, fwd_lane, out)
	_check_tls_without_signal(doc, profile, fwd_node, out)
	_check_tls_state_turns(doc, profile, out)
	_check_orphans(profile, out)
	return out


# ============================================================
# 1. 车型 id 引用
# ============================================================

## 已知车型 = 6 个预设 + 车型库里自定义的。**空 id 不算错**（= 跟随进口构成）。
static func _check_vehicle_types(profile: SumoProfile, fwd_lane: Dictionary, out: Array) -> void:
	var known := {}
	for preset in SumoFleetModel.PRESETS:
		known[String(preset)] = true
	for vt in profile.vehicle_types:
		var id := String(vt.get("id", ""))
		if id != "":
			known[id] = true

	# 进口构成
	for lane_key in profile.lane_mix.keys():
		for row in profile.lane_mix[lane_key]:
			var id := String((row as Dictionary).get("id", ""))
			if id == "" or known.has(id):
				continue
			out.append(_item("sumo_vehicle_type_missing", SEV_ERROR,
				"进口构成引用了不存在的车型「%s」。导出会按预设临时补一条，但比例不是你要的。" % id,
				_lane_path(fwd_lane, int(lane_key))))

	# 三类车流
	for pair in [["机动车流", profile.flows], ["行人流", profile.person_flows],
			["非机动车流", profile.bike_flows]]:
		var label := String(pair[0])
		for row in pair[1]:
			var d: Dictionary = row
			var id := String(d.get("vtype", ""))
			if id == "" or known.has(id):
				continue
			out.append(_item("sumo_vehicle_type_missing", SEV_ERROR,
				"%s 引用了不存在的车型「%s」。" % [label, id],
				_lane_path(fwd_lane, int(d.get("lane", -1)))))


# ============================================================
# 2. 需求挂在出口车道
# ============================================================

## SUMO 的车从某条车道出发、沿它的驶出转向走。**出口车道没有驶出转向** →
## 导出时那一行需求会被跳过（`SumoRouWriter` 的"需求挂在出口车道"错误），
## 用户只会觉得"这个进口怎么一辆车都没有"。
static func _check_demand_direction(doc: Doc, fwd_lane: Dictionary, out: Array) -> void:
	var has_exit := {}
	for t in doc.turns:
		if String(t.get("type", "")) == "Crosswalk":
			continue
		has_exit[int(t.get("src_lane", -1))] = true

	for row in doc.demand:
		var lane := int((row as Dictionary).get("lane", -1))
		if lane < 0 or has_exit.has(lane):
			continue
		var l: Dictionary = doc.find_lane(lane)
		if l.is_empty():
			continue
		out.append(_item("sumo_demand_on_exit_lane", SEV_ERROR,
			"车道 #%d 没有驶出转向（它是出口车道）—— 挂在这里的需求发不出车，请改挂到进口车道。" % lane,
			_lane_path(fwd_lane, lane)))


# ============================================================
# 3. 车流时段超出仿真结束时刻
# ============================================================

static func _check_flow_segments(profile: SumoProfile, fwd_lane: Dictionary, out: Array) -> void:
	var sim_end := SumoRunModel.value(profile, "end")
	for pair in [["机动车流", profile.flows], ["行人流", profile.person_flows],
			["非机动车流", profile.bike_flows]]:
		var label := String(pair[0])
		for row in pair[1]:
			var d: Dictionary = row
			var segs: Array = d.get("segments", []) if d.get("segments", []) is Array else []
			var overflow := 0.0
			for seg in segs:
				overflow = maxf(overflow, float((seg as Dictionary).get("end", 0.0)))
			if overflow <= sim_end + 0.001:
				continue
			out.append(_item("sumo_flow_out_of_range", SEV_WARNING,
				"%s 的时段到 %.0f s，超过「结束时刻」%.0f s —— 超出的那部分车不会出现。" % [
					label, overflow, sim_end],
				_lane_path(fwd_lane, int(d.get("lane", -1)))))


# ============================================================
# 4. 给非信号路口配了专业配时
# ============================================================

static func _check_tls_without_signal(doc: Doc, profile: SumoProfile,
		fwd_node: Dictionary, out: Array) -> void:
	for row in profile.tls:
		var node := int((row as Dictionary).get("node", -1))
		if node < 0:
			continue
		var n: Dictionary = doc.find_node(node)
		if n.is_empty():
			continue                      # 已删的节点走 orphan，不重复报
		var si := doc.find_signal_index(node)
		var has_plan := si >= 0 and not (doc.signals[si].get("plans", []) as Array).is_empty()
		if String(n.get("type", "")) == "Signalized" and has_plan:
			continue
		out.append(_item("sumo_tls_without_signal", SEV_WARNING,
			"路口 #%d 配了专业配时，但它%s —— 导出的 net.xml 里不会有这个灯，配时不会生效。" % [
				node, "是普通路口" if String(n.get("type", "")) != "Signalized" else "没有信号方案"],
			_node_path(fwd_node, node)))


# ============================================================
# 5. 孤儿条目
# ============================================================

## 灯态配到了**当前关卡里不存在的转向**上。
## ★ 为什么必须在这里查：`SumoProfile.remapped()` 把专业档翻成文件 id 时，
##   找不到的 turn 会直接进 `orphans`（不静默丢数据），于是 C# 那边永远看不到它们 ——
##   导出的 net.xml 里沉默少一条灯。用户只会觉得"我配的灯怎么没生效"。
static func _check_tls_state_turns(doc: Doc, profile: SumoProfile, out: Array) -> void:
	var known := {}
	for t in doc.turns:
		known[int(t.id)] = true

	var reported := {}
	for row in profile.tls:
		var d: Dictionary = row
		var node := int(d.get("node", -1))
		var programs: Array = d.get("programs", []) if d.get("programs", []) is Array else []
		for program in programs:
			var phases: Array = (program as Dictionary).get("phases", []) \
				if (program as Dictionary).get("phases", []) is Array else []
			for phase in phases:
				var states = (phase as Dictionary).get("states", {})
				if not (states is Dictionary):
					continue
				for key in (states as Dictionary).keys():
					var tid := int(key)
					if known.has(tid) or reported.has(tid):
						continue
					reported[tid] = true
					out.append(_item("sumo_tls_state_unmapped", SEV_ERROR,
						"路口 #%d 的配时里给连接 #%d 配了灯态，但它已经不在当前关卡里 —— 这条灯态会丢失。" % [
							node, tid], _node_path_from_doc(doc, node)))


## 没有 `_fwd_*` 时也能给出节点路径（体检在导出流程里，映射总是有的；这里只是兜底）
static func _node_path_from_doc(doc: Doc, node_id: int) -> String:
	return "nodes[%d].sumo" % node_id if not doc.find_node(node_id).is_empty() else ""


## `profile.orphans` 是**载入 / 存盘时**产生的（`.sumo.json` 里的 id 在当前关卡找不到）。
## ★ 不静默丢数据（技术方案06 §8 风险表）：只标记 + 报出来，用户自己决定删还是补。
##
## ★ `tls.states` 单独报成 Error：那是"某个转向的灯态"，转向没了灯态就丢了 ——
##   与"某条车流指向已删车道"（Warning，导不出车而已）不是一个量级。
##   这也接住了 C# 侧 `sumo_tls_state_unmapped` 接不住的那一半：`remapped()` 在把专业档
##   翻成文件 id 时，找不到的 turn 会直接进 orphans（不会流到 C#），所以那边只能看见
##   "turn 在、但它没被导出"那种情况。
static func _check_orphans(profile: SumoProfile, out: Array) -> void:
	for o in profile.orphans:
		var d: Dictionary = o
		var table := String(d.get("table", "?"))
		var id := int(d.get("id", -1))
		var reason := String(d.get("reason", ""))
		if table.begins_with("tls.states"):
			out.append(_item("sumo_tls_state_unmapped", SEV_ERROR,
				"连接 #%d 配了灯态，但它已经不在当前关卡里（%s）—— 这条灯态会丢失，请重配或删掉。" % [
					id, reason], ""))
			continue
		out.append(_item("sumo_orphan_entry", SEV_WARNING,
			"专业档的「%s」第 %d 条指向已不存在的对象（id=%d，%s）—— 导出时会跳过。" % [
				table, int(d.get("index", -1)), id, reason], ""))


# ============================================================
# 内部
# ============================================================

static func _item(code: String, severity: String, message: String, path: String) -> Dictionary:
	return {"Code": code, "Severity": severity, "Message": message, "Path": path}


## 实时车道 id → `lanes[文件 id].sumo`（可被 `locate_validation` 反查）
static func _lane_path(fwd_lane: Dictionary, live_id: int) -> String:
	var f := int(fwd_lane.get(live_id, -1))
	return "lanes[%d].sumo" % f if f >= 0 else ""


static func _node_path(fwd_node: Dictionary, live_id: int) -> String:
	var f := int(fwd_node.get(live_id, -1))
	return "nodes[%d].sumo" % f if f >= 0 else ""
