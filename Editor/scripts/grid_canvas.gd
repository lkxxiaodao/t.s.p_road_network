@tool
class_name GridCanvas
extends Node2D

## 世界坐标约定（全项目唯一口径）：
##   数据一律以「米」存储（与 .spmap 的 x / y / length 一致）；
##   只有画到屏幕上时才 × PPM 变成像素。
##   本节点本地坐标 (0,0) = 世界原点（第一关路口的中心）。

const PPM := 4.0              ## 像素 / 米 —— 全项目唯一真值
const GRID_METERS := 5.0      ## 最细网格间距（米）
const MAGNET_PX := 14.0       ## 节点磁吸半径（屏幕像素，不随缩放变化）
const ANGLE_LOCK_STEP_DEG := 45.0   ## 按住 Shift 拖时的锁角步长（相对起点：0°/45°/90°）
const MEASURE_COLOR := Color(1.0, 0.55, 0.15, 0.95)   ## 测量线颜色（临时线，不进 Doc）
const BOX_CLICK_M := 2.0            ## 框选拖出的矩形小于这个（米）→ 当成右键单击（在选中集里加/减一条）

## 网格分级（LOD）：倍数是相对最细格。1→5m、5→25m、25→125m。
## 缩小时细线太密会自动渐隐，只留更粗的一级。
const LOD_MUL: Array[int] = [1, 5, 25, 125]
const FADE_NEAR_PX := 60.0     ## 某级线屏幕间距低于此值 → 完全隐藏
const FADE_FAR_PX := 80.0     ## 某级线屏幕间距高于此值 → 完全显示

## ★ 画布配色一律读 `UITokens`（视觉唯一真值，见 docs/ui/UI-R0 §1.1/§1.1b）。
##   **不要再写死十六进制**，也不要在 `GridCanvas.tscn` 里覆盖这四个 export ——
##   早先场景里覆盖了 background_color 与 line_color，只改脚本默认值完全没效果（踩过）。
@export var background_color: Color = UITokens.SURFACE_0:
	set(v):
		background_color = v
		queue_redraw()

@export var line_color: Color = UITokens.GRID_FINE:
	set(v):
		line_color = v
		queue_redraw()

@export var major_color: Color = UITokens.GRID_MAJOR:
	set(v):
		major_color = v
		queue_redraw()

## 125 m 那一级（LOD 最后两档）—— 远景第一眼分层靠它
@export var bold_color: Color = UITokens.GRID_BOLD:
	set(v):
		bold_color = v
		queue_redraw()

@export var origin_color: Color = UITokens.GRID_AXIS:
	set(v):
		origin_color = v
		queue_redraw()

@export_range(2.0, 32.0, 1.0) var origin_arm_px: float = 6.0:
	set(v):
		origin_arm_px = v
		queue_redraw()

## ★ 会话级设置（X 键切，状态栏显示）：不进 .spmap / .spdeco，也不写 user://
var snap_enabled: bool = true        ## 格点吸附开关；关掉 = 只留节点磁吸

## ★ M 键测量（临时线）：只活在内存里，不写 Doc、不进撤销栈
var measuring := false
var measure_has_a := false
var measure_has_b := false
var measure_a_m := Vector2.ZERO
var measure_b_m := Vector2.ZERO

var _measure_node: Node2D = null     ## 测量线专用子节点（z_index 拉高，压在道路/装饰之上）

## ★ 右键拖 = 框选路（左键拖动一律交给模式 = 画路，按不按 Ctrl 都一样；2026-10-25 从 Ctrl+左键拖 迁来）
var box_selecting := false
var box_start_m := Vector2.ZERO
var box_end_m := Vector2.ZERO
var _sel_node: Node2D = null         ## 多选高亮 / 框选矩形专用子节点

## ★ C3 脏标记：网格线是画在世界坐标里的，相机一动可见范围就变 → 必须重画；
##   但相机不动时（最常见）每帧重画一遍纯属白烧，大图尤其明显。
var _grid_sig := ""

func _ready() -> void:
	# ★ 多选高亮画在**独立**的 SelectLayer（Node2D）上，`_draw` 不会自己重画。
	#   以前只在 `_process` 里"有选中就刷"，一旦选中集被别处清空（切模式 / 删除 / 撤销），
	#   这根线就留在画布上不再更新 —— 老板报的"蓝色线切模式还在、删了路还在"就是这个（2026-10-28）。
	if Engine.is_editor_hint():
		return
	EditorApp.selection_changed.connect(_refresh_sel)


func _process(_delta: float) -> void:
	# ★ 本脚本是 @tool：编辑器里打开 Main.tscn 时 _process 照样每帧跑，而这时没有 EditorApp 可用
	#   （见下面 _unhandled_input 的保护说明）→ 编辑器视口保持原行为（每帧重画）。
	if Engine.is_editor_hint():
		queue_redraw()
		return
	var sig := _grid_sig_now()
	if sig != _grid_sig:
		_grid_sig = sig
		queue_redraw()
	# 测量等第二个点时，预览线要跟着鼠标走（其余时候测量层不用重画）
	if measuring and measure_has_a and not measure_has_b:
		_refresh_measure()
	# 多选高亮 / 框选矩形：有就不空转（拖动中每帧要跟着动）
	if box_selecting or not EditorApp.selection.is_empty():
		_refresh_sel()

## 网格层的"画面签名"：相机位置 + 缩放 + 视口尺寸 + 图层开关。
## ★ 底图状态不用管：`basemap_layer._redraw()` 会直接给父节点（本节点）queue_redraw。
func _grid_sig_now() -> String:
	var cam := get_viewport().get_camera_2d()
	var cp := Vector2.ZERO
	var cz := Vector2.ONE
	if cam != null:
		cp = cam.global_position
		cz = cam.zoom
	var vs := get_viewport_rect().size
	return "%.2f/%.2f/%.4f/%.4f|%dx%d|%d%d" % [cp.x, cp.y, cz.x, cz.y,
		int(vs.x), int(vs.y), int(_show_grid_layer()), int(_show_basemap_layer())]

func _unhandled_input(event: InputEvent) -> void:
	if Engine.is_editor_hint():
		return
	# ★ 快照只在"手势边界"打：左键按下 / 按键 → 记；左键松手 / 按键 → 判定
	#   拖动中间的每个 motion 都不记，所以"拖一次 = 一步"
	var is_press: bool = event is InputEventMouseButton \
		and event.button_index == MOUSE_BUTTON_LEFT and event.pressed
	var is_release: bool = event is InputEventMouseButton \
		and event.button_index == MOUSE_BUTTON_LEFT and not event.pressed
	var is_key: bool = event is InputEventKey and event.pressed and not event.echo
	if is_press or is_key:
		EditorApp.begin_edit()
	_dispatch(event)
	if is_release or is_key:
		EditorApp.end_edit()


func _dispatch(event: InputEvent) -> void:
	# ★ F1 必须在输入框守卫**之前**处理：键位表一打开就把焦点交给它的搜索框，
	#   守卫（下面那行 `focus is LineEdit`）会把 F1 一起吞掉 —— 面板右下角写着「F1 关闭」，
	#   按下去却毫无反应（老板 2026-11-03 报的）。面板开关本来也不该受"正在打字"影响。
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_F1:
		EditorApp.toggle_help()
		get_viewport().set_input_as_handled()
		return
	# ★ 面板输入框有焦点时不抢键（否则打数字会切模式、打 R 会转装饰）
	var focus := get_viewport().gui_get_focus_owner()
	if event is InputEventKey and focus != null and (focus is LineEdit or focus is TextEdit):
		return
	if event is InputEventKey and event.pressed and not event.echo:
		if _handle_global_key(event):
			get_viewport().set_input_as_handled()
			return
		if event.keycode == KEY_DELETE or event.keycode == KEY_BACKSPACE:
			# ★ 多选（>1 条路）→ Delete 删整批，整体一个撤销步
			if EditorApp.selection.size() > 1:
				EditorApp.delete_selection()
				get_viewport().set_input_as_handled()
				return
			if EditorApp.mode == EditorMode.Kind.DECO and EditorApp.selected_deco >= 0:
				EditorApp.doc.delete_deco(EditorApp.selected_deco)
				EditorApp.select_deco(-1)
				get_viewport().set_input_as_handled()
			elif EditorApp.mode == EditorMode.Kind.CONNECT and EditorApp.selected_turn >= 0:
				EditorApp.doc.delete_turn(EditorApp.selected_turn)
				EditorApp.select_turn(-1)
				get_viewport().set_input_as_handled()
			elif EditorApp.selected_island >= 0:
				# ★ 安全岛不是"删掉就没了"的实体：它背后挂着两条过街段，直接删岛会留下
				#   一对没有岛的 leg（校验器报 crosswalk_island_missing）。Delete = 并回一段。
				var isl := EditorApp.doc.find_island(EditorApp.selected_island)
				var owner_tid := -1
				for t in EditorApp.doc.turns:
					if int(t.get("island", -1)) == EditorApp.selected_island:
						owner_tid = int(EditorApp.doc.first_leg_turn(t).id)
						break
				if owner_tid >= 0:
					EditorApp.begin_edit("island_merge")
					var m: Dictionary = EditorApp.doc.merge_crosswalk(owner_tid)
					EditorApp.end_edit()
					if bool(m.get("ok", false)):
						print("安全岛 #%d 已并入一段过街（Ctrl+Z 可撤销）" % int(isl.get("id", -1)))
					else:
						print("合并失败：%s" % String(m.get("error", "")))
				EditorApp.select_island(-1)
				get_viewport().set_input_as_handled()
			elif EditorApp.selected_road >= 0:
				EditorApp.doc.delete_road(EditorApp.selected_road)
				# ★ 多选层：删完把已不存在的剔掉（若还选了别的路，剩下那些仍保持选中）
				EditorApp.prune_selection()
				get_viewport().set_input_as_handled()
			return
	# ★ 测量中：左键只用来落测量点，不再交给模式（否则会顺手画一条路 / 选一个对象）
	if measuring and event is InputEventMouseButton \
			and event.button_index == MOUSE_BUTTON_LEFT and event.pressed:
		_take_measure_point(mouse_world_meters())
		get_viewport().set_input_as_handled()
		return
	# ★ 右键拖 = 框选路（2026-10-25 从「Ctrl+左键拖」迁来，老板批准的唯一功能变更，见 docs/ui/UI-R1 P0-3）。
	#   左键拖动原样交给模式：现在按不按 Ctrl 都走模式（画路不再被 Ctrl 抢走）。
	#   右键原地单击（框 < BOX_CLICK_M）= 在选中集里加/减一条，与原「Ctrl+单击」语义完全等价。
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_RIGHT:
		if event.pressed:
			box_selecting = true
			box_start_m = mouse_world_meters()
			box_end_m = box_start_m
			_refresh_sel()
			get_viewport().set_input_as_handled()
			return
		if box_selecting:
			box_selecting = false
			_commit_box_select(EditorApp.doc)
			# ★ 必须重绘一次：框画在 SelectLayer 的 _draw_sel 里，松手不再重绘的话，
			#   "框里没选中任何东西"（pick/set_selection 都没发出变更）时框会留在画布上，
			#   直到下一次随便什么操作把它擦掉。有无选中都一样擦。
			_refresh_sel()
			get_viewport().set_input_as_handled()
			return
	elif event is InputEventMouseMotion and box_selecting:
		box_end_m = mouse_world_meters()
		_refresh_sel()
		get_viewport().set_input_as_handled()
		return
	var world_m := mouse_world_meters()
	match EditorApp.mode:
		EditorMode.Kind.ROAD:
			EditorApp.road_mode.handle(event, world_m, self, EditorApp.doc)
		EditorMode.Kind.CONNECT:
			EditorApp.connect_mode.handle(event, world_m, self, EditorApp.doc)
		EditorMode.Kind.TRAFFIC:
			EditorApp.traffic_mode.handle(event, world_m, self, EditorApp.doc)
		EditorMode.Kind.DECO:
			EditorApp.deco_mode.handle(event, world_m, self, EditorApp.doc)

# --- 全局快捷键（输入框有焦点时到不了这里）-------------------------------

const MODE_KEYS := {
	KEY_1: EditorMode.Kind.ROAD,
	KEY_2: EditorMode.Kind.CONNECT,
	KEY_3: EditorMode.Kind.TRAFFIC,
	KEY_4: EditorMode.Kind.DECO,
}

func _handle_global_key(e: InputEventKey) -> bool:
	if e.ctrl_pressed:
		match e.keycode:
			KEY_N:
				EditorApp.new_document()         # Ctrl+N = 新建（有未保存改动会先问）
				return true
			KEY_Z:
				EditorApp.undo()
				return true
			KEY_Y:
				EditorApp.redo()
				return true
			KEY_C:
				EditorApp.copy_selection()
				return true
			KEY_V:
				EditorApp.paste_clipboard()
				return true
			KEY_S:
				if e.shift_pressed:
					EditorApp.ask_save_as()      # Ctrl+Shift+S = 另存为
				else:
					EditorApp.save_current()     # Ctrl+S = 保存（没存过则弹另存为）
				return true
			KEY_O:
				EditorApp.ask_open()             # Ctrl+O = 打开
				return true
			KEY_E:
				if e.shift_pressed:
					EditorApp.toggle_edition()        # Ctrl+Shift+E = 基础 / 专业
				else:
					EditorApp.open_export_dialog()    # Ctrl+E = 导出为其他格式
				return true
		return false
	match e.keycode:
		# F1 已提到 `_dispatch` 开头处理（不受输入框焦点影响），这里不再重复
		KEY_HOME:
			EditorApp.fit_all()              # ★ Home = 缩放到全部内容（B19）
			return true
		KEY_M:
			start_measure()
			return true
		KEY_X:
			toggle_snap()
			return true
		KEY_ESCAPE:
			if measuring:
				_cancel_measure()
				return true
			return false     # 没在测量 → 不抢 Esc，留给模式（连接/装饰模式用它取消半成品）
	if MODE_KEYS.has(e.keycode):
		var k: int = MODE_KEYS[e.keycode]
		EditorApp.set_mode(k)
		return true
	return false


func _draw() -> void:
	var cam := get_viewport().get_camera_2d()
	if cam == null:
		return

	# 当前可见的世界区域（换算到本节点局部坐标）
	var half := get_viewport_rect().size * 0.5 / cam.zoom
	var cam_local := to_local(cam.global_position)
	var tl := cam_local - half
	var br := cam_local + half

	var base := GRID_METERS * PPM      # 最细一级的世界像素间距（20 px）
	var fine_w := 1.0 / cam.zoom.x     # 屏幕上线宽恒定
	var bold_w := 2.0 / cam.zoom.x

	draw_rect(Rect2(tl, br - tl), background_color)

	# ★ 层序：背景板 → 底图 → 网格线。底图自己不会画（BasemapLayer 只提供 draw_into），
	#   所以必须在【本节点的 _draw 里】按位置调一次，才能保证它压在背景板之上、网格线之下。
	#   没挂底图层（或没载入图）→ _basemap_node() 返回 null，这一段等于不存在，行为与以前完全一致。
	var bm := _basemap_node()
	if bm != null and _show_basemap_layer():
		bm.draw_into(self)

	# ★ 图层开关（B19）：网格层关掉 → 坐标轴与原点十字都不画；背景板照画（否则是个黑洞）
	if _show_grid_layer():
		_draw_axis(tl, br, base, true, cam.zoom.x, fine_w, bold_w)   # 竖线
		_draw_axis(tl, br, base, false, cam.zoom.y, fine_w, bold_w)  # 横线
		_draw_origin_axes(tl, br, bold_w)                            # 贯穿 X/Y 轴 + 原点十字

## 图层开关读取（B19）。★ 本脚本是 @tool：编辑器里打开 Main.tscn 时 EditorApp（autoload）那个节点
##   没有脚本实例，访问它的任何成员都会刷 "Invalid access ... Node (editor_app.gd)" → 编辑器里一律当全开。
##   ★ 也不许写 `var x := EditorApp.xxx`（autoload 与 mode 脚本互为编译依赖，推断不出类型会连累整份编不过）。
func _show_grid_layer() -> bool:
	if Engine.is_editor_hint():
		return true
	return bool(EditorApp.show_grid)

func _show_basemap_layer() -> bool:
	if Engine.is_editor_hint():
		return true
	return bool(EditorApp.show_basemap)

## 原点处的 X/Y 贯穿轴 + 十字（`GRID_AXIS` 最黑那一档）。
##
## 为什么要有贯穿轴：R0 §1.1b 把 `GRID_AXIS` 定义为「X/Y 坐标轴 + 原点十字」，
## 而原先只画了 `origin_arm_px`（±6 px）那么小的一个十字 —— 在一屏 40 格里根本找不到原点。
## 轴线比普通网格粗一点（1.5×），画在网格之后**覆盖**掉落在 x=0 / y=0 上的那条粗网格线。
## ⚠ 网格整体画在**路面之下**（RoadLayer 是后画的子节点），所以轴再黑也不会弄脏路面。
## ⚠ 线宽必须**大于** `bold_w`：x=0 / y=0 这两条在 `_draw_axis` 里已经按"125 m 粗线"画过一遍，
## 轴线如果比它细，就会整条被裹在粗线里看不出来（第一版传 fine_w 就是这么翻车的）。
func _draw_origin_axes(tl: Vector2, br: Vector2, bold_w: float) -> void:
	var w := bold_w * 1.4
	if tl.x <= 0.0 and br.x >= 0.0:
		draw_line(Vector2(0, tl.y), Vector2(0, br.y), origin_color, w, true)
	if tl.y <= 0.0 and br.y >= 0.0:
		draw_line(Vector2(tl.x, 0), Vector2(br.x, 0), origin_color, w, true)
	# 十字再加长一点，压在轴的交点上更醒目
	var arm := origin_arm_px * 2.0
	draw_line(Vector2(-arm, 0), Vector2(arm, 0), origin_color, w, true)
	draw_line(Vector2(0, -arm), Vector2(0, arm), origin_color, w, true)
## 画一个方向的所有网格线；vertical=true 时沿 x 推进画竖线
func _draw_axis(tl: Vector2, br: Vector2, base: float, vertical: bool,
		zoom_c: float, fine_w: float, bold_w: float) -> void:
	var lo := tl.x if vertical else tl.y
	var hi := br.x if vertical else br.y
	var k0 := int(floor(lo / base))
	var k1 := int(ceil(hi / base))
	for k in range(k0, k1 + 1):
		var lv := _level_of(k)
		var sp := base * float(LOD_MUL[lv]) * zoom_c   # 这一级线的屏幕间距
		var a := smoothstep(FADE_NEAR_PX, FADE_FAR_PX, sp)
		if a <= 0.01:
			continue                                   # 太密 → 整级不画
		var col: Color = _grid_color_of(lv)
		col.a *= a
		var w: float = bold_w if lv >= 1 else fine_w
		var p := float(k) * base
		if vertical:
			draw_line(Vector2(p, tl.y), Vector2(p, br.y), col, w, true)
		else:
			draw_line(Vector2(tl.x, p), Vector2(br.x, p), col, w, true)

## LOD 级别 → 颜色（四档炭灰，R0 §1.1b）。0=5 m 细线、1=25 m 主线、≥2=125 m 粗线。
## ⚠ 网格画在**路面之下**（RoadLayer 是子节点、后画），所以哪怕最黑的轴色也不会弄脏路面。
func _grid_color_of(lv: int) -> Color:
	if lv >= 2:
		return bold_color
	if lv == 1:
		return major_color
	return line_color

## 这条线属于哪一级：取"能整除它的最大倍数"
func _level_of(k: int) -> int:
	for lv in range(LOD_MUL.size() - 1, -1, -1):
		if k % LOD_MUL[lv] == 0:
			return lv
	return 0

# --- 坐标换算（世界米 ↔ 画布像素）----------------------------------------

## 世界坐标（米，Y+ 为北）→ 画布像素
static func world_to_px(p_m: Vector2) -> Vector2:
	return Vector2(p_m.x, -p_m.y) * PPM

## 画布像素 → 世界坐标（米，Y+ 为北）
static func px_to_world(p_px: Vector2) -> Vector2:
	return Vector2(p_px.x, -p_px.y) / PPM

## 当前鼠标的世界坐标（米）
func mouse_world_meters() -> Vector2:
	return px_to_world(to_local(get_global_mouse_position()))

# --- 吸附（画路时用）----------------------------------------------------

## 位置吸附到最近的 5 米格交点
## ★ 吸附开关关掉时原样返回 —— 判断只写在这一处，所有调用处（画路端点 / 拖节点 / 拆分点 / 装饰 Shift）
##   都自动跟着开关走，不用各自再判一遍。
func snap_m(p: Vector2) -> Vector2:
	if not snap_enabled:
		return p
	return Vector2(roundf(p.x / GRID_METERS) * GRID_METERS,
			roundf(p.y / GRID_METERS) * GRID_METERS)

## 屏幕像素 → 世界米（按相机缩放折算，缩放级别不同磁吸手感一致）
func screen_px_to_m(px: float) -> float:
	var cam := get_viewport().get_camera_2d()
	var z := cam.zoom.x if cam != null else 1.0
	return px / (PPM * z)

## 解析一个端点：优先吸已有节点，其次吸最近的格交点。
## 返回 {"pos": Vector2(米), "node": 已有节点 id 或 -1}
func resolve_point(world_m: Vector2, doc: Doc) -> Dictionary:
	var r := screen_px_to_m(MAGNET_PX)
	var nid := doc.find_node_near(world_m, r)
	if nid >= 0:
		return {"pos": doc.node_pos(nid), "node": nid}
	return {"pos": snap_m(world_m), "node": -1}

## 连接模式用：转发给 RoadLayer，问哪条转向曲线离鼠标最近（tol_m 为米）
## ★ 图层关了就不许点中看不见的东西（C4，与 pick_deco 同口径）
## 这个点还落在路口范围内吗（连接模式：点空白但没出路口 → 保留路口选中）
func point_in_junction(world_m: Vector2, doc: Doc, node_id: int, pad_m: float = 0.0) -> bool:
	var rl = get_node_or_null("RoadLayer")
	if rl == null:
		return false
	return rl.point_in_junction(doc, world_m, node_id, pad_m)


func pick_turn(world_m: Vector2, doc: Doc, tol_m: float, only_node: int = -1) -> int:
	if not EditorApp.show_turns:
		return -1
	var rl = get_node_or_null("RoadLayer")
	if rl == null:
		return -1
	return rl.pick_turn(world_m, tol_m, only_node)

## 连接模式用：转发给 RoadLayer，问哪条车道离鼠标最近（tol_m 为米）
## ★ 同上：车道层关掉后点不中车道（C4）
func pick_lane(world_m: Vector2, doc: Doc, tol_m: float) -> int:
	if not EditorApp.show_lanes:
		return -1
	var rl = get_node_or_null("RoadLayer")
	if rl == null:
		return -1
	return rl.pick_lane(doc, world_m, tol_m)

## 车辆模式用：点到了哪个检测设施（S3）。返回设施 id，"" = 没点中。
## ★ 不受图层开关影响：设施没有独立图层，专业模式下始终可见可点。
func pick_facility(world_m: Vector2, doc: Doc, tol_m: float) -> String:
	var rl = get_node_or_null("RoadLayer")
	if rl == null or not rl.has_method("pick_facility"):
		return ""
	return String(rl.pick_facility(world_m, tol_m))


## 连接模式用：转发给 RoadLayer，问点到了哪个安全岛（E92 G2 · N6b）
## ★ 与 pick_turn 同口径：岛归「车道」层管，那层关掉就点不中
func pick_island(world_m: Vector2, doc: Doc, tol_m: float) -> int:
	if not EditorApp.show_lanes:
		return -1
	var rl = get_node_or_null("RoadLayer")
	if rl == null:
		return -1
	return rl.pick_island(world_m, tol_m)

## 装饰模式用：转发给 DecoLayer，问点到了哪个装饰实例
func pick_deco(world_m: Vector2, doc: Doc) -> int:
	# ★ 图层开关（B19）：装饰层关掉了 → 不许点到看不见的实例（DecoLayer 只认 visible 关绘制，
	#   pick_deco 走的是 Doc 数据，不看 visible，所以这一层判断必须留在本处）
	if not EditorApp.show_decos:
		return -1
	var dl = EditorApp.deco_layer
	if dl == null or not is_instance_valid(dl):
		dl = get_node_or_null("DecoLayer")      # 兜底
	if dl == null:
		return -1
	return dl.pick_deco(doc, world_m)

## 底图层节点（视图辅助）：先当子节点找，再当兄弟节点找 —— 两种挂法都认，挂错位置不至于白折腾。
## 没有这个节点、或节点的脚本不是底图层（没有 draw_into）→ 返回 null，调用处当"没有底图"处理。
func _basemap_node() -> Node:
	var n: Node = get_node_or_null("BasemapLayer")
	if n == null:
		var p := get_parent()
		if p != null:
			n = p.get_node_or_null("BasemapLayer")
	if n != null and not n.has_method("draw_into"):
		return null
	return n

# --- 拖动落点 / 角度锁（Shift）------------------------------------------

## 把 to 相对 from 的方向锁到最近的 45° 倍数（0°/45°/90°…），长度保持不变（米）。
## ★ 只算"落点"，不碰任何已有数据。
func snap_angle_45(from_m: Vector2, to_m: Vector2) -> Vector2:
	var v := to_m - from_m
	if v.length() < 0.001:
		return from_m
	var step := deg_to_rad(ANGLE_LOCK_STEP_DEG)
	var ang := roundf(atan2(v.y, v.x) / step) * step
	return from_m + Vector2(cos(ang), sin(ang)) * v.length()

## 拖动中的端点落点。
##   普通拖动 = 画路原来的规则（先磁吸已有节点，其次按"吸附"开关决定要不要吸格点）
##   按住 Shift = 只锁角度（相对起点 0/45/90），不磁吸也不吸格点 —— 这样"锁水平"必定 y 相同。
##                代价：Shift 拖到已有节点上不会复用那个节点（要复用就别按 Shift）。
func resolve_drag_point(from_m: Vector2, world_m: Vector2, lock_angle: bool, doc: Doc) -> Dictionary:
	if not lock_angle:
		return resolve_point(world_m, doc)
	return {"pos": snap_angle_45(from_m, world_m), "node": -1}

## 世界坐标（x 东 / y 北）下 from→to 的方位角：0° = 正北，顺时针，0..360
static func bearing_deg(from_m: Vector2, to_m: Vector2) -> float:
	var v := to_m - from_m
	return fposmod(rad_to_deg(atan2(v.x, v.y)), 360.0)

# --- M 键测量（临时线；不写 Doc、不进撤销栈）-----------------------------

## 公开入口：开始/重开测量（**M 键与左侧工具条共用这一条**，别各写一份）
func start_measure() -> void:
	measuring = true
	measure_has_a = false
	measure_has_b = false
	_refresh_measure()
	print("测量：左键点第一个点，再点第二个点；Esc 取消")


## 公开入口：退出测量（**Esc 与左侧工具条按钮共用这一条**）
func cancel_measure() -> void:
	_cancel_measure()


## 公开入口：切换格点吸附（**X 键与左侧工具条共用这一条**）
func toggle_snap() -> void:
	snap_enabled = not snap_enabled
	print("格点吸附：%s（关掉后只留节点磁吸）" % ("开" if snap_enabled else "关"))
	if not Engine.is_editor_hint():                  # ★ 本文件是 @tool：碰 EditorApp 前必须挡一下
		EditorApp.toast("格点吸附：%s" % ("开" if snap_enabled else "关"), "info")


## Esc：退出测量并清线
func _cancel_measure() -> void:
	measuring = false
	measure_has_a = false
	measure_has_b = false
	_refresh_measure()
	print("测量已取消")

## 左键落一个测量点；落点规则与画路一致（节点磁吸优先，其次看吸附开关）
func _take_measure_point(world_m: Vector2) -> void:
	var p: Vector2 = resolve_point(world_m, EditorApp.doc).pos
	if not measure_has_a:
		measure_a_m = p
		measure_has_a = true
		measure_has_b = false
	elif not measure_has_b:
		measure_b_m = p
		measure_has_b = true
		print("测量：%.2f m  方位角 %.1f°" % [
			measure_a_m.distance_to(measure_b_m), bearing_deg(measure_a_m, measure_b_m)])
	else:
		measure_a_m = p            # 第三个点 = 从这一点重新量
		measure_has_a = true
		measure_has_b = false
	_refresh_measure()

func _refresh_measure() -> void:
	_measure_layer().queue_redraw()

## 测量线的载体：一个 z_index 拉高的子节点。
## ★ 本批不许新增脚本文件，所以不写子类脚本，靠 CanvasItem 的 draw 信号画（drawing 标志在信号期间为真）。
func _measure_layer() -> Node2D:
	if _measure_node != null and is_instance_valid(_measure_node):
		return _measure_node
	var n := Node2D.new()
	n.name = "MeasureLayer"
	n.z_index = 200                  # 压在 RoadLayer / DecoLayer 之上
	n.draw.connect(_draw_measure.bind(n))
	add_child(n)
	_measure_node = n
	return n

## 测量线的画法（ci 就是这个 MeasureLayer 自己）
func _draw_measure(ci: CanvasItem) -> void:
	if not measuring or not measure_has_a:
		return
	var a_px := world_to_px(measure_a_m)
	if not measure_has_b:
		# 还差第二个点：从 A 拉一条到"光标会落到的地方"的预览线
		var pv: Vector2 = resolve_point(mouse_world_meters(), EditorApp.doc).pos
		ci.draw_line(a_px, world_to_px(pv), Color(MEASURE_COLOR, 0.6), 1.0, true)
		ci.draw_circle(a_px, 4.0, MEASURE_COLOR)
		return
	var b_px := world_to_px(measure_b_m)
	ci.draw_line(a_px, b_px, MEASURE_COLOR, 1.5, true)
	ci.draw_circle(a_px, 4.0, MEASURE_COLOR)
	ci.draw_circle(b_px, 4.0, MEASURE_COLOR)
	var mid := (a_px + b_px) * 0.5
	var txt := "%.2f m  方位角 %.1f°" % [
		measure_a_m.distance_to(measure_b_m), bearing_deg(measure_a_m, measure_b_m)]
	# ★ 2026-10-27：原来是淡黄 (1,0.95,0.6)，压在奶油画布上等于看不见（老板实测）。
	#   改成深墨字 + 一圈浅色描边（画 4 个偏移副本），压在路上或格子上都读得清。
	var pos := mid + Vector2(8.0, -8.0)
	var halo := UITokens.SURFACE_0
	for off in [Vector2(1, 0), Vector2(-1, 0), Vector2(0, 1), Vector2(0, -1)]:
		ci.draw_string(ThemeDB.fallback_font, pos + off, txt,
			HORIZONTAL_ALIGNMENT_LEFT, -1, 14, Color(halo.r, halo.g, halo.b, 0.85))
	ci.draw_string(ThemeDB.fallback_font, pos, txt,
		HORIZONTAL_ALIGNMENT_LEFT, -1, 14, UITokens.INK_1)

# --- 框选路（右键拖）/ 多选高亮 -----------------------------------------

## 多选高亮与框选矩形的载体（同测量层的手法：z_index 拉高 + draw 信号画，不新增脚本文件）
func _sel_layer() -> Node2D:
	if _sel_node != null and is_instance_valid(_sel_node):
		return _sel_node
	var n := Node2D.new()
	n.name = "SelectLayer"
	n.z_index = 150                  # 压在道路/装饰之上、测量线之下
	n.draw.connect(_draw_sel.bind(n))
	add_child(n)
	_sel_node = n
	return n

func _refresh_sel() -> void:
	_sel_layer().queue_redraw()

## 框选矩形（米）
func _box_rect_m() -> Rect2:
	return Rect2(box_start_m.min(box_end_m), (box_end_m - box_start_m).abs())

## 框选矩形（画布像素；world_to_px 有 Y 翻转，所以按两点重算而不是拿 size 直接用）
func _box_rect_px() -> Rect2:
	var p0 := world_to_px(box_start_m)
	var p1 := world_to_px(box_end_m)
	var tl := Vector2(minf(p0.x, p1.x), minf(p0.y, p1.y))
	var br := Vector2(maxf(p0.x, p1.x), maxf(p0.y, p1.y))
	return Rect2(tl, br - tl)

func _draw_sel(ci: CanvasItem) -> void:
	var doc: Doc = EditorApp.doc
	# 多选（≥2 条）才在这里描：单选已经由 RoadLayer 的橙色高亮负责，两边都画等于同一根线描两遍。
	# ★ 颜色一律走 `SEL_*` 橙系：原来是青色 Color(0.25,0.85,1.0)，在奶油底上像调试线，
	#   而且和"没选中"几乎没差别 —— 老板直接把那些青线叫"蓝色线"（2026-10-28）。
	if doc != null and EditorApp.selection.size() > 1:
		for rid in EditorApp.selection:
			var r: Dictionary = doc.find_road(int(rid))
			if r.is_empty():
				continue
			var a := world_to_px(doc.node_pos(int(r.a_node)))
			var b := world_to_px(doc.node_pos(int(r.b_node)))
			ci.draw_line(a, b, UITokens.SEL_GLOW, 8.0, true)
			ci.draw_line(a, b, UITokens.SEL_RING, 3.0, true)
	# 框选矩形
	if box_selecting:
		var r2 := _box_rect_px()
		ci.draw_rect(r2, Color(UITokens.SEL_RING, 0.10), true)
		ci.draw_rect(r2, Color(UITokens.SEL_RING, 0.85), false, 1.5)

## 松手：算出框里的路，交给 EditorApp 定多选（框太小 = 右键单击，在选中集里加/减一条）
func _commit_box_select(doc: Doc) -> void:
	var rect := _box_rect_m()
	if rect.size.length() < BOX_CLICK_M:
		var rid := doc.pick_road(box_start_m, screen_px_to_m(10.0))
		if rid >= 0:
			EditorApp.toggle_road_in_selection(rid)
		return
	var ids: Array = []
	for r in doc.roads:
		if _road_hits_rect(doc, r, rect):
			ids.append(int(r.road_id))
	EditorApp.set_selection_roads(ids)

## 路与框相交？（端点落在框内，或线段与四条边任一相交 —— "框住"和"穿过"都算选中）
## ★ 只是几何判断，不改 pick_road 的语义（那条是"点到最近的线段"）。
func _road_hits_rect(doc: Doc, road: Dictionary, rect: Rect2) -> bool:
	var a := doc.node_pos(int(road.a_node))
	var b := doc.node_pos(int(road.b_node))
	if rect.has_point(a) or rect.has_point(b):
		return true
	var c := rect.position
	var d := Vector2(rect.end.x, rect.position.y)
	var e := rect.end
	var f := Vector2(rect.position.x, rect.end.y)
	return Geometry2D.segment_intersects_segment(a, b, c, d) != null \
		or Geometry2D.segment_intersects_segment(a, b, d, e) != null \
		or Geometry2D.segment_intersects_segment(a, b, e, f) != null \
		or Geometry2D.segment_intersects_segment(a, b, f, c) != null
