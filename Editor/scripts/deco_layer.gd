extends Node2D
class_name DecoLayer

## 装饰层：按 z 从小到大画（小的先画、被大的盖住）；缺图时画红叉占位。
## 位置一律经 GridCanvas.world_to_px（那里统一处理 Y+ = 北）。

func _ready() -> void:
	EditorApp.deco_layer = self      # 自报家门：下游不必按"节点名/层级"去找
	texture_repeat = CanvasItem.TEXTURE_REPEAT_ENABLED   # ★ tile 平铺必须打开它
	print("DecoLayer 已就绪")

## ★ C3 脏标记：旧版每帧无条件 queue_redraw()（每个实例的贴图都重画一遍），装饰多时纯属白烧。
##   装饰是静态的：只有 deco 数据或选中项变了才需要重画。
var _redraw_sig: String = ""

func _process(_delta: float) -> void:
	var doc: Doc = EditorApp.doc
	if doc == null:
		return
	var s := _state_sig(doc)
	if s == _redraw_sig:
		return
	_redraw_sig = s
	queue_redraw()

## 装饰状态签名：数量 + 选中项 + 每个实例的绘制参数（位置/旋转/缩放/层级/锚点/平铺/尺寸/素材）。
## ★ 拼一个短字符串比"每帧把所有贴图重画一遍"便宜得多，而且是**精确**比较
##   （用浮点累加做签名会在实例很多时丢精度，那种"偶发不刷新"的 bug 最难查）。
func _state_sig(doc: Doc) -> String:
	var parts := PackedStringArray()
	parts.append("%d|%d" % [doc.decos.size(), EditorApp.selected_deco])
	for d in doc.decos:
		parts.append("%d/%.2f/%.2f/%.1f/%.3f/%d/%s/%d/%d/%d/%s" % [
			int(d.get("id", -1)), float(d.get("x", 0.0)), float(d.get("y", 0.0)),
			float(d.get("rotation", 0.0)), float(d.get("scale", 1.0)), int(d.get("z", 1)),
			String(d.get("anchor", "center")), int(bool(d.get("tile", false))),
			int(d.get("w", 0)), int(d.get("h", 0)), String(d.get("asset", ""))])
	return "|".join(parts)

func _draw() -> void:
	var doc: Doc = EditorApp.doc
	if doc == null:
		return
	var list: Array = doc.decos.duplicate()
	list.sort_custom(func(a, b): return int(a.z) < int(b.z))     # z 小的先画
	for d in list:
		_draw_one(d)
	if EditorApp.selected_deco >= 0:                            # 选中：画外框
		var sd := doc.find_deco(EditorApp.selected_deco)
		if not sd.is_empty():
			_draw_sel_box(sd)

func _draw_one(d: Dictionary) -> void:
	var tex := _load_tex(String(d.get("asset", "")))
	var size := _draw_size(d, tex)
	apply_deco_transform(d)
	if tex == null:
		draw_line(Vector2(-8, -8), Vector2(8, 8), UITokens.CONFLICT, 2.0)   # 缺图：红叉
		draw_line(Vector2(-8, 8), Vector2(8, -8), UITokens.CONFLICT, 2.0)
	else:
		draw_texture_rect(tex, Rect2(_anchor_off(d, size), size),
			bool(d.get("tile", false)))                                     # tile=true → 平铺
	draw_set_transform(Vector2.ZERO, 0.0, Vector2.ONE)                      # ★ 用完复位

func _draw_sel_box(d: Dictionary) -> void:
	var tex := _load_tex(String(d.get("asset", "")))
	var size := _draw_size(d, tex)
	apply_deco_transform(d)
	draw_rect(Rect2(_anchor_off(d, size), size), UITokens.SEL_RING, false, 2.0)
	draw_set_transform(Vector2.ZERO, 0.0, Vector2.ONE)

## 把绘制变换设成"该实例"的（绘制与拾取共用同一套）
func apply_deco_transform(d: Dictionary) -> void:
	var pos := GridCanvas.world_to_px(Vector2(float(d.get("x", 0.0)), float(d.get("y", 0.0))))
	var s := float(d.get("scale", 1.0))
	draw_set_transform(pos, deg_to_rad(float(d.get("rotation", 0.0))), Vector2(s, s))

## 锚点偏移（像素）：按"实际绘制尺寸"算（scale=1.0 时 = 原始像素）
func _anchor_off(d: Dictionary, size: Vector2) -> Vector2:
	match String(d.get("anchor", "center")):
		"top_left":   return Vector2.ZERO
		"top_center": return Vector2(-size.x * 0.5, 0.0)
	return -size * 0.5

## 这个实例实际要画的尺寸（像素）：tile 且填了 w/h → 用 w/h；否则用素材原始尺寸
func _draw_size(d: Dictionary, tex: Texture2D) -> Vector2:
	if bool(d.get("tile", false)):
		var w := float(d.get("w", 0.0))
		var h := float(d.get("h", 0.0))
		if w > 0.0 and h > 0.0:
			return Vector2(w, h)
	return tex.get_size() if tex != null else Vector2(16, 16)

var _tex_cache: Dictionary = {}    ## 素材路径 → 纹理（只解一次，之后直接复用）
var _seen: Dictionary = {}         ## 缺图警告 / 加载日志 各只打一次

## 取素材纹理。文件里存的是**相对 `assets/` 的路径**（如 `deco/tree.png`），
## 具体从哪个根读由 `AssetPaths` 决定：发布版是 `<exe 同目录>/assets/`（可加图），编辑器是 `res://assets/`。
## ★ 外部磁盘上的 png 不能用 `load()`（它只认导入过的资源），`AssetPaths.load_texture` 内部走
##   `Image.load_from_file`；包内那份仍走 `load()` 并转 `ImageTexture`（绕开本环境画白的问题）。
func _load_tex(asset: String) -> Texture2D:
	if asset == "":
		return null
	if _tex_cache.has(asset):
		return _tex_cache[asset]
	var tex := AssetPaths.load_texture(asset)
	if tex == null:
		if not _seen.has("miss:" + asset):
			_seen["miss:" + asset] = true
			push_warning("装饰素材缺失：%s（在 %s 下没找到；画红叉占位）" % [
				asset, AssetPaths.deco_dir_for_hint()])
		_tex_cache[asset] = null
		return null
	if not _seen.has("ok:" + asset):
		_seen["ok:" + asset] = true
		print("装饰素材加载成功：%s 尺寸=%s" % [asset, tex.get_size()])
	_tex_cache[asset] = tex
	return tex

## 装饰模式用：找包含 world_m 的实例（z 大的先命中）；没有返回 -1
func pick_deco(doc: Doc, world_m: Vector2) -> int:
	var m := GridCanvas.world_to_px(world_m)
	var list: Array = doc.decos.duplicate()
	list.sort_custom(func(a, b): return int(a.z) > int(b.z))
	for d in list:
		var tex := _load_tex(String(d.get("asset", "")))
		var size := _draw_size(d, tex)
		var off := _anchor_off(d, size)
		var pos := GridCanvas.world_to_px(Vector2(float(d.get("x", 0.0)), float(d.get("y", 0.0))))
		var s := float(d.get("scale", 1.0))
		var local := (m - pos).rotated(-deg_to_rad(float(d.get("rotation", 0.0)))) / s
		if Rect2(off, size).has_point(local):
			return int(d.id)
	return -1
