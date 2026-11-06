extends Node

## 教学文档配图生成器（2026-11-06）
##
## 用法（**必须窗口模式**，不能 headless —— headless 没有渲染器）：
##   "$GODOT" --path Editor res://qa/qa_guide_shots.tscn
## 产物：`docs/教学/img/*.png`（覆盖写）。
##
## ★ 为什么要这个脚本：教学文档要"真实截图"而不是手绘图。手点截图有三个问题：
##   ① 分辨率/缩放不一致；② 无法复现；③ 改版后要全部重截。脚本化后一条命令重生成。
## ★ 所有状态切换都在内存里做，**不落盘**（导入用示例关卡，专业档数据是临时构造的）。

const W := 1800
const H := 1200

## 输出目录（相对工程根的上一级：Editor/ → 项目根）
const OUT_REL := "../docs/教学/img/"

var main: Node
var fails := 0

func _ready() -> void:
	await get_tree().process_frame
	get_window().size = Vector2i(W, H)
	get_window().position = Vector2i(30, 20)
	await get_tree().process_frame
	await get_tree().process_frame

	main = load("res://scenes/Main.tscn").instantiate()
	# ★ Main 必须直接挂在 root 下、并成为 current_scene：
	#   `EditorApp.fit_all()` / `focus_world()` 都从 current_scene 往下找 EditorCamera。
	#   挂在自己（ShootProto）名下会报 `p_scene->get_parent() != root`，current_scene 设不上，
	#   相机就不动 —— 表现是「关卡挤在画面中间、四周大片空白」。
	get_tree().root.add_child(main)
	get_tree().current_scene = main
	await get_tree().process_frame
	await get_tree().create_timer(0.9).timeout

	var norm := _norm_dir(ProjectSettings.globalize_path("res://").path_join(OUT_REL))
	DirAccess.make_dir_recursive_absolute(norm)
	print("[SHOTS] 输出目录 = ", norm)

	EditorApp.import_map(ProjectSettings.globalize_path("res://../docs/examples/01-1-first-crossing.spmap"))
	EditorApp.set_edition(Edition.Level.BASIC)
	await _settle(0.9)

	await _basic_shots()
	await _export_shots()
	await _pro_shots()

	print("[SHOTS] === 完成：失败 %d 项 ===" % fails)
	get_tree().quit(1 if fails > 0 else 0)


# ============================================================
# 基础版
# ============================================================

func _basic_shots() -> void:
	# --- 1. 界面总览 ---
	EditorApp.set_mode(EditorMode.Kind.ROAD)
	EditorApp.select_object("road", -1, false)
	await _settle()
	EditorApp.fit_all(0.10)
	await _settle(0.6)
	await _shot("b1-界面总览.png")

	# --- 2. 横断面面板（选中一条路）---
	var rid := _first_road_id()
	print("[SHOTS] 第一条路 id = ", rid)
	EditorApp.select_road(rid)
	await _settle(0.8)
	await _shot("b2-横断面.png", _dock_rect())
	await _shot("b2b-选中一条路.png")

	# --- 3. 连接模式（选信号路口）---
	var sig := _signal_node_id()
	print("[SHOTS] 信号路口 id = ", sig)
	EditorApp.set_mode(EditorMode.Kind.CONNECT)
	EditorApp.select_turn(-1)
	EditorApp.select_junction(sig)
	await _settle(1.0)
	await _shot("b3-连接模式.png")
	await _shot("b4-配时面板.png", _dock_rect())

	# --- 4. 需求曲线（车辆模式选进口车道）---
	var lane := _first_motor_lane()
	print("[SHOTS] 进口车道 id = ", lane)
	EditorApp.set_mode(EditorMode.Kind.TRAFFIC)
	EditorApp.select_traffic_lane(lane)
	await _settle(1.0)
	await _shot("b5-需求曲线.png", _dock_rect())

	# --- 5. 导出对话框 ---
	EditorApp.set_mode(EditorMode.Kind.ROAD)
	await _settle(0.5)
	EditorApp.open_export_dialog()
	await _settle(1.2)
	await _shot("b6-导出对话框.png")
	# ★ 不关它：下一段还要在同一个窗口里真导出、真试跑（关掉再开会让状态白丢）


# ============================================================
# 导出 / 试跑 / 校验清单
# ============================================================

func _export_shots() -> void:
	# ★ `_on_export()` 会把输出目录写进 `user://export_settings.json`，
	#   截完必须恢复 —— 否则老板下次打开导出框，目录变成我们的临时目录。
	var settings_script: Script = load("res://scripts/export/export_settings.gd")
	var backup: Dictionary = settings_script.load_settings()

	# 造一条 Warning（给**普通路口**配专业配时）→ 校验清单里才有东西可看
	EditorApp.set_edition(Edition.Level.PRO)
	await _settle(0.8)
	var p: SumoProfile = EditorApp.profile
	var plain := _plain_node_id()
	var turns := _turn_ids_at(plain)
	var states := {}
	for tid in turns:
		states[int(tid)] = "G"
	p.tls = [{"node": plain, "programs": [{"id": "0", "activate_at": 0.0, "offset": 0.0,
		"type": "static", "phases": [{"duration": 30.0, "name": "全放", "states": states}]}]}]
	p.touch()
	await _settle(0.6)

	var dlg: Node = EditorApp.get("_export_dialog")
	if dlg == null or not is_instance_valid(dlg):
		EditorApp.open_export_dialog()
		await _settle(1.2)
		dlg = EditorApp.get("_export_dialog")
	if dlg == null or not is_instance_valid(dlg):
		fails += 1
		print("[SHOTS] [FAIL] 导出对话框没打开")
		settings_script.save_settings(backup)
		return

	var tmp := OS.get_user_data_dir().path_join("guide_export")
	DirAccess.make_dir_recursive_absolute(tmp)

	# 填一个干净的目录/文件名，并只勾 SUMO 三件套（教学图别带上一堆无关勾选）
	var dir_edit = dlg.get("_dir_edit")
	var name_edit = dlg.get("_name_edit")
	var pills: Dictionary = dlg.get("_pills")
	if dir_edit != null:
		(dir_edit as LineEdit).text = tmp.replace("\\", "/")
	if name_edit != null:
		(name_edit as LineEdit).text = "01-1"
	for key in pills.keys():
		var wanted: bool = String(key) in ["net", "rou", "sumocfg"]
		var pill = pills[key]
		if pill != null and (pill as Button).button_pressed != wanted:
			(pill as Button).button_pressed = wanted
	await _settle(0.6)

	# --- 真导出（走对话框自己的按钮逻辑，与用户点击完全同一条路）---
	if not (dlg as Window).visible:
		(dlg as Window).popup_centered(Vector2i(780, 780))
		await _settle(0.6)
	dlg.call("_on_export")
	await _settle(1.8)
	# ★ 窗口固定 780×780，结果区在滚动区底部 —— 不滚到底根本看不见（用户也要滚）
	_scroll_dialog_bottom(dlg)
	await _settle(0.6)
	await _shot("b7-导出结果.png")

	# --- 真跑一次 sumo，截统计 ---
	var run_btn = dlg.get("_run_btn")
	if run_btn != null and not (run_btn as Button).disabled:
		dlg.call("_on_run_sumo")
		# 试跑是异步的：等它把按钮文字从「正在跑…」改回来
		var guard := 0
		while guard < 120:
			await get_tree().create_timer(0.5).timeout
			guard += 1
			if not String((run_btn as Button).text).contains("正在跑"):
				break
		_scroll_dialog_bottom(dlg)
		await _settle(0.8)
		await _shot("b8-试跑统计.png")
	else:
		fails += 1
		print("[SHOTS] [FAIL] 试跑按钮没点亮（导出没成功？）")

	# 收起对话框，打开校验清单
	dlg.hide()
	await _settle(0.6)
	EditorApp.open_validation_drawer()
	await _settle(1.0)
	await _shot("b9-校验清单.png")
	var drawer := main.get_node_or_null("CanvasLayer/ValidationDrawer") as Control
	if drawer != null:
		drawer.visible = false
	await _settle(0.3)

	# 收尾：清掉造出来的 Warning，恢复导出设置
	p.tls.clear()
	p.touch()
	settings_script.save_settings(backup)
	print("[SHOTS] 导出目录设置已恢复")


# ============================================================
# 专业版
# ============================================================

func _pro_shots() -> void:
	EditorApp.set_edition(Edition.Level.PRO)
	await _settle(1.0)

	# 造一点专业档数据，面板才不是空的（只在内存里，不落盘）
	_seed_pro_profile()

	# --- 6. 专业模式 · 关卡面板（滚到「连接生成」，顺带露出两块 SUMO 关卡设置）---
	EditorApp.set_mode(EditorMode.Kind.ROAD)
	EditorApp.select_object("road", -1, false)
	await _settle(1.2)
	await _scroll_to_section("连接生成", -40)
	# p1 是整窗（看清"面板在哪"），面板特写用后面 p4-运行配置（同一滚动位置，裁得更清）——
	# 两张同源，所以这里只留整窗那张，别再存一份重复的面板裁剪。
	await _shot("p1-专业模式关卡.png")

	# --- 7. 逐灯位配时矩阵 ---
	EditorApp.set_mode(EditorMode.Kind.CONNECT)
	EditorApp.select_turn(-1)
	EditorApp.select_junction(_signal_node_id())
	await _settle(1.6)
	await _scroll_to_section("信号灯配时", -6)
	await _shot("p2-逐灯位矩阵.png", _dock_rect())

	# --- 8~10. 车道面板：车型构成 / 检测设施 / 车道属性 ---
	EditorApp.set_mode(EditorMode.Kind.TRAFFIC)
	EditorApp.select_traffic_lane(_first_motor_lane())
	await _settle(1.4)
	await _scroll_to_section("进口车型构成", -8)
	await _shot("p3-车型与车流.png", _dock_rect())
	# ★ 车道面板一屏就装下了「车型构成 / 检测设施 / 交通设施 / 公交线路 / 车道属性」，
	#   再往下滚还是同一屏（内容到底了），截出来是重复图 —— 所以只留 p3 这一张。
	#   实测：scroll=754 与 scroll=991 出来的像素完全一样，别再截第二张。

	# --- 11. 运行配置（关卡面板底部）---
	EditorApp.set_mode(EditorMode.Kind.ROAD)
	EditorApp.select_object("road", -1, false)
	await _settle(1.4)
	await _scroll_to_section("SUMO 运行配置", -8)
	await _shot("p4-运行配置.png", _dock_rect())
	var body := _dock_body()
	if body != null:
		body.scroll_vertical = 0


# ============================================================
# 专业档示例数据（内存构造）
# ============================================================

func _seed_pro_profile() -> void:
	var p: SumoProfile = EditorApp.profile
	p.config.clear()
	p.overrides.clear()
	p.tls.clear()
	p.additional.clear()
	p.flows.clear()

	# 逐灯位矩阵：给信号路口配两相位
	var turns := _turn_ids_at(_signal_node_id())
	var ph1 := {}
	var ph2 := {}
	for i in turns.size():
		# ★ key 必须是 **int**：面板按 turn id（int）查 states，写字符串会全都查不到 → 矩阵一片红。
		ph1[int(turns[i])] = "G" if i % 2 == 0 else "r"
		ph2[int(turns[i])] = "r" if i % 2 == 0 else "G"
	p.tls = [{
		"node": _signal_node_id(),
		"programs": [{"id": "0", "activate_at": 0.0, "offset": 0.0, "type": "static",
			"phases": [
				{"duration": 30.0, "name": "东西直行", "states": ph1},
				{"duration": 3.0, "name": "黄", "states": {}},
				{"duration": 27.0, "name": "南北直行", "states": ph2},
			]}],
	}]

	# 覆盖层：一条车道只允许公交 + 一个路口无控制
	SumoNetModel.set_lane_field(p, _first_motor_lane(), "access", "bus")
	SumoNetModel.set_netconvert_option(p, "no_internal_links", true)

	# 检测设施：一条进口车道上的 E1 检测器
	p.additional = [{"kind": "e1", "lane": _first_motor_lane(), "pos": 20.0, "id": "e1_1"}]

	p.touch()
	print("[SHOTS] 专业档已造：tls=%d additional=%d overrides=%s" % [
		p.tls.size(), p.additional.size(), str(p.overrides.keys())])


# ============================================================
# 工具
# ============================================================

func _settle(t := 0.45) -> void:
	await get_tree().process_frame
	await get_tree().create_timer(t).timeout
	await get_tree().process_frame
	# ★ Toast 会浮在画布右下角挡住内容，截之前一律清掉
	_clear_toasts()


func _clear_toasts() -> void:
	var host := main.get_node_or_null("CanvasLayer/Toast")
	if host == null:
		return
	for c in host.get_children():
		if c is Control:
			(c as Control).visible = false


## 把导出对话框的正文滚到底（结果区/统计/试跑按钮都在下面）
func _scroll_dialog_bottom(dlg: Node) -> void:
	for c in (dlg as Node).find_children("*", "ScrollContainer", true, false):
		(c as ScrollContainer).scroll_vertical = 100000
		return


func _dock() -> Control:
	return main.get_node_or_null("CanvasLayer/Dock") as Control


func _dock_rect() -> Rect2i:
	var d := _dock()
	if d == null:
		fails += 1
		print("[SHOTS] [FAIL] 找不到 Dock")
		return Rect2i()
	var r: Rect2 = d.get_global_rect()
	return Rect2i(int(r.position.x), int(r.position.y), int(r.size.x), int(r.size.y))


func _dock_body() -> ScrollContainer:
	var d := _dock()
	if d == null:
		return null
	for c in d.find_children("*", "ScrollContainer", true, false):
		return c as ScrollContainer
	return null


func _scroll_dock(y: int) -> void:
	var body := _dock_body()
	if body == null:
		return
	body.scroll_vertical = y
	await _settle(0.5)


## 把某个折叠小节的头滚到可视区顶部。★ 只能靠标题按钮定位 —— 面板每次重建都会换新控件，
## 所以每次都要现找，不能缓存。
func _scroll_to_section(title: String, offset := 0) -> void:
	var body := _dock_body()
	if body == null:
		fails += 1
		print("[SHOTS] [FAIL] 找不到坞，无法滚到「%s」" % title)
		return
	# ★ 直接在 ScrollContainer 子树里找标题按钮：PropertyPanel 被 dock.gd reparent 进段体，
	#   按 `CanvasLayer/Dock/PropertyPanel` 这种路径取不到它。
	for c in body.find_children("*", "Button", true, false):
		var btn := c as Button
		var txt := String(btn.text)
		# 小节头一律以 ▾ / ▸ 起头，别点到别处同名文字
		if not (txt.begins_with("▾") or txt.begins_with("▸")):
			continue
		if not txt.contains(title):
			continue
		var dy := btn.get_global_rect().position.y - body.get_global_rect().position.y
		body.scroll_vertical = int(maxf(0.0, body.scroll_vertical + dy - 8.0 + offset))
		await _settle(0.6)
		print("[SHOTS] 滚到「%s」→ scroll=%d" % [title, body.scroll_vertical])
		return
	fails += 1
	print("[SHOTS] [FAIL] 面板里没有小节「%s」" % title)


func _first_road_id() -> int:
	var d: Doc = EditorApp.doc
	for r in d.roads:
		return int(r.road_id)
	return -1


func _signal_node_id() -> int:
	var d: Doc = EditorApp.doc
	for n in d.nodes:
		if String(n.get("type", "")) == "Signalized":
			return int(n.id)
	return -1


func _plain_node_id() -> int:
	var d: Doc = EditorApp.doc
	for n in d.nodes:
		if String(n.get("type", "")) == "Unsignalized":
			return int(n.id)
	return -1


func _first_motor_lane() -> int:
	var d: Doc = EditorApp.doc
	for l in d.lanes:
		if String(l.get("type", "")) in Doc.MOTOR_LANE_TYPES:
			return int(l.id)
	return -1


func _turn_ids_at(node_id: int) -> Array:
	var out: Array = []
	var d: Doc = EditorApp.doc
	for t in d.turns:
		if int(t.get("node", -1)) == node_id:
			out.append(int(t.id))
	return out


func _norm_dir(p: String) -> String:
	var parts := p.replace("\\", "/").split("/")
	var stack: Array = []
	for s in parts:
		if s == "" or s == ".":
			continue
		if s == "..":
			if not stack.is_empty():
				stack.pop_back()
			continue
		stack.append(s)
	var head := "/" if p.begins_with("/") else ""
	return head + "/".join(PackedStringArray(stack))


func _shot(name: String, crop := Rect2i()) -> void:
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var img: Image = get_viewport().get_texture().get_image()
	if crop.size.x > 0 and crop.size.y > 0:
		var safe := crop.intersection(Rect2i(Vector2i.ZERO, img.get_size()))
		if safe.size.x > 0 and safe.size.y > 0:
			img = img.get_region(safe)
		else:
			fails += 1
			print("[SHOTS] [FAIL] %s 裁剪区越界：%s" % [name, str(crop)])
			return
	if img.is_empty():
		fails += 1
		print("[SHOTS] [FAIL] %s 截到空图" % name)
		return
	var out := _norm_dir(ProjectSettings.globalize_path("res://").path_join(OUT_REL + name))
	var err := img.save_png(out)
	if err != OK:
		fails += 1
		print("[SHOTS] [FAIL] %s 保存失败 err=%d" % [name, err])
		return
	print("[SHOTS] ✓ %s  %dx%d" % [name, img.get_width(), img.get_height()])
