extends Control

## 左侧工具条（`docs/ui/UI-R0 §2.2 ②`）：**64 px 竖排**，4 个模式 + 分隔 + 3 个工具。
##
## ★ 全部按钮都是"已有动作的入口"，不新增能力：
##   模式 → `EditorApp.set_mode`；测量 → `GridCanvas.start_measure / cancel_measure`（与 M / Esc 同一函数）；
##   吸附 → `GridCanvas.toggle_snap`（与 X 键同一函数）。
##
## ★ 2026-10-27 按老板要求去掉「生（生成连接）」按钮：**生成连接的能力没删**，
##   仍走连接模式的 `G` 键（`ConnectMode` 里接的就是 `EditorApp.generate_connections`），F1 键位表里也照旧。
##
## ★ 模式按钮的状态**只由 `EditorApp.mode` 决定**（`mode_changed` 时同步），
##   所以按 1~4 键切模式时，左栏高亮会跟着走 —— 不会出现"按钮亮着但模式已经变了"。

const BOX := UITokens.ICON_BTN   ## 按钮边长（栏宽 - 两侧各 8，随字号一起长）

var _mode_btns: Dictionary = {}       ## mode(int) → Button
var _glider: Panel = null             ## 模式高亮的橙底滑块（P4-2）：位置跟着当前模式走
var _glider_ready := false            ## 第一帧布局还没算出来，先别滑（直接落位）
var _group := ButtonGroup.new()
var _btn_snap: Button
var _btn_measure: Button
var _grid: Node = null


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP
	offset_top = float(UITokens.TOP_BAR_H)
	offset_bottom = -float(UITokens.STATUS_BAR_H)
	offset_right = float(UITokens.TOOL_RAIL_W)
	# 先按相对路径找（本节点永远在 Main/CanvasLayer 下），找不到再退回 current_scene ——
	# 临时自测场景把 Main 当子节点实例化时，current_scene 不是 Main，只靠它会拿到 null。
	_grid = get_node_or_null("../../GridCanvas")
	if _grid == null:
		var sc: Node = get_tree().current_scene
		if sc != null:
			_grid = sc.get_node_or_null("GridCanvas")
	_build()
	EditorApp.mode_changed.connect(_on_mode_changed)
	_sync_mode(EditorApp.mode)
	resized.connect(func() -> void: _move_glider(EditorApp.mode, false))
	call_deferred("_move_glider", EditorApp.mode, false)   # 首帧布局算完再落位（_ready 里还拿不到按钮坐标）
	set_process(true)


func _process(_delta: float) -> void:
	# 吸附 / 测量都是会话级状态，X / M / Esc 键也能改 → 每帧对一次（只在变时写）
	if _btn_snap != null and _grid != null:
		var on: bool = bool(_grid.snap_enabled)
		if _btn_snap.button_pressed != on:
			_btn_snap.set_pressed_no_signal(on)
	if _btn_measure != null and _grid != null:
		var m: bool = bool(_grid.measuring)
		if _btn_measure.button_pressed != m:
			_btn_measure.set_pressed_no_signal(m)


func _build() -> void:
	var bg := Clay.flat()
	bg.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	bg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(bg)

	# ★ 橙底滑块必须在按钮**下面**：先加（更早绘制），再挂 margin 与按钮
	_glider = Panel.new()
	_glider.name = "ModeGlider"
	_glider.theme_type_variation = "ModeGlider"
	_glider.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_glider.size = Vector2(BOX, BOX)
	_glider.visible = false
	add_child(_glider)

	var m := MarginContainer.new()
	m.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	m.add_theme_constant_override("margin_left", UITokens.S2)
	m.add_theme_constant_override("margin_right", UITokens.S2)
	m.add_theme_constant_override("margin_top", UITokens.S3)
	m.add_theme_constant_override("margin_bottom", UITokens.S3)
	add_child(m)

	var v := VBoxContainer.new()
	v.add_theme_constant_override("separation", UITokens.S2)
	m.add_child(v)

	# --- 4 个模式（互斥）---
	_add_mode(v, EditorMode.Kind.ROAD, "道", "道路模式（1）：画路 / 改形 / 拆分")
	_add_mode(v, EditorMode.Kind.CONNECT, "连", "连接模式（2）：转向连接 / 信号")
	_add_mode(v, EditorMode.Kind.TRAFFIC, "车", "车辆模式（3）：车道与过街")
	_add_mode(v, EditorMode.Kind.DECO, "饰", "装饰模式（4）：铺贴图")

	v.add_child(_sep())

	# --- 2 个工具（"生"已按老板要求去掉：生成连接仍在连接模式的 G 键上）---
	_btn_measure = Clay.icon_btn("测", BOX)
	_btn_measure.toggle_mode = true                 # 亮着 = 正在测量（Esc 或再点一次退出）
	_btn_measure.tooltip_text = "测量（M）：左键点两点读距离与方位角；再点一次或 Esc 退出"
	_btn_measure.pressed.connect(_on_measure)
	if _grid != null:
		_btn_measure.set_pressed_no_signal(bool(_grid.measuring))
	v.add_child(_btn_measure)

	_btn_snap = Clay.icon_btn("吸", BOX)
	_btn_snap.toggle_mode = true
	_btn_snap.tooltip_text = "格点吸附 开/关（X）"
	_btn_snap.pressed.connect(_on_snap)
	v.add_child(_btn_snap)

	# --- 底部发丝线：与画布分隔 ---
	var line := ColorRect.new()
	line.color = UITokens.SURFACE_3
	line.mouse_filter = Control.MOUSE_FILTER_IGNORE
	line.set_anchors_and_offsets_preset(Control.PRESET_RIGHT_WIDE)
	line.offset_left = -1
	line.offset_right = 0
	add_child(line)


func _add_mode(parent: Node, kind: int, glyph: String, tip: String) -> void:
	var b := Clay.icon_btn(glyph, BOX)
	b.theme_type_variation = "ModeButton"     # 按下态透明，橙底交给滑块
	b.toggle_mode = true
	b.button_group = _group
	b.tooltip_text = tip
	b.pressed.connect(func() -> void: EditorApp.set_mode(kind))
	parent.add_child(b)
	_mode_btns[kind] = b


func _sep() -> Control:
	var c := ColorRect.new()
	c.color = UITokens.SURFACE_3
	c.custom_minimum_size = Vector2(0, 1)
	c.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return c


func _on_mode_changed(kind: int) -> void:
	_sync_mode(kind)


func _sync_mode(kind: int) -> void:
	for k in _mode_btns:
		var b: Button = _mode_btns[k]
		b.set_pressed_no_signal(int(k) == kind)
	_move_glider(kind, _glider_ready)


## 把橙底滑块挪到某个模式按钮上（首帧直接落位，之后 220ms 滑过去）
func _move_glider(kind: int, animate: bool) -> void:
	if _glider == null or not _mode_btns.has(kind):
		return
	var b: Button = _mode_btns[kind]
	if not b.is_inside_tree():
		return
	var target: Vector2 = b.get_global_rect().position - get_global_rect().position
	_glider.visible = true
	if not animate:
		_glider.position = target
		_glider_ready = true
		return
	Motion.slide_to(_glider, target)


## 测量按钮：亮着时再点 = 退出（与 Esc 同一个函数），灭着时点 = 开始/重开
func _on_measure() -> void:
	if _grid == null:
		return
	if bool(_grid.measuring):
		_grid.cancel_measure()
	else:
		_grid.start_measure()


func _on_snap() -> void:
	if _grid != null:
		_grid.toggle_snap()
