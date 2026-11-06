extends Window

## 导出为其他格式（Shapefile / SUMO net.xml）。
## 只做“选格式 + 选路径 + 调 EditorApp”，不直接改 Doc。

const SettingsScript := preload("res://scripts/export/export_settings.gd")

const PROJECTION_UTM := 1
const PROJECTION_WGS84 := 0
const PROJECTION_LOCAL := 2

var _settings: Dictionary = {}
var _dir_edit: LineEdit
var _name_edit: LineEdit
var _pills := {}
var _lon_edit: LineEdit
var _lat_edit: LineEdit
var _proj_opt: OptionButton
var _lane_width: SpinBox
var _cross_pill: Button
var _net_path: LineEdit
var _net_status: Label
var _hint: Label
var _summary: Label
var _results: Label
var _export_btn: Button
## S5 试跑：上次导出的 .sumocfg 路径（空 = 这次没生成，按钮置灰）
var _last_cfg: String = ""
var _run_btn: Button
var _gui_btn: Button
var _sumo_stats: Label
var _net_ok := false
var _net_version := ""

var _dir_dialog: FileDialog
var _net_dialog: FileDialog


func _ready() -> void:
	title = "导出为其他格式"
	exclusive = false
	min_size = Vector2i(720, 640)
	theme = load("res://ui/editor_theme.tres")
	EditorApp.prep_window(self)        # ★ 窗口底透明：圆角外面才不会露出深灰方块（E91 C19）
	_settings = SettingsScript.load_settings()
	_build()
	_build_dialogs()
	_load_defaults()
	close_requested.connect(_on_cancel)
	_refresh_state()


func _build() -> void:
	# ★ 不再铺 `Clay.flat()`：那是"桌面"的奶油色（SURFACE_0），会把窗口自己的圆角卡面
	#   盖成一整块**方的**奶油纸，于是它看起来不像弹窗、而是画布上的一块纸（E91 C19）。
	#   留空 = 露出 Window 主题的 SURFACE_1 圆角卡面 + 1px 描边 + 投影，与「退出前保存」等弹窗同一张皮。
	var margin := MarginContainer.new()
	margin.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	margin.add_theme_constant_override("margin_left", UITokens.S4)
	margin.add_theme_constant_override("margin_right", UITokens.S4)
	margin.add_theme_constant_override("margin_top", UITokens.S4)
	margin.add_theme_constant_override("margin_bottom", UITokens.S4)
	add_child(margin)

	# 上下两段：可滚的正文 + **钉住的操作行**。
	# ★ 以前按钮是正文的最后一行，正文 908 px 高、可视区 748 → 「导出」要滚到最底下才看得见，
	#   而它又是本窗唯一的主动作（橙底那颗），藏起来等于没有。
	var outer := VBoxContainer.new()
	outer.add_theme_constant_override("separation", UITokens.S3)
	margin.add_child(outer)

	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	outer.add_child(scroll)

	var v := VBoxContainer.new()
	v.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	v.add_theme_constant_override("separation", UITokens.S3)
	scroll.add_child(v)

	var path_card := _card("输出")
	v.add_child(path_card)
	var path_body: VBoxContainer = path_card.get_node("Body")

	_dir_edit = LineEdit.new()
	_dir_edit.placeholder_text = "输出目录"
	var dir_row := HBoxContainer.new()
	dir_row.add_theme_constant_override("separation", UITokens.S2)
	_dir_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	dir_row.add_child(_dir_edit)
	var browse_dir := Clay.btn("浏览…")
	browse_dir.pressed.connect(_on_browse_dir)
	dir_row.add_child(browse_dir)
	path_body.add_child(Clay.field_row("目录", dir_row))

	_name_edit = LineEdit.new()
	_name_edit.placeholder_text = "文件名（不含扩展名）"
	path_body.add_child(Clay.field_row("文件名", _name_edit))

	var fmt_card := _card("格式")
	v.add_child(fmt_card)
	var fmt_body: VBoxContainer = fmt_card.get_node("Body")

	var fmt_row1 := HBoxContainer.new()
	fmt_row1.add_theme_constant_override("separation", UITokens.S2)
	fmt_body.add_child(fmt_row1)
	_pills["spmap"] = _pill("原生 .spmap", true)
	_pills["shp"] = _pill("Shapefile .shp", false)
	_pills["net"] = _pill("SUMO .net.xml", false)
	_pills["rou"] = _pill("SUMO .rou.xml（车流）", false)
	_pills["add"] = _pill("SUMO .add.xml（检测设施）", false)
	# 分两行：基础格式一行、SUMO 三件套一行（5 颗挤一行会压到对话框边缘）
	for key in ["spmap", "shp"]:
		fmt_row1.add_child(_pills[key])

	var fmt_row2 := HBoxContainer.new()
	fmt_row2.add_theme_constant_override("separation", UITokens.S2)
	fmt_body.add_child(fmt_row2)
	for key in ["net", "rou", "add"]:
		fmt_row2.add_child(_pills[key])

	# 第三行：运行配置（S5）。它不是"数据文件"，独占一行更清楚。
	var fmt_row3 := HBoxContainer.new()
	fmt_row3.add_theme_constant_override("separation", UITokens.S2)
	fmt_body.add_child(fmt_row3)
	_pills["sumocfg"] = _pill("SUMO .sumocfg（一键试跑）", false)
	fmt_row3.add_child(_pills["sumocfg"])

	# ★ .rou.xml 依赖 .net.xml（路线要用 net 的 origID 把车道映射回边）→ 勾选联动，
	#   避免用户勾了车流却没生成网络，导出时才发现跑不起来。
	_pills["rou"].toggled.connect(func(pressed: bool) -> void:
		if pressed and not _pills["net"].button_pressed:
			_pills["net"].button_pressed = true
		elif not pressed and _pills["sumocfg"].button_pressed:
			_pills["sumocfg"].button_pressed = false)   # 没车流就没得试跑
	# 检测设施同理：add.xml 的 lane 要用 net.xml 的映射，没有 net 就写不出来
	_pills["add"].toggled.connect(func(pressed: bool) -> void:
		if pressed and not _pills["net"].button_pressed:
			_pills["net"].button_pressed = true)
	# ★ .sumocfg 同时依赖 net + rou（试跑要有网络、有车流）→ 勾它就把两个都带上
	_pills["sumocfg"].toggled.connect(func(pressed: bool) -> void:
		if not pressed:
			return
		if not _pills["net"].button_pressed:
			_pills["net"].button_pressed = true
		if not _pills["rou"].button_pressed:
			_pills["rou"].button_pressed = true)
	_pills["net"].toggled.connect(func(pressed: bool) -> void:
		if not pressed:
			if _pills["rou"].button_pressed:
				_pills["rou"].button_pressed = false
			if _pills["add"].button_pressed:
				_pills["add"].button_pressed = false
			if _pills["sumocfg"].button_pressed:
				_pills["sumocfg"].button_pressed = false
		_refresh_state())

	var note := Label.new()
	note.text = "所有外部格式都从同一份已校验的 .spmap 派生；校验有 Error 时任何格式都不落盘。"
	note.theme_type_variation = "HintLabel"
	note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	fmt_body.add_child(note)

	var geo_card := _card("地理参考（SHP 必填）")
	v.add_child(geo_card)
	var geo_body: VBoxContainer = geo_card.get_node("Body")

	var lon_lat := HBoxContainer.new()
	lon_lat.add_theme_constant_override("separation", UITokens.S2)
	_lon_edit = LineEdit.new()
	_lon_edit.placeholder_text = "经度，如 116.397"
	_lon_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_lat_edit = LineEdit.new()
	_lat_edit.placeholder_text = "纬度，如 39.909"
	_lat_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	lon_lat.add_child(Clay.field_row("经度", _lon_edit))
	lon_lat.add_child(Clay.field_row("纬度", _lat_edit))
	# ★ HBox 的子节点默认只有 FILL（按最小宽），两个 field_row 不撑开 → 输入框只有 80 px 宽、
	#   右边空一大片（2026-10-28 实测）。两行各占一半。
	for c in lon_lat.get_children():
		(c as Control).size_flags_horizontal = Control.SIZE_EXPAND_FILL
	geo_body.add_child(lon_lat)

	_proj_opt = OptionButton.new()
	_proj_opt.add_item("UTM（米）", PROJECTION_UTM)
	_proj_opt.add_item("WGS84（经纬度）", PROJECTION_WGS84)
	_proj_opt.add_item("本地米（仅 SHP）", PROJECTION_LOCAL)
	geo_body.add_child(Clay.field_row("坐标系", _proj_opt))

	_cross_pill = _pill("输出人行横道图层", true)
	geo_body.add_child(_cross_pill)

	var sumo_card := _card("SUMO")
	v.add_child(sumo_card)
	var sumo_body: VBoxContainer = sumo_card.get_node("Body")

	var net_row := HBoxContainer.new()
	net_row.add_theme_constant_override("separation", UITokens.S2)
	_net_path = LineEdit.new()
	_net_path.placeholder_text = "netconvert.exe 路径（留空则自动检测）"
	_net_path.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_net_path.text_changed.connect(_on_net_path_changed)
	net_row.add_child(_net_path)
	var browse_net := Clay.btn("浏览…")
	browse_net.pressed.connect(_on_browse_net)
	net_row.add_child(browse_net)
	var detect_btn := Clay.btn("检测")
	detect_btn.pressed.connect(_on_detect)
	net_row.add_child(detect_btn)
	sumo_body.add_child(Clay.field_row("netconvert", net_row))

	_net_status = Label.new()
	_net_status.text = "尚未检测"
	_net_status.theme_type_variation = "HintLabel"
	_net_status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	sumo_body.add_child(_net_status)

	# ★ 车道宽 2026-11-06 从「地理参考」移到 SUMO 卡（老板指出位置上不对）：
	#   它不是地理投影参数，是**路网几何**参数 —— 决定 OpenDRIVE 里每条车道的宽度，
	#   进而决定 net.xml 的 lane width 与 SHP 车道图层。跟 netconvert 放一起才讲得通。
	_lane_width = SpinBox.new()
	_lane_width.min_value = 2.0
	_lane_width.max_value = 6.0
	_lane_width.step = 0.1
	_lane_width.suffix = " m"
	_lane_width.value = 3.5
	_lane_width.tooltip_text = "每条车道的宽度。影响 .net.xml 的 lane width 与 SHP 的车道图层；" \
		+ "不影响 .spmap（游戏侧的通行能力口径）"
	sumo_body.add_child(Clay.field_row("车道宽", _lane_width))

	var download := Clay.btn_ghost("如何安装 SUMO / 下载 netconvert")
	download.pressed.connect(func() -> void:
		OS.shell_open("https://sumo.dlr.de/docs/Downloads.html"))
	sumo_body.add_child(download)

	var sumo_note := Label.new()
	sumo_note.text = "生成 .net.xml 后请直接加载 .net.xml；不要在 SUMO 里重新生成路口，否则会重建路口与转向。" \
		+ "\n专业配时（单套方案）会直接写进 .net.xml，加载 .net.xml 即生效，也能在 netedit 里继续改。" \
		+ "\n只有配了多套方案时才会另生成 .tll.xml —— 那种情况才需要 --additional-files 加载它。"
	sumo_note.theme_type_variation = "HintLabel"
	sumo_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	sumo_body.add_child(sumo_note)

	_hint = Label.new()
	_hint.theme_type_variation = "HintLabel"
	_hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	# ★ 这一行只写「为什么现在不能导出」，属于拦人的话 → 用语义红（和属性面板的红字冲突提示同一个色），
	#   原来跟普通灰提示同色，读者会把它当说明扫过去。
	_hint.add_theme_color_override("font_color", UITokens.CONFLICT)
	v.add_child(_hint)

	_summary = Label.new()
	_summary.theme_type_variation = "MutedLabel"
	_summary.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	v.add_child(_summary)

	_results = Label.new()
	_results.theme_type_variation = "HintLabel"
	_results.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_results.custom_minimum_size = Vector2(0, 56)
	v.add_child(_results)

	# ★ S5 一键试跑：只有这次导出真的产出了 .sumocfg 才可用（见 _show_results）
	var run_bar := HBoxContainer.new()
	run_bar.add_theme_constant_override("separation", UITokens.S2)
	v.add_child(run_bar)
	_run_btn = Clay.btn("▶ 试跑（只出统计）")
	_run_btn.tooltip_text = "按「SUMO 运行配置」的时长跑一次 sumo，回统计（不开窗口）"
	_run_btn.disabled = true
	_run_btn.pressed.connect(_on_run_sumo)
	run_bar.add_child(_run_btn)
	_gui_btn = Clay.btn("用 sumo-gui 打开")
	_gui_btn.tooltip_text = "用 SUMO 图形界面打开这次导出的 .sumocfg（看车怎么走）"
	_gui_btn.disabled = true
	_gui_btn.pressed.connect(_on_open_gui)
	run_bar.add_child(_gui_btn)

	_sumo_stats = Label.new()
	_sumo_stats.theme_type_variation = "HintLabel"
	_sumo_stats.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_sumo_stats.visible = false
	v.add_child(_sumo_stats)

	var buttons := HBoxContainer.new()
	buttons.alignment = BoxContainer.ALIGNMENT_END
	buttons.add_theme_constant_override("separation", UITokens.S2)
	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	buttons.add_child(spacer)
	var cancel := Clay.btn("取消")
	cancel.pressed.connect(_on_cancel)
	buttons.add_child(cancel)
	_export_btn = Clay.btn_primary("导出")
	_export_btn.pressed.connect(_on_export)
	buttons.add_child(_export_btn)
	outer.add_child(Clay.sep())        # 正文与操作行之间一道发丝线（R0 §1.6：分区间距用分隔线收口）
	outer.add_child(buttons)


func _card(title_text: String) -> PanelContainer:
	var card := Clay.inset(UITokens.S3)
	var body := VBoxContainer.new()
	body.name = "Body"
	body.add_theme_constant_override("separation", UITokens.S2)
	var label := Label.new()
	label.text = title_text
	label.theme_type_variation = "TitleLabel"
	body.add_child(label)
	card.add_child(body)
	return card


func _pill(text: String, on: bool) -> Button:
	var b := Clay.pill(text, on)
	b.toggled.connect(func(_pressed: bool) -> void: _refresh_state())
	return b


func _build_dialogs() -> void:
	_dir_dialog = FileDialog.new()
	EditorApp.prep_window(_dir_dialog)         # ★ 圆角外不露深灰（E91 C19）
	_dir_dialog.access = FileDialog.ACCESS_FILESYSTEM
	_dir_dialog.file_mode = FileDialog.FILE_MODE_OPEN_DIR
	_dir_dialog.title = "选择输出目录"
	_dir_dialog.exclusive = false
	_dir_dialog.dir_selected.connect(func(path: String) -> void:
		_dir_edit.text = path
		_refresh_state())
	add_child(_dir_dialog)

	_net_dialog = FileDialog.new()
	EditorApp.prep_window(_net_dialog)         # ★ 圆角外不露深灰（E91 C19）
	_net_dialog.access = FileDialog.ACCESS_FILESYSTEM
	_net_dialog.file_mode = FileDialog.FILE_MODE_OPEN_FILE
	_net_dialog.title = "选择 netconvert.exe"
	_net_dialog.filters = PackedStringArray(["netconvert.exe ; netconvert", "*.exe ; 可执行文件"])
	_net_dialog.exclusive = false
	_net_dialog.file_selected.connect(func(path: String) -> void:
		_net_path.text = path
		_on_detect())
	add_child(_net_dialog)


func _load_defaults() -> void:
	_dir_edit.text = String(_settings.get("output_dir", ""))
	if _dir_edit.text.strip_edges() == "":
		if EditorApp.current_path != "":
			_dir_edit.text = EditorApp.current_path.get_base_dir()
		else:
			_dir_edit.text = OS.get_system_dir(OS.SYSTEM_DIR_DOCUMENTS)

	_name_edit.text = String(_settings.get("filename", ""))
	if _name_edit.text.strip_edges() == "":
		if EditorApp.current_path != "":
			_name_edit.text = EditorApp.current_path.get_file().get_basename()
		else:
			_name_edit.text = _sanitize(EditorApp.doc.meta.get("name", "map"))

	for key in _pills.keys():
		_pills[key].button_pressed = bool(_settings.get(key, key == "spmap"))

	var geo: Dictionary = _settings.get("geo", {})
	var lon = geo.get("lon", null)
	var lat = geo.get("lat", null)
	_lon_edit.text = "" if lon == null else str(lon)
	_lat_edit.text = "" if lat == null else str(lat)
	var proj := int(geo.get("projection", PROJECTION_UTM))
	_select_option_id(_proj_opt, proj)

	_lane_width.value = float(_settings.get("lane_width_m", 3.5))
	_cross_pill.button_pressed = true

	_net_path.text = String(_settings.get("netconvert_path", ""))
	_on_detect()


func _select_option_id(opt: OptionButton, id: int) -> void:
	for i in opt.item_count:
		if opt.get_item_id(i) == id:
			opt.select(i)
			return


func _sanitize(value: String) -> String:
	var text := value.strip_edges()
	for ch in ["\\", "/", ":", "*", "?", "\"", "<", ">", "|"]:
		text = text.replace(ch, "_")
	if text == "":
		text = "map"
	return text


func _output_base() -> String:
	var dir := _dir_edit.text.strip_edges()
	var name := _name_edit.text.strip_edges()
	if name.to_lower().ends_with(".spmap"):
		name = name.substr(0, name.length() - 6)
	return dir.path_join(name)


func _geo_values() -> Dictionary:
	var lon_text := _lon_edit.text.strip_edges()
	var lat_text := _lat_edit.text.strip_edges()
	if lon_text == "" or lat_text == "":
		return {"ok": false, "lon": null, "lat": null, "error": "请填写地理原点经纬度。"}
	if not lon_text.is_valid_float() or not lat_text.is_valid_float():
		return {"ok": false, "lon": null, "lat": null, "error": "经纬度必须是数字。"}
	var lon := float(lon_text)
	var lat := float(lat_text)
	if lon < -180.0 or lon > 180.0:
		return {"ok": false, "lon": null, "lat": null, "error": "经度必须在 [-180, 180]。"}
	if lat < -85.0 or lat > 85.0:
		return {"ok": false, "lon": null, "lat": null, "error": "纬度必须在 [-85, 85]。"}
	return {"ok": true, "lon": lon, "lat": lat, "error": ""}


func _refresh_state() -> void:
	var problems: Array[String] = []
	var formats := _selected_formats()
	if formats.is_empty():
		problems.append("至少选一种导出格式。")

	if _dir_edit.text.strip_edges() == "":
		problems.append("输出目录不能为空。")
	if _name_edit.text.strip_edges() == "":
		problems.append("文件名不能为空。")

	var need_geo := bool(formats.get("shp", false))
	var geo := _geo_values()
	if need_geo and not bool(geo.ok):
		problems.append(String(geo.error))

	if bool(formats.get("net", false)):
		if not _net_ok:
			problems.append("未检测到 netconvert，无法生成 .net.xml。")
		elif _net_path.text.strip_edges() != "" and not FileAccess.file_exists(_net_path.text.strip_edges()):
			problems.append("netconvert 路径不存在。")

	_hint.text = "\n".join(PackedStringArray(problems))
	_hint.visible = not problems.is_empty()          # 没问题就别占一行高（窗口本来就挤）
	_export_btn.disabled = not problems.is_empty()
	_summary.text = "将生成：" + _describe_outputs(formats)


func _selected_formats() -> Dictionary:
	var out := {}
	for key in _pills.keys():
		if _pills[key].button_pressed:
			out[key] = true
	return out


func _describe_outputs(formats: Dictionary) -> String:
	var base := _output_base()
	var parts: Array[String] = []
	if formats.has("spmap"):
		parts.append(base + ".spmap")
	if formats.has("shp"):
		parts.append(base + "_roads/lanes/nodes.shp")
	if formats.has("rou"):
		parts.append(base + ".rou.xml（车型 / 车流 / 行人）")
	if formats.has("add"):
		parts.append(base + ".add.xml（检测器 / 公交站 / 停车场）")
	if formats.has("sumocfg"):
		parts.append(base + ".sumocfg（一键试跑）")
	if formats.has("net"):
		parts.append(base + ".net.xml")
		# ★ 只有**多套**方案才会另出 .tll.xml；单套方案已经写进 net.xml 的 programID="0"
		if _has_multi_program_tls():
			parts.append(base + ".tll.xml")
	return "、".join(PackedStringArray(parts)) if not parts.is_empty() else "（尚未选择）"


## 是否存在**多套**方案的信号路口（只有多套才会另出 .tll.xml）。
## ★ 单套方案由 C# 侧直接写进 net.xml 的 programID="0"（2026-11-04 调整）——
##   两套程序并存时 netedit 只显示网络自带那套，用户在那边根本看不到自己的配时。
func _has_multi_program_tls() -> bool:
	for row in EditorApp.profile.tls:
		var programs: Array = row.get("programs", [])
		if programs.size() >= 2:
			return true
	return false


func _on_browse_dir() -> void:
	_dir_dialog.popup_centered_ratio(0.55)


func _on_browse_net() -> void:
	_net_dialog.popup_centered_ratio(0.55)


func _on_net_path_changed(_text: String) -> void:
	_net_ok = false
	_net_status.text = "路径已改，请点“检测”。"
	_net_status.add_theme_color_override("font_color", UITokens.INK_2)
	_refresh_state()


func _on_detect() -> void:
	var hint := _net_path.text.strip_edges()
	var result: Dictionary = EditorApp.detect_netconvert(hint)
	if bool(result.get("ok", false)):
		var found := String(result.get("path", ""))
		if hint == "" and found != "":
			_net_path.text = found
		_net_ok = true
		_net_version = String(result.get("version", ""))
		_net_status.text = "已检测到：" + (_net_version if _net_version != "" else found)
		_net_status.add_theme_color_override("font_color", UITokens.INK_1)   # ★ 成功用主文字色，不再是灰的
	else:
		_net_ok = false
		_net_version = ""
		_net_status.text = "未检测到 netconvert：" + String(result.get("error", ""))
		_net_status.add_theme_color_override("font_color", UITokens.CONFLICT)  # ★ 失败用语义红
	_refresh_state()


func _on_export() -> void:
	if _export_btn.disabled:
		return

	var geo := _geo_values()
	var proj := _proj_opt.get_selected_id()
	var opts := {
		"spmap": _pills["spmap"].button_pressed,
		"shp": _pills["shp"].button_pressed,
		"net": _pills["net"].button_pressed,
		"rou": _pills["rou"].button_pressed,
		"add": _pills["add"].button_pressed,
		"sumocfg": _pills["sumocfg"].button_pressed,
		"outputBase": _output_base(),
		"originLon": geo.lon if bool(geo.ok) else null,
		"originLat": geo.lat if bool(geo.ok) else null,
		"projection": proj,
		"laneWidthM": _lane_width.value,
		"includeCrosswalks": _cross_pill.button_pressed,
		"netconvertPath": _net_path.text.strip_edges(),
		"netconvertSearchDir": ProjectSettings.globalize_path("res://../Tools/sumo/bin"),
		"yellowDuration": 3.0,
		"allRedDuration": 2.0,
	}

	_settings["output_dir"] = _dir_edit.text.strip_edges()
	_settings["filename"] = _name_edit.text.strip_edges()
	_settings["spmap"] = opts.spmap
	_settings["shp"] = opts.shp
	_settings["net"] = opts.net
	_settings["rou"] = opts.rou
	_settings["sumocfg"] = opts.sumocfg
	_settings["geo"] = {
		"lon": opts.originLon,
		"lat": opts.originLat,
		"projection": proj,
	}
	_settings["lane_width_m"] = _lane_width.value
	_settings["netconvert_path"] = opts.netconvertPath
	SettingsScript.save_settings(_settings)

	_export_btn.disabled = true
	_export_btn.text = "正在导出…"
	_results.text = ""
	await get_tree().process_frame

	var result: Dictionary = EditorApp.export_with_options(opts)
	_show_results(result)
	_export_btn.text = "再次导出"
	_refresh_state()


func _show_results(result: Dictionary) -> void:
	var lines: Array[String] = []
	var spmap_selected := bool(result.get("spmap_selected", _pills["spmap"].button_pressed))
	var external_selected := bool(result.get("external_selected",
		_pills["shp"].button_pressed or _pills["net"].button_pressed))
	var spmap_ok := bool(result.get("spmap_ok", true))
	var external_ok := bool(result.get("external_ok", true))

	var spmap_errors := _error_items(result.get("spmap_errors", []))
	var external_errors := _error_items(result.get("errors", []))
	var game_only_errors := _error_items(result.get("game_only", []))

	if spmap_selected and not spmap_errors.is_empty():
		lines.append("原生 .spmap 未写出：%d 条校验错误。" % spmap_errors.size())
		_append_error_lines(lines, spmap_errors)

	# spmap 已失败时，外部格式的结构错误与它同源，不再重复列一遍。
	if external_selected and not external_errors.is_empty() and (not spmap_selected or spmap_ok):
		lines.append("外部格式未写出：%d 条结构校验错误。" % external_errors.size())
		_append_error_lines(lines, external_errors)

	if external_selected and external_ok and not game_only_errors.is_empty() and (not spmap_selected or spmap_ok):
		lines.append("游戏扩展项校验未通过，已忽略（不影响外部格式）：")
		_append_error_lines(lines, game_only_errors)

	# ★ 每次导出先把试跑按钮复位：只有这次真的产出了 .sumocfg 才重新点亮
	_last_cfg = ""
	if _run_btn != null:
		_run_btn.disabled = true
		_run_btn.text = "▶ 试跑（只出统计）"
	if _gui_btn != null:
		_gui_btn.disabled = true
	if _sumo_stats != null:
		_sumo_stats.visible = false
		_sumo_stats.text = ""

	var results: Array = result.get("results", [])
	for r in results:
		if not (r is Dictionary):
			continue
		var ok := bool(r.get("ok", false))
		var fmt := String(r.get("format", "?"))
		if fmt == "sumocfg" and ok:
			_last_cfg = String(r.get("path", ""))
		if ok:
			lines.append("✓ %s → %s" % [fmt, String(r.get("path", ""))])
			if fmt == "shp":
				lines.append("   同目录同名生成 .shx/.dbf/.prj/.cpg；请勿只复制 .shp。")
		else:
			lines.append("✗ %s：%s" % [fmt, String(r.get("error", ""))])
		for w in r.get("warnings", []):
			lines.append("   ! " + String(w))
	if lines.is_empty():
		lines.append("没有生成任何文件。")
	_results.text = "\n".join(PackedStringArray(lines))
	# ★ 结果行的颜色按"最坏的一条"定：有 ✗ / 未写出 → 语义红；全是 ✓ → 主文字色（不再一律灰）。
	var failed := false
	for line in lines:
		if line.contains("✗") or line.contains("未写出"):
			failed = true
			break
	_results.add_theme_color_override("font_color",
		UITokens.CONFLICT if failed else UITokens.INK_1)
	for line in lines:
		print("[导出] " + line)

	if _last_cfg != "":
		if _run_btn != null:
			_run_btn.disabled = false
		if _gui_btn != null:
			_gui_btn.disabled = false
		if _sumo_stats != null:
			_sumo_stats.visible = true
			_sumo_stats.text = "生成好了 —— 可以点「▶ 试跑」看统计，或用 sumo-gui 打开看车。"
			_sumo_stats.add_theme_color_override("font_color", UITokens.INK_2)


func _error_items(raw: Variant) -> Array:
	var out: Array = []
	if not (raw is Array):
		return out
	for e in raw:
		if e is Dictionary and String(e.get("Severity", "")) == "Error":
			out.append(e)
	return out


func _append_error_lines(lines: Array[String], items: Array) -> void:
	for e in items:
		if e is Dictionary:
			lines.append("  [%s] %s @ %s" % [
				String(e.get("Code", "?")),
				String(e.get("Message", "")),
				String(e.get("Path", "")),
			])


# --- S5 一键试跑 ---------------------------------------------------------

## 跑一次 sumo（后台线程 + 轮询），回统计。**不阻塞界面**。
func _on_run_sumo() -> void:
	if _last_cfg == "":
		return
	_run_btn.disabled = true
	_run_btn.text = "正在跑…"
	_gui_btn.disabled = true
	_sumo_stats.visible = true
	_sumo_stats.text = "正在跑 sumo（%s）…" % EditorApp.current_path.get_file()
	_sumo_stats.add_theme_color_override("font_color", UITokens.INK_2)

	var r: Dictionary = await EditorApp.run_sumo_and_wait(_last_cfg)

	_run_btn.text = "▶ 再跑一次"
	_run_btn.disabled = false
	_gui_btn.disabled = false
	if bool(r.get("ok", false)):
		_sumo_stats.text = EditorApp.sumo_stats_text(r)
		_sumo_stats.add_theme_color_override("font_color", UITokens.INK_1)
	else:
		var msg := "✗ 试跑失败：" + String(r.get("error", "未知原因"))
		var tail := String(r.get("tail", ""))
		if tail != "":
			msg += "\n" + tail.substr(maxi(tail.length() - 600, 0))
		_sumo_stats.text = msg
		_sumo_stats.add_theme_color_override("font_color", UITokens.CONFLICT)
		print("[导出] " + msg)


## 用 sumo-gui 打开（不等窗口关闭；后面怎么看由 SUMO 自己管）
func _on_open_gui() -> void:
	if _last_cfg == "":
		return
	var r: Dictionary = EditorApp.open_sumo_gui(_last_cfg)
	if bool(r.get("ok", false)):
		print("[导出] sumo-gui 已打开：" + _last_cfg)
		EditorApp.toast("已用 sumo-gui 打开（关掉窗口即结束）", "info")
		return
	var msg := "打不开 sumo-gui：" + String(r.get("error", ""))
	print("[导出] " + msg)
	EditorApp.toast(msg, "error")
	_sumo_stats.visible = true
	_sumo_stats.text = msg
	_sumo_stats.add_theme_color_override("font_color", UITokens.CONFLICT)


func _on_cancel() -> void:
	hide()
	queue_free()
