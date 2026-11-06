extends Control

## F1 键位表 + 图层开关条。
##
## ★ 两件东西住在一个文件里：`Main.tscn` 的 `CanvasLayer/HelpPanel` 同时承载
##   **常驻的图层开关条**（左下角一条）+ **按 F1 弹出的键位表**（居中浮层）。
##   两者都在代码里搭，场景里只有这个空 Control。
##
## ★ 全是会话级状态：不写 .spmap / .spdeco，也不写 user://（关掉编辑器就回默认全开）。
##   开关的真值只存在 EditorApp 的 show_* 六个数上（冲突层 2026-10-28 撤掉），本文件只负责"勾选框 ↔ 那六个数"。
##
## ★ 键位表口径：**逐条照抄代码，不照抄文档**。来源已核对：
##   `grid_canvas.gd` 的 `MODE_KEYS` / `_handle_global_key` / `_dispatch`（Delete、右键框选、测量）、
##   `editor_camera.gd`（中键 / 滚轮）、`road_mode.gd` / `connect_mode.gd` / `deco_mode.gd` /
##   `traffic_mode.gd` 的 `handle()`。代码改动后本表必须跟着改，否则就是骗人。
##   `docs/kb/editor/编辑器-操作手册.md` §8 与本表同步维护（改一处要改两处）。

## 图层条：键名与 EditorApp.set_layer / layer_enabled 的键一致
const LAYERS: Array = [
	["grid", "网格"],
	["lanes", "车道"],
	["turns", "连接"],
	["decos", "装饰"],
	["annotations", "标注"],
	["basemap", "底图"],
]

## 键位表：[键位, 说明]；说明为空 = 分组标题行
## ★ 键位表（单一真值：F1 面板与底部提示条都读这张表）。
##   2026-11-03 全面精简：**一条只写一个动词**，不写括号解释、不写"为什么"。
##   理由：这张表是"扫一眼想起来怎么操作"，不是教程；解释放 `操作手册` 与 `docs/教学`。
const KEYS: Array = [
	["视图 / 全局", ""],
	["鼠标中键拖", "平移"],
	["滚轮上 / 下", "缩放"],
	["Home", "看全部"],
	["F1", "键位表"],
	["模式切换", ""],
	["1 / 2 / 3 / 4", "道路 / 连接 / 车辆 / 装饰"],
	["文件与撤销", ""],
	["Ctrl+Z", "撤销"],
	["Ctrl+Y", "重做"],
	["Ctrl+C", "复制"],
	["Ctrl+V", "粘贴"],
	["Ctrl+S", "保存"],
	["Ctrl+Shift+S", "另存为"],
	["Ctrl+N", "新建"],
	["Ctrl+O", "打开"],
	["各模式通用", ""],
	["Delete / Backspace", "删除选中"],
	["右键拖", "框选路"],
	["M", "测量"],
	["X", "格点吸附"],
	["Esc", "取消 / 退出"],
	["道路模式（1）", ""],
	["左键拖", "画路"],
	["左键单击", "选中"],
	["拖端点把手", "改端点"],
	["拖弦中点手柄", "弯成弧"],
	["双击节点", "普通 ↔ 信号"],
	["双击路上的点", "插节点"],
	["拖拽时按住 Shift", "锁 0° / 45° / 90°"],
	["连接模式（2）", ""],
	["点路口节点", "选中路口"],
	["先点进口、再点出口", "手动画连接"],
	["点转向曲线", "选中连接"],
	["点另一个节点", "换路口"],
	["点路口里的空白", "只清连接"],
	["G", "自动生成连接"],
	["Esc", "取消手动画连接"],
	["车辆模式（3）", ""],
	["点车道", "配车流"],
	["点过街连接", "配行人"],
	["装饰模式（4）", ""],
	["点空白", "放素材"],
	["点实例", "选中"],
	["拖动时按住 Shift", "吸附格点"],
	["R / Shift+R", "旋转 ±15°"],
	["+ / = / 小键盘 +", "放大"],
	["- / 小键盘 -", "缩小"],
	["PageUp / ]", "上一层"],
	["PageDown / [", "下一层"],
	["Esc", "取消素材"],
	["Delete", "删除装饰"],
]

## 键位表分栏（P4-2）：左列放"全局类"，右列放"四种模式"。**分组名必须与 `KEYS` 里的组标题逐字一致**
const GROUPS_LEFT := ["视图 / 全局", "模式切换", "文件与撤销", "各模式通用", "车辆模式（3）"]
const GROUPS_RIGHT := ["道路模式（1）", "连接模式（2）", "装饰模式（4）"]

var _bar: PanelContainer = null      ## 图层开关条（常驻）
var _keymap: PanelContainer = null   ## 键位表（默认隐藏，F1 开）
var _boxes: Dictionary = {}          ## 图层键 → 胶囊开关（Button，P2-1 从 CheckBox 换成 Clay.pill）
var _search: LineEdit = null         ## 键位表搜索框（P4-2）
var _key_rows: Array = []            ## [{node, key, desc}] —— 搜索过滤用
var _key_groups: Array = []          ## [{node, rows}] —— 整组都空了就藏整张卡
var _empty_hint: Label = null        ## "没有匹配"的提示
var _count_label: Label = null       ## 脚注：共 N 条 / 匹配 M / N

func _ready() -> void:
	EditorApp.help_panel = self      # 自报家门：F1 在 grid_canvas 里，不必按节点名去找
	mouse_filter = Control.MOUSE_FILTER_IGNORE   # 本节点铺满全屏，但自己不吃点击（子面板才吃）
	_build_layer_bar()
	_build_keymap()

# --- 图层开关条 ---------------------------------------------------------

## 图层开关条（R0 §2.2 ⑤）：**贴工具条右侧竖排**，宽 `UITokens.LAYER_RAIL_W`（58），6 个胶囊。
## ★ 2026-10-26 P2-1 改：原来是"底部横排 7 个 CheckBox"（硬编码 430~1060、-44~-8），
##   新状态栏（42）与键位提示条（32）也落在底部，三条必然打架 —— 所以挪到左栏。
## ★ 控件是 `Clay.pill`（胶囊开关，R0 §3.6 的 Pill），`button_pressed` 直接对 `EditorApp.layer_enabled()`。
func _build_layer_bar() -> void:
	var p := PanelContainer.new()
	p.name = "LayerBar"
	p.anchor_left = 0.0
	p.anchor_top = 0.0
	p.anchor_right = 0.0
	p.anchor_bottom = 0.0
	p.offset_left = UITokens.TOOL_RAIL_W + UITokens.S2
	p.offset_top = UITokens.TOP_BAR_H + UITokens.S3
	var vb := VBoxContainer.new()
	vb.add_theme_constant_override("separation", UITokens.S1)
	p.add_child(vb)
	for pair in LAYERS:
		var key := String(pair[0])
		var b := Clay.pill(String(pair[1]), EditorApp.layer_enabled(key))
		b.custom_minimum_size = Vector2(UITokens.LAYER_RAIL_W, 32)
		b.add_theme_font_size_override("font_size", UITokens.T_CAP)
		b.focus_mode = Control.FOCUS_NONE          # 别抢键盘焦点：画图中还要按 1~4 / R / X
		b.toggled.connect(_on_layer_pill.bind(key))
		vb.add_child(b)
		_boxes[key] = b
	add_child(p)
	p.add_to_group("layer_rail")                    # 上下文条靠它算自己的左边界（别让开图层列）
	p.reset_size()                                  # 锚点全 0 → 让面板按内容收缩成"窄条"
	_bar = p
	EditorApp.apply_layer_visibility()              # 启动时把 DecoLayer.visible 对齐一遍


func _on_layer_pill(on: bool, key: String) -> void:
	_on_layer_toggled(on, key)

func _on_layer_toggled(pressed: bool, key: String) -> void:
	EditorApp.set_layer(key, pressed)

# --- 键位表（F1）--------------------------------------------------------

func _build_keymap() -> void:
	var p := PanelContainer.new()
	p.name = "Keymap"
	p.visible = false

	var m := MarginContainer.new()
	m.add_theme_constant_override("margin_left", UITokens.S4)
	m.add_theme_constant_override("margin_top", UITokens.S4)
	m.add_theme_constant_override("margin_right", UITokens.S4)
	m.add_theme_constant_override("margin_bottom", UITokens.S4)
	p.add_child(m)

	var v := VBoxContainer.new()
	v.add_theme_constant_override("separation", UITokens.S3)
	m.add_child(v)

	# --- 头：标题 + 副标 + 搜索 + 关闭 ---
	var head := HBoxContainer.new()
	head.add_theme_constant_override("separation", UITokens.S2)
	v.add_child(head)
	var title := Label.new()
	title.text = "快捷键"
	title.theme_type_variation = "TitleLabel"
	head.add_child(title)
	var sub := Label.new()
	sub.text = "照抄代码，不是文档"
	sub.theme_type_variation = "HintLabel"
	sub.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	head.add_child(sub)
	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	head.add_child(spacer)
	_search = LineEdit.new()
	_search.placeholder_text = "搜键位或说明（Ctrl / 撤销 / 路口…）"
	_search.custom_minimum_size = Vector2(250, 0)
	_search.clear_button_enabled = true
	_search.text_changed.connect(_filter_keys)
	head.add_child(_search)
	var close := Clay.btn_ghost("✕")
	close.name = "Close"
	close.focus_mode = Control.FOCUS_NONE
	close.tooltip_text = "关掉（F1）"
	close.pressed.connect(toggle_visible)
	head.add_child(close)

	# --- 两列卡片：左 = 全局类，右 = 四种模式 ---
	var cols := HBoxContainer.new()
	cols.add_theme_constant_override("separation", UITokens.S4)
	cols.size_flags_vertical = Control.SIZE_EXPAND_FILL
	v.add_child(cols)
	_fill_column(_key_column(cols), GROUPS_LEFT)
	_fill_column(_key_column(cols), GROUPS_RIGHT)

	# --- 脚：空结果提示 + 操作提示 ---
	var foot := HBoxContainer.new()
	foot.add_theme_constant_override("separation", UITokens.S2)
	v.add_child(foot)
	_empty_hint = Label.new()
	_empty_hint.theme_type_variation = "HintLabel"
	_empty_hint.visible = false
	foot.add_child(_empty_hint)
	_count_label = Label.new()
	_count_label.theme_type_variation = "HintLabel"
	foot.add_child(_count_label)
	var fspacer := Control.new()
	fspacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	foot.add_child(fspacer)
	var tip := Label.new()
	tip.text = "F1 关闭 · 打字时按键不抢"
	tip.theme_type_variation = "HintLabel"
	foot.add_child(tip)

	add_child(p)
	_keymap = p
	_layout_keymap()
	get_viewport().size_changed.connect(_layout_keymap)


## 一列 = 滚动容器 + VBox（两个模式分栏都靠它）
func _key_column(parent: Node) -> VBoxContainer:
	var sc := ScrollContainer.new()
	sc.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	sc.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	sc.size_flags_vertical = Control.SIZE_EXPAND_FILL
	parent.add_child(sc)
	var vb := VBoxContainer.new()
	vb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vb.add_theme_constant_override("separation", UITokens.S3)
	sc.add_child(vb)
	return vb


## 把 `KEYS` 里的分组（说明为空的行 = 组标题）按 GROUPS_LEFT/RIGHT 分到两列
func _fill_column(col: VBoxContainer, groups: Array) -> void:
	var cursor := 0
	while cursor < KEYS.size():
		var row: Array = KEYS[cursor]
		if String(row[1]) != "":                 # 不是组标题 → 跳过（组标题之后的行走内层循环）
			cursor += 1
			continue
		var title := String(row[0])
		cursor += 1
		var rows: Array = []
		while cursor < KEYS.size() and String((KEYS[cursor] as Array)[1]) != "":
			rows.append(KEYS[cursor])
			cursor += 1
		if groups.has(title):
			col.add_child(_group_card(title, rows))


func _group_card(title: String, rows: Array) -> Control:
	var card := Clay.inset(UITokens.S3)
	var v := VBoxContainer.new()
	v.add_theme_constant_override("separation", 2)      # 行距 2：一条只占一行字，不用松
	card.add_child(v)
	var t := Label.new()
	t.text = title
	t.theme_type_variation = "TitleLabel"
	t.add_theme_font_size_override("font_size", UITokens.T_BODY)
	v.add_child(t)
	var recs: Array = []
	for r in rows:
		var rec := _key_row(String(r[0]), String(r[1]))
		v.add_child(rec["node"])
		recs.append(rec)
		_key_rows.append(rec)
	_key_groups.append({"node": card, "rows": recs})
	return card


## 一行 = [键帽 / 长句][说明]。记录留着给搜索过滤用。
func _key_row(k: String, d: String) -> Dictionary:
	var h := HBoxContainer.new()
	h.add_theme_constant_override("separation", UITokens.S2)
	var wrap := HBoxContainer.new()
	wrap.custom_minimum_size = Vector2(132, 0)
	wrap.add_child(_key_cap(k))
	h.add_child(wrap)
	var desc := Label.new()
	desc.text = d
	desc.theme_type_variation = "MutedLabel"
	desc.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	desc.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	desc.custom_minimum_size = Vector2(170, 0)      # 自动换行的 Label 必须给宽（E99 配方）
	desc.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	h.add_child(desc)
	return {"node": h, "key": k, "desc": d}


## 短键位画成键帽；长句（"拖拽时按住 Shift"、"先点进口车道…"）当普通小字 —— 否则一颗键帽能长一里地
func _key_cap(k: String) -> Control:
	var l := Label.new()
	l.text = k
	l.theme_type_variation = "KeyCap" if k.length() <= 12 else "HintLabel"
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	return l


## 搜索：按"键位 + 说明"子串过滤；整组都空了就藏整张卡
func _filter_keys(q: String) -> void:
	var needle := q.strip_edges().to_lower()
	var any := false
	for g in _key_groups:
		var shown := 0
		for rec in g["rows"]:
			var hit: bool = needle == "" \
				or String(rec["key"]).to_lower().contains(needle) \
				or String(rec["desc"]).to_lower().contains(needle)
			(rec["node"] as Control).visible = hit
			if hit:
				shown += 1
		(g["node"] as Control).visible = shown > 0
		if shown > 0:
			any = true
	var total := _key_rows.size()
	var matched := 0
	for rec in _key_rows:
		if (rec["node"] as Control).visible:
			matched += 1
	if _empty_hint != null:
		_empty_hint.visible = (not any) and needle != ""
		_empty_hint.text = "没有匹配「%s」的键位。" % q.strip_edges()
	if _count_label != null:
		_count_label.text = ("共 %d 条" % total) if needle == "" else ("匹配 %d / %d" % [matched, total])


## 居中 + 按窗口留边（最小窗口 990×470 也不会顶出去）
func _layout_keymap() -> void:
	if _keymap == null or not is_inside_tree():
		return
	var vp := get_viewport_rect().size
	var w: float = clampf(vp.x - 120.0, 700.0, 940.0)
	# ★ 上限从 660 提到 900：精简文案后两列内容仍要 ~870（含边框 147），
	#   660 会让 1080p 上也在中间把卡片切断。900 在 1080p 上刚好装下、不滚动。
	var h: float = clampf(vp.y - 100.0, 300.0, 940.0)
	_keymap.size = Vector2(w, h)
	_keymap.position = ((vp - _keymap.size) * 0.5).floor()


## F1：显隐键位表（打开时清掉搜索、聚焦搜索框、pop_in 入场）
func toggle_visible() -> void:
	if _keymap == null:
		return
	var open := not _keymap.visible
	_keymap.visible = open
	if not open:
		return
	_layout_keymap()
	if _search != null:
		_search.text = ""
	_filter_keys("")
	Motion.pop_in(_keymap, _keymap.size * 0.5)
	if _search != null:
		_search.grab_focus()

