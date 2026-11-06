extends Control

## 上下文工具条 ContextBar（`docs/ui/UI-R0 §2.2 ③`）：悬浮在画布上沿的 44 px 胶囊条。
##
## 左：当前模式名 │ 中：**选中面包屑**（"路 #3 · 4 车道 · 12.4 m"）
## 右：该模式**已有开关的镜像** —— 道路=吸附；连接=含掉头/含过街；装饰=当前素材名。
##
## ★ 只做"看 + 转发现有开关"：这里每个开关背后都是 `EditorApp` / `GridCanvas` 里**已经存在**的那个变量，
##   不存在任何新能力。开关状态每帧从真值读回（`set_pressed_no_signal`），所以属性面板里改、这里也会跟着变。
##
## ★ 不重复放工具条已有的"测量"：那是全局工具，放一处就够（左栏）。

const TURN_CN := {
	"Straight": "直行", "Left": "左转", "Right": "右转",
	"UTurn": "掉头", "Crosswalk": "人行横道",
}

var _mode_label: Label
var _crumb_label: Label
var _toggles: HBoxContainer
var _grid: Node = null
var _pill_snap: Button
var _pill_uturn: Button
var _pill_cross: Button
var _asset_label: Label
var _last_crumb := ""


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_PASS
	# 位置全部从令牌推：顶栏下 12px 起，高度 CONTEXT_BAR_H
	offset_top = float(UITokens.TOP_BAR_H) + float(UITokens.S3)
	offset_bottom = offset_top + float(UITokens.CONTEXT_BAR_H)
	_grid = get_node_or_null("../../GridCanvas")
	if _grid == null:
		var sc: Node = get_tree().current_scene
		if sc != null:
			_grid = sc.get_node_or_null("GridCanvas")
	_build()
	EditorApp.mode_changed.connect(_on_mode_changed)
	_on_mode_changed(EditorApp.mode)
	set_process(true)


func _process(_delta: float) -> void:
	_ensure_anchors()
	_sync_pills()
	var c := _crumb()
	if c != _last_crumb:
		_last_crumb = c
		if _crumb_label != null:
			_crumb_label.text = c
	if _asset_label != null and _asset_label.visible:
		var a := String(EditorApp.current_asset)
		var text := "素材：（未选）"
		if a != "":
			text = "素材：%s" % a.get_file()
		if _asset_label.text != text:
			_asset_label.text = text


func _build() -> void:
	var card := Clay.plate(UITokens.S1, UITokens.S2)
	card.name = "Card"
	card.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	card.mouse_filter = Control.MOUSE_FILTER_PASS
	add_child(card)

	var h := HBoxContainer.new()
	h.add_theme_constant_override("separation", UITokens.S3)
	card.get_node("Body").add_child(h)

	_mode_label = Label.new()
	_mode_label.theme_type_variation = "TitleLabel"
	_mode_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	h.add_child(_mode_label)

	var div := ColorRect.new()
	div.color = UITokens.SURFACE_3
	div.custom_minimum_size = Vector2(1, 18)
	div.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	div.mouse_filter = Control.MOUSE_FILTER_IGNORE
	h.add_child(div)

	_crumb_label = Label.new()
	_crumb_label.theme_type_variation = "MutedLabel"
	_crumb_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_crumb_label.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_crumb_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	h.add_child(_crumb_label)

	_toggles = HBoxContainer.new()
	_toggles.add_theme_constant_override("separation", UITokens.S2)
	h.add_child(_toggles)

	_pill_snap = Clay.pill("吸附", true)
	_pill_snap.tooltip_text = "格点吸附（X）"
	_pill_snap.pressed.connect(_on_snap)
	_toggles.add_child(_pill_snap)

	_pill_uturn = Clay.pill("含掉头", false)
	_pill_uturn.tooltip_text = "生成连接时是否包含掉头（与属性面板同一开关）"
	_pill_uturn.pressed.connect(_on_uturn)
	_toggles.add_child(_pill_uturn)

	_pill_cross = Clay.pill("含过街", true)
	_pill_cross.tooltip_text = "生成连接时是否包含行人过街（与属性面板同一开关）"
	_pill_cross.pressed.connect(_on_cross)
	_toggles.add_child(_pill_cross)

	_asset_label = Label.new()
	_asset_label.theme_type_variation = "HintLabel"
	_asset_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_toggles.add_child(_asset_label)


## 左右边界都要靠"别的节点"算：坞在场景里排在本节点之后、图层列属于 HelpPanel（更靠后），
## 所以这里轮询到两者都出现为止，之后交给 resized 信号。
var _dock: Control = null
var _rail: Control = null


func _ensure_anchors() -> void:
	if _dock == null:
		var docks := get_tree().get_nodes_in_group("editor_dock")
		if not docks.is_empty():
			_dock = docks[0]
			_dock.resized.connect(_apply_right_offset)
			_apply_right_offset()
	if _rail == null:
		var rails := get_tree().get_nodes_in_group("layer_rail")
		if not rails.is_empty():
			_rail = rails[0]
			_fit_left()


## 左边界让开"左栏 + 图层胶囊列"：以前写死 128，字号一变大胶囊列变宽就又压上了
func _fit_left() -> void:
	var x := float(UITokens.TOOL_RAIL_W) + float(UITokens.S2)
	if _rail != null:
		x = _rail.position.x + _rail.size.x + float(UITokens.S2)
	offset_left = x


## 右边界跟着右侧坞走（坞宽可拖，硬编码会导致提示条被坞盖住或悬空）
func _apply_right_offset() -> void:
	if _dock == null:
		offset_right = -float(UITokens.DOCK_W_DEFAULT) - 16.0
		return
	offset_right = -(_dock.size.x + 16.0)


func _on_mode_changed(kind: int) -> void:
	_mode_label.text = _mode_name(kind)
	var is_road := kind == EditorMode.Kind.ROAD
	var is_conn := kind == EditorMode.Kind.CONNECT
	var is_deco := kind == EditorMode.Kind.DECO
	_pill_snap.visible = is_road
	_pill_uturn.visible = is_conn
	_pill_cross.visible = is_conn
	_asset_label.visible = is_deco
	_sync_pills()


func _sync_pills() -> void:
	if _grid != null and _pill_snap != null:
		_pill_snap.set_pressed_no_signal(bool(_grid.snap_enabled))
	if _pill_uturn != null:
		_pill_uturn.set_pressed_no_signal(bool(EditorApp.include_uturn))
	if _pill_cross != null:
		_pill_cross.set_pressed_no_signal(bool(EditorApp.include_crosswalk))


func _on_snap() -> void:
	if _grid != null:
		_grid.toggle_snap()          # 与 X 键、左栏"吸"共用同一函数


func _on_uturn() -> void:
	EditorApp.include_uturn = _pill_uturn.button_pressed


func _on_cross() -> void:
	EditorApp.include_crosswalk = _pill_cross.button_pressed


# ============================================================
# 面包屑
# ============================================================

func _crumb() -> String:
	if EditorApp.selection.size() > 1:
		return "已选 %d 条路" % EditorApp.selection.size()
	if EditorApp.selected_deco >= 0:
		return "装饰 #%d" % EditorApp.selected_deco
	if EditorApp.selected_junction >= 0:
		var node: Dictionary = EditorApp.doc.find_node(EditorApp.selected_junction)
		return "节点 #%d · %s" % [EditorApp.selected_junction, String(node.get("type", "?"))]
	if EditorApp.selected_turn >= 0:
		var t: Dictionary = EditorApp.doc.find_turn(EditorApp.selected_turn)
		var ty := String(t.get("type", ""))
		return "连接 #%d · %s" % [EditorApp.selected_turn, String(TURN_CN.get(ty, ty))]
	if EditorApp.selected_road >= 0:
		var road: Dictionary = EditorApp.doc.find_road(EditorApp.selected_road)
		if road.is_empty():
			return "路 #%d" % EditorApp.selected_road
		var a: Vector2 = EditorApp.doc.node_pos(int(road.get("a_node", -1)))
		var b: Vector2 = EditorApp.doc.node_pos(int(road.get("b_node", -1)))
		return "路 #%d · %d 车道 · %.1f m" % [
			EditorApp.selected_road, (road.get("lanes", []) as Array).size(), a.distance_to(b)]
	return "未选中对象"


func _mode_name(k: int) -> String:
	match k:
		EditorMode.Kind.ROAD: return "道路"
		EditorMode.Kind.CONNECT: return "连接"
		EditorMode.Kind.TRAFFIC: return "车辆"
		EditorMode.Kind.DECO: return "装饰"
	return "?"
