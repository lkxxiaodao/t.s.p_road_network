class_name ConnectMode
extends RefCounted

## 连接模式：
##   没选路口时：**只能点节点**（点中 = 选中该路口；点空白 = 什么都不变）。
##   已选路口时：点【进口/出口】车道 = 手动画一条连接（两点成一条）；
##               点**本路口自己的**转向曲线 = 选中该连接（别的路口的曲线点不中）；
##               点另一个节点 = 换选那个路口；点**路口范围内**的空白 = 只清连接（路口保留）；
##               点**路口外**的空白 = 全部取消。
##   G = 给选中路口自动生成连接；Esc = 取消半途的手动画连接；Delete 删连接由 GridCanvas 统一处理。
##
## ★ 2026-10-28 老板定案（交互修订）：
##   ① **只有连接模式能选中路口**（道路/车辆/装饰模式不再能选中路口，切换模式自动清空选中）；
##   ② **必须先选中路口，才能选中它的连接线** —— 以前"车道→岛→曲线→节点"一把抓，
##      点路口十次有八次被曲线或车道抢走，路口几乎点不中；
##   ③ 选中路口后**保留**路口选中态再去选连接线（面板显示连接，但画布上的路口高亮与
##      进口/出口提示不断），这样"从哪个路口选出来的"一直看得见。

const PICK_PX := 10.0      ## 点中转向曲线的容差（屏幕像素）
const NODE_PX := 16.0      ## 点中路口的容差（屏幕像素）—— 2026-10-28 从 12 放大：路口要更好点
const LANE_PX := 10.0      ## 点中车道的容差（屏幕像素）
const ISLAND_PX := 4.0     ## 点中安全岛的容差（屏幕像素；岛本身有半径，这里只给一点点余量）

func handle(event: InputEvent, world_m: Vector2, canvas: GridCanvas, doc: Doc) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_G:
			EditorApp.generate_connections()
		elif event.keycode == KEY_ESCAPE:
			EditorApp.cancel_pending()
		return
	if not (event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT and event.pressed):
		return

	# ── 第一段：还没选路口 → 只认节点 ─────────────────────────────────────
	var jid: int = EditorApp.selected_junction
	if jid < 0:
		var nid0 := doc.find_node_near(world_m, canvas.screen_px_to_m(NODE_PX))
		if nid0 < 0:
			return                                  # 点空白：不改选中（免得手滑清掉）
		EditorApp.select_island(-1)
		EditorApp.select_turn(-1)
		EditorApp.select_junction(nid0)
		return

	# ── 第二段：已选路口 → 车道（手动画连接）→ 安全岛 → **本路口的**曲线 → 换路口 → 空白 ──
	if doc.can_be_signalized(jid):
		var lid := canvas.pick_lane(world_m, doc, canvas.screen_px_to_m(LANE_PX))
		if lid >= 0:
			var l := doc.find_lane(lid)
			if not l.is_empty() and (int(l.to_node) == jid or int(l.from_node) == jid):
				EditorApp.on_lane_clicked(lid)
				return

	# 安全岛（E92 G2 · N6b）：必须排在转向曲线之前 —— 岛落在过街线中点上，与那条 Crosswalk 曲线重叠，
	# 先判曲线的话永远点不中岛。★ 只认"属于本路口那条过街"的岛。
	var iid := canvas.pick_island(world_m, doc, canvas.screen_px_to_m(ISLAND_PX))
	if iid >= 0 and _island_at_junction(doc, iid, jid):
		EditorApp.select_turn(-1)
		EditorApp.select_island(iid)
		return

	# 连接曲线：只认本路口的（别人的曲线现在点不中，必须先切到那个路口）
	var tid := canvas.pick_turn(world_m, doc, canvas.screen_px_to_m(PICK_PX), jid)
	if tid >= 0:
		EditorApp.select_island(-1)
		EditorApp.select_turn(tid)
		return

	# 换一个路口
	var nid := doc.find_node_near(world_m, canvas.screen_px_to_m(NODE_PX))
	if nid >= 0:
		EditorApp.select_island(-1)
		EditorApp.select_turn(-1)
		EditorApp.select_junction(nid)
		return

	# 空白：**还在本路口范围内就只清连接/岛，路口选中保留**（2026-10-28 老板：想点连接线没点中就退出路口，太恼人）。
	# 真要在路口里退出 → 点路口外面；或 Esc / 切模式（都走各自那条路）。
	if canvas.point_in_junction(world_m, doc, jid, canvas.screen_px_to_m(PICK_PX)):
		EditorApp.select_island(-1)
		EditorApp.select_turn(-1)
		return
	# 出了路口 → 全清
	EditorApp.select_island(-1)
	EditorApp.select_turn(-1)
	EditorApp.select_junction(-1)


## 这个岛是不是挂在"该路口的某条过街"上（二次过街的岛只有这一种挂法）
func _island_at_junction(doc: Doc, island_id: int, node_id: int) -> bool:
	for t in doc.turns:
		if int(t.get("island", -1)) == island_id and int(t.get("node", -1)) == node_id:
			return true
	return false
