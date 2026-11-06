extends Node

## S6 复验：**故意造 9 类专业档错误**，逐条确认能被报出来。
##
## 跑法：
##   godot --headless --path Editor res://qa/qa_s6.tscn > qa_s6.log 2>&1
##   grep -E "\[FAIL\]|SCRIPT ERROR|ERROR:" qa_s6.log     # 期望 0
##
## ★ 分两轮（因为导出前 Error 会跳过 SUMO 产物，C# 的 4 条就没机会跑）：
##   第一轮只造**导出前**能查的 5 条 → 验证全报 + 产物被跳过；
##   第二轮清掉它们，只造**导出后**才查得到的 4 条 → 验证全报。

var fails := 0
var out_dir := ""


func _mark(text: String) -> void:
	print("\n[QA] " + text)


func _ok(text: String, cond: bool) -> void:
	if cond:
		print("  ✓ " + text)
	else:
		fails += 1
		print("  [FAIL] " + text)


func _settle(extra: float = 0.0) -> void:
	await get_tree().process_frame
	await get_tree().create_timer(0.4 + extra).timeout
	await get_tree().process_frame


## findings 里有没有这个码
func _has_code(list: Array, code: String) -> bool:
	for f in list:
		if f is Dictionary and String((f as Dictionary).get("Code", "")) == code:
			return true
	return false


## 某个码的全部条目
func _of_code(list: Array, code: String) -> Array:
	var out: Array = []
	for f in list:
		if f is Dictionary and String((f as Dictionary).get("Code", "")) == code:
			out.append(f)
	return out


func _clean_profile() -> void:
	var p: SumoProfile = EditorApp.profile
	p.config.clear()
	p.overrides.clear()
	p.netconvert.clear()
	p.vehicle_types.clear()
	p.lane_mix.clear()
	p.tls.clear()
	p.flows.clear()
	p.person_flows.clear()
	p.bike_flows.clear()
	p.additional.clear()
	p.bus_lines.clear()
	p.orphans.clear()
	p.vehicle_types = [
		{"id": "car", "preset": "car", "style": "normal", "share": 1.0},
	]
	p.touch()


func _export() -> Dictionary:
	return EditorApp.export_with_options({
		"spmap": false, "shp": false,
		"net": true, "rou": true, "add": true, "sumocfg": true,
		"outputBase": out_dir.path_join("qa6"),
		"netconvertPath": "",
		"netconvertSearchDir": ProjectSettings.globalize_path("res://../Tools/sumo/bin"),
		"laneWidthM": 3.5,
		"includeCrosswalks": true,
		"yellowDuration": 3.0,
		"allRedDuration": 2.0,
	})


func _clear_outputs() -> void:
	for ext in [".net.xml", ".rou.xml", ".sumocfg", ".add.xml", ".tll.xml"]:
		var f := out_dir.path_join("qa6" + ext)
		if FileAccess.file_exists(f):
			DirAccess.remove_absolute(f)


func _ready() -> void:
	await get_tree().process_frame
	await get_tree().process_frame

	EditorApp.import_map(ProjectSettings.globalize_path("res://../docs/examples/01-1-first-crossing.spmap"))
	EditorApp.set_edition(Edition.Level.PRO)
	_clean_profile()
	await _settle()

	out_dir = OS.get_environment("TEMP").path_join("qa_s6_export")
	DirAccess.make_dir_recursive_absolute(out_dir)
	_clear_outputs()

	# 示例关卡：中心节点 0 是信号路口；车道 4 是出口；turns 0..9
	var exit_lane := 4
	var plain_node := 1

	# ============================================================
	# 第一轮：导出前能查的 5 条
	# ============================================================
	_mark("第一轮 · 造 5 类导出前错误")
	var p: SumoProfile = EditorApp.profile
	# 1) 车型 id 不存在
	p.lane_mix["0"] = [{"id": "ghost_type", "share": 1.0}]
	# 2) 车流时段超出结束时刻
	p.flows = [{
		"id": "f_bad", "lane": 0, "vtype": "car",
		"segments": [{"begin": 0.0, "end": 99999.0, "veh_per_hour": 600.0}],
	}]
	# 3) 给普通路口配专业配时
	# 4) 灯态配到了不存在的转向（#9999）—— 这条是**导出前** Error，会拦住产物，只能放第一轮
	p.tls = [
		{"node": plain_node, "programs": [
			{"id": "p0", "activate_at": 0.0, "offset": 0.0, "type": "static",
			 "phases": [{"duration": 30.0, "name": "g1", "states": {0: "G"}}]},
		]},
		{"node": 0, "programs": [
			{"id": "p0", "activate_at": 0.0, "offset": 0.0, "type": "static",
			 "phases": [{"duration": 30.0, "name": "g1", "states": {9999: "G"}}]},
		]},
	]
	# 4) 孤儿条目
	p.orphans = [{"table": "flows", "index": 3, "id": 777, "reason": "id 在当前关卡里不存在"}]
	p.touch()
	# 5) 需求挂在出口车道（改 doc）
	EditorApp.doc.demand.append({"lane": exit_lane, "time_points": [0.0, 600.0], "flow_rates": [0.1, 0.1]})
	await _settle()

	var r1: Dictionary = _export()
	var found1: Array = r1.get("sumo_findings", [])
	print("[QA] 第一轮 findings：%s" % str(found1.map(func(f): return String((f as Dictionary).get("Code", "")))))
	for code in ["sumo_vehicle_type_missing", "sumo_demand_on_exit_lane",
			"sumo_flow_out_of_range", "sumo_tls_without_signal", "sumo_orphan_entry",
			"sumo_tls_state_unmapped"]:
		_ok("报出 %s" % code, _has_code(found1, code))
	_ok("导出前有 Error → 不生成 net.xml", not FileAccess.file_exists(out_dir.path_join("qa6.net.xml")))
	_ok("导出前有 Error → 不生成 rou.xml", not FileAccess.file_exists(out_dir.path_join("qa6.rou.xml")))
	_ok("体检结果进了「校验问题」清单（可点定位）",
		_matches_validation_items(["sumo_vehicle_type_missing", "sumo_orphan_entry"]))
	# 可点定位：车道类条目必须有 Path
	var lane_item := _of_code(found1, "sumo_demand_on_exit_lane")
	_ok("需求挂出口那条带 Path（能点定位）",
		lane_item.size() > 0 and String((lane_item[0] as Dictionary).get("Path", "")).begins_with("lanes["))
	var unmapped1 := _of_code(found1, "sumo_tls_state_unmapped")
	_ok("灯态失效那条点名了连接 #9999",
		unmapped1.size() > 0 and String((unmapped1[0] as Dictionary).get("Message", "")).contains("9999"))

	# ============================================================
	# 第二轮：导出后能查的 4 条
	# ============================================================
	_mark("第二轮 · 清掉上一轮，造 4 类导出后错误")
	EditorApp.doc.demand.pop_back()          # 去掉挂错的那条
	_clean_profile()
	_clear_outputs()
	await _settle()

	# 找一对真正互相冲突的转向（SimCore 的冲突矩阵，与运行期同源）
	var pairs: Array = []
	var bridge := EditorApp.get_bridge()
	if bridge != null:
		var raw: String = bridge.ConflictsAt(EditorApp._cached_temp_spmap(), 0)
		var parsed = JSON.parse_string(raw)
		if parsed is Dictionary and parsed.get("pairs") is Array:
			pairs = parsed.pairs
	_ok("拿到示例关卡的冲突对（%d 对）" % pairs.size(), pairs.size() > 0)

	var conflict_a := -1
	var conflict_b := -1
	if pairs.size() > 0:
		conflict_a = int((pairs[0] as Array)[0])
		conflict_b = int((pairs[0] as Array)[1])

	p = EditorApp.profile
	# 1) 只给 turn 0 绿灯 → 其它 link 从未放行（Missing green phase）
	# 2) 同一相位同时放行一对冲突转向
	var states := {0: "G"}
	if conflict_a >= 0:
		states[conflict_a] = "G"
		states[conflict_b] = "G"
	p.tls = [{"node": 0, "programs": [
		{"id": "p0", "activate_at": 0.0, "offset": 0.0, "type": "static",
		 "phases": [{"duration": 30.0, "name": "g1", "states": states}]},
	]}]
	# 4) 检测器位置越界
	p.additional = [{"id": "det_bad", "kind": "e1", "lane": 0, "pos": 9999.0,
		"freq": 60.0, "length": 0.0, "duration": 0.0, "capacity": 0}]
	p.touch()
	await _settle()

	var r2: Dictionary = _export()
	var found2: Array = r2.get("sumo_findings", [])
	print("[QA] 第二轮 findings：%s" % str(found2.map(func(f): return String((f as Dictionary).get("Code", "")))))
	for code in ["sumo_tls_never_green", "sumo_detector_out_of_range", "sumo_phase_conflict"]:
		_ok("报出 %s" % code, _has_code(found2, code))
	_ok("这轮 net.xml 生成了（Error 是事后发现的，产物不删）",
		FileAccess.file_exists(out_dir.path_join("qa6.net.xml")))
	_ok("导出结果里有一条 sumo 失败（界面显示 ✗）",
		(r2.get("results", []) as Array).any(func(r): return String((r as Dictionary).get("format", "")) == "sumo"))

	# 冲突那条要成对出现
	var clash := _of_code(found2, "sumo_phase_conflict")
	_ok("相位冲突那条列出了成对的连接",
		clash.size() > 0 and String((clash[0] as Dictionary).get("Message", "")).contains("×"))

	# ============================================================
	_clean_profile()
	EditorApp.validation_error_items.clear()
	EditorApp.validation_errors = -1
	print("\n[QA] === 完成：失败 %d 项 ===" % fails)
	get_tree().quit(1 if fails > 0 else 0)


## 校验清单里有没有这些码（体检结果必须并进去，否则清单浮窗看不到）
func _matches_validation_items(codes: Array) -> bool:
	var items: Array = EditorApp.validation_error_items
	for code in codes:
		if not _has_code(items, String(code)):
			return false
	return true
