extends Control

## 顶栏（`docs/ui/UI-R0 §2.2 ①`）：`[品牌] │ 文件 编辑 视图 最近 │ … │ 撤销 重做 │ 文件名★ │ 校验 · 保存 导出`
##
## 为什么整栏在代码里搭：`.tscn` 里手写 anchor/offset 正是"布局硬编码"的病根
## （旧 `tool.gd` 里两个 `Rect2` 常量就是这么来的）。这里锚点只有"整条贴住父容器"一种，
## 内部全靠 `HBoxContainer` 排版 —— 1920 与 1280 两档窗口都不用改任何数字。
##
## ★ 本栏只做"入口聚合"：菜单项调用的全是 `EditorApp` 已有方法，**不新增任何能力**。
##   原 `scripts/tool.gd`（文件折叠框 + 视图/最近两个 MenuButton）由本文件取代并已删除。

# --- 菜单项 id（每个菜单各自编号）---
const F_NEW := 0
const F_OPEN := 1
const F_SAVE := 2
const F_SAVE_AS := 3
const F_EXPORT_OTHER := 4

const E_UNDO := 0
const E_REDO := 1
const E_COPY := 2
const E_PASTE := 3
const E_DELETE := 4

const V_LOAD_BASEMAP := 0
const V_BASEMAP_PANEL := 1
const V_FIT_ALL := 2
const V_RESET_LAYOUT := 3

var _file_label: Label
var _check_label: Label
var _btn_undo: Button
var _btn_redo: Button
var _last_file_text := ""
var _last_check_text := ""
var _pill_basic: Button
var _pill_pro: Button


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP      # 顶栏挡住画布，别让点击漏到下面
	offset_top = 0.0                              # 高度从令牌推，.tscn 里的数字只是初值
	offset_bottom = float(UITokens.TOP_BAR_H)
	_build()
	_refresh()
	# ★ 能力级（基础/专业）切换：别的入口（Ctrl+Shift+E）改了状态，这里要跟上
	EditorApp.edition_changed.connect(_on_edition_changed)
	set_process(true)


func _process(_delta: float) -> void:
	_refresh()


# ============================================================
# 搭界面
# ============================================================

func _build() -> void:
	var bg := Clay.flat()
	bg.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	bg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(bg)

	var m := MarginContainer.new()
	m.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	m.add_theme_constant_override("margin_left", UITokens.S4)
	m.add_theme_constant_override("margin_right", UITokens.S4)
	m.add_theme_constant_override("margin_top", UITokens.S2)
	m.add_theme_constant_override("margin_bottom", UITokens.S2)
	add_child(m)

	var h := HBoxContainer.new()
	h.add_theme_constant_override("separation", UITokens.S2)
	m.add_child(h)

	# --- 左：品牌 ---
	var brand := Label.new()
	brand.text = "路网编辑器"
	brand.theme_type_variation = "TitleLabel"
	brand.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	h.add_child(brand)
	var dot := Label.new()
	dot.text = "●"
	dot.add_theme_color_override("font_color", UITokens.ORANGE_500)
	dot.add_theme_font_size_override("font_size", UITokens.T_CAP)
	dot.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	h.add_child(dot)

	h.add_child(_divider())

	# --- 中左：四个单层菜单（自己一个 HBox：菜单之间的间距要和品牌/右侧按钮分开调）
	# ★ 原来四个菜单直接挂在主 HBox 上，用的是 S2=8 的通用间距，"文件 编辑 视图 最近"挤成一片。
	#   单独一个容器 + S4=16，悬停底一出来才不会互相顶到。
	var menus := HBoxContainer.new()
	menus.add_theme_constant_override("separation", UITokens.S4)
	h.add_child(menus)

	var mb_file := _menu("文件", _on_file_menu)
	mb_file.get_popup().about_to_popup.connect(_fill_file_menu.bind(mb_file))
	menus.add_child(mb_file)

	var mb_edit := _menu("编辑", _on_edit_menu)
	mb_edit.get_popup().about_to_popup.connect(_fill_edit_menu.bind(mb_edit))
	menus.add_child(mb_edit)

	var mb_view := _menu("视图", _on_view_menu)
	mb_view.get_popup().about_to_popup.connect(_fill_view_menu.bind(mb_view))
	menus.add_child(mb_view)

	var mb_recent := _menu("最近", _on_recent_menu)
	mb_recent.get_popup().about_to_popup.connect(_fill_recent_menu.bind(mb_recent))
	menus.add_child(mb_recent)

	# --- 撑开 ---
	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	spacer.mouse_filter = Control.MOUSE_FILTER_IGNORE
	h.add_child(spacer)

	# --- 右：能力级（基础 / 专业）双段胶囊 ---
	h.add_child(_edition_capsule())

	h.add_child(_divider())

	# --- 右：撤销 / 重做 ---
	_btn_undo = Clay.icon_btn("↶", 32)
	_btn_undo.tooltip_text = "撤销（Ctrl+Z；最多 100 步）"
	_btn_undo.pressed.connect(func() -> void: EditorApp.undo())
	h.add_child(_btn_undo)

	_btn_redo = Clay.icon_btn("↷", 32)
	_btn_redo.tooltip_text = "重做（Ctrl+Y）"
	_btn_redo.pressed.connect(func() -> void: EditorApp.redo())
	h.add_child(_btn_redo)

	h.add_child(_divider())

	# --- 右：文件名（脏了加 *）---
	_file_label = Label.new()
	_file_label.theme_type_variation = "MutedLabel"
	_file_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_file_label.custom_minimum_size = Vector2(120, 0)
	_file_label.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	h.add_child(_file_label)

	# --- 右：校验状态（可点：打开「校验问题」清单，P4-1）---
	var badge := Clay.inset(UITokens.S2)
	badge.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	badge.mouse_filter = Control.MOUSE_FILTER_STOP
	badge.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	badge.tooltip_text = "看校验问题清单（点一条定位到对象）"
	badge.gui_input.connect(func(ev: InputEvent) -> void:
		if ev is InputEventMouseButton:
			var mb := ev as InputEventMouseButton
			if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
				EditorApp.toggle_validation_drawer()
				badge.accept_event())
	_check_label = Label.new()
	_check_label.theme_type_variation = "HintLabel"
	_check_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	badge.add_child(_check_label)
	h.add_child(badge)

	# --- 右：保存 / 导出 ---
	var save_btn := Clay.btn_primary("保存")
	save_btn.tooltip_text = "保存到当前文件（Ctrl+S；没存过盘则弹另存为）"
	save_btn.pressed.connect(func() -> void: EditorApp.save_current())
	h.add_child(save_btn)

	var export_btn := Clay.btn("导出")
	export_btn.tooltip_text = "导出为其他格式…（Ctrl+E）"
	export_btn.pressed.connect(func() -> void: EditorApp.open_export_dialog())
	h.add_child(export_btn)

	# --- 底边 1px 发丝线：把顶栏和画布分开（不用投影，投影会压住网格第一行）---
	var line := ColorRect.new()
	line.color = UITokens.SURFACE_3
	line.mouse_filter = Control.MOUSE_FILTER_IGNORE
	line.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_WIDE)
	line.offset_top = -1
	line.offset_bottom = 0
	add_child(line)


## 能力级胶囊：「基础 │ 专业」。两段互斥，且**已选中的那段不能被点灭**。
## ★ 只切界面可见性，不动数据（技术方案06 §6.1）—— 所以这里不置脏、不进撤销栈。
func _edition_capsule() -> HBoxContainer:
	var box := HBoxContainer.new()
	box.add_theme_constant_override("separation", 0)
	box.size_flags_vertical = Control.SIZE_SHRINK_CENTER

	_pill_basic = Clay.pill("基础", not EditorApp.is_pro())
	_pill_basic.tooltip_text = "基础模式：画路 / 连接 / 配时 / 需求（Ctrl+Shift+E 切换）"
	_pill_basic.pressed.connect(func() -> void: _pick_edition(Edition.Level.BASIC))
	box.add_child(_pill_basic)

	_pill_pro = Clay.pill("专业", EditorApp.is_pro())
	_pill_pro.tooltip_text = "专业模式：车型 / 逐灯配时 / 检测设施 / 运行配置（Ctrl+Shift+E 切换）"
	_pill_pro.pressed.connect(func() -> void: _pick_edition(Edition.Level.PRO))
	box.add_child(_pill_pro)
	return box


func _pick_edition(level: int) -> void:
	EditorApp.set_edition(level)
	_sync_edition_pills()


## 已选中的那段被点一下会变"未按下" → 立刻按回去，别让两段同时灭
func _sync_edition_pills() -> void:
	if _pill_basic == null or _pill_pro == null:
		return
	_pill_basic.set_pressed_no_signal(not EditorApp.is_pro())
	_pill_pro.set_pressed_no_signal(EditorApp.is_pro())


func _on_edition_changed(_level: int) -> void:
	_sync_edition_pills()


func _divider() -> ColorRect:
	var c := ColorRect.new()
	c.color = UITokens.SURFACE_3
	c.custom_minimum_size = Vector2(1, 22)
	c.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	c.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return c


## 造一个下拉菜单按钮（列表内容在展开前由 `_fill_*` 重建，保证永远是当前状态）
func _menu(text: String, handler: Callable) -> MenuButton:
	var mb := MenuButton.new()
	mb.text = text
	# ★ 不用 flat：flat 会绕开主题里的 normal/hover 样式，悬停反馈就只剩引擎默认那层（白底）。
	#   本主题里 normal 本来就是空的，所以 flat=false 看起来和 flat 一样，但悬停/按下走我们的样式。
	mb.flat = false
	mb.focus_mode = Control.FOCUS_NONE
	mb.get_popup().id_pressed.connect(handler)
	return mb


# ============================================================
# 菜单内容（全部指向 EditorApp 已有方法）
# ============================================================

## 加一条菜单项，并把加速键绑到**这一条自己的索引**上。
## ★ 2026-10-29 修：原来用写死的下标（`set_item_accelerator(2, …)`），而 `add_separator()`
##   同样占一个索引 —— 文件菜单从「保存」起整列错位一行：保存显示 Ctrl+Shift+S、
##   另存为显示 Ctrl+E、导出什么都不显示，真正的 Ctrl+S 落到那条分隔线上（看不见）。
##   一律改成用 `add_item()` 的返回值，插分隔线也不会再错位。
func _add(pop: PopupMenu, text: String, id: int, accel: int = KEY_NONE) -> int:
	pop.add_item(text, id)
	var idx := pop.item_count - 1      # ★ Godot 4 的 add_item() 返回 void，只能自己数
	if accel != KEY_NONE:
		pop.set_item_accelerator(idx, accel)
	return idx


func _fill_file_menu(mb: MenuButton) -> void:
	var pop := mb.get_popup()
	pop.clear()
	_add(pop, "新建", F_NEW, KEY_MASK_CTRL | KEY_N)
	_add(pop, "打开…", F_OPEN, KEY_MASK_CTRL | KEY_O)
	pop.add_separator()
	_add(pop, "保存", F_SAVE, KEY_MASK_CTRL | KEY_S)
	_add(pop, "另存为…", F_SAVE_AS, KEY_MASK_CTRL | KEY_MASK_SHIFT | KEY_S)
	_add(pop, "导出为其他格式…", F_EXPORT_OTHER, KEY_MASK_CTRL | KEY_E)


func _fill_edit_menu(mb: MenuButton) -> void:
	var pop := mb.get_popup()
	pop.clear()
	_add(pop, "撤销", E_UNDO, KEY_MASK_CTRL | KEY_Z)
	_add(pop, "重做", E_REDO, KEY_MASK_CTRL | KEY_Y)
	if EditorApp.undo_depth() == 0:
		pop.set_item_disabled(0, true)
	if EditorApp.redo_depth() == 0:
		pop.set_item_disabled(1, true)
	pop.add_separator()
	_add(pop, "复制", E_COPY, KEY_MASK_CTRL | KEY_C)
	_add(pop, "粘贴", E_PASTE, KEY_MASK_CTRL | KEY_V)
	pop.add_separator()
	_add(pop, "删除选中", E_DELETE, KEY_DELETE)


func _fill_view_menu(mb: MenuButton) -> void:
	var pop := mb.get_popup()
	pop.clear()
	_add(pop, "载入底图…", V_LOAD_BASEMAP)
	_add(pop, "底图面板", V_BASEMAP_PANEL)
	pop.add_separator()
	_add(pop, "缩放到全部内容", V_FIT_ALL, KEY_HOME)
	pop.add_separator()
	_add(pop, "重置面板布局", V_RESET_LAYOUT)


## 最近文件：每次展开都重建（保存/打开都会改它，缓存必然对不上）
func _fill_recent_menu(mb: MenuButton) -> void:
	var pop := mb.get_popup()
	pop.clear()
	var list: Array = EditorApp.recent_files
	if list.is_empty():
		pop.add_item("（还没有最近文件）", 0)
		pop.set_item_disabled(0, true)
		return
	for i in list.size():
		var path := String(list[i])
		pop.add_item("%d. %s" % [i + 1, path.get_file()], i)
		pop.set_item_tooltip(i, path)
		if not FileAccess.file_exists(path):
			pop.set_item_disabled(i, true)        # 文件没了 → 灰掉，别让人点了报错


# ============================================================
# 菜单回调
# ============================================================

func _on_file_menu(id: int) -> void:
	match id:
		F_NEW: EditorApp.new_document()
		F_OPEN: EditorApp.ask_open()
		F_SAVE: EditorApp.save_current()
		F_SAVE_AS: EditorApp.ask_save_as()
		F_EXPORT_OTHER: EditorApp.open_export_dialog()


func _on_edit_menu(id: int) -> void:
	match id:
		E_UNDO: EditorApp.undo()
		E_REDO: EditorApp.redo()
		E_COPY: EditorApp.copy_selection()
		E_PASTE: EditorApp.paste_clipboard()
		E_DELETE: EditorApp.delete_selection()


func _on_view_menu(id: int) -> void:
	match id:
		V_LOAD_BASEMAP:
			var bm := _basemap()
			if bm != null:
				bm.ask_load()
		V_BASEMAP_PANEL:
			var bm2 := _basemap()
			if bm2 != null:
				bm2.toggle_panel()
		V_FIT_ALL: EditorApp.fit_all()
		V_RESET_LAYOUT: _reset_layout()


func _on_recent_menu(id: int) -> void:
	EditorApp.open_recent(id)


## 重置右侧坞布局（坞宽 / 三段比例 / 折叠状态）。坞自己挂在 `editor_dock` 组里
## ★ 走组而不是写死节点路径：坞以后可能被挪到 P5 的其它容器里，路径会变，组不会。
func _reset_layout() -> void:
	var docks := get_tree().get_nodes_in_group("editor_dock")
	if docks.is_empty():
		return
	docks[0].reset_layout()


## 底图层（GridCanvas 子节点；挂成兄弟节点也认）—— 与原 tool.gd 同口径
func _basemap() -> Node:
	var sc: Node = get_tree().current_scene
	if sc == null:
		return null
	var n: Node = sc.get_node_or_null("GridCanvas/BasemapLayer")
	if n == null:
		n = sc.get_node_or_null("BasemapLayer")
	if n == null:
		push_error("找不到底图层：Main.tscn 里应有 GridCanvas/BasemapLayer")
	return n


# ============================================================
# 每帧刷新（只在文本变时写 Label —— 赋值会触发重排）
# ============================================================

func _refresh() -> void:
	if _btn_undo != null:
		_btn_undo.disabled = EditorApp.undo_depth() == 0
	if _btn_redo != null:
		_btn_redo.disabled = EditorApp.redo_depth() == 0

	var path := String(EditorApp.current_path)
	var name_text := "（未保存）"
	if path != "":
		name_text = path.get_file()
	var dirty: bool = false
	if EditorApp.doc != null:
		dirty = bool(EditorApp.doc.dirty)
	if dirty:
		name_text += " *"
	if name_text != _last_file_text:
		_last_file_text = name_text
		if _file_label != null:
			_file_label.text = name_text
			_file_label.tooltip_text = path if path != "" else "还没存过盘（Ctrl+S 另存为）"

	var errs: int = EditorApp.validation_errors
	var check_text := "未校验"
	if errs == 0:
		check_text = "校验通过"
	elif errs > 0:
		check_text = "%d 处错误" % errs
	if check_text != _last_check_text:
		_last_check_text = check_text
		if _check_label != null:
			_check_label.text = check_text
			_check_label.add_theme_color_override("font_color",
				UITokens.INK_3 if errs <= 0 else UITokens.CONFLICT)
