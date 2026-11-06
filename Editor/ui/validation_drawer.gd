class_name ValidationDrawer
extends "res://ui/float_window.gd"

## 校验问题抽屉（P4-1）：把**上一次校验已经算出来**的 `validation_errors` 逐条列出来，点一条就定位到对象。
##
## ★ 不新增能力：清单里的数据就是 `EditorApp.validation_error_items`（保存/导出时那次校验的原样结果）；
##   点一条走的是 `EditorApp.locate_validation()` → `select_object()` + `focus_world()`，与大纲双击同一条路。
##
## ★ 为什么用 FloatWindow：它是画布上的"工具类"浮层（和装饰素材一样），要能拖走、要让开各栏；
##   右侧坞是"属性"的去处，问题清单塞进去会和属性面板抢高度。

const W := 460.0
const LIST_H := 300.0

var _summary: Label
var _list: VBoxContainer
var _scroll: ScrollContainer


func _ready() -> void:
	title = "校验问题"
	closable = true
	default_on_left = false
	super._ready()
	_build_body()
	hide()


func _build_body() -> void:
	custom_minimum_size = Vector2(W, 260)
	size = Vector2(W, 420)
	_summary = Label.new()
	_summary.theme_type_variation = "HintLabel"
	_summary.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_summary.custom_minimum_size = Vector2(W - 32.0, 0)
	body().add_child(_summary)

	_scroll = ScrollContainer.new()
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_scroll.custom_minimum_size = Vector2(W - 32.0, LIST_H)
	_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	body().add_child(_scroll)

	_list = VBoxContainer.new()
	_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_list.add_theme_constant_override("separation", UITokens.S2)
	_scroll.add_child(_list)


## 打开并刷新（入口：状态栏「校验」胶囊 / 顶栏「校验」/ 导出失败后的提示）
func open_and_refresh() -> void:
	show()
	_place_default()
	refresh()


## 默认落在画布**右上角**：右下角是 Toast 的地盘（P4-1 同批做的），叠在一起就两边都看不清
func _place_default() -> void:
	var area := _canvas_rect()
	position = Vector2(area.end.x - size.x - float(UITokens.S4), area.position.y + float(UITokens.S4))
	_clamp_pos()


func refresh() -> void:
	if _list == null:
		return
	for c in _list.get_children():
		c.queue_free()
	var items: Array = EditorApp.validation_error_items
	var errs := 0
	var warns := 0
	for e in items:
		if e is Dictionary and String(e.get("Severity", "")) == "Error":
			errs += 1
		else:
			warns += 1

	if int(EditorApp.validation_errors) < 0:
		_summary.text = "还没校验过。保存或导出时会跑一次校验，结果会列在这里。"
		_fit_height()
		return
	if items.is_empty():
		_summary.text = "上次校验通过：0 条错误。" + ("" if warns == 0 else "（%d 条提示）" % warns)
		_fit_height()
		return
	_summary.text = "上次校验：%d 条错误" % errs + ("" if warns == 0 else "、%d 条提示" % warns) \
		+ "  ·  点一条定位到对象"

	# 错误在前、提示在后；同类里保持校验器给的顺序（Path 大致按表排，读起来顺）
	for sev in ["Error", "Warning"]:
		for e in items:
			if not (e is Dictionary):
				continue
			if String(e.get("Severity", "")) != sev:
				continue
			_list.add_child(_row(e))
	_fit_height()


## 窗口高度跟着条目数走：一条错误就别开一个 420 高的空抽屉（最多到 LIST_H）
func _fit_height() -> void:
	await get_tree().process_frame                       # 等容器算出子节点高度
	if _scroll == null or not is_inside_tree():
		return
	var need: float = _list.get_combined_minimum_size().y
	var list_h := clampf(need, 68.0, LIST_H)
	_scroll.custom_minimum_size = Vector2(W - 32.0, list_h)
	var h := list_h + 132.0                              # 标题栏 34 + 概览行 + 上下内边距
	size = Vector2(W, h)
	custom_minimum_size = Vector2(W, h)
	_clamp_pos()
	Motion.pop_in(self, size * 0.5)              # P4-2：打开时 scale 0.96→1 + 淡入


func _row(e: Dictionary) -> Control:
	var is_err := String(e.get("Severity", "")) == "Error"
	var card := Clay.inset(UITokens.S2)
	card.mouse_filter = Control.MOUSE_FILTER_STOP
	card.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	var can_locate: bool = EditorApp.can_locate_validation(e)
	card.tooltip_text = "点一下定位到该对象" if can_locate else "这条没有对应的画布对象（%s）" % String(e.get("Path", ""))

	var v := VBoxContainer.new()
	v.mouse_filter = Control.MOUSE_FILTER_IGNORE
	v.add_theme_constant_override("separation", 0)
	card.add_child(v)

	var top := HBoxContainer.new()
	top.mouse_filter = Control.MOUSE_FILTER_IGNORE
	top.add_theme_constant_override("separation", UITokens.S1)
	v.add_child(top)
	var dot := Label.new()
	dot.text = "●"
	dot.mouse_filter = Control.MOUSE_FILTER_IGNORE
	dot.add_theme_font_size_override("font_size", UITokens.T_CAP)
	dot.add_theme_color_override("font_color", UITokens.RED_500 if is_err else UITokens.AMBER_500)
	top.add_child(dot)
	var head := Label.new()
	head.text = String(e.get("Code", "?"))
	head.mouse_filter = Control.MOUSE_FILTER_IGNORE
	head.add_theme_font_size_override("font_size", UITokens.T_CAP)
	head.add_theme_color_override("font_color", UITokens.INK_2)
	head.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	head.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(head)

	var msg := Label.new()
	msg.text = String(e.get("Message", ""))
	msg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	msg.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	msg.custom_minimum_size = Vector2(W - 92.0, 0)      # 自动换行的 Label 必须给宽（E99 配方）
	msg.add_theme_font_size_override("font_size", UITokens.T_CAP + 1)
	msg.add_theme_color_override("font_color", UITokens.INK_1)
	v.add_child(msg)

	var path := Label.new()
	path.text = String(e.get("Path", ""))
	path.mouse_filter = Control.MOUSE_FILTER_IGNORE
	path.add_theme_font_size_override("font_size", UITokens.T_CAP)
	path.add_theme_color_override("font_color", UITokens.INK_3)
	path.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	v.add_child(path)

	if can_locate:
		card.gui_input.connect(func(ev: InputEvent) -> void:
			if ev is InputEventMouseButton:
				var mb := ev as InputEventMouseButton
				if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
					EditorApp.locate_validation(e)
					card.accept_event())
	return card
