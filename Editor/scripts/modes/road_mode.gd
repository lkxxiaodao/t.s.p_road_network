class_name RoadMode
extends RefCounted

## 道路模式：
##   拖 = 画新路；单击 = 选中/取消选中；
##   双击路上的点 = 插节点拆分；拖端点把手 = 改形；
##   拖弦中点手柄 = 把这条直路弯成弧（N 段 road）。

const MIN_LEN_M := 5.0      ## 拖动短于这个长度 → 当作单击，不画路
const PICK_PX := 10.0       ## 选中路 / 双击命中的点击容差（屏幕像素）
const HANDLE_PX := 12.0     ## 端点把手的抓取半径（屏幕像素）
const BEND_PX := 12.0       ## 弯曲手柄的抓取半径（屏幕像素）
const MIN_BOW_M := 5.0      ## 弯曲量小于此值 → 视为没弯，取消

var dragging := false
var start_m := Vector2.ZERO
var end_m := Vector2.ZERO
var start_node := -1
var end_node := -1
var press_raw_m := Vector2.ZERO

var drag_node_id := -1      ## 正在拖动的已有节点 id（-1 = 没在拖）
var drag_ref_m := Vector2.ZERO    ## Shift 锁角的基准 = 这条路【另一端】节点的位置
var drag_isolated := false        ## 被拖的是否孤立端点（只连这一条路）；共同端点不锁角

var curving := false        ## 正在拉弧线手柄
var curve_a := Vector2.ZERO ## 弦起点（米）
var curve_b := Vector2.ZERO ## 弦终点（米）
var curve_mid := Vector2.ZERO   ## 手柄当前位置（米）
var curve_nrm := Vector2.RIGHT  ## 弦的单位法向（按下时算好）

func handle(event: InputEvent, world_m: Vector2, canvas: GridCanvas, doc: Doc) -> void:
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			press_raw_m = world_m
			if event.double_click:
				var nid := doc.find_node_near(world_m, canvas.screen_px_to_m(HANDLE_PX))
				if nid >= 0:
					EditorApp.toggle_junction_signal(nid)
					return                         # 双击节点 → 普通路口 ↔ 信号路口
			var hit := _pick_handle(world_m, canvas, doc)
			if hit >= 0:
				drag_node_id = hit                 # 抓端点 → 改形
				# ★ Shift 锁角以【这条路另一端】为基准；且只对孤立端点生效（连 ≥2 条路的共同端点不锁）
				var road := doc.find_road(EditorApp.selected_road)
				drag_isolated = false
				if not road.is_empty():
					var other: int = road.b_node if int(road.a_node) == hit else int(road.a_node)
					drag_ref_m = doc.node_pos(other)
					drag_isolated = doc.road_count_at(hit) <= 1
				return
			if event.double_click and _try_split(world_m, canvas, doc):
				return                             # 双击 → 插节点拆分
			if _start_bend(world_m, canvas, doc):
				return                             # 抓弦中点 → 弯弧
			var s := canvas.resolve_point(world_m, doc)
			start_m = s.pos
			start_node = s.node
			end_m = start_m
			end_node = start_node
			dragging = true
		else:
			if drag_node_id >= 0:
				_finish_node_drag(doc)
			elif curving:
				curving = false
				_commit_curve(doc)
			elif dragging:
				dragging = false
				_finish(canvas, doc)
	elif event is InputEventMouseMotion:
		if drag_node_id >= 0:
			# ★ 按住 Shift 且是孤立端点：以【这条路另一端】为基准锁 0°/45°/90°（长度按光标到基准的距离）
			#   共同端点（连 ≥2 条路）不锁 —— 一拖会把相邻那条路也掰歪
			var np := Vector2.ZERO
			if event is InputEventMouseMotion and event.shift_pressed and drag_isolated:
				np = canvas.snap_angle_45(drag_ref_m, world_m)
			else:
				np = canvas.snap_m(world_m)
			doc.move_node(drag_node_id, np)
			_refresh_touching_roads(doc, drag_node_id)
		elif curving:
			curve_mid = world_m                    # 弧线不吸附，保持自由
		elif dragging:
			# ★ 按住 Shift：落点锁到相对起点的 0°/45°/90°（只约束落点，松手即恢复自由；不碰已画的路）
			var lock_angle: bool = event is InputEventMouseMotion and event.shift_pressed
			var e := canvas.resolve_drag_point(start_m, world_m, lock_angle, doc)
			end_m = e.pos
			end_node = e.node

# --- 端点把手 / 拆分 -----------------------------------------------------

## 抓取「选中路」的端点把手；没抓到返回 -1
func _pick_handle(world_m: Vector2, canvas: GridCanvas, doc: Doc) -> int:
	var sel: int = EditorApp.selected_road
	if sel < 0:
		return -1
	var road := doc.find_road(sel)
	if road.is_empty():
		return -1
	var r := canvas.screen_px_to_m(HANDLE_PX)
	if world_m.distance_to(doc.node_pos(road.a_node)) <= r:
		return road.a_node
	if world_m.distance_to(doc.node_pos(road.b_node)) <= r:
		return road.b_node
	return -1

## 双击路上的点 → 插节点拆两段
func _try_split(world_m: Vector2, canvas: GridCanvas, doc: Doc) -> bool:
	var rid := doc.pick_road(world_m, canvas.screen_px_to_m(PICK_PX))
	if rid < 0:
		return false
	var m := doc.split_road(rid, canvas.snap_m(world_m))
	if m < 0:
		return false
	EditorApp.select_road(-1)
	print("插节点 %d：road %d 拆为两段｜节点 %d 条 路 %d 条" % [m, rid, doc.nodes.size(), doc.roads.size()])
	return true

## 节点动了 → 重算所有连着它的 road 的 lane length
func _refresh_touching_roads(doc: Doc, node_id: int) -> void:
	for road in doc.roads:
		if road.a_node == node_id or road.b_node == node_id:
			doc.refresh_road_lengths(road.road_id)

## 松手：若与另一节点重合 → 合并；否则只重算长度
func _finish_node_drag(doc: Doc) -> void:
	var nid := drag_node_id
	drag_node_id = -1
	var other := doc.find_node_near_except(doc.node_pos(nid), nid, Doc.MERGE_EPS_M)
	if other >= 0:
		var dead := doc.merge_nodes(nid, other)
		# ★ 合并把被拖节点的引用全改指到 other：端点坐标换了 → 受影响 road 的 lane.length 必须重算，
		#   否则属性面板按 node_pos 算的长度与 lane.length 对不上（导出后仿真吃的就是旧长度）
		_refresh_touching_roads(doc, other)
		if EditorApp.selected_road >= 0 and doc.find_road(EditorApp.selected_road).is_empty():
			EditorApp.select_road(-1)
		print("合并节点 %d → %d（删自环 %d 条）｜节点 %d 条 路 %d 条" % [
			nid, other, dead.size(), doc.nodes.size(), doc.roads.size()])
	else:
		_refresh_touching_roads(doc, nid)
	doc.dirty = true
# --- 弧线 ---------------------------------------------------------------

## 抓「选中路」的弦中点手柄 → 进入弯弧
func _start_bend(world_m: Vector2, canvas: GridCanvas, doc: Doc) -> bool:
	var sel: int = EditorApp.selected_road
	if sel < 0:
		return false
	var road := doc.find_road(sel)
	if road.is_empty():
		return false
	var a := doc.node_pos(road.a_node)
	var b := doc.node_pos(road.b_node)
	var chord := b - a
	if chord.length() < 0.001:
		return false
	var mid := (a + b) * 0.5
	if world_m.distance_to(mid) > canvas.screen_px_to_m(BEND_PX):
		return false
	curve_a = a
	curve_b = b
	curve_mid = mid
	curve_nrm = Vector2(-chord.normalized().y, chord.normalized().x)
	curving = true
	return true

## 当前弯曲量（米，沿弦法向）：0 = 直线
func curve_bow() -> float:
	var mid := (curve_a + curve_b) * 0.5
	return (curve_mid - mid).dot(curve_nrm)

## 段数：按弯度取 4~8 段
func _seg_count(bow: float) -> int:
	return clampi(4 + int(roundf(absf(bow) / 20.0)), 4, 8)

## 贝塞尔采样（米），共 n+1 个点
func _curve_points_m(n: int) -> Array:
	var mid := (curve_a + curve_b) * 0.5
	var ctrl := mid + (curve_mid - mid) * 2.0     # 让曲线正好过手柄
	var pts: Array = []
	for i in range(n + 1):
		var t := float(i) / float(n)
		var p := (1.0 - t) * (1.0 - t) * curve_a \
			+ 2.0 * (1.0 - t) * t * ctrl \
			+ t * t * curve_b
		pts.append(p)
	return pts

## 预览采样（像素）——高分辨率，供 _draw 画平滑弧
func curve_preview_points() -> PackedVector2Array:
	var out := PackedVector2Array()
	if not curving:
		return out
	var mid := (curve_a + curve_b) * 0.5
	var ctrl := mid + (curve_mid - mid) * 2.0
	var steps := 32
	for i in range(steps + 1):
		var t := float(i) / float(steps)
		var p := (1.0 - t) * (1.0 - t) * curve_a \
			+ 2.0 * (1.0 - t) * t * ctrl \
			+ t * t * curve_b
		out .append(GridCanvas.world_to_px(p))
	return out

## 松手：弯度够大才落成 N 条 road
func _commit_curve(doc: Doc) -> void:
	var sel: int = EditorApp.selected_road
	var road := doc.find_road(sel)
	if road.is_empty():
		return
	var bow := curve_bow()
	if absf(bow) < MIN_BOW_M:
		return                                    # 没怎么弯 → 保持原样
	var n := _seg_count(bow)
	var new_ids := doc.replace_road_with_chain(sel, _curve_points_m(n), road.template)
	print("弧线：road %d → %d 段｜节点 %d 条 路 %d 条" % [sel, new_ids.size(), doc.nodes.size(), doc.roads.size()])
	# ★ 弧段的中间节点每个都是"新路口"，但一条 turn 都没有 → 车过不去，界面原来毫无提示。
	#   这里只提示（Output），不擅自生成连接；连不连由用户在连接模式里逐个决定。
	if not new_ids.is_empty() and n > 1:
		print("弧线：新增 %d 个路口待连接（切到连接模式点选节点后按 G 生成，否则车流会断在弧上）" % (n - 1))
	EditorApp.select_road(-1)

# --- 建新路 -------------------------------------------------------------

func _finish(canvas: GridCanvas, doc: Doc) -> void:
	if start_m.distance_to(end_m) < MIN_LEN_M:
		EditorApp.select_road(doc.pick_road(press_raw_m, canvas.screen_px_to_m(PICK_PX)))
		return
	var a: int = start_node if start_node >= 0 else doc.node_at_or_new(start_m)
	var b: int = end_node if end_node >= 0 else doc.node_at_or_new(end_m)
	if a == b:
		return
	# ★ 新建路优先用"面板里点过的模板"，没点过就用 Doc 的默认
	var tmpl: Dictionary = Doc.default_template()
	if not EditorApp.new_road_template.is_empty():
		tmpl = EditorApp.new_road_template.duplicate(true)
	var rid := doc.create_road(a, b, tmpl)
	EditorApp.select_road(rid)
	print("建路 %d（node %d → %d）｜节点 %d 条 车道 %d 条" % [rid, a, b, doc.nodes.size(), doc.lanes.size()])
	start_node = -1
	end_node = -1
