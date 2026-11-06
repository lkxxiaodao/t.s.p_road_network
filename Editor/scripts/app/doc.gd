class_name Doc
extends RefCounted

const LANE_WIDTH_M := 3.5        ## 一条车道宽（米）
const CROSSWALK_CHORD_DEPTH := 6.0  ## 过街弦在节点两侧的伸出深度（米）；★ 必须等于 SimCore 的 CrosswalkChordDepth
const DEFAULT_SPEED := 13.89     ## 默认限速（米/秒）
const SIDEWALK_SPEED := 1.4
const BIKING_SPEED := 5.0
const NODE_SNAP_M := 3.0         ## 端点合并到已有节点的判定半径（米）
const MERGE_EPS_M := 0.6

var next_node_id: int = 0
var next_lane_id: int = 0
var next_road_id: int = 0
var next_turn_id: int = 0
var next_deco_id: int = 0
var next_island_id: int = 0

var nodes: Array = []
var roads: Array = []
var lanes: Array = []
var turns: Array = []
var islands: Array = []      ## 行人二次过街的中央安全岛（schema islands[]，E92 G2 · N6b）
var signals: Array = []
var demand: Array = []
var pedestrians: Array = []
var bicycles: Array = []
var decos: Array = []

var meta := {"name": "未命名", "author": "", "chapter": 1, "index": 1}
var game_rules := {
	"anchors": {"R": [0, 0], "D": [0, 0], "P": [0, 0], "Q": [0, 0]},
	"weights": {"R": 0.4, "D": 0.2, "P": 0.2, "Q": 0.2},
	"vehicle_mix": "CarOnly",        # ★ S2 新增：车型构成预设（关卡级）
	"events": [],
	"tutorial": [],
}
var dirty: bool = false
var signal_repair_queue: Array = []   ## 相位被摘空而降级的节点 id，等 EditorApp 重推（S4-b）
var signal_tuning_stash: Dictionary = {}   ## 相位整段被摘空时暂存的手调配时（key=节点 id）；★ 编辑器私有，不进 .spmap


# --- 默认道路模板（非对称车道：正/反向各一个车道数）--------------------

static func default_template() -> Dictionary:
	return {
		"lanes_fwd": 1,      # 正向（a→b）车道数
		"lanes_rev": 1,      # 反向（b→a）车道数
		"oneway": false,     # 单向：只生成正向段
		"speed_limit": 13.89,
		"bus": false,        # 公交专用道（每向 +1）
		"bike": false,       # 独立非机动车道（每向 +1）
		"sidewalk": true,    # 人行道（每向 +1）
		# ★ 左转专用道（E92 G1）：现实中只在**进口道**出现，出口道没有 →
		#   按方向独立开关，默认都关。a→b 那一向的进口是 **b 端**。
		"left_turn_fwd": false,
		"left_turn_rev": false,
	}


# --- 节点 ---------------------------------------------------------------

## 找离 pos_m 不超过 radius 的已有节点 id；没有返回 -1
func find_node_near(pos_m: Vector2, radius: float = NODE_SNAP_M) -> int:
	for n in nodes:
		if Vector2(n.x, n.y).distance_to(pos_m) <= radius:
			return n.id
	return -1

func add_node(type: String, pos_m: Vector2) -> int:
	var id := next_node_id
	next_node_id += 1
	nodes.append({"id": id, "type": type, "x": pos_m.x, "y": pos_m.y})
	dirty = true
	return id

## 复用附近节点，没有就新建（默认 Unsignalized）
func node_at_or_new(pos_m: Vector2) -> int:
	var id := find_node_near(pos_m)
	if id >= 0:
		return id
	return add_node("Unsignalized", pos_m)

func node_pos(id: int) -> Vector2:
	for n in nodes:
		if n.id == id:
			return Vector2(n.x, n.y)
	return Vector2.ZERO

# --- 道路（壳）与车道（实体）--------------------------------------------

## 建一条路：展开出 lanes，返回 road_id（失败返回 -1）
func create_road(a_node: int, b_node: int, tmpl: Dictionary) -> int:
	var length := node_pos(a_node).distance_to(node_pos(b_node))
	if length <= 0.0:
		return -1

	var road_id := next_road_id
	next_road_id += 1

	var lane_ids: Array = []
	for spec in expand_lanes(road_id, a_node, b_node, length, tmpl):
		lane_ids.append(_append_lane(spec))

	roads.append({
		"road_id": road_id,
		"a_node": a_node,
		"b_node": b_node,
		"lanes": lane_ids,
		"template": tmpl.duplicate(),
	})
	dirty = true
	recompute_node_types()
	return road_id

## 纯展开：一条 road → 若干 lane 规格（不含 id；id 由调用方分配）
static func expand_lanes(road_id: int, a: int, b: int, len_m: float, t: Dictionary) -> Array:
	var out: Array = []
	var v: float = float(t.get("speed_limit", DEFAULT_SPEED))
	out.append_array(_dir_lanes(road_id, a, b, int(t.get("lanes_fwd", 1)), v, len_m, t,
		bool(t.get("left_turn_fwd", false))))
	if not bool(t.get("oneway", false)):
		out.append_array(_dir_lanes(road_id, b, a, int(t.get("lanes_rev", 1)), v, len_m, t,
			bool(t.get("left_turn_rev", false))))
	return out

## 一个方向的车道块：从中心线向外排，lat_index = 0,1,2…（含左转专用 / 公交 / 非机动车 / 人行道）
## ★ 左转专用道（`SharedLeftTurn`，E92 G1）排**最内侧**（贴中心线）：lat_index 0、lane_index 0，
##   机动车序号整体后移一位 —— 否则它和 Driving 0 在仿真里横向位置重合（lane_index 参与
##   (lane_index+0.5)·车道宽 的偏移计算）。不开这个开关时循环体不执行，**与旧输出逐字段一致**。
static func _dir_lanes(road_id: int, from_n: int, to_n: int, n_drv: int, v: float,
		len_m: float, t: Dictionary, with_lt: bool) -> Array:
	var out: Array = []
	var lat := 0
	var idx := 0
	if with_lt:
		out.append(_lane_spec(road_id, from_n, to_n, "SharedLeftTurn", 0, lat, len_m, v))
		lat += 1
		idx = 1
	for i in n_drv:
		out.append(_lane_spec(road_id, from_n, to_n, "Driving", idx + i, lat, len_m, v))
		lat += 1
	if t.get("bus", false):
		out.append(_lane_spec(road_id, from_n, to_n, "Bus", 0, lat, len_m, v))
		lat += 1
	if t.get("bike", false):
		out.append(_lane_spec(road_id, from_n, to_n, "Biking", 0, lat, len_m, BIKING_SPEED))
		lat += 1
	if t.get("sidewalk", true):
		out.append(_lane_spec(road_id, from_n, to_n, "Sidewalk", 0, lat, len_m, SIDEWALK_SPEED))
	return out

static func _lane_spec(road_id: int, a: int, b: int, ty: String, idx: int,
		lat: int, ln: float, sp: float) -> Dictionary:
	return {"road": road_id, "from_node": a, "to_node": b,
			"type": ty, "lane_index": idx, "lat_index": lat,
			"length": ln, "speed_limit": sp}

## 给 lane 规格分配 id 并落库
func _append_lane(spec: Dictionary) -> int:
	var id := next_lane_id
	next_lane_id += 1
	var lane := spec.duplicate()
	lane["id"] = id
	lanes.append(lane)
	return id


# --- 查询 / 编辑 ---------------------------------------------------------

func find_road(road_id: int) -> Dictionary:
	for r in roads:
		if r.road_id == road_id:
			return r
	return {}

## 用新模板重建一条路的全部车道（改车道数/单向/类型时调用）

func rebuild_road(road_id: int, new_tmpl: Dictionary) -> void:
	var road := find_road(road_id)
	if road.is_empty():
		return
	var old_ids: Array = road.lanes.duplicate()
	_queue_signal_repair_at(old_ids) 

	# ★ 先把后果说清楚（撤销由 E08 的快照栈兜住，不用额外做）
	var will_drop := 0
	for t in turns:
		if old_ids.has(int(t.src_lane)) or old_ids.has(int(t.dst_lane)):
			will_drop += 1
	if will_drop > 0:
		print("重建 road %d：车道集合要变 → 将删掉 %d 条引用了旧车道的连接（Ctrl+Z 可撤销）" % [road_id, will_drop])

	lanes = lanes.filter(func(l): return not old_ids.has(l.id))
	_drop_turns_touching(old_ids)               # ★ 删 turn + 清相位死引用 + 清需求

	var length := node_pos(road.a_node).distance_to(node_pos(road.b_node))
	var new_ids: Array = []
	for spec in expand_lanes(road_id, road.a_node, road.b_node, length, new_tmpl):
		new_ids.append(_append_lane(spec))
	road.lanes = new_ids
	road.template = new_tmpl.duplicate()
	dirty = true

## 原地改 road 的模板字段（★ 不重建 lane、不动 turns / signals / demand）
## 只适用于"不改变车道集合"的字段：目前只有 speed_limit
func set_road_template_field(road_id: int, key: String, value) -> bool:
	var r := find_road(road_id)
	if r.is_empty():
		return false
	r.template[key] = value
	if key == "speed_limit":
		var v := float(value)
		for lid in r.lanes:
			var l := find_lane(int(lid))
			if l.is_empty():
				continue
			# ★ 只有机动车道吃模板限速；Biking / Sidewalk 走各自的固定速度（见 expand_lanes）
			var lt := String(l.type)
			if lt == "Driving" or lt == "Bus" or lt == "SharedLeftTurn":
				l["speed_limit"] = v
	dirty = true
	return true

# --- 复制 / 粘贴（剪贴板由 EditorApp 持有；粘贴一律分配新 id）--------------

## 复制一条路：记下"横断面模板 + 两端坐标（米）"，粘贴时整体平移
func copy_road_snapshot(road_id: int) -> Dictionary:
	var r := find_road(road_id)
	if r.is_empty():
		return {}
	var a := node_pos(int(r.a_node))
	var b := node_pos(int(r.b_node))
	return {
		"template": (r.template as Dictionary).duplicate(true),
		"ax": a.x, "ay": a.y, "bx": b.x, "by": b.y,
	}

## 粘贴一条路：两端各新建一个节点，再按模板建路（★ 新节点新车道，id 全新）
func paste_road(payload: Dictionary, offset_m: Vector2) -> int:
	var tmpl: Dictionary = (payload.get("template", {}) as Dictionary).duplicate(true)
	var a := Vector2(float(payload.get("ax", 0.0)), float(payload.get("ay", 0.0))) + offset_m
	var b := Vector2(float(payload.get("bx", 0.0)), float(payload.get("by", 0.0))) + offset_m
	var a_node := add_node("Unsignalized", a)
	var b_node := add_node("Unsignalized", b)
	return create_road(a_node, b_node, tmpl)

## 复制一个装饰（去掉 id，粘贴时再分配）
func copy_deco_snapshot(id: int) -> Dictionary:
	var d := find_deco(id)
	if d.is_empty():
		return {}
	var c := d.duplicate(true)
	c.erase("id")
	return c

## 粘贴一个装饰：偏移 offset_m，分配新 id，其余字段照搬
func paste_deco(payload: Dictionary, offset_m: Vector2) -> int:
	var src := payload.duplicate(true)
	var asset := String(src.get("asset", ""))
	if asset == "":
		return -1
	var pos := Vector2(float(src.get("x", 0.0)), float(src.get("y", 0.0))) + offset_m
	var id := add_deco(asset, pos)
	if id < 0:
		return -1
	var nd := find_deco(id)
	nd["rotation"] = float(src.get("rotation", 0.0))
	nd["scale"] = float(src.get("scale", 1.0))
	nd["z"] = int(src.get("z", 1))
	nd["anchor"] = String(src.get("anchor", "center"))
	nd["tile"] = bool(src.get("tile", false))
	nd["w"] = int(src.get("w", 0))
	nd["h"] = int(src.get("h", 0))
	return id

# --- 复制 / 粘贴：路口包（B21；C8"复制路口"）-----------------------------

## 复制一个"路口包"：该节点 + 与它相连的路（连 lane 一起）+ 该路口的全部连接 + 相位。
## ★ 快照里只存【局部序号】（node / road / lane / turn 各 0..n-1）+ 相对中心的坐标，**不存实时 id**：
##   粘贴时才能全量重映射，粘多少次都不会共享 id。
## ★★ 复制这一刻【只读】—— 一个字段都不改源数据（追加约束）。中心节点恒为 nodes[0]。
func copy_junction_snapshot(node_id: int) -> Dictionary:
	var center := find_node(node_id)
	if center.is_empty():
		return {}
	# ① 相关 road：任何一条 lane 的端点在中心节点上（按 lanes 出现顺序，结果稳定）
	var road_ids: Array = []
	for l in lanes:
		var rid := int(l.get("road", -1))
		if (int(l.get("from_node", -1)) == node_id or int(l.get("to_node", -1)) == node_id) \
				and not road_ids.has(rid):
			road_ids.append(rid)
	if road_ids.is_empty():
		return {}                              # 孤立节点：没有可复制的路段
	# ② 相关 node：中心 + 每条 road 的另一端
	var node_ids: Array = [node_id]
	for rid in road_ids:
		var r := find_road(int(rid))
		if r.is_empty():
			continue
		for nid in [int(r.a_node), int(r.b_node)]:
			if not node_ids.has(nid):
				node_ids.append(nid)
	var node_idx := {}
	for i in node_ids.size():
		node_idx[int(node_ids[i])] = i
	# ③ 相关 lane（这些 road 的全部车道）
	var lane_ids: Array = []
	for l in lanes:
		if road_ids.has(int(l.get("road", -1))):
			lane_ids.append(int(l.id))
	var lane_idx := {}
	for i in lane_ids.size():
		lane_idx[int(lane_ids[i])] = i
	# ④ 该路口的连接：src/dst 必定在上面这批 lane 里（进口 to_node、出口 from_node 都是中心）
	var turn_ids: Array = []
	for t in turns:
		if int(t.node) == node_id:
			turn_ids.append(int(t.id))
	var turn_idx := {}
	for i in turn_ids.size():
		turn_idx[int(turn_ids[i])] = i
	var cn := node_pos(node_id)
	# ⑤ 节点：存相对中心的偏移（米）
	var sn: Array = []
	for nid in node_ids:
		var n := find_node(int(nid))
		var p := node_pos(int(nid))
		sn.append({
			"type": String(n.get("type", "Unsignalized")),
			"dx": p.x - cn.x, "dy": p.y - cn.y,
		})
	# ⑥ 道路壳（两端用局部 node 序号）
	var sr: Array = []
	for rid in road_ids:
		var r := find_road(int(rid))
		sr.append({
			"a": int(node_idx.get(int(r.a_node), 0)),
			"b": int(node_idx.get(int(r.b_node), 0)),
			"template": (r.get("template", {}) as Dictionary).duplicate(true),
		})
	# ⑦ 车道（road / from / to 全用局部序号）
	var sl: Array = []
	for lid in lane_ids:
		var l := find_lane(int(lid))
		sl.append({
			"road": road_ids.find(int(l.get("road", -1))),
			"from": int(node_idx.get(int(l.get("from_node", -1)), 0)),
			"to": int(node_idx.get(int(l.get("to_node", -1)), 0)),
			"type": String(l.get("type", "Driving")),
			"lane_index": int(l.get("lane_index", 0)),
			"lat_index": int(l.get("lat_index", 0)),
			"length": float(l.get("length", 0.0)),
			"speed_limit": float(l.get("speed_limit", DEFAULT_SPEED)),
		})
	# ⑧ 连接（src/dst 用局部 lane 序号；node 恒为中心，省略）
	var st: Array = []
	for tid in turn_ids:
		var t := find_turn(int(tid))
		st.append({
			"src": int(lane_idx.get(int(t.src_lane), -1)),
			"dst": int(lane_idx.get(int(t.dst_lane), -1)),
			"type": String(t.get("type", "Straight")),
			"ratio": float(t.get("ratio", 1.0)),
		})
	# ⑨ 相位（protected/permitted 换成局部 turn 序号，翻不到的丢掉；只有数值，不含任何实时 id）
	var sig := {}
	var si := find_signal_index(node_id)
	if si >= 0:
		var s: Dictionary = signals[si]
		var plans: Array = []
		for pl in s.get("plans", []):
			var stages: Array = []
			for stg in pl.get("stages", []):
				stages.append({
					"type": String(stg.get("type", "Fixed")),
					"duration": float(stg.get("duration", 0.0)),
					"protected": _to_local_ids(stg.get("protected", []), turn_idx),
					"permitted": _to_local_ids(stg.get("permitted", []), turn_idx),
				})
			plans.append({"start_time": float(pl.get("start_time", 0.0)), "stages": stages})
		sig = {"offset": float(s.get("offset", 0.0)), "plans": plans}
	# ⑩ 需求曲线不随包复制（A4）：这里只统计"源路口本来带了几条"，粘贴时明确告警，
	#    免得粘完看着一切正常、跑起来新路口一辆车都没有（校验器不查"某车道无需求"）。
	var prof := {"demand": 0, "pedestrians": 0, "bicycles": 0}
	for dm in demand:
		if lane_ids.has(int(dm.get("lane", -1))):
			prof["demand"] = int(prof["demand"]) + 1
	for p in pedestrians:
		if turn_ids.has(int(p.get("crosswalk_turn", -1))):
			prof["pedestrians"] = int(prof["pedestrians"]) + 1
	for b in bicycles:
		if lane_ids.has(int(b.get("lane", -1))):
			prof["bicycles"] = int(prof["bicycles"]) + 1
	return {"center": 0, "nodes": sn, "roads": sr, "lanes": sl, "turns": st,
		"signal": sig, "profiles": prof}

## 实时 id → 局部序号（翻不到的直接丢）
static func _to_local_ids(ids, idx: Dictionary) -> Array:
	var out: Array = []
	for v in ids:
		if idx.has(int(v)):
			out.append(int(idx[int(v)]))
	return out

## 局部 turn 序号 → 新 turn id（翻不到 / 建失败的用 -1 占位，后面会被丢掉）
static func _from_local_ids(local_ids, new_turns: Array) -> Array:
	var out: Array = []
	for v in local_ids:
		var i := int(v)
		if i >= 0 and i < new_turns.size() and int(new_turns[i]) >= 0:
			out.append(int(new_turns[i]))
	return out

## 粘贴路口包：在 offset 处重建整包（★ 全量新 id）。返回新中心节点 id；失败 -1。
## ★ 车道照快照【逐条复制】而不是走 create_road 重展开 —— 这里要的是"原样复制一段已有的路"，
##   不是"按模板新建一条路"（后者是 paste_road 的语义）。
## ★ 中心保留原类型；**其它端点一律退回普通节点**（它们在新图里只连 1 条路，留着 Signalized
##   会导出报 signalized_without_phases），只有 Origin / Destination 语义保留。
func paste_junction(payload: Dictionary, offset_m: Vector2) -> int:
	var sn: Array = payload.get("nodes", [])
	if sn.is_empty():
		return -1
	# ① 节点
	var new_nodes: Array = []
	for i in sn.size():
		var d: Dictionary = sn[i]
		var ty := String(d.get("type", "Unsignalized"))
		if i != 0 and ty != "Origin" and ty != "Destination":
			ty = "Unsignalized"
		new_nodes.append(add_node(ty, Vector2(float(d.get("dx", 0.0)), float(d.get("dy", 0.0))) + offset_m))
	var center_new := int(new_nodes[0])
	# ② 道路壳（lanes 先留空，等车道建完再回填）
	var new_roads: Array = []
	var road_lanes: Array = []
	for d in payload.get("roads", []):
		var rid := next_road_id
		next_road_id += 1
		roads.append({
			"road_id": rid,
			"a_node": int(new_nodes[int(d.get("a", 0))]),
			"b_node": int(new_nodes[int(d.get("b", 0))]),
			"lanes": [],
			"template": (d.get("template", {}) as Dictionary).duplicate(true),
		})
		new_roads.append(rid)
		road_lanes.append([])
	# ③ 车道
	var new_lanes: Array = []
	for d in payload.get("lanes", []):
		var ri := int(d.get("road", -1))
		if ri < 0 or ri >= new_roads.size():
			new_lanes.append(-1)
			continue
		var lid := next_lane_id
		next_lane_id += 1
		lanes.append({
			"id": lid,
			"road": int(new_roads[ri]),
			"from_node": int(new_nodes[int(d.get("from", 0))]),
			"to_node": int(new_nodes[int(d.get("to", 0))]),
			"type": String(d.get("type", "Driving")),
			"lane_index": int(d.get("lane_index", 0)),
			"lat_index": int(d.get("lat_index", 0)),
			"length": float(d.get("length", 0.0)),
			"speed_limit": float(d.get("speed_limit", DEFAULT_SPEED)),
		})
		(road_lanes[ri] as Array).append(lid)
		new_lanes.append(lid)
	for i in new_roads.size():
		var r := find_road(int(new_roads[i]))
		if not r.is_empty():
			r["lanes"] = road_lanes[i]
	# ④ 连接（src/dst 换成新 lane；node 一律是新中心）
	var new_turns: Array = []
	for d in payload.get("turns", []):
		var si := int(d.get("src", -1))
		var di := int(d.get("dst", -1))
		if si < 0 or di < 0 or si >= new_lanes.size() or di >= new_lanes.size() \
				or int(new_lanes[si]) < 0 or int(new_lanes[di]) < 0:
			new_turns.append(-1)
			continue
		var tid := add_turn(center_new, int(new_lanes[si]), int(new_lanes[di]),
			String(d.get("type", "Straight")))
		if tid >= 0:
			var t := find_turn(tid)
			t["ratio"] = float(d.get("ratio", 1.0))
		new_turns.append(tid)
	# ⑤ 相位（turn 序号翻成新 id；空阶段丢掉；全丢光 → 绝不留"信号路口没相位"）
	var sig: Dictionary = payload.get("signal", {})
	if not sig.is_empty():
		var plans: Array = []
		for pl in sig.get("plans", []):
			var stages: Array = []
			for stg in pl.get("stages", []):
				var pr := _from_local_ids(stg.get("protected", []), new_turns)
				var pm := _from_local_ids(stg.get("permitted", []), new_turns)
				if pr.is_empty() and pm.is_empty():
					continue
				stages.append({
					"protected": pr, "permitted": pm,
					"type": String(stg.get("type", "Fixed")),
					"duration": float(stg.get("duration", 0.0)),
				})
			if stages.is_empty():
				continue
			plans.append({"start_time": float(pl.get("start_time", 0.0)), "stages": stages})
		if plans.is_empty():
			var cn := find_node(center_new)
			if not cn.is_empty():
				cn["type"] = "Unsignalized"
			push_warning("粘贴路口：相位一条都没搬过来（连接没建成功？）→ 新节点 %d 已退回普通路口" % center_new)
		else:
			signals.append({"node": center_new, "offset": float(sig.get("offset", 0.0)), "plans": plans})
	# ★ 粘完再兜一道（B10）：源数据可能是"Signalized 但没有 signal 记录"（坏文件 / 手工改过）。
	#   这种情况绝不能照样粘成一个"信号路口没相位"，否则导出必被拦。
	var cn2 := find_node(center_new)
	if not cn2.is_empty() and String(cn2.get("type", "")) == "Signalized" \
			and find_signal_index(center_new) < 0:
		cn2["type"] = "Unsignalized"
		push_warning("粘贴路口：源数据是 Signalized 但没有相位记录 → 新节点 %d 已退回普通路口" % center_new)
	# ★ 需求曲线不随路口包复制（A4）：明确告警，别让人以为"连需求一起复制了"
	var pc: Dictionary = payload.get("profiles", {})
	var pc_total := int(pc.get("demand", 0)) + int(pc.get("pedestrians", 0)) + int(pc.get("bicycles", 0))
	if pc_total > 0:
		push_warning("粘贴路口：需求曲线不随路口包复制 —— 源路口有 %d 条机动车 / %d 条行人 / %d 条非机动车需求，请在新路口重新配" % [
			int(pc.get("demand", 0)), int(pc.get("pedestrians", 0)), int(pc.get("bicycles", 0))])
	dirty = true
	recompute_node_types()
	return center_new

## 找离 pos_m 最近、且在 max_dist 米内的 road id；没有返回 -1
func pick_road(pos_m: Vector2, max_dist: float) -> int:
	var best := -1
	var best_d := max_dist
	for r in roads:
		var d := dist_to_seg(pos_m, node_pos(r.a_node), node_pos(r.b_node))
		if d < best_d:
			best_d = d
			best = r.road_id
	return best

static func dist_to_seg(p: Vector2, a: Vector2, b: Vector2) -> float:
	var ab := b - a
	var l2 := ab.length_squared()
	if l2 < 0.0001:
		return p.distance_to(a)
	var t := clampf((p - a).dot(ab) / l2, 0.0, 1.0)
	return p.distance_to(a + ab * t)

# --- 编辑：删除 / 拆分 / 节点拖动 ----------------------------------------

## 删一条路：删它的 lane 和引用这些 lane 的 turn，再清理变成孤儿的端点
func delete_road(road_id: int) -> void:
	var road := find_road(road_id)
	if road.is_empty():
		return
	var ids: Array = road.lanes.duplicate()
	lanes = lanes.filter(func(l): return not ids.has(l.id))
	var gone := _drop_turns_touching(ids)       # ★ 删 turn + 清相位 + 清需求，一次做完
	if gone > 0:
		print("删道路 %d：连带删掉 %d 条连接" % [road_id, gone])
	roads = roads.filter(func(r): return r.road_id != road_id)
	prune_flows() 
	_prune_orphan_nodes([road.a_node, road.b_node])
	_cleanup_if_not_junction(road.a_node)
	_cleanup_if_not_junction(road.b_node)
	recompute_node_types()
	dirty = true


# --- 需求曲线（demand / pedestrians / bicycles，三者同构）------------------

## 能挂 demand 的车道类型（对齐 SimCore 的 IsMotorLane；B21 批量建曲线用）
const MOTOR_LANE_TYPES := ["Driving", "Bus", "SharedLeftTurn"]

## 转向类型的**默认转率权重**（相对值，导出时按进口归一化）。
## ★ 2026-11-05：转率不再用"全 1.0 = 未配置"这套哨兵语义 ——
##   1:1:1 也是合法需求，必须能被表达。所以默认值直接写进 `turns[].ratio`，
##   导出时就读这个值。掉头给 0.02 而不是 0：契约要求 ratio ∈ (0, 1]，
##   填 0 会被校验器判 `turn_ratio_out_of_range`；真要禁止掉头请删掉那条转向。
const DEFAULT_TURN_WEIGHT := {
	"Straight": 6.0,
	"Right": 2.5,
	"Left": 1.5,
	"UTurn": 0.02,
	"Crosswalk": 1.0,
}


## 某类转向的默认转率（已除以 10 让它落进 (0, 1]，便于单条时直接当 1.0 用）
static func default_turn_ratio(type: String) -> float:
	var w: float = float(DEFAULT_TURN_WEIGHT.get(type, 1.0))
	return clampf(w / 10.0, 0.01, 1.0)

## 记录用的"键字段名"：行人挂转向，另两类挂车道
static func profile_key(kind: String) -> String:
	return "crosswalk_turn" if kind == "pedestrians" else "lane"

## kind → 对应数组（返回的是引用，可直接 append）
func profile_array(kind: String) -> Array:
	match kind:
		"demand": return demand
		"pedestrians": return pedestrians
		"bicycles": return bicycles
	return []

## 找一条需求记录；没有返回 {}
func find_profile(kind: String, key_value: int) -> Dictionary:
	var k := profile_key(kind)
	for r in profile_array(kind):
		if int(r.get(k, -1)) == key_value:
			return r
	return {}

## 曲线合法性：非空、两数组等长、time_points 严格递增、flow_rates 非负
static func profile_ok(time_points: Array, flow_rates: Array) -> bool:
	if time_points.is_empty() or time_points.size() != flow_rates.size():
		return false
	for i in time_points.size():
		if float(flow_rates[i]) < 0.0:
			return false
		if i > 0 and float(time_points[i]) <= float(time_points[i - 1]):
			return false
	return true

## 写回 / 新建一条需求曲线（三类共用）。不合法 → 不写并告警，返回 false
func write_profile(kind: String, key_value: int, time_points: Array,
		flow_rates: Array, mode: String = "") -> bool:
	if not profile_ok(time_points, flow_rates):
		push_warning("需求曲线不合法（须非空、等长、时刻严格递增、流率非负），未写入")
		return false
	var rec := find_profile(kind, key_value)
	if rec.is_empty():
		rec = {profile_key(kind): key_value}
		profile_array(kind).append(rec)          # 数组是引用 → 直接追加到 Doc 上
	rec["time_points"] = time_points.duplicate()
	rec["flow_rates"] = flow_rates.duplicate()
	if kind == "bicycles" and mode != "":
		rec["mode"] = mode
	dirty = true
	return true

## 删掉一条需求记录
func remove_profile(kind: String, key_value: int) -> void:
	var k := profile_key(kind)
	var kept: Array = []
	for r in profile_array(kind):
		if int(r.get(k, -1)) != key_value:
			kept.append(r)
	match kind:
		"demand": demand = kept
		"pedestrians": pedestrians = kept
		"bicycles": bicycles = kept
	dirty = true

## ★ 清掉"引用了已消失的车道/转向"的需求记录 —— 删路 / 重建道路 / 删连接后都要调，

##   否则导出必报 dangling_reference。
func prune_flows() -> void:
	var lane_ids := {}
	for l in lanes:
		lane_ids[int(l.id)] = true
	var turn_ids := {}
	for t in turns:
		turn_ids[int(t.id)] = true
	var before := demand.size() + pedestrians.size() + bicycles.size()
	demand = demand.filter(func(d): return lane_ids.has(int(d.get("lane", -1))))
	bicycles = bicycles.filter(func(b): return lane_ids.has(int(b.get("lane", -1))))
	pedestrians = pedestrians.filter(func(p): return turn_ids.has(int(p.get("crosswalk_turn", -1))))
	var gone := before - (demand.size() + pedestrians.size() + bicycles.size())
	if gone > 0:
		print("清理 %d 条失效需求记录（引用的车道/转向已被删除）" % gone)
		dirty = true

# --- 批量建曲线（B21；A6"按比例批量建曲线"）--------------------------------

## 一条需求曲线的"同组"目标 key（**含源自己**，写入幂等）：
##   scope = "road"     → 源所在【同一条路】上与源【同类型】的全部车道
##   scope = "junction" → 源所在【路口】的全部【进口】里与源同类型的车道（进口 = to_node 是该路口）
##   kind  = "pedestrians" → 作用域一律按"路口"算（行人挂在过街转向上，没有"路"这一层）：该路口全部 Crosswalk
## ★ "源所在路口"的取法：源当作进口（to_node 是路口）优先，否则退回 from_node ——
##   出口车道上点批量也能用，不逼用户先挑一条进口。
func profile_group(kind: String, src_key: int, scope: String) -> Array:
	var out: Array = []
	if kind == "pedestrians":
		var t := find_turn(src_key)
		if t.is_empty():
			return out
		var nid := int(t.node)
		for x in turns:
			if int(x.node) != nid or String(x.type) != "Crosswalk":
				continue
			# ★ 二次过街只把【前段】当一条过街（schema §4.4b：需求挂 leg=0）。
			#   不加这一条，批量建需求会把后段也当成独立过街，凭空多出 N 条没人走的行人需求。
			if is_split_turn(x) and int(x.get("leg", 0)) != 0:
				continue
			out.append(int(x.id))
		return out
	var src := find_lane(src_key)
	if src.is_empty():
		return out
	var ty := String(src.get("type", ""))
	if scope == "road":
		for l in lanes:
			if int(l.road) == int(src.road) and String(l.get("type", "")) == ty:
				out.append(int(l.id))
		return out
	var nid := _lane_junction_of(src)
	if nid < 0:
		return out
	for l in lanes:
		if int(l.to_node) == nid and String(l.get("type", "")) == ty:
			out.append(int(l.id))
	return out

## 这条车道"当成进口"挂在哪个路口上（to_node 是路口就用它，否则用 from_node）；两边都不是路口 → -1
func _lane_junction_of(lane: Dictionary) -> int:
	var to_n := int(lane.get("to_node", -1))
	if to_n >= 0 and road_count_at(to_n) >= 2:
		return to_n
	var from_n := int(lane.get("from_node", -1))
	if from_n >= 0 and road_count_at(from_n) >= 2:
		return from_n
	return -1

## 批量建曲线：把源曲线的 time_points / flow_rates 抄给一组 key。
## ★ 只走 write_profile（不另写存储）；源没有曲线 / 曲线不合法 → 一条都不写，返回 0。
## ★ 源自己跳过（本来就有，不必重写）；返回**实际写入**的条数。
func batch_write_profile(kind: String, src_key: int, target_keys: Array) -> int:
	var rec := find_profile(kind, src_key)
	if rec.is_empty():
		return 0
	var times: Array = (rec.get("time_points", []) as Array).duplicate()
	var rates: Array = (rec.get("flow_rates", []) as Array).duplicate()
	if not profile_ok(times, rates):
		return 0
	var mode := String(rec.get("mode", ""))
	var n := 0
	for k in target_keys:
		if int(k) == src_key:
			continue
		if write_profile(kind, int(k), times, rates, mode):
			n += 1
	return n


## 把一批 turn id 从所有相位的 protected/permitted 里摘掉。
## 把某节点当前的手调配时收进暂存袋（只存数值：offset / start_time / 每段 duration）
## ★ 不存 turn id：重建时连接会全部换新 id，旧相位留着必成 dangling。
##   `sig` 可传入已知记录（省一次查找）；传空字典则按 node 现查。返回是否真的存了。
## ★ 用途：① 相位被连接删空时自动暂存（_drop_turns_from_signals）；
##          ② 把信号路口改成普通/起点/终点时"别把用户调好的配时直接扔掉"（A3）。
func stash_signal_tuning(node_id: int, sig: Dictionary = {}) -> bool:
	var s: Dictionary = sig
	if s.is_empty():
		var si := find_signal_index(node_id)
		if si < 0:
			return false
		s = signals[si]
	var old_plans: Array = s.get("plans", [])
	var ds: Array = []
	var st0 := 0.0
	if not old_plans.is_empty():
		var pl0: Dictionary = old_plans[0]
		st0 = float(pl0.get("start_time", 0.0))
		for st in pl0.get("stages", []):
			ds.append(float((st as Dictionary).get("duration", 0.0)))
	signal_tuning_stash[node_id] = {
		"offset": float(s.get("offset", 0.0)),
		"start_time": st0,
		"durations": ds,
	}
	return true

## ★ 摘空的"阶段"整条丢掉；方案里阶段全丢光 → 方案也丢；信号全丢光 → 连节点类型一起退回普通路口
##   （否则会留下"有信号无相位"或"绿灯放行 0 个流向"这种导出时才发现、或根本发现不了的坏数据）
func _drop_turns_from_signals(drop: Array) -> void:
	if drop.is_empty():
		return
	var kept_signals: Array = []
	for s in signals:
		var nid := int(s.get("node", -1))
		var plans: Array = []
		for pl in s.get("plans", []):
			var stages: Array = []
			for st in pl.get("stages", []):
				if st.has("protected"):
					st["protected"] = _drop_ids(st["protected"], drop)
				if st.has("permitted"):
					st["permitted"] = _drop_ids(st["permitted"], drop)
				var pr: Array = st.get("protected", [])
				var pm: Array = st.get("permitted", [])
				if pr.is_empty() and pm.is_empty():
					continue                        # ★ 阶段被摘空 → 丢掉
				stages.append(st)
			if stages.is_empty():
				continue                            # ★ 方案里没阶段了 → 丢掉
			pl["stages"] = stages
			plans.append(pl)
		if plans.is_empty():
			var n := find_node(nid)
			if not n.is_empty():
				n["type"] = "Unsignalized"          # ★ 绝不留"信号路口没相位"
			# ★ 顺手把"数值"收进暂存袋：此刻 protected/permitted 已被摘改，但 duration / offset /
			#   start_time 没被碰，直接读 s 就是手调值。turn id 马上要全换新，旧相位留着必成 dangling
			#   → 只留数值，等连接补回、apply_phases_at 重建时再套回去（编辑器私有，不进 .spmap）。
			stash_signal_tuning(nid, s)
			var kept_d: Array = (signal_tuning_stash[nid] as Dictionary).get("durations", [])
			print("⚠ 节点 %d 的相位已随连接删光 → 已退回普通路口｜配时已暂存（offset=%.1f，%d 段）：连接补回或重新标信号时沿用" % [
				nid, float(s.get("offset", 0.0)), kept_d.size()])
			continue                                # 不保留空信号
		s["plans"] = plans
		kept_signals.append(s)
	signals = kept_signals

static func _drop_ids(ids, drop: Array) -> Array:
	var out: Array = []
	for v in ids:
		if not drop.has(int(v)):
			out.append(int(v))
	return out

## 记下"这批车道连着的、原本是信号路口"的节点，等 end_edit 重推连接与相位（S4-c）
## ★ 必须在删车道【之前】调用：删掉以后就查不到 from_node / to_node 了
func _queue_signal_repair_at(lane_ids: Array) -> void:
	for l in lanes:
		if not lane_ids.has(int(l.id)):
			continue
		for nid in [int(l.from_node), int(l.to_node)]:
			var n := find_node(nid)
			if n.is_empty() or String(n.get("type", "")) != "Signalized":
				continue
			if not signal_repair_queue.has(nid):
				signal_repair_queue.append(nid)


func _drop_turns_touching(lane_ids: Array) -> int:
	var gone: Array = []
	for t in turns:
		if lane_ids.has(int(t.src_lane)) or lane_ids.has(int(t.dst_lane)):
			gone.append(int(t.id))
	if gone.is_empty():
		return 0
	turns = turns.filter(func(t): return not gone.has(int(t.id)))
	_drop_turns_from_signals(gone)
	prune_flows()
	prune_orphan_islands()          # 车道被删 → 过街段没了 → 岛也可能成了孤儿
	return gone.size()


## 删一条连接
func delete_turn(turn_id: int) -> void:
	turns = turns.filter(func(t): return int(t.id) != turn_id)
	_drop_turns_from_signals([turn_id])
	prune_flows()                           # 行人需求挂在这条转向上的，一并清
	prune_orphan_islands()                  # 二次过街只剩一段时，那条段的岛也得清掉
	dirty = true

## 清掉"没有任何转向再引用"的安全岛（N6b）。
## ★ 不清理的话：删掉一段过街 → 岛还在 islands[] 里 → 校验器报 crosswalk_leg_missing，
##   用户看到一条莫名其妙的错误，却找不到该删什么。
func prune_orphan_islands() -> int:
	if islands.is_empty():
		return 0
	var used := {}
	for t in turns:
		if t.has("island"):
			used[int(t["island"])] = true
	var keep: Array = []
	for isl in islands:
		if used.has(int(isl.id)):
			keep.append(isl)
	var dropped := islands.size() - keep.size()
	islands = keep
	return dropped

# --- 行人二次过街 / 中央安全岛（E92 G2 · N6b）----------------------------
#
# 数据口径（schema §4.4b）：一条过街拆成【两条】Crosswalk 转向，带同一个 island id：
#   leg=0 = 路缘 → 岛（前段，行人需求挂这条）
#   leg=1 = 岛 → 对岸（后段）
# 两段共用同一对 src_lane / dst_lane（几何上是同一条弦），只是行人走哪一半不同。
# 岛本身是独立实体（islands[] 的 {id, x, y, capacity}），坐标只用于渲染与校验。

## 找安全岛；没有返回 {}
func find_island(id: int) -> Dictionary:
	for isl in islands:
		if int(isl.id) == id:
			return isl
	return {}

## 一条转向是否已属于二次过街（带 leg）
static func is_split_turn(t: Dictionary) -> bool:
	return t.has("leg") and int(t.get("leg", -1)) >= 0

## 找同一对人行横道的另一段（同 node / 同 src / 同 dst / 同 island、leg 互补）；没有返回 {}
func find_pair_turn(t: Dictionary) -> Dictionary:
	if not is_split_turn(t):
		return {}
	var want := 1 if int(t.get("leg", 0)) == 0 else 0
	for other in turns:
		if int(other.id) == int(t.id):
			continue
		if not is_split_turn(other):
			continue
		if int(other.get("leg", -1)) != want:
			continue
		if int(other.get("island", -1)) != int(t.get("island", -1)):
			continue
		if int(other.node) != int(t.node):
			continue
		if int(other.src_lane) != int(t.src_lane) or int(other.dst_lane) != int(t.dst_lane):
			continue
		return other
	return {}

## 前段转向（leg=0）；传入后段时自动翻到它的配对
func first_leg_turn(t: Dictionary) -> Dictionary:
	if not is_split_turn(t):
		return t
	if int(t.get("leg", 0)) == 0:
		return t
	var pair := find_pair_turn(t)
	return t if pair.is_empty() else pair

## 这条过街的"中点"（世界米）—— 新岛的落位默认取它
## ★ 必须与 SimCore 的弦模型**逐式一致**（`Engine.CrosswalkDistanceOf` 的注释：
##   入口 = N − u_src·D + Right(u_src)·offset、出口 = N + u_dst·D + Right(u_dst)·offset，
##   D = 6.0 米弦深度、Right(v) = (v.Y, −v.X)、offset = (lat_index + 0.5)·3.5）。
##   以前这里按"车道哪一端连节点"硬取端点 —— 那是错的：SimCore 的进出口是**沿车道方向
##   从节点往外退 D 米再横移 offset**，与车道的 from/to 谁挨着节点无关。写错就把岛放到
##   另一侧人行道上（用户报的第 1 个问题）。
func crosswalk_midpoint(t: Dictionary) -> Vector2:
	var s := find_lane(int(t.get("src_lane", -1)))
	var d := find_lane(int(t.get("dst_lane", -1)))
	if s.is_empty() or d.is_empty():
		return Vector2.ZERO
	var n := node_pos(int(t.get("node", -1)))
	var us := _lane_dir(s)
	var ud := _lane_dir(d)
	var so := (float(s.get("lat_index", 0)) + 0.5) * LANE_WIDTH_M
	var dof := (float(d.get("lat_index", 0)) + 0.5) * LANE_WIDTH_M
	# 入口：沿源车道方向【反向】退 D 米（u_src 指向节点），再沿右法向横移
	var entry := n - us * CROSSWALK_CHORD_DEPTH + _right(us) * so
	# 出口：沿目标车道方向【正向】退 D 米（u_dst 背离节点），再沿右法向横移
	var exit := n + ud * CROSSWALK_CHORD_DEPTH + _right(ud) * dof
	return (entry + exit) * 0.5

## 车道行驶方向单位向量（From → To）；零长退化为零向量（与 SimCore LaneDirection 同）
func _lane_dir(l: Dictionary) -> Vector2:
	var v := node_pos(int(l.to_node)) - node_pos(int(l.from_node))
	return Vector2.ZERO if v.length() < 0.000001 else v.normalized()

## 右法向 Right(v) = (v.y, −v.x)（与 SimCore 逐字一致；注意与常见的左法向 (−y, x) 相反）
static func _right(v: Vector2) -> Vector2:
	return Vector2(v.y, -v.x)

## 把一条过街拆成两段 + 建一个安全岛。
## 返回 {"ok": bool, "island": 岛 id, "leg0": 前段 id, "leg1": 后段 id, "error": 原因}
## ★ 已拆过的直接返回既有结果（幂等），不会重复建岛。
func split_crosswalk(turn_id: int, capacity: int = 6, island_pos = null) -> Dictionary:
	var t := find_turn(turn_id)
	if t.is_empty():
		return {"ok": false, "error": "转向 %d 不存在" % turn_id}
	if String(t.get("type", "")) != "Crosswalk":
		return {"ok": false, "error": "只有人行横道（Crosswalk）能设二次过街"}
	if is_split_turn(t):
		var pair := find_pair_turn(t)
		return {"ok": true, "island": int(t.get("island", -1)),
			"leg0": int(first_leg_turn(t).id),
			"leg1": -1 if pair.is_empty() else int(pair.id), "error": ""}

	var cap := maxi(1, capacity)
	var pos: Vector2 = crosswalk_midpoint(t) if island_pos == null else island_pos
	var iid := next_island_id
	next_island_id += 1
	islands.append({"id": iid, "x": pos.x, "y": pos.y, "capacity": cap})

	# 原来这条转向变成前段，另建一条后段（其余字段照抄，保证 ratio / 几何完全一致）
	t["leg"] = 0
	t["island"] = iid
	var leg1 := t.duplicate(true)
	leg1["id"] = next_turn_id
	next_turn_id += 1
	leg1["leg"] = 1
	leg1["island"] = iid
	# ★ 两段不能同时被同一条 phase 放行 —— 但那是配时的事，不在这里改；
	#   这里只保证 id 不撞。插在 turns 末尾，重排交给 renumber。
	turns.append(leg1)

	# 行人需求继续挂前段（schema §4.4b）；万一原本挂在别的 id 上也不动。
	dirty = true
	return {"ok": true, "island": iid, "leg0": int(t.id), "leg1": int(leg1["id"]), "error": ""}

## 把二次过街合并回一条（删后段 + 删岛 + 摘掉 leg/island 字段）。
## ★ 行人需求若挂在后段，会搬到前段去，避免"合并一次需求凭空消失"。
func merge_crosswalk(turn_id: int) -> Dictionary:
	var t := find_turn(turn_id)
	if t.is_empty():
		return {"ok": false, "error": "转向 %d 不存在" % turn_id}
	if not is_split_turn(t):
		return {"ok": true, "error": ""}          # 幂等
	var first := first_leg_turn(t)
	var pair := find_pair_turn(first)
	var iid := int(first.get("island", -1))
	var leg1_id := -1 if pair.is_empty() else int(pair.id)

	# 后段上的行人需求搬到前段
	if leg1_id >= 0:
		for p in pedestrians:
			if int(p.get("crosswalk_turn", -1)) == leg1_id:
				p["crosswalk_turn"] = int(first.id)

	first.erase("leg")
	first.erase("island")
	if leg1_id >= 0:
		var keep: Array = []
		for other in turns:
			if int(other.id) != leg1_id:
				keep.append(other)
		turns = keep
		_drop_turns_from_signals([leg1_id])
	if iid >= 0:
		var keep_isl: Array = []
		for isl in islands:
			if int(isl.id) != iid:
				keep_isl.append(isl)
		islands = keep_isl

	dirty = true
	return {"ok": true, "leg0": int(first.id), "error": ""}

## 改岛的容量（≥1）与位置；返回是否真的改了
func set_island_field(island_id: int, key: String, value) -> bool:
	var isl := find_island(island_id)
	if isl.is_empty():
		return false
	match key:
		"capacity":
			var v := maxi(1, int(value))
			if int(isl.get("capacity", 0)) == v:
				return false
			isl["capacity"] = v
		"x", "y":
			var f := float(value)
			if absf(float(isl.get(key, 0.0)) - f) < 0.0001:
				return false
			isl[key] = f
		_:
			return false
	dirty = true
	return true

# --- 装饰（只写 .spdeco，不参与 .spmap）----------------------------------

## 放一个新装饰实例；返回 id（失败 -1）
func add_deco(asset: String, pos_m: Vector2) -> int:
	if asset == "":
		return -1
	var id := next_deco_id
	next_deco_id += 1
	decos.append({
		"id": id, "asset": asset,
		"x": pos_m.x, "y": pos_m.y,
		"rotation": 0.0, "scale": 1.0,
		"z": 1, "anchor": "center", "tile": false,
		"w": 0, "h": 0,
	})
	dirty = true
	return id

func find_deco(id: int) -> Dictionary:
	for d in decos:
		if int(d.id) == id:
			return d
	return {}

func delete_deco(id: int) -> void:
	decos = decos.filter(func(d): return int(d.id) != id)
	dirty = true

func move_deco(id: int, pos_m: Vector2) -> void:
	var d := find_deco(id)
	if d.is_empty():
		return
	d["x"] = pos_m.x
	d["y"] = pos_m.y

## 装饰：直接改单个字段（rotation / scale / z / anchor / tile / asset / w / h）
func set_deco_field(id: int, key: String, value) -> void:
	var d := find_deco(id)
	if d.is_empty():
		return
	d[key] = value
	dirty = true

## 装饰：旋转（度；结果归一到 [0, 360)）
func rotate_deco(id: int, delta_deg: float) -> void:
	var d := find_deco(id)
	if d.is_empty():
		return
	d["rotation"] = wrapf(float(d.get("rotation", 0.0)) + delta_deg, 0.0, 360.0)
	dirty = true

## 装饰：缩放（加法步进；夹在 [0.1, 10.0]）
func scale_deco(id: int, delta: float) -> void:
	var d := find_deco(id)
	if d.is_empty():
		return
	d["scale"] = clampf(float(d.get("scale", 1.0)) + delta, 0.1, 10.0)
	dirty = true

## 装饰：层级（整数步进；夹在 [-20, 20]，保证装饰永远在道路之上）
func nudge_deco_z(id: int, delta: int) -> void:
	var d := find_deco(id)
	if d.is_empty():
		return
	d["z"] = clampi(int(d.get("z", 1)) + delta, -20, 20)
	dirty = true

## 装饰 → .spdeco.json 的对象（id 在"新造的字典"上重排为 0..N-1，不动实时数据）
func to_deco_dict() -> Dictionary:
	var items: Array = []
	for i in decos.size():
		var d: Dictionary = decos[i]
		items.append({
			"id": i,
			"asset": String(d.get("asset", "")),
			"x": float(d.get("x", 0.0)), "y": float(d.get("y", 0.0)),
			"rotation": float(d.get("rotation", 0.0)),
			"scale": float(d.get("scale", 1.0)),
			"z": int(d.get("z", 1)),
			"anchor": String(d.get("anchor", "center")),
			"tile": bool(d.get("tile", false)),
			"w": int(d.get("w", 0)), "h": int(d.get("h", 0)),
		})
	return {"format_version": 1, "items": items}

## 从 .spdeco.json 文本解析装饰列表 —— 只做"本地 JSON 合法性检查"，
## 不合法就告警 + 退回空数组，绝不炸。
static func decos_from_json_text(txt: String) -> Array:
	var parsed = JSON.parse_string(txt)
	if not (parsed is Dictionary):
		push_warning(".spdeco.json 顶层不是对象，已忽略")
		return []
	var ver := int(parsed.get("format_version", 1))
	if ver != 1:
		push_warning(".spdeco format_version=%d 不受支持（仅支持 1）" % ver)
	var items = parsed.get("items", [])
	if not (items is Array):
		push_warning(".spdeco.json 的 items 不是数组，已忽略")
		return []
	var out: Array = []
	for it in items:
		if not (it is Dictionary):
			push_warning(".spdeco.json 里有一条 item 不是对象，已跳过")
			continue
		var asset := String(it.get("asset", ""))
		if asset == "":
			push_warning(".spdeco.json 里有一条 item 缺 asset，已跳过")
			continue
		out.append({
			"id": out.size(), "asset": asset,               # 统一重排成 0..N-1
			"x": float(it.get("x", 0.0)), "y": float(it.get("y", 0.0)),
			"rotation": float(it.get("rotation", 0.0)),
			"scale": float(it.get("scale", 1.0)),
			"z": int(it.get("z", 1)),
			"anchor": String(it.get("anchor", "center")),
			"tile": bool(it.get("tile", false)),
			"w": int(it.get("w", 0)), "h": int(it.get("h", 0)),
		})
	return out

## 用一份解析好的装饰列表替换当前装饰（导入用）；计数器接到末尾，避免新放的撞号
func set_decos(list: Array) -> void:
	decos = list.duplicate(true)
	next_deco_id = 0
	for d in decos:
		next_deco_id = maxi(next_deco_id, int(d.get("id", -1)) + 1)

## 新增一条连接（进口 → 出口）。type 由调用方给出（取自 SimCore 候选，GDScript 不自判）
## 返回新 turn id；失败/重复返回 -1
func add_turn(node_id: int, src_lane: int, dst_lane: int, type: String) -> int:
	if node_id < 0 or src_lane < 0 or dst_lane < 0 or type == "":
		return -1
	var s := find_lane(src_lane)
	var d := find_lane(dst_lane)
	if s.is_empty() or d.is_empty():
		return -1
	if int(s.to_node) != node_id or int(d.from_node) != node_id:
		return -1                       # 进口/出口与该路口不自洽
	for t in turns:                     # 已存在同一对 → 不重复建
		if int(t.src_lane) == src_lane and int(t.dst_lane) == dst_lane:
			return -1
	# ★ 该进口的第一条转向恒为 1.0（独占全部流量）；之后按类型默认权重给个起手值
	var same_import := 0
	for t in turns:
		if int(t.node) == node_id and int(t.src_lane) == src_lane:
			same_import += 1
	var start_ratio: float = 1.0 if same_import == 0 else default_turn_ratio(type)

	var tid := next_turn_id
	next_turn_id += 1
	turns.append({
		"id": tid, "src_lane": src_lane, "dst_lane": dst_lane,
		"node": node_id, "type": type, "ratio": start_ratio,
	})
	dirty = true
	return tid

## 旧数据迁移：`turns[].ratio` 在契约里是**可选字段**（缺省 1.0），
## 所以 S2 之前存出来的文件根本没有这个键。
## 这里只给"**没有** ratio 字段"的进口补默认权重 —— **有值的绝不覆盖**，
## 否则用户精心配的 1:1:1 会被下次打开吃掉。
## 返回补了多少条（0 = 无需迁移）。
func fill_default_ratios() -> int:
	var groups := {}
	for t in turns:
		if t.has("ratio"):
			continue
		var key := "%d/%d" % [int(t.get("node", -1)), int(t.get("src_lane", -1))]
		if not groups.has(key):
			groups[key] = []
		(groups[key] as Array).append(t)

	var filled := 0
	for key in groups.keys():
		var rows: Array = groups[key]
		if rows.size() < 2:
			continue          # 单条转向恒为 1.0，不用动
		var total := 0.0
		for t in rows:
			total += float(DEFAULT_TURN_WEIGHT.get(String(t.get("type", "Straight")), 1.0))
		if total <= 0.0:
			continue
		for t in rows:
			var w: float = float(DEFAULT_TURN_WEIGHT.get(String(t.get("type", "Straight")), 1.0))
			t["ratio"] = clampf(snappedf(w / total, 0.0001), 0.01, 1.0)
			filled += 1
	if filled > 0:
		print("转率默认值已填入：%d 条转向（旧数据没有 ratio 字段）" % filled)
	return filled


## 删掉"不再被任何 lane 引用"的节点（避免 orphan_node 校验错）
func _prune_orphan_nodes(candidates: Array) -> void:
	for nid in candidates:
		if not _node_in_use(nid):
			nodes = nodes.filter(func(n): return n.id != nid)

func _node_in_use(node_id: int) -> bool:
	for l in lanes:
		if l.from_node == node_id or l.to_node == node_id:
			return true
	return false

## 移动一个节点（拖动中只改坐标；松手时由调用方置 dirty）
func move_node(node_id: int, pos_m: Vector2) -> void:
	for n in nodes:
		if n.id == node_id:
			n.x = pos_m.x
			n.y = pos_m.y
			return

## 重算某条 road 全部 lane 的 length（节点被拖动后调用）
func refresh_road_lengths(road_id: int) -> void:
	var road := find_road(road_id)
	if road.is_empty():
		return
	var length := node_pos(road.a_node).distance_to(node_pos(road.b_node))
	for l in lanes:
		if l.road == road_id:
			l.length = length

## 在一条路上插节点 M，拆成两条 road（R1: A→M，R2: M→B）；返回新节点 id，失败 -1
func split_road(road_id: int, at_m: Vector2) -> int:
	var road := find_road(road_id)
	if road.is_empty():
		return -1
	var a: int = road.a_node
	var b: int = road.b_node
	var tmpl: Dictionary = road.template.duplicate()
	var mid := node_at_or_new(at_m)
	if mid == a or mid == b:
		return -1                      # 插点落在端点上：无意义
	var old_ids: Array = road.lanes.duplicate()
	_queue_signal_repair_at(old_ids)        # ★ 必须在删车道之前
	lanes = lanes.filter(func(l): return not old_ids.has(l.id))
	_drop_turns_touching(old_ids)          # ★ 删 turn + 清相位死引用 + 清需求，一次做完
	roads = roads.filter(func(r): return r.road_id != road_id)
	create_road(a, mid, tmpl)
	create_road(mid, b, tmpl)
	prune_flows() 
	return mid

## 把一条 road 替换成"节点链"构成的 N 条 road（弧线/折线的落地）
## pts_m：含首尾的 n+1 个点（米），首尾即原 road 的两端
## 返回新建的 road id 数组（空 = 失败）
func replace_road_with_chain(road_id: int, pts_m: Array, tmpl: Dictionary) -> Array:
	var road := find_road(road_id)
	if road.is_empty():
		return []
	var n := pts_m.size() - 1
	if n < 1:
		return []
	# ★ 先验：任何一段长度为 0 就整件事不做（B9）。弧线只能落一次，绝不能留下"缺一段"的断口 ——
	#   断口在画布上看着只是少了一段，但拓扑上是两个悬空节点，导出才报 orphan_node。
	for i in range(n):
		var p0: Vector2 = pts_m[i]
		var p1: Vector2 = pts_m[i + 1]
		if p0.distance_to(p1) <= 0.001:
			push_error("弧线取消：第 %d/%d 段长度为 0（两点重合），整条弧线未改动" % [i + 1, n])
			return []
	var snap := snapshot()                  # ★ 兜底：后面任何一步失败都整条回滚
	var a: int = road.a_node
	var b: int = road.b_node
	# 删原路（不清理端点：它们要被复用）
	var old_ids: Array = road.lanes.duplicate()
	_queue_signal_repair_at(old_ids)        # ★ 同上
	lanes = lanes.filter(func(l): return not old_ids.has(l.id))
	_drop_turns_touching(old_ids)          # ★ 删 turn + 清相位死引用 + 清需求，一次做完
	roads = roads.filter(func(r): return r.road_id != road_id)
	# 节点链：首 = a，尾 = b，中间按 pts 新建
	var chain: Array = [a]
	for i in range(1, n):
		chain.append(add_node("Unsignalized", pts_m[i]))
	chain.append(b)
	# N 条 road，相邻共享节点
	var new_ids: Array = []
	for i in range(n):
		var rid := create_road(chain[i], chain[i + 1], tmpl)
		if rid < 0:
			push_error("弧线落段失败：节点 %d → %d（第 %d/%d 段）→ 整条弧线回滚" % [
				chain[i], chain[i + 1], i + 1, n])
			restore(snap)
			return []
		new_ids.append(rid)
	dirty = true
	return new_ids

# --- 导出前：id 重排（0..N-1 连续）---------------------------------------

## 进口道拓宽（E92 G4 / N5）：在 at_node 往另一端 dist_m 处插一个节点，把**靠 at_node 的那一段**
## 换成 near_tmpl，另一段保持原模板。
## ★ 一次调用 = "拆一条 + 建两段"：turn / 相位死引用 / 需求的清理都走既有路径
##   （`_queue_signal_repair_at` + `_drop_turns_touching`），信号路口由调用方 `_drain_signal_repair` 重推。
## ★ 两段的方向保持一致（都是原路的 a→b 方向），否则 fwd/rev 会翻转、车道数算反。
## 返回 {"ok":bool, "msg":String, "mid_node":int, "near_road":int, "far_road":int}
func split_road_near_node(road_id: int, at_node: int, dist_m: float,
		near_tmpl: Dictionary) -> Dictionary:
	var fail := {"ok": false, "msg": "", "mid_node": -1, "near_road": -1, "far_road": -1}
	var road := find_road(road_id)
	if road.is_empty():
		fail["msg"] = "道路不存在"
		return fail
	var a := int(road.a_node)
	var b := int(road.b_node)
	if at_node != a and at_node != b:
		fail["msg"] = "指定的节点不是这条路的两端"
		return fail
	var other := b if at_node == a else a
	var pa := node_pos(at_node)
	var pb := node_pos(other)
	var total := pa.distance_to(pb)
	if dist_m <= 0.5 or dist_m >= total - 0.5:
		fail["msg"] = "拓宽长度 %.1f m 不合法（这条路只有 %.1f m）" % [dist_m, total]
		return fail
	var mid_pos := pa + (pb - pa).normalized() * dist_m
	var mid := find_node_near(mid_pos, NODE_SNAP_M)      # 落在已有节点上就直接复用，别叠一个重合点
	if mid < 0:
		mid = add_node("Unsignalized", mid_pos)

	var old_tmpl: Dictionary = (road.template as Dictionary).duplicate(true)
	var old_ids: Array = road.lanes.duplicate()
	_queue_signal_repair_at(old_ids)
	lanes = lanes.filter(func(l): return not old_ids.has(int(l.id)))
	_drop_turns_touching(old_ids)                        # 删 turn + 清相位死引用 + 清需求，一次做完
	roads = roads.filter(func(r): return r.road_id != road_id)

	var near := -1
	var far := -1
	if at_node == a:
		near = create_road(a, mid, near_tmpl)
		far = create_road(mid, b, old_tmpl)
	else:
		far = create_road(a, mid, old_tmpl)
		near = create_road(mid, b, near_tmpl)
	dirty = true
	recompute_node_types()
	if near < 0 or far < 0:
		fail["msg"] = "落段失败（长度为 0 或节点异常）"
		return fail
	return {"ok": true, "msg": "", "mid_node": mid, "near_road": near, "far_road": far}


## 导出前调用一次：把 nodes/lanes/turns 的 id 重排成 0..N-1 连续，并同步所有引用
## ★ 只能对【导出的深拷贝】调用（to_dict / to_export 内部就是这么做的）。
##   对 live doc 调会把实时 id 打乱（撤销、选中、桥接反查表全靠它）。
## ★ 刻意不重映射 signal_tuning_stash：它的 key 是节点 id，但它是编辑器私有数据、不导出；
##   一旦对 live doc 调了 renumber，stash 会和新 id 错位 —— 这也是"只能跑在副本上"的另一条理由。
func renumber() -> Dictionary:
	var map_node := {}
	for i in nodes.size():
		map_node[nodes[i].id] = i
		nodes[i].id = i
	var map_lane := {}
	for i in lanes.size():
		map_lane[lanes[i].id] = i
		lanes[i].id = i
	var map_turn := {}
	for i in turns.size():
		map_turn[turns[i].id] = i
		turns[i].id = i

	for l in lanes:
		l.from_node = _remap_or_mark(map_node, int(l.from_node), "lanes[].from_node")
		l.to_node = _remap_or_mark(map_node, int(l.to_node), "lanes[].to_node")
	for t in turns:
		t.src_lane = _remap_or_mark(map_lane, int(t.src_lane), "turns[].src_lane")
		t.dst_lane = _remap_or_mark(map_lane, int(t.dst_lane), "turns[].dst_lane")
		t.node = _remap_or_mark(map_node, int(t.node), "turns[].node")
	# Road 是编辑期"壳"，也要跟着重映射
	for r in roads:
		r.a_node = _remap_or_mark(map_node, int(r.a_node), "roads[].a_node")
		r.b_node = _remap_or_mark(map_node, int(r.b_node), "roads[].b_node")
		var ids: Array = []
		for lid in r.lanes:
			if map_lane.has(int(lid)):
				ids.append(int(map_lane[int(lid)]))
			else:
				# ★ 壳里的车道已不存在：跳过（Road 不参与导出），但要留一条可读 warning
				push_warning("renumber：road %d 的 lanes[] 引用了不存在的 lane %d（已跳过）" % [
					int(r.road_id), int(lid)])
		r.lanes = ids
	# ★ 相位里的转向 id 也过 map_turn；引用的节点/转向已消失的记录整条丢弃
	var new_signals: Array = []
	for s in signals:
		var nid := int(s.get("node", -1))
		if not map_node.has(nid):
			continue
		s["node"] = map_node[nid]
		for pl in s.get("plans", []):
			for st in pl.get("stages", []):
				if st.has("protected"):
					st["protected"] = _remap_ids(st["protected"], map_turn)
				if st.has("permitted"):
					st["permitted"] = _remap_ids(st["permitted"], map_turn)
		new_signals.append(s)
	signals = new_signals

	for d in demand:
		d.lane = _remap_or_mark(map_lane, int(d.get("lane", -1)), "demand[].lane")
	for p in pedestrians:
		p.crosswalk_turn = _remap_or_mark(map_turn, int(p.get("crosswalk_turn", -1)),
			"pedestrians[].crosswalk_turn")
	for b in bicycles:
		b.lane = _remap_or_mark(map_lane, int(b.get("lane", -1)), "bicycles[].lane")

	# 安全岛：id 连续重排；turns[].island 跟着过 map_island
	var map_island := {}
	for i in islands.size():
		map_island[islands[i].id] = i
		islands[i].id = i
	for t in turns:
		if t.has("island"):
			t["island"] = _remap_or_mark(map_island, int(t["island"]), "turns[].island")

	# 计数器接在末尾，之后新建的对象不会撞号
	next_node_id = nodes.size()
	next_lane_id = lanes.size()
	next_turn_id = turns.size()
	next_island_id = islands.size()
	return {"node": map_node, "lane": map_lane, "turn": map_turn, "island": map_island}

## 取映射后的新 id；键不存在（引用已损坏）→ 告警 + 返回 -1，
## 让导出被校验器以 dangling_reference 拦下，而不是在这里抛 GDScript 错误。
## ★ 键存在时返回的就是 map 里的原值 —— 正常路径的 id 分配顺序完全不受影响。
static func _remap_or_mark(m: Dictionary, v: int, field: String) -> int:
	if m.has(v):
		return int(m[v])
	push_warning("renumber：%s 引用的 id %d 已不存在（坏引用），已标为 -1 交给校验器报 dangling_reference" % [
		field, v])
	return -1

## 找离 pos_m 不超过 radius、且 id ≠ except_id 的节点；没有返回 -1
func find_node_near_except(pos_m: Vector2, except_id: int, radius: float) -> int:
	for n in nodes:
		if n.id == except_id:
			continue
		if Vector2(n.x, n.y).distance_to(pos_m) <= radius:
			return n.id
	return -1

## 把节点 from_id 并进 to_id（"把两条路的端点拖到一起" → 串成一个路口）
## 合并后若出现自环 road（两端同点）则整条删掉；返回被删掉的 road id 数组
func merge_nodes(from_id: int, to_id: int) -> Array:
	if from_id == to_id:
		return []
	for l in lanes:
		if l.from_node == from_id:
			l.from_node = to_id
		if l.to_node == from_id:
			l.to_node = to_id
	for t in turns:
		if t.node == from_id:
			t.node = to_id
	# ★ Road 壳也必须重映射：否则 road.a_node/b_node 指向已删节点，
	#   node_pos() 找不到会回退成 (0,0)，端点就"跑到原点"了
	for r in roads:
		if r.a_node == from_id:
			r.a_node = to_id
		if r.b_node == from_id:
			r.b_node = to_id
	nodes = nodes.filter(func(n): return n.id != from_id)
	# 清理自环 road（此时 a_node == b_node 才是真的 A→A，lane 长度必为 0）
	var dead: Array = []
	for r in roads:
		if r.a_node == r.b_node:
			dead.append(r.road_id)
	for rid in dead:
		delete_road(rid)
	dirty = true
	recompute_node_types()
	return dead

# --- 导出：Doc → schema JSON --------------------------------------------


## 转成 .spmap 的 JSON 形状（给导出落盘用；★ 只在副本上 renumber）
func to_dict() -> Dictionary:
	_normalize_int_fields()
	var snap: Doc = _export_copy()
	snap.renumber()
	return _schema_dict(snap)

## 导出用：序列化 + 返回"文件 id → 实时 id"的反查表（调桥接时把返回值翻回来）
## ★ 同时给出正向表 `fwd_*`（实时 id → 文件 id）：SUMO 专业档（`.sumo.json`）里存的是**文件 id**，
##   存盘时要按它重排，所以两个方向都得有，别只留反查。
func to_export() -> Dictionary:
	_normalize_int_fields()
	var snap: Doc = _export_copy()
	var fwd: Dictionary = snap.renumber()        # 实时 id → 文件 id
	return {
		"json": JSON.stringify(_schema_dict(snap), "  ", false),
		"inv_node": _invert_map(fwd["node"]),
		"inv_lane": _invert_map(fwd["lane"]),
		"inv_turn": _invert_map(fwd["turn"]),
		"fwd_node": fwd["node"],
		"fwd_lane": fwd["lane"],
		"fwd_turn": fwd["turn"],
		"inv_island": _invert_map(fwd["island"]),
	}

## 只做 schema 形状（to_dict / to_export 共用）
## ★ islands 是【可选】顶层键：没有岛时整段不写（schema §2「缺省不写」）；
##   否则旧关卡一过编辑器就会凭空多出 "islands": []，与"逐字节不变"的承诺不符。
func _schema_dict(s: Doc) -> Dictionary:
	var out := {
		"format_version": 1,
		"meta": s.meta.duplicate(true),
		"nodes": s.nodes.duplicate(true),
		"lanes": s.lanes.duplicate(true),
		"turns": s.turns.duplicate(true),
		"signals": s.signals.duplicate(true),
		"demand": s.demand.duplicate(true),
		"pedestrians": s.pedestrians.duplicate(true),
		"bicycles": s.bicycles.duplicate(true),
		"game_rules": s.game_rules.duplicate(true),
	}
	if not s.islands.is_empty():
		out["islands"] = s.islands.duplicate(true)
	return out

## 把映射表反过来（值 → 键）
static func _invert_map(m: Dictionary) -> Dictionary:
	var out := {}
	for k in m:
		out[m[k]] = k
	return out

## 造一份"深拷贝"快照：在它上面重排/序列化，不动实时数据
func _export_copy() -> Doc:
	var c := Doc.new()
	c.meta = meta.duplicate(true)
	c.nodes = nodes.duplicate(true)
	c.lanes = lanes.duplicate(true)
	c.turns = turns.duplicate(true)
	c.signals = signals.duplicate(true)
	c.demand = demand.duplicate(true)
	c.pedestrians = pedestrians.duplicate(true)
	c.bicycles = bicycles.duplicate(true)
	c.islands = islands.duplicate(true)
	c.game_rules = game_rules.duplicate(true)
	c.next_node_id = next_node_id
	c.next_lane_id = next_lane_id
	c.next_road_id = next_road_id
	c.next_turn_id = next_turn_id
	c.next_island_id = next_island_id
	return c

# --- 导入：schema JSON → Doc（含反展开）---------------------------------

## 从 .spmap 的 JSON 文本构造 Doc；失败返回 null
static func from_json_text(txt: String) -> Doc:
	var d = JSON.parse_string(txt)
	if not (d is Dictionary):
		return null
	var ver := int(d.get("format_version", 1))
	if ver != 1:
		push_warning("format_version=%d 不受支持（本编辑器仅支持 1），可能不兼容" % ver)

	# ★ 结构校验（B11）：下面所有代码都假设"表是数组、元素是对象、signals 的 plans/stages 是数组"。
	#   畸形文件必须在进 Doc 之前挡住，否则 `l.type` / `n.x` 这类属性访问会直接
	#   Invalid access（导入半途崩，还会留下一个半成品 doc）。
	var shape_err := _import_shape_error(d)
	if shape_err != "":
		push_error("导入失败：" + shape_err)
		return null

	var doc := Doc.new()
	doc.meta = d.get("meta", {})
	doc.nodes = d.get("nodes", [])
	doc.lanes = d.get("lanes", [])
	doc.turns = d.get("turns", [])
	doc.signals = d.get("signals", [])
	doc.demand = d.get("demand", [])
	doc.pedestrians = d.get("pedestrians", [])
	doc.bicycles = d.get("bicycles", [])
	doc.islands = d.get("islands", [])
	doc.game_rules = d.get("game_rules", {})
	doc._fill_import_defaults()            # ★ 必填字段 / meta / game_rules 缺键兜底
	doc._normalize_int_fields()
	doc.rebuild_roads()
	doc.fill_missing_lat_index()          # ★ 必须在 rebuild_roads 之后（要用到 template）
	doc.refresh_counters()
	return doc

## 顶层与表的形状校验："" = 通过，否则是给人看的原因（B11）
## ★ 只查"会让后续代码崩掉/静默错位"的类型问题；语义问题交给 SimCore 校验器。
static func _import_shape_error(d: Dictionary) -> String:
	if d.has("meta") and not (d["meta"] is Dictionary):
		return "meta 不是对象"
	if d.has("game_rules") and not (d["game_rules"] is Dictionary):
		return "game_rules 不是对象"
	for key in ["nodes", "lanes", "turns", "signals", "demand", "pedestrians", "bicycles", "islands"]:
		if not d.has(key):
			continue
		var tbl = d[key]
		if not (tbl is Array):
			return "%s 不是数组" % key
		for i in tbl.size():
			if not (tbl[i] is Dictionary):
				return "%s[%d] 不是对象" % [key, i]
	# signals 的嵌套：normalize / 面板 / 导出都直接遍历 plans→stages→protected
	var sigs = d.get("signals", [])
	for i in sigs.size():
		var s: Dictionary = sigs[i]
		if not s.has("plans"):
			continue
		var plans = s["plans"]
		if not (plans is Array):
			return "signals[%d].plans 不是数组" % i
		for j in plans.size():
			var pl = plans[j]
			if not (pl is Dictionary):
				return "signals[%d].plans[%d] 不是对象" % [i, j]
			if not pl.has("stages"):
				continue
			var stages = pl["stages"]
			if not (stages is Array):
				return "signals[%d].plans[%d].stages 不是数组" % [i, j]
			for k in stages.size():
				var st = stages[k]
				if not (st is Dictionary):
					return "signals[%d].plans[%d].stages[%d] 不是对象" % [i, j, k]
				for b in ["protected", "permitted"]:
					if st.has(b) and not (st[b] is Array):
						return "signals[%d].plans[%d].stages[%d].%s 不是数组" % [i, j, k, b]
	return ""

## 导入兜底：把 schema 必填、但文件里可能缺的字段补上默认值（B11）
## ★ 不补的话 rebuild_roads / 面板 / 渲染里 `l.type`、`n.x` 这类属性访问会 Invalid access。
##   缺的是"可选但被代码默认读取"的结构键（meta / game_rules 的子键）也一并补。
func _fill_import_defaults() -> void:
	var dflt := Doc.new()
	if meta is Dictionary:
		for k in dflt.meta:
			if not meta.has(k):
				meta[k] = dflt.meta[k]
	else:
		meta = dflt.meta.duplicate(true)
	if game_rules is Dictionary:
		for k in dflt.game_rules:
			if game_rules.has(k):
				continue
			var v = dflt.game_rules[k]
			if v is Array or v is Dictionary:
				game_rules[k] = v.duplicate(true)
			else:
				game_rules[k] = v
	else:
		game_rules = dflt.game_rules.duplicate(true)
	for n in nodes:
		if not n.has("type"): n["type"] = "Unsignalized"
		if not n.has("x"): n["x"] = 0.0
		if not n.has("y"): n["y"] = 0.0
	for l in lanes:
		if not l.has("type"): l["type"] = "Driving"
		if not l.has("length"): l["length"] = 0.0
		if not l.has("speed_limit"): l["speed_limit"] = DEFAULT_SPEED
	for t in turns:
		if not t.has("type"): t["type"] = "Straight"
	# ★ 刻意不补 `ratio` / `signals.offset` / `signals.plans`：
	#   它们都是【可选】字段（schema §6.2 / §6.1），补上去会让"导入→导出"平白多出字段；
	#   `plans` 缺失本来就是一个非法信号记录，应该让校验器报 signalized_without_phases，而不是伪造空方案。

## JSON 解析出来的数字统一：凡"该是整数"的字段转 int。
## 不转的话再导出会出现 "id": 0.0 这种噪声，且 map 查表可能对不上。
func _normalize_int_fields() -> void:
	if meta.has("chapter"):
		meta["chapter"] = int(meta["chapter"])
	if meta.has("index"):
		meta["index"] = int(meta["index"])
	for n in nodes:
		n["id"] = int(n.get("id", 0))
	for l in lanes:
		l["id"] = int(l.get("id", 0))
		l["road"] = int(l.get("road", 0))
		l["from_node"] = int(l.get("from_node", 0))
		l["to_node"] = int(l.get("to_node", 0))
		l["lane_index"] = int(l.get("lane_index", 0))
		l["lat_index"] = int(l.get("lat_index", -1))
	for t in turns:
		t["id"] = int(t.get("id", 0))
		t["src_lane"] = int(t.get("src_lane", 0))
		t["dst_lane"] = int(t.get("dst_lane", 0))
		t["node"] = int(t.get("node", 0))
	for s in signals:
		s["node"] = int(s.get("node", 0))
		for pl in s.get("plans", []):
			for st in pl.get("stages", []):
				for k in ["protected", "permitted"]:
					if st.has(k):
						var arr: Array = st[k]
						for i in arr.size():
							arr[i] = int(arr[i])
	for dm in demand:
		dm["lane"] = int(dm.get("lane", 0))
	for p in pedestrians:
		p["crosswalk_turn"] = int(p.get("crosswalk_turn", 0))
	for b in bicycles:
		b["lane"] = int(b.get("lane", 0))
	# ★ islands：只有 id / capacity 必须取整；x / y 是米，保留浮点。
	#   缺 capacity 不补默认值 —— 那是一个非法岛，应让校验器报 island_capacity_invalid。
	for isl in islands:
		isl["id"] = int(isl.get("id", 0))
		if isl.has("capacity"):
			isl["capacity"] = int(isl["capacity"])
	# ★ legs：只规范化"有值"的两条；没有就不写（schema 规定成对出现、缺省不写）
	for t in turns:
		if t.has("leg"):
			t["leg"] = int(t["leg"])
		if t.has("island"):
			t["island"] = int(t["island"])

## 从 lanes[] 反展开：按 road 分组重建 Road 壳（★ 只建壳，不改动 lanes 本身）
func rebuild_roads() -> void:
	roads = []
	var groups := {}
	var order: Array = []
	for l in lanes:
		var rid := int(l.road)
		if not groups.has(rid):
			groups[rid] = []
			order.append(rid)
		groups[rid].append(l)

	for rid in order:
		var group: Array = groups[rid]
		if group.is_empty():
			continue
		# ★ 参考方向优先取"第一条机动车道"（Driving / Bus），其次才是第一条车道（B2）：
		#   人行道/非机动车道排在前面时，不能让它们决定"正向"，否则正/反向车道数会整体对调。
		var ref: Dictionary = group[0]
		for l in group:
			if String(l.type) == "Driving" or String(l.type) == "Bus":
				ref = l
				break
		var a := int(ref.from_node)
		var b := int(ref.to_node)
		if a == b:
			continue                            # 非法数据：跳过，交给校验器报

		var tmpl := default_template()
		tmpl.lanes_fwd = 0
		tmpl.lanes_rev = 0
		tmpl.sidewalk = false
		tmpl.bus = false
		tmpl.bike = false
		tmpl.left_turn_fwd = false                  # ★ 从 lanes 反推（G1），按方向各算各的
		tmpl.left_turn_rev = false
		for l in group:
			var f := int(l.from_node)
			var t := int(l.to_node)
			var rev := (f == b and t == a)
			match String(l.type):
				"Driving":
					if rev:
						tmpl.lanes_rev += 1
					else:
						tmpl.lanes_fwd += 1
					tmpl.speed_limit = float(l.speed_limit)
				"Bus":
					tmpl.bus = true             # ★ 模板的这几个开关是"每向 +1"：任一方向有就算有
				"Biking":
					tmpl.bike = true
				"Sidewalk":
					tmpl.sidewalk = true
				"SharedLeftTurn":
					if rev:
						tmpl.left_turn_rev = true   # ★ 左转专用道（G1）：按方向记
					else:
						tmpl.left_turn_fwd = true
		# ★ 归一到"正向有车"（B2）：只有反向 Driving 时交换 a/b 与两个计数。
		#   不归一会得到 lanes_fwd=0 + oneway=true，之后改任何横断面字段就展开出 0 条车道。
		if tmpl.lanes_fwd == 0 and tmpl.lanes_rev > 0:
			var sw := a
			a = b
			b = sw
			var n: int = int(tmpl.lanes_fwd)
			tmpl.lanes_fwd = tmpl.lanes_rev
			tmpl.lanes_rev = n
		tmpl.oneway = tmpl.lanes_rev == 0       # 反向一条机动车道都没有 → 单向

		roads.append({
			"road_id": rid,
			"a_node": a,
			"b_node": b,
			"lanes": group.map(func(l): return int(l.id)),
			"template": tmpl,
		})

# --- 快照（给 EditorApp 的撤销栈用）--------------------------------------

## 整份"编辑期模型"深拷贝一份（★ 必须 duplicate(true)，浅拷贝会让撤销失效）
func snapshot() -> Dictionary:
	return {
		"nodes": nodes.duplicate(true), "roads": roads.duplicate(true),
		"lanes": lanes.duplicate(true), "turns": turns.duplicate(true),
		"signals": signals.duplicate(true), "demand": demand.duplicate(true),
		"pedestrians": pedestrians.duplicate(true), "bicycles": bicycles.duplicate(true),
		"islands": islands.duplicate(true),
		"decos": decos.duplicate(true),
		"meta": meta.duplicate(true), "game_rules": game_rules.duplicate(true),
		"signal_repair_queue": signal_repair_queue.duplicate(),
		"signal_tuning_stash": signal_tuning_stash.duplicate(true),
		"dirty": dirty,                              # ★ 连带脏标记一起存（撤销回"刚存过"就不该带 *）
		"next_node_id": next_node_id, "next_lane_id": next_lane_id,
		"next_road_id": next_road_id, "next_turn_id": next_turn_id,
		"next_deco_id": next_deco_id, "next_island_id": next_island_id,
	}

## 用快照覆盖当前模型（撤销 / 重做）
func restore(s: Dictionary) -> void:
	nodes = (s.get("nodes", []) as Array).duplicate(true)
	roads = (s.get("roads", []) as Array).duplicate(true)
	lanes = (s.get("lanes", []) as Array).duplicate(true)
	turns = (s.get("turns", []) as Array).duplicate(true)
	signals = (s.get("signals", []) as Array).duplicate(true)
	demand = (s.get("demand", []) as Array).duplicate(true)
	pedestrians = (s.get("pedestrians", []) as Array).duplicate(true)
	bicycles = (s.get("bicycles", []) as Array).duplicate(true)
	islands = (s.get("islands", []) as Array).duplicate(true)
	decos = (s.get("decos", []) as Array).duplicate(true)
	meta = (s.get("meta", {}) as Dictionary).duplicate(true)
	game_rules = (s.get("game_rules", {}) as Dictionary).duplicate(true)
	signal_repair_queue = (s.get("signal_repair_queue", []) as Array).duplicate()
	signal_tuning_stash = (s.get("signal_tuning_stash", {}) as Dictionary).duplicate(true)
	next_node_id = int(s.get("next_node_id", 0))
	next_lane_id = int(s.get("next_lane_id", 0))
	next_road_id = int(s.get("next_road_id", 0))
	next_turn_id = int(s.get("next_turn_id", 0))
	next_deco_id = int(s.get("next_deco_id", 0))
	next_island_id = int(s.get("next_island_id", 0))
	# ★ 脏标记以快照为准：撤销回"刚存过"的那一步 → * 消失；重做到有改动的那一步 → * 回来
	#   （旧快照没有这个键 → 按 true 兜底，与旧行为一致）
	dirty = bool(s.get("dirty", true))

## 由快照造回一个 Doc
static func from_snapshot(s: Dictionary) -> Doc:
	var d := Doc.new()
	d.restore(s)
	return d

## 把自增计数器抬到"已用 id 的最大值 + 1"，避免新建对象撞号
func refresh_counters() -> void:
	next_node_id = 0
	next_lane_id = 0
	next_turn_id = 0
	next_road_id = 0
	next_deco_id = 0
	next_island_id = 0
	for n in nodes:
		next_node_id = maxi(next_node_id, int(n.id) + 1)
	for l in lanes:
		next_lane_id = maxi(next_lane_id, int(l.id) + 1)
	for t in turns:
		next_turn_id = maxi(next_turn_id, int(t.id) + 1)
	for r in roads:
		next_road_id = maxi(next_road_id, int(r.road_id) + 1)
	for d in decos:
		next_deco_id = maxi(next_deco_id, int(d.id) + 1)
	for isl in islands:
		next_island_id = maxi(next_island_id, int(isl.id) + 1)

## 深比较两份 JSON 值是否语义等价（数字按容差比，忽略 int/float 差异）
static func json_equal(a, b, eps: float = 1e-6) -> bool:
	if a is Dictionary and b is Dictionary:
		if a.size() != b.size():
			return false
		for k in a:
			if not b.has(k) or not json_equal(a[k], b[k], eps):
				return false
		return true
	if a is Array and b is Array:
		if a.size() != b.size():
			return false
		for i in a.size():
			if not json_equal(a[i], b[i], eps):
				return false
		return true
	# ★ 数字要单独处理：两边都是数字按容差比；**一边数字一边不是**直接判不等。
	#   脏数据（如手改出的 `game_rules.anchors: 5`）与快照里的 Dictionary 进 `a == b`
	#   会报 "Invalid operands 'int' and 'Dictionary'"，撤销/重做一路都崩。
	var a_num: bool = a is float or a is int
	var b_num: bool = b is float or b is int
	if a_num or b_num:
		return a_num and b_num and absf(float(a) - float(b)) <= eps
	if typeof(a) != typeof(b):
		return false          # String vs Dictionary 这种直接进 `==` 一样会报 Invalid operands
	return a == b

## 比两份"快照"的内容是否等价 —— 忽略 dirty（脏标记只影响标题栏，不算一次改动）。
## ★ 用它替代 json_equal(snapshot, snapshot)：不必为了忽略一个键把整份模型多深拷贝两遍
static func snapshot_equal(a: Dictionary, b: Dictionary) -> bool:
	if a.size() != b.size():
		return false
	for k in a:
		if k == "dirty":
			continue
		if not b.has(k) or not json_equal(a[k], b[k]):
			return false
	return true

# --- 路口：节点 / 车道查询 -----------------------------------------------

func find_lane(lane_id: int) -> Dictionary:
	for l in lanes:
		if int(l.id) == lane_id:
			return l
	return {}

func find_node(node_id: int) -> Dictionary:
	for n in nodes:
		if int(n.id) == node_id:
			return n
	return {}

func find_turn(turn_id: int) -> Dictionary:
	for t in turns:
		if int(t.id) == turn_id:
			return t
	return {}

## 该节点被几条"路"连着（按 road 去重）
func road_count_at(node_id: int) -> int:
	var seen := {}
	for l in lanes:
		if int(l.from_node) == node_id or int(l.to_node) == node_id:
			seen[int(l.road)] = true
	return seen.size()

## 只有连 ≥2 条路的节点才算路口，才允许标信号
func can_be_signalized(node_id: int) -> bool:
	return road_count_at(node_id) >= 2

func remove_signal(node_id: int) -> void:
	signals = signals.filter(func(s): return int(s.node) != node_id)

## 该节点的信号记录下标（没有 = -1）。★ "原地改相位"靠它定位，不许 remove_signal 再 append
func find_signal_index(node_id: int) -> int:
	for i in signals.size():
		if int(signals[i].get("node", -1)) == node_id:
			return i
	return -1

# --- 时变配时：方案增删（E92 T2 / N4）--------------------------------------
## ★ plans[] 的运行期语义（SimCore `SignalStateMachine.SelectPlanIndex`）：
##   取 `start_time ≤ 当前时刻` 里 start_time **最大**的那个方案 —— **与数组顺序无关**。
##   所以这里不强制排序：改 start_time 不会让面板里的方案跳位；新增时插到时间顺序的位置
##   只是为了人看着顺眼。
## ★ plans[0] 是默认方案，不许删（删空之后校验器直接报 signalized_without_phases）。

## 新增方案：复制 plans[0] 的阶段结构（深拷贝；turn id 原样沿用）
## 返回新方案的下标（按 start_time 插入后的位置）；失败 -1
func add_signal_plan(node_id: int, start_time: float) -> int:
	var si := find_signal_index(node_id)
	if si < 0:
		return -1
	var plans: Array = signals[si].get("plans", [])
	if plans.is_empty():
		return -1
	var src_stages: Array = plans[0].get("stages", [])
	if src_stages.is_empty():
		return -1                    # 没有阶段可复制 → 交给校验器报"没有相位"
	var t := maxf(0.0, start_time)
	var plan := {"start_time": t, "stages": src_stages.duplicate(true)}
	var at := plans.size()
	for i in plans.size():
		if t < float(plans[i].get("start_time", 0.0)):
			at = i
			break
	plans.insert(at, plan)
	signals[si]["plans"] = plans
	dirty = true
	return at

## 删除方案：只允许 plans[1..]（默认方案必须留着）
func remove_signal_plan(node_id: int, plan_i: int) -> bool:
	var si := find_signal_index(node_id)
	if si < 0:
		return false
	var plans: Array = signals[si].get("plans", [])
	if plan_i <= 0 or plan_i >= plans.size():
		return false
	plans.remove_at(plan_i)
	signals[si]["plans"] = plans
	dirty = true
	return true

## 改某个方案的生效时刻。plan 0 固定从 0 s 起（不允许改，免得"第一段不从开局算"）
func set_signal_plan_start(node_id: int, plan_i: int, start_time: float) -> bool:
	var si := find_signal_index(node_id)
	if si < 0:
		return false
	var plans: Array = signals[si].get("plans", [])
	if plan_i <= 0 or plan_i >= plans.size():
		return false
	plans[plan_i]["start_time"] = maxf(0.0, start_time)
	dirty = true
	return true

## 取出并删掉某节点的配时暂存（没有 → 空字典）；重建相位时只消费一次
func take_tuning_stash(node_id: int) -> Dictionary:
	if not signal_tuning_stash.has(node_id):
		return {}
	var d: Dictionary = signal_tuning_stash[node_id]
	signal_tuning_stash.erase(node_id)
	return d

## 只补不覆盖：把"该路口还没被任何阶段引用"的连接并进【已有】相位。
##   ★ 只追加到每个方案的【最后一个阶段】的 permitted；绝不进 protected、
##     不改 duration、不动阶段顺序、不碰 offset / start_time（手调配时靠这条活下来）。
##   为什么是最后一个阶段：末段通常是"让行 / 次要流向"，新连接放这儿最不容易被当主直行绿灯放出去；
##   万一放错，在配时面板里拖一下就能改。
## 返回 {"added": int, "stage": int, "plans": int}：
##   added = -1 → 该节点没有信号记录（调用方按"重建"处理）
##   stage      → 最后一个方案里被追加的阶段序号（1 起）；0 = 没有任何阶段可用（异常数据）
func upsert_signal_stage_turns(node_id: int, turn_ids: Array) -> Dictionary:
	var si := find_signal_index(node_id)
	if si < 0:
		return {"added": -1, "stage": 0, "plans": 0}
	var sig: Dictionary = signals[si]
	var plans: Array = sig.get("plans", [])
	# 1) 已被任何阶段（protected 或 permitted）引用的 id 都算"有归属"，一个都不动
	var covered := {}
	for pl in plans:
		for st in pl.get("stages", []):
			for k in ["protected", "permitted"]:
				for v in st.get(k, []):
					covered[int(v)] = true
	var fresh: Array = []
	for v in turn_ids:
		var tid := int(v)
		if covered.has(tid):
			continue
		fresh.append(tid)
		covered[tid] = true
	# 2) 每个方案的最后一个阶段收下这批连接
	#    （多个方案=时变配时时逐方案补，否则某些时段会"放行 0 个流向"）
	var stage_no := 0
	for pl in plans:
		var stages: Array = pl.get("stages", [])
		if stages.is_empty():
			continue
		var last: Dictionary = stages[stages.size() - 1]
		var pm: Array = last.get("permitted", [])
		for tid in fresh:
			if not pm.has(tid):
				pm.append(tid)
		last["permitted"] = pm
		stage_no = stages.size()
	return {"added": fresh.size(), "stage": stage_no, "plans": plans.size()}

## 阶段结构编辑（一次调用 = 一步操作）：op = "up" / "down" / "insert" / "delete"
##   返回操作后该阶段的新下标；失败 = -1（含"只剩 1 段不许删"）。
##   ★ insert 是"在 stage_i 下面插一个"：默认 duration=25、type="Fixed"、两列都空。
func signal_stage_op(node_id: int, plan_i: int, stage_i: int, op: String) -> int:
	var si := find_signal_index(node_id)
	if si < 0:
		return -1
	var plans: Array = signals[si].get("plans", [])
	if plan_i < 0 or plan_i >= plans.size():
		return -1
	var stages: Array = plans[plan_i].get("stages", [])
	var n := stages.size()
	match op:
		"up":
			if stage_i <= 0 or stage_i >= n:
				return -1
			var a = stages[stage_i - 1]
			stages[stage_i - 1] = stages[stage_i]
			stages[stage_i] = a
			return stage_i - 1
		"down":
			if stage_i < 0 or stage_i >= n - 1:
				return -1
			var b = stages[stage_i + 1]
			stages[stage_i + 1] = stages[stage_i]
			stages[stage_i] = b
			return stage_i + 1
		"insert":
			if stage_i < -1 or stage_i >= n:
				return -1
			stages.insert(stage_i + 1, {
				"protected": [], "permitted": [], "type": "Fixed", "duration": 25.0,
			})
			return stage_i + 1
		"delete":
			if n <= 1:
				print("节点 %d：只剩 1 个阶段，不能删（至少要留一段绿灯）" % node_id)
				return -1
			if stage_i < 0 or stage_i >= n:
				return -1
			stages.remove_at(stage_i)
			return mini(stage_i, stages.size() - 1)
	return -1

## 勾选某阶段的一个 turn：bucket = "protected" / "permitted" / ""（取消勾选）
##   ★ 同一个 turn 只能在一列 —— 写 protected 就把它从 permitted 摘掉，反之亦然；
##     turn_id 是**实时 id**，落盘时由 renumber() 统一翻成文件 id。
func signal_stage_set_turn(node_id: int, plan_i: int, stage_i: int, turn_id: int, bucket: String) -> bool:
	# ★ 只许写本路口的 turn（B5）：跨节点写入会造出"相位放行别的路口流向"的文件，
	#   而校验器不查这条 → 到游戏里才变成莫名其妙的全绿/全红。面板只列本节点，这是防外部调用。
	var own := find_turn(turn_id)
	if own.is_empty() or int(own.get("node", -1)) != node_id:
		push_warning("节点 %d：连接 %d 不属于这个路口，拒绝写入相位" % [node_id, turn_id])
		return false
	var si := find_signal_index(node_id)
	if si < 0:
		return false
	var plans: Array = signals[si].get("plans", [])
	if plan_i < 0 or plan_i >= plans.size():
		return false
	var stages: Array = plans[plan_i].get("stages", [])
	if stage_i < 0 or stage_i >= stages.size():
		return false
	var st: Dictionary = stages[stage_i]
	for k in ["protected", "permitted"]:
		var arr: Array = st.get(k, [])
		if k == bucket:
			if not arr.has(turn_id):
				arr.append(turn_id)
		else:
			while arr.has(turn_id):
				arr.erase(turn_id)
		st[k] = arr
	return true

## 把一组 id 按映射重排，映射里没有的（对象已被删）直接丢掉
static func _remap_ids(ids, m: Dictionary) -> Array:
	var out: Array = []
	for v in ids:
		var k := int(v)
		if m.has(k):
			out.append(m[k])
	return out

## 节点不再是路口（<2 条路）时：清掉它的全部连接与相位，并降回普通节点
func _cleanup_if_not_junction(node_id: int) -> void:
	if road_count_at(node_id) >= 2:
		return
	turns = turns.filter(func(t): return int(t.node) != node_id)
	remove_signal(node_id)
	var n := find_node(node_id)
	if not n.is_empty() and String(n.type) == "Signalized":
		n["type"] = "Unsignalized"
	dirty = true

## 按"连了几条路"重算节点角色（★ 增删道路后调用；导入/导出路径里绝不要调，见 E04 坑）
##   ≥2 条路：还挂着 Origin/Destination 的一律收回 Unsignalized（Signalized / Unsignalized 不动）
##   自由端 ：不做任何自动标注 —— 默认保持 Unsignalized，由用户在面板里手标。
##   （理由：双向路的两端天然"既进又出"，单值枚举表达不了；Origin/Destination 是
##    "需求源汇"的粗粒度设计标注，对 SimCore 零影响。详见 docs/kb/editor/E04-路口与连接.md）
func recompute_node_types() -> void:
	var changed := false
	for n in nodes:
		var t := String(n.type)
		if (t == "Origin" or t == "Destination") and road_count_at(int(n.id)) >= 2:
			n["type"] = "Unsignalized"
			changed = true
	if changed:
		dirty = true

## 横向排位（0 = 贴中心线，向外递增）：优先读文件里的 lat_index，旧文件按横断面模板回退
static func lane_lat_rank(lane: Dictionary, road: Dictionary) -> int:
	var li := int(lane.get("lat_index", -1))
	if li >= 0:
		return li
	var t: Dictionary = road.get("template", {})
	var forward: bool = int(lane.get("from_node", -1)) == int(road.get("a_node", -2))
	var n_drv: int = int(t.get("lanes_fwd", 1)) if forward else int(t.get("lanes_rev", 1))
	var has_bus: bool = bool(t.get("bus", false))
	var has_bike: bool = bool(t.get("bike", false))
	# 左转道占最内侧一位（G1）：按方向各取各的开关（GDScript 不支持反斜杠续行，键名走三元）
	var has_lt: bool = bool(t.get("left_turn_fwd" if forward else "left_turn_rev", false))
	var inner: int = 1 if has_lt else 0
	match String(lane.get("type", "")):
		"SharedLeftTurn": return 0                        # 永远最内侧
		"Driving": return int(lane.get("lane_index", 0))
		"Bus": return n_drv + inner
		"Biking": return n_drv + inner + (1 if has_bus else 0)
		"Sidewalk": return n_drv + inner + (1 if has_bus else 0) + (1 if has_bike else 0)
	return 0

## 导入后给缺 lat_index 的旧车道回填 —— 导出后 Game 不必再推公式
func fill_missing_lat_index() -> void:
	for l in lanes:
		if int(l.get("lat_index", -1)) >= 0:
			continue
		var r := find_road(int(l.get("road", -1)))
		if r.is_empty():
			continue
		l["lat_index"] = lane_lat_rank(l, r)
