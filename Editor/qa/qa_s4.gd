extends Node

## S4 复验：**走真实控件操作**验证「网络覆盖层」四块面板（车道 / 连接 / 路口 / 关卡）
## 与导出链路。与 `qa_s1s3.gd` 的分工：
##   - qa_s1s3 只走一遍看日志有没有 ERROR；
##   - 本脚本**带断言**（失败打 [QA][FAIL]），因为 S4 的产物不是"长什么样"而是"文件里写了什么"。
##
## 跑法：
##   godot --headless --path Editor res://qa/qa_s4.tscn > qa_s4.log 2>&1
##   然后 grep -E "\[FAIL\]|SCRIPT ERROR|ERROR:" qa_s4.log   # 期望 0
##
## ★ 每一步都改完就断言 `EditorApp.profile` 的**数据**，而不是断言控件文字 ——
##   文字会被主题/字体影响，数据才是导出的真相。

var panel: Node = null
var signal_node: int = -1
var plain_node: int = -1
var motor_lane: int = -1
var motor_turn: int = -1
var fails: int = 0


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
	await get_tree().create_timer(0.45 + extra).timeout
	await get_tree().process_frame


## 某小节在面板里有没有出现（标题是 Button 的 text）
func _has_section(title: String) -> bool:
	return _find_button(panel, title) != null


func _ready() -> void:
	await get_tree().process_frame
	await get_tree().process_frame

	# ============ 准备 ============
	_mark("载入示例关卡 + 切专业模式")
	var path := ProjectSettings.globalize_path("res://../docs/examples/01-1-first-crossing.spmap")
	EditorApp.import_map(path)
	EditorApp.set_edition(Edition.Level.PRO)
	# 清掉可能残留的专业档，避免上一轮跑出的数据干扰断言
	EditorApp.profile.overrides.clear()
	EditorApp.profile.netconvert.clear()
	EditorApp.profile.additional.clear()
	EditorApp.profile.bus_lines.clear()
	EditorApp.profile.vehicle_types.clear()
	EditorApp.profile.lane_mix.clear()
	EditorApp.profile.touch()
	await _settle()

	for n in EditorApp.doc.nodes:
		var ty := String(n.get("type", ""))
		if ty == "Signalized" and signal_node < 0:
			signal_node = int(n.get("id", -1))
		elif ty == "Unsignalized" and plain_node < 0:
			plain_node = int(n.get("id", -1))
	for l in EditorApp.doc.lanes:
		if Doc.MOTOR_LANE_TYPES.has(String(l.get("type", ""))) and motor_lane < 0:
			motor_lane = int(l.get("id", -1))
	for t in EditorApp.doc.turns:
		var a: Dictionary = EditorApp.doc.find_lane(int(t.get("src_lane", -1)))
		var b: Dictionary = EditorApp.doc.find_lane(int(t.get("dst_lane", -1)))
		if motor_turn < 0 and not a.is_empty() and not b.is_empty() \
				and Doc.MOTOR_LANE_TYPES.has(String(a.get("type", ""))) \
				and Doc.MOTOR_LANE_TYPES.has(String(b.get("type", ""))):
			motor_turn = int(t.get("id", -1))
	print("[QA] 信号路口=%d 普通路口=%d 机动车道=%d 机动车转向=%d" % [
		signal_node, plain_node, motor_lane, motor_turn])
	_ok("找齐测试对象", signal_node >= 0 and plain_node >= 0 and motor_lane >= 0 and motor_turn >= 0)

	panel = VBoxContainer.new()
	panel.set_script(load("res://scripts/property_panel.gd"))
	panel.size = Vector2(396, 1600)
	add_child(panel)
	await _settle()

	# ============ 车道属性 ============
	_mark("车道 · 车辆模式选中机动车道 → 「SUMO 车道属性」")
	EditorApp.set_mode(EditorMode.Kind.TRAFFIC)
	EditorApp.select_traffic_lane(motor_lane)
	await _settle()
	_ok("面板出现「SUMO 车道属性」小节", _has_section("SUMO 车道属性"))

	var lane_pick: OptionButton = _find_option_under("允许车型")
	if lane_pick != null:
		lane_pick.item_selected.emit(1)          # 0=跟随 1=仅公交
		await _settle()
	var lane_row: Dictionary = SumoNetModel.lane_row(EditorApp.profile, motor_lane)
	_ok("下拉选「仅公交」→ access=bus 写进 overrides", String(lane_row.get("access", "")) == "bus")

	var sp: SpinBox = _find_spin_under("限速覆盖")
	if sp != null:
		sp.value = 8.5
		await _settle()
	lane_row = SumoNetModel.lane_row(EditorApp.profile, motor_lane)
	_ok("限速覆盖 8.5 写进 overrides", is_equal_approx(float(lane_row.get("speed", 0.0)), 8.5))

	var wd: SpinBox = _find_spin_under("宽度覆盖")
	if wd != null:
		wd.value = 3.25
		await _settle()
	lane_row = SumoNetModel.lane_row(EditorApp.profile, motor_lane)
	_ok("宽度覆盖 3.25 写进 overrides", is_equal_approx(float(lane_row.get("width", 0.0)), 3.25))

	# ============ 连接属性 ============
	_mark("连接 · 连接模式选中机动车转向 → 「SUMO 连接属性」")
	EditorApp.set_mode(EditorMode.Kind.CONNECT)
	EditorApp.select_turn(motor_turn)
	await _settle()
	_ok("面板出现「SUMO 连接属性」小节", _has_section("SUMO 连接属性"))

	var turn_pick: OptionButton = _find_option_under("允许车型")
	if turn_pick != null:
		turn_pick.item_selected.emit(2)          # 0=跟随 1=仅公交 2=禁止机动车
		await _settle()
	var turn_row: Dictionary = SumoNetModel.connection_row(EditorApp.profile, motor_turn)
	_ok("下拉选「禁止机动车」→ access=none 写进 overrides",
		String(turn_row.get("access", "")) == "none")

	# ============ 路口控制 ============
	_mark("路口 · 选中普通路口 → 「SUMO 路口控制」可改")
	EditorApp.select_turn(-1)                     # ★ select_junction 不清 turn，真实 UI 由画布清
	EditorApp.select_junction(plain_node)
	await _settle()
	_ok("面板出现「SUMO 路口控制」小节", _has_section("SUMO 路口控制"))
	var junc_pick: OptionButton = _find_option_under("控制方式")
	if junc_pick != null:
		junc_pick.item_selected.emit(2)          # 0=自动 1=优先让行 2=全向停车
		await _settle()
	_ok("下拉选「全向停车」→ type=right_before_left",
		SumoNetModel.junction_type(EditorApp.profile, plain_node) == "right_before_left")

	_mark("路口 · 选中信号路口 → 只读，不允许改控制方式")
	EditorApp.select_turn(-1)
	EditorApp.select_junction(signal_node)
	await _settle(0.6)                            # 信号路口要等探针
	var sig_pick: OptionButton = _find_option_under("控制方式")
	_ok("信号路口的「控制方式」不是下拉", sig_pick == null)
	_ok("信号路口没有被写进 overrides.junctions",
		SumoNetModel.junction_row(EditorApp.profile, signal_node).is_empty())

	# ============ 关卡级 netconvert 开关 ============
	_mark("关卡 · 未选中任何对象 → 「SUMO 路网生成」4 个开关")
	EditorApp.set_mode(EditorMode.Kind.ROAD)      # set_mode 本身清空全部选中项
	await _settle()
	_ok("面板出现「SUMO 路网生成」小节", _has_section("SUMO 路网生成"))
	var boxes := _checks_under_section("SUMO 路网生成")
	_ok("小节里有 4 个勾选框（实际 %d）" % boxes.size(), boxes.size() == 4)

	if boxes.size() == 4:
		var internal_box: CheckBox = boxes[1]      # 顺序 = NETCONVERT_KEYS
		internal_box.button_pressed = true        # ★ 会自己发 toggled，别再手动 emit
		await _settle()
	_ok("勾「不生成内部车道」→ netconvert.no_internal_links=true",
		SumoNetModel.netconvert_option(EditorApp.profile, "no_internal_links"))

	_ok("默认值没被写进文件（no_turnarounds 与默认一致 → 文件里不该有这个键）",
		not EditorApp.profile.netconvert.has("no_turnarounds"))

	# ============ 基础模式：这些小节全都不出现 ============
	_mark("基础模式 → 四块 SUMO 小节都不出现")
	EditorApp.set_edition(Edition.Level.BASIC)
	await _settle()
	EditorApp.set_mode(EditorMode.Kind.TRAFFIC)
	EditorApp.select_traffic_lane(motor_lane)
	await _settle()
	_ok("基础模式车道面板没有「SUMO 车道属性」", not _has_section("SUMO 车道属性"))
	EditorApp.set_mode(EditorMode.Kind.CONNECT)
	EditorApp.select_turn(motor_turn)
	await _settle()
	_ok("基础模式连接面板没有「SUMO 连接属性」", not _has_section("SUMO 连接属性"))
	EditorApp.select_junction(plain_node)
	await _settle()
	_ok("基础模式路口面板没有「SUMO 路口控制」", not _has_section("SUMO 路口控制"))
	EditorApp.set_mode(EditorMode.Kind.ROAD)
	await _settle()
	_ok("基础模式关卡面板没有「SUMO 路网生成」", not _has_section("SUMO 路网生成"))
	EditorApp.set_edition(Edition.Level.PRO)
	await _settle()

	# ============ 撤销 ============
	_mark("撤销 · 改路口控制方式后 Ctrl+Z 一步回退")
	var before_type := SumoNetModel.junction_type(EditorApp.profile, plain_node)
	EditorApp.begin_edit("qa_junction_type")
	SumoNetModel.set_junction_type(EditorApp.profile, plain_node, "unregulated")
	EditorApp.profile.touch()
	EditorApp.end_edit()
	await _settle()
	_ok("改成 unregulated 生效", SumoNetModel.junction_type(EditorApp.profile, plain_node) == "unregulated")
	EditorApp.undo()
	await _settle()
	_ok("Ctrl+Z 回退到原值（%s）" % before_type,
		SumoNetModel.junction_type(EditorApp.profile, plain_node) == before_type)

	# ============ 导出链路（真的写出 net.xml 再读回来）============
	_mark("导出 · 带 overrides 导出 net.xml，读回文件验证属性")
	var out_dir := OS.get_environment("TEMP").path_join("qa_s4_export")
	DirAccess.make_dir_recursive_absolute(out_dir)
	var res: Dictionary = EditorApp.export_with_options({
		"spmap": false,
		"shp": false,
		"net": true,
		"rou": false,
		"add": false,
		"outputBase": out_dir.path_join("qa4"),
		"netconvertPath": "",
		"netconvertSearchDir": ProjectSettings.globalize_path("res://../Tools/sumo/bin"),
		"laneWidthM": 3.5,
		"includeCrosswalks": true,
		"yellowDuration": 3.0,
		"allRedDuration": 2.0,
	})
	var net_path: String = out_dir.path_join("qa4.net.xml")
	_ok("net.xml 写出来了", FileAccess.file_exists(net_path))
	var text := ""
	if FileAccess.file_exists(net_path):
		text = FileAccess.get_file_as_string(net_path)
	_ok("文件里有「仅公交」的 allow=\"bus\"", text.contains("allow=\"bus\""))
	_ok("文件里有被禁连接的 allow=\"pedestrian\"", text.contains("allow=\"pedestrian\""))
	_ok("文件里没有 disallow 与 allow 并存的痕迹",
		not text.contains("disallow=\"pedestrian bicycle rail\""))
	print("[QA] 导出结果：%s" % str(res.get("results", [])))

	# ============ 清场 ============
	_mark("清掉本轮写下的专业档数据（别污染老板正在用的关卡）")
	EditorApp.profile.overrides.clear()
	EditorApp.profile.netconvert.clear()
	EditorApp.profile.touch()

	print("\n[QA] === 完成：失败 %d 项 ===" % fails)
	get_tree().quit(1 if fails > 0 else 0)


# ============================================================
# 小工具
# ============================================================

func _find_all(root: Node, cls: String) -> Array:
	if root == null:
		return []
	return Array(root.find_children("*", cls, true, false))


func _find_button(root: Node, text: String) -> Button:
	for b in _find_all(root, "Button"):
		if String((b as Button).text).contains(text):
			return b as Button
	return null


## 面板是"字段名 + 值控件"两列 → 顺着某行找控件。
## 简化实现：找出所有该类型控件，按"同一行里有这个字段名"筛。
func _find_control_under(label_text: String, cls: String) -> Control:
	for row in _find_all(panel, "HBoxContainer"):
		var has_label := false
		for c in (row as Node).get_children():
			if c is Label and String((c as Label).text).contains(label_text):
				has_label = true
				break
		if not has_label:
			continue
		for c in (row as Node).find_children("*", cls, true, false):
			return c as Control
	return null


func _find_option_under(label_text: String) -> OptionButton:
	return _find_control_under(label_text, "OptionButton") as OptionButton


func _find_spin_under(label_text: String) -> SpinBox:
	return _find_control_under(label_text, "SpinBox") as SpinBox


## 某小节正文里的全部 CheckBox（按添加顺序）
func _checks_under_section(title: String) -> Array:
	var head := _find_button(panel, title)
	if head == null:
		return []
	# 小节头之后的下一个兄弟是正文 VBox（见 property_panel._section）
	var parent := head.get_parent()
	var idx := head.get_index()
	if parent == null or idx < 0 or idx + 1 >= parent.get_child_count():
		return []
	var body := parent.get_child(idx + 1)
	return _find_all(body, "CheckBox")
