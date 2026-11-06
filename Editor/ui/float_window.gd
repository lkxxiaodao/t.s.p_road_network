class_name FloatWindow
extends PanelContainer

## 画布上的浮窗（可拖动、限制在画布区域内）。
##
## 第一个用户 = 装饰素材列表：老板 2026-10-27 要求它**不再占右侧坞**，改成浮在画布左侧居中、可拖。
##
## ★ 本类只管"壳"：标题条 + 正文容器 + 拖动 + 边界钳制。
##   里面放什么由场景决定（`DecoFloat` 下面挂 `DecoPalette`），本类把子节点收进正文、剥掉它们自带的卡片底。
##
## ★ 拖动同样必须在 `_input()` 里收 Motion（`gui_input` 只在指针仍压着标题条时才来，手一抖就断）。
##
## ★ 钳制区域 = **画布可见区**：让开左栏、图层胶囊列、顶栏、上下文条、状态栏、键位条、右侧坞。
##   宁可少拖一点，也不能让浮窗压到那些栏上（否则关不掉、拖不回）。

const HEAD_H := 34

signal closed                  ## 点了标题栏右侧的 ✕（closable = true 时才存在）

@export var title: String = "浮窗"
## >= 0 时只在这个模式显示（用 `EditorMode.Kind`）；-1 = 一直显示
@export var only_mode: int = -1
## 首次出现时靠左居中；之后记住用户拖到的位置
@export var default_on_left: bool = true
## 标题栏右侧给一个 ✕（点它只隐藏 + 发 closed，不 destroy）
@export var closable: bool = false

var _head: PanelContainer
var _body: VBoxContainer
var _drag := false
var _grab := Vector2.ZERO
var _placed := false
## 标题栏里的按钮（✕ 等）。★ 拖动必须先让开它们，见 `_input` 的注释。
var _head_buttons: Array = []


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP
	custom_minimum_size = Vector2(240, 300)
	_build()
	_adopt_children()
	_layout_default()
	resized.connect(_clamp_pos)          # 内容变高/变窄也要拉回画布内
	get_viewport().size_changed.connect(_clamp_pos)
	if only_mode >= 0:
		EditorApp.mode_changed.connect(_on_mode_changed)
		_on_mode_changed(EditorApp.mode)


func body() -> VBoxContainer:
	return _body


func _build() -> void:
	var root := VBoxContainer.new()
	root.name = "Root"
	root.add_theme_constant_override("separation", 0)
	add_child(root)

	_head = PanelContainer.new()
	_head.name = "Head"
	_head.theme_type_variation = "CardHeaderPanel"
	_head.custom_minimum_size = Vector2(0, HEAD_H)
	_head.mouse_filter = Control.MOUSE_FILTER_STOP
	_head.mouse_default_cursor_shape = Control.CURSOR_MOVE
	var hb := HBoxContainer.new()
	hb.add_theme_constant_override("separation", UITokens.S2)
	_head.add_child(hb)
	var grip := Label.new()
	grip.text = "⋮⋮"
	grip.theme_type_variation = "HintLabel"
	grip.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	hb.add_child(grip)
	var lab := Label.new()
	lab.name = "Title"
	lab.text = title
	lab.theme_type_variation = "TitleLabel"
	lab.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	lab.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	hb.add_child(lab)
	if closable:
		var x := Clay.btn_ghost("✕")
		x.name = "Close"
		x.focus_mode = Control.FOCUS_NONE
		x.custom_minimum_size = Vector2(28, 24)
		x.tooltip_text = "关掉这个浮窗"
		x.pressed.connect(func() -> void:
			hide()
			closed.emit())
		hb.add_child(x)
		_head_buttons.append(x)
	root.add_child(_head)

	_body = VBoxContainer.new()
	_body.name = "Body"
	_body.add_theme_constant_override("separation", UITokens.S2)
	_body.size_flags_vertical = Control.SIZE_EXPAND_FILL
	root.add_child(_body)


## 把场景里预先挂上的子节点收进正文：剥掉它们自带的卡片底（外面这层已经是卡片了）
func _adopt_children() -> void:
	var root: Node = _body.get_parent()
	var kids: Array = []
	for c in get_children():
		if c != root:
			kids.append(c)          # ★ 先快照再搬：边遍历边 reparent 会跳元素
	for c in kids:
		if c is Control:
			var ctl := c as Control
			var was_visible := ctl.visible
			ctl.reparent(_body, false)
			ctl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			ctl.size_flags_vertical = Control.SIZE_EXPAND_FILL
			ctl.visible = was_visible
			ctl.add_theme_stylebox_override("panel", StyleBoxEmpty.new())
		else:
			c.reparent(_body, false)


func _layout_default() -> void:
	if _placed:
		return
	_placed = true
	var area := _canvas_rect()
	size = Vector2(maxf(custom_minimum_size.x, size.x), maxf(custom_minimum_size.y, size.y))
	if default_on_left:
		position = Vector2(area.position.x + float(UITokens.S4),
			area.position.y + (area.size.y - size.y) * 0.5)
	_clamp_pos()


func _on_mode_changed(kind: int) -> void:
	var was := visible
	visible = kind == only_mode
	if visible:
		_layout_default()
		if not was:
			Motion.pop_in(self, size * 0.5)      # P4-2：浮窗出现时 scale 0.96→1 + 淡入


# ============================================================
# 拖动
# ============================================================

func _input(ev: InputEvent) -> void:
	if not visible:
		return
	if ev is InputEventMouseButton:
		var mb := ev as InputEventMouseButton
		if mb.button_index != MOUSE_BUTTON_LEFT:
			return
		if mb.pressed:
			# ★ 标题栏里的按钮（✕）不能启动拖动 —— 2026-11-06 修的 bug：
			#   原来只要落在标题条里就 `_drag = true` + `set_input_as_handled()`，
			#   按下的鼠标事件在 `_input` 阶段就被吞掉，✕ 的按下/抬起配不上对，
			#   `pressed` 永远不触发 → 表现成"浮窗上的 ✕ 点了没反应，只能用触发它的按钮再关一次"。
			if _head.get_global_rect().has_point(mb.position) and not _over_head_button(mb.position):
				_drag = true
				_grab = mb.position - position
				get_viewport().set_input_as_handled()
		else:
			_drag = false
		return
	if ev is InputEventMouseMotion and _drag:
		var mm := ev as InputEventMouseMotion
		if mm.button_mask & MOUSE_BUTTON_MASK_LEFT == 0:
			_drag = false
			return
		position = mm.position - _grab
		_clamp_pos()
		get_viewport().set_input_as_handled()


## 按下的点是不是落在标题栏的某个按钮上（✕ 等）
func _over_head_button(pos: Vector2) -> bool:
	for b in _head_buttons:
		if is_instance_valid(b) and (b as Control).get_global_rect().has_point(pos):
			return true
	return false


## 画布可见区：左栏 + 图层胶囊列 → 右坞左缘；顶栏 + 上下文条 → 状态栏 + 键位条
func _canvas_rect() -> Rect2:
	var vp := get_viewport_rect().size
	var dock_w := float(UITokens.DOCK_W_DEFAULT)
	var docks := get_tree().get_nodes_in_group("editor_dock")
	if not docks.is_empty():
		dock_w = (docks[0] as Control).size.x
	var left := float(UITokens.TOOL_RAIL_W) + float(UITokens.LAYER_RAIL_W) + float(UITokens.S4)
	var top := float(UITokens.TOP_BAR_H) + float(UITokens.CONTEXT_BAR_H) + float(UITokens.S3)
	var right := vp.x - dock_w - float(UITokens.S4)
	var bottom := vp.y - float(UITokens.STATUS_BAR_H) - float(UITokens.HINT_BAR_H) - float(UITokens.S2)
	return Rect2(left, top, maxf(right - left, 120.0), maxf(bottom - top, 120.0))


func _clamp_pos() -> void:
	if not is_inside_tree():
		return
	var area := _canvas_rect()
	position.x = clampf(position.x, area.position.x, maxf(area.position.x, area.end.x - size.x))
	position.y = clampf(position.y, area.position.y, maxf(area.position.y, area.end.y - size.y))
