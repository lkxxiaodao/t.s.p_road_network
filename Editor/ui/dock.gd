class_name PlannerDock
extends Control

## 右侧面板坞（`docs/ui/UI-R0 §2.2 ④` / §2.3 / §2.4）：**两段**纵向堆叠 = 属性 / 大纲。
##
## ★ 2026-10-27：装饰素材**搬出坞了**（老板要求它改画布左侧的浮窗，可拖）。
##   那段面板现在挂在 `CanvasLayer/DecoFloat`（`ui/float_window.gd`）里，坞不再管它 ——
##   所以这里的段数、比例、折叠数组都是 **2**（旧布局文件里若还是 3 个值，`LayoutStore` 会判越界并回默认）。
##
## 本批（P2-2）做到：
##   · 坞宽可拖（左边缘 6 px 抓手，`DOCK_W_MIN`~`DOCK_W_MAX` = 380~560，R0 §2.2）
##   · 每段可折叠（点段头），折叠后只留段头那么高
##   · 段与段之间可拖高度（6 px 横抓手）
##   · 全部写入 `user://ui_layout.json`（`LayoutStore`），关掉重开还在；视图菜单可重置
##
## ★ 类名用 PlannerDock：Godot 4.7 自带一个原生类叫 EditorDock，class_name 重名会直接 Parse Error。
##
## ★ **不搬数据、不碰 Doc**：两段里的面板还是原来那两个脚本（`property_panel` / `outline_panel`），
##   本文件只负责"把它们塞进哪一格、给多大地方"。
##
## ★ 为什么不做真正的 `HSplitContainer`：画布 `GridCanvas` 是 `Node2D` + `Camera2D`，
##   视口始终是整个窗口 —— 塞进 SplitContainer 也不会让相机看到"更窄的视口"，只会白费一次重排。
##   所以坞是**浮在画布之上**、拖左边缘改宽度，效果等价且零风险（真要做真分栏得先上 SubViewport）。
##
## ★ 拖拽的 Motion 事件**必须在 `_input()` 里收**：`gui_input` 只发给鼠标当前所在的控件，
##   手一抖出了那 6 px 的抓手，拖动就断了（第一版就这么断的）。

const HANDLE_W := 6            ## 抓手厚（左右/上下同厚）
const HEAD_H := UITokens.DOCK_HEAD_H   ## 段头高（随字号走）
const MIN_SECTION_H := 120     ## 展开状态下每段最小高度（字号放大后 96 只够放标题）
const SAVE_DELAY := 0.6        ## 停手多久后落盘（拖的过程中别一直写盘）

var _dock_w: float = float(UITokens.DOCK_W_DEFAULT)
var _ratios: Array = [0.62, 0.38]
var _folded: Array = [false, false]

var _sections: Array = []      ## [{root, head, body, title, panel}]
var _vbox: VBoxContainer
var _timer: Timer
var _drag_width := false
var _drag_h := -1
var _panel_nodes: Array = []


func _ready() -> void:
	add_to_group("editor_dock")
	mouse_filter = Control.MOUSE_FILTER_STOP
	# 上下边界从令牌推：顶栏之下、状态栏之上（写死 64/-32 字号一变就被栏挡住）
	offset_top = float(UITokens.TOP_BAR_H)
	offset_bottom = -float(UITokens.STATUS_BAR_H)

	var lay := LayoutStore.load_layout()
	_dock_w = float(lay["dock_w"])
	_ratios = (lay["ratios"] as Array).duplicate()
	_folded = (lay["folded"] as Array).duplicate()

	# 场景里挂着的三个面板 —— 本节点 _ready 一定晚于它们的 _ready，此时它们已经把自己搭好了
	for nm in ["PropertyPanel", "OutlinePanel"]:
		var n: Node = get_node_or_null(nm)
		if n != null:
			_panel_nodes.append(n)

	_timer = Timer.new()
	_timer.one_shot = true
	_timer.wait_time = SAVE_DELAY
	_timer.timeout.connect(_flush_save)
	add_child(_timer)

	_build()
	_adopt_panels()
	_apply()


func _exit_tree() -> void:
	LayoutStore.save_layout(_snapshot())      # 关窗口时立刻存一次，别等防抖


# ============================================================
# 搭骨架
# ============================================================

func _build() -> void:
	var bg := PanelContainer.new()
	bg.name = "Backdrop"
	bg.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	bg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(bg)

	var m := MarginContainer.new()
	m.name = "Margins"
	m.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	m.add_theme_constant_override("margin_left", UITokens.S2)
	m.add_theme_constant_override("margin_right", UITokens.S2)
	m.add_theme_constant_override("margin_top", UITokens.S2)
	m.add_theme_constant_override("margin_bottom", UITokens.S2)
	m.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(m)

	_vbox = VBoxContainer.new()
	_vbox.name = "Stack"
	_vbox.add_theme_constant_override("separation", 0)
	m.add_child(_vbox)

	var titles := ["属性", "大纲"]
	for i in titles.size():
		if i > 0:
			_vbox.add_child(_make_vhandle(i))
		_vbox.add_child(_make_section(i, String(titles[i])))

	# 左边缘：拖宽度的抓手
	var wh := Control.new()
	wh.name = "WidthHandle"
	wh.mouse_default_cursor_shape = Control.CURSOR_HSIZE
	wh.set_anchors_and_offsets_preset(Control.PRESET_LEFT_WIDE)
	wh.offset_right = HANDLE_W
	wh.mouse_filter = Control.MOUSE_FILTER_STOP
	wh.gui_input.connect(_on_width_input)
	add_child(wh)


func _make_section(idx: int, title: String) -> Control:
	var root := VBoxContainer.new()
	root.name = "Section%d" % idx
	root.add_theme_constant_override("separation", 0)

	var head := Button.new()
	head.name = "Head"
	head.theme_type_variation = "GhostButton"
	head.alignment = HORIZONTAL_ALIGNMENT_LEFT
	head.focus_mode = Control.FOCUS_NONE
	head.custom_minimum_size = Vector2(0, HEAD_H)
	head.pressed.connect(_on_head_pressed.bind(idx))
	root.add_child(head)

	var body := ScrollContainer.new()
	body.name = "Body"
	body.size_flags_vertical = Control.SIZE_EXPAND_FILL
	body.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	root.add_child(body)

	_sections.append({"root": root, "head": head, "body": body, "title": title, "panel": null})
	return root


## 段与段之间的横向抓手（改上下两段的高度）
## ★ `upper` 是它**下面**那一段的序号（HHandle1 夹在 0/1 之间、HHandle2 夹在 1/2 之间）。
##   绑定时要传 `upper - 1`：`_drag_section(idx)` 的 idx 是**上面**那一段，
##   它同时改 `ratios[idx]` 和 `ratios[idx + 1]`。以前直接把 `upper` 绑进去，
##   拖最后一根抓手就变成 `ratios[2] / ratios[3]` → 越界报错（2026-10-27 老板实测）。
func _make_vhandle(upper: int) -> Control:
	var h := Control.new()
	h.name = "HHandle%d" % upper
	h.custom_minimum_size = Vector2(0, HANDLE_W)
	h.mouse_default_cursor_shape = Control.CURSOR_VSIZE
	h.mouse_filter = Control.MOUSE_FILTER_STOP
	h.gui_input.connect(_on_height_input.bind(upper - 1))
	return h


## 把场景里的三个面板放进各自段体（先脱锚，交给容器排版）
func _adopt_panels() -> void:
	for i in mini(_panel_nodes.size(), _sections.size()):
		var panel: Node = _panel_nodes[i]
		var body: Control = _sections[i]["body"]
		if panel.get_parent() != null:
			panel.reparent(body, false)
		else:
			body.add_child(panel)
		if panel is Control:
			var c := panel as Control
			c.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
			c.custom_minimum_size = Vector2.ZERO
			c.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			c.size_flags_vertical = Control.SIZE_EXPAND_FILL
			# 面板自带一层卡片底 → 段本身已经是卡片，里面再套一层会显脏
			c.add_theme_stylebox_override("panel", StyleBoxEmpty.new())
		_sections[i]["panel"] = panel


# ============================================================
# 应用布局状态
# ============================================================

func _apply() -> void:
	offset_left = -_dock_w
	offset_right = 0.0
	for i in _sections.size():
		var s: Dictionary = _sections[i]
		var folded: bool = bool(_folded[i])
		var head: Button = s["head"]
		var body: Control = s["body"]
		var root: Control = s["root"]
		head.text = ("▸  " if folded else "▾  ") + String(s["title"])
		body.visible = not folded
		if folded:
			root.size_flags_vertical = Control.SIZE_SHRINK_BEGIN
			root.custom_minimum_size = Vector2(0, HEAD_H)
		else:
			root.size_flags_vertical = Control.SIZE_EXPAND_FILL
			root.size_flags_stretch_ratio = maxf(float(_ratios[i]), 0.05)
			root.custom_minimum_size = Vector2(0, MIN_SECTION_H)
	# 抓手只在两侧都展开**且都实际可见**时才显示
	# （装饰素材只在装饰模式出现；它藏起来时那根抓手也该藏，否则拖了没反应）
	for i in _sections.size() - 1:
		var h: Control = _vbox.get_node_or_null("HHandle%d" % (i + 1))
		if h != null:
			var up_vis: bool = not bool(_folded[i]) and (_sections[i]["root"] as Control).visible
			var dn_vis: bool = not bool(_folded[i + 1]) and (_sections[i + 1]["root"] as Control).visible
			h.visible = up_vis and dn_vis


func _on_head_pressed(idx: int) -> void:
	_folded[idx] = not bool(_folded[idx])
	_apply()
	_save_later()


# ============================================================
# 拖拽
# ============================================================

func _on_width_input(ev: InputEvent) -> void:
	if ev is InputEventMouseButton and (ev as InputEventMouseButton).button_index == MOUSE_BUTTON_LEFT:
		_drag_width = (ev as InputEventMouseButton).pressed


func _on_height_input(ev: InputEvent, idx: int) -> void:
	if ev is InputEventMouseButton and (ev as InputEventMouseButton).button_index == MOUSE_BUTTON_LEFT:
		var mb := ev as InputEventMouseButton
		_drag_h = idx if mb.pressed else -1


func _input(ev: InputEvent) -> void:
	if not (ev is InputEventMouseMotion):
		return
	var mm := ev as InputEventMouseMotion
	if mm.button_mask & MOUSE_BUTTON_MASK_LEFT == 0:
		_drag_width = false
		_drag_h = -1
		return
	if _drag_width:
		_dock_w = clampf(_dock_w - mm.relative.x,
			float(UITokens.DOCK_W_MIN), float(UITokens.DOCK_W_MAX))
		offset_left = -_dock_w
		_save_later()
	elif _drag_h >= 0:
		_drag_section(_drag_h, mm.relative.y)


func _drag_section(idx: int, dy: float) -> void:
	# ★ 兜底：idx 必须还有"下一段"可换份量。少了这句，绑错索引就是运行时崩（越界）。
	if idx < 0 or idx + 1 >= _sections.size():
		_drag_h = -1
		return
	var avail: float = maxf(_vbox.size.y - HANDLE_W * 2.0, 1.0)
	var total := 0.0
	for i in _sections.size():
		if not bool(_folded[i]):
			total += maxf(float(_ratios[i]), 0.05)
	if total <= 0.0:
		return
	var d := dy / avail * total
	var lo := 0.06 * total                  # 每段至少留 6% 的份额
	_ratios[idx] = maxf(float(_ratios[idx]) + d, lo)
	_ratios[idx + 1] = maxf(float(_ratios[idx + 1]) - d, lo)
	_apply()
	_save_later()


# ============================================================
# 持久化
# ============================================================

func _snapshot() -> Dictionary:
	return {
		"version": LayoutStore.VERSION,
		"dock_w": _dock_w,
		"ratios": (_ratios as Array).duplicate(),
		"folded": (_folded as Array).duplicate(),
	}


func _save_later() -> void:
	if _timer == null:
		return
	_timer.start()


func _flush_save() -> void:
	LayoutStore.save_layout(_snapshot())


## 视图 → 重置布局（顶栏菜单调用）
func reset_layout() -> void:
	var lay := LayoutStore.reset()
	_dock_w = float(lay["dock_w"])
	_ratios = (lay["ratios"] as Array).duplicate()
	_folded = (lay["folded"] as Array).duplicate()
	_apply()
