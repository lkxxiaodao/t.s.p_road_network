extends Control

## 键位提示条（`docs/ui/UI-R0 §2.2 ⑦`）：贴在状态栏上方，**半透明胶囊**，
## 只显示"当前模式最常用的几个键"（默认最多 4 个，窄窗口自动减少）。
##
## ★ 单一真值：内容**直接读 `scripts/help_panel.gd` 的 `KEYS` 表**（按"道路模式/连接模式/…"分组匹配）。
##   决不在本文件再抄一份 —— 抄一份的下场就是 F1 键位表和提示条说法不一致，而文档已明令"逐条照抄代码"。
##
## ★ 宽度自适应（2026-10-27）：条目数按可用宽度算，**不是**画完再裁。
##   先按"每条约 220 px"估能放几条，重建时只放这么多；窗口一变就重建 —— 比事后删子节点可靠得多
##   （`queue_free()` 是延迟的，边删边量会把子节点删过头）。

const HELP := preload("res://scripts/help_panel.gd")
const MAX_ITEMS := 4

var _box: HBoxContainer
var _last_mode := -1
var _last_count := -1
var _dock: Control = null


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	# 贴状态栏上沿，高度同样从令牌推（叠在状态栏里就会被裁掉下半截）
	offset_top = -(float(UITokens.STATUS_BAR_H) + float(UITokens.BAR_GAP) + float(UITokens.HINT_BAR_H))
	offset_bottom = -(float(UITokens.STATUS_BAR_H) + float(UITokens.BAR_GAP))
	_build()
	EditorApp.mode_changed.connect(_on_mode_changed)
	_rebuild(EditorApp.mode)
	resized.connect(_on_resized)
	set_process(true)


func _process(_delta: float) -> void:
	# 坞在场景里排在本节点之后 → 它的 _ready 晚于这里，所以"找到就连、找不到下帧再看"
	if _dock == null:
		var docks := get_tree().get_nodes_in_group("editor_dock")
		if docks.is_empty():
			offset_right = -float(UITokens.DOCK_W_DEFAULT) - 16.0
			return
		_dock = docks[0]
		_dock.resized.connect(_apply_right_offset)
		_apply_right_offset()
	else:
		set_process(false)          # 连上就不用再查了（宽度变化由 resized 信号驱动）


## 右边界跟着右侧坞走（坞宽可拖，写死就会被坞盖住）
func _apply_right_offset() -> void:
	if _dock == null:
		return
	offset_right = -(_dock.size.x + 16.0)
	_on_resized()


func _on_resized() -> void:
	_rebuild(EditorApp.mode)


func _build() -> void:
	var pill := Clay.inset(UITokens.S1)
	pill.modulate = Color(1, 1, 1, 0.92)          # 半透明：压在画布上但不抢眼
	pill.mouse_filter = Control.MOUSE_FILTER_IGNORE
	pill.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	pill.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	add_child(pill)

	_box = HBoxContainer.new()
	_box.add_theme_constant_override("separation", UITokens.S3)
	_box.mouse_filter = Control.MOUSE_FILTER_IGNORE
	pill.add_child(_box)


func _on_mode_changed(kind: int) -> void:
	_rebuild(kind)


## 重建整条：先全放上去，再按**实测**宽度从尾部整对删（估算法不可靠 —— 说明文字长短差 3 倍）
func _rebuild(kind: int) -> void:
	if _box == null:
		return
	_last_mode = kind
	for c in _box.get_children():
		_box.remove_child(c)
		c.queue_free()

	var pairs: Array = _pairs_for_mode(kind)
	var n: int = mini(pairs.size(), MAX_ITEMS)
	for i in n:
		if i > 0:
			_box.add_child(_gap())
		var row: Array = pairs[i]
		_box.add_child(_key_chip(String((row as Array)[0])))
		var d := Label.new()
		d.text = String((row as Array)[1])
		d.theme_type_variation = "HintLabel"
		d.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		d.mouse_filter = Control.MOUSE_FILTER_IGNORE
		_box.add_child(d)
	_fit_by_measure()
	_last_count = _box.get_child_count()


## 放不下就从尾部整对丢（[键][说明] (+ 前面的分隔线)），宁可少提示也不压到右边的坞
func _fit_by_measure() -> void:
	var limit: float = size.x - float(UITokens.S2) * 2.0
	if limit <= 0.0:
		return                                   # 首帧还没布局好，等 resized 再来
	var guard := 0
	while _box.get_child_count() > 2 and _box.get_combined_minimum_size().x > limit and guard < 8:
		guard += 1
		for k in 3:                              # 说明 / 键 / 分隔线
			if _box.get_child_count() <= 2:
				break
			var c := _box.get_child(_box.get_child_count() - 1)
			_box.remove_child(c)
			c.queue_free()


func _key_chip(text: String) -> Label:
	var l := Label.new()
	l.text = text
	l.theme_type_variation = "TitleLabel"
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return l


func _gap() -> ColorRect:
	var c := ColorRect.new()
	c.color = UITokens.SURFACE_3
	c.custom_minimum_size = Vector2(1, 14)
	c.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	c.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return c


## 从 F1 键位表里取"当前模式"那一段的前几条（分组标题 = 说明为空的行）
func _pairs_for_mode(kind: int) -> Array:
	var prefix := _group_prefix(kind)
	var out: Array = []
	var inside := false
	var rows: Array = HELP.KEYS
	for row in rows:
		var key := String((row as Array)[0])
		var desc := String((row as Array)[1])
		if desc == "":
			inside = key.begins_with(prefix)
			continue
		if inside:
			out.append(row)
	return out


func _group_prefix(kind: int) -> String:
	match kind:
		EditorMode.Kind.ROAD: return "道路模式"
		EditorMode.Kind.CONNECT: return "连接模式"
		EditorMode.Kind.TRAFFIC: return "车辆模式"
		EditorMode.Kind.DECO: return "装饰模式"
	return "各模式通用"
