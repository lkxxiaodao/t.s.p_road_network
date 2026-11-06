extends Node2D
class_name RoadLayer

## 把 Doc 里的车道画出来：不存任何图元，全部由 lanes 反推。
## 位置一律经 GridCanvas.world_to_px 换算（那里统一处理 Y+ = 北 的 y 取反）。
## 路口处把路面两端各"退回"一段（_lane_node_inset_m），再把各路端面的角点连成凸多边形补面，避免所有线糊在一起。

## 进口箭头：一个"进口"只画一组，放在【最外侧车道外面】，从路口回退一段。
## 尺寸按屏幕像素折算（画布像素 = 屏幕像素 × k），k 见 _arrow_px_scale()。
## ★ 箭头是纯绘制：只读 Doc.turns / Doc.lanes，不写 Doc、不进 undo。
## ★ 2026-10-23 调整：① 由"逐车道贴停止线"改为"每进口一组、画在车道外"② 整体放大、拉开间距
##   ③ 加掉头 ④ 去文字（改靠形状区分）⑤ 尺寸补偿由"完全恒定"改为"软补偿"（有上下限）。
## ★ P1-3 起：这个常量只服务**矢量降级路径**（素材缺失时才用）。PNG 正常时根本不读它。
##   PNG 自带纯白，不需要 modulate，也不用再为"底色深浅"调色。
##   （历史：深底时代是亮金 `#FFCC38`，P1-2 临时改暖白，现在退居 fallback。）
## ★ 2026-10-26：一条车道**只画一个箭头**（组合图），所以"同组间距 / 同组排序"那几个常量全部作废：
##   ARROW_LEN_PX / ARROW_HALF_W_PX / ARROW_PITCH_MUL / ARROW_GAP_PX / ARROW_SPRITE_GAP_PX 已删。
##   尺寸交给 `ArrowAssets.fit()`（按实测长宽比 + 车道宽封顶）。
##   **纵向位置不再用屏幕像素**（原来的 ARROW_BACKOFF_PX 已删）——改由停止线推出来，
##   见 `RoadMarks.arrow_tip_offset_m()` / `ARROW_TO_STOP_GAP_M`。
const ARROW_COLOR := Color("#F7F3EC")
const ARROW_ZOOM_SOFT_LO := 0.5   ## 缩到比这更远，箭头才开始跟着变小（下限制约）
const ARROW_ZOOM_SOFT_HI := 3.0   ## 放到比这更大，箭头才开始跟着变大（上限制约）
const ARROW_KIND_ORDER := ["Straight", "Left", "Right", "UTurn"]   ## 组合顺序：直→左→右→掉头（固定）
## ★ 专业配时矩阵的"当前行"高亮色。刻意用蓝：橙（SEL_RING）已经是"选中"，
##   两种高亮必须能同时出现且分得开（技术方案06 §6.3）。
const SUMO_LINK_HILITE := Color("#3B7DD8")
## 箭头横向基准的【降级链】（2026-10-23 老板定）：
##   有人行道 → 人行道外缘；没有 → 非机动车道；再没有 → 公交专用道；都没有 → 机动车道。
## ★ 显式写出，不靠 Doc.lane_lat_rank 的隐式递增顺序：那个顺序一旦将来被调整，
##   箭头基准会静默跟着漂，且不报任何错（本项目最怕这种漂移）。

# --- 节点 → 关联对象 索引（缓存）----------------------------------------
## ★ 为什么要它：_draw 里 _junction_inset_m 每条 lane 调两次、_junction_corners 每个节点扫全部 lanes、
##   _draw_junction_fills 每个节点还要 road_count_at 一次 → 合起来 O(节点×车道)/帧，100 个路口就掉帧。
## ★ 失效判据 = 结构签名（lanes/roads/nodes 数量 + next_lane_id + next_road_id）+
##   坐标签名（Σx,Σy,Σx·i,Σy·i 两次矩）：
##   create_road / delete_road / split_road / rebuild_road / merge_nodes 必然改到结构那五项之一
##   （rebuild 用新 lane id 顶掉旧的 → next_lane_id 必涨；merge 会少一个节点；delete 会少车道）；
##   move_node 改的是坐标 → 由坐标签名捕到（只重建坐标表，不动车道索引）。
##   六个入口由此全覆盖，没有一处要记得"手工通知缓存失效"。
## ★ 文档对象换过（导入 / 撤销 / 重做都换 Doc）也走同一条重建路径。
var _idx_doc: Doc = null
var _idx_sig: String = ""
var _idx_pos_sig: String = ""
var _idx_lanes: Dictionary = {}       ## node_id → Array[lane]（车道字典本身，不复制）
var _idx_roads: Dictionary = {}       ## node_id → {road_id: road}（按 road_id 去重；壳缺失时值是 {}）
var _idx_road_by_id: Dictionary = {}  ## road_id → road
var _idx_lane_by_id: Dictionary = {}  ## lane_id → lane（_draw_turns / 进口箭头每帧按 id 取车道，Doc.find_lane 是线性的）
var _idx_pos: Dictionary = {}         ## node_id → Vector2（世界米；与 Doc.node_pos 同语义）
var _idx_half: Dictionary = {}        ## road_id → 该路半宽（米）
var _idx_inset: Dictionary = {}       ## node_id → 路口统一退让量（米）
var _idx_hull: Dictionary = {}        ## node_id → 路口补面的凸包（像素；只作废受影响的那几个，见 _sync_index）
var _redraw_sig: String = ""          ## 上一帧"画面状态"签名（C3 脏标记用；与 Doc 内容无关）

## 结构签名（O(1)）：对象数量 + 两个自增计数器。访问器每帧上百次调用都只付这点开销。
##   create_road / delete_road / split_road / rebuild_road / merge_nodes 必改其中之一
##   （rebuild 用新 lane id 顶掉旧的 → next_lane_id 必涨；merge 少一个节点；delete 少车道）。
static func _struct_sig(doc: Doc) -> String:
	# ★ 带上 turns 的条数与计数器（都是 O(1)）：进口箭头按"每条车道有哪些转向"画，
	#   按 G 生成 / 删掉一条转向必须立刻重画，而 lanes/roads 的数量这时可能一点没变。
	#   代价是按 G 那一下会重建一次索引（几 ms），相对 SimCore 的几百 ms 可忽略。
	# ★ 2026-10-25（N6b）再加 islands 的数量与计数器：拆 / 并二次过街只动这两项。
	return "%d/%d/%d/%d/%d/%d/%d/%d/%d" % [doc.lanes.size(), doc.roads.size(), doc.nodes.size(),
		doc.next_lane_id, doc.next_road_id, doc.turns.size(), doc.next_turn_id,
		doc.islands.size(), doc.next_island_id]

## 坐标签名（O(节点)）：Σx, Σy, Σx·i, Σy·i 两次矩 —— 只要有一个节点被拖动就变。
## ★ 它只在 _draw() / pick_* 的入口算一次（_sync_index），不进访问器 —— 否则就变成 O(车道×节点)。
static func _pos_sig(doc: Doc) -> String:
	var sx := 0.0
	var sy := 0.0
	var wx := 0.0
	var wy := 0.0
	for i in doc.nodes.size():
		var n: Dictionary = doc.nodes[i]
		var x := float(n.x)
		var y := float(n.y)
		sx += x
		sy += y
		wx += x * float(i + 1)
		wy += y * float(i + 1)
	# ★ 安全岛的 x/y/capacity 也是"坐标"（N6b）：在面板里改容量/挪位置必须立刻重画。
	#   放进 _pos_sig 而不是 _struct_sig —— 改值不该触发索引重建。
	# ★ 容量必须进【求和】项，不能只靠 x·cap 的矩项：岛常在原点（x=y=0），矩项恒为 0。
	for isl in doc.islands:
		var ix := float(isl.get("x", 0.0))
		var iy := float(isl.get("y", 0.0))
		var ic := float(isl.get("capacity", 0))
		sx += ix
		sy += iy
		wx += ix * (ic + 1.0) + ic          # +ic 保证原点上的岛改容量也能被看见
		wy += iy * (ic + 1.0)
	return "%.3f/%.3f/%.3f/%.3f" % [sx, sy, wx, wy]

## 每帧（_process）/ 每次点选入口调一次：结构变了重建全部，只是节点动了只重建坐标表。
## 返回 true = 索引变了（结构或坐标）——调用方据此决定要不要重画。
func _sync_index(doc: Doc) -> bool:
	var rebuilt := _ensure_index(doc)
	var ps := _pos_sig(doc)
	if ps == _idx_pos_sig:
		return rebuilt
	_idx_pos_sig = ps
	# ★ 只让"真的动过的节点 + 它的邻居"的补面作废（D3）：拖一个节点时其余路口的凸包照旧复用。
	#   补面凸包只跟本节点及其相邻节点的坐标有关（角点 = 车道端面，端面方向看两端节点）。
	var moved: Array = []
	var new_pos := {}
	for n in doc.nodes:
		var nid := int(n.id)
		var p := Vector2(float(n.x), float(n.y))
		new_pos[nid] = p
		if _idx_pos.get(nid, Vector2.INF) != p:
			moved.append(nid)
	_idx_pos = new_pos
	for nid in moved:
		_idx_hull.erase(nid)
		var rs: Dictionary = _idx_roads.get(nid, {})
		for rid in rs:
			var r: Dictionary = rs[rid]
			if r.is_empty():
				continue
			_idx_hull.erase(int(r.a_node))
			_idx_hull.erase(int(r.b_node))
	return true

## 确保索引对得上"当前这份 Doc"（文档换过 / 结构变了 → 重建；O(1) 快路径）
## 返回 true = 刚重建过（调用方要重画）
func _ensure_index(doc: Doc) -> bool:
	var sig := _struct_sig(doc)
	if doc == _idx_doc and sig == _idx_sig:
		return false
	_idx_doc = doc
	_idx_sig = sig
	_idx_pos_sig = _pos_sig(doc)
	_idx_lanes = {}
	_idx_roads = {}
	_idx_road_by_id = {}
	_idx_lane_by_id = {}
	_idx_pos = {}
	_idx_half = {}
	_idx_inset = {}
	_idx_hull = {}
	for n in doc.nodes:
		_idx_pos[int(n.id)] = Vector2(float(n.x), float(n.y))
	for r in doc.roads:
		_idx_road_by_id[int(r.road_id)] = r
	# 一遍扫完 lanes：同时建"节点 → 车道"、"车道 id → 车道"与"路 → 半宽"
	for lane in doc.lanes:
		_idx_lane_by_id[int(lane.id)] = lane
		var rid := int(lane.get("road", -1))
		var road: Dictionary = _idx_road_by_id.get(rid, {})
		if not road.is_empty():
			var h := absf(_lane_offset(lane, road)) + Doc.LANE_WIDTH_M * 0.5
			_idx_half[rid] = maxf(float(_idx_half.get(rid, 0.0)), h)
		for nid in [int(lane.from_node), int(lane.to_node)]:
			var ls: Array = _idx_lanes.get(nid, [])
			ls.append(lane)
			_idx_lanes[nid] = ls
			var rs: Dictionary = _idx_roads.get(nid, {})
			rs[rid] = road                    # 字典天然按 road_id 去重；壳缺失时值是 {}
			_idx_roads[nid] = rs
	# 节点退让量：连 ≥2 条路才退，取其中最宽那条的半宽（与改前的 _junction_inset_m 同口径）
	for n in doc.nodes:
		var nid := int(n.id)
		var rs: Dictionary = _idx_roads.get(nid, {})
		if rs.size() < 2:
			_idx_inset[nid] = 0.0
			continue
		var half := 0.0
		for rid in rs:
			if (rs[rid] as Dictionary).is_empty():
				continue
			half = maxf(half, float(_idx_half.get(int(rid), 0.0)))
		_idx_inset[nid] = half
	return true

## 该节点连了几条路（按 road 去重）—— 与 Doc.road_count_at 同口径，但走缓存 O(1)
func _junction_degree(doc: Doc, node_id: int) -> int:
	_ensure_index(doc)
	return (_idx_roads.get(node_id, {}) as Dictionary).size()

## 按 id 取 road（走缓存；找不到返回 {}，与 Doc.find_road 同语义）
func _road_of(doc: Doc, road_id: int) -> Dictionary:
	_ensure_index(doc)
	return _idx_road_by_id.get(road_id, {})

## 按 id 取节点坐标（走缓存；找不到返回 ZERO，与 Doc.node_pos 同语义）
func _node_pos(doc: Doc, node_id: int) -> Vector2:
	_ensure_index(doc)
	return _idx_pos.get(node_id, Vector2.ZERO)

## 按 id 取车道（走缓存；找不到返回 {}，与 Doc.find_lane 同语义）
## ★ 为什么要它：Doc.find_lane 是线性查找，而 _draw_turns 每条 turn 要查两条、
##   _collect_approaches 每条 turn 查一条 → 旧版是 O(turn × lane)/帧（E91 C1）
func _lane_of(doc: Doc, lane_id: int) -> Dictionary:
	_ensure_index(doc)
	return _idx_lane_by_id.get(lane_id, {})

## ★ C3 脏标记：旧版每帧无条件 queue_redraw()，稳态也在跑全量几何（100 路口约 14 ms/帧）。
##   现在只在"索引变了（结构/坐标）"或"画面状态变了（选中/模式/图层/冲突/拖拽预览）"时重画。
func _process(_delta: float) -> void:
	var doc: Doc = EditorApp.doc
	if doc == null:
		return
	if _sync_index(doc):
		_redraw_sig = _view_sig()
		queue_redraw()
		return
	var s := _view_sig()
	if s == _redraw_sig:
		return
	_redraw_sig = s
	queue_redraw()

## "画面状态"签名：选中项 / 模式 / 图层开关 / 冲突集合 / 撤销无关的拖拽预览。
## ★ 故意不含 Doc 内容 —— 那部分由 _sync_index 的结构签名 + 坐标签名负责，别重复。
func _view_sig() -> String:
	var rm: RoadMode = EditorApp.road_mode
	var drag_nid: int = rm.drag_node_id if rm != null else -1
	var s := "%d|%d|%d|%d|%d|%d|%d%d%d|%d|%d|%d|%d|%s|%d" % [
		EditorApp.selected_road, EditorApp.selected_turn, EditorApp.selected_junction,
		EditorApp.selected_deco, EditorApp.mode, EditorApp.pending_src_lane,
		int(EditorApp.show_lanes), int(EditorApp.show_turns),
		int(EditorApp.show_annotations),
		drag_nid, EditorApp.selected_island, EditorApp.sumo_highlight_turn,
		EditorApp.sumo_tint_rev,
		# ★ 设施图标：选中态 + 专业档 revision 都要进签名。
		#   用 revision（而不是 additional.size()）—— 改 pos / 长度 / 线路名同样要重绘。
		EditorApp.selected_facility,
		EditorApp.profile.revision]
	if rm == null:
		return s
	if rm.curving:
		s += "|c%.3f/%.3f" % [rm.curve_mid.x, rm.curve_mid.y]
	if rm.dragging:
		s += "|d%.3f/%.3f/%.3f/%.3f/%d/%d" % [rm.start_m.x, rm.start_m.y,
			rm.end_m.x, rm.end_m.y, rm.start_node, rm.end_node]
	return s

func _draw() -> void:
	var doc: Doc = EditorApp.doc
	if doc == null:
		return
	_sync_index(doc)          # ★ 幂等兜底：点选/重画入口都可能先进来，这里再对齐一次（O(1) 快路径）

	# ★ 图层开关（B19）：车道 / 连接 / 标注 三层各自独立；判断只加在这里，子函数一个字没改。
	if EditorApp.show_lanes:
		_draw_junction_fills(doc)
		_draw_lanes(doc)
		_draw_lane_dividers(doc)   # ★ 同向机动车道之间的白虚线（≥2 条车道才有）
		_draw_edge_lines(doc)      # ★ 车行道边缘线（机动车道组外缘，白实线）
		_draw_center_lines(doc)    # ★ P1-3：双向路的中心双黄线
		_draw_stop_lines(doc)      # ★ P1-3：国标停止线（路面之上、连接曲线之下）
		_draw_crosswalks(doc)      # ★ P1-3：人行横道条纹（同样归路面标线层）
		_draw_islands(doc)         # ★ 安全岛归「车道」层：它本来就是路面的一部分
		_draw_lane_hints(doc)      # ★ C5：进口/出口提示画的是车道，归「车道」层管（以前漏在外面）
	if EditorApp.show_turns:
		_draw_turns(doc)
	if EditorApp.show_annotations:
		_draw_lane_arrows(doc)
	_draw_facilities(doc)      # ★ S3 检测设施：画在最上层（小图标，压在标线上才看得见）

	var rm: RoadMode = EditorApp.road_mode
	var sel: int = EditorApp.selected_road

	# 选中高亮：浅橙光晕 + 橙中线 + 端点把手（R0 §5.1「选中描边」/§5.2「手柄」）
	if sel >= 0:
		var sr := _road_of(doc, sel)
		if not sr.is_empty():
			var pa := GridCanvas.world_to_px(_node_pos(doc, int(sr.a_node)))
			var pb := GridCanvas.world_to_px(_node_pos(doc, int(sr.b_node)))
			draw_line(pa, pb, UITokens.SEL_GLOW, 7.0, true)
			draw_line(pa, pb, UITokens.SEL_RING, 3.0, true)

			# 端点把手（拖动新路时不显示）
			if not rm.dragging:
				for nid in [sr.a_node, sr.b_node]:
					var hp := GridCanvas.world_to_px(_node_pos(doc, int(nid)))
					if nid == rm.drag_node_id:
						draw_circle(hp, 12.0, UITokens.SEL_GLOW)   # 正在拖：光晕 + 橙实心
						draw_circle(hp, 8.0, UITokens.SEL_RING)
					else:
						draw_circle(hp, 7.0, UITokens.NODE_DOT)    # 可抓：白圆 + 橙描边
						draw_arc(hp, 7.0, 0.0, TAU, 24, UITokens.SEL_RING, 2.0, true)
				# 弦中点手柄：拖它 = 弯成弧（双击它 = 插节点）
				draw_circle((pa + pb) * 0.5, 6.0, UITokens.SNAP_FREE)

	# 弯弧中的实时预览
	if rm.curving:
		var cpts := rm.curve_preview_points()
		if cpts.size() >= 2:
			draw_polyline(cpts, Color(UITokens.SEL_RING, 0.9), 3.0, true)
		draw_circle(GridCanvas.world_to_px(rm.curve_mid), 7.0, UITokens.SNAP_FREE)

	# 正在拖拽的预览线（吸到已有节点时画绿色高亮环）
	if rm.dragging:
		var p0 := GridCanvas.world_to_px(rm.start_m)
		var p1 := GridCanvas.world_to_px(rm.end_m)
		draw_line(p0, p1, Color(UITokens.SEL_RING, 0.75), 3.0, true)
		_draw_snap_dot(p0, rm.start_node)
		_draw_snap_dot(p1, rm.end_node)

	# 节点（★ 标注层：与进口箭头同一个开关）
	# ★ 奶油底上"白圆"会直接消失 —— 必须配深色描边，白心才立得住
	# ★ 被选中的路口跳过不画（2026-10-28）：它的中心要让给"点连接线"，白点压在那儿只会挡视线。
	#   路口选中态由 `_draw_lane_hints()` 的进口（橙）/出口（蓝）短提示表达，画布上不给路口画任何标记。
	if EditorApp.show_annotations:
		for n in doc.nodes:
			if int(n.id) == EditorApp.selected_junction:
				continue
			var np := GridCanvas.world_to_px(Vector2(n.x, n.y))
			draw_circle(np, 5.5, UITokens.NODE_DOT)
			draw_arc(np, 5.5, 0.0, TAU, 20, UITokens.NODE_EDGE, 1.5, true)

	# ★ 选中的路口**不画任何标记**（2026-10-28 老板定案，第二次收紧）：
	#   路口中心那圈橙环（哪怕再细）也会压在连接线的汇聚处，看着碍眼。
	#   现在"选中了哪个路口"完全靠 `_draw_lane_hints()` 的进口（橙）/出口（蓝）短提示——
	#   看到某条路两侧有橙+蓝短线，就知道是它，而且线条本身一根都没被挡住。

# --- 中央安全岛（E92 G2 · N6b）------------------------------------------

## 行人二次过街的安全岛：一个带描边的圆角块 + 容量数字，选中时加一圈青色。
## ★ 尺寸按"能站 capacity 个人"折：每人 0.5 m² 的量级 → 半径 = clamp(0.35·√cap, 0.8, 3.0) 米。
##   这只是给眼睛看的近似（真实通行能力由 SimCore 按 capacity 硬判），所以不做精确排布。
func _draw_islands(doc: Doc) -> void:
	if doc.islands.is_empty():
		return
	var sel: int = EditorApp.selected_island
	for isl in doc.islands:
		var iid := int(isl.get("id", -1))
		var c := GridCanvas.world_to_px(Vector2(float(isl.get("x", 0.0)), float(isl.get("y", 0.0))))
		var cap := maxi(1, int(isl.get("capacity", 1)))
		var r_m := clampf(0.35 * sqrt(float(cap)), 0.8, 3.0)
		var r := r_m * GridCanvas.PPM
		var pts := _ellipse_px(c, r, r, 20)
		draw_colored_polygon(pts, UITokens.ROAD_ISLAND)                   # 岛面：浅于路面一档（R2 §2.3）
		draw_polyline(_close_ring(pts), UITokens.ROAD_ISLAND_EDGE, 2.0, true)
		if iid == sel:
			draw_polyline(_close_ring(pts), UITokens.SEL_GLOW, 6.0, true)
			draw_polyline(_close_ring(pts), UITokens.SEL_RING, 3.0, true)
		# 容量数字（只在放得下时画，免得缩小时糊成一团）
		if r >= 9.0:
			var f := ThemeDB.fallback_font
			var txt := str(cap)
			var fs := 11
			var sz := f.get_string_size(txt, HORIZONTAL_ALIGNMENT_CENTER, -1, fs)
			draw_string(f, c - Vector2(sz.x * 0.5, -sz.y * 0.32), txt,
				HORIZONTAL_ALIGNMENT_LEFT, -1, fs, UITokens.INK_1)

## 椭圆采样点（像素）
func _ellipse_px(c: Vector2, rx: float, ry: float, n: int) -> PackedVector2Array:
	var out := PackedVector2Array()
	for i in n:
		var a := TAU * float(i) / float(n)
		out.append(c + Vector2(cos(a) * rx, sin(a) * ry))
	return out

## 把首点补到末尾，供 draw_polyline 画闭合轮廓
func _close_ring(pts: PackedVector2Array) -> PackedVector2Array:
	var out := pts.duplicate()
	if out.size() > 0:
		out.append(out[0])
	return out

## 点选安全岛：返回离 world_m 最近、且在 tol_m 内的岛 id；没有返回 -1
func pick_island(world_m: Vector2, tol_m: float) -> int:
	var doc: Doc = EditorApp.doc
	if doc == null:
		return -1
	var m := GridCanvas.world_to_px(world_m)
	var best := -1
	var best_d := tol_m * GridCanvas.PPM
	for isl in doc.islands:
		var c := GridCanvas.world_to_px(Vector2(float(isl.get("x", 0.0)), float(isl.get("y", 0.0))))
		var cap := maxi(1, int(isl.get("capacity", 1)))
		var r := clampf(0.35 * sqrt(float(cap)), 0.8, 3.0) * GridCanvas.PPM
		var d := m.distance_to(c) - r          # 点在岛面内 → d < 0
		if d <= best_d:
			best_d = d
			best = int(isl.get("id", -1))
	return best

# --- 路面 ---------------------------------------------------------------

func _draw_lanes(doc: Doc) -> void:
	var w := Doc.LANE_WIDTH_M * GridCanvas.PPM
	var edge_w := 2.0        ## 描边总宽增量（画布像素）= 每条边 1 px
	for lane in doc.lanes:
		if _road_of(doc, int(lane.road)).is_empty():
			continue
		var seg := _lane_seg_px(doc, lane)
		if seg.size() < 2:
			continue
		# ★ 先画宽的深色打底、再压填充 —— 每条车道得到一圈 1 px 的边。
		#   浅底上"路面色 vs 画布色"只有 1.4~1.8:1，光靠填充分不出相邻车道；
		#   这圈边就是临时的"车道分界线"，P1-3 会换成国标的白虚线/黄实线。
		draw_line(seg[0], seg[1], UITokens.ROAD_EDGE, w + edge_w, true)
		draw_line(seg[0], seg[1], _lane_color(lane.type), w, true)

## 机动车可通行类型 —— 与 SimCore 的进口口径一致（人行道/非机动车道不算）
func _is_motor(ty: String) -> bool:
	return ty == "Driving" or ty == "Bus" or ty == "SharedLeftTurn"

## 同向车行道分界线（GB 5768.3 §4.2）：**相邻两条机动车道之间**画白虚线（4 m 实 / 8 m 空）。
## ★ 注意：**一个方向只有 1 条机动车道时，本来就没有虚线可画** ——
##   车行道与人行道/非机动车道之间是「车行道边缘线」（白**实**线，见 `_draw_edge_lines`），不是虚线。
##   所以要看到虚线，得先给某个方向加到 ≥2 条机动车道（模板字段 `lanes_fwd/rev`）。
func _draw_lane_dividers(doc: Doc) -> void:
	var zoom := _zoom()
	var groups := {}
	for lane in doc.lanes:
		if not _is_motor(String(lane.type)):
			continue
		var key := "%d|%d" % [int(lane.road), int(lane.from_node)]
		if not groups.has(key):
			groups[key] = []
		(groups[key] as Array).append(lane)
	for key in groups:
		var lanes: Array = groups[key]
		if lanes.size() < 2:
			continue                               # 单向只有一条机动车道 → 无分界线
		var road := _road_of(doc, int(String(key).split("|")[0]))
		if road.is_empty():
			continue
		lanes.sort_custom(func(a, b): return Doc.lane_lat_rank(a, road) < Doc.lane_lat_rank(b, road))
		for i in lanes.size() - 1:
			var l0: Dictionary = lanes[i]
			var l1: Dictionary = lanes[i + 1]
			var seg := _lane_axis_px(doc, l0)      # ★ 中心线（不含横向偏移）
			if seg.size() < 2:
				continue
			# 两条车道**中心线**的中点 = 它们的公共边界（只叠这一次偏移）
			var mid := (_lane_lateral_px(doc, l0) + _lane_lateral_px(doc, l1)) * 0.5
			# ★ 整体向道路中心线内收固定像素：所有分界线收同一个量 → 相互间距仍是 1 个车道宽
			var inward := mid.normalized() * -RoadMarks.MARK_PULL_IN_PX
			RoadMarks.dashed(self, seg[0] + mid + inward, seg[1] + mid + inward,
				RoadMarks.W_DIVIDER, RoadMarks.C_WHITE, zoom)

## 车行道边缘线（GB 5768.3 §4.2）：白实线 0.2 m，画在**机动车道组的外缘**。
## ★ 不是"整条路的最外缘"：外侧通常还有非机动车道/人行道，边缘线的语义是"车行道到此为止"，
##   所以取该方向上**最外侧那条机动车道**的外边（中心线 + 半个车道宽）。
func _draw_edge_lines(doc: Doc) -> void:
	var zoom := _zoom()
	var groups := {}
	for lane in doc.lanes:
		if not _is_motor(String(lane.type)):
			continue
		var key := "%d|%d" % [int(lane.road), int(lane.from_node)]
		if not groups.has(key):
			groups[key] = []
		(groups[key] as Array).append(lane)
	var half := Doc.LANE_WIDTH_M * 0.5 * GridCanvas.PPM
	for key in groups:
		var lanes: Array = groups[key]
		var road := _road_of(doc, int(String(key).split("|")[0]))
		if road.is_empty():
			continue
		var outer: Dictionary = {}
		var best := -1.0
		for lane in lanes:
			var r := Doc.lane_lat_rank(lane, road)
			if r > best:
				best = r
				outer = lane
		if outer.is_empty():
			continue
		var off := _lane_lateral_px(doc, outer)
		if off.length() < 0.001:
			continue
		var seg := _lane_axis_px(doc, outer)      # ★ 中心线（不含横向偏移）
		if seg.size() < 2:
			continue
		# 中心 + 半个车道宽 = 该车道的外缘（只叠这一次偏移），再整体向中心线内收固定像素
		var edge := off + off.normalized() * maxf(half - RoadMarks.MARK_PULL_IN_PX, 0.0)
		RoadMarks.line(self, seg[0] + edge, seg[1] + edge,
			RoadMarks.W_EDGE, RoadMarks.C_WHITE, zoom)

# --- 进口车道箭头 -------------------------------------------------------

## 进口导向箭头：**每条进口机动车道一组**，画在该车道中心线上、贴近停止线。
## 位置 = 该车道中心线在停止线处，沿行驶反方向回退一点。
## 类型 = **这条车道自己**实际有的 turn（直/左/右/掉头）—— 所以加减车道、删某条转向，箭头都跟着变。
## 只做"画"：不写 Doc、不进 undo。
func _draw_lane_arrows(doc: Doc) -> void:
	if EditorApp.mode == EditorMode.Kind.DECO:
		return                                  # 装饰模式是"铺贴图"视角，箭头会挡住底下的素材
	var k := _arrow_px_scale()                  # 屏幕像素 → 画布像素
	if k <= 0.0:
		return
	var approaches := _collect_approaches(doc)
	for key in approaches:
		_draw_approach_arrows(doc, approaches[key], k)

## 屏幕像素 → 画布像素。
## ★ 完全恒定（1/zoom）两端都难看：缩到 25% 箭头细成一条、放到 400% 又小得离谱。
##   这里用"软补偿"：zoom 在 [SOFT_LO, SOFT_HI] 内屏幕尺寸严格恒定；越出该区间才让箭头跟着缩放。
##   于是屏幕尺寸被夹在 1/4 倍 ~ 2.7 倍之间（zoom 0.01~8 全域），既自然又不会失控。
func _arrow_px_scale() -> float:
	var cam := get_viewport().get_camera_2d()
	var z := cam.zoom.x if cam != null else 1.0
	if z <= 0.0001:
		return 0.0
	return 1.0 / clampf(z, ARROW_ZOOM_SOFT_LO, ARROW_ZOOM_SOFT_HI)

## 当前相机缩放（标线的"最小屏幕宽度"判据要用它，不能拿箭头那套软补偿）
func _zoom() -> float:
	var cam := get_viewport().get_camera_2d()
	if cam == null:
		return 1.0
	return maxf(cam.zoom.x, 0.0001)

# --- 国标标线（P1-3）-----------------------------------------------------

## 中心双黄线（GB 5768.3 §4.2「禁止跨越对向车行道分界线」）。
## 只画在**双向路**上（两侧都有车道）；单向路不画 —— 否则会出现一条"假的中心线"。
## 位置就用道路的几何中心（a_node→b_node），车道在两侧对称排开，所以它天然落在中间。
func _draw_center_lines(doc: Doc) -> void:
	var zoom := _zoom()
	var by_road := {}
	for lane in doc.lanes:
		var rid := int(lane.road)
		if not by_road.has(rid):
			by_road[rid] = {}
		var sides: Dictionary = by_road[rid]
		sides[int(lane.from_node)] = true          # 记录有哪些"起点端"→ 用来判断双向
	for road in doc.roads:
		var rid := int(road.road_id)
		var sides: Dictionary = by_road.get(rid, {})
		if sides.size() < 2:
			continue                               # 车道全从同一端出发 = 单向路
		var pa := GridCanvas.world_to_px(_node_pos(doc, int(road.a_node)))
		var pb := GridCanvas.world_to_px(_node_pos(doc, int(road.b_node)))
		var seg := pb - pa
		var seg_len := seg.length()
		# ★ 中心线 = **道路实际绘制长度**，每端按该路口自己的退让量截断（2026-10-26 改）。
		#   退让量就是"车道画到哪儿为止"（`_junction_inset_m`，由该路口最宽那条路的半宽算出），
		#   所以**加车道 → 路口变大 → 退让量变大 → 黄线自动缩短**，永远不会插进路口里。
		#   （旧写法把"半个路口"硬编码成 `LANE_WIDTH_M × 1.5` ≈ 5.25 m，只在"每向 3 车道"时才对，加车道就穿帮。）
		var trim_a := RoadMarks.center_trim_m(_junction_inset_m(doc, int(road.a_node))) * GridCanvas.PPM
		var trim_b := RoadMarks.center_trim_m(_junction_inset_m(doc, int(road.b_node))) * GridCanvas.PPM
		if seg_len <= trim_a + trim_b:
			continue                               # 太短（比两端退让加起来还短）→ 不画
		var dir := seg / seg_len
		RoadMarks.double_yellow(self, pa + dir * trim_a, pb - dir * trim_b, zoom)

## 停止线：每个进口一条白实线，宽 0.4 m，横跨该进口所有机动车道（GB 5768.3 §4.2）。
## 数据源不用 `_collect_approaches` —— 那条只在"配了转向"的进口才产出条目，
## 而停止线对**所有进口道**都该存在（哪怕还没连转向）。
func _draw_stop_lines(doc: Doc) -> void:
	var zoom := _zoom()
	_ensure_index(doc)
	for n in doc.nodes:
		var nid := int(n.id)
		var lanes: Array = _idx_lanes.get(nid, [])
		var by_road := {}
		for lane in lanes:
			if int(lane.to_node) != nid:
				continue
			var ty := String(lane.type)
			if ty != "Driving" and ty != "Bus" and ty != "SharedLeftTurn":
				continue                                   # 人行道/非机动车道不设停止线
			var rid := int(lane.road)
			if not by_road.has(rid):
				by_road[rid] = []
			(by_road[rid] as Array).append(lane)
		for rid in by_road:
			_draw_one_stop_line(doc, by_road[rid], zoom)

func _draw_one_stop_line(doc: Doc, lanes: Array, zoom: float) -> void:
	if lanes.is_empty():
		return
	var first: Dictionary = lanes[0]
	var fwd := GridCanvas.world_to_px(_lane_dir_m(doc, first))
	if fwd.length() < 0.001:
		return
	fwd = fwd.normalized()
	var nrm := Vector2(-fwd.y, fwd.x)
	var half_lane := Doc.LANE_WIDTH_M * 0.5 * GridCanvas.PPM

	var c0 := _lane_end_px(doc, first, true, _lane_node_inset_m(doc, first, int(first.to_node))) \
		+ _lane_lateral_px(doc, first)
	var t0 := c0.dot(nrm)
	var lo := 0.0
	var hi := 0.0
	var init := false
	for lane in lanes:
		var c := _lane_end_px(doc, lane, true, _lane_node_inset_m(doc, lane, int(lane.to_node))) \
			+ _lane_lateral_px(doc, lane)
		var t := c.dot(nrm) - t0
		if not init:
			lo = t - half_lane
			hi = t + half_lane
			init = true
		else:
			lo = minf(lo, t - half_lane)
			hi = maxf(hi, t + half_lane)
	if not init:
		return
	# ★ 往路口外退 `RoadMarks.STOP_SETBACK_M`：人行横道贴路口外侧占 5.25~9.25 m，
	#   停止线要落在它**外面**才有意义（国标：停止线设在横道前 1~2 m）。
	#   ★ 这个数在 `RoadMarks` 可调区，调小到 4.0 以下会埋进条纹里。
	var center := c0 + nrm * ((lo + hi) * 0.5) \
		- fwd * (RoadMarks.stop_center_offset_m() * GridCanvas.PPM)
	RoadMarks.stop_line(self, center, nrm, (hi - lo) * 0.5, zoom)

## 人行横道条纹（GB 5768.3 §4.9）：条纹**平行于车流方向**，宽 0.45 m、间隔 0.6 m。
## 几何取 `_turn_points` 的首尾 —— 那就是过街弦（行人走的路径，横跨整个路宽）。
## ★ 关键一步：弦的中点恰好落在**路口边缘**（实测距节点 5.25 m），而条纹沿车流方向有 4 m 宽，
##   直接以中点为心画，会有**一半条纹压在路口里面**（和连接曲线缠在一起）。
##   所以先把 `along` 校正到"远离路口"的一侧，再整体外移半个横道宽 —— 条纹就刚好贴在路口外侧。
func _draw_crosswalks(doc: Doc) -> void:
	var zoom := _zoom()
	for t in doc.turns:
		if String(t.type) != "Crosswalk":
			continue
		var pts := _turn_points(doc, t)
		if pts.size() < 2:
			continue
		var p0: Vector2 = pts[0]
		var p1: Vector2 = pts[pts.size() - 1]
		var chord := p1 - p0
		var chord_len := chord.length()
		if chord_len < 1.0:
			continue
		var center := (p0 + p1) * 0.5
		var np := GridCanvas.world_to_px(_node_pos(doc, int(t.node)))
		var along := Vector2(-chord.y, chord.x).normalized()   # 弦的法向 = 车流方向
		if (center - np).dot(along) < 0.0:
			along = -along                                     # 统一指向"远离路口"
		# 外移「半个横道宽 + 额外量」→ 横道内缘刚好贴路口边缘。两个数都在 RoadMarks 可调区。
		var shift := (RoadMarks.CROSS_WIDTH_M * 0.5 + RoadMarks.CROSS_EXTRA_M) * GridCanvas.PPM
		RoadMarks.crosswalk(self, center + along * shift, along, chord_len * 0.5, zoom)

## 按 (路口, 道路) 归并进口，并且**按进口车道分组**：
## 键 = "节点|路"；值 = {node, road, by_lane:{lane_id: {turn 类型: true}}}
## ★ 2026-10-24 改：箭头原来只按"该进口有哪些转向"画（固定 3 个，与车道无关）；
##   现在是**每条进口机动车道一组**，内容是这条车道自己实际有的 turn 类型 ——
##   加/减车道、删掉某条转向，箭头都跟着变（真实路面的导向箭头就是这么标的）。
## ★ Crosswalk 等不在 ARROW_KIND_ORDER 里的类型直接跳过，所以"只有过街"的进口不会画箭头。
func _collect_approaches(doc: Doc) -> Dictionary:
	var out := {}
	for t in doc.turns:
		var ty := String(t.get("type", ""))
		if not ARROW_KIND_ORDER.has(ty):
			continue
		var lane: Dictionary = _lane_of(doc, int(t.get("src_lane", -1)))
		if lane.is_empty():
			continue
		var nid := int(t.get("node", -1))
		if int(lane.to_node) != nid:
			continue                            # 脏数据（turn_node_mismatch）：宁可不画，别画错位置
		var key := "%d|%d" % [nid, int(lane.road)]
		if not out.has(key):
			out[key] = {"node": nid, "road": int(lane.road), "by_lane": {}}
		var by: Dictionary = out[key]["by_lane"]
		var lid := int(lane.id)
		if not by.has(lid):
			by[lid] = {}
		by[lid][ty] = true
	return out

## 该 (路, 路口) 的进口机动车道，按 lat_rank **从内到外**（0 是贴中心线那条）
## ★ 只要机动车可通行类型 —— 与 SimCore TurnGenerator 的进口口径一致；
##   人行道/非机动车道的 turn 是 Crosswalk/独立非机动车，不画机动车导向箭头。
func _import_motor_lanes(doc: Doc, road: Dictionary, node_id: int) -> Array:
	var rid := int(road.get("road_id", -1))
	var out: Array = []
	_ensure_index(doc)
	for lane in (_idx_lanes.get(node_id, []) as Array):
		if int(lane.road) != rid or int(lane.to_node) != node_id:
			continue
		var ty := String(lane.type)
		if ty != "Driving" and ty != "Bus" and ty != "SharedLeftTurn":
			continue
		out.append(lane)
	# 进口车道就几条，每帧排一次可以忽略；排序只为"同一进口内从内到外"稳定
	out.sort_custom(func(a, b): return Doc.lane_lat_rank(a, road) < Doc.lane_lat_rank(b, road))
	return out

## 画一个进口的箭头：**每条进口机动车道一个箭头**，形状由这条车道"有哪些转向"决定。
## ★ 2026-10-26 改：旧做法是"有几种转向就前后排几支"（直+左+右 = 三支首尾相接），
##   现在素材里有组合图，改成把转向集合折成一个组合键（`ArrowAssets.key_for`）去查表 ——
##   「只能左转」→ 左转图；「直行 + 右转」→ 直行右转图；「直 + 左 + 右」→ 直左右图。
## ★ 组合键的**来源仍然是这条车道自己的 turn**，所以加减车道、删掉某条转向，箭头都跟着变。
func _draw_approach_arrows(doc: Doc, ap: Dictionary, k: float) -> void:
	var nid := int(ap["node"])
	var road := _road_of(doc, int(ap["road"]))
	if road.is_empty():
		return
	var by: Dictionary = ap["by_lane"]
	# 可用宽度：车道宽 × 系数。组合箭头按宽封顶，绝不跨进隔壁车道。
	var avail_w: float = Doc.LANE_WIDTH_M * GridCanvas.PPM * ArrowAssets.MAX_W_MUL
	for lane in _import_motor_lanes(doc, road, nid):
		var lid := int(lane.id)
		if not by.has(lid):
			continue                             # 这条车道一条转向都没有 → 不画（去连接模式按 G）
		var fwd := GridCanvas.world_to_px(_lane_dir_m(doc, lane))
		var lat := _lane_lateral_px(doc, lane)
		if fwd.length() < 0.001 or lat.length() < 0.001:
			continue
		fwd = fwd.normalized()
		var types: Array = []
		for ty in ARROW_KIND_ORDER:
			if (by[lid] as Dictionary).has(ty):
				types.append(String(ty))
		if types.is_empty():
			continue
		var key := ArrowAssets.key_for(types)
		if key == "":
			continue
		var box := ArrowAssets.fit(key, k, avail_w)          # Vector2(高, 宽)，画布像素
		var inset := _lane_node_inset_m(doc, lane, nid)
		# 纵向位置**由停止线推出来**（RoadMarks.arrow_tip_offset_m）：箭头永远整体落在停止线外侧，
		# 不跟着屏幕缩放漂——调 STOP_SETBACK_M 时它自动跟着走。
		var tip := _lane_end_px(doc, lane, true, inset) + lat \
			- fwd * (RoadMarks.arrow_tip_offset_m() * GridCanvas.PPM)
		if not _draw_arrow_sprite(tip, fwd, key, box.x, box.y):
			# 素材缺失 → 退回矢量箭头：只画最主要的那一支（形状差些，但功能不残废）
			_draw_turn_arrow(tip - fwd * box.x, tip, fwd, String(types[0]), box.x, box.y * 0.5)

## 贴一张导向箭头：`tip` = 尖端落点（朝路口那一头），`fwd` = 行驶方向，`key` = 组合键。
## 素材在烘焙时已统一成"尖朝上、垂直"（掉头旋过 180°），所以这里只有一个旋转变换、没有任何按类型的分支。
## 返回 false = 素材缺失，调用方自己降级。
func _draw_arrow_sprite(tip: Vector2, fwd: Vector2, key: String, ln: float, wd: float) -> bool:
	var tex := ArrowAssets.tex(key)
	if tex == null:
		return false
	# 局部 -Y（素材的"尖"）要指向 fwd → 旋转角 = atan2(fwd.x, -fwd.y)
	var ang := atan2(fwd.x, -fwd.y)
	draw_set_transform(tip, ang, Vector2.ONE)
	# 矩形从锚点沿局部 +Y 展开（= 世界的 −fwd，即从尖端往箭尾）
	draw_texture_rect(tex, Rect2(Vector2(-wd * 0.5, 0.0), Vector2(wd, ln)), false)
	# ★ 必须复位：draw_set_transform 是全局状态，不复位会污染后续所有绘制
	draw_set_transform(Vector2.ZERO, 0.0, Vector2.ONE)
	return true

## 一支箭头：origin = 箭尾、tip = 尖端（朝路口），fwd = 行驶方向。
## ★ 已去掉文字：直 / 左 / 右 / 掉头 完全靠形状区分，所以四种形状的折向必须拉开差别。
## 形状一律收在"自己的格子"内（横向包络 < ARROW_PITCH_MUL 个半宽），保证相邻两支不互相覆盖。
func _draw_turn_arrow(origin: Vector2, tip: Vector2, fwd: Vector2,
		ty: String, len_px: float, half_w: float) -> void:
	var nrm := Vector2(-fwd.y, fwd.x)            # fwd 的右侧法向
	var shaft := maxf(1.5, half_w * 0.42)
	match ty:
		"Left":
			var corner := tip                            # 杆顶到 tip：四型纵向长度一致
			var end := corner - nrm * (half_w * 1.0)
			draw_polyline(PackedVector2Array([origin, corner, end]), ARROW_COLOR, shaft, true)
			_arrow_head(end, (-nrm + fwd).normalized(), half_w * 0.85)   # 头朝左上（约 45°）
		"Right":
			var corner := tip
			var end := corner + nrm * (half_w * 1.0)
			draw_polyline(PackedVector2Array([origin, corner, end]), ARROW_COLOR, shaft, true)
			_arrow_head(end, (nrm + fwd).normalized(), half_w * 0.85)    # 头朝右上（约 45°）
		"UTurn":
			# U 形：两条腿分居格子中心【左右各一】，头朝后。
			# ★ 必须对称：头挂在右腿上会让包络右偏，而"进口在路左侧"时整组是往左排的，
			#   那一侧正好压到相邻箭头 → 整支往左平移半个头宽，把包络拉回格子中心。
			var side := half_w * 0.85
			var shift := nrm * (half_w * 0.40)
			var p0 := origin - nrm * side - shift
			var p1 := tip - nrm * side - shift
			var p2 := tip + nrm * side - shift
			var p3 := p2 - fwd * (len_px * 0.40)
			draw_polyline(PackedVector2Array([p0, p1, p2, p3]), ARROW_COLOR, shaft, true)
			_arrow_head(p3, -fwd, half_w * 0.80)                          # 头朝后（掉头）
		_:
			var back := tip - fwd * (half_w * 1.7)
			draw_line(origin, back, ARROW_COLOR, shaft, true)
			_arrow_head(tip, fwd, half_w)                                 # 头朝前（直行）

## 三角箭头：尖端在 tip、指向 dir，底边半宽 half
func _arrow_head(tip: Vector2, dir: Vector2, half: float) -> void:
	var n := Vector2(-dir.y, dir.x)
	var back := tip - dir * (half * 1.7)
	draw_colored_polygon(PackedVector2Array([
		tip, back + n * half, back - n * half]), ARROW_COLOR)

# --- 连接（turn）渲染 ----------------------------------------------------

## 一条车道中心线的两端（像素，已按各端退回 + 横向偏移）——绘制与点选共用
func _lane_seg_px(doc: Doc, lane: Dictionary) -> PackedVector2Array:
	var lat := _lane_lateral_px(doc, lane)
	var out := PackedVector2Array()
	out.append(_lane_end_px(doc, lane, false, _lane_node_inset_m(doc, lane, int(lane.from_node))) + lat)
	out.append(_lane_end_px(doc, lane, true, _lane_node_inset_m(doc, lane, int(lane.to_node))) + lat)
	return out

## 车道【中心线】的绘制段（像素，**不含横向偏移**）。
## ★ 要自己叠加横向偏移时用它，**不要用 `_lane_seg_px`** —— 那个内部已经加过一遍 `+ lat` 了，
##   再叠一次就会整整多偏半个车道宽（2026-10-26 踩过：单车道时边缘线跑到人行道正中）。
func _lane_axis_px(doc: Doc, lane: Dictionary) -> PackedVector2Array:
	var out := PackedVector2Array()
	out.append(_lane_end_px(doc, lane, false, _lane_node_inset_m(doc, lane, int(lane.from_node))))
	out.append(_lane_end_px(doc, lane, true, _lane_node_inset_m(doc, lane, int(lane.to_node))))
	return out

func _draw_turns(doc: Doc) -> void:
	var sel: int = EditorApp.selected_turn
	var hl: int = EditorApp.sumo_highlight_turn
	for t in doc.turns:
		var ty := String(t.type)
		var pts := _turn_points(doc, t)
		if pts.size() < 2:
			continue
		var w := 1.2 if ty == "Crosswalk" else 2.5
		var is_sel := int(t.id) == sel
		# ★ 连接线**只用转向色**，冲突不再染色（2026-10-28 老板：选中路口时线全变红，混在一起看不清）。
		#   为什么不能"只把红改淡/改窄"：一个路口里的连接几乎两两冲突（示例路口 10/10），
		#   只要还染红就是满屏红——全红 = 没有信息。
		#   冲突改由**属性面板配时区**的「⚠ 保护冲突」逐阶段列出（数据源 fetch_conflict_pairs 未动）。
		# ★ 专业配时矩阵的"当前行"用**蓝色**另标一路：橙色已经是"选中"，两者同时出现时必须能分开。
		if int(t.id) == hl:
			draw_polyline(pts, UITokens.SEL_GLOW, w + 11.0, true)
			draw_polyline(pts, SUMO_LINK_HILITE, w + 4.5, true)
		if is_sel:
			draw_polyline(pts, UITokens.SEL_GLOW, w + 7.0, true)
			draw_polyline(pts, UITokens.SEL_RING, w + 2.0, true)
		# ★ 专业配时矩阵选中的那一跳：整条线按灯态染色（绿/黄/红），
		#   这样"我在配哪个阶段、哪几条在路上跑"一眼能对上（2026-11-04 老板要求）。
		var tls_state := EditorApp.sumo_turn_state(int(t.id))
		if tls_state != "":
			var tinted := _tls_state_color(tls_state)
			tinted.a = 0.55 if ty == "Crosswalk" else 0.92
			draw_polyline(pts, tinted, w + 0.6, true)
		else:
			draw_polyline(pts, _turn_color(ty), w, true)

## 一条连接的曲线采样点（像素）——绘制与"点选"共用这一份几何，避免两处各写一套
## ★ 端点口径与 SimCore 的弦模型一致（`Doc.crosswalk_midpoint` 同源）：
##   入口 = 节点 − u_src·D、出口 = 节点 + u_dst·D，再叠车道横向偏移。
##   **不能**按"车道哪一端连节点"取端点（以前的写法）：源车道的 to_node 不一定是路口。
##   写错时曲线会跑到另一侧，岛也跟着跑错（用户报的第 1 个问题）。
func _turn_points(doc: Doc, t: Dictionary) -> PackedVector2Array:
	var s: Dictionary = _lane_of(doc, int(t.src_lane))
	var d: Dictionary = _lane_of(doc, int(t.dst_lane))
	if s.is_empty() or d.is_empty():
		return PackedVector2Array()
	var node_id := int(t.node)
	var np := GridCanvas.world_to_px(_node_pos(doc, node_id))
	var us := _lane_dir_m(doc, s)
	var ud := _lane_dir_m(doc, d)
	var ins_s := maxf(_lane_node_inset_m(doc, s, node_id), Doc.CROSSWALK_CHORD_DEPTH)
	var ins_d := maxf(_lane_node_inset_m(doc, d, node_id), Doc.CROSSWALK_CHORD_DEPTH)
	var p0 := np - GridCanvas.world_to_px(us * ins_s) + _lane_lateral_px(doc, s)
	var p3 := np + GridCanvas.world_to_px(ud * ins_d) + _lane_lateral_px(doc, d)
	var c1 := p0 + GridCanvas.world_to_px(us * ins_s * 0.5)
	var c2 := p3 - GridCanvas.world_to_px(ud * ins_d * 0.5)
	return _sample_cubic(p0, c1, c2, p3, 14)

## 连接模式用：找离 world_m 最近、且在 tol_m 米内的 turn id；没有返回 -1
## only_node >= 0 时只认这个路口的转向（连接模式"先选路口再选连接线"的口径，2026-10-28）
func pick_turn(world_m: Vector2, tol_m: float, only_node: int = -1) -> int:
	var doc: Doc = EditorApp.doc
	if doc == null:
		return -1
	_sync_index(doc)          # ★ 点选前先对齐索引（刚拖完节点就点，坐标必须是最新的）
	var m := GridCanvas.world_to_px(world_m)
	var tol_px := tol_m * GridCanvas.PPM
	var best := -1
	var best_d := tol_px
	for t in doc.turns:
		if only_node >= 0 and int(t.node) != only_node:
			continue
		var pts := _turn_points(doc, t)
		if pts.size() < 2:
			continue
		var d := _dist_to_polyline(m, pts)
		if d < best_d:
			best_d = d
			best = int(t.id)
	return best

## 进口/出口提示只描这么长（米）—— 从路口那头算起
const HINT_LEN_M := 12.0

## 选中路口时：把它的进口车道（暖色）/出口车道（冷色）描**路口那头的一小段**，提示可从这里画连接
func _draw_lane_hints(doc: Doc) -> void:
	if EditorApp.mode != EditorMode.Kind.CONNECT:
		return                                # 进口/出口提示只在连接模式显示
	var jid: int = EditorApp.selected_junction
	if jid < 0:
		return
	var pend: int = EditorApp.pending_src_lane
	var hint_len_px: float = HINT_LEN_M * GridCanvas.PPM
	_ensure_index(doc)
	# ★ 只扫"连在这个路口上的车道"（索引），不再每帧扫全图 lanes（E91 C1）
	for lane in (_idx_lanes.get(jid, []) as Array):
		var is_import := int(lane.to_node) == jid
		var is_export := int(lane.from_node) == jid
		if not is_import and not is_export:
			continue
		var seg := _lane_seg_px(doc, lane)
		if seg.size() < 2:
			continue
		# ★ 只画**路口这一头的一小段**（2026-10-28 老板反馈的"蓝线横穿整张图"就是这个）：
		#   整条车道描一遍会把画布铺满橙/蓝长线，看着像选中了整条路；描 8 m 够表达"从这条进 / 从这条出"。
		var near: Vector2 = seg[1] if is_import else seg[0]
		var far: Vector2 = seg[0] if is_import else seg[1]
		var span := near.distance_to(far)
		var stub: Vector2 = near if span <= hint_len_px else near.lerp(far, hint_len_px / span)
		var col := Color(UITokens.SEL_RING, 0.85) if is_import else Color(UITokens.BLUE_500, 0.85)
		var w := 3.0
		if pend >= 0 and is_import and int(lane.id) == pend:
			col = UITokens.ORANGE_700          # 已选进口：更深一档 + 加粗（原来用亮黄，浅底上糊）
			w = 5.0
		draw_line(stub, near, col, w, true)

## 连接模式用：找离 world_m 最近、且在容差内的 lane id；没有返回 -1
func pick_lane(doc: Doc, world_m: Vector2, tol_m: float) -> int:
	_sync_index(doc)          # ★ 同上：点选前对齐索引
	var m := GridCanvas.world_to_px(world_m)
	var tol_px := (tol_m + Doc.LANE_WIDTH_M * 0.5) * GridCanvas.PPM
	var best := -1
	var best_d := tol_px
	for lane in doc.lanes:
		var seg := _lane_seg_px(doc, lane)
		if seg.size() < 2:
			continue
		var d := Doc.dist_to_seg(m, seg[0], seg[1])
		if d < best_d:
			best_d = d
			best = int(lane.id)
	return best

## 点到折线的最短距离（像素）：逐段调 Doc.dist_to_seg
func _dist_to_polyline(p: Vector2, pts: PackedVector2Array) -> float:
	var best := INF
	for i in range(pts.size() - 1):
		best = minf(best, Doc.dist_to_seg(p, pts[i], pts[i + 1]))
	return best

# --- 几何小工 -----------------------------------------------------------


## 某个路口该"退回"多少（米）：该路口所有道路里【最宽那条的半宽】；<2 条路返回 0
## ★ 同一路口统一口径：窄路也按最宽的退 —— 否则窄路会侵入路口、连接曲线两端也会错位
## ★ 现在直接从索引读（原来每条 lane 每帧都把全图 roads × lanes 扫一遍）
func _junction_inset_m(doc: Doc, node_id: int) -> float:
	_ensure_index(doc)
	return float(_idx_inset.get(node_id, 0.0))

## 一条车道在某个端点该退回多少（米）：直接取该路口的统一退让量
## （_lane 已不参与计算，仅为不改动多处调用点而保留在签名里）
func _lane_node_inset_m(doc: Doc, _lane: Dictionary, node_id: int) -> float:
	return _junction_inset_m(doc, node_id)

## 车道【中心线】在"节点那一端、沿车道退回 inset 米"的点（像素，不含横向偏移）
## ★ 节点坐标读索引（Doc.node_pos 是全表线性查找，这条函数每帧被调上千次）
func _lane_end_px(doc: Doc, lane: Dictionary, at_to: bool, inset_m: float) -> Vector2:
	var node_id := int(lane.to_node) if at_to else int(lane.from_node)
	var np := GridCanvas.world_to_px(_node_pos(doc, node_id))
	if inset_m <= 0.0:
		return np
	var v := _node_pos(doc, int(lane.to_node)) - _node_pos(doc, int(lane.from_node))
	if v.length() < 0.001:
		return np
	var along: Vector2 = (-v.normalized() if at_to else v.normalized()) * inset_m
	return np + GridCanvas.world_to_px(along)

## 车道的横向偏移（像素）：按其【道路参考方向】的法向（与路面绘制同一规则）
func _lane_lateral_px(doc: Doc, lane: Dictionary) -> Vector2:
	var road := _road_of(doc, int(lane.get("road", -1)))
	if road.is_empty():
		return Vector2.ZERO
	return _road_normal_px(doc, road) * _lane_offset(lane, road) * GridCanvas.PPM

## 道路参考方向（a_node → b_node）的单位法向（像素）；退化为 0 时返回零向量
func _road_normal_px(doc: Doc, road: Dictionary) -> Vector2:
	var ra := GridCanvas.world_to_px(_node_pos(doc, int(road.a_node)))
	var rb := GridCanvas.world_to_px(_node_pos(doc, int(road.b_node)))
	var rd := rb - ra
	if rd.length() < 0.001:
		return Vector2.ZERO
	var nd := rd.normalized()
	return Vector2(-nd.y, nd.x)

## 车道行驶方向单位向量（from_node → to_node，世界米）
func _lane_dir_m(doc: Doc, lane: Dictionary) -> Vector2:
	var v := _node_pos(doc, int(lane.to_node)) - _node_pos(doc, int(lane.from_node))
	return v.normalized() if v.length() > 0.001 else Vector2.ZERO

## 三次贝塞尔采样成折线（像素）；绘制与点选共用
func _sample_cubic(p0: Vector2, c1: Vector2, c2: Vector2, p1: Vector2, steps: int) -> PackedVector2Array:
	var pts := PackedVector2Array()
	for i in range(steps + 1):
		var t := float(i) / float(steps)
		var mt := 1.0 - t
		pts.append(mt * mt * mt * p0 + 3.0 * mt * mt * t * c1 \
			+ 3.0 * mt * t * t * c2 + t * t * t * p1)
	return pts

## 端点标记：吸到节点画绿环，否则画橙点
func _draw_snap_dot(p: Vector2, node_id: int) -> void:
	if node_id >= 0:
		draw_arc(p, 12.0, 0.0, TAU, 28, UITokens.SNAP_OK, 2.5, true)
	else:
		draw_circle(p, 6.0, UITokens.SNAP_FREE)

## 车道相对"道路参考方向"的横向偏移（米）。
## 参考方向 = road.a_node → road.b_node；正向在一侧、反向在另一侧，互不叠。
## ★ S2：排位一律问 Doc.lane_lat_rank()，这里只做"米 → 侧别"的换算。
func _lane_offset(lane: Dictionary, road: Dictionary) -> float:
	var side := 1.0 if int(lane.get("from_node", -1)) == int(road.get("a_node", -2)) else -1.0
	return side * (float(Doc.lane_lat_rank(lane, road)) + 0.5) * Doc.LANE_WIDTH_M

## 车道填充色 —— 一律读 `UITokens`（R0 §5.1）。浅底上的整套取值与深底时代完全不同，
## 别再往这里写十六进制。
func _lane_color(type: String) -> Color:
	match type:
		"Driving": return UITokens.ROAD_DRIVING
		"Bus": return UITokens.ROAD_BUS
		"Biking": return UITokens.ROAD_BIKING
		"Sidewalk": return UITokens.ROAD_SIDEWALK
		"SharedLeftTurn": return UITokens.ROAD_LEFT_TURN
	return UITokens.ROAD_DRIVING

## turn 配色：人行横道画得更淡，避免淹没机动车转向。
## ⚠ 掉头用紫不用红 —— 红是「冲突」专用色，两者要能同时分辨。
func _turn_color(ty: String) -> Color:
	var c: Color = UITokens.INK_3
	match ty:
		"Straight": c = UITokens.TURN_STRAIGHT
		"Left": c = UITokens.TURN_LEFT
		"Right": c = UITokens.TURN_RIGHT
		"UTurn": c = UITokens.TURN_UTURN
		"Crosswalk": c = UITokens.TURN_CROSSWALK
	c.a = 0.55 if ty == "Crosswalk" else 0.85
	return c


## 专业配时阶段 → 连接线颜色。G 深绿 / g 浅绿 / y 黄 / r 红 / o 灭（灰）。
## ★ 与信号灯三色语义对齐：G = 保护放行（能走）、g = 允许但要让行（对方也是绿）。
func _tls_state_color(state: String) -> Color:
	match state:
		"G": return Color("#2E7D32")
		"g": return Color("#7CC08A")
		"y": return Color("#E0A320")
		"o": return Color("#9AA0A6")
	return Color("#C0392B")          # r（含认不出来的）


## 路口补面：把被"退回"剪掉的那块填回来（否则路口中间是个洞）。
## 做法 = 取各路端面的外侧角点，连成【凸多边形】填充：
##   4 条等宽路 → 仍是方块；2 条路宽不同（道路展宽）→ 平滑的梯形；斜交 → 顺着走的多边形。
func _draw_junction_fills(doc: Doc) -> void:
	for n in doc.nodes:
		var nid := int(n.id)
		if _junction_degree(doc, nid) < 2:
			continue
		var hull := _junction_hull(doc, nid)
		if hull.size() >= 3:
			draw_colored_polygon(hull, _junction_color())

## 世界坐标点是否落在"这个路口的补面范围内"（含 `pad_m` 米余量）。
## 连接模式用它决定"点空白要不要退出路口"——没点中连接线但还在路口里，就不该被踢出去。
func point_in_junction(doc: Doc, world_m: Vector2, node_id: int, pad_m: float = 0.0) -> bool:
	_sync_index(doc)
	var m := GridCanvas.world_to_px(world_m)
	var pad := pad_m * GridCanvas.PPM
	var hull := _junction_hull(doc, node_id)
	if hull.size() >= 3:
		if Geometry2D.is_point_in_polygon(m, hull):
			return true
		if pad <= 0.0:
			return false
		for i in hull.size():                       # 凸包外扩一圈：贴着路口边点也算里面
			if Doc.dist_to_seg(m, hull[i], hull[(i + 1) % hull.size()]) <= pad:
				return true
		return false
	# 没有补面（自由端 / 退化）→ 退化成"节点附近一点点"，至少不会因为点空而闪一下就丢选中
	var jp := GridCanvas.world_to_px(_node_pos(doc, node_id))
	return jp.distance_to(m) <= maxf(pad, 16.0)


## 某路口补面用的凸包（像素）——按节点缓存；凸包只跟"角点"有关，角点只跟索引有关，
## 所以和索引共用同一套失效（结构变 / 坐标变 → 整表清空）。
## ★ 这一步是稳态帧里最贵的一段（每条边都要比一次排序），缓存后稳态帧基本不再付。
func _junction_hull(doc: Doc, node_id: int) -> PackedVector2Array:
	if _idx_hull.has(node_id):
		return _idx_hull[node_id]
	var hull := _convex_hull(_junction_corners(doc, node_id))
	_idx_hull[node_id] = hull
	return hull

## 某路口处、每条相连车道端面的两个外侧角点（像素）
## ★ 现在只扫"连在这个节点上的车道"（索引），不再每节点扫全图 lanes
func _junction_corners(doc: Doc, node_id: int) -> PackedVector2Array:
	var pts := PackedVector2Array()
	var w_half := Doc.LANE_WIDTH_M * 0.5 * GridCanvas.PPM
	_ensure_index(doc)
	var inset := float(_idx_inset.get(node_id, 0.0))
	for lane in (_idx_lanes.get(node_id, []) as Array):
		var at_to := int(lane.to_node) == node_id
		var road := _road_of(doc, int(lane.road))
		if road.is_empty():
			continue
		var c := _lane_end_px(doc, lane, at_to, inset) + _lane_lateral_px(doc, lane)
		var nrm := _road_normal_px(doc, road)
		pts.append(c + nrm * w_half)
		pts.append(c - nrm * w_half)
	return pts

## 凸包（Andrew 单调链）：输入点很少，开销可忽略；共线/重复点会被剔除。
func _convex_hull(points: PackedVector2Array) -> PackedVector2Array:
	var n := points.size()
	if n < 3:
		return points
	var pts: Array = Array(points)
	pts.sort_custom(func(a: Vector2, b: Vector2) -> bool: return a.x < b.x or (a.x == b.x and a.y < b.y))
	var lower: Array[Vector2] = []
	for p in pts:
		while lower.size() >= 2 and _cross(lower[lower.size() - 2], lower[lower.size() - 1], p) <= 0.0:
			lower.pop_back()
		lower.append(p)
	var upper: Array[Vector2] = []
	for i in range(pts.size() - 1, -1, -1):
		var p: Vector2 = pts[i]
		while upper.size() >= 2 and _cross(upper[upper.size() - 2], upper[upper.size() - 1], p) <= 0.0:
			upper.pop_back()
		upper.append(p)
	lower.pop_back()
	upper.pop_back()
	var out := PackedVector2Array()
	for p in lower:
		out.append(p)
	for p in upper:
		out.append(p)
	return out

## 叉积：(a-o) × (b-o)；> 0 表示 o→a→b 逆时针（左转）
func _cross(o: Vector2, a: Vector2, b: Vector2) -> float:
	return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x)

func _junction_color() -> Color:
	return UITokens.ROAD_JUNCTION

# ============================================================
# 专业模式：检测设施图标（S3，2026-11-05）
# ============================================================
## 设施挂在车道上：检测器是**一个点**，站台/停车场是**一段**。
## 颜色按用途分，与信号灯的绿/黄/红错开（避免"这段黄线是黄灯吗"）。
const FACILITY_DET_COLOR := Color("#2A9D8F")    ## 检测器（青）
const FACILITY_STOP_COLOR := Color("#E9A23B")   ## 公交站（琥珀）
const FACILITY_PARK_COLOR := Color("#5B7BD5")   ## 停车场（靛）


## 车道上的「pos 米处」对应哪个屏幕像素点（含横向偏移，落在车道中线）
func _lane_point_px(doc: Doc, lane: Dictionary, pos_m: float) -> Vector2:
	var axis := _lane_axis_px(doc, lane)
	if axis.size() < 2:
		return Vector2.ZERO
	var total: float = maxf(float(lane.get("length", 0.0)), 0.001)
	var t: float = clampf(pos_m / total, 0.0, 1.0)
	return axis[0].lerp(axis[1], t) + _lane_lateral_px(doc, lane)


func _facility_color(kind: String) -> Color:
	if kind == SumoFacilitiesModel.KIND_BUS_STOP:
		return FACILITY_STOP_COLOR
	if kind == SumoFacilitiesModel.KIND_PARKING:
		return FACILITY_PARK_COLOR
	return FACILITY_DET_COLOR


func _draw_facilities(doc: Doc) -> void:
	var profile: SumoProfile = EditorApp.profile
	if profile == null or profile.additional.is_empty():
		return
	if not EditorApp.is_pro():
		return                      # 设施是专业档数据，基础模式一个像素都不画
	var sel := EditorApp.selected_facility

	for row in profile.additional:
		var lane := _lane_of(doc, int(row.get("lane", -1)))
		if lane.is_empty():
			continue
		var kind := String(row.get("kind", ""))
		var color := _facility_color(kind)
		var is_sel := sel != "" and sel == String(row.get("id", ""))

		if kind == SumoFacilitiesModel.KIND_E1 or kind == SumoFacilitiesModel.KIND_E2:
			# 检测器：一个点（e2 附带检测区那段，从 pos 往前 length）
			var pos := float(row.get("pos", 0.0))
			if kind == SumoFacilitiesModel.KIND_E2:
				var seg_a := _lane_point_px(doc, lane, maxf(pos - float(row.get("length", 0.0)), 0.0))
				var seg_b := _lane_point_px(doc, lane, pos)
				if is_sel:
					draw_line(seg_a, seg_b, UITokens.SEL_GLOW, 11.0, true)
				draw_line(seg_a, seg_b, color, 5.0, true)
			var p := _lane_point_px(doc, lane, pos)
			if is_sel:
				draw_circle(p, 9.0, UITokens.SEL_GLOW)
			var box := Rect2(p - Vector2(4.5, 4.5), Vector2(9, 9))
			draw_rect(box, color, true)
			draw_rect(box, Color.WHITE, false, 1.2)
		else:
			# 站台 / 停车场：一段
			var a := _lane_point_px(doc, lane, float(row.get("start", 0.0)))
			var b := _lane_point_px(doc, lane, float(row.get("end", 0.0)))
			if is_sel:
				draw_line(a, b, UITokens.SEL_GLOW, 13.0, true)
			draw_line(a, b, color, 8.0, true)
			# 两端各一个小横杠，像站台/车位边界
			draw_line(a, b, Color(1, 1, 1, 0.65), 2.0, true)


## 点中哪个设施（世界坐标，米）。找不到返回 ""。
## ★ 容差比车道小：设施画在车道中线上，容差给大了永远抢不过车道。
func pick_facility(world_m: Vector2, tol_m: float) -> String:
	var profile: SumoProfile = EditorApp.profile
	if profile == null or profile.additional.is_empty() or not EditorApp.is_pro():
		return ""
	var doc: Doc = EditorApp.doc
	var m := GridCanvas.world_to_px(world_m)
	var tol_px: float = maxf(tol_m * GridCanvas.PPM, 6.0)

	var best := ""
	var best_d := tol_px
	for row in profile.additional:
		var lane := _lane_of(doc, int(row.get("lane", -1)))
		if lane.is_empty():
			continue
		var kind := String(row.get("kind", ""))
		var d := INF
		if kind == SumoFacilitiesModel.KIND_E1 or kind == SumoFacilitiesModel.KIND_E2:
			d = m.distance_to(_lane_point_px(doc, lane, float(row.get("pos", 0.0))))
		else:
			var a := _lane_point_px(doc, lane, float(row.get("start", 0.0)))
			var b := _lane_point_px(doc, lane, float(row.get("end", 0.0)))
			d = _dist_to_polyline(m, PackedVector2Array([a, b]))
		if d < best_d:
			best_d = d
			best = String(row.get("id", ""))
	return best
