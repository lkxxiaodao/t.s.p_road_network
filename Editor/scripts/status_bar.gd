extends Control

## 状态栏（`docs/ui/UI-R0 §2.2 ⑥`）：高 32，**分区胶囊**。
## 左：模式 / 选中 / 坐标；右：测量 / 缩放 / 吸附 / 校验 / 文件。
##
## ★ 数据口径与旧版（一条长 Label）**逐项对齐**，一项没少；
##   改成胶囊是因为拼字符串到后面字段一长就挤成一片，也没法单独上色。
## ★ 坐标用 `UITokens.tnum_font()`（等宽数字），否则数字宽度不一会让整条栏左右抖（R0 §1.7 硬要求）。
## ★ 旧版 `_ready()` 里那两行"深色字 + 白描边"的兜底已随本文件重写而删除 —— 现在有背景板了。

var _l_mode: Label
var _l_sel: Label
var _l_pos: Label
var _l_measure: Label
var _l_zoom: Label
var _l_snap: Label
var _l_check: Label
var _l_file: Label

var _grid: Node = null
var _cache := {}
var _last_errs := -2            ## 上一次的错误数（-2 = 还没读过）；变了才闪红（P4-2）


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP
	# 高度从令牌推：字号一改这里自动跟上（写死在 .tscn 里就会把文字裁掉一截）
	offset_top = -float(UITokens.STATUS_BAR_H)
	offset_bottom = 0.0
	# 先按相对路径找（本节点永远在 Main/CanvasLayer 下），找不到再退回 current_scene ——
	# 临时自测场景把 Main 当子节点实例化时，current_scene 不是 Main，只靠它会拿到 null。
	_grid = get_node_or_null("../../GridCanvas")
	if _grid == null:
		var sc: Node = get_tree().current_scene
		if sc != null:
			_grid = sc.get_node_or_null("GridCanvas")
	_build()
	set_process(true)


func _process(_delta: float) -> void:
	_refresh()


func _build() -> void:
	var bg := Clay.flat()
	bg.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	bg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(bg)

	# 上沿发丝线：状态栏压在画布下沿，需要一道分界
	var line := ColorRect.new()
	line.color = UITokens.SURFACE_3
	line.mouse_filter = Control.MOUSE_FILTER_IGNORE
	line.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	line.offset_top = 0
	line.offset_bottom = 1
	add_child(line)

	var m := MarginContainer.new()
	m.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	m.add_theme_constant_override("margin_left", UITokens.S3)
	m.add_theme_constant_override("margin_right", UITokens.S3)
	m.add_theme_constant_override("margin_top", UITokens.S1)
	m.add_theme_constant_override("margin_bottom", UITokens.S1)
	add_child(m)

	var h := HBoxContainer.new()
	h.add_theme_constant_override("separation", UITokens.S2)
	m.add_child(h)

	_l_mode = _pill(h, false)
	_l_sel = _pill(h, false)
	_l_pos = _pill(h, true)

	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	spacer.mouse_filter = Control.MOUSE_FILTER_IGNORE
	h.add_child(spacer)

	_l_measure = _pill(h, true)
	_l_zoom = _pill(h, true)
	_l_snap = _pill(h, false)
	# 校验胶囊：点一下打开「校验问题」清单（P4-1）。清单里点一条能定位到对象。
	_l_check = _pill(h, true, func() -> void: EditorApp.toggle_validation_drawer())
	_l_file = _pill(h, false)
	# ★ 窄窗口（最小 990）下文件名比剩余空间长，硬切会切掉半个字 —— 给省略号（P5 复查）
	_l_file.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_l_file.clip_text = true                  # ★ 只设 overrun 不够：Label 最小宽 = 文本宽，压不下去就不会省略
	_l_file.custom_minimum_size.x = 150        # 但也别给 0 —— HBox 会把它压到 0 宽（整个文件项消失）


## 造一个胶囊：凹槽底 + 一个 Label，返回 Label 供写文本。
## `on_click` 有效时整个胶囊可点（校验那颗用它打开问题清单）；Label 必须 IGNORE，否则点不到胶囊本身。
func _pill(parent: Node, tnum: bool, on_click: Callable = Callable()) -> Label:
	var p := Clay.inset(UITokens.S1)          # 胶囊内边距收到 4px：字号 15 时 "字 + 上下 6" 正好装进 42 高的栏
	p.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	if on_click.is_valid():
		p.mouse_filter = Control.MOUSE_FILTER_STOP
		p.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
		p.tooltip_text = "看校验问题清单（点一条定位到对象）"
		p.gui_input.connect(func(ev: InputEvent) -> void:
			if ev is InputEventMouseButton:
				var mb := ev as InputEventMouseButton
				if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
					on_click.call()
					p.accept_event())
	var l := Label.new()
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	l.theme_type_variation = "MutedLabel"
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	if tnum:
		l.add_theme_font_override("font", UITokens.tnum_font())
	p.add_child(l)
	parent.add_child(p)
	return l


# ============================================================
# 刷新（只在文本变时写 Label —— 赋值会触发重排）
# ============================================================

func _refresh() -> void:
	_put(_l_mode, "mode", "模式：%s" % _mode_name(EditorApp.mode))
	_put(_l_sel, "sel", "选中：%s" % _selection_text())
	_put(_l_pos, "pos", _pos_text())
	_put(_l_measure, "measure", _measure_text())
	# ★ 测量胶囊只在测量时存在（老板 2026-10-28：缩放左边那个空框就是它 —— 不测量时它是空的）。
	#   以前只写空文本、壳留着，于是永远挂着一个空凹槽。
	var measure_pill := _l_measure.get_parent() as Control
	if measure_pill != null:
		measure_pill.visible = bool(_grid != null and bool(_grid.measuring))
	_put(_l_zoom, "zoom", _zoom_text())
	_put(_l_snap, "snap", _snap_text())
	_put(_l_check, "check", _check_text())
	_put(_l_file, "file", _file_text())

	if _l_check != null:
		var errs: int = EditorApp.validation_errors
		_l_check.add_theme_color_override("font_color",
			UITokens.CONFLICT if errs > 0 else UITokens.INK_2)
		# ★ P4-2：错误数**变了**才闪一次红（R0 §4「校验错误」）；启动/切换文档的那次不算
		if errs != _last_errs:
			var first := _last_errs == -2
			_last_errs = errs
			if errs > 0 and not first:
				var pill := _l_check.get_parent() as Control
				if pill != null:
					Motion.pulse(pill, 2, 0.12)


func _put(label: Label, key: String, text: String) -> void:
	if label == null:
		return
	if String(_cache.get(key, "")) == text:
		return
	_cache[key] = text
	label.text = text


func _pos_text() -> String:
	if _grid == null:
		return "坐标：—"
	var m: Vector2 = _grid.mouse_world_meters()
	return "坐标：%.1f m, %.1f m" % [m.x, m.y]


## 选中口径与旧版一致：多选报总数（而不是"最后一项"）
func _selection_text() -> String:
	if EditorApp.selection.size() > 1:
		return "已选 %d 条路" % EditorApp.selection.size()
	if EditorApp.selected_deco >= 0:
		return "装饰 #%d" % EditorApp.selected_deco
	if EditorApp.selected_junction >= 0:
		var node: Dictionary = EditorApp.doc.find_node(EditorApp.selected_junction)
		var nt := String(node.get("type", "?"))
		return "节点 #%d (%s)" % [EditorApp.selected_junction, nt]
	if EditorApp.selected_turn >= 0:
		return "连接 #%d" % EditorApp.selected_turn
	if EditorApp.selected_road >= 0:
		return "路 #%d" % EditorApp.selected_road
	return "无"


func _zoom_text() -> String:
	var cam := get_viewport().get_camera_2d()
	if cam == null:
		return "缩放：—"
	return "缩放：%d%%" % int(round(cam.zoom.x * 100.0))


func _snap_text() -> String:
	if _grid == null:
		return "吸附：—"
	return "吸附：%s" % ("开" if bool(_grid.snap_enabled) else "关")


func _check_text() -> String:
	var errs: int = EditorApp.validation_errors
	if errs < 0:
		return "校验：未跑"
	if errs == 0:
		return "校验：通过"
	return "校验：%d 错" % errs


func _file_text() -> String:
	var p := String(EditorApp.current_path)
	var name_text := "（未保存）"
	if p != "":
		name_text = p.get_file()
	if EditorApp.doc != null and bool(EditorApp.doc.dirty):
		name_text += " *"
	return "文件：%s" % name_text


func _measure_text() -> String:
	if _grid == null or not bool(_grid.measuring):
		return ""
	if bool(_grid.measure_has_a) and bool(_grid.measure_has_b):
		var a: Vector2 = _grid.measure_a_m
		var b: Vector2 = _grid.measure_b_m
		return "测量：%.2f m  方位角 %.1f°" % [a.distance_to(b), GridCanvas.bearing_deg(a, b)]
	var step := "点第一个点"
	if bool(_grid.measure_has_a):
		step = "点第二个点"
	return "测量：%s（Esc 取消）" % step


func _mode_name(k: int) -> String:
	match k:
		EditorMode.Kind.ROAD: return "道路"
		EditorMode.Kind.CONNECT: return "连接"
		EditorMode.Kind.TRAFFIC: return "车辆"
		EditorMode.Kind.DECO: return "装饰物"
	return "?"
