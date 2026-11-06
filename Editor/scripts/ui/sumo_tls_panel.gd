extends VBoxContainer

## 信号灯配时编辑器（专业模式）—— 阶段 × 灯位矩阵。
##
## **嵌在右侧坞的路口属性面板里**（2026-11-04 老板要求：不做浮窗，且专业模式下**取代**原来的「配时」小节）。
## 所以宽度只有坞那么宽（默认 420），布局必须挤压：格子自适应均分、阶段时长放到下面的「当前阶段」行里改。
##
## 灯位（行）来自**探针**（`EditorApp.sumo_probe_result()`）：那是 netconvert 实际分配的
## linkIndex 表，含人行横道与自行车。不能凭 `Doc.turns` 自己排 —— 排出来会和实际生效的灯错位。
##
## ★ 三种编辑操作的口径：
##   ① 点格子 = 循环 G→g→y→r→o（每一步都是一次可撤销的编辑）；
##   ② 数字（时长 / 相位差 / 感应参数）= 不重建界面，只就地更新周期标签；
##   ③ 结构性操作（增删阶段 / 增删方案 / 从基础方案生成）= 重建整块。
##
## ★ **不能订阅 `content_changed` 做重建**：专业档每次 `touch()` 都会 bump
##   `profile.revision`，而它进了 `content_signature()` —— 订阅了就会"拖一格重建一次"，
##   正在拖的 SpinBox 会被拆掉。只订阅 `doc_changed`（换文档 / 撤销）。

## ★ 格子**固定宽度**（2026-11-04）：不再"均分剩余宽度"。
##   原因：坞宽可拖，均分时"多出来的几像素"会在各列间不均，看着有的列略宽；
##   而且格子宽随坞变，视觉不稳定。固定 44 之后，1 个阶段和 10 个阶段列宽一致。
const CELL_W := 44
const ROW_W := 72
const CELL_H := 28

const C_GREEN := Color("#4FA96B")
const C_GREEN_SOFT := Color("#A9D3B4")
const C_AMBER := Color("#E8A93C")
const C_RED := Color("#8C4A44")
const C_OFF := Color("#9AA0A6")
const C_TEXT_DARK := Color("#20301F")

var node_id: int = -1

var _prog_i: int = 0
var _phase_i: int = 0
var _content: VBoxContainer
var _cycle_label: Label
var _scroll: ScrollContainer          ## 矩阵的横向滚动容器（重构时会被换掉，用 is_instance_valid 判活）
var _grid: GridContainer


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_PASS
	add_theme_constant_override("separation", UITokens.S2)
	EditorApp.sumo_probe_changed.connect(_on_probe_changed)
	EditorApp.doc_changed.connect(_on_doc_changed)
	rebuild()


func _exit_tree() -> void:
	# ★ 面板被拆（换选中 / 撤销 / 切回基础）→ 画布上的灯态染色必须跟着消失，
	#   否则会留下一堆"看起来还在配时"的彩色连接线。
	EditorApp.clear_sumo_tint()


## 打开某个路口的配时（路口属性面板创建本节点后调用）
func open_for(nid: int) -> void:
	if nid < 0:
		return
	if nid != node_id:
		node_id = nid
		_prog_i = 0
		_phase_i = 0
	# 专业面板先要一张真实 link 表；拿不到就先把占位画出来，等 sumo_probe_changed
	EditorApp.request_sumo_probe()
	if is_inside_tree():
		rebuild()


func _on_probe_changed() -> void:
	rebuild()


func _on_doc_changed() -> void:
	rebuild()                              # 换文档 / 撤销重做 → 灯位描述与数据都得重来


func _refresh_later() -> void:
	call_deferred("rebuild")


# ============================================================
# 重建
# ============================================================

func rebuild() -> void:
	_scroll = null                        # 下面会整块重建，旧引用一律作废
	_grid = null
	if _content != null and is_instance_valid(_content):
		remove_child(_content)
		_content.queue_free()
	_content = VBoxContainer.new()
	_content.add_theme_constant_override("separation", UITokens.S2)
	add_child(_content)

	if node_id < 0:
		_content.add_child(_hint("在连接模式里选中一个信号路口。"))
		return

	var node: Dictionary = EditorApp.doc.find_node(node_id)
	if node.is_empty():
		_content.add_child(_hint("节点 #%d 已不存在。" % node_id))
		return
	if String(node.get("type", "")) != "Signalized":
		_content.add_child(_hint("节点 #%d 不是信号路口。先把节点类型改成「信号路口」。" % node_id))
		return

	var probe: Dictionary = EditorApp.sumo_probe_result()
	if probe.is_empty() or not bool(probe.get("ok", false)):
		_build_loading()
		return

	var links: Array = EditorApp.sumo_probe.links_at(node_id)
	if links.is_empty():
		_content.add_child(_hint("探针在节点 #%d 上没找到信号灯位（网络里这个路口可能不是 traffic_light）。" % node_id))
		return

	_build_program_rows(links)
	_build_matrix(links)
	_build_phase_bar(links)
	_push_tint(links)


func _build_loading() -> void:
	if EditorApp.sumo_probe.busy:
		_content.add_child(_hint("正在解析路网（跑一次 netconvert，取真实灯位表）…"))
		return
	var err: String = EditorApp.sumo_probe.error
	_content.add_child(_hint("探针失败：%s" % err if err != "" else "还没有灯位表。"))
	var retry := Clay.btn("解析路网")
	retry.pressed.connect(func() -> void: EditorApp.request_sumo_probe(true))
	_content.add_child(retry)


# --- 方案（选哪套 / 类型 / 参数）------------------------------------------

func _build_program_rows(links: Array) -> void:
	var programs: Array = SumoTlsModel.programs_of(EditorApp.profile, node_id)
	if programs.is_empty():
		_content.add_child(_hint("这个路口还没有专业配时。"))
		var gen := Clay.btn_primary("从基础方案生成")
		gen.tooltip_text = "用「基础模式」里现成的阶段生成一套（绿/黄/全红各一跳）"
		gen.pressed.connect(func() -> void: _generate(links))
		_content.add_child(gen)
		var blank := Clay.btn("从空白开始")
		blank.tooltip_text = "先建一套只有一个阶段的空方案，再自己点灯"
		blank.pressed.connect(func() -> void: _new_blank(links))
		_content.add_child(blank)
		return

	_prog_i = clampi(_prog_i, 0, programs.size() - 1)

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", UITokens.S1)
	var opt := OptionButton.new()
	opt.fit_to_longest_item = false
	opt.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	for i in programs.size():
		var program: Dictionary = programs[i]
		var when := float(program.get("activate_at", 0.0))
		opt.add_item("方案 %s%s" % [String(program.get("id", str(i))),
			("" if when <= 0.0 else " · %.0f s 起" % when)])
	opt.select(_prog_i)
	opt.item_selected.connect(func(i: int) -> void:
		_prog_i = i
		_phase_i = 0
		rebuild())
	row.add_child(opt)

	var dup := Clay.icon_btn("⧉", 28)
	dup.tooltip_text = "复制当前方案"
	dup.pressed.connect(func() -> void:
		EditorApp.begin_edit()
		_prog_i = SumoTlsModel.duplicate_program(EditorApp.profile, node_id, _prog_i)
		_mark_dirty()
		EditorApp.end_edit()
		_refresh_later())
	row.add_child(dup)

	var del := Clay.icon_btn("✕", 28)
	del.tooltip_text = "删除当前方案"
	del.pressed.connect(func() -> void:
		EditorApp.begin_edit()
		SumoTlsModel.remove_program(EditorApp.profile, node_id, _prog_i)
		_prog_i = 0
		_phase_i = 0
		_mark_dirty()
		EditorApp.end_edit()
		_refresh_later())
	row.add_child(del)
	_content.add_child(Clay.field_row("方案", row))

	var program: Dictionary = SumoTlsModel.program_of(EditorApp.profile, node_id, _prog_i)
	var type_opt := OptionButton.new()
	type_opt.fit_to_longest_item = false
	for i in SumoTlsModel.PROGRAM_TYPES.size():
		type_opt.add_item(SumoTlsModel.PROGRAM_TYPE_LABELS[i])
	var cur_type := String(program.get("type", "static"))
	type_opt.select(maxi(SumoTlsModel.PROGRAM_TYPES.find(cur_type), 0))
	type_opt.tooltip_text = "定时 = 固定周期；感应 = 按车流自适应（SUMO 自动生成本地检测器）"
	type_opt.item_selected.connect(func(i: int) -> void:
		EditorApp.begin_edit()
		SumoTlsModel.set_program_field(EditorApp.profile, node_id, _prog_i,
			"type", SumoTlsModel.PROGRAM_TYPES[i])
		_mark_dirty()
		EditorApp.end_edit()
		_refresh_later())
	_content.add_child(Clay.field_row("类型", type_opt))

	var offset := _spin(float(program.get("offset", 0.0)), 0.0, 600.0)
	offset.tooltip_text = "SUMO: offset。整条干线做绿波时用它错开各路口相位"
	offset.value_changed.connect(func(v: float) -> void: _set_program_num("offset", v))
	_content.add_child(Clay.field_row("相位差", offset, "s"))

	var activate := _spin(float(program.get("activate_at", 0.0)), 0.0, 86400.0)
	activate.tooltip_text = "SUMO: WAUT 切换时刻。多套方案时，从这一秒起换成该方案"
	activate.value_changed.connect(func(v: float) -> void: _set_program_num("activate_at", v))
	_content.add_child(Clay.field_row("启用时刻", activate, "s"))

	_cycle_label = Label.new()
	_cycle_label.theme_type_variation = "MutedLabel"
	_content.add_child(Clay.field_row("周期", _cycle_label))
	_update_cycle_label()

	if cur_type != "static":
		var params: Dictionary = program.get("params", {})
		for spec in [["max-gap", "最大车头间距", 3.0], ["detector-gap", "检测器间距", 2.0],
				["passing-time", "通过时间", 2.0]]:
			var key := String(spec[0])
			var spin := _spin(float(params.get(key, float(spec[2]))), 0.0, 30.0)
			spin.tooltip_text = "SUMO: %s（%s）" % [key, String(spec[1])]
			spin.value_changed.connect(func(v: float) -> void: _set_param(key, v))
			_content.add_child(Clay.field_row(String(spec[1]), spin, "s"))


# --- 矩阵 ----------------------------------------------------------------

func _build_matrix(links: Array) -> void:
	var phases: Array = SumoTlsModel.phases_of(EditorApp.profile, node_id, _prog_i)
	if phases.is_empty():
		_content.add_child(_hint("这套方案还没有阶段。点下面「＋ 阶段」开始。"))
		return

	_phase_i = clampi(_phase_i, 0, phases.size() - 1)

	# ★ 横向：**只让矩阵自己滚**（2026-11-04 老板提的溢出问题）。
	#   GridContainer 的最小宽度会一路顶给 VBox → 整块（连下面的按钮）被撑宽，
	#   而坞的横向滚动是 DISABLED → 直接被裁掉、点不到右侧的阶段。
	#   套一层 ScrollContainer 后：它的最小宽度不再来自子节点（H=AUTO），
	#   于是"矩阵超宽 → 只有矩阵滚"，下面的按钮照旧固定成坞宽。
	#   ★ vertical 必须 DISABLED：坞本身已经是竖向滚动，再叠一层滚轮会打架；
	#     关掉后它的最小高度仍跟随网格，纵向位置和以前一样。
	#   ★ mouse_filter=IGNORE：滚轮事件不在这里被吞，直接冒泡给坞的竖向滚动；
	#     横向用底下的滚动条拖（滚动条是子节点，照样收得到事件）。
	_scroll = ScrollContainer.new()
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_AUTO
	_scroll.vertical_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	# ★ 滚轮事件不在这里被吞，直接冒泡给坞的竖向滚动；横向用下面那根加粗的滚动条拖
	#   （滚动条是子节点，照样收得到点击 —— IGNORE 只忽略容器本身）。
	_scroll.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_scroll.size_flags_horizontal = Control.SIZE_FILL
	_content.add_child(_scroll)

	var grid := GridContainer.new()
	grid.columns = phases.size() + 1
	grid.add_theme_constant_override("h_separation", 3)
	grid.add_theme_constant_override("v_separation", 3)
	grid.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN   # ★ 固定宽，不许被拉伸
	_scroll.add_child(grid)
	_grid = grid
	# ★ 主题里的滚动条只有 ~4px 且轨道透明 → 用户"找不到滚动条"（2026-11-04 实测反馈）。
	#   这里只给本实例加厚：轨道浅灰、滑块深灰、上下各 4px 内容边距 ≈ 10px 高。
	_style_h_scrollbar(_scroll.get_h_scroll_bar())

	var corner := Label.new()
	corner.text = "灯位"
	corner.theme_type_variation = "HintLabel"
	corner.custom_minimum_size = Vector2(ROW_W, 0)
	corner.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	grid.add_child(corner)

	for j in phases.size():
		grid.add_child(_phase_header_cell(j, phases[j]))

	for link in links:
		grid.add_child(_row_head(link))
		var tid := int(link.get("turn", -1))
		for j in phases.size():
			grid.add_child(_state_cell(link, tid, j))


func _phase_header_cell(index: int, phase: Dictionary) -> Control:
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 1)
	box.custom_minimum_size = Vector2(CELL_W, 0)
	box.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN

	var head := Button.new()
	head.custom_minimum_size = Vector2(CELL_W, 20)
	head.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	head.text = ("●%d" % (index + 1)) if index == _phase_i else str(index + 1)
	head.tooltip_text = "选中第 %d 个阶段（下面的阶段操作都作用在它身上）" % (index + 1)
	if index == _phase_i:
		head.theme_type_variation = "PillToggle"
		head.button_pressed = true
	head.pressed.connect(func() -> void:
		_phase_i = index
		rebuild())
	box.add_child(head)

	var dur := Label.new()
	dur.text = "%.0fs" % float(phase.get("duration", 0.0))
	dur.theme_type_variation = "HintLabel"
	dur.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	dur.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	box.add_child(dur)
	return box


func _row_head(link: Dictionary) -> Button:
	var tid := int(link.get("turn", -1))
	var b := Button.new()
	b.text = "%s%s" % [_dir_arrow(String(link.get("dir", ""))), _kind_short(String(link.get("kind", "")))]
	b.custom_minimum_size = Vector2(ROW_W, CELL_H)
	b.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	b.clip_text = true
	b.tooltip_text = "%s\n%s\n点一下在画布上高亮这条连接（再点一次取消）" % [b.text, _link_tooltip(link)]
	b.pressed.connect(func() -> void:
		if EditorApp.sumo_highlight_turn == tid:
			EditorApp.clear_sumo_highlight()
		else:
			EditorApp.sumo_highlight_turn = tid
		EditorApp.toast("画布高亮：%s" % _link_tooltip(link), "info"))
	return b


func _state_cell(link: Dictionary, turn_id: int, phase_j: int) -> Button:
	var state := SumoTlsModel.state_at(EditorApp.profile, node_id, _prog_i, phase_j, turn_id)
	var b := Button.new()
	b.custom_minimum_size = Vector2(CELL_W, CELL_H)
	b.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	b.text = state
	b.tooltip_text = "灯位 #%d ｜ 点一下切换 G→g→y→r→o（绿→让→黄→红→灭）\n%s" % [
		int(link.get("link_index", -1)), _link_tooltip(link)]
	_paint_cell(b, state)
	b.pressed.connect(func() -> void: _on_cell(turn_id, phase_j))
	return b


func _on_cell(turn_id: int, phase_j: int) -> void:
	if turn_id < 0:
		return
	var current := SumoTlsModel.state_at(EditorApp.profile, node_id, _prog_i, phase_j, turn_id)
	EditorApp.begin_edit("tls:%d:%d:%d" % [node_id, phase_j, turn_id])
	SumoTlsModel.set_link_state(EditorApp.profile, node_id, _prog_i, phase_j, turn_id,
		SumoTlsModel.next_state(current))
	_mark_dirty()
	EditorApp.end_edit()
	_refresh_later()


# --- 阶段操作（当前阶段的属性都放这里，矩阵里只留格子）--------------------

func _build_phase_bar(links: Array) -> void:
	var phases: Array = SumoTlsModel.phases_of(EditorApp.profile, node_id, _prog_i)
	if phases.is_empty():
		var add0 := Clay.btn("＋ 阶段")
		add0.pressed.connect(func() -> void: _add_phase())
		_content.add_child(add0)
		return

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", UITokens.S1)
	var add := Clay.btn("＋ 阶段")
	add.tooltip_text = "在当前阶段之后插入一跳（复制它的灯态，方便微调）"
	add.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add.pressed.connect(func() -> void: _add_phase())
	row.add_child(add)

	var left := Clay.icon_btn("◀", 28)
	left.tooltip_text = "当前阶段左移"
	left.pressed.connect(func() -> void: _move_phase(-1))
	row.add_child(left)

	var right := Clay.icon_btn("▶", 28)
	right.tooltip_text = "当前阶段右移"
	right.pressed.connect(func() -> void: _move_phase(1))
	row.add_child(right)

	var del := Clay.btn("删除", "danger")
	del.tooltip_text = "删除当前阶段"
	del.pressed.connect(func() -> void:
		EditorApp.begin_edit()
		SumoTlsModel.remove_phase(EditorApp.profile, node_id, _prog_i, _phase_i)
		_phase_i = maxi(0, _phase_i - 1)
		_mark_dirty()
		EditorApp.end_edit()
		_refresh_later())
	row.add_child(del)
	_content.add_child(row)

	var cur: Dictionary = phases[_phase_i]
	var dur := _spin(float(cur.get("duration", 20.0)), 0.1, 600.0)
	dur.tooltip_text = "第 %d 个阶段的时长。SUMO: phase/duration" % (_phase_i + 1)
	dur.value_changed.connect(func(v: float) -> void: _set_phase_num(_phase_i, "duration", v))
	_content.add_child(Clay.field_row("阶段 %d 时长" % (_phase_i + 1), dur, "s"))

	var program: Dictionary = SumoTlsModel.program_of(EditorApp.profile, node_id, _prog_i)
	if String(program.get("type", "static")) != "static":
		var base := float(cur.get("duration", 20.0))
		var mn := _spin(float(cur.get("min_dur", maxf(1.0, base * 0.3))), 0.1, 600.0)
		mn.tooltip_text = "SUMO: phase/minDur。感应控制下这一跳最少亮多久"
		mn.value_changed.connect(func(v: float) -> void: _set_phase_num(_phase_i, "min_dur", v))
		_content.add_child(Clay.field_row("最小绿", mn, "s"))

		var mx := _spin(float(cur.get("max_dur", base * 1.5)), 0.1, 600.0)
		mx.tooltip_text = "SUMO: phase/maxDur。感应控制下这一跳最多亮多久"
		mx.value_changed.connect(func(v: float) -> void: _set_phase_num(_phase_i, "max_dur", v))
		_content.add_child(Clay.field_row("最大绿", mx, "s"))

	var name_edit := LineEdit.new()
	name_edit.text = String(cur.get("name", ""))
	name_edit.tooltip_text = "只是个备注，写进 SUMO 的 phase@name"
	name_edit.text_submitted.connect(func(t: String) -> void: _set_phase_text("name", t))
	name_edit.focus_exited.connect(func() -> void: _set_phase_text("name", name_edit.text))
	_content.add_child(Clay.field_row("阶段名称", name_edit))

	# 校验提示：有灯位从头到尾没绿过 → 导出的 tll 会自动补绿，但用户应当知道
	var turns: Array = []
	for link in links:
		turns.append(int(link.get("turn", -1)))
	var lost := SumoTlsModel.never_green_turns(EditorApp.profile, node_id, _prog_i, turns)
	if not lost.is_empty():
		var warn := Label.new()
		warn.text = "⚠ %d 个灯位全程没绿过（导出时会自动在第 1 相位补绿，否则 SUMO 拒绝加载）" % lost.size()
		warn.add_theme_color_override("font_color", UITokens.AMBER_500)
		warn.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		_content.add_child(warn)

	var regen := Clay.btn("从基础方案重新生成")
	regen.tooltip_text = "丢掉当前方案，按基础模式的阶段重来（会覆盖这套方案）"
	regen.pressed.connect(func() -> void: _generate(links))
	_content.add_child(regen)

	var clear := Clay.btn("清空本路口配时", "danger")
	clear.tooltip_text = "删掉这个路口的全部专业配时（回到基础模式那套）"
	clear.pressed.connect(func() -> void:
		EditorApp.begin_edit()
		SumoTlsModel.clear(EditorApp.profile, node_id)
		_mark_dirty()
		EditorApp.end_edit()
		_refresh_later())
	_content.add_child(clear)


# ============================================================
# 画布染色：把当前阶段的灯态交给 RoadLayer
# ============================================================

func _push_tint(links: Array) -> void:
	var phases: Array = SumoTlsModel.phases_of(EditorApp.profile, node_id, _prog_i)
	if phases.is_empty() or _phase_i < 0 or _phase_i >= phases.size():
		EditorApp.clear_sumo_tint()
		return
	var tint := {}
	for link in links:
		var tid := int(link.get("turn", -1))
		if tid < 0:
			continue
		tint[tid] = SumoTlsModel.state_at(EditorApp.profile, node_id, _prog_i, _phase_i, tid)
	EditorApp.set_sumo_tint(tint)


# ============================================================
# 数据操作的小封装
# ============================================================

func _mark_dirty() -> void:
	EditorApp.doc.dirty = true              # 专业档也是内容：标题的 * 与自动保存都要跟


func _set_program_num(key: String, value: float) -> void:
	EditorApp.begin_edit("tls_prog:%d:%s" % [node_id, key])
	SumoTlsModel.set_program_field(EditorApp.profile, node_id, _prog_i, key, value)
	_mark_dirty()
	EditorApp.end_edit()
	_update_cycle_label()


func _set_param(key: String, value: float) -> void:
	EditorApp.begin_edit("tls_param:%d:%s" % [node_id, key])
	SumoTlsModel.set_param(EditorApp.profile, node_id, _prog_i, key, str(value))
	_mark_dirty()
	EditorApp.end_edit()


func _set_phase_num(index: int, key: String, value: float) -> void:
	# ★ 只留 1 位小数：SpinBox 的 float 常带 34.0000000004 这类噪声，
	#   直接写进 .sumo.json 会让人以为"又多了个 0.1"（2026-11-04）。
	var v := snappedf(value, 0.1)
	EditorApp.begin_edit("tls_phase:%d:%d:%s" % [node_id, index, key])
	SumoTlsModel.set_phase_field(EditorApp.profile, node_id, _prog_i, index, key, v)
	_mark_dirty()
	EditorApp.end_edit()
	_update_cycle_label()


func _set_phase_text(key: String, value: String) -> void:
	var phases: Array = SumoTlsModel.phases_of(EditorApp.profile, node_id, _prog_i)
	if _phase_i < 0 or _phase_i >= phases.size():
		return
	if String(phases[_phase_i].get(key, "")) == value:
		return
	EditorApp.begin_edit()
	SumoTlsModel.set_phase_field(EditorApp.profile, node_id, _prog_i, _phase_i, key, value)
	_mark_dirty()
	EditorApp.end_edit()


func _add_phase() -> void:
	EditorApp.begin_edit()
	_phase_i = SumoTlsModel.add_phase(EditorApp.profile, node_id, _prog_i, _phase_i)
	_mark_dirty()
	EditorApp.end_edit()
	_refresh_later()


func _move_phase(dir: int) -> void:
	EditorApp.begin_edit()
	_phase_i = SumoTlsModel.move_phase(EditorApp.profile, node_id, _prog_i, _phase_i, dir)
	_mark_dirty()
	EditorApp.end_edit()
	_refresh_later()


func _generate(links: Array) -> void:
	var stages: Array = _basic_stages()
	if stages.is_empty():
		EditorApp.toast("这个路口还没有基础配时（先用基础模式配阶段）", "warn")
		return
	var turns: Array = []
	for link in links:
		var tid := int(link.get("turn", -1))
		if tid >= 0:
			turns.append(tid)
	EditorApp.begin_edit()
	var count := SumoTlsModel.build_from_basic(EditorApp.profile, node_id, stages, turns)
	_prog_i = 0
	_phase_i = 0
	_mark_dirty()
	EditorApp.end_edit()
	print("专业配时：节点 %d 从基础方案生成了 %d 跳" % [node_id, count])
	EditorApp.toast("已按基础方案生成 %d 跳" % count, "ok")
	_refresh_later()


func _new_blank(links: Array) -> void:
	EditorApp.begin_edit()
	var pi := SumoTlsModel.add_program(EditorApp.profile, node_id)
	SumoTlsModel.add_phase(EditorApp.profile, node_id, pi, -1)
	_prog_i = pi
	_phase_i = 0
	_mark_dirty()
	EditorApp.end_edit()
	_refresh_later()


## 基础模式那套阶段性配时（plans[0].stages）；没有就返回空数组
func _basic_stages() -> Array:
	var sig: Dictionary = {}
	for s in EditorApp.doc.signals:
		if int(s.get("node", -1)) == node_id:
			sig = s
			break
	if sig.is_empty():
		return []
	var plans: Array = sig.get("plans", [])
	if plans.is_empty():
		return []
	return plans[0].get("stages", [])


# ============================================================
# 外观小工具
# ============================================================

func _paint_cell(b: Button, state: String) -> void:
	var bg := C_RED
	var fg := Color.WHITE
	match state:
		"G":
			bg = C_GREEN
		"g":
			bg = C_GREEN_SOFT
			fg = C_TEXT_DARK
		"y":
			bg = C_AMBER
			fg = C_TEXT_DARK
		"o":
			bg = C_OFF
		_:
			bg = C_RED
	for name in ["normal", "hover", "pressed", "focus"]:
		var sb := StyleBoxFlat.new()
		sb.bg_color = bg.lightened(0.08) if name == "hover" else bg
		sb.set_corner_radius_all(5)
		sb.border_width_left = 1
		sb.border_width_top = 1
		sb.border_width_right = 1
		sb.border_width_bottom = 1
		sb.border_color = bg.darkened(0.25)
		b.add_theme_stylebox_override(name, sb)
	b.add_theme_color_override("font_color", fg)
	b.add_theme_color_override("font_hover_color", fg)
	b.add_theme_color_override("font_pressed_color", fg)


## 只给这一个横向滚动条加厚。**不改全局主题** —— 坞和弹窗也用它，改了就到处变粗。
## 主题里默认滚动条只有 ~4px、轨道全透明，实测"用户找不到滚动条"。
func _style_h_scrollbar(bar: HScrollBar) -> void:
	if bar == null:
		return
	var track := StyleBoxFlat.new()
	track.bg_color = UITokens.SURFACE_2
	track.set_corner_radius_all(4)
	track.content_margin_top = 4
	track.content_margin_bottom = 4
	var grab := StyleBoxFlat.new()
	grab.bg_color = UITokens.INK_3
	grab.set_corner_radius_all(4)
	grab.content_margin_top = 4
	grab.content_margin_bottom = 4
	var grab_hi := StyleBoxFlat.new()
	grab_hi.bg_color = UITokens.INK_2
	grab_hi.set_corner_radius_all(4)
	grab_hi.content_margin_top = 4
	grab_hi.content_margin_bottom = 4
	bar.add_theme_stylebox_override("scroll", track)
	bar.add_theme_stylebox_override("grabber", grab)
	bar.add_theme_stylebox_override("grabber_highlight", grab_hi)
	bar.add_theme_stylebox_override("grabber_pressed", grab_hi)
	bar.custom_minimum_size.y = 10


func _spin(value: float, min_v: float, max_v: float) -> SpinBox:
	var s := SpinBox.new()
	s.min_value = min_v
	s.max_value = max_v
	# ★ step 必须能整除 min，否则 Godot 的 Range 会按 `min + k×step` 吸附取值：
	#   min=0.1 + step=1 时网格是 0.1 / 1.1 / … / 34.1 —— 用户输 34，出来就是 34.1
	#   （2026-11-04 实测，见 E10 §7-11）。
	#   step 取 0.1（SUMO 允许小数时长），箭头单独按 1 秒走，输整数的体验不变。
	s.step = 0.1
	s.custom_arrow_step = 1.0
	s.value = value                       # ★ 先赋值再连信号，避免多余写回
	return s


func _hint(text: String) -> Label:
	var l := Label.new()
	l.text = text
	l.theme_type_variation = "HintLabel"
	l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	return l


func _update_cycle_label() -> void:
	if _cycle_label == null or not is_instance_valid(_cycle_label):
		return
	var total := SumoTlsModel.cycle_seconds(EditorApp.profile, node_id, _prog_i)
	var count := SumoTlsModel.phases_of(EditorApp.profile, node_id, _prog_i).size()
	_cycle_label.text = "%.0f s（%d 跳）" % [total, count]


func _dir_arrow(dir: String) -> String:
	match dir:
		"l": return "←"
		"r": return "→"
		"t": return "↰"
		"u": return "⤺"
		"s": return "↑"
	return "·"


## 行头只放得下两个字：用「机 / 自 / 人」，完整说明在 tooltip 里
func _kind_short(kind: String) -> String:
	match kind:
		"motor": return "机"
		"bike": return "自"
		"crosswalk": return "人"
		"other": return "他"
	return "?"


func _link_tooltip(link: Dictionary) -> String:
	var kind_text := "机动车"
	match String(link.get("kind", "")):
		"bike": kind_text = "自行车"
		"crosswalk": kind_text = "行人"
		"other": kind_text = "其他"
		"unknown": kind_text = "未知"
	return "%s ｜ 边 %s 车道 %d → 边 %s 车道 %d" % [kind_text,
		String(link.get("from", "?")), int(link.get("from_lane", 0)),
		String(link.get("to", "?")), int(link.get("to_lane", 0))]
