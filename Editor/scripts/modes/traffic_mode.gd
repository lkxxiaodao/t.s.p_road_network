class_name TrafficMode
extends RefCounted

## 车辆模式：
##   点一条 Driving / Biking 车道 = 选中它（右侧配机动车流 / 非机动车流）；
##   点一条 Crosswalk 转向 = 选中它（右侧配行人过街需求）；
##   点空白 = 取消选中。车型本期不做（留位灰显）。

const PICK_PX := 10.0      ## 点中车道 / 转向的容差（屏幕像素）

func handle(event: InputEvent, world_m: Vector2, canvas: GridCanvas, doc: Doc) -> void:
	if not (event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT and event.pressed):
		return
	var tol := canvas.screen_px_to_m(PICK_PX)

	# 1) 先试"过街连接"（人行横道转向）—— 它画在路口里，优先命中
	var tid := canvas.pick_turn(world_m, doc, tol)
	if tid >= 0:
		var t := doc.find_turn(tid)
		if String(t.type) == "Crosswalk":
			EditorApp.select_facility("")
			EditorApp.select_traffic_turn(tid)
			print("车辆模式：选中过街连接 %d（配行人需求）" % tid)
		else:
			print("转向 %d 是 %s，不是人行横道 —— 行人需求只能挂 Crosswalk 转向" % [tid, String(t.type)])
		return                 # ★ 点到转向就不算"点空白"，保持当前选中

	# 2) 检测设施（S3）：画在车道中线上，**必须排在车道之前**，
	#    否则容差一重叠就永远只选中车道、点不到设施。
	var fid := canvas.pick_facility(world_m, doc, tol)
	if fid != "":
		EditorApp.select_facility(fid)
		print("车辆模式：选中检测设施 %s" % fid)
		return

	# 3) 再试车道：能给需求的可选 —— 机动车道（Driving / Bus / SharedLeftTurn）+ 非机动车道（Biking）
	var lid := canvas.pick_lane(world_m, doc, tol)
	if lid >= 0:
		var l := doc.find_lane(lid)
		var lt := String(l.get("type", ""))
		if lt == "Driving" or lt == "Bus" or lt == "SharedLeftTurn" or lt == "Biking":
			EditorApp.select_traffic_lane(lid)
			print("车辆模式：选中车道 %d（%s）" % [lid, lt])
		elif lt == "Sidewalk":
			print("人行道不挂需求：行人需求挂在【人行横道转向】上 —— 请点路口里那条过街连接")
		else:
			print("车道 %d 是 %s，没有可配的需求" % [lid, lt])
		return                    # ★ 点到车道就不算"点空白"，保持当前选中

	# 4) 空白 → 取消选中（含设施）
	EditorApp.select_facility("")
	EditorApp.select_traffic_lane(-1)
