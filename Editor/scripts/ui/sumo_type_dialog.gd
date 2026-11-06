extends Window

## 车型库（SUMO）—— 弹窗。
##
## ## 为什么是弹窗而不是右侧面板
##
## 车型定义（车长 / 限速 / 加减速 / 净距）是**低频、全局**的事：改一次影响所有进口。
## 塞进右侧坞会把"这个进口用多少比例"（高频）挤下去；做成弹窗，右侧只留比例。
## 老板 2026-11-05：「车型相关都挪到车道设置下，自定义车型这种功能可以制作弹窗」。
##
## ## 和车道面板的分工
##
## - 本弹窗：**定义**车型（全局库，所有进口共享）
## - 车道面板「进口车型构成」：**引用**车型 + 配比例（每进口一套）
##
## ## 参数口径
##
## 界面给 `km/h`（人看得懂），文件里存 `m/s`（SUMO 口径）。
## 换算只在读写这一层做，别在别处再转一次。

var _list: VBoxContainer
var _empty_hint: Label


func _ready() -> void:
	title = "车型库（SUMO）"
	exclusive = true
	min_size = Vector2i(780, 640)
	size = Vector2i(780, 680)
	theme = load("res://ui/editor_theme.tres")
	EditorApp.prep_window(self)
	_build()
	close_requested.connect(func() -> void: EditorApp._close_dialog(self))


func _build() -> void:
	var outer := VBoxContainer.new()
	outer.add_theme_constant_override("separation", UITokens.S3)
	outer.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	outer.offset_left = UITokens.S4
	outer.offset_top = UITokens.S3
	outer.offset_right = -UITokens.S4
	outer.offset_bottom = -UITokens.S3
	add_child(outer)

	var hint := Label.new()
	hint.text = "车型定义对所有进口通用。每个进口用哪几种、各占多少，在「车道 → 进口车型构成」里配。"
	hint.theme_type_variation = "HintLabel"
	hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	outer.add_child(hint)

	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	outer.add_child(scroll)

	_list = VBoxContainer.new()
	_list.add_theme_constant_override("separation", UITokens.S2)
	_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(_list)

	_empty_hint = Label.new()
	_empty_hint.text = "还没有自定义车型。下面「＋ 新建车型」加一条；不加也能导出 —— 会按关卡的「车型构成」预设自动生成。"
	_empty_hint.theme_type_variation = "HintLabel"
	_empty_hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	outer.add_child(_empty_hint)

	var bar := HBoxContainer.new()
	bar.add_theme_constant_override("separation", UITokens.S2)
	outer.add_child(bar)
	var add := Clay.btn("＋ 新建车型")
	add.pressed.connect(_on_add)
	bar.add_child(add)
	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	bar.add_child(spacer)
	var done := Clay.btn_primary("完成")
	done.pressed.connect(func() -> void: EditorApp._close_dialog(self))
	bar.add_child(done)

	rebuild()


func rebuild() -> void:
	if _list == null or not is_instance_valid(_list):
		return
	for c in _list.get_children():
		_list.remove_child(c)
		c.queue_free()

	var types: Array = SumoFleetModel.types_of(EditorApp.profile)
	_empty_hint.visible = types.is_empty()
	for i in types.size():
		_list.add_child(_type_card(i, types[i]))


func _type_card(index: int, t: Dictionary) -> Control:
	var card := Clay.inset(UITokens.S3)
	var body := VBoxContainer.new()
	body.add_theme_constant_override("separation", UITokens.S1)
	card.add_child(body)

	# --- 第一行：id / 预设 / 风格 / 删除 ---
	var row1 := HBoxContainer.new()
	row1.add_theme_constant_override("separation", UITokens.S1)
	body.add_child(row1)

	var id_edit := LineEdit.new()
	id_edit.text = String(t.get("id", ""))
	id_edit.tooltip_text = "车型 id（车流引用它，改了会同步到引用处）；同库内不能重名"
	id_edit.custom_minimum_size.x = 130
	id_edit.text_submitted.connect(func(txt: String) -> void:
		_set_field(index, "id", txt)
		rebuild())
	id_edit.focus_exited.connect(func() -> void:
		_set_field(index, "id", id_edit.text)
		rebuild())
	row1.add_child(id_edit)

	var preset := OptionButton.new()
	for k in SumoFleetModel.PRESETS.size():
		preset.add_item(SumoFleetModel.PRESET_LABELS[k])
	preset.select(maxi(SumoFleetModel.PRESETS.find(String(t.get("preset", "car"))), 0))
	preset.tooltip_text = "换预设会把该预设的默认参数填进来（之后仍可手改）"
	preset.custom_minimum_size.x = 110
	preset.item_selected.connect(func(k: int) -> void:
		_set_field(index, "preset", SumoFleetModel.PRESETS[k])
		rebuild())
	row1.add_child(preset)

	var style := OptionButton.new()
	for k in SumoFleetModel.STYLES.size():
		style.add_item(SumoFleetModel.STYLE_LABELS[k])
		style.set_item_tooltip(k, "（SUMO: sigma / tau / speedFactor）%s" % SumoFleetModel.STYLE_TIPS[k])
	style.select(maxi(SumoFleetModel.STYLES.find(String(t.get("style", "normal"))), 0))
	style.custom_minimum_size.x = 90
	style.item_selected.connect(func(k: int) -> void: _set_field(index, "style", SumoFleetModel.STYLES[k]))
	row1.add_child(style)

	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row1.add_child(spacer)

	var del := Clay.btn_ghost("删除")
	del.pressed.connect(func() -> void:
		EditorApp.begin_edit("sumo_type:del:%d" % index)
		SumoFleetModel.remove_type(EditorApp.profile, index)
		EditorApp.profile.touch()
		EditorApp.doc.dirty = true
		EditorApp.end_edit()
		rebuild())
	row1.add_child(del)

	# --- 第二行：物理参数 ---
	var row2 := HBoxContainer.new()
	row2.add_theme_constant_override("separation", UITokens.S2)
	body.add_child(row2)

	row2.add_child(_num("长度", t, index, "length", 1.0, 30.0, 0.5, "m"))
	row2.add_child(_num("限速", t, index, "max_speed", 5.0, 180.0, 1.0, "km/h", 3.6))
	row2.add_child(_num("加速", t, index, "accel", 0.1, 10.0, 0.1, "m/s²"))
	row2.add_child(_num("减速", t, index, "decel", 0.1, 15.0, 0.1, "m/s²"))
	row2.add_child(_num("最小净距", t, index, "min_gap", 0.1, 20.0, 0.1, "m"))

	# --- 第三行：颜色 + vClass 副标 ---
	var row3 := HBoxContainer.new()
	row3.add_theme_constant_override("separation", UITokens.S2)
	body.add_child(row3)

	var color := LineEdit.new()
	color.text = String(t.get("color", ""))
	color.placeholder_text = "0.85,0.85,0.90"
	color.tooltip_text = "SUMO 颜色：r,g,b（0~1）"
	color.custom_minimum_size.x = 160
	color.focus_exited.connect(func() -> void: _set_field(index, "color", color.text.strip_edges()))
	row3.add_child(color)

	var vclass := Label.new()
	var preset_key := String(t.get("preset", "car"))
	vclass.text = "SUMO 类型：%s" % String(SumoFleetModel.PRESET_VCLASS.get(preset_key, "passenger"))
	vclass.theme_type_variation = "HintLabel"
	row3.add_child(vclass)

	return card


## 一个数值输入（label + SpinBox + 单位）。`scale` = 显示值 ÷ 存储值（限速用 3.6 转 km/h）。
func _num(label_text: String, t: Dictionary, index: int, key: String,
		min_v: float, max_v: float, step: float, unit: String, scale: float = 1.0) -> Control:
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 0)

	var lab := Label.new()
	lab.text = label_text
	lab.theme_type_variation = "HintLabel"
	box.add_child(lab)

	var s := SpinBox.new()
	s.min_value = min_v
	s.max_value = max_v
	s.step = step
	s.suffix = unit
	# ★ S1f 的坑：min 必须能被 step 整除。上面这些组合都满足（5/1、0.1/0.1、1/0.5…）。
	s.value = float(t.get(key, 0.0)) * scale
	s.custom_minimum_size.x = 108
	s.value_changed.connect(func(v: float) -> void: _set_field(index, key, v / scale))
	box.add_child(s)
	return box


func _set_field(index: int, key: String, value: Variant) -> void:
	EditorApp.begin_edit("sumo_type:%d:%s" % [index, key])
	var changed := SumoFleetModel.set_type_field(EditorApp.profile, index, key, value)
	if changed:
		EditorApp.profile.touch()
		EditorApp.doc.dirty = true
	EditorApp.end_edit()


func _on_add() -> void:
	EditorApp.begin_edit("sumo_type:add:%d" % EditorApp.profile.vehicle_types.size())
	SumoFleetModel.add_type(EditorApp.profile)
	EditorApp.profile.touch()
	EditorApp.doc.dirty = true
	EditorApp.end_edit()
	rebuild()
