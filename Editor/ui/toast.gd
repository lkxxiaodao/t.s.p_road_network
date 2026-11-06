class_name ToastHost
extends Control

## 轻提示（P4-1）：把「用户动作的结论」浮在画布右下角，3 秒后自动淡出。
##
## ★ 口径（`UI-R0` §3.4 / §4）：
##   - 只在**动作发生的那一刻**造一个胶囊，不挂 `_process`、不逐帧补间；
##   - 位置在**画布右下角**、让开右侧坞与底部状态栏/键位条（和 `FloatWindow` 用同一套算法）；
##   - 同时最多 `MAX_STACK` 条，超了先把最老的淡出扔掉（连按 20 次也不会堆成一堵墙）。
##
## ★ 它**不替代 `print()`**：`EditorApp` 里每条提示都是"print 照旧 + 追加一次 toast"，
##   Output 的日志条数一条不少（P4-1 的验收口径）。

const WIDTH := 360.0             ## 胶囊宽度（文字自动换行，不随内容变宽 → 不会跳）
const LIFETIME_SEC := 3.0        ## 停留时长
const MAX_STACK := 4

const KIND_COLOR := {
	"info": UITokens.BLUE_500,
	"ok": UITokens.GREEN_500,
	"warn": UITokens.AMBER_500,
	"error": UITokens.RED_500,
}

var _stack: VBoxContainer
var _items: Array = []            ## [{node: Control, born: float}]


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_stack = VBoxContainer.new()
	_stack.name = "Stack"
	_stack.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_stack.alignment = BoxContainer.ALIGNMENT_END
	_stack.add_theme_constant_override("separation", UITokens.S2)
	# 锚在右下角、向左上生长：坞一拖宽，这里立刻跟着让位（`_sync_anchor`）
	_stack.anchor_left = 1.0
	_stack.anchor_right = 1.0
	_stack.anchor_top = 1.0
	_stack.anchor_bottom = 1.0
	_stack.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	_stack.grow_vertical = Control.GROW_DIRECTION_BEGIN
	add_child(_stack)
	_sync_anchor()
	resized.connect(_sync_anchor)


## 推一条提示。kind = "info" / "ok" / "warn" / "error"
func push(text: String, kind: String = "info") -> void:
	var msg := text.strip_edges()
	if msg == "":
		return
	if _stack == null:
		return
	_sync_anchor()
	while _items.size() >= MAX_STACK:
		_drop_oldest()

	var card := PanelContainer.new()
	card.theme_type_variation = "ToastPanel"
	card.mouse_filter = Control.MOUSE_FILTER_IGNORE
	card.custom_minimum_size = Vector2(WIDTH, 0)
	var row := HBoxContainer.new()
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	row.add_theme_constant_override("separation", UITokens.S2)
	card.add_child(row)

	var dot := Label.new()
	dot.text = "●"
	dot.mouse_filter = Control.MOUSE_FILTER_IGNORE
	dot.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	dot.add_theme_font_size_override("font_size", UITokens.T_BODY)
	dot.add_theme_color_override("font_color", KIND_COLOR.get(kind, KIND_COLOR["info"]))
	row.add_child(dot)

	var lab := Label.new()
	lab.text = msg
	lab.mouse_filter = Control.MOUSE_FILTER_IGNORE
	lab.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	lab.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	# ★ 自动换行的 Label 必须给宽度，否则它按"最小宽 0"算高度（E99 里记过，会把卡片撑成一条竖线）
	lab.custom_minimum_size = Vector2(WIDTH - 60.0, 0)
	lab.add_theme_font_size_override("font_size", UITokens.T_BODY)
	lab.add_theme_color_override("font_color", UITokens.INK_1)
	row.add_child(lab)

	_stack.add_child(card)
	_items.append({"node": card, "born": Time.get_ticks_msec() / 1000.0})
	Motion.fade_in(card)
	_expire_later(card)


## 到点淡出并移除（一条一条排，不靠 `_process`）
func _expire_later(card: Control) -> void:
	await get_tree().create_timer(LIFETIME_SEC).timeout
	if not is_instance_valid(card):
		return
	await Motion.fade_out(card).finished
	_remove(card)


func _remove(card: Control) -> void:
	for i in range(_items.size() - 1, -1, -1):
		if _items[i]["node"] == card:
			_items.remove_at(i)
	if is_instance_valid(card):
		card.queue_free()


func _drop_oldest() -> void:
	if _items.is_empty():
		return
	var card: Control = _items[0]["node"]
	_items.remove_at(0)
	if is_instance_valid(card):
		Motion.fade_out(card)
		card.queue_free()


## 右下角让开：右侧坞、状态栏、键位提示条
func _sync_anchor() -> void:
	if _stack == null or not is_inside_tree():
		return
	var dock_w := float(UITokens.DOCK_W_DEFAULT)
	var docks := get_tree().get_nodes_in_group("editor_dock")
	if not docks.is_empty():
		dock_w = (docks[0] as Control).size.x
	_stack.offset_right = -(dock_w + float(UITokens.S5))
	_stack.offset_bottom = -(float(UITokens.STATUS_BAR_H) + float(UITokens.HINT_BAR_H) + float(UITokens.S5))
	_stack.offset_left = _stack.offset_right - WIDTH
	_stack.offset_top = _stack.offset_bottom - 400.0    # 只是给容器一个高度上界，内容自己往下排


## 只给探针/自测用：当前在屏上的条数
func count() -> int:
	return _items.size()
