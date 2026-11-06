class_name DecoMode
extends RefCounted

## 装饰模式：
##   点空白 = 放一个"当前素材"实例并选中；点已有实例 = 选中它；
##   拖动 = 移动（按住 Shift 吸附到 5 米网格）；Esc = 取消当前素材；Delete 由 GridCanvas 处理。

var dragging := false
var drag_deco_id := -1

func handle(event: InputEvent, world_m: Vector2, canvas: GridCanvas, doc: Doc) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_ESCAPE:
			EditorApp.current_asset = ""          # 取消当前素材，之后点空白只做取消选中
			EditorApp.select_deco(-1)
			print("已取消当前素材")
		else:
			_handle_selected_key(event)           # ★ 选中装饰时的旋转/缩放/层级快捷键
		return
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			var hit := canvas.pick_deco(world_m, doc)
			if hit >= 0:
				EditorApp.select_deco(hit)         # 点到实例 → 选中并拖
				dragging = true
				drag_deco_id = hit
			elif EditorApp.current_asset != "":
				# ★ C11：落点跟画路一样走吸附（`snap_m` 自己看 `X` 开关；磁吸到已有节点不适用于装饰）
				var id := doc.add_deco(EditorApp.current_asset, canvas.snap_m(world_m))
				if id >= 0:
					EditorApp.select_deco(id)
					dragging = true
					drag_deco_id = id
					print("放置装饰 %d（%s）" % [id, EditorApp.current_asset])
			else:
				EditorApp.select_deco(-1)
		else:
			if dragging:
				doc.dirty = true
				var d := doc.find_deco(drag_deco_id)
				if not d.is_empty():
					print("装饰 %d 落在 (%.1f, %.1f)" % [drag_deco_id, float(d.x), float(d.y)])
			dragging = false
			drag_deco_id = -1
	elif event is InputEventMouseMotion and dragging:
		var p := world_m
		if Input.is_key_pressed(KEY_SHIFT):
			p = canvas.snap_m(world_m)             # 按住 Shift → 吸附到 5 米网格
		doc.move_deco(drag_deco_id, p)

## 选中装饰时的快捷键：R/Shift+R 旋转 ±15°、+/- 缩放 ±0.1、PageUp/PageDown 与 [ ] 调 z
func _handle_selected_key(event: InputEventKey) -> bool:
	# ★ 显式写类型（不能用 :=）：EditorApp 是 autoload，而 editor_app.gd 又依赖本文件（DecoMode），
	#   两者构成编译依赖环 —— 编译本文件时 EditorApp 的静态类型可能还没定，:= 推断不出来会报
	#   "Cannot infer the type of ..."，连带 editor_app.gd 整份加载失败（autoload 变空 Node）。
	var id: int = EditorApp.selected_deco
	if id < 0:
		return false
	var d: Dictionary = EditorApp.doc.find_deco(id)
	if d.is_empty():
		return false
	match event.keycode:
		KEY_R:
			EditorApp.doc.rotate_deco(id, -15.0 if event.shift_pressed else 15.0)
		KEY_PLUS, KEY_EQUAL, KEY_KP_ADD:
			EditorApp.doc.scale_deco(id, 0.1)
		KEY_MINUS, KEY_KP_SUBTRACT:
			EditorApp.doc.scale_deco(id, -0.1)
		KEY_PAGEUP, KEY_BRACKETRIGHT:
			EditorApp.doc.nudge_deco_z(id, 1)
		KEY_PAGEDOWN, KEY_BRACKETLEFT:
			EditorApp.doc.nudge_deco_z(id, -1)
		_:
			return false
	EditorApp.selection_changed.emit()      # 让右侧面板跟着刷新数值
	print("装饰 %d：rot=%.0f°  scale=%.2f  z=%d" % [
		id, float(d.rotation), float(d.scale), int(d.z)])
	return true
