extends VBoxContainer

## 属性面板：显示当前选中【路】的模板 / 【连接】的类型与分流比 / 【路口】的相位 / 【装饰】与【车道】的属性；
## 什么都没选时显示关卡设置。
##
## ★ 重建时机有两条，别再加第三条：
##   ① `EditorApp.selection_changed`（点选 / 框选 / 换模式 / 换文档）→ 立刻重建；
##   ② `EditorApp.content_changed`（0.25s 轮询 + 0.08s 防抖，统一收在 EditorApp）→ **指纹变了才重建**。
##      绝不无条件跟着 ② 重建：那会把用户正拖着 / 正输入的 SpinBox、LineEdit 拆掉（焦点丢失、
##      撤销步被打断）。指纹只放"结构性"信息（对象还在不在、路口有哪些 turn），
##      不放坐标 / 模板 / 相位时长。
##
## ★ 选中项被级联删掉时（如改车道数把当前选中的连接删了）一律**当作没选**，见 `_panel_target()`。
##
## ★ P3-1 结构（2026-10-27）：原来是 2 列 `GridContainer`，列宽按"各列最大最小宽"自动分，
##   方案行/阶段块一宽就把字段名挤到右边去（E91 C17）。现在换成**单列 VBox + 每行一个 `field_row`**：
##   字段名固定 `UITokens.FIELD_W`、值占满剩余宽，行内自洽，永远对得齐。
##   面板正文按小节分组（`_section()`），小节头可点折叠，折叠状态存 `user://ui_panel_folds.json`。
##   **小节只是把现有行分成段，不改变行的先后顺序**（改顺序会影响肌肉记忆，见 UI-R1 §2 的 P3 红线）。
##
## ★ P5 例外：配时区的冲突查询慢（大图 ~600 ms），**不放进重建流程** —— 先建好面板与占位行
##   「正在计算冲突…」，两帧后再查，算完**就地改文本**（`_fill_conflicts_later()`，README 铁律 25）。

const TURN_TYPES := ["Straight", "Left", "Right", "UTurn", "Crosswalk"]   ## 写进文件的枚举值
const TURN_LABELS := ["直行", "左转", "右转", "掉头", "人行横道"]          ## 面板显示用中文，与上表按索引对应
## 专业模式的逐灯位配时矩阵（内嵌在「信号灯配时」小节里，不做浮窗）
const SumoTlsPanelScript := preload("res://scripts/ui/sumo_tls_panel.gd")

var _rev_spin: SpinBox
var _sum_label: Label
var _offset_spin: SpinBox        ## 配时区：相位差（改 duration 时上限要跟着周期走）
var _phase_edit_open := false    ## 配时区：这次输入是否已经记过撤销快照
var _open_stage := ""            ## 配时区：当前展开的阶段（key = "方案-阶段"，空 = 全收起）
var _stage_doc: Doc = null       ## 配时区：展开状态属于哪份文档
var _plan_warn := ""             ## 配时区：阶段数切换失败的提示（换文档/换路口时清空）
var _stage_nid := -1             ## 配时区：展开状态属于哪个节点
var _fingerprint := ""           ## 面板"显示内容"的指纹：content_changed 来了先比它，没变就不重建
var _plan_time_label: Label = null ## 配时区·时变配时：方案时刻重复提示（改 start_time 就地刷新，不重建面板）
var _anchor_warn: Label = null   ## 关卡设置·锚点区：方向/标定提示（改一下就地刷新文本，不重建面板）
var _weights_sum: Label = null   ## 关卡设置·权重区：合计提示（同上）
## ★ E91 C18：配时区的相位条与需求折线是"重建时算一次"的图，而时长/流量**故意不进指纹**
##   （进了就会在拖 SpinBox 时把面板拆掉）。所以写回之后必须自己把图刷一遍 —— 这里存的就是那几张图。
var _fade_tw: Tween = null       ## P4-2：换选中对象时的淡入补间（连点要先 kill 旧的）
var _stage_bars: Array = []      ## [{bar: TimelineBar, plan: int, node: int}]
var _clash_status: Label = null      ## 配时区「冲突检查」状态行（P5 加载态：先"正在计算…"再填结果）
var _clash_labels: Dictionary = {}   ## stage_key → {label: Label, st: Dictionary}（冲突红字，异步回填）
var _clash_node: int = -1            ## 这份冲突数据属于哪个路口（换选中后旧协程不许回填）
var _cycle_label: Label = null   ## 配时区「周期」那一行（时长改了它也跟着变）
var _curve_prevs: Array = []     ## [{prev: CurvePreview, kind: String, key: int}]

var _body: VBoxContainer = null  ## 当前小节的正文容器；null = 还没开小节，行直接挂在根上
var _section_bodies: Dictionary = {}  ## 折叠键 → 正文 VBox（重建时清空）
var _section_heads: Dictionary = {}   ## 折叠键 → 头部 Button
var _section_titles: Dictionary = {}  ## 折叠键 → 显示标题（折叠回写 head.text 用）

## 小节折叠状态（标题 → bool）。★ 标题当键 = **跨对象类型共用**：
## 在大纲里把「基本信息」收起来，切到道路面板它也还是收着的（同一件事只教一次）。
## 与坞的三段折叠不是一回事（那份在 `ui_layout.json`，定长数组），别混。
static var _fold_state: Dictionary = {}


func _ready() -> void:
	# 单列：一行一个 field_row。绝不能再回到 2 列 GridContainer —— 列宽会随内容跳（E91 C17）
	add_theme_constant_override("separation", UITokens.S2)
	if _fold_state.is_empty():
		_fold_state = LayoutStore.load_folds()
	EditorApp.selection_changed.connect(_rebuild)
	EditorApp.content_changed.connect(_on_content_changed)   # ★ B4：级联删除后别停在已删对象上
	# ★ 能力级切换会改变面板内容（专业模式多「专业配时」入口）→ 延后重建
	EditorApp.edition_changed.connect(func(_level: int) -> void: _refresh_later())
	_rebuild()

## 内容变了（来源见 EditorApp.content_changed 的注释）：指纹没变就什么都不做
func _on_content_changed() -> void:
	if _fingerprint_of(_panel_target()) == _fingerprint:
		return
	_refresh_later()          # ★ 延后一拍，别在被销毁的控件回调里重建面板

## 面板该显示谁 —— **唯一裁决处**（`_rebuild` 与指纹都走它，避免两处判断漂移）
## ★ 选中项在 doc 里已经不存在时**跳过它**（B4）：级联删除 / 撤销之后别停在死 id 上，
##   往下落到还活着的那个选中项；一个都没有就显示关卡设置。
func _panel_target() -> Dictionary:
	var doc: Doc = EditorApp.doc
	if EditorApp.selection_roads().size() > 1:
		return {"kind": "multi", "id": -1}          # ★ 多选（框选路）：批量改限速 / 套模板 / 删除
	if EditorApp.traffic_turn >= 0 and not doc.find_turn(EditorApp.traffic_turn).is_empty():
		return {"kind": "traffic_turn", "id": EditorApp.traffic_turn}
	if EditorApp.traffic_lane >= 0 and not doc.find_lane(EditorApp.traffic_lane).is_empty():
		return {"kind": "traffic_lane", "id": EditorApp.traffic_lane}
	if EditorApp.selected_deco >= 0 and not doc.find_deco(EditorApp.selected_deco).is_empty():
		return {"kind": "deco", "id": EditorApp.selected_deco}
	if EditorApp.selected_island >= 0 and not doc.find_island(EditorApp.selected_island).is_empty():
		return {"kind": "island", "id": EditorApp.selected_island}
	if EditorApp.selected_turn >= 0 and not doc.find_turn(EditorApp.selected_turn).is_empty():
		return {"kind": "turn", "id": EditorApp.selected_turn}
	if EditorApp.selected_junction >= 0 and not doc.find_node(EditorApp.selected_junction).is_empty():
		return {"kind": "junction", "id": EditorApp.selected_junction}
	if EditorApp.selected_road >= 0 and not doc.find_road(EditorApp.selected_road).is_empty():
		return {"kind": "road", "id": EditorApp.selected_road}
	# ★ 车辆模式下什么都没选 → 显示「车辆总览」（公交线路 / 车型库 / 宏观数字）。
	#   公交线路是**跨道路**的，挂在某条路上本身就说不清；总览里能看到全部线路。
	#   基础模式仍给关卡设置（总览里的东西全是专业功能）。
	if EditorApp.mode == EditorMode.Kind.TRAFFIC and EditorApp.is_pro():
		return {"kind": "traffic_overview", "id": -1}
	return {"kind": "level", "id": -1}              # ★ 什么都没选 → 显示关卡设置

## 面板"显示内容"的指纹：只放会影响面板文字的结构性信息。
##   ★ 放模板 / 相位时长 / 坐标 = 每次微调都重建 → 正在拖的控件被拆掉，绝不能这么干。
func _fingerprint_of(target: Dictionary) -> String:
	var kind := String(target["kind"])
	if kind == "level":
		# ★ 关卡面板要显示的"外部状态"只有三样：**当前文件路径**、**规模**、两个原始字段的条数。
		#   路径以前没进指纹 → 从空关卡打开一份文件后，这一行还写着「（未保存）」（P5 复查抓到）。
		# ★ 2026-11-06 修：原来这里直接塞 `EditorApp.content_signature()`，而它含
		#   `profile.revision` —— 于是**勾一个 SUMO 开关 / 拖一下运行配置，整块面板就重建一次**
		#   （老板看到的"每次调整右侧栏都闪一下"）。那些值都由控件自己显示，根本不需要重建。
		#   现在只放"这一屏真的印出来的结构性信息"。
		var d: Doc = EditorApp.doc
		var ev_n := 0
		var tut_n := 0
		if d.game_rules.get("events") is Array:
			ev_n = (d.game_rules["events"] as Array).size()
		if d.game_rules.get("tutorial") is Array:
			tut_n = (d.game_rules["tutorial"] as Array).size()
		return "level|%s|%d|%d|%d|%d|%d|%d" % [
			EditorApp.current_path,
			d.roads.size(), d.nodes.size(), d.lanes.size(), d.turns.size(),
			ev_n, tut_n]
	if kind == "traffic_overview":
		# ★ 总览里的数字与列表都来自专业档 → 车型库 / 线路 / 站台 / 需求车道的集合变了要重建。
		#   只放 id 集合（不放间隔/份额）：那些改了不用重建，正在编辑的控件要留住。
		var vt_ids: Array = []
		for vt in EditorApp.profile.vehicle_types:
			vt_ids.append(String(vt.get("id", "")))
		vt_ids.sort()
		var line_ids: Array = []
		for b in EditorApp.profile.bus_lines:
			line_ids.append(String(b.get("id", "")))
		line_ids.sort()
		var stop_ids: Array = []
		for f in EditorApp.profile.additional:
			if String(f.get("kind", "")) == SumoFacilitiesModel.KIND_BUS_STOP:
				stop_ids.append(String(f.get("id", "")))
		stop_ids.sort()
		var demand_lanes: Array = []
		for d in EditorApp.doc.demand:
			demand_lanes.append(int(d.get("lane", -1)))
		demand_lanes.sort()
		# ★ 不把 revision 放进来：那会让每次改间隔都重建面板，正在编辑的 SpinBox 被拆掉。
		return "ov|%s|%s|%s|%s" % [
			",".join(PackedStringArray(vt_ids)), ",".join(PackedStringArray(line_ids)),
			",".join(PackedStringArray(stop_ids)), str(demand_lanes)]
	if kind == "multi":
		var ids: Array = EditorApp.selection_roads().duplicate()
		ids.sort()
		return "multi|%s" % str(ids)
	if kind == "island":
		# ★ 岛面板会列"所属过街的段"并据此判 2 段警告（2026-10-25 N6b）：
		#   段集合变了必须重建，否则删了一段后红字警告永远不出现（同路口面板的道理）。
		#   容量/坐标**不进**指纹 —— 那些改了要留住正在编辑的 SpinBox。
		var ids: Array = []
		for t in EditorApp.doc.turns:
			if int(t.get("island", -1)) == int(target["id"]):
				ids.append(int(t.id))
		ids.sort()
		return "island|%d|%s" % [int(target["id"]), str(ids)]
	if kind == "traffic_lane" or kind == "traffic_turn":
		# ★ 车型下拉的**选项**来自车型库 → 库的 id 列表变了必须重建面板。
		#   只放 id（不放长度/份额）：那些改了不用重建，正在编辑的控件要留住。
		var ids: Array = []
		for vt in EditorApp.profile.vehicle_types:
			ids.append(String(vt.get("id", "")))
		ids.sort()
		# ★ 本车道的检测设施也要跟（新增/删除 → 重建；改数值不用重建，编辑器控件要留住）
		var fac: Array = []
		for f in EditorApp.profile.additional:
			if int(f.get("lane", -1)) == int(target["id"]):
				fac.append(String(f.get("id", "")))
		fac.sort()
		# ★ 从本车道发出的公交线路（新建/删除 → 重建；改间隔/勾站不用重建）
		var buses: Array = []
		for b in EditorApp.profile.bus_lines:
			if int(b.get("lane", -1)) == int(target["id"]):
				buses.append(String(b.get("id", "")))
		buses.sort()
		return "%s|%d|%s|%s|%s" % [kind, int(target["id"]),
			",".join(PackedStringArray(ids)), ",".join(PackedStringArray(fac)),
			",".join(PackedStringArray(buses))]
	if kind != "junction":
		return "%s|%d" % [kind, int(target["id"])]
	# 路口面板会列"该路口连接"和相位摘要（摘要里全是 turn id）→ 这个节点的 turn 集合变了就必须重建
	var doc: Doc = EditorApp.doc
	var nid := int(target["id"])
	var tids: Array = []
	for t in doc.turns:
		if int(t.node) == nid:
			tids.append(int(t.id))
	# ★ 方案结构（几个方案、每个几段）也要进指纹：加/删方案会增删整块行。
	#   但**不放 start_time / duration**——那些改了要留住正在编辑的 SpinBox（W4 的口径）。
	var si := doc.find_signal_index(nid)
	var plan_sig := ""
	if si >= 0:
		var parts: Array = []
		for pl in doc.signals[si].get("plans", []):
			parts.append(str((pl.get("stages", []) as Array).size()))
		plan_sig = ",".join(PackedStringArray(parts))
	return "junction|%d|%s|%s|%s|%s" % [nid, String(doc.find_node(nid).get("type", "?")),
		si >= 0, str(tids), plan_sig]

func _rebuild() -> void:
	_phase_end()            # ★ 面板要重建：把还没收口的配时输入先记成一步撤销
	var target := _panel_target()
	_fingerprint = _fingerprint_of(target)
	for c in get_children():
		remove_child(c)
		c.queue_free()
	_rev_spin = null
	_sum_label = null
	_offset_spin = null
	_anchor_warn = null      # ★ 这几个 label 属于特定面板，重建时要清掉引用
	_weights_sum = null
	_plan_time_label = null
	_stage_bars.clear()      # ★ 子控件这一拍就被 queue_free 了，引用必须一起清（C18）
	_cycle_label = null
	_clash_status = null     # ★ 同上：冲突状态行与红字都是本次重建前创建的子控件
	_clash_labels.clear()
	_clash_node = -1
	_curve_prevs.clear()
	_body = null             # ★ 小节容器的引用同样要清（否则下一面板的行会挂进已 free 的容器）
	_section_bodies.clear()
	_section_heads.clear()
	_section_titles.clear()
	match String(target["kind"]):
		"multi":
			_build_multi()
		"traffic_turn", "traffic_lane":
			_build_traffic(String(target["kind"]), int(target["id"]))
		"traffic_overview":
			_build_traffic_overview()
		"deco":
			_build_deco(int(target["id"]))
		"island":
			_build_island(int(target["id"]))
		"turn":
			_build_turn(int(target["id"]))
		"junction":
			_build_junction(int(target["id"]))
		"road":
			_build_road(int(target["id"]))
		_:
			_build_level()
	# ★ P4-2：换选中对象时新内容淡入（R0 §4「选中对象变化」）。**只补 alpha** ——
	#   面板在坞的 VBox 里，动 position/height 会被容器布局覆盖（同一个坑在 Toast 上踩过）。
	if _fade_tw != null and _fade_tw.is_valid():
		_fade_tw.kill()                      # 连点两下选中：别叠两个补间互相打架
	_fade_tw = Motion.fade_in(self, Motion.D_FADE_IN)

## 路（注意"新建路默认"只是会话级变量，不随选中变化）
func _build_road(rid: int) -> void:
	var road: Dictionary = EditorApp.doc.find_road(rid)
	if road.is_empty():
		return
	var t: Dictionary = road.template

	_header("道", "道路", "#%d" % rid)
	_section("基本信息")
	_add_row("道路编号", _info("#%d" % rid))
	_add_row("新建路默认", _info(_template_label(EditorApp.new_road_template)))
	var presets := VBoxContainer.new()
	for p in ROAD_PRESETS:
		var b := _btn(String(p["name"]), func(): _apply_preset(rid, p))
		# ★ 允许截断：这一组按钮的最长者（「2 车道 + 非机动车道 + 人行道」）会把面板的
		#   min_width 顶到 357，坞一拖窄（<380）就被裁。截断 + tooltip 让坞可以自由拖窄。
		b.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
		b.tooltip_text = String(p["name"])
		b.custom_minimum_size.x = 0
		presets.add_child(b)
	_add_row("模板", presets)
	_section("车道与限速")
	_add_row("正向车道数", _spin(float(t.lanes_fwd), 1, 4, 1,
		func(v): _apply(rid, "lanes_fwd", int(v))))
	_rev_spin = _spin(float(t.lanes_rev), 0, 4, 1,
		func(v): _apply(rid, "lanes_rev", int(v)))
	_rev_spin.editable = not bool(t.oneway)
	_add_row("反向车道数", _rev_spin)
	_add_row("单向", _check(bool(t.oneway), _on_oneway.bind(rid)))
	_add_row("限速 (m/s)", _spin(float(t.speed_limit), 1.0, 40.0, 0.5,
		func(v): _apply(rid, "speed_limit", float(v))))
	_add_row("人行道", _check(bool(t.sidewalk), func(on): _apply(rid, "sidewalk", on)))
	_add_row("独立非机动车道", _check(bool(t.bike), func(on): _apply(rid, "bike", on)))
	_add_row("公交专用道", _check(bool(t.bus), func(on): _apply(rid, "bus", on)))
	# ★ 左转专用道现实中只在**进口道**出现，出口道没有 → 按方向各一个开关。
	#   a→b 这一向的进口在 **b 端**：画路时从上游拖向路口，勾上面那个就对了。
	_add_row("左转道 a→b", _check(bool(t.get("left_turn_fwd", false)),
		func(on): _apply(rid, "left_turn_fwd", on)))
	_add_row("左转道 b→a", _check(bool(t.get("left_turn_rev", false)),
		func(on): _apply(rid, "left_turn_rev", on)))
	_add_row("", _wrapped("只在进口道用：a→b 是进 b 的那一向。"))
	_section("进口拓宽")
	# ★ 进口道拓宽（G4）：插一个节点，只把靠这一端的一段加宽（出口道不拓宽）
	var wrow := HBoxContainer.new()
	wrow.add_theme_constant_override("separation", UITokens.S2)
	var wlen := _spin(60.0, 10.0, 400.0, 5.0, func(_v): pass)     # 会话级参数，不写 Doc
	wlen.suffix = " m"
	wlen.custom_minimum_size = Vector2(76, 0)
	wlen.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	wlen.tooltip_text = "靠路口的那一段有多长（渐变段的视觉留给表现层）"
	wrow.add_child(wlen)
	# 两个按钮竖着叠：并排会把这行的最小宽度顶到 422（> 面板可用的 412），整块右边缘被裁
	var wbtns := VBoxContainer.new()
	wbtns.add_theme_constant_override("separation", UITokens.S1)
	wbtns.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	wrow.add_child(wbtns)
	var na := int(road.a_node)
	var nb := int(road.b_node)
	var ba := Button.new()
	ba.text = "拓宽 a 端"
	ba.tooltip_text = "在 a 端前插一个节点，把靠 a 的那段加宽（这一端的进口方向 +1 车道）"
	ba.pressed.connect(func(): _widen(rid, na, wlen.value))
	wbtns.add_child(ba)
	var bb := Button.new()
	bb.text = "拓宽 b 端"
	bb.tooltip_text = "在 b 端前插一个节点，把靠 b 的那段加宽（这一端的进口方向 +1 车道）"
	bb.pressed.connect(func(): _widen(rid, nb, wlen.value))
	wbtns.add_child(bb)
	_add_row("进口拓宽", wrow)
	_add_row("", _wrapped("拆两段，只加宽靠路口那段；加完自动选中近段。"))

func _widen(rid: int, at_node: int, dist_m: float) -> void:
	# ★ 不用 _btn：widen_approach 内部自己包 begin/end（它要把"重推相位"也算进同一步撤销）
	if EditorApp.widen_approach(rid, at_node, dist_m):
		_refresh_later()

func _on_oneway(on: bool, rid: int) -> void:
	_apply(rid, "oneway", on)
	if _rev_spin != null:
		_rev_spin.editable = not on      # 单向时反向车道数不生效，置灰

## ★ 每个预设都写全 left_turn_fwd / left_turn_rev：`_apply_preset` 只覆盖"预设里有的键"，
##   不写就会把上一份模板的左转道留着（套「双向 2 车道」却还多一条左转道）。
const ROAD_PRESETS := [
	{"name": "双向 2 车道",
		"t": {"lanes_fwd": 1, "lanes_rev": 1, "oneway": false, "bus": false, "bike": false,
			"sidewalk": true, "left_turn_fwd": false, "left_turn_rev": false}},
	{"name": "双向 4 车道",
		"t": {"lanes_fwd": 2, "lanes_rev": 2, "oneway": false, "bus": false, "bike": false,
			"sidewalk": true, "left_turn_fwd": false, "left_turn_rev": false}},
	{"name": "2 车道 + 非机动车道 + 人行道",
		"t": {"lanes_fwd": 1, "lanes_rev": 1, "oneway": false, "bus": false, "bike": true,
			"sidewalk": true, "left_turn_fwd": false, "left_turn_rev": false}},
	{"name": "单行道（2 车道）",
		"t": {"lanes_fwd": 2, "lanes_rev": 0, "oneway": true, "bus": false, "bike": false,
			"sidewalk": true, "left_turn_fwd": false, "left_turn_rev": false}},
	{"name": "4 车道 + 左转专用道",
		"t": {"lanes_fwd": 2, "lanes_rev": 2, "oneway": false, "bus": false, "bike": false,
			"sidewalk": true, "left_turn_fwd": true, "left_turn_rev": false}},
]

## 把横断面预设套到"选中的路"；同时记为"新建路的默认"（面板上会显示）
func _apply_preset(road_id: int, preset: Dictionary) -> void:
	var road: Dictionary = EditorApp.doc.find_road(road_id)
	if road.is_empty():
		return
	var t: Dictionary = (road.template as Dictionary).duplicate(true)
	for k in preset["t"]:
		t[k] = preset["t"][k]
	EditorApp.new_road_template = t.duplicate(true)
	EditorApp.doc.rebuild_road(road_id, t)      # ★ 会删掉引用旧车道的连接（既有行为，会打印条数）
	print("道路 %d 套用「%s」；之后新建的路也用同一模板" % [road_id, String(preset["name"])])
	_refresh_later()        # ★ 延后一拍：现在正待在"模板"按钮的 pressed 回调里，别当场把它拆了

func _template_label(t: Dictionary) -> String:
	if t.is_empty():
		return "默认 1 正 / 1 反（点下面模板可改）"
	var s := "%d 正 / %d 反" % [int(t.get("lanes_fwd", 0)), int(t.get("lanes_rev", 0))]
	if bool(t.get("oneway", false)):
		s += "  单向"
	var ltf := bool(t.get("left_turn_fwd", false))
	var ltr := bool(t.get("left_turn_rev", false))
	if ltf and ltr:
		s += "  +左转道(两向)"
	elif ltf:
		s += "  +左转道(a→b)"
	elif ltr:
		s += "  +左转道(b→a)"
	if bool(t.get("sidewalk", false)):
		s += "  人行道"
	if bool(t.get("bike", false)):
		s += "  非机动"
	if bool(t.get("bus", false)):
		s += "  公交"
	return s

const IN_PLACE_KEYS := ["speed_limit"]   ## 不改车道集合的字段 → 原地更新，绝不重建


## 面板写回：按字段决定"原地更新"还是"重建"
##  ★ 只改限速这种字段绝不能重建 —— 一重建就把两端路口的连接全删了
func _apply(road_id: int, key: String, value) -> void:
	var road: Dictionary = EditorApp.doc.find_road(road_id)
	if road.is_empty():
		return
	if IN_PLACE_KEYS.has(key):
		EditorApp.doc.set_road_template_field(road_id, key, value)
		return
	road.template[key] = value
	EditorApp.doc.rebuild_road(road_id, road.template)

# --- 控件小工 -----------------------------------------------------------

## 面板标题区：图标块 + 类型名 + 「#ID」。**纯看板**，不带任何交互 ——
## 每一行自己的编号仍在下面的「基本信息」里（P3 红线：不删控件）。
## sub 只放**指纹里有的**信息（id / 已选条数 / 文件名），否则改车道数时会留着过期文字。
func _header(glyph: String, title: String, sub: String) -> void:
	var card := Clay.plate(UITokens.S3, 0)
	var h := HBoxContainer.new()
	h.add_theme_constant_override("separation", UITokens.S3)
	h.add_child(_glyph_box(glyph))
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 0)
	box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var t := Label.new()
	t.text = title
	t.theme_type_variation = "TitleLabel"
	box.add_child(t)
	var s := Label.new()
	s.text = sub
	s.theme_type_variation = "HintLabel"
	box.add_child(s)
	h.add_child(box)
	card.get_node("Body").add_child(h)
	add_child(card)


## 图标方块（40×40 圆底 + 一个字）。没有矢量图标集之前，先用字形占位（与工具条同口径）
func _glyph_box(glyph: String) -> PanelContainer:
	var box := Clay.inset(UITokens.S1)
	box.custom_minimum_size = Vector2(40, 40)
	box.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var l := Label.new()
	l.text = glyph
	l.theme_type_variation = "TitleLabel"
	l.add_theme_font_size_override("font_size", UITokens.T_DISPLAY)
	l.add_theme_color_override("font_color", UITokens.ORANGE_600)
	l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	box.add_child(l)
	return box


## 开一个小节：折叠头 + 正文容器。**之后所有 `_add_row` 都进这个正文**。
## ★ 折叠是纯显隐：不写 Doc、不占撤销步、不进指纹；状态存 `user://ui_panel_folds.json`。
## ★ 小节的先后顺序 = 代码里调用的顺序 = 原有行的顺序，一处都没重排。
## ★ `default_folded` = 这一节**第一次见**时的状态；用户手动点过就以文件里存的为准。
## ★ `fold_key` = 持久化键，默认等于标题。**关卡面板统一传 `level/` 前缀**（见 `_build_level`）：
##   老板 2026-11-06 要求专业模式下关卡设置默认收起，而旧 json 里已经有 `"关卡信息": false`
##   （那是"默认展开"时代留下的），不换键的话改了默认值也看不出效果。
func _section(title: String, default_folded: bool = false, fold_key: String = "") -> void:
	if not _section_bodies.is_empty():
		add_child(Clay.sep())               # 小节的缝：首节之前不加，免得顶部多一条线
	var key := fold_key if fold_key != "" else title
	var folded := bool(_fold_state.get(key, default_folded))
	var head := Clay.btn_ghost(("▸  " if folded else "▾  ") + title)
	head.alignment = HORIZONTAL_ALIGNMENT_LEFT
	head.focus_mode = Control.FOCUS_NONE    # 别抢键盘焦点：画图时还要按 1~4 / G / R
	head.custom_minimum_size = Vector2(0, UITokens.SECTION_HEAD_H)
	head.add_theme_font_size_override("font_size", UITokens.T_TITLE)
	head.pressed.connect(_toggle_section.bind(key))
	add_child(head)
	var body := VBoxContainer.new()
	body.add_theme_constant_override("separation", UITokens.S2)
	body.visible = not folded
	add_child(body)
	_body = body
	_section_heads[key] = head
	_section_titles[key] = title
	_section_bodies[key] = body


func _toggle_section(key: String) -> void:
	if not _section_bodies.has(key):
		return
	var body: Control = _section_bodies[key]
	var head: Button = _section_heads[key]
	# ★ 折叠状态**从正文的实际可见性反推**，不要用 `not _fold_state.get(key, false)`：
	#   `default_folded=true` 的节（关卡面板那几节）在用户点之前**文件里没有记录**，
	#   按"缺省 = 展开"反推会把"点开展开"算成"折叠"—— 点了没反应，且把 true 写进文件。
	var folded := body.visible
	_fold_state[key] = folded
	LayoutStore.save_folds(_fold_state)
	var title := String(_section_titles.get(key, key))
	head.text = ("▸  " if folded else "▾  ") + title
	# ★ P4-2：折叠/展开补一段淡入淡出（R0 §4「面板折叠」）。高度不给补间 ——
	#   行高由容器算，补 height 只会被下一帧重排打回去，还会让滚动条乱跳。
	if folded:
		await Motion.fade_out(body, Motion.D_FADE_OUT).finished
		if bool(_fold_state.get(key, false)):        # 淡出途中又点开了 → 别把它藏起来
			body.visible = false
	else:
		body.visible = true
		Motion.fade_in(body)


## 「限速 (m/s)」→ ["限速", "m/s"]：单位交给 field_row 右侧灰字，字段名只留语义。
## ★ 只认白名单：括号里是"语义"而不是单位的标签（如某些动态标题）不能拆。
const UNIT_WORDS := ["m/s", "km/h", "辆/秒", "人/秒", "辆", "px", "°", "%", "人", "米", "m", "s"]


func _split_unit(label_text: String) -> Array:
	var t := label_text.strip_edges()
	for u in UNIT_WORDS:
		for pair in [[" (", ")"], ["（", "）"]]:
			var tail := String(pair[0]) + String(u) + String(pair[1])
			if t.length() > tail.length() and t.ends_with(tail):
				return [t.substr(0, t.length() - tail.length()), String(u)]
	return [label_text, ""]


## 一行 = [固定宽字段名][值控件][单位]。**面板里所有行都必须走这里** ——
## 直接 `add_child` 会让这一行缺字段名列，整块和上下对不齐（E91 C17 的观感就是它）。
## label_text 传空 = 整行宽（提示文字 / 一排按钮 / 一块勾选表）。
func _add_row(label_text: String, control: Control) -> void:
	var host: Node = _body if _body != null else self
	if label_text == "":
		host.add_child(control)
		return
	if control is Label:
		# ★ 只读值可能是长句子（如「默认 1 正 / 1 反（点下面模板可改）」）。Label 的**最小宽度 = 整行字宽**，
		#   不换行的话它会顶着面板的 min_width 把坞撑爆（实测 422 > 412，右边缘被裁 14 px）。
		#   允许折行 + 留 tooltip，长文本就自己变两行，不挤别人。
		var l: Label = control
		l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		l.custom_minimum_size.x = 0.0
		if l.tooltip_text == "":
			l.tooltip_text = l.text
	elif control is OptionButton or control is Button:
		# ★ 下拉/按钮的**最小宽度 = 文字宽**，在窄坞里会顶穿右边缘（实测「阶段数」那一行 354 > 346）。
		#   允许省略号 + tooltip：坞拖窄时文字自己收，操作不受影响。
		var b := control as Button
		b.clip_text = true       # ★ 只有 clip_text 会**同时**压缩最小宽度；text_overrun 只影响绘制
		if control is OptionButton:
			# ★ OptionButton 默认按**最长条目**算最小宽（fit_to_longest_item = true）：
			#   「4 阶段（左转独立保护）」这种长条目会把整行顶到 354。改成按当前文本算。
			(control as OptionButton).fit_to_longest_item = false
		if b.tooltip_text == "":
			b.tooltip_text = b.text
	var parts := _split_unit(label_text)
	host.add_child(Clay.field_row(String(parts[0]), control, String(parts[1])))

func _info(text: String) -> Label:
	var l := Label.new()
	l.text = text
	return l

## tag 可选：传了就用它当撤销合并键（同一字段连续微调并成一步、不同字段各占一步）；
## 不传走 `_auto_tag()`（结构签名 + 选中项，原地改数值时会合并）。
func _spin(value: float, min_v: float, max_v: float, step: float, cb: Callable, tag: String = "") -> SpinBox:
	var s := SpinBox.new()
	s.min_value = min_v
	s.max_value = max_v
	s.step = step
	s.value = value          # 先赋值再连信号，避免触发一次多余的写回
	s.value_changed.connect(func(_v): EditorApp.begin_edit(tag))   # ★ 先记快照
	s.value_changed.connect(cb)                                 # 再改值
	s.value_changed.connect(func(_v): EditorApp.end_edit())     # ★ 改完判定是否真变了
	return s

func _check(on: bool, cb: Callable) -> CheckBox:
	var c := CheckBox.new()
	c.button_pressed = on
	c.toggled.connect(func(_on): EditorApp.begin_edit())
	c.toggled.connect(cb)
	c.toggled.connect(func(_on): EditorApp.end_edit())
	return c

## 按钮工厂：按"记快照 → 做事 → 判定"三段连（信号按连接顺序依次执行）
func _btn(text: String, cb: Callable) -> Button:
	var b := Button.new()
	b.text = text
	b.pressed.connect(func(): EditorApp.begin_edit())
	b.pressed.connect(cb)
	b.pressed.connect(func(): EditorApp.end_edit())
	return b

# --- 连接（turn）属性 ---------------------------------------------------

func _build_turn(turn_id: int) -> void:
	var doc: Doc = EditorApp.doc
	var t: Dictionary = doc.find_turn(turn_id)
	if t.is_empty():
		return
	var tid := int(t.id)
	var src := int(t.src_lane)
	_header("连", "连接", "#%d" % tid)
	_section("基本信息")
	_add_row("连接编号", _info("#%d" % tid))
	_add_row("所属路口", _info("#%d" % int(t.node)))
	_add_row("进口车道", _info("#%d" % src))
	_add_row("出口车道", _info("#%d" % int(t.dst_lane)))
	_section("转向与分流")
	_add_row("转向类型", _type_option(String(t.type),
		func(ty): _set_turn_type(tid, ty)))
	var sp := _spin(float(t.get("ratio", 1.0)) * 100.0, 1.0, 100.0, 1.0,
		func(v): _set_turn_ratio(tid, float(v) / 100.0))
	sp.suffix = "%"                       # 面板按百分比显示
	_add_row("分流比", sp)
	_sum_label = _info("%.2f" % _ratio_sum(doc, src))
	_add_row("该进口合计", _sum_label)     # 同一进口各出口的 ratio 之和，应 ≈ 1
	if String(t.type) == "Crosswalk":
		_section("二次过街")
		_build_crosswalk_legs(t)
	# ★ S4：只给**机动车转向**显示（人行横道/自行车转向限车型没意义）。
	if _is_motor_turn(doc, t):
		_build_sumo_connection(tid)


## 这条转向是不是机动车转向（进出口都是机动车道）
func _is_motor_turn(doc: Doc, t: Dictionary) -> bool:
	var a: Dictionary = doc.find_lane(int(t.get("src_lane", -1)))
	var b: Dictionary = doc.find_lane(int(t.get("dst_lane", -1)))
	if a.is_empty() or b.is_empty():
		return false
	return Doc.MOTOR_LANE_TYPES.has(String(a.get("type", ""))) \
		and Doc.MOTOR_LANE_TYPES.has(String(b.get("type", "")))

## 过街的「二次过街（中央安全岛）」区 —— E92 G2 · N6b
##
## 交互：一键把这条过街拆成 leg=0 / leg=1 两条 + 建一个岛（岛落在这条过街的中点），
## 岛建好后可点选它改容量 / 位置。已拆过的显示配对信息 + 「取消二次过街」。
func _build_crosswalk_legs(t: Dictionary) -> void:
	var doc: Doc = EditorApp.doc
	var tid := int(t.id)
	if not Doc.is_split_turn(t):
		_add_row("二次过街", _info("否（一次走完）"))
		var cap := _spin(6.0, 1.0, 60.0, 1.0, func(_v): pass, "island_cap:%d" % tid)
		_add_row("岛容量（人）", cap)
		# ★ 容量在这里先读出来、再一次性传给拆分：拆分是一次原子操作，不该分两步写 doc
		var b := _btn("设为二次过街", func():
			var r: Dictionary = EditorApp.doc.split_crosswalk(tid, int(cap.value))
			if not bool(r.get("ok", false)):
				push_warning("二次过街失败：" + String(r.get("error", "")))
				return
			EditorApp.select_island(int(r.get("island", -1)))
		)
		b.tooltip_text = "把这条过街拆成「路缘→岛」「岛→对岸」两段，并在这条过街的中点放一个安全岛。\n拆完请到路口相位面板里，让两段各自被不同相位放行。"
		_add_row("", b)
		_add_row("说明", _info("拆成两段后，行人先走到岛、在岛上等后段放行。"))
		return

	var leg := int(t.get("leg", 0))
	var iid := int(t.get("island", -1))
	var pair := doc.find_pair_turn(t)
	var first := doc.first_leg_turn(t)
	_add_row("二次过街", _info("是（本段 = %s）" % ("前段 路缘→岛" if leg == 0 else "后段 岛→对岸")))
	# ★ 两段各是独立 turn，要能一键跳到另一段去看 / 改（以前只是纯文本，点不动）
	var go_first := _btn(_turn_label(doc, int(first.id)), func(): EditorApp.select_turn(int(first.id)))
	go_first.tooltip_text = "跳到前段（路缘 → 岛）"
	if pair.is_empty():
		_add_row("前段连接", go_first)
		_add_row("后段连接", _warn_label("缺失（校验会报 crosswalk_leg_missing）"))
	else:
		var go_pair := _btn(_turn_label(doc, int(pair.id)), func(): EditorApp.select_turn(int(pair.id)))
		go_pair.tooltip_text = "跳到后段（岛 → 对岸）"
		_add_row("前段连接", go_first)
		_add_row("后段连接", go_pair)
	var isl := doc.find_island(iid)
	if isl.is_empty():
		_add_row("安全岛", _info("缺失（#%d）" % iid))
	else:
		# ★ 安全岛区（P3-2）：岛是这条过街的"另一半"，信息 + 跳转合成一张小卡，
		#   不再散成"一行信息 + 一行按钮"（在长卷里很容易看不出这两行是一回事）。
		var card := Clay.inset(UITokens.S2)
		var cv := VBoxContainer.new()
		cv.add_theme_constant_override("separation", UITokens.S1)
		card.add_child(cv)
		var l1 := _info("安全岛 #%d" % iid)
		l1.theme_type_variation = "TitleLabel"
		cv.add_child(l1)
		var l2 := _info("容量 %d 人　（两段过街的放行要分开配）" % int(isl.get("capacity", 0)))
		l2.theme_type_variation = "HintLabel"
		l2.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		cv.add_child(l2)
		var go := _btn("选中安全岛", func(): EditorApp.select_island(iid))
		go.custom_minimum_size = Vector2(0, 30)
		cv.add_child(go)
		_add_row("", card)
	var m := _btn("取消二次过街", func():
		var r: Dictionary = EditorApp.doc.merge_crosswalk(int(first.id))
		if not bool(r.get("ok", false)):
			push_warning("取消二次过街失败：" + String(r.get("error", "")))
		EditorApp.select_turn(int(first.id))
	)
	_add_row("", m)

## 安全岛面板（E92 G2 · N6b）
func _build_island(island_id: int) -> void:
	var doc: Doc = EditorApp.doc
	var isl: Dictionary = doc.find_island(island_id)
	if isl.is_empty():
		return
	var iid := int(isl.get("id", -1))
	_header("岛", "安全岛", "#%d" % iid)
	_section("基本信息")
	_add_row("安全岛编号", _info("#%d" % iid))
	_add_row("容量（人）", _spin(float(isl.get("capacity", 1)), 1.0, 60.0, 1.0,
		func(v): doc.set_island_field(iid, "capacity", int(v)), "island_cap:%d" % iid))
	_section("位置")
	_add_row("位置 X（米）", _spin(float(isl.get("x", 0.0)), -100000.0, 100000.0, 0.5,
		func(v): doc.set_island_field(iid, "x", v), "island_x:%d" % iid))
	_add_row("位置 Y（米）", _spin(float(isl.get("y", 0.0)), -100000.0, 100000.0, 0.5,
		func(v): doc.set_island_field(iid, "y", v), "island_y:%d" % iid))

	# 这个岛在哪条过街上
	_section("所属过街")
	var legs: Array = []
	for t in doc.turns:
		if int(t.get("island", -1)) == iid:
			legs.append(t)
	_add_row("所属过街", _info("%d 段" % legs.size()))
	for t in legs:
		var num := int(t.get("leg", 0))
		var go := _btn(_turn_label(doc, int(t.id)), func(): EditorApp.select_turn(int(t.id)))
		go.tooltip_text = "跳到这段过街（去相位面板配它的放行）"
		_add_row("  段 %d" % num, go)
	if legs.size() != 2:
		var warn := _info("⚠ 应当正好两段（现在 %d 段）—— 导出会被校验器拦下" % legs.size())
		warn.modulate = Color(1.0, 0.55, 0.45)
		_add_row("检查", warn)

	var del := _btn("删除这个安全岛（同时把过街并回一段）", func():
		if legs.size() > 0:
			doc.merge_crosswalk(int(legs[0].id))
		EditorApp.select_island(-1)
	)
	_add_row("", del)

# --- 连接属性：类型下拉 -------------------------------------------------

func _type_option(cur: String, cb: Callable) -> OptionButton:
	var o := OptionButton.new()
	for i in TURN_TYPES.size():
		o.add_item(TURN_LABELS[i])       # 下拉里显示中文
	var idx := TURN_TYPES.find(cur)      # 中文标签的下标 = 英文枚举的下标
	o.select(maxi(idx, 0))               # 先选中、后连信号，避免触发一次多余写回
	o.item_selected.connect(func(_i): EditorApp.begin_edit())          # ★ 先记快照
	o.item_selected.connect(func(i: int): cb.call(TURN_TYPES[i]))      # 原来的写回
	o.item_selected.connect(func(_i): EditorApp.end_edit())            # ★ 改完判定
	return o

func _set_turn_type(turn_id: int, ty: String) -> void:
	var t: Dictionary = EditorApp.doc.find_turn(turn_id)
	if t.is_empty() or String(t.get("type", "")) == ty:
		return
	t["type"] = ty
	EditorApp.doc.dirty = true
	print("连接 %d：转向类型 → %s" % [turn_id, ty])

func _set_turn_ratio(turn_id: int, ratio: float) -> void:
	var t: Dictionary = EditorApp.doc.find_turn(turn_id)
	if t.is_empty():
		return
	var v := clampf(ratio, 0.01, 1.0)    # 兜底：绝不越界，避免 turn_ratio_out_of_range
	t["ratio"] = v
	EditorApp.doc.dirty = true
	if _sum_label != null:
		_sum_label.text = "%.2f" % _ratio_sum(EditorApp.doc, int(t.get("src_lane", -1)))

## 同一进口车道所有出口的 ratio 之和（应 ≈ 1）
func _ratio_sum(doc: Doc, src_lane: int) -> float:
	var s := 0.0
	for t in doc.turns:
		if int(t.src_lane) == src_lane:
			s += float(t.get("ratio", 1.0))
	return s

# --- 装饰（deco）属性 ---------------------------------------------------

const DECO_ANCHORS := ["center", "top_left", "top_center"]
const DECO_ANCHOR_LABELS := ["中心", "左上", "上中"]

func _build_deco(id: int) -> void:
	var doc: Doc = EditorApp.doc
	var d: Dictionary = doc.find_deco(id)
	if d.is_empty():
		return
	_header("饰", "装饰", "#%d" % id)
	_section("基本信息")
	_add_row("装饰编号", _info("#%d" % id))
	_add_row("位置 (m)", _info("%.2f, %.2f" % [float(d.get("x", 0.0)), float(d.get("y", 0.0))]))
	_add_row("素材", _asset_option(String(d.get("asset", "")), func(a): _set_deco(id, "asset", a)))
	_section("变换")
	_add_row("旋转 (°)", _spin(float(d.get("rotation", 0.0)), 0.0, 360.0, 1.0,
		func(v): _set_deco(id, "rotation", wrapf(float(v), 0.0, 360.0))))
	_add_row("缩放", _spin(float(d.get("scale", 1.0)), 0.1, 10.0, 0.1,
		func(v): _set_deco(id, "scale", clampf(float(v), 0.1, 10.0))))
	_add_row("层级 z", _spin(float(d.get("z", 1)), -20.0, 20.0, 1.0,
		func(v): _set_deco(id, "z", int(v))))
	_add_row("锚点", _anchor_option(String(d.get("anchor", "center")),
		func(a): _set_deco(id, "anchor", a)))
	_section("平铺")
	_add_row("平铺 tile", _check(bool(d.get("tile", false)),
		func(on): _set_deco(id, "tile", on)))
	if bool(d.get("tile", false)):                    # 勾了平铺才出现宽/高两行
		var nat := _asset_size(String(d.get("asset", "")))
		var w := int(d.get("w", 0))
		var h := int(d.get("h", 0))
		_add_row("平铺宽 (px)", _spin(float(w if w > 0 else int(nat.x)), 1.0, 4096.0, 1.0,
			func(v): _set_deco(id, "w", int(v))))
		_add_row("平铺高 (px)", _spin(float(h if h > 0 else int(nat.y)), 1.0, 4096.0, 1.0,
			func(v): _set_deco(id, "h", int(v))))

func _set_deco(id: int, key: String, value) -> void:
	EditorApp.doc.set_deco_field(id, key, value)
	if key == "tile":
		_refresh_later()        # 勾/去勾 → 面板要增删宽/高两行（延后一拍，别拆掉正在发 toggled 的勾选框）

func _asset_option(cur: String, cb: Callable) -> OptionButton:
	var o := OptionButton.new()
	var groups := AssetLibrary.scan_deco()
	var i := 0
	var cur_i := -1
	for g in groups:
		for it in groups[g]:
			o.add_item("%s / %s" % [g, String(it["label"])])
			o.set_item_metadata(i, String(it["asset"]))
			if String(it["asset"]) == cur:
				cur_i = i
			i += 1
	if cur_i >= 0:
		o.select(cur_i)
	o.item_selected.connect(func(_i): EditorApp.begin_edit())          # ★ 先记快照
	o.item_selected.connect(func(i: int): cb.call(o.get_item_metadata(i)))      # 原来的写回
	o.item_selected.connect(func(_i): EditorApp.end_edit())            # ★ 改完判定
	return o

func _anchor_option(cur: String, cb: Callable) -> OptionButton:
	var o := OptionButton.new()
	for i in DECO_ANCHORS.size():
		o.add_item(DECO_ANCHOR_LABELS[i])
	o.select(maxi(DECO_ANCHORS.find(cur), 0))
	o.item_selected.connect(func(_i): EditorApp.begin_edit())          # ★ 先记快照
	o.item_selected.connect(func(i: int): cb.call(DECO_ANCHORS[i]))      # 原来的写回
	o.item_selected.connect(func(_i): EditorApp.end_edit())            # ★ 改完判定
	return o

## 素材原始像素尺寸（拿不到就 (1,1)）
func _asset_size(asset: String) -> Vector2:
	if asset == "":
		return Vector2.ONE
	var t := load("res://assets/" + asset) as Texture2D
	return t.get_size() if t != null else Vector2.ONE

# --- 节点（junction）属性 ------------------------------------------------

const NODE_TYPES := ["Unsignalized", "Signalized", "Origin", "Destination"]
const NODE_TYPE_LABELS := ["普通路口", "信号路口", "起点 Origin", "终点 Destination"]

func _build_junction(nid: int) -> void:
	var doc: Doc = EditorApp.doc
	var n: Dictionary = doc.find_node(nid)
	if n.is_empty():
		return
	if not is_same(doc, _stage_doc) or nid != _stage_nid:
		_plan_warn = ""
		_open_stage = ""                 # 换了文档（导入/撤销）或换了路口 → 收起展开状态
		_stage_doc = doc
		_stage_nid = nid
	_header("口", "路口", "#%d" % nid)
	_section("基本信息")
	_add_row("节点编号", _info("#%d" % nid))
	_add_row("连接路数", _info("%d 条" % doc.road_count_at(nid)))
	_add_row("位置 (m)", _info("%.2f, %.2f" % [float(n.x), float(n.y)]))
	_add_row("节点类型", _node_type_option(String(n.type),
		func(t): EditorApp.set_node_type(nid, t)))
	# ★ S4：路口在 SUMO net.xml 里的控制方式（优先让行 / 全向停车 / 无控制）。
	#   信号路口在这里是只读的 —— 改它会让 tlLogic 静默失效（见 SumoNetModel 注释）。
	_build_sumo_junction(nid)
	# ★ 批量建曲线（B21）：源 = 该路口第一条已有曲线的进口/过街，一次套给该路口全部同类位置
	if doc.can_be_signalized(nid):
		_add_row("批量建曲线", _junction_batch_bar(nid))
	if String(n.type) == "Signalized":
		# ★ 专业模式：右侧「配时」整块换成**逐灯位矩阵**（2026-11-04 老板要求：不做浮窗，
		#   就放在原配时的位置）。基础模式一个字节都不变。
		if EditorApp.is_pro():
			_section("信号灯配时")
			_add_row("该路口连接", _info("%d 条" % _turn_count(doc, nid)))
			var tls_panel: Control = SumoTlsPanelScript.new()
			_add_row("", tls_panel)
			tls_panel.call("open_for", nid)      # ★ 必须先入树再 open_for（_ready 才连得上信号）
			return
		_section("配时")
		_add_row("该路口连接", _info("%d 条" % _turn_count(doc, nid)))
		_build_phases(doc, nid)                 # ★ 配时编辑区（只读写 Doc）
	elif doc.can_be_signalized(nid):             # 普通路口：保留原有内容，只多一个按钮
		_section("配时")
		# ★ 走 set_node_type("Signalized")：它内部就是"补连接 → apply_phases_at → 失败回滚"，
		#   且会把节点类型一并改掉；只调 apply_phases_at 会留下"有相位、类型仍是普通路口"的半成品。
		var b := _btn("生成相位", func(): EditorApp.set_node_type(nid, "Signalized"))
		b.pressed.connect(_refresh_later)        # 生成完重建面板（延后一拍，别拆掉自己）
		_add_row("配时", b)

## 路口级批量建曲线（B21）：源由 EditorApp 自己挑（该路口第一条已有曲线的进口 / 过街）
## ★ 两个按钮**竖排**：并排时这行的最小宽度 > 面板可用宽，整块右边缘会被裁
##   （P3-1 实测：并排 465 > 400，面板右边缘连 OptionButton 一起被切）
func _junction_batch_bar(nid: int) -> VBoxContainer:
	var bar := VBoxContainer.new()
	bar.add_theme_constant_override("separation", UITokens.S1)
	var b1 := _btn("机动车流 → 全部进口",
		func(): EditorApp.batch_profile_from_junction(nid, "demand"))
	b1.pressed.connect(_refresh_later)
	bar.add_child(b1)
	var b2 := _btn("行人 → 全部过街",
		func(): EditorApp.batch_profile_from_junction(nid, "pedestrians"))
	b2.pressed.connect(_refresh_later)
	bar.add_child(b2)
	return bar

func _node_type_option(cur: String, cb: Callable) -> OptionButton:
	var o := OptionButton.new()
	for i in NODE_TYPES.size():
		o.add_item(NODE_TYPE_LABELS[i])
	o.select(maxi(NODE_TYPES.find(cur), 0))     # 先选中、后连信号，避免多余写回
	o.item_selected.connect(func(_i): EditorApp.begin_edit())          # ★ 先记快照
	o.item_selected.connect(func(i: int): cb.call(NODE_TYPES[i]))      # 原来的写回
	o.item_selected.connect(func(_i): EditorApp.end_edit())            # ★ 改完判定
	return o

func _turn_count(doc: Doc, node_id: int) -> int:
	var c := 0
	for t in doc.turns:
		if int(t.node) == node_id:
			c += 1
	return c

## 该路口一条都没被任何阶段放行的 turn（★ 只看 plans[0]，即当前生效方案）
func _turns_never_green(doc: Doc, node_id: int, plans: Array) -> Array:
	var seen := {}
	var stages: Array = (plans[0] as Dictionary).get("stages", []) if not plans.is_empty() else []
	for st in stages:
		for tid in st.get("protected", []): seen[int(tid)] = true
		for tid in st.get("permitted", []): seen[int(tid)] = true
	var out: Array = []
	for t in doc.turns:
		if int(t.node) == node_id and not seen.has(int(t.id)):
			out.append(int(t.id))
	return out


# --- 配时（signal）编辑区 -------------------------------------------------
# ★ 只读写 Doc，绝不调 SimCore；每个改值控件都包在 _phase_spin 的 begin/end 里

## 配时区：offset（0~周期秒）+ 每个方案的每个阶段一行（序号 / 绿灯时长 / 放行摘要）
func _build_phases(doc: Doc, node_id: int) -> void:
	var si := doc.find_signal_index(node_id)
	if si < 0 or (doc.signals[si].get("plans", []) as Array).is_empty():
		_add_row("相位", _info("没有相位数据（可重新生成）"))
		var b := _btn("生成相位", func(): EditorApp.apply_phases_at(node_id))
		b.pressed.connect(_refresh_later)
		_add_row("", b)
		return
	var sig: Dictionary = doc.signals[si]
	var plans: Array = sig.get("plans", [])
	var cyc := _cycle_seconds(sig)
	var n0: int = (plans[0].get("stages", []) as Array).size() if not plans.is_empty() else 0
	_add_row("阶段数", _plan_option(node_id, n0))
	if _plan_warn != "":
		_add_row("", _warn_label(_plan_warn))
	# ★ 时变配时（E92 T2）：加一个"从第 N 秒起生效"的方案，默认复制当前方案的阶段结构
	var add_bar := HBoxContainer.new()
	add_bar.add_theme_constant_override("separation", 4)
	var add_btn := _btn("＋ 添加方案", func(): _add_plan(node_id))
	add_btn.tooltip_text = "时变配时：复制默认方案的阶段，加一个从「上一方案 + 一个周期」起生效的方案"
	add_btn.pressed.connect(_refresh_later)      # ★ 延后重建，别拆掉正在发信号的按钮
	add_bar.add_child(add_btn)
	_add_row("时变配时", add_bar)
	if plans.size() > 1:
		# ★ 建成常驻 label（空文本时隐藏），改 start_time 就就地刷新 —— 别重建面板，
		#   否则正在编辑的 SpinBox 会被拆掉（L 记：第一次就是漏了这一步，红字永远不出现）
		_plan_time_label = _warn_label("")
		_add_row("", _plan_time_label)
		_refresh_plan_time_warn()
	# ★ 冲突判定只信 SimCore（ConflictMatrix，经桥接 ConflictsAt）；这里只做"是否同段 protected"的过滤。
	# ★ P5 加载态：`fetch_conflict_pairs` 在大图上要 ~600 ms（SimCore Load + Compile），**不能同步跑** ——
	#   同步跑的话面板要等它算完才第一次画出来，用户看到的是"点了路口，卡住不动"。
	#   这里只摆一行"正在计算冲突…"，计算推迟到面板画出来之后，算完由 `_fill_conflicts_later()` 就地回填。
	var pairs: Array = []
	_clash_node = node_id
	_clash_status = _info("正在计算冲突…")
	_add_row("冲突检查", _clash_status)
	_offset_spin = _phase_spin(float(sig.get("offset", 0.0)), 0.0, cyc,
		func(v): _set_signal_offset(node_id, v))
	_offset_spin.suffix = " s"
	_add_row("相位差 offset", _offset_spin)
	for pi in plans.size():
		var pl: Dictionary = plans[pi]
		if plans.size() > 1:                    # 时变配时才有"方案"这一行
			_add_row("方案 %d" % (pi + 1), _plan_row(node_id, pi, pl))
		var stages: Array = pl.get("stages", [])
		# ★ 相位条（P3-2）：一圈周期怎么切，列表看不出来，条形一眼就能看出谁吃掉大半周期。
		#   点某一段 = 展开该阶段的勾选表（和「展开」按钮同一个回调），不新增任何写数据能力。
		var bar := TimelineBar.new()
		bar.set_stages(_stage_bar_data(stages, doc, node_id, pi), _stage_index_of(pi))
		bar.stage_picked.connect(func(k: int): _open_stage_toggle(pi, k))
		_add_row("相位条" if plans.size() == 1 else "　相位条", bar)
		_stage_bars.append({"bar": bar, "plan": pi, "node": node_id})
		for k in stages.size():
			_add_row("阶段 %d" % (k + 1), _stage_block(node_id, pi, k, stages[k], stages.size(), pairs))
	var lost := _turns_never_green(doc, node_id, plans)
	if not lost.is_empty():
		_add_row("未放行", _warn_label("所有阶段都没放行：%s" % _turn_digest(doc, lost)))
	_cycle_label = _info("%.0f s（各阶段绿灯之和）" % cyc)
	_add_row("周期", _cycle_label)
	_fill_conflicts_later(node_id)

## 相位条的每段数据：[{seconds, label, warn}]。warn = 这一段一条 turn 都没放行
func _stage_bar_data(stages: Array, doc: Doc, node_id: int, pi: int) -> Array:
	var out: Array = []
	for k in stages.size():
		var st: Dictionary = stages[k]
		var empty: bool = (st.get("protected", []) as Array).is_empty() \
			and (st.get("permitted", []) as Array).is_empty()
		out.append({
			"seconds": float(st.get("duration", 0.0)),
			"label": "%d 段" % (k + 1),
			"warn": empty,
		})
	return out


## 当前展开的阶段属于哪个方案 → 给相位条标"选中段"（-1 = 本方案没有展开的段）
func _stage_index_of(pi: int) -> int:
	if _open_stage == "":
		return -1
	var parts := _open_stage.split("-")
	if parts.size() != 2 or int(parts[0]) != pi:
		return -1
	return int(parts[1])


## ★ E91 C18：把相位条与「周期」按当前 Doc 重算一遍 —— **不重建面板**。
##   为什么不靠重建：时长**故意不进指纹**（进了的话，拖 SpinBox 的每一格都会把正在拖的控件拆掉，W4 的裁定），
##   所以"数字变了图不变"只能自己刷。改时长 / 改流量都走这里。
func _refresh_stage_bars(node_id: int) -> void:
	if _stage_bars.is_empty() and _cycle_label == null:
		return
	var doc: Doc = EditorApp.doc
	var si := doc.find_signal_index(node_id)
	if si < 0:
		return
	var sig: Dictionary = doc.signals[si]
	var plans: Array = sig.get("plans", [])
	for e in _stage_bars:
		var bar: Variant = e.get("bar")
		if not (bar is TimelineBar) or not is_instance_valid(bar):
			continue
		var pi := int(e.get("plan", 0))
		if pi >= plans.size():
			continue
		bar.set_stages(_stage_bar_data(plans[pi].get("stages", []), doc, node_id, pi), _stage_index_of(pi))
	if _cycle_label != null and is_instance_valid(_cycle_label):
		_cycle_label.text = "%.0f s（各阶段绿灯之和）" % _cycle_seconds(sig)


## ★ E91 C18：需求折线同理 —— 流率不进指纹，改完要自己刷。
func _refresh_curve_previews(kind: String, key_value: int) -> void:
	if _curve_prevs.is_empty():
		return
	var rec: Dictionary = EditorApp.doc.find_profile(kind, key_value)
	if rec.is_empty():
		return
	for e in _curve_prevs:
		if String(e.get("kind", "")) != kind or int(e.get("key", -1)) != key_value:
			continue
		var prev: Variant = e.get("prev")
		if not (prev is CurvePreview) or not is_instance_valid(prev):
			continue
		prev.set_data(rec.get("time_points", []), rec.get("flow_rates", []))


## 方案行右侧：「起 <秒> s」+（非默认方案）删除按钮
## ★ plan 0 的 start_time 置灰：默认方案固定从 0 s 起，否则开局那段没有方案生效
func _plan_row(node_id: int, pi: int, pl: Dictionary) -> HBoxContainer:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 4)
	row.add_child(_info("起"))
	var sp := _phase_spin(float(pl.get("start_time", 0.0)), 0.0, 86400.0,
		func(v): _set_plan_start(node_id, pi, v))
	sp.suffix = " s"
	sp.custom_minimum_size = Vector2(88, 0)
	sp.editable = pi > 0
	sp.tooltip_text = "这个方案从第几秒开始生效"
	row.add_child(sp)
	if pi > 0:
		var del := _btn("✕", func(): _delete_plan(node_id, pi))
		del.tooltip_text = "删掉这个方案（默认方案不能删）"
		del.pressed.connect(_refresh_later)
		row.add_child(del)
	return row

func _add_plan(node_id: int) -> void:
	var doc: Doc = EditorApp.doc
	var si := doc.find_signal_index(node_id)
	if si < 0:
		return
	var cyc: float = _cycle_seconds(doc.signals[si])
	var base := 0.0
	for p in doc.signals[si].get("plans", []):
		base = maxf(base, float(p.get("start_time", 0.0)))
	var at := doc.add_signal_plan(node_id, base + cyc)
	if at < 0:
		print("添加方案失败：这个路口没有可复制的相位（先点「生成相位」）")
		return
	print("已加方案 %d：从 %.0f s 起（阶段结构复制自默认方案，之后可单独改）" % [at + 1, base + cyc])

func _set_plan_start(node_id: int, plan_i: int, v: float) -> void:
	EditorApp.doc.set_signal_plan_start(node_id, plan_i, v)
	_refresh_plan_time_warn()          # ★ 就地刷新，不重建面板

## 方案时刻提示的就地刷新：没有 label（只有一个方案）或换过路口 → 什么都不做
func _refresh_plan_time_warn() -> void:
	if _plan_time_label == null:
		return
	var doc: Doc = EditorApp.doc
	var si := doc.find_signal_index(_stage_nid)
	if si < 0:
		_plan_time_label.text = ""
		_plan_time_label.visible = false
		return
	var w := _plan_time_warn(doc.signals[si].get("plans", []))
	_plan_time_label.text = w
	_plan_time_label.visible = w != ""

func _delete_plan(node_id: int, plan_i: int) -> void:
	if EditorApp.doc.remove_signal_plan(node_id, plan_i):
		print("已删除方案 %d（Ctrl+Z 可撤销）" % (plan_i + 1))

## 方案生效时刻体检：重复 → 其中一个永远不会生效（仿真按 start 最大的那个选）
## ★ 顺序不影响结果（SimCore 按值选），所以"不递增"不提示
func _plan_time_warn(plans: Array) -> String:
	var seen := {}
	var dup := false
	for p in plans:
		var v := float(p.get("start_time", 0.0))
		if seen.has(v):
			dup = true
		seen[v] = true
	if dup:
		return "⚠ 有两个方案 start_time 相同 → 其中一个永远不会生效"
	return ""

## 「2 阶段 / 4 阶段」下拉：选 4 阶段 = 用 SimCore 的 plans[1] 整份重写（★ offset 保留）
func _plan_option(node_id: int, stages_now: int) -> OptionButton:
	var o := OptionButton.new()
	o.add_item("2 阶段（默认）")
	o.add_item("4 阶段（左转独立保护）")
	o.select(1 if stages_now >= 4 else 0)      # 先 select 再连信号，避免多余写回
	o.item_selected.connect(func(_i): EditorApp.begin_edit())          # ★ 先记快照
	o.item_selected.connect(func(i: int): _set_plan_count(node_id, i))  # 0 = 2 阶段 / 1 = 4 阶段
	o.item_selected.connect(func(_i): EditorApp.end_edit())            # ★ 改完判定
	return o

func _set_plan_count(node_id: int, plan_i: int) -> void:
	if EditorApp.apply_phases_at(node_id, true, plan_i):
		_plan_warn = ""
	else:
		_plan_warn = "切不到 %d 阶段：这个路口没有这套方案，已保持原样" % (2 + plan_i * 2)
	_refresh_later()


## 该阶段 protected 内部互冲的对（判定来自 SimCore，这里只按"同在本段 protected"过滤）
func _stage_conflicts(st: Dictionary, pairs: Array) -> Array:
	var prot: Array = st.get("protected", [])
	var out: Array = []
	for p in pairs:
		if prot.has(int(p[0])) and prot.has(int(p[1])):
			out.append(p)
	return out

## 红字：列出哪两条 turn 互冲（不写进文件、不计入 validation_errors）。
## ★ `pairs` 为空时返回**空文本 + 隐藏**的 label：冲突是异步算的（P5 加载态），先占好位再回填。
func _clash_label(doc: Doc, pairs: Array) -> Label:
	var l := _wrapped("" if pairs.is_empty() else _clash_text(doc, pairs))
	l.add_theme_color_override("font_color", Color(1.0, 0.35, 0.35))
	l.visible = not pairs.is_empty()
	return l


## 冲突红字的正文（建 label 与异步回填共用这一份文案）
func _clash_text(doc: Doc, pairs: Array) -> String:
	var parts: Array = []
	for p in pairs:
		parts.append("%s × %s" % [_turn_label(doc, int(p[0])), _turn_label(doc, int(p[1]))])
	return "⚠ 保护冲突（同一阶段两股车流会撞）：%s —— 仅提示，不拦导出" % "，".join(parts)


## ★ P5 加载态：把冲突查询推到"面板画出来之后"再跑，算完**就地改文本**（不重建面板）。
##   大图一次约 600 ms，慢的是 SimCore 的 Load + Compile，面板这边没法加速，只能让等待可见。
func _fill_conflicts_later(node_id: int) -> void:
	await get_tree().process_frame          # 第一帧：面板与"正在计算冲突…"先画出来
	await get_tree().process_frame
	if not is_inside_tree() or _clash_node != node_id:
		return                              # 期间换了选中 / 面板已重建 → 这份结果作废
	var doc: Doc = EditorApp.doc
	if doc == null:
		return
	var t0 := Time.get_ticks_msec()
	var pairs: Array = EditorApp.fetch_conflict_pairs(node_id)
	var cost := Time.get_ticks_msec() - t0
	if _clash_node != node_id:              # 算的这段时间里换了对象（罕见，但别把旧结果写进新面板）
		return
	if _clash_status != null and is_instance_valid(_clash_status):
		_clash_status.text = "未发现同段互冲" if pairs.is_empty() \
			else "%d 对候选，见各阶段红字" % pairs.size()
	for key in _clash_labels:
		var e: Dictionary = _clash_labels[key]
		var lbl: Label = e["label"]
		if not is_instance_valid(lbl):
			continue
		var clash := _stage_conflicts(e["st"], pairs)
		lbl.text = "" if clash.is_empty() else _clash_text(doc, clash)
		lbl.visible = not clash.is_empty()
	if cost >= 120:
		print("[属性面板] 冲突查询 %d ms（%d 对）" % [cost, pairs.size()])

## 黄字提示（不写文件、不进 validation_errors、不拦导出）；红字留给 _clash_label
## 警告 label。text 为空时只建一个隐藏的空 label（供"就地刷新"复用，别在空文本上留个光秃秃的 ⚠）
func _warn_label(text: String) -> Label:
	var l := _wrapped(text if text == "" else "⚠ %s" % text)
	l.add_theme_color_override("font_color", Color(1.0, 0.8, 0.3))
	if text == "":
		l.visible = false
	return l


## 一个阶段 = 一张凹槽卡（P3-2）：时长 + 类型 + 结构按钮一行 / 放行摘要 / 展开勾选表
## ★ 卡片不是新控件、也不写数据：里面还是原来那几个 SpinBox 和按钮，只是**外面套一层底**，
##   让"一段"在长卷里有明确边界（以前四五行连成一片，分不清哪行属于哪一段）。
func _stage_block(node_id: int, plan_i: int, stage_i: int, st: Dictionary, count: int,
		pairs: Array = []) -> PanelContainer:
	var doc: Doc = EditorApp.doc
	# ★ 用**凹槽**（`Clay.inset`）而不是 `Clay.plate`：plate 的 9-patch 内容边距是 36，
	#   一张小卡白吃 72 px 宽，最小坞宽下直接顶出右边缘。
	#   胶囊关态看不清的问题改在主题里解决（`PillToggle` 关态底色 SURFACE_3，2026-10-28）。
	var card := Clay.inset(UITokens.S2)
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", UITokens.S1)
	card.add_child(box)

	# ★ 时长 + 类型 一行、四个结构按钮**另一行**：塞成一行时卡片最小宽度 = 84+40+4×40+间距 ≈ 320，
	#   比坞能给的还宽（最小坞宽下只有 ~196），会把整块面板撑出右边缘。两行是这里唯一装得下的排法。
	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", UITokens.S2)
	box.add_child(top)
	var sp := _phase_spin(float(st.get("duration", 30.0)), 1.0, 600.0,
		func(v): _set_stage_duration(node_id, plan_i, stage_i, v))
	sp.suffix = " s"
	sp.custom_minimum_size = Vector2(84, 0)
	top.add_child(sp)
	var ty := _info(String(st.get("type", "Fixed")))
	ty.theme_type_variation = "HintLabel"
	ty.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	top.add_child(ty)
	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(spacer)
	var ops := HBoxContainer.new()
	ops.add_theme_constant_override("separation", UITokens.S1)
	ops.alignment = BoxContainer.ALIGNMENT_END
	box.add_child(ops)
	# ★ 结构按钮：一次点击 = 一步撤销（_btn 内部就是 begin → 改 → end）
	var up := _btn("↑", func(): _stage_op(node_id, plan_i, stage_i, "up"))
	up.tooltip_text = "上移一段"
	up.disabled = stage_i <= 0
	var dn := _btn("↓", func(): _stage_op(node_id, plan_i, stage_i, "down"))
	dn.tooltip_text = "下移一段"
	dn.disabled = stage_i >= count - 1
	var ins := _btn("＋", func(): _stage_op(node_id, plan_i, stage_i, "insert"))
	ins.tooltip_text = "在下面插入一个阶段（25 s / Fixed）"
	var del := _btn("✕", func(): _stage_op(node_id, plan_i, stage_i, "delete"))
	del.tooltip_text = "删掉这个阶段（只剩 1 段时不许删）"
	del.disabled = count <= 1
	for b in [up, dn, ins, del]:
		# ★ 不要压窄 + clip_text：次按钮样式的左右内边距各 16 px，宽度压到 34 时内容区只剩 2 px，
		#   符号会被裁得一个都看不见（第一版就是这样，四个按钮变成四个空白圆）。
		b.custom_minimum_size = Vector2(40, 30)
		ops.add_child(b)

	box.add_child(_wrapped("保护 %s" % _turn_digest(doc, st.get("protected", []))))
	box.add_child(_wrapped("让行 %s" % _turn_digest(doc, st.get("permitted", []))))
	# ★ 红字**总是建一个**（没冲突时隐藏）：冲突是异步算的，算完要就地改这几行，不能重建面板
	var clash_l := _clash_label(doc, _stage_conflicts(st, pairs))
	box.add_child(clash_l)
	_clash_labels[_stage_key(plan_i, stage_i)] = {"label": clash_l, "st": st}
	# 展开：勾这个路口每条 turn 的归属（同一 turn 只能在一列）；只开一个
	var is_open := _open_stage == _stage_key(plan_i, stage_i)
	# ★ 文案要短：`Button` 的最小宽度 = 文字宽（`clip_text` 只裁绘制、不压最小宽），
	#   原来那句「展开 ▾（勾保护 / 让行）」会把卡片最小宽顶到 243，把整块面板撑出右边缘。
	#   解释放 tooltip，按钮上只留动作。
	var tb := Clay.btn_ghost("收起 ▴" if is_open else "展开 ▾")
	tb.alignment = HORIZONTAL_ALIGNMENT_LEFT
	tb.focus_mode = Control.FOCUS_NONE
	tb.tooltip_text = "展开/收起本阶段的勾选表（保护 / 让行）"
	tb.pressed.connect(func(): _open_stage_toggle(plan_i, stage_i))
	box.add_child(tb)
	if is_open:
		box.add_child(_turn_grid(doc, node_id, plan_i, stage_i, st))
	return card

## 展开后的勾选表：一行一条 turn → [12#左转] [保护胶囊] [让行胶囊]
## ★ 二次过街的两段**必须分开成两行**（各有各的相位），并紧邻排列 + 标注所属安全岛
## ★ P3-2：两个勾选框换成**胶囊**（「保护」「让行」写在胶囊上）——
##   表头行原来只是给两个空方框做解释，现在信息在胶囊里，删掉表头省一行高。
##   勾选语义**一字未改**：仍是 `signal_stage_set_turn(..., "protected"/"permitted"/"")`。
func _turn_grid(doc: Doc, node_id: int, plan_i: int, stage_i: int, st: Dictionary) -> VBoxContainer:
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", UITokens.S1)
	var ids: Array = []
	for t in doc.turns:
		if int(t.node) == node_id:
			ids.append(int(t.id))
	if ids.is_empty():
		box.add_child(_info("该路口还没有连接（先按 G 生成连接）"))
		return box
	var prot: Array = st.get("protected", [])
	var perm: Array = st.get("permitted", [])
	# ★ 两段的行要挨着：前段后段成组输出，组的顺序仍按 turn 出现顺序
	for tid in _paired_turn_order(doc, ids):
		var t: Dictionary = doc.find_turn(int(tid))
		var row := HBoxContainer.new()
		row.add_theme_constant_override("separation", UITokens.S1)
		row.add_child(_fixed_label(_turn_label(doc, tid), 80))
		row.add_child(_stage_check(node_id, plan_i, stage_i, tid, "protected", prot.has(tid)))
		row.add_child(_stage_check(node_id, plan_i, stage_i, tid, "permitted", perm.has(tid)))
		box.add_child(row)
		if Doc.is_split_turn(t):
			# 岛容量 = 后段能不能进得去，配相位时得看得见（前段行下面标一次）
			if int(t.get("leg", 0)) == 0:
				box.add_child(_info("   ↳ 中隔安全岛 #%d（容量 %d 人）" % [
					int(t.get("island", -1)), _island_capacity(doc, int(t.get("island", -1)))]))
	return box

## 把 turn id 列表排成「前段、后段」相邻的顺序；不成对的照原样留在原位
func _paired_turn_order(doc: Doc, ids: Array) -> Array:
	var out: Array = []
	var done := {}
	for tid in ids:
		if done.has(int(tid)):
			continue
		var t: Dictionary = doc.find_turn(int(tid))
		if Doc.is_split_turn(t):
			var a := int(doc.first_leg_turn(t).get("id", -1))
			var b := int(doc.find_pair_turn(t).get("id", -1))
			for x in [a, b]:
				if x >= 0 and not done.has(x):
					out.append(x)
					done[x] = true
			continue
		out.append(int(tid))
		done[int(tid)] = true
	return out

func _island_capacity(doc: Doc, island_id: int) -> int:
	var isl: Dictionary = doc.find_island(island_id)
	return int(isl.get("capacity", 0)) if not isl.is_empty() else 0

## 一条 turn × 一列：勾上 = 写进这一列（自动从另一列摘掉）；勾掉 = 两列都摘
## ★ P3-2 从 `CheckBox` 换成**胶囊**（`Clay.pill`）：空方框在奶油底上只有 14 px 的点按区、
##   也看不出"这列是保护还是让行"。胶囊把名字写在上面、点整块就行。
##   `Button.toggled` 与 `CheckBox.toggled` 签名一致（`toggled(on)`），所以下面三行连线**一字未动**。
func _stage_check(node_id: int, plan_i: int, stage_i: int, turn_id: int,
		bucket: String, on: bool) -> Button:
	var c := Clay.pill("保护" if bucket == "protected" else "让行", on)
	# 50 = 两字 @15px（~30）+ 胶囊左右内边距（8+8）+ 余量；再窄就裁字
	c.custom_minimum_size = Vector2(50, 26)
	c.focus_mode = Control.FOCUS_NONE          # 别抢键盘焦点：配相位时还要按 1~4 / G / Esc
	c.tooltip_text = "保护放行" if bucket == "protected" else "允许通行（须让行）"
	c.toggled.connect(func(_on): EditorApp.begin_edit())          # ★ 先记快照
	c.toggled.connect(_on_stage_turn_toggled.bind(node_id, plan_i, stage_i, turn_id, bucket))
	c.toggled.connect(func(_on): EditorApp.end_edit())            # ★ 改完判定
	return c

func _on_stage_turn_toggled(on: bool, node_id: int, plan_i: int, stage_i: int,
		turn_id: int, bucket: String) -> void:
	var doc: Doc = EditorApp.doc
	doc.signal_stage_set_turn(node_id, plan_i, stage_i, turn_id, bucket if on else "")
	doc.dirty = true
	print("节点 %d 阶段 %d：%s%s（%s）" % [
		node_id, stage_i + 1, _turn_label(doc, turn_id),
		" → 勾" if on else " → 取消", "保护" if bucket == "protected" else "让行"])
	_refresh_later()                   # 另一列的勾要跟着变 → 延后一拍重建（别拆掉自己）

## 阶段上移 / 下移 / 插入 / 删除：改完延后一拍重建面板
func _stage_op(node_id: int, plan_i: int, stage_i: int, op: String) -> void:
	var doc: Doc = EditorApp.doc
	var ni := doc.signal_stage_op(node_id, plan_i, stage_i, op)
	if ni < 0:
		return
	doc.dirty = true
	if op == "insert":
		_open_stage = _stage_key(plan_i, ni)          # 新段直接展开，方便马上勾 turn
	elif op == "delete":
		_open_stage = ""
	else:                                             # 上移/下移：展开状态跟着那一段走
		var moved := _stage_key(plan_i, stage_i)
		var target := _stage_key(plan_i, ni)
		if _open_stage == moved:
			_open_stage = target
		elif _open_stage == target:
			_open_stage = moved
	print("节点 %d：阶段 %s（%d → %d）" % [node_id, op, stage_i + 1, ni + 1])
	_refresh_later()

## 展开/收起某阶段（不动 Doc → 不占撤销步）
func _open_stage_toggle(plan_i: int, stage_i: int) -> void:
	var k := _stage_key(plan_i, stage_i)
	_open_stage = "" if _open_stage == k else k
	_refresh_later()

func _stage_key(plan_i: int, stage_i: int) -> String:
	return "%d-%d" % [plan_i, stage_i]

## 单条连接的显示名："12#左转"；二次过街的两段加后缀 → "12#人行横道·前段" / "13#人行横道·后段"
## ★ 两段各自是独立的 turn，各自配相位，所以标签必须能分辨（N6b）
func _turn_label(doc: Doc, turn_id: int) -> String:
	var t: Dictionary = doc.find_turn(int(turn_id))
	var ty := String(t.get("type", ""))
	var i := TURN_TYPES.find(ty)
	var base := "%d#%s" % [int(turn_id), TURN_LABELS[i] if i >= 0 else ty]
	if not Doc.is_split_turn(t):
		return base
	return "%s·%s" % [base, "后段" if int(t.get("leg", 0)) != 0 else "前段"]

func _fixed_label(text: String, w: float) -> Label:
	var l := _info(text)
	l.custom_minimum_size = Vector2(w, 0)
	return l

## 配时专用 SpinBox：**一次输入 = 一步撤销**
##   value_changed 首次触发才 begin_edit（整段输入只记一次快照），失焦时 end_edit 收口。
##   面板里其它 _spin 是"每次触发都 begin/end + 300ms 合并"，配时不能那么算。
func _phase_spin(value: float, min_v: float, max_v: float, cb: Callable) -> SpinBox:
	var s := SpinBox.new()
	s.min_value = min_v
	s.max_value = max_v
	s.step = 1.0
	s.value = value                    # ★ 先赋值再连信号，避免触发多余写回
	s.value_changed.connect(func(_v): _phase_begin())
	s.value_changed.connect(cb)
	s.focus_exited.connect(_phase_end)
	s.get_line_edit().focus_exited.connect(_phase_end)   # ★ 真正拿到焦点的是内嵌 LineEdit
	return s

func _phase_begin() -> void:
	if _phase_edit_open:
		return
	_phase_edit_open = true
	EditorApp.begin_edit()

func _phase_end() -> void:
	if not _phase_edit_open:
		return
	_phase_edit_open = false
	EditorApp.end_edit()

## 改路口级相位差：夹在 0~周期秒
func _set_signal_offset(node_id: int, v: float) -> void:
	var doc: Doc = EditorApp.doc
	var si := doc.find_signal_index(node_id)
	if si < 0:
		return
	var sig: Dictionary = doc.signals[si]
	sig["offset"] = clampf(v, 0.0, _cycle_seconds(sig))
	doc.dirty = true

## 改某方案某阶段的绿灯时长：必须 > 0（stage_duration_non_positive 是 Error）
func _set_stage_duration(node_id: int, plan_i: int, stage_i: int, v: float) -> void:
	var doc: Doc = EditorApp.doc
	var si := doc.find_signal_index(node_id)
	if si < 0:
		return
	var plans: Array = doc.signals[si].get("plans", [])
	if plan_i >= plans.size():
		return
	var stages: Array = plans[plan_i].get("stages", [])
	if stage_i >= stages.size():
		return
	stages[stage_i]["duration"] = maxf(v, 1.0)
	doc.dirty = true
	if _offset_spin != null:                      # 周期变了 → offset 的上限跟着走
		_offset_spin.max_value = _cycle_seconds(doc.signals[si])
	_refresh_stage_bars(node_id)                  # ★ C18：条的比例与「周期」跟着改

## 周期 = 第一个方案各阶段绿灯之和（黄灯/全红按 schema 不写文件，`spmap-schema.md` §4.5）
func _cycle_seconds(sig: Dictionary) -> float:
	var plans: Array = sig.get("plans", [])
	if plans.is_empty():
		return 0.0
	var s := 0.0
	for st in plans[0].get("stages", []):
		s += float(st.get("duration", 0.0))
	return maxf(s, 1.0)

## turn 摘要："12#左转 15#直行"（拿不到类型就原样给英文）
func _turn_digest(doc: Doc, ids) -> String:
	var arr: Array = ids
	if arr.is_empty():
		return "—"
	var parts: Array = []
	for v in arr:
		parts.append(_turn_label(doc, int(v)))
	return "  ".join(parts)

# --- 车辆流与需求（traffic）---------------------------------------------

const PROFILE_TITLES := {"demand": "机动车流", "pedestrians": "行人过街", "bicycles": "非机动车流"}
const PROFILE_UNITS := {"demand": "辆/秒", "pedestrians": "人/秒", "bicycles": "辆/秒"}

var _refresh_queued := false

## kind 由 `_panel_target()` 裁决（"traffic_turn" = 过街连接 / "traffic_lane" = 车道）
func _build_traffic(kind: String, id: int) -> void:
	var doc: Doc = EditorApp.doc
	if kind == "traffic_turn":
		var t: Dictionary = doc.find_turn(id)
		if t.is_empty():
			return
		_header("流", "过街流量", "#%d" % id)
		_section("基本信息")
		_add_row("过街连接", _info("#%d（%s）" % [int(t.id), String(t.type)]))
		_add_row("所属路口", _info("#%d" % int(t.node)))
		if String(t.type) == "Crosswalk":
			_section("需求曲线")
			_build_profile("pedestrians", int(t.id))
		else:
			_add_row("提示", _info("不是人行横道转向，不能挂行人需求"))
		return
	var l: Dictionary = doc.find_lane(id)
	if l.is_empty():
		return
	var lt := String(l.type)
	_header("流", "车道流量", "#%d" % id)
	_section("基本信息")
	_add_row("车道", _info("#%d（%s）" % [int(l.id), lt]))
	_add_row("方向", _info("%d → %d" % [int(l.from_node), int(l.to_node)]))
	_add_row("长度 (m)", _info("%.2f" % float(l.length)))
	_section("需求曲线")
	if lt == "Driving":
		_build_profile("demand", int(l.id))
		_build_profile("bicycles", int(l.id))     # Driving 车道上的 bicycles 记录 = 机非混行
	elif lt == "Bus" or lt == "SharedLeftTurn":
		_build_profile("demand", int(l.id))       # 机动车道：只配机动车流（混行只允许 Biking/Driving）
	elif lt == "Biking":
		_build_profile("bicycles", int(l.id))
	elif lt == "Sidewalk":
		_add_row("提示", _info("人行道不挂需求；行人需求挂在人行横道转向上"))
	else:
		_add_row("提示", _info("该车道类型没有可配的需求"))
	# ★ 专业模式：车型构成 + 转率 + 检测设施（全部挂在车道上，跟选中对象走）
	if lt in Doc.MOTOR_LANE_TYPES:
		_build_sumo_fleet(int(l.id))
		_build_facilities(int(l.id))
		_build_sumo_lane(int(l.id))       # S4：车道允许车型 / 限速 / 宽度覆盖

## 一类需求曲线：有记录就出表格，没有就给一个"＋ 添加"
func _build_profile(kind: String, key_value: int) -> void:
	var doc: Doc = EditorApp.doc
	var rec := doc.find_profile(kind, key_value)
	_add_row("%s（%s）" % [PROFILE_TITLES[kind], PROFILE_UNITS[kind]],
		_info("—" if rec.is_empty() else "%d 段" % (rec.get("time_points", []) as Array).size()))
	if kind == "bicycles" and not rec.is_empty():
		_add_row("　模式", _info(String(rec.get("mode", ""))))
	if rec.is_empty():
		_add_row("", _btn("＋ 添加%s" % PROFILE_TITLES[kind], func(): _profile_make(kind, key_value)))
		return
	var times: Array = rec.get("time_points", [])
	var rates: Array = rec.get("flow_rates", [])
	# ★ 折线预览（P3-2）：时段表只有数字，"平稳"和"早晚高峰"在表里长得一样，一条折线就看出来了。
	#   纯展示（`CurvePreview` 不读也不写 Doc），鼠标穿透，不抢画布的点击。
	var prev := CurvePreview.new()
	prev.set_data(times, rates)
	_add_row("曲线", prev)
	_curve_prevs.append({"prev": prev, "kind": kind, "key": key_value})
	# 数值表：默认展开（与 P3-1「默认全展开」同口径），想收起来可点这一行；
	# ★ 折叠状态是**会话级**（不落盘）：这里没有稳定的小节名可以当键，硬造一个反而会互相串。
	var toggle := Clay.btn_ghost("▾ 数值表")
	toggle.alignment = HORIZONTAL_ALIGNMENT_LEFT
	toggle.focus_mode = Control.FOCUS_NONE
	_add_row("", toggle)
	var table := VBoxContainer.new()
	table.add_theme_constant_override("separation", UITokens.S2)
	(_body if _body != null else self).add_child(table)
	var outer: VBoxContainer = _body
	_body = table            # ← 下面几行全进 table（_add_row 只认 _body）
	for i in times.size():
		# ★ 这一行**自带窄标签**（76 px）而不是走 field_row 的 112 px 字段名列：
		#   两个 SpinBox 的最小宽各约 100（内部 LineEdit + 箭头），112+8+243 会超出坞宽。
		#   表格本来就是缩进的，标签放行内反而更像"表"。
		var box := HBoxContainer.new()
		box.add_theme_constant_override("separation", UITokens.S1)
		box.add_child(_fixed_label("时段 %d" % (i + 1), 76))
		var st := _spin(float(times[i]), 0.0, 86400.0, 1.0,
			func(v): _profile_set(kind, key_value, i, true, v))
		st.custom_minimum_size = Vector2(64, 0)
		box.add_child(st)
		var sr := _spin(float(rates[i]), 0.0, 10.0, 0.01,
			func(v): _profile_set(kind, key_value, i, false, v))
		sr.custom_minimum_size = Vector2(56, 0)
		box.add_child(sr)
		var delrow := _btn("✕", func(): _profile_del_row(kind, key_value, i))
		delrow.theme_type_variation = "GhostButton"     # 12 px 内边距（次按钮是 16），省 8 px 宽
		delrow.custom_minimum_size = Vector2(32, 0)
		box.add_child(delrow)
		_add_row("", box)
	# === 数值表到此为止 ===
	_body = outer                              # ★ 必须还回去，否则后面所有行都会塞进 table
	var bar := HBoxContainer.new()
	bar.add_child(_btn("＋ 加一段", func(): _profile_add_row(kind, key_value)))
	bar.add_child(_btn("清空", func(): _profile_clear(kind, key_value)))
	_add_row("", bar)
	toggle.pressed.connect(func():
		table.visible = not table.visible
		toggle.text = "▾ 数值表" if table.visible else "▸ 数值表")
	# ★ 批量套用（B21）：把这一条的曲线抄给同组，省得一条条敲。
	#   两个作用域各自一个按钮：本路同类 / 本路口全部进口（行人只有"本路口全部过街"这一档）。
	# ★ 竖排：两个按钮并排时这一行最小宽 332（+字段名 = 452），最小坞宽下必被裁
	var bar2 := VBoxContainer.new()
	bar2.add_theme_constant_override("separation", UITokens.S1)
	if kind == "pedestrians":
		var pb := _btn("↧ 套用到本路口全部过街",
			func(): EditorApp.batch_profile(kind, key_value, "junction"))
		pb.pressed.connect(_refresh_later)
		bar2.add_child(pb)
	else:
		var rb := _btn("↧ 套用到本路同类",
			func(): EditorApp.batch_profile(kind, key_value, "road"))
		rb.pressed.connect(_refresh_later)
		bar2.add_child(rb)
		var jb := _btn("↧ 套用到本路口进口",
			func(): EditorApp.batch_profile(kind, key_value, "junction"))
		jb.pressed.connect(_refresh_later)
		bar2.add_child(jb)
	_add_row("批量", bar2)

## 新建一条默认曲线（恒定 = 只有 1 段）
func _profile_make(kind: String, key_value: int) -> void:
	var mode := ""
	if kind == "bicycles":
		var l: Dictionary = EditorApp.doc.find_lane(key_value)   # ★ 显式类型，理由同 deco_mode.gd:52（autoload 循环依赖 + := 推断）
		mode = "Independent" if String(l.get("type", "")) == "Biking" else "Mixed"
	EditorApp.doc.write_profile(kind, key_value, [0.0], [0.1], mode)
	_refresh_later()        # ★ 延后一拍：现在待在"＋ 添加"按钮的 pressed 回调里

## 改某一行的时刻 / 流率
func _profile_set(kind: String, key_value: int, i: int, is_time: bool, v: float) -> void:
	var doc: Doc = EditorApp.doc
	var rec := doc.find_profile(kind, key_value)
	if rec.is_empty():
		return
	var times: Array = rec["time_points"].duplicate()
	var rates: Array = rec["flow_rates"].duplicate()
	if i >= times.size():
		return
	if is_time:
		times[i] = v
	else:
		rates[i] = maxf(v, 0.0)                     # ★ 流率非负
	_commit_profile(doc, kind, key_value, times, rates)
	_refresh_curve_previews(kind, key_value)        # ★ C18：折线要跟着数字走
	if is_time:
		_refresh_later()                            # ★ 时刻改了可能重排 → 延后一拍重建面板

## 加一段：时刻 = 上一段 +10 秒（自动保证严格递增）
func _profile_add_row(kind: String, key_value: int) -> void:
	var doc: Doc = EditorApp.doc
	var rec := doc.find_profile(kind, key_value)
	if rec.is_empty():
		return
	var times: Array = rec["time_points"].duplicate()
	var rates: Array = rec["flow_rates"].duplicate()
	times.append(float(times[times.size() - 1]) + 10.0)
	rates.append(float(rates[rates.size() - 1]))
	_commit_profile(doc, kind, key_value, times, rates)
	_refresh_later()        # ★ 延后一拍：现在待在"＋ 加一段"按钮的 pressed 回调里

## 删一行（两数组永远同步增删，不会不等长）
func _profile_del_row(kind: String, key_value: int, i: int) -> void:
	var doc: Doc = EditorApp.doc
	var rec := doc.find_profile(kind, key_value)
	if rec.is_empty():
		return
	var times: Array = rec["time_points"].duplicate()
	var rates: Array = rec["flow_rates"].duplicate()
	times.remove_at(i)
	rates.remove_at(i)
	if times.is_empty():
		times = [0.0]
		rates = [0.0]
	_commit_profile(doc, kind, key_value, times, rates)
	_refresh_later()        # ★ 延后一拍：现在待在行尾"✕"按钮的 pressed 回调里

## 清空（整条需求删掉）
func _profile_clear(kind: String, key_value: int) -> void:
	EditorApp.doc.remove_profile(kind, key_value)
	_refresh_later()        # ★ 延后一拍：现在待在"清空"按钮的 pressed 回调里

## 落盘前统一排序 + 去重 + 夹负值，保证曲线一定合法
func _commit_profile(doc: Doc, kind: String, key_value: int, times: Array, rates: Array) -> void:
	var pairs: Array = []
	for i in times.size():
		pairs.append([float(times[i]), maxf(float(rates[i]), 0.0)])
	pairs.sort_custom(func(a, b): return a[0] < b[0])
	var t2: Array = []
	var r2: Array = []
	var prev := -1.0
	for p in pairs:
		if p[0] <= prev:
			continue                                # 丢掉重复/倒退的时刻
		prev = p[0]
		t2.append(p[0])
		r2.append(p[1])
	var mode := ""
	if kind == "bicycles":
		mode = String(doc.find_profile(kind, key_value).get("mode", ""))
	doc.write_profile(kind, key_value, t2, r2, mode)

## 延后一拍刷新面板（避免销毁"正在发 value_changed 信号的 SpinBox"）
func _refresh_later() -> void:
	if _refresh_queued:
		return
	_refresh_queued = true
	call_deferred("_do_refresh")

func _do_refresh() -> void:
	_refresh_queued = false
	EditorApp.selection_changed.emit()

# 关卡设置（未选中任何对象时显示）

const VEHICLE_MIX_IDS := ["CarOnly", "CityMix", "FreightHeavy", "BusHeavy", "CarBusMix"]
const VEHICLE_MIX_LABELS := ["汽车城市", "混合城市", "货运城市", "工业城市", "普通城市"]
const VEHICLE_MIX_TIPS := [
	"小汽车 100% ｜ 车头间距 6.00 m ｜ 通行能力 100%",
	"小汽车 85% / 货车 12% / 公交 3% ｜ 6.53 m ｜ 92%",
	"小汽车 55% / 货车 45% ｜ 7.13 m ｜ 84%",
	"小汽车 70% / 货车 10% / 公交 20% ｜ 7.75 m ｜ 77%",
	"小汽车 92% / 公交 8% ｜ 6.60 m ｜ 91%",
]

## --- 评分锚点 / 指标权重（E92 G5a） ---------------------------------------
## 口径全部来自 `游戏设计v2.md` §6 与 `docs/spmap-schema.md`：
##   anchors.{R,D,P,Q} = [b, g]：b = 底线值（该项 0 分）、g = 优秀值（满分）；[0,0] = 未标定
##   R 越大越好（要求 g ≥ b）；D/P/Q 越小越好（要求 g ≤ b）；方向反了 → anchor_ordering_inverted
##   weights.{R,D,P,Q} 之和应为 1.00（weights_not_normalized 告警）、不得为负（weights_invalid = Error）
## ★ 上限按"物理可能值"给，不按示例值：拥挤进口道的延误能到几百秒，别让 SpinBox 夹住真实值。
const METRIC_KEYS := ["R", "D", "P", "Q"]
const METRIC_NAMES := ["R 通行服务率", "D 平均延误", "P 平均停车次数", "Q 最长排队"]
const METRIC_LARGER_BETTER := [true, false, false, false]
const METRIC_RANGES := [[0.0, 1.0, 0.01], [0.0, 900.0, 1.0], [0.0, 50.0, 0.1], [0.0, 1.0, 0.01]]
const METRIC_TIPS := [
	"通过单位数 ÷ 总需求单位数（0~1）。越大越好：优秀值 g 应 ≥ 底线值 b。",
	"每个单位的平均延误，单位秒。越小越好：优秀值 g 应 ≤ 底线值 b。",
	"每辆机动车 / 非机动车通过前的平均停车次数。越小越好：g ≤ b。",
	"最长排队 ÷ 该等候区容量（0~1，即 50% 填 0.5）。越小越好：g ≤ b。",
]
const DEFAULT_WEIGHTS := {"R": 0.4, "D": 0.2, "P": 0.2, "Q": 0.2}


func _build_level() -> void:
	var doc: Doc = EditorApp.doc
	var f: String = "（未保存）" if EditorApp.current_path == "" else EditorApp.current_path.get_file()
	# ★ 2026-11-06 老板要求：**专业模式下关卡设置默认收起**，只留「连接生成」「SUMO 路网生成」
	#   展开 —— 这两节是关卡级的入口，藏起来就找不到了；其余是低频配置，让位给车道/路口面板。
	#   基础模式维持原样（全部展开）。
	# ★ 折叠键统一加 `level/` 前缀：旧的 `ui_panel_folds.json` 里已经有 `"关卡信息": false`
	#   （"默认展开"时代留下的），不换键的话新默认值会被旧记录盖住、改了看不出效果。
	# ★ 不要写 `var fold := EditorApp.is_pro()`：autoload 推断类型会炸（GDScript 铁律）。
	var fold: bool = EditorApp.is_pro()
	var lk: String = "level/"
	_header("关", "关卡设置", f)
	# ★ P5 空状态引导：关卡里一条路都没有时，把"从哪开始"写在最上面。**不加新按钮**，
	#   只指路（每一条都对应顶栏已有的入口），所以它不会多出任何要维护的功能分支。
	if doc != null and doc.roads.is_empty():
		_add_row("", _empty_state_card())
	_section("关卡信息", fold, lk + "关卡信息")
	_add_row("关卡文件", _wrapped(f))
	_add_row("关卡名", _meta_line("name", String(doc.meta.get("name", "未命名"))))
	_add_row("章节", _meta_int("chapter", int(doc.meta.get("chapter", 1))))
	_add_row("关内序号", _meta_int("index", int(doc.meta.get("index", 1))))
	_add_row("作者", _meta_line("author", String(doc.meta.get("author", ""))))
	_add_row("规模", _wrapped("路 %d ｜ 节点 %d ｜ 车道 %d ｜ 连接 %d" % [
		doc.roads.size(), doc.nodes.size(), doc.lanes.size(), doc.turns.size()]))
	_add_row("车型构成", _mix_option(String(doc.game_rules.get("vehicle_mix", "CarOnly"))))
	_add_row("说明", _wrapped("这是**游戏侧**的车型构成（决定车长与通行能力，写进 .spmap）。"
		+ "SUMO 的车流车型在选中进口车道后配。"))
	_section("评分锚点", fold, lk + "评分锚点")
	_add_row("评分锚点", _wrapped("底线 b = 0 分，优秀 g = 满分；都填 0 = 未标定。"))
	for i in METRIC_KEYS.size():
		_add_row("锚点 " + String(METRIC_KEYS[i]), _anchor_pair(i))
	_anchor_warn = _wrapped("")
	_add_row("锚点检查", _anchor_warn)
	_section("指标权重", fold, lk + "指标权重")
	_add_row("指标权重", _wrapped("四项之和 = 1.00。"))
	for i in METRIC_KEYS.size():
		_add_row("权重 " + String(METRIC_KEYS[i]), _weight_spin(i))
	_weights_sum = _wrapped("")
	_add_row("权重合计", _weights_sum)
	_add_row("", _btn("权重恢复默认 40/20/20/20", _reset_weights))
	_add_row("", _btn("锚点全部清空（未标定）", _clear_anchors))
	_section("原始字段", fold, lk + "原始字段")
	for row in RAW_JSON_ROWS:
		_add_row(String(row["label"]), _raw_json_cell(String(row["key"])))
	_section("连接生成", false, lk + "连接生成")
	_add_row("含车辆掉头", _check(EditorApp.include_uturn, _on_include_uturn))
	_add_row("含行人过街", _check(EditorApp.include_crosswalk, _on_include_crosswalk))
	_add_row("说明", _wrapped("车型决定车长与限速；「章-序」决定选关排序。"))
	_add_row("连接生成", _wrapped("会话级，不写进文件；改完要再按一次 G。"))
	_refresh_anchor_warn()          # ★ 两个 label 在这里建好，立刻按当前数据填一次
	_refresh_weights_sum()
	_build_sumo_netgen()            # ★ S4：netconvert 的 4 个常用开关（关卡级）
	_build_sumo_run_config()        # ★ S5：.sumocfg 的时间 / 步长 / 种子 / 传送阈值


## 空状态引导卡（P5）：只在"一条路都没有"时出现，四条路全是**已有入口**的文字指路
func _empty_state_card() -> Control:
	var card := Clay.inset(UITokens.S3)
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", UITokens.S1)
	card.add_child(box)
	var t := Label.new()
	t.text = "空关卡 —— 从这里开始"
	t.theme_type_variation = "TitleLabel"
	t.add_theme_font_size_override("font_size", UITokens.T_BODY)
	box.add_child(t)
	for line in [
		"① 画路：切「道路」，在画布上拖。",
		"② 打开：「文件」→ 打开…（Ctrl+O）",
		"③ 描图：「视图」→ 载入底图…",
		"④ 连路口：切「连接」点路口，按 G",
	]:
		var l := _wrapped(line)
		l.theme_type_variation = "MutedLabel"
		l.add_theme_constant_override("line_spacing", UITokens.S1)
		box.add_child(l)
	return card


# --- 评分锚点：每项一行「[bottom] [good]」 ---------------------------------
func _anchor_pair(i: int) -> HBoxContainer:
	var m: String = METRIC_KEYS[i]
	var r: Array = METRIC_RANGES[i]
	var cur: Array = _anchor_of(m)
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 4)
	# ★ 每个数一个 tag：改「底线」和改「优秀值」是两件事，撤销时不能并成一步；
	#   同一字段连点箭头仍会并（tag 相同）。
	var b := _spin(float(cur[0]), float(r[0]), float(r[1]), float(r[2]),
		func(v: float): _write_anchor(m, 0, v), "anchor:%s:b" % m)
	var g := _spin(float(cur[1]), float(r[0]), float(r[1]), float(r[2]),
		func(v: float): _write_anchor(m, 1, v), "anchor:%s:g" % m)
	var tip: String = String(METRIC_NAMES[i]) + "\n" + String(METRIC_TIPS[i])
	for s in [b, g]:
		s.custom_minimum_size.x = 70
		s.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		s.tooltip_text = tip
		row.add_child(s)
	return row


func _weight_spin(i: int) -> SpinBox:
	var m: String = METRIC_KEYS[i]
	var s := _spin(_weight_of(m), 0.0, 1.0, 0.05, func(v: float): _set_weight(m, v), "weight:%s" % m)
	s.tooltip_text = String(METRIC_NAMES[i]) + " 的权重（0~1，四项之和应为 1.00）"
	return s


## 读一项锚点；畸形 / 缺键 / 长度不足都当"未标定"返回 [0,0]（绝不在这里改 Doc）
func _anchor_of(metric: String) -> Array:
	var a = EditorApp.doc.game_rules.get("anchors", {})
	if not (a is Dictionary):
		return [0.0, 0.0]
	var v = a.get(metric, [0, 0])
	if not (v is Array) or (v as Array).size() < 2:
		return [0.0, 0.0]
	return [float(v[0]), float(v[1])]


## which：0 = 底线 b，1 = 优秀 g。撤销由 `_spin` 的 begin/end_edit 包着
func _write_anchor(metric: String, which: int, v: float) -> void:
	var doc: Doc = EditorApp.doc
	if not (doc.game_rules.get("anchors") is Dictionary):
		doc.game_rules["anchors"] = {"R": [0.0, 0.0], "D": [0.0, 0.0], "P": [0.0, 0.0], "Q": [0.0, 0.0]}
	var anchors: Dictionary = doc.game_rules["anchors"]
	var arr = anchors.get(metric, [0.0, 0.0])
	if not (arr is Array) or (arr as Array).size() < 2:
		arr = [0.0, 0.0]
	arr[which] = v
	anchors[metric] = arr                  # ★ 显式写回：别依赖"取到的 Array 一定是同一份引用"
	doc.dirty = true
	_refresh_anchor_warn()


func _weight_of(metric: String) -> float:
	var w = EditorApp.doc.game_rules.get("weights", {})
	if not (w is Dictionary):
		return float(DEFAULT_WEIGHTS[metric])
	return float(w.get(metric, DEFAULT_WEIGHTS[metric]))


func _set_weight(metric: String, v: float) -> void:
	var doc: Doc = EditorApp.doc
	if not (doc.game_rules.get("weights") is Dictionary):
		doc.game_rules["weights"] = DEFAULT_WEIGHTS.duplicate(true)
	doc.game_rules["weights"][metric] = v
	doc.dirty = true
	_refresh_weights_sum()


## 方向 / 标定状态的即时提示（不拦写入，只告诉你校验器会说什么）
func _refresh_anchor_warn() -> void:
	if _anchor_warn == null:
		return
	var bad: Array = []
	var unset := 0
	for i in METRIC_KEYS.size():
		var cur: Array = _anchor_of(String(METRIC_KEYS[i]))
		var b: float = float(cur[0])
		var g: float = float(cur[1])
		if is_zero_approx(b) and is_zero_approx(g):
			unset += 1
			continue
		var inverted: bool = (g < b) if bool(METRIC_LARGER_BETTER[i]) else (g > b)
		if inverted:
			bad.append(String(METRIC_KEYS[i]))
	var parts: Array = []
	if bad.size() > 0:
		parts.append("⚠ 方向相反：%s（R 要 g≥b；D/P/Q 要 g≤b）" % "、".join(PackedStringArray(bad)))
	if unset == METRIC_KEYS.size():
		parts.append("⚠ 四项都未标定 → 导出告警 anchors_unset")
	elif unset > 0:
		parts.append("%d 项未标定（填 0/0 的那几项）" % unset)
	var has_problem := parts.size() > 0
	_anchor_warn.text = "；".join(PackedStringArray(parts)) if has_problem else "✓ 方向与标定状态正常"
	_anchor_warn.modulate = Color(1.0, 0.78, 0.35) if has_problem else Color(0.65, 1.0, 0.7)


func _refresh_weights_sum() -> void:
	if _weights_sum == null:
		return
	var s := 0.0
	for m in METRIC_KEYS:
		s += _weight_of(String(m))
	var ok: bool = absf(s - 1.0) < 0.0005
	_weights_sum.text = ("合计 %.2f ✓" % s) if ok else ("合计 %.2f ⚠ 应为 1.00" % s)
	_weights_sum.modulate = Color(0.65, 1.0, 0.7) if ok else Color(1.0, 0.78, 0.35)


## 「权重恢复默认」：40/20/20/20（撤销由 `_btn` 的 begin/end_edit 包着，别再包一层）
func _reset_weights() -> void:
	var doc: Doc = EditorApp.doc
	doc.game_rules["weights"] = DEFAULT_WEIGHTS.duplicate(true)
	doc.dirty = true
	_refresh_later()               # 面板里的四个 SpinBox 得跟着回到默认值
	print("指标权重已恢复默认：R 0.40 / D 0.20 / P 0.20 / Q 0.20")


## 「锚点全部清空」：四项写回 [0,0] = 未标定（导出会告警 anchors_unset，这是预期行为）
func _clear_anchors() -> void:
	var doc: Doc = EditorApp.doc
	doc.game_rules["anchors"] = {"R": [0.0, 0.0], "D": [0.0, 0.0], "P": [0.0, 0.0], "Q": [0.0, 0.0]}
	doc.dirty = true
	_refresh_later()
	print("评分锚点已全部清空（四项 = [0,0]，即未标定）")


# --- game_rules.events / tutorial：原始 JSON 编辑（E92 G5b）------------------
## 为什么不是表单：`docs/spmap-schema.md` §3.2 明确写"events / tutorial 原样承载任意
## JSON，不规定字段"，而且实现里就是原样读写。**造字段就是替未来拍板**——所以这里只做
## "原样编辑 + 语法/类型校验"，一个字段名都不臆造。
##
## ★ 顶层必须是**数组**（schema 里这两个键的占位值就是 `[]`）。
## ★ 引用 id 的坑（写进提示里）：事件若引用车道/节点/连接，要填**导出后文件里的 id**
##   （`to_dict()` 会 renumber 成 0..N-1）。图改过之后 id 可能变，必须复核。
const RAW_JSON_ROWS := [
	{"key": "events", "label": "场景事件",
		"hint": "第四章的「小意外」（抛锚车 / 封道 / 站台 / 慢行区…）。schema 不规定字段，这里按原样保存。"},
	{"key": "tutorial", "label": "教学步骤",
		"hint": "关卡开头的逐步提示（游戏设计v2 §8.6）。schema 不规定字段，这里按原样保存。"},
]


## 面板上那一行：左边摘要（条数 + 出现过的键），右边「编辑…」
func _raw_json_cell(key: String) -> HBoxContainer:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 4)
	var lbl := _info(_raw_json_summary(key))
	lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	lbl.custom_minimum_size.x = 100
	row.add_child(lbl)
	var b := Button.new()                      # ★ 不走 _btn：这里没有"改数据"，不该记撤销快照
	b.text = "编辑…"
	b.pressed.connect(func(): _open_raw_json(key))
	row.add_child(b)
	return row


## 摘要：条数 + 实际出现过的键（只报告文件里有什么，不猜字段含义）。
## 键可能很多 → 只列前 6 个，其余用「…」带过。
func _raw_json_summary(key: String) -> String:
	var v = EditorApp.doc.game_rules.get(key, [])
	if not (v is Array):
		return "⚠ 不是数组（打开编辑会按空数组处理）"
	var arr: Array = v
	if arr.is_empty():
		return "空（[]）"
	var seen := {}
	for item in arr:
		if item is Dictionary:
			for k in item:
				seen[String(k)] = true
	var ks: Array = seen.keys()
	ks.sort()
	var extra := ""
	if ks.size() > 0:
		var head: Array = ks.slice(0, 6)
		extra = "｜键：%s%s" % ["、".join(PackedStringArray(head)), "…" if ks.size() > 6 else ""]
	return "%d 条%s" % [arr.size(), extra]


## 读成可编辑文本（缩进 2 空格；脏类型按 `[]` 处理，不让 TextEdit 里出现非 JSON）
func _raw_json_text(key: String) -> String:
	var v = EditorApp.doc.game_rules.get(key, [])
	if not (v is Array):
		v = []
	return JSON.stringify(v, "  ")


## 校验 + 写回。返回 "" = 成功；否则是给人看的错误说明（UI 直接显示，测试也直接调它）。
## ★ 先校验、后写入：语法/类型不对**一个字都不改**，避免把坏数据塞进 Doc。
func _apply_raw_json(key: String, text: String) -> String:
	var t := text.strip_edges()
	var parsed = null
	if t == "":
		parsed = []                            # 全删空 = 清空，不报错
	else:
		var j := JSON.new()
		if j.parse(t) != OK:
			var ln := j.get_error_line()      # 单行文本时 Godot 给 0，那就别写"第 0 行"
			if ln > 0:
				return "JSON 语法错误（第 %d 行）：%s" % [ln, j.get_error_message()]
			return "JSON 语法错误：%s" % j.get_error_message()
		parsed = j.data
	if not (parsed is Array):
		return "顶层必须是数组，当前是%s" % _json_type_cn(typeof(parsed))
	# ★ tag 带时间戳：对话框是"提交式"操作，**每次提交各占一步**撤销
	#   （不像 SpinBox 连点那样合并；否则连改两次只有一次能撤回来）
	EditorApp.begin_edit("json:%s#%d" % [key, Time.get_ticks_msec()])
	EditorApp.doc.game_rules[key] = parsed
	EditorApp.doc.dirty = true
	EditorApp.end_edit()
	print("%s：已写入 %d 条（game_rules.%s）" % [
		"场景事件" if key == "events" else "教学步骤", (parsed as Array).size(), key])
	return ""


func _json_type_cn(t: int) -> String:
	match t:
		TYPE_ARRAY:
			return "数组"
		TYPE_DICTIONARY:
			return "对象（要写成 [{...}]，别只写 {...}）"
		TYPE_STRING:
			return "字符串"
		TYPE_INT, TYPE_FLOAT:
			return "数字"
		TYPE_BOOL:
			return "布尔值"
		TYPE_NIL:
			return "null"
	return "未知类型"


## 打开编辑窗口。★ 窗口挂在 root 下：属性面板每次重建都会清空自己的子节点，
## 挂在面板里会被顺手销毁（`_autosave_failed` 的对话框也是这么处理的）。
func _open_raw_json(key: String) -> void:
	var meta: Dictionary = {}
	for row in RAW_JSON_ROWS:
		if String(row["key"]) == key:
			meta = row
			break
	var label := String(meta.get("label", key))
	# ★ 防连点：同一个键已经有窗口开着就先关掉（否则会撞 "parent window already has another
	#   exclusive child" 告警，而且两个窗口叠在一起更难操作）
	for old in get_tree().root.get_children():
		if old is ConfirmationDialog and String(old.get_meta("raw_json_key", "")) == key:
			old.queue_free()
	var dlg := ConfirmationDialog.new()
	EditorApp.prep_window(dlg)                 # ★ 窗口底透明，否则圆角四角露深灰（E91 C19）
	dlg.set_meta("raw_json_key", key)
	dlg.title = "%s · game_rules.%s" % [label, key]
	dlg.ok_button_text = "应用"
	dlg.cancel_button_text = "取消"
	dlg.dialog_hide_on_ok = false          # 校验不过就不许关，改成手动 hide
	# ★ 尺寸要小：按钮区在窗口最底部，窗口一旦比屏幕高，「应用」就点不到了。
	#   两个限制一起用：内容最小高度压到 ~330 px（说明 2 行 + 文本框 150），
	#   再用 popup_centered_clamped 按"屏幕可用区域的 85%"夹一次（小屏/高 DPI 也放得下）。
	dlg.min_size = Vector2i(600, 360)

	var box := VBoxContainer.new()
	dlg.add_child(box)
	var tip := Label.new()
	tip.text = "%s\n顶层必须是数组（空 = []）；语法错了不会写进去。\n引用车道/节点/连接时 id 填【导出后】文件里的 id（保存会重排成 0..N-1），改过几何要复核。" % String(meta.get("hint", ""))
	tip.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	# ★ 自动换行的 Label **必须**给宽度约束：否则 get_contents_minimum_size() 会按"每字一行"
	#   算最小高度（实测把窗口撑到 3381 px，底部按钮直接被顶出屏幕 —— 2026-10-24 踩过）。
	tip.custom_minimum_size = Vector2(556, 0)
	tip.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	tip.modulate = Color(0.85, 0.9, 1.0)
	box.add_child(tip)

	var te := TextEdit.new()
	te.text = _raw_json_text(key)
	te.custom_minimum_size = Vector2(0, 150)   # ★ 只保底 150，多出来的高度由窗口给
	te.size_flags_vertical = Control.SIZE_EXPAND_FILL
	te.wrap_mode = TextEdit.LINE_WRAPPING_NONE
	box.add_child(te)

	var err := Label.new()
	err.modulate = Color(1.0, 0.6, 0.4)
	err.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	err.custom_minimum_size = Vector2(556, 0)      # 同上：autowrap 的 Label 必须给宽度约束
	err.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	box.add_child(err)

	var close_once := func() -> void:
		if is_instance_valid(dlg):
			dlg.hide()
			dlg.queue_free()
	dlg.confirmed.connect(func() -> void:
		var msg := _apply_raw_json(key, te.text)
		if msg == "":
			close_once.call()
			_refresh_later()               # 摘要行要跟着变
		else:
			err.text = msg)                # 不关窗口：让用户就地改
	dlg.canceled.connect(func() -> void: close_once.call())
	dlg.close_requested.connect(func() -> void: close_once.call())
	dlg.add_button("格式化", true, "format")
	dlg.custom_action.connect(func(action: StringName) -> void:
		if String(action) != "format":
			return
		var j := JSON.new()
		if j.parse(te.text.strip_edges()) != OK:
			err.text = "格式化失败：先修掉语法错误（第 %d 行）" % j.get_error_line()
		elif not (j.data is Array):
			err.text = "格式化失败：顶层必须是数组，当前是%s" % _json_type_cn(typeof(j.data))
		else:
			te.text = JSON.stringify(j.data, "  ")
			err.text = "")
	get_tree().root.add_child(dlg)
	dlg.popup_centered_clamped(Vector2i(600, 400), 0.85)   # 放不下就按屏幕 85% 缩，别把按钮顶出屏幕


## 连接生成的会话级开关：只改 EditorApp 的两个变量，不碰 Doc → 导出里不会多字段
## （_check 内部走 begin/end_edit，但 Doc 没变 → 不占撤销步，见 E08「没改不占一步」）
func _on_include_uturn(on: bool) -> void:
	EditorApp.include_uturn = on
	print("含掉头：%s（不会自动重推，请再按一次 G）" % ("是" if on else "否"))

func _on_include_crosswalk(on: bool) -> void:
	EditorApp.include_crosswalk = on
	print("含过街：%s（不会自动重推，请再按一次 G）" % ("是" if on else "否"))


## 面板最窄只有 `DOCK_W_MIN`(380) − 内边距 − 滚动条 ≈ 350 px，长文本必须允许自动折行
func _wrapped(text: String) -> Label:
	var l := _info(text)
	l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	l.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	l.custom_minimum_size.x = 120
	return l

## 关卡元数据·单行文本（name / author）
## 撤销口径：获得焦点 = 手势开始，失焦 = 手势结束 → 一次连续输入只占一步
func _meta_line(key: String, cur: String) -> LineEdit:
	var e := LineEdit.new()
	e.text = cur                                    # ★ 先赋值再连信号，否则会多写一次
	e.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	e.custom_minimum_size.x = 120
	e.focus_entered.connect(EditorApp.begin_edit)
	e.text_changed.connect(func(t: String): _set_meta_text(key, t))
	e.focus_exited.connect(EditorApp.end_edit)
	return e

func _set_meta_text(key: String, t: String) -> void:
	EditorApp.doc.meta[key] = t
	EditorApp.doc.dirty = true

## 关卡元数据·整数（chapter / index）：复用现成的 _spin（它内部已是 begin→改→end）
func _meta_int(key: String, cur: int) -> SpinBox:
	return _spin(float(cur), 1, 99, 1, func(v: float): _set_meta_int(key, v))

func _set_meta_int(key: String, v: float) -> void:
	EditorApp.doc.meta[key] = int(v)
	EditorApp.doc.dirty = true
	var m: Dictionary = EditorApp.doc.meta
	print("关卡编号：%d-%d（%s）" % [int(m.get("chapter", 1)), int(m.get("index", 1)), String(m.get("name", ""))])

## 车型构成下拉（只许选预设，不许填比例 —— 比例在 SimCore 里）
func _mix_option(cur: String) -> OptionButton:
	var o := OptionButton.new()
	for i in VEHICLE_MIX_IDS.size():
		o.add_item(VEHICLE_MIX_LABELS[i])
		o.set_item_tooltip(i, VEHICLE_MIX_TIPS[i])
	o.select(maxi(VEHICLE_MIX_IDS.find(cur), 0))   # ★ 必须先 select，再 connect
	o.item_selected.connect(_on_mix_selected)
	return o

func _on_mix_selected(i: int) -> void:
	EditorApp.begin_edit()                          # 撤销：记下改前快照
	EditorApp.doc.game_rules["vehicle_mix"] = VEHICLE_MIX_IDS[i]
	EditorApp.doc.dirty = true
	EditorApp.end_edit()                            # 撤销：真变了才占一步
	print("车型构成：%s（%s）" % [VEHICLE_MIX_LABELS[i], VEHICLE_MIX_IDS[i]])


# --- 专业：检测设施（检测器 / 公交站 / 停车场）----------------------------
## 2026-11-05（S3）：与车型构成一样挂在**车道**上 —— 设施本来就是"车道上的一个点/一段"。
##
## ★ 位置口径：文件存 `pos`（从车道起点算，SUMO 的口径），
##   界面给「离路口多远」。换算只在 `SumoFacilitiesModel` 里做一处，
##   面板不许自己算（两处各算一遍迟早不一致）。

func _build_facilities(lane_id: int) -> void:
	if not EditorApp.is_pro():
		return
	var doc: Doc = EditorApp.doc
	var lane: Dictionary = doc.find_lane(lane_id)
	if not SumoFacilitiesModel.supports_detector(String(lane.get("type", ""))):
		return

	var all_rows: Array = SumoFacilitiesModel.list_of(EditorApp.profile, lane_id)
	var detectors: Array = []
	var transports: Array = []
	for row in all_rows:
		var kind := String(row.get("kind", ""))
		if kind == SumoFacilitiesModel.KIND_E1 or kind == SumoFacilitiesModel.KIND_E2:
			detectors.append(row)
		else:
			transports.append(row)

	# --- 检测设施：只放检测器（量数据的）---
	_section("检测设施")
	if detectors.is_empty():
		_add_row("", _wrapped("还没有检测器。断面流量统计通过车辆数，排队检测统计排队长度与停车次数。"))
	else:
		for row in detectors:
			_add_facility_row(row)
	_add_row("", _facility_add_bar(lane_id,
		[SumoFacilitiesModel.KIND_E1, SumoFacilitiesModel.KIND_E2]))

	# --- 交通设施：公交站 / 停车场（不是检测器，是路侧设施）---
	_section("交通设施")
	if transports.is_empty():
		_add_row("", _wrapped("还没有路侧设施。公交站让公交按线路停靠，停车场提供路边车位。"))
	else:
		for row in transports:
			_add_facility_row(row)
	_add_row("", _facility_add_bar(lane_id,
		[SumoFacilitiesModel.KIND_BUS_STOP, SumoFacilitiesModel.KIND_PARKING]))

	# --- 公交线路：从这条车道发出的（跨车道，所以单独一节）---
	_build_bus_lines(lane_id)


func _add_facility_row(row: Dictionary) -> void:
	var fid := String(row.get("id", ""))
	var kind := String(row.get("kind", ""))

	# 标题行：类型 + 删除
	var head := HBoxContainer.new()
	head.add_theme_constant_override("separation", UITokens.S1)
	var title := Label.new()
	title.text = "%s  #%s" % [_facility_kind_label(kind), fid.rsplit("_", true, 1)[-1]]
	title.add_theme_color_override("font_color", UITokens.INK_1)
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	title.tooltip_text = "SUMO id：%s" % fid
	head.add_child(title)
	var del := Clay.icon_btn("✕", 28)
	del.tooltip_text = "删掉这个设施"
	del.pressed.connect(func() -> void: _remove_facility(fid))
	head.add_child(del)
	_add_row("", head)

	# 位置：检测器是点，站台/停车场是段
	_add_row("离路口", _facility_road_spin(row))

	match kind:
		SumoFacilitiesModel.KIND_E2:
			_add_row("检测区", _facility_num_spin(row, "length", 1.0, 999.0, 0.5, " m"))
			_add_row("输出频率", _facility_num_spin(row, "freq", 1.0, 3600.0, 1.0, " s"))
		SumoFacilitiesModel.KIND_E1:
			_add_row("输出频率", _facility_num_spin(row, "freq", 1.0, 3600.0, 1.0, " s"))
		SumoFacilitiesModel.KIND_BUS_STOP:
			_add_row("站名", _facility_name_edit(row))
			_add_row("站台长度", _facility_span_len_spin(row))
			_add_row("停靠时长", _facility_num_spin(row, "duration", 0.0, 600.0, 1.0, " s"))
			_add_row("线路名", _facility_lines_edit(row))
		SumoFacilitiesModel.KIND_PARKING:
			_add_row("车位长度", _facility_span_len_spin(row))
			_add_row("车位数量", _facility_num_spin(row, "capacity", 0.0, 999.0, 1.0, " 个"))


func _facility_kind_label(kind: String) -> String:
	return String(SumoFacilitiesModel.KIND_LABELS.get(kind, kind))


## 「离路口多远」。站台/停车场是**中心点**，改它会整段平移（长度不变）。
func _facility_road_spin(row: Dictionary) -> SpinBox:
	var fid := String(row.get("id", ""))
	var kind := String(row.get("kind", ""))
	var lane_id := int(row.get("lane", -1))
	var is_span: bool = kind == SumoFacilitiesModel.KIND_BUS_STOP \
		or kind == SumoFacilitiesModel.KIND_PARKING

	var center_pos := float(row.get("pos", 0.0))
	if is_span:
		center_pos = (float(row.get("start", 0.0)) + float(row.get("end", 0.0))) * 0.5
	var current: float = SumoFacilitiesModel.road_from_pos(EditorApp.doc, lane_id, center_pos)
	var total: float = SumoFacilitiesModel.lane_length(EditorApp.doc, lane_id)

	var s := _spin(current, 0.0, maxf(total, 1.0), 0.5, func(v: float) -> void:
		EditorApp.begin_edit("facility:%s:road" % fid)
		var changed: bool = false
		if is_span:
			changed = SumoFacilitiesModel.set_span_road_distance(
				EditorApp.doc, EditorApp.profile, fid, v)
		else:
			changed = SumoFacilitiesModel.set_road_distance(
				EditorApp.doc, EditorApp.profile, fid, v)
		if changed:
			_touch_profile()
		EditorApp.end_edit(), "facility:%s:road" % fid)
	s.custom_minimum_size.x = 82
	s.tooltip_text = "离路口多远（车道全长 %.0f m）。SUMO: %s" % [
		total, "startPos/endPos 的中点" if is_span else "pos"]
	return s


## 通用数值字段（e2 的检测区长度、输出频率、停靠时长、车位数）
func _facility_num_spin(
	row: Dictionary, key: String, min_v: float, max_v: float, step: float, suffix: String) -> SpinBox:
	var fid := String(row.get("id", ""))
	var s := _spin(float(row.get(key, 0.0)), min_v, max_v, step, func(v: float) -> void:
		EditorApp.begin_edit("facility:%s:%s" % [fid, key])
		if SumoFacilitiesModel.set_number(EditorApp.doc, EditorApp.profile, fid, key, v):
			_touch_profile()
		EditorApp.end_edit(), "facility:%s:%s" % [fid, key])
	s.custom_minimum_size.x = 82
	s.suffix = suffix
	return s


## 站台 / 停车区的长度（保持起点，往路口方向伸）
func _facility_span_len_spin(row: Dictionary) -> SpinBox:
	var fid := String(row.get("id", ""))
	var span: float = maxf(float(row.get("end", 0.0)) - float(row.get("start", 0.0)), 1.0)
	var total: float = SumoFacilitiesModel.lane_length(EditorApp.doc, int(row.get("lane", -1)))
	var s := _spin(span, 1.0, maxf(total, 1.0), 0.5, func(v: float) -> void:
		EditorApp.begin_edit("facility:%s:span" % fid)
		if SumoFacilitiesModel.set_span_length(EditorApp.doc, EditorApp.profile, fid, v):
			_touch_profile()
		EditorApp.end_edit(), "facility:%s:span" % fid)
	s.custom_minimum_size.x = 82
	s.suffix = " m"
	return s


## 设施显示名（目前只有公交站用）。起个名字，勾停靠站时就能认出来是哪一个。
func _facility_name_edit(row: Dictionary) -> LineEdit:
	var fid := String(row.get("id", ""))
	var edit := LineEdit.new()
	edit.text = String(row.get("name", ""))
	edit.placeholder_text = "如「市政府站」"
	edit.custom_minimum_size.x = 120
	edit.tooltip_text = "站名（SUMO: name）。填了之后，勾选停靠站时会显示这个名字而不是「站1」"
	edit.text_changed.connect(func(t: String) -> void:
		EditorApp.begin_edit("facility:%s:name" % fid)
		if SumoFacilitiesModel.set_field(EditorApp.profile, fid, "name", t.strip_edges()):
			_touch_profile()
		EditorApp.end_edit())
	return edit


func _facility_lines_edit(row: Dictionary) -> LineEdit:
	var fid := String(row.get("id", ""))
	var edit := LineEdit.new()
	edit.text = String(row.get("lines", "bus"))
	edit.custom_minimum_size.x = 96
	edit.tooltip_text = "停靠线路名（SUMO: lines）。导出时会给同名线路的公交 flow 插 stop"
	edit.text_changed.connect(func(t: String) -> void:
		var cleaned := t.strip_edges()
		if cleaned == "":
			cleaned = "bus"
		EditorApp.begin_edit("facility:%s:lines" % fid)
		if SumoFacilitiesModel.set_field(EditorApp.profile, fid, "lines", cleaned):
			_touch_profile()
		EditorApp.end_edit())
	return edit


func _facility_add_bar(lane_id: int, kinds: Array) -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", UITokens.S1)
	for kind in kinds:
		var b := Clay.btn("＋ " + _facility_kind_label(String(kind)))
		b.tooltip_text = _facility_hint(String(kind))
		b.pressed.connect(func() -> void: _add_facility(lane_id, String(kind)))
		row.add_child(b)
	return row


func _facility_hint(kind: String) -> String:
	match kind:
		SumoFacilitiesModel.KIND_E1:
			return "断面流量：线圈，统计每个时段通过了几辆车（SUMO: e1Detector）"
		SumoFacilitiesModel.KIND_E2:
			return "排队检测：一段检测区，统计排队长度与停车次数（SUMO: e2Detector）"
		SumoFacilitiesModel.KIND_BUS_STOP:
			return "公交站：公交会按停靠时长停一下（需要该进口跑的是公交车型）"
		_:
			return "停车场：路边停车位（SUMO: parkingArea）"


func _add_facility(lane_id: int, kind: String) -> void:
	EditorApp.begin_edit("facility:add:%d:%s" % [lane_id, kind])
	var fid: String = SumoFacilitiesModel.add(EditorApp.doc, EditorApp.profile, lane_id, kind)
	if fid != "":
		_touch_profile()
	EditorApp.end_edit()
	if fid == "":
		EditorApp.toast("这条车道太短，放不下设施", "warn")
		return
	print("新增检测设施：%s（车道 #%d）" % [fid, lane_id])
	_refresh_later()


func _remove_facility(facility_id: String) -> void:
	var row := SumoFacilitiesModel.find(EditorApp.profile, facility_id)
	var was_stop: bool = String(row.get("kind", "")) == SumoFacilitiesModel.KIND_BUS_STOP
	EditorApp.begin_edit("facility:del:%s" % facility_id)
	if SumoFacilitiesModel.remove(EditorApp.profile, facility_id):
		# ★ 公交站被删 → 所有线路里对它的引用一起清掉，别留悬空引用
		if was_stop:
			var touched: int = SumoBusModel.drop_stop(EditorApp.profile, facility_id)
			if touched > 0:
				print("删站 %s：已从 %d 条公交线路里移除该停靠" % [facility_id, touched])
		_touch_profile()
	EditorApp.end_edit()
	print("删除检测设施：%s" % facility_id)
	_refresh_later()


# --- 专业：公交线路（S3b）------------------------------------------------
## 一条线路 = 起点进口 + 发车间隔 + 停靠哪些站。
## ★ **路线不让用户画**：导出时按起点进口的转率枚举，取权重最大的那条
##   （与"用户永不写路线"一致）。站台不在该路线上的会被过滤并告警。
##
## 为什么单开一节：线路是**跨车道**的（一趟车走好几个路口），挂在单条车道上会误导。

# --- 专业：车辆总览（车辆模式未选车道时）--------------------------------
## 2026-11-05（S3c）老板要求：车辆模式下**没选车道**时不要显示关卡设置，
## 改显示"关卡内的车辆宏观信息 + 功能入口"。
##
## 为什么需要：公交线路是**跨道路**的（一趟车走好几个路口），
## 挂在某一条路上本身就说不清"这条线属于谁"。总览里能一次看到全部。
##
## ★ 这不是"移动功能"：车道面板里的「进口车型构成」「检测设施」「交通设施」「公交线路」
##   一个都没删，仍然按车道显示。总览是**另一个入口**，看的是全局。

func _build_traffic_overview() -> void:
	var doc: Doc = EditorApp.doc
	var profile: SumoProfile = EditorApp.profile

	# --- 宏观数字 ---
	_section("车辆总览")
	var motor := 0
	var with_demand := 0
	var total_vph := 0.0
	for l in doc.lanes:
		if not Doc.MOTOR_LANE_TYPES.has(String(l.type)):
			continue
		motor += 1
		var rec: Dictionary = doc.find_profile("demand", int(l.id))
		if rec.is_empty():
			continue
		with_demand += 1
		var rates: Array = rec.get("flow_rates", [])
		if not rates.is_empty():
			total_vph += float(rates[0]) * 3600.0

	_add_row("机动车道", _info("%d 条 · 有需求 %d 条" % [motor, with_demand]))
	_add_row("总需求", _info("%.0f 辆/时（各进口首段之和）" % total_vph))
	_add_row("公交线路", _info("%d 条" % SumoBusModel.all_of(profile).size()))
	_add_row("公交站", _info("%d 个" % SumoFacilitiesModel.count_of(
		profile, SumoFacilitiesModel.KIND_BUS_STOP)))
	if with_demand < motor:
		var warn := _wrapped("⚠ 有 %d 条机动车道没有需求曲线 —— 这些进口不会出车。" % (motor - with_demand))
		warn.add_theme_color_override("font_color", UITokens.CONFLICT)
		_add_row("提示", warn)
	_add_row("说明", _wrapped("点画布上的车道可以看到/编辑该进口的车型构成、检测设施与转率。"))

	# --- 车型库入口（原来在关卡面板里，S2b 挪进弹窗；这里给一个全局入口）---
	_section("SUMO 车型库")
	var names: Array = []
	for vt in profile.vehicle_types:
		names.append(String(vt.get("id", "")))
	_add_row("已定义", _info("%d 种" % names.size()))
	if names.is_empty():
		_add_row("当前", _wrapped("跟随关卡车型构成（未自定义）。要单独调某类车的车长/限速/驾驶风格，点下面的按钮。"))
	else:
		_add_row("当前", _wrapped("、".join(PackedStringArray(names))))
	var mng := Clay.btn("管理车型库…")
	mng.tooltip_text = "新建车型 / 改车长、限速、加减速、驾驶风格（对所有进口通用）"
	mng.pressed.connect(_open_type_dialog)
	_add_row("", mng)

	# --- 公交线路（全部，跨道路）---
	_build_bus_lines(-1)


func _build_bus_lines(lane_id: int) -> void:
	if not EditorApp.is_pro():
		return

	# ★ 常驻：没有公交站也显示，只是给一句"无可用公交站"。
	#   以前是"有站才出现"，于是新用户根本不知道有这功能。
	var stops: Array = SumoBusModel.stop_choices(EditorApp.profile)
	var all_mode: bool = lane_id < 0
	var lines: Array = SumoBusModel.all_of(EditorApp.profile) if all_mode \
		else SumoBusModel.lines_of(EditorApp.profile, lane_id)

	_section("公交线路")

	if stops.is_empty():
		_add_row("可用站台", _wrapped("⚠ 无可用公交站 —— 先在进口车道的「交通设施」里建公交站，才能勾选停靠。"))
	else:
		_add_row("可用站台", _info("%d 个" % stops.size()))

	if lines.is_empty():
		_add_row("", _wrapped("还没有公交线路。新建一条，填发车间隔、勾停靠站 —— "
			+ "路线按起点进口的主路线自动生成。"))
	else:
		for row in lines:
			_build_bus_line_row(row, all_mode)

	_add_row("", _bus_line_add_bar(lane_id))


func _build_bus_line_row(row: Dictionary, show_lane: bool = false) -> void:
	var lid := String(row.get("id", ""))
	var line := String(row.get("line", "1"))

	# 标题行：线路名 + 删除
	var head := HBoxContainer.new()
	head.add_theme_constant_override("separation", UITokens.S1)
	var title := Label.new()
	title.text = "线路 %s" % line
	title.add_theme_color_override("font_color", UITokens.INK_1)
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	title.tooltip_text = "SUMO id：%s（车辆 id 会是 %s.0、%s.1 …）" % [lid, lid, lid]
	head.add_child(title)
	var del := Clay.icon_btn("✕", 28)
	del.tooltip_text = "删掉这条公交线路（不影响公交站）"
	del.pressed.connect(func() -> void: _remove_bus_line(lid))
	head.add_child(del)
	_add_row("", head)

	# ★ 总览里必须能看/改起点 —— 否则一堆线路不知道各自从哪发车
	if show_lane:
		_add_row("起点", _bus_line_lane_pick(row))
	_add_row("线路名", _bus_line_name_edit(row))
	_add_row("发车间隔", _bus_line_num_spin(row, "headway", 10.0, 3600.0, " s"))
	_add_row("运营时段", _bus_line_span(row))
	_add_row("停靠站", _bus_line_stops(row))


## 起点车道（只列机动车道）。改了等于给这条线换发车站。
func _bus_line_lane_pick(row: Dictionary) -> OptionButton:
	var lid := String(row.get("id", ""))
	var current := int(row.get("lane", -1))
	var options: Array = []
	for l in EditorApp.doc.lanes:
		if Doc.MOTOR_LANE_TYPES.has(String(l.get("type", ""))):
			options.append(int(l.get("id", -1)))

	var pick := OptionButton.new()
	pick.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	# ★ 没有机动车道（新建的空关卡、或还没画路）→ 不能 select，否则 Godot 报
	#   `Index p_which = 0 is out of bounds (popup->get_item_count() = 0)`
	if options.is_empty():
		pick.add_item("（还没有机动车道）")
		pick.disabled = true
		pick.tooltip_text = "关卡里还没有机动车道，先画路或建车道"
		return pick

	var idx := 0
	for i in options.size():
		var lane_id: int = options[i]
		var lane: Dictionary = EditorApp.doc.find_lane(lane_id)
		pick.add_item("车道 #%d（%.0f m）" % [lane_id, float(lane.get("length", 0.0))])
		if lane_id == current:
			idx = i
	pick.select(idx)
	pick.tooltip_text = "这条线路从哪条进口发车（路线按该进口的主路线生成）"
	pick.item_selected.connect(func(k: int) -> void:
		if k < 0 or k >= options.size():
			return
		EditorApp.begin_edit("bus:%s:lane" % lid)
		if SumoBusModel.set_field(EditorApp.profile, lid, "lane", int(options[k])):
			_touch_profile()
		EditorApp.end_edit()
		_refresh_later())
	return pick


func _bus_line_name_edit(row: Dictionary) -> LineEdit:
	var lid := String(row.get("id", ""))
	var edit := LineEdit.new()
	edit.text = String(row.get("line", "1"))
	edit.custom_minimum_size.x = 96
	edit.tooltip_text = "线路名（SUMO: line）。公交站的「线路名」要与它一致，车才会在那站停"
	edit.text_changed.connect(func(t: String) -> void:
		var cleaned := t.strip_edges()
		if cleaned == "":
			return
		EditorApp.begin_edit("bus:%s:line" % lid)
		if SumoBusModel.set_field(EditorApp.profile, lid, "line", cleaned):
			_touch_profile()
		EditorApp.end_edit())
	return edit


func _bus_line_num_spin(
	row: Dictionary, key: String, min_v: float, max_v: float, suffix: String) -> SpinBox:
	var lid := String(row.get("id", ""))
	var step := 1.0
	if key == "headway":
		step = 10.0        # 发车间隔按 10 秒调；min 10 能被 10 整除
	var s := _spin(float(row.get(key, 0.0)), min_v, max_v, step, func(v: float) -> void:
		EditorApp.begin_edit("bus:%s:%s" % [lid, key])
		if SumoBusModel.set_number(EditorApp.profile, lid, key, v):
			_touch_profile()
		EditorApp.end_edit(), "bus:%s:%s" % [lid, key])
	s.custom_minimum_size.x = 82
	s.suffix = suffix
	return s


## 运营时段：起 ~ 止（两个框一行）
func _bus_line_span(row: Dictionary) -> Control:
	var box := HBoxContainer.new()
	box.add_theme_constant_override("separation", UITokens.S1)
	var lid := String(row.get("id", ""))
	box.add_child(_bus_line_num_spin(row, "begin", 0.0, 86400.0, " s"))
	var tilde := Label.new()
	tilde.text = "~"
	tilde.add_theme_color_override("font_color", UITokens.INK_3)
	box.add_child(tilde)
	box.add_child(_bus_line_num_spin(row, "end", 0.0, 86400.0, " s"))
	return box


## 停靠站：一个站一个勾选框（自动换行）。勾了才停。
func _bus_line_stops(row: Dictionary) -> Control:
	var lid := String(row.get("id", ""))
	var flow := HFlowContainer.new()
	flow.add_theme_constant_override("h_separation", UITokens.S2)
	flow.add_theme_constant_override("v_separation", UITokens.S1)

	var chosen: Array = SumoBusModel.stops_of(EditorApp.profile, lid)
	for stop in SumoBusModel.stop_choices(EditorApp.profile):
		var sid := String(stop.get("id", ""))
		var cb := CheckBox.new()
		cb.text = _stop_label(sid)
		cb.button_pressed = chosen.has(sid)
		cb.tooltip_text = "停靠 %s（站台位置：离路口 %.0f m）" % [
			sid, SumoFacilitiesModel.road_from_pos(EditorApp.doc,
				int(stop.get("lane", -1)),
				(float(stop.get("start", 0.0)) + float(stop.get("end", 0.0))) * 0.5)]
		cb.toggled.connect(func(pressed: bool) -> void:
			EditorApp.begin_edit("bus:%s:stop:%s:%s" % [lid, sid, str(pressed)])
			if SumoBusModel.toggle_stop(EditorApp.profile, lid, sid):
				_touch_profile()
			EditorApp.end_edit())
		flow.add_child(cb)

	if flow.get_child_count() == 0:
		return _wrapped("全图还没有公交站 —— 先在「交通设施」里建一个。")
	return flow


## 站台的**可读名**。光叫「站1」分不清是哪个 —— 把所在车道与离路口距离带上。
## 优先用用户起的站名（交通设施里的「站名」输入框）。
func _stop_label(stop_id: String) -> String:
	var row := SumoFacilitiesModel.find(EditorApp.profile, stop_id)
	if row.is_empty():
		return stop_id
	var lane_id := int(row.get("lane", -1))
	var center := (float(row.get("start", 0.0)) + float(row.get("end", 0.0))) * 0.5
	var dist := SumoFacilitiesModel.road_from_pos(EditorApp.doc, lane_id, center)
	var named := String(row.get("name", "")).strip_edges()
	if named != "":
		return "%s（车道#%d · %.0fm）" % [named, lane_id, dist]
	var tail := stop_id.rsplit("_", true, 1)[-1]
	var short := "站 %s" % tail if tail.is_valid_int() else stop_id
	return "%s（车道#%d · %.0fm）" % [short, lane_id, dist]


func _bus_line_add_bar(lane_id: int) -> Control:
	var b := Clay.btn("＋ 新建公交线路")
	b.tooltip_text = ("新建一条公交线路（默认 300 秒一班）；起点自动取第一个有需求的进口，建完可改"
		if lane_id < 0 else "从这条进口发一条公交线路（默认 300 秒一班）")
	b.pressed.connect(func() -> void: _add_bus_line(lane_id))
	return b


func _add_bus_line(lane_id: int) -> void:
	# ★ 总览里（lane_id < 0）新建时没有"当前车道" → 取第一个有需求的机动车道当起点，
	#   建完可以在「起点」行里改。
	var target_lane: int = lane_id
	if target_lane < 0:
		target_lane = _first_demand_motor_lane()
	if target_lane < 0:
		EditorApp.toast("还没有任何机动车需求 —— 先在进口车道上画需求曲线", "warn")
		return

	EditorApp.begin_edit("bus:add:%d:%d" % [target_lane, SumoBusModel.all_of(EditorApp.profile).size()])
	var lid: String = SumoBusModel.add(EditorApp.profile, target_lane)
	if lid != "":
		_touch_profile()
	EditorApp.end_edit()
	if lid == "":
		EditorApp.toast("没能新建公交线路", "warn")
		return
	print("新增公交线路：%s（起点车道 #%d）" % [lid, target_lane])
	_refresh_later()


## 第一个有需求曲线的机动车道（找不到返回 -1）
func _first_demand_motor_lane() -> int:
	var doc: Doc = EditorApp.doc
	for d in doc.demand:
		var lane_id := int(d.get("lane", -1))
		var lane: Dictionary = doc.find_lane(lane_id)
		if not lane.is_empty() and Doc.MOTOR_LANE_TYPES.has(String(lane.get("type", ""))):
			return lane_id
	for l in doc.lanes:
		if Doc.MOTOR_LANE_TYPES.has(String(l.get("type", ""))):
			return int(l.get("id", -1))
	return -1


func _remove_bus_line(line_id: String) -> void:
	EditorApp.begin_edit("bus:del:%s" % line_id)
	if SumoBusModel.remove(EditorApp.profile, line_id):
		_touch_profile()
	EditorApp.end_edit()
	print("删除公交线路：%s" % line_id)
	_refresh_later()


# --- 专业：进口车流（车型构成 + 转率）------------------------------------
## 2026-11-05 老板要求：车型 / 车流相关**全部收拢到「车道设置」下**，
## 关卡面板与路口面板里的那两块已删除。
##
## 分工：
##   - 车型**定义**（车长 / 限速 / 加减速）→ 弹窗（低频、全局，改一次影响所有进口）
##   - 车型**比例** + 转率 → 这里（高频、按进口）
##
## 数据落点：比例写 `.sumo.json` 的 `lane_mix`，转率写 `.spmap` 的 `turns[].ratio`。

func _build_sumo_fleet(lane_id: int) -> void:
	if not EditorApp.is_pro():
		return
	var doc: Doc = EditorApp.doc
	var profile: SumoProfile = EditorApp.profile
	var mix_name := String(doc.game_rules.get("vehicle_mix", "CarOnly"))
	var own := SumoFleetModel.has_own_mix(profile, lane_id)

	_section("进口车型构成")
	# ★ 车流是**从需求曲线派生**的，不是从车型派生 —— 这句必须显性化，
	#   否则"我配了 4 个进口的车型，怎么只有 1 个进口有车"必然再发生一次。
	_add_row("本进口流量", _demand_summary(lane_id))
	_add_row("来源", _info("本进口自定义" if own else "跟随默认（%s）" % mix_name))

	var rows: Array = SumoFleetModel.effective_mix(profile, lane_id, mix_name)
	for i in rows.size():
		_add_row("车型 %d" % (i + 1), _fleet_mix_row(lane_id, i, rows, mix_name))
	_add_row("份额合计", _wrapped("%.0f%%（导出时归一化，不必凑满 100）"
		% SumoFleetModel.mix_total(rows)))
	_add_row("", _fleet_bar(lane_id, own, mix_name))

	# --- 转率：只有该进口有 ≥2 条出口转向时才出现（一条恒为 1，配了没意义）---
	var turns: Array = SumoFleetModel.turn_entries(doc, lane_id)
	if turns.size() >= 2:
		_section("进口转率")
		_add_row("说明", _wrapped("这个进口的车按比例分到各条转向。填的就是**权重**，"
			+ "导出时按本进口归一化 —— 想 1:1:1 就三条都填 1，想 60/30/10 就填 0.6/0.3/0.1。"))
		for t in turns:
			_add_row(_turn_type_label(String(t["type"])), _ratio_spin(t))
		_add_row("实际占比", _wrapped(_ratio_summary(lane_id)))

	_add_row("提示", _wrapped("导出时：车流按上面的比例抽车型，沿转率分配出的路线行驶。"
		+ "车型的车长 / 限速在「管理车型库…」里调。"))


## 一行：车型下拉 + 份额 + 删除
func _fleet_mix_row(lane_id: int, index: int, rows: Array, mix_name: String) -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", UITokens.S1)
	if index < 0 or index >= rows.size():
		return row

	var options := _fleet_type_options(rows)
	var current := String(rows[index].get("id", ""))

	var pick := OptionButton.new()
	for k in options.size():
		var tid := String(options[k])
		pick.add_item(_fleet_type_label(tid))
		pick.set_item_tooltip(k, "（引用车型库里的 %s）" % tid)
	pick.select(maxi(options.find(current), 0))
	pick.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	pick.item_selected.connect(func(k: int) -> void:
		_set_fleet_type(lane_id, index, String(options[k]), mix_name))
	row.add_child(pick)

	var share := _spin(float(rows[index].get("share", 0.0)), 0.0, 100.0, 1.0,
		func(v: float): _set_fleet_share(lane_id, index, v, mix_name), "fleet:%d:%d" % [lane_id, index])
	share.custom_minimum_size.x = 72
	share.tooltip_text = "份额（%）；同一条车流里按这个比例抽车型"
	row.add_child(share)

	var del := Clay.icon_btn("✕", 28)
	del.tooltip_text = "从这个进口的构成里去掉这个车型"
	del.pressed.connect(func() -> void: _remove_fleet_row(lane_id, index, mix_name))
	row.add_child(del)
	return row


## 可选车型 = 车型库里的全部；库为空时退回"当前这几条"，保证下拉不是空的
func _fleet_type_options(rows: Array) -> Array:
	var out: Array = []
	for t in SumoFleetModel.types_of(EditorApp.profile):
		out.append(String(t.get("id", "")))
	if out.is_empty():
		for r in rows:
			var tid := String(r.get("id", ""))
			if not out.has(tid):
				out.append(tid)
	return out


func _fleet_type_label(type_id: String) -> String:
	var t: Dictionary = SumoFleetModel.find_type(EditorApp.profile, type_id)
	if t.is_empty():
		return type_id
	var preset := String(t.get("preset", "car"))
	var i: int = SumoFleetModel.PRESETS.find(preset)
	var label: String = SumoFleetModel.PRESET_LABELS[i] if i >= 0 else preset
	return "%s（%s）" % [label, type_id]


func _fleet_bar(lane_id: int, own: bool, mix_name: String) -> Control:
	var bar := VBoxContainer.new()
	bar.add_theme_constant_override("separation", UITokens.S1)

	var row1 := HBoxContainer.new()
	row1.add_theme_constant_override("separation", UITokens.S1)
	bar.add_child(row1)

	var add := Clay.btn("＋ 添加车型")
	add.tooltip_text = "往这个进口的构成里加一种车（从车型库里挑）"
	add.pressed.connect(func() -> void: _add_fleet_row(lane_id, mix_name))
	row1.add_child(add)

	var lib := Clay.btn("管理车型库…")
	lib.tooltip_text = "新建车型 / 改车长、限速、加减速、驾驶风格（对所有进口通用）"
	lib.pressed.connect(_open_type_dialog)
	row1.add_child(lib)

	if own:
		var reset := Clay.btn_ghost("恢复跟随默认")
		reset.tooltip_text = "丢掉这个进口的自定义构成，回到关卡默认"
		reset.pressed.connect(func() -> void: _reset_fleet(lane_id))
		bar.add_child(reset)
	return bar


# --- 进口构成：写操作（统一 begin/end_edit，一步撤销）-------------------

func _set_fleet_share(lane_id: int, index: int, value: float, mix_name: String) -> void:
	EditorApp.begin_edit("fleet:%d:share:%d" % [lane_id, index])
	SumoFleetModel.start_own_mix(EditorApp.profile, lane_id, mix_name)
	if SumoFleetModel.set_mix_share(EditorApp.profile, lane_id, index, value):
		_touch_profile()
	EditorApp.end_edit()


func _set_fleet_type(lane_id: int, index: int, type_id: String, mix_name: String) -> void:
	EditorApp.begin_edit("fleet:%d:type:%d" % [lane_id, index])
	SumoFleetModel.start_own_mix(EditorApp.profile, lane_id, mix_name)
	if SumoFleetModel.set_mix_type(EditorApp.profile, lane_id, index, type_id):
		_touch_profile()
	EditorApp.end_edit()
	_refresh_later()


func _add_fleet_row(lane_id: int, mix_name: String) -> void:
	EditorApp.begin_edit("fleet:%d:add:%d" % [lane_id, SumoFleetModel.mix_of(EditorApp.profile, lane_id).size()])
	if SumoFleetModel.add_mix_row(EditorApp.profile, lane_id, mix_name):
		_touch_profile()
	EditorApp.end_edit()
	_refresh_later()


func _remove_fleet_row(lane_id: int, index: int, mix_name: String) -> void:
	EditorApp.begin_edit("fleet:%d:del:%d" % [lane_id, index])
	SumoFleetModel.start_own_mix(EditorApp.profile, lane_id, mix_name)
	if SumoFleetModel.remove_mix_row(EditorApp.profile, lane_id, index):
		_touch_profile()
	EditorApp.end_edit()
	_refresh_later()


func _reset_fleet(lane_id: int) -> void:
	EditorApp.begin_edit("fleet:%d:reset" % lane_id)
	SumoFleetModel.reset_own_mix(EditorApp.profile, lane_id)
	_touch_profile()
	EditorApp.end_edit()
	_refresh_later()


func _touch_profile() -> void:
	EditorApp.profile.touch()
	EditorApp.doc.dirty = true              # 专业档也是内容：标题的 * 与自动保存都要跟


func _open_type_dialog() -> void:
	EditorApp.open_type_dialog()


# --- 转率 ---------------------------------------------------------------

func _ratio_spin(t: Dictionary) -> SpinBox:
	var tid := int(t["id"])
	# ★ min 0.01 / step 0.01：契约要求 ratio ∈ (0, 1]，填 0 会被校验器判越界。
	#   想禁掉一条转向请直接删转向，而不是把比例压到 0。
	var s := _spin(float(t.get("ratio", 1.0)), 0.01, 1.0, 0.01,
		func(v: float): _set_ratio(tid, v), "ratio:%d" % tid)
	s.custom_minimum_size.x = 84
	s.custom_arrow_step = 0.05
	s.tooltip_text = "到车道 #%d 的转率权重（0.01~1）。导出时按本进口所有转向归一化" % int(t["dst"])
	return s


## 该进口有没有车流。**没有需求曲线 = 这个进口永远不出车**，用红字说明。
func _demand_summary(lane_id: int) -> Control:
	var rec: Dictionary = EditorApp.doc.find_profile("demand", lane_id)
	var lane: Dictionary = EditorApp.doc.find_lane(lane_id)
	var is_motor: bool = Doc.MOTOR_LANE_TYPES.has(String(lane.get("type", "")))

	if rec.is_empty():
		var box := VBoxContainer.new()
		box.add_theme_constant_override("separation", UITokens.S1)
		var warn := _wrapped("⚠ 没有需求曲线 → 这个进口不会出车。")
		warn.add_theme_color_override("font_color", UITokens.CONFLICT)
		box.add_child(warn)
		# 一键兜底：默认 360 辆/时（0.1 辆/秒），用户随后可在「需求曲线」里改
		var make := _btn("＋ 给这个进口建需求（360 辆/时）", func(): _profile_make("demand", lane_id))
		box.add_child(make)
		return box

	var rates: Array = rec.get("flow_rates", [])
	var first := float(rates[0]) * 3600.0 if not rates.is_empty() else 0.0
	var text := "%.0f 辆/时（%d 段）" % [first, rates.size()]
	if rates.size() > 1:
		text += "，另有 %d 段按时段变化" % (rates.size() - 1)
	if first <= 0.0:
		var zero := _wrapped("⚠ 需求曲线第一段流量是 0 → 这个进口暂时不出车（检查曲线数值）。")
		zero.add_theme_color_override("font_color", UITokens.CONFLICT)
		return zero
	if not is_motor:
		var wrong := _wrapped("⚠ 这条是%s车道，却挂着机动车需求 —— 车会从同一条边的机动车道出发。"
			% String(lane.get("type", "")))
		wrong.add_theme_color_override("font_color", UITokens.CONFLICT)
		return wrong
	return _wrapped(text)


## "直行 60% / 右转 25% / 左转 15%" —— 让用户一眼看到权重换算成的实际占比
func _ratio_summary(lane_id: int) -> String:
	var parts: Array = []
	for row in SumoFleetModel.normalized_percent(EditorApp.doc, lane_id):
		parts.append("%s %.0f%%" % [_turn_type_label(String(row["type"])), float(row["pct"])])
	return "归一化后：" + " ／ ".join(PackedStringArray(parts))


func _set_ratio(turn_id: int, value: float) -> void:
	EditorApp.begin_edit("ratio:%d" % turn_id)
	if SumoFleetModel.set_ratio(EditorApp.doc, turn_id, value):
		EditorApp.doc.dirty = true
	EditorApp.end_edit()


func _turn_type_label(ty: String) -> String:
	var i: int = TURN_TYPES.find(ty)
	return String(TURN_LABELS[i]) if i >= 0 else ty


# --- 多选（框选路）：批量动作 ---------------------------------------------
## ★ 三个批量动作全部由 EditorApp 内部包 begin_edit/end_edit = 一步撤销；
##   面板只发命令，不直接写 Doc（免得撤销口径散在两处）。

func _build_multi() -> void:
	var ids: Array = EditorApp.selection_roads()
	_header("批", "多选", "已选 %d 条" % ids.size())
	_section("批量操作")
	# ★ 批量条（P3-2）：把"已选多少 + 删除"合成顶部一条 —— 删除是危险操作，放在最显眼处、用红字按钮，
	#   不再埋在最后一行。`head` 仍是同一个 Label（改限速时它要就地刷新"已写入 N 条"）。
	var bar := Clay.inset(UITokens.S2)
	var bh := HBoxContainer.new()
	bh.add_theme_constant_override("separation", UITokens.S2)
	bar.add_child(bh)
	var head := Label.new()
	head.text = "已选 %d 条" % ids.size()
	head.theme_type_variation = "TitleLabel"
	head.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	head.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	bh.add_child(head)
	var del := _btn("删除这 %d 条" % ids.size(), func(): EditorApp.delete_selection())
	del.theme_type_variation = "DangerButton"
	del.custom_minimum_size = Vector2(116, 30)
	bh.add_child(del)
	_add_row("", bar)

	# 统一限速：★ 原地改（Doc.set_road_template_field），不重建车道、不动连接/相位/需求
	var sp := SpinBox.new()
	sp.min_value = 1.0
	sp.max_value = 40.0
	sp.step = 0.5
	sp.value = _common_speed(ids)
	sp.value_changed.connect(func(v):
		var n: int = EditorApp.set_selection_speed(float(v))
		head.text = "已选 %d 条　→　限速 %.1f m/s 已写入 %d 条" % [ids.size(), float(v), n])
	_add_row("统一限速 (m/s)", sp)

	# 套模板（★ 会重建车道：引用旧车道的连接会被删，与单条一致，可一次撤销回退）
	var presets := VBoxContainer.new()
	for p in ROAD_PRESETS:
		var b := _btn(String(p["name"]), func():
			EditorApp.apply_selection_template(p["t"])
			_refresh_later())        # ★ 延后一拍重建（B4/C12）：别在按钮自己的 pressed 回调里把它拆掉
		b.clip_text = true                                            # 同上：窄坞里允许裁字
		b.tooltip_text = String(p["name"])
		presets.add_child(b)
	_add_row("套模板", presets)

	var hint := Label.new()
	hint.text = "批量操作整体算一步，Ctrl+Z 一次全回退"
	hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	hint.custom_minimum_size = Vector2(120, 0)
	_add_row("", hint)

## 多选里的"当前限速"：全一致就显示那个值，不一致就用第一条的值（只作 SpinBox 初值）
func _common_speed(ids: Array) -> float:
	var v := -1.0
	for rid in ids:
		var r: Dictionary = EditorApp.doc.find_road(int(rid))
		if r.is_empty():
			continue
		var s := float((r.template as Dictionary).get("speed_limit", 13.89))
		if v < 0.0:
			v = s
		elif not is_equal_approx(v, s):
			return maxf(s, 1.0)
	return v if v > 0.0 else 13.89


# ============================================================================
# SUMO 网络覆盖层（S4）
# ----------------------------------------------------------------------------
# ★ 一屏一对象：车道属性挂车道、连接属性挂连接、路口控制挂路口、netconvert 开关挂关卡。
# ★ 数据全在 `.sumo.json` 的 `overrides` / `netconvert` 段 —— `.spmap` 一个字段都不加
#   （游戏侧 SimCore 不认识这些概念，见技术方案06 §3 D1）。
# ★ 全部走 `SumoNetModel`（纯数据层），面板不直接改字典；改完 `profile.touch()`
#   让 `content_signature()` 变一下（导出/自动保存据此判断"有没有脏"）。
# ============================================================================

## 车道：允许车型 / 限速覆盖 / 宽度覆盖
func _build_sumo_lane(lane_id: int) -> void:
	if not EditorApp.is_pro():
		return
	var profile: SumoProfile = EditorApp.profile
	var row: Dictionary = SumoNetModel.lane_row(profile, lane_id)
	var lane: Dictionary = EditorApp.doc.find_lane(lane_id)
	if lane.is_empty():
		return

	_section("SUMO 车道属性")
	_add_row("允许车型", _net_choice(
		SumoNetModel.LANE_ACCESS, SumoNetModel.LANE_ACCESS_LABELS,
		String(row.get("access", "")),
		func(v: String): _set_lane_net(lane_id, "access", v)))
	_add_row("限速覆盖 (m/s)", _spin(float(row.get("speed", 0.0)), 0.0, 60.0, 0.5,
		func(v: float): _set_lane_net(lane_id, "speed", v), "net_lane_speed:%d" % lane_id))
	_add_row("宽度覆盖 (m)", _spin(float(row.get("width", 0.0)), 0.0, 10.0, 0.25,
		func(v: float): _set_lane_net(lane_id, "width", v), "net_lane_width:%d" % lane_id))
	_add_row("本路限速", _info("%.1f m/s" % float(lane.get("speed_limit", 0.0))))
	_add_row("说明", _wrapped("填 0 = 不覆盖（用路网自己的值）。"
		+ "「仅公交」= 把这条车道变成公交专用道（sumo-gui 里会变灰、小汽车不再进）。"))


## 连接：允许车型 / 限速覆盖
func _build_sumo_connection(turn_id: int) -> void:
	if not EditorApp.is_pro():
		return
	var profile: SumoProfile = EditorApp.profile
	var row: Dictionary = SumoNetModel.connection_row(profile, turn_id)

	_section("SUMO 连接属性")
	_add_row("允许车型", _net_choice(
		SumoNetModel.TURN_ACCESS, SumoNetModel.TURN_ACCESS_LABELS,
		String(row.get("access", "")),
		func(v: String): _set_connection_net(turn_id, "access", v)))
	_add_row("限速覆盖 (m/s)", _spin(float(row.get("speed", 0.0)), 0.0, 60.0, 0.5,
		func(v: float): _set_connection_net(turn_id, "speed", v), "net_turn_speed:%d" % turn_id))
	_add_row("说明", _wrapped("「禁止机动车通行」= 这条连接只留行人。"
		+ "★ 不是删掉连接 —— 删了会让 SUMO 直接崩溃退出。"))


## 路口：SUMO 的控制方式
func _build_sumo_junction(node_id: int) -> void:
	if not EditorApp.is_pro():
		return
	var node: Dictionary = EditorApp.doc.find_node(node_id)
	if node.is_empty():
		return
	var profile: SumoProfile = EditorApp.profile

	_section("SUMO 路口控制")
	if String(node.get("type", "")) == "Signalized":
		_add_row("控制方式", _info("红绿灯（由信号配时决定）"))
		_add_row("说明", _wrapped("要改成让行 / 全向停，先把上面的「节点类型」改成普通路口 —— "
			+ "直接改这里会让配时静默失效。"))
		return
	_add_row("控制方式", _net_choice(
		SumoNetModel.JUNCTION_TYPES, SumoNetModel.JUNCTION_TYPE_LABELS,
		SumoNetModel.junction_type(profile, node_id),
		func(v: String): _set_junction_net(node_id, v)))
	# ★ 度数不足的节点（典型：路端点）netconvert 只给 dead_end，不生成可配置的路口。
	#   这里照写不误，但要提前说清楚"导出时会被跳过"，别让用户以为配了没用上。
	var roads := EditorApp.doc.road_count_at(node_id)
	if roads < 3:
		_add_row("注意", _wrapped("该节点只有 %d 条路，netconvert 不会为它生成独立路口 —— "
			% roads + "这里的设置导出时会被跳过。"))
	_add_row("说明", _wrapped("「自动」= netconvert 按路数自己决定。"
		+ "改这里只影响导出的 net.xml，游戏侧判定仍走 .spmap。"))


## 关卡：netconvert 的 4 个常用开关
func _build_sumo_netgen() -> void:
	if not EditorApp.is_pro():
		return
	var profile: SumoProfile = EditorApp.profile

	_section("SUMO 路网生成", false, "level/SUMO 路网生成")
	for key in SumoNetModel.NETCONVERT_KEYS:
		var k := String(key)
		var tip := String(SumoNetModel.NETCONVERT_TIPS.get(k, ""))
		var box := _check(SumoNetModel.netconvert_option(profile, k),
			func(on: bool): _set_netconvert_net(k, on))
		box.tooltip_text = tip
		_add_row(String(SumoNetModel.NETCONVERT_LABELS.get(k, k)), box)
	_add_row("说明", _wrapped("这 4 项决定 net.xml 怎么生成，改完要重新导出。"
		+ "车道宽在导出面板里设（默认 3.5 m）。"))


## 关卡：SUMO 运行配置（S5）。五个字段原样进 `.sumocfg`，点「▶ 试跑」就按这个时长跑。
func _build_sumo_run_config() -> void:
	if not EditorApp.is_pro():
		return
	var profile: SumoProfile = EditorApp.profile

	_section("SUMO 运行配置", false, "level/SUMO 运行配置")
	for key in SumoRunModel.KEYS:
		var k := String(key)
		var r: Array = SumoRunModel.RANGES[k]
		var unit := String(SumoRunModel.UNITS[k])
		var label := String(SumoRunModel.LABELS[k])
		if unit != "":
			label += " (%s)" % unit
		var sp := _spin(SumoRunModel.value(profile, k), float(r[0]), float(r[1]), float(r[2]),
			func(v: float): _set_sumo_run_field(k, v), "sumo_run:%s" % k)
		sp.tooltip_text = String(SumoRunModel.TIPS[k])
		_add_row(label, sp)
	_add_row("当前配置", _info(SumoRunModel.describe(profile)))
	_add_row("", _btn("恢复默认（0~3600 / 步长 1 / 种子 42 / 传送 300）", _reset_sumo_run))
	_add_row("说明", _wrapped("这些写进 .sumocfg（导出面板勾「SUMO .sumocfg」）。"
		+ "导出完点结果里的「▶ 试跑」，就跑这个时长并回统计。"
		+ "改完记得重新导出，sumocfg 才会更新。"))


# --- S4 的小工具 -----------------------------------------------------------

## 下拉：选项与显示文字分开传（值进文件，中文给人看）。改完包一步撤销。
func _net_choice(options: Array, labels: Array, cur: String, cb: Callable) -> OptionButton:
	var o := OptionButton.new()
	for i in options.size():
		o.add_item(String(labels[i]) if i < labels.size() else String(options[i]))
	o.select(maxi(options.find(cur), 0))       # 先选中、后连信号，避免多余写回
	o.item_selected.connect(func(_i): EditorApp.begin_edit())
	o.item_selected.connect(func(i: int): cb.call(String(options[i])))
	o.item_selected.connect(func(_i): EditorApp.end_edit())
	return o


func _set_lane_net(lane_id: int, key: String, value) -> void:
	var profile: SumoProfile = EditorApp.profile
	if not SumoNetModel.set_lane_field(profile, lane_id, key, value):
		return
	profile.touch()          # 不重建面板：下拉/数字框自己显示的就是新值


func _set_connection_net(turn_id: int, key: String, value) -> void:
	var profile: SumoProfile = EditorApp.profile
	if not SumoNetModel.set_connection_field(profile, turn_id, key, value):
		return
	profile.touch()


func _set_junction_net(node_id: int, type: String) -> void:
	var profile: SumoProfile = EditorApp.profile
	if not SumoNetModel.set_junction_type(profile, node_id, type):
		return
	profile.touch()


func _set_netconvert_net(key: String, on: bool) -> void:
	var profile: SumoProfile = EditorApp.profile
	if not SumoNetModel.set_netconvert_option(profile, key, on):
		return
	profile.touch()
	# ★ 探针签名里已经带了这 4 个开关（EditorApp.sumo_probe_signature），
	#   所以不用手动清探针缓存 —— 下次打开配时面板会自己重跑。


func _set_sumo_run_field(key: String, v: float) -> void:
	var profile: SumoProfile = EditorApp.profile
	if not SumoRunModel.set_value(profile, key, v):
		return
	# end 必须大于 begin：改 begin 越界时把 end 抬上来（用户能看见的联动）
	if key == "begin" and SumoRunModel.keep_span_valid(profile):
		profile.touch()
		_refresh_later()          # 另一个框的数字变了，重建一次让显示跟上
		return
	profile.touch()


func _reset_sumo_run() -> void:
	var profile: SumoProfile = EditorApp.profile
	if not SumoRunModel.reset(profile):
		return
	profile.touch()
	_refresh_later()              # 五个数字都要变回默认
