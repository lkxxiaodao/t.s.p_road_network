extends Node

## S1~S3 复验：走**真实控件操作**路径（点按钮 / 改输入框 / 触发下拉），
## 目的是逼出"headless 自测调 API 时漏掉"的引擎报错。
##
## 跑法：
##   godot --headless --path Editor res://_qa_s1s3.tscn 2>&1 | tee qa.log
## 然后把 qa.log 里所有 ERROR / SCRIPT ERROR 逐条查。
##
## ★ 本脚本只负责"走一遍并打标记"，不做断言 —— 判断靠日志。

var panel: Node = null
var signal_node: int = -1
var motor_lane: int = -1
var step_i: int = 0


func _mark(text: String) -> void:
	step_i += 1
	print("\n[QA %02d] %s" % [step_i, text])


## 等面板重建（content_changed 0.25s 轮询 + 0.08s 防抖 + 一帧）
func _settle(extra: float = 0.0) -> void:
	await get_tree().process_frame
	await get_tree().create_timer(0.45 + extra).timeout
	await get_tree().process_frame


func _ready() -> void:
	await get_tree().process_frame
	await get_tree().process_frame

	# ============ 准备 ============
	_mark("载入示例关卡 + 切专业模式")
	var path := ProjectSettings.globalize_path("res://../docs/examples/01-1-first-crossing.spmap")
	EditorApp.import_map(path)
	EditorApp.set_edition(Edition.Level.PRO)
	EditorApp.profile.additional.clear()
	EditorApp.profile.bus_lines.clear()
	EditorApp.profile.vehicle_types.clear()
	EditorApp.profile.lane_mix.clear()
	EditorApp.profile.touch()
	await _settle()

	for n in EditorApp.doc.nodes:
		if String(n.get("type", "")) == "Signalized":
			signal_node = int(n.get("id", -1))
			break
	for l in EditorApp.doc.lanes:
		if Doc.MOTOR_LANE_TYPES.has(String(l.get("type", ""))) and float(l.get("length", 0)) > 100.0:
			motor_lane = int(l.get("id", -1))
			break
	print("[QA] 信号路口 = %d，机动车道 = %d" % [signal_node, motor_lane])

	# 属性面板（测试场景没有 Main.tscn 的坞）
	panel = VBoxContainer.new()
	panel.set_script(load("res://scripts/property_panel.gd"))
	panel.size = Vector2(396, 1400)
	add_child(panel)
	await _settle()

	# ============ S1：信号灯 ============
	_mark("S1 · 连接模式选中信号路口（触发探针 + 配时矩阵）")
	EditorApp.set_mode(EditorMode.Kind.CONNECT)
	EditorApp.select_junction(signal_node)
	# 探针要真跑 netconvert，等够
	await get_tree().process_frame
	var t0 := Time.get_ticks_msec()
	while EditorApp.sumo_probe.busy and Time.get_ticks_msec() - t0 < 30000:
		await get_tree().create_timer(0.1).timeout
	await _settle()
	print("[QA] 探针：busy=%s err=%s tls=%d" % [EditorApp.sumo_probe.busy,
		EditorApp.sumo_probe.error, (EditorApp.sumo_probe.result.get("tls", []) as Array).size()])

	_mark("S1 · 点「从基础方案生成」")
	var gen: Button = _find_button(panel, "从基础方案生成")
	if gen != null:
		gen.pressed.emit()
	else:
		print("[QA] 没找到生成按钮（面板可能还没画出来）")
	await _settle()

	_mark("S1 · 点灯态格子（G→g→y→r→o 循环）")
	print("[QA] 格子数 = %d" % _count_state_cells())
	# ★ 每点一次都重新查控件：点格子会触发面板重建，旧引用当场失效
	#   （真实用户是鼠标点，不持有引用，所以这不算产品缺陷）
	for i in 5:
		var cell := _first_state_cell()
		if cell == null:
			print("[QA] 第 %d 次点格子时找不到格子了" % i)
			break
		var before := String(cell.text)
		cell.pressed.emit()
		await _settle(0.1)
		var after_cell := _first_state_cell()
		print("[QA]   格子 %d：%s → %s" % [i, before,
			String(after_cell.text) if after_cell != null else "?"])

	_mark("S1 · 改阶段时长（SpinBox 直接赋值 + 发信号）")
	var spins: Array = _find_all(panel, "SpinBox")
	print("[QA] SpinBox 数 = %d" % spins.size())
	if spins.size() > 0:
		(spins[0] as SpinBox).value = 45.0
		await get_tree().process_frame
	await _settle()

	_mark("S1 · 加阶段 / 删阶段")
	var add_phase: Button = _find_button(panel, "＋ 阶段")
	if add_phase != null:
		add_phase.pressed.emit()
		await _settle()
	var del_phase: Button = _find_button(panel, "删除")
	if del_phase != null:
		del_phase.pressed.emit()
		await _settle()

	_mark("S1 · 切方案 / 复制 / 删除")
	var dup: Button = _find_button(panel, "⧉")
	if dup != null:
		dup.pressed.emit()
		await _settle()
	var del_prog: Button = _find_button(panel, "✕")
	if del_prog != null:
		del_prog.pressed.emit()
		await _settle()

	_mark("S1 · 点行头（画布高亮）")
	for i in 2:
		var head: Button = null
		for b in _find_all(panel, "Button"):
			var t := String((b as Button).text)
			if t.length() == 2 and t.find("机") >= 0:
				head = b as Button
				break
		if head == null:
			print("[QA] 第 %d 次点行头时找不到了" % i)
			break
		head.pressed.emit()
		await _settle()
		print("[QA]   行头点击 %d → 高亮 = %d" % [i, EditorApp.sumo_highlight_turn])

	# ============ S2：车型 / 车流 / 转率 ============
	_mark("S2 · 车辆模式选中机动车道")
	EditorApp.set_mode(EditorMode.Kind.TRAFFIC)
	EditorApp.select_traffic_lane(motor_lane)
	await _settle()

	_mark("S2 · 读车道面板的车型构成（来源行）")
	print("[QA] 车型数 = %d，lane_mix 键 = %s（此时应为 0/空：还没改过比例）" % [
		EditorApp.profile.vehicle_types.size(), str(EditorApp.profile.lane_mix.keys())])

	_mark("S2 · 点「＋ 添加车型」")
	var add_type: Button = _find_button(panel, "＋ 添加车型")
	if add_type != null:
		add_type.pressed.emit()
		await _settle()

	_mark("S2 · 改份额 SpinBox + 车型下拉")
	var share_spins: Array = _find_all(panel, "SpinBox").filter(func(s: SpinBox) -> bool:
		return s.max_value >= 99.0 and s.max_value <= 101.0)
	print("[QA] 份额框数 = %d" % share_spins.size())
	if share_spins.size() > 0:
		(share_spins[0] as SpinBox).value = 60.0
		await _settle(0.1)
	var picks: Array = _find_all(panel, "OptionButton")
	print("[QA] 下拉数 = %d" % picks.size())
	if picks.size() > 0:
		var p: OptionButton = picks[0]
		if p.item_count > 1:
			p.item_selected.emit(1)
		await _settle(0.1)

	_mark("S2 · 打开车型库弹窗（改参数）")
	EditorApp.open_type_dialog()
	await get_tree().create_timer(0.5).timeout
	var dlg: Window = EditorApp._type_dialog
	print("[QA] 弹窗 = %s" % str(dlg))
	if dlg != null and is_instance_valid(dlg):
		dlg.call("rebuild")
		await get_tree().create_timer(0.3).timeout
		var edits: Array = _find_all(dlg, "LineEdit")
		print("[QA] 弹窗里 LineEdit 数 = %d" % edits.size())
		if edits.size() > 0:
			(edits[0] as LineEdit).text = "qa_car"
			(edits[0] as LineEdit).text_changed.emit("qa_car")
			await get_tree().process_frame
		var dspins: Array = _find_all(dlg, "SpinBox")
		print("[QA] 弹窗里 SpinBox 数 = %d" % dspins.size())
		if dspins.size() > 0:
			(dspins[0] as SpinBox).value = 5.5
			await get_tree().process_frame
		dlg.hide()
		await _settle()

	_mark("S2 · 改转率")
	var ratio_spins: Array = _find_all(panel, "SpinBox").filter(func(s: SpinBox) -> bool:
		return s.max_value == 1.0)
	print("[QA] 转率框数 = %d" % ratio_spins.size())
	if ratio_spins.size() > 0:
		(ratio_spins[0] as SpinBox).value = 0.5
		await get_tree().process_frame
	await _settle()

	_mark("S2 · 需求曲线加一段")
	var add_demand: Button = _find_button(panel, "＋ 添加")
	if add_demand != null:
		add_demand.pressed.emit()
		await _settle()

	# ============ S3：设施 / 公交线路 ============
	_mark("S3 · 加四种设施（点面板按钮）")
	for label in ["＋ 断面流量", "＋ 排队检测", "＋ 公交站", "＋ 停车场"]:
		var b: Button = _find_button(panel, label)
		if b != null:
			b.pressed.emit()
			await _settle()
	print("[QA] 设施数 = %d" % EditorApp.profile.additional.size())

	_mark("S3 · 改设施字段（站名 / 线路名 / 长度 / 频率）")
	print("[QA] 面板 LineEdit 数 = %d" % _find_all(panel, "LineEdit").size())
	# 每次重新取第一个（改字段可能触发重建）
	for i in mini(6, _find_all(panel, "LineEdit").size()):
		var edits_now: Array = _find_all(panel, "LineEdit")
		if i >= edits_now.size():
			break
		var e: LineEdit = edits_now[i]
		var txt := "qa_%d" % i
		e.text = txt
		e.text_changed.emit(txt)
		await _settle(0.1)

	_mark("S3 · 点「＋ 新建公交线路」")
	var add_bus: Button = _find_button(panel, "＋ 新建公交线路")
	if add_bus != null:
		add_bus.pressed.emit()
		await _settle()
	print("[QA] 线路数 = %d，站数 = %d" % [
		SumoBusModel.all_of(EditorApp.profile).size(),
		SumoBusModel.stop_choices(EditorApp.profile).size()])

	_mark("S3 · 勾停靠站（CheckBox）")
	print("[QA] 勾选框数 = %d" % _find_all(panel, "CheckBox").size())
	for i in _find_all(panel, "CheckBox").size():
		var checks_now: Array = _find_all(panel, "CheckBox")
		if i >= checks_now.size():
			break
		var c: CheckBox = checks_now[i]
		# ★ 只设 button_pressed —— 它的 setter 自己会发 toggled(true)。
		#   再手动 emit 一次就等于 toggle 两次（勾上又取消），S3 复验第一版就是栽在这里。
		c.button_pressed = true
		await _settle(0.1)

	_mark("S3 · 改发车间隔")
	var headway_spins: Array = _find_all(panel, "SpinBox").filter(func(s: SpinBox) -> bool:
		return s.max_value >= 3600.0)
	print("[QA] 间隔框数 = %d" % headway_spins.size())
	if headway_spins.size() > 0:
		(headway_spins[0] as SpinBox).value = 180.0
		await get_tree().process_frame
	await _settle()

	_mark("S3 · 撤销 / 重做（连做 5 次）")
	for i in 5:
		EditorApp.undo()
		await get_tree().process_frame
	for i in 5:
		EditorApp.redo()
		await get_tree().process_frame
	await _settle()

	# ============ 车辆总览 ============
	_mark("总览 · 未选车道 → 车辆总览")
	EditorApp.select_traffic_lane(-1)
	await _settle()
	var ov_btns: Array = _find_all(panel, "Button")
	print("[QA] 总览按钮 = %s" % str(ov_btns.map(func(b: Button) -> String: return String(b.text))))

	_mark("总览 · 点「管理车型库…」")
	var mng: Button = _find_button(panel, "管理车型库…")
	if mng != null:
		mng.pressed.emit()
		await get_tree().create_timer(0.5).timeout
		if EditorApp._type_dialog != null and is_instance_valid(EditorApp._type_dialog):
			EditorApp._type_dialog.call("rebuild")
			await get_tree().create_timer(0.3).timeout
			EditorApp._type_dialog.hide()
		await _settle()

	_mark("总览 · 总览里新建线路（自动挑起点）")
	var add_bus2: Button = _find_button(panel, "＋ 新建公交线路")
	if add_bus2 != null:
		add_bus2.pressed.emit()
		await _settle()
	print("[QA] 线路数 = %d" % SumoBusModel.all_of(EditorApp.profile).size())

	# ============ 切模式 / 切档 / 往返 ============
	_mark("切换：基础档 → 专业档 → 连接模式 → 车辆模式")
	EditorApp.set_edition(Edition.Level.BASIC)
	await _settle()
	EditorApp.select_traffic_lane(motor_lane)
	await _settle()
	EditorApp.set_edition(Edition.Level.PRO)
	await _settle()
	EditorApp.set_mode(EditorMode.Kind.CONNECT)
	EditorApp.select_junction(signal_node)
	await _settle()
	EditorApp.set_mode(EditorMode.Kind.TRAFFIC)
	await _settle()

	# ============ 导出 + 存盘 ============
	_mark("导出对话框：打开 → 点每颗格式胶囊 → 关掉")
	EditorApp.open_export_dialog()
	await get_tree().create_timer(0.6).timeout
	var edlg: Window = EditorApp._export_dialog
	if edlg != null and is_instance_valid(edlg):
		print("[QA] 导出对话框 = %s" % str(edlg))
		var pills: Array = _find_all(edlg, "Button").filter(func(b: Button) -> bool:
			return String((b as Button).text).begins_with("SUMO") 				or String((b as Button).text).begins_with("原生") 				or String((b as Button).text).begins_with("Shapefile"))
		print("[QA] 格式胶囊 = %s" % str(pills.map(func(b: Button) -> String: return String(b.text))))
		for b in pills:
			var pill: Button = b
			pill.button_pressed = not pill.button_pressed
			await get_tree().create_timer(0.08).timeout
			pill.button_pressed = not pill.button_pressed
			await get_tree().create_timer(0.08).timeout
		edlg.hide()
		await get_tree().create_timer(0.2).timeout
	else:
		print("[QA] 没打开导出对话框")

	_mark("导出 net / rou / add")
	var out_dir := ProjectSettings.globalize_path("user://qa_s3")
	DirAccess.make_dir_recursive_absolute(out_dir)
	var res: Dictionary = EditorApp.export_with_options({
		"outputBase": out_dir.path_join("01-1"),
		"spmap": false, "shp": false, "net": true, "rou": true, "add": true,
		"laneWidthM": 3.5, "netconvertPath": "",
		"netconvertSearchDir": ProjectSettings.globalize_path("res://../Tools/sumo/bin"),
		"yellowDuration": 3.0, "allRedDuration": 2.0,
	})
	print("[QA] 导出 ok=%s err=%s" % [str(res.get("ok", false)), String(res.get("error", ""))])
	for r in res.get("results", []):
		print("[QA]   %s ok=%s %s" % [String(r.get("format", "")), str(r.get("ok", false)),
			String(r.get("error", ""))])
		for w in r.get("warnings", []):
			print("[QA]     · %s" % String(w))

	_mark("存盘 → 重开")
	var save_path := out_dir.path_join("qa-roundtrip.spmap")
	var ok_save: bool = EditorApp._save_to(save_path)
	print("[QA] 保存 = %s" % str(ok_save))
	EditorApp.import_map(save_path)
	await _settle()
	print("[QA] 重开后：车型 %d / 设施 %d / 线路 %d" % [
		EditorApp.profile.vehicle_types.size(),
		EditorApp.profile.additional.size(),
		EditorApp.profile.bus_lines.size()])

	_mark("清空重载（新建关卡）")
	EditorApp._do_new()
	await _settle()

	# ============ 空关卡：最容易出 null 的场景 ============
	_mark("空关卡 · 四种模式逐个切（每切一次面板重建）")
	for m in [EditorMode.Kind.ROAD, EditorMode.Kind.CONNECT,
			EditorMode.Kind.TRAFFIC, EditorMode.Kind.DECO]:
		EditorApp.set_mode(m)
		await _settle()
		print("[QA]   模式 %d → 面板 target = %s" % [m,
			String(panel.call("_panel_target")["kind"])])

	_mark("空关卡 · 专业档下未选车道（车辆总览 + 空线路列表）")
	EditorApp.set_mode(EditorMode.Kind.TRAFFIC)
	await _settle()
	EditorApp.set_edition(Edition.Level.PRO)
	await _settle()
	print("[QA]   空关卡总览：线路 %d / 站 %d" % [
		SumoBusModel.all_of(EditorApp.profile).size(),
		SumoBusModel.stop_choices(EditorApp.profile).size()])

	_mark("空关卡 · 在总览里点新建线路（没有车道，应给提示不报错）")
	var add_bus3: Button = _find_button(panel, "＋ 新建公交线路")
	if add_bus3 != null:
		add_bus3.pressed.emit()
		await _settle()

	_mark("空关卡 · 基础档 ↔ 专业档来回切")
	EditorApp.set_edition(Edition.Level.BASIC)
	await _settle()
	EditorApp.set_edition(Edition.Level.PRO)
	await _settle()

	_mark("完成")
	print("\n[QA] === 全部走完 ===\n")
	get_tree().quit()


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


func _count_state_cells() -> int:
	return _find_all(panel, "Button").filter(func(b: Button) -> bool:
		return ["G", "g", "y", "r", "o"].has(String((b as Button).text))).size()


func _first_state_cell() -> Button:
	for b in _find_all(panel, "Button"):
		if ["G", "g", "y", "r", "o"].has(String((b as Button).text)):
			return b as Button
	return null
