extends Node2D
class_name BasemapLayer

## 底图层（视图辅助 / 描图垫）：把一张 PNG 垫在【网格之下、道路之下】，用来对齐描路。
##
## ★ 三条硬口径：
##   1) 纯会话设置：不进 .spmap、也不进 .spdeco；只写 user://basemap_session.json。
##   2) 层序由 GridCanvas 掌控：本节点自己【不画】任何东西，只提供 draw_into()，
##      由 grid_canvas.gd 的 _draw() 按「背景板 → 底图 → 网格线」的顺序调用它。
##      （这样既不用 show_behind_parent，也不会把 GridCanvas 的不透明背景板盖在图上）
##   3) 纹理一律走 ImageTexture：本环境 CompressedTexture2D 直接 draw 出来是纯白（E06 / E90 坑表），
##      所以 res:// 里已导入的图先 load() 再 get_image() 转 ImageTexture；
##      磁盘上的任意 PNG 直接解成 Image 再转 ImageTexture。两条路都不把 CompressedTexture2D 交给绘制。
##
## ★ 本层不注册任何输入（没有 _input / _unhandled_input）、也不参与拾取：
##   底图永远是"背景"，点它只会落到下面的道路 / 装饰上；锁定 = 面板上的标定控件一起冻住。

const SESSION_RES := "user://basemap_session.json"
const SAVE_DEBOUNCE_MS := 400            ## 拖滑块时别每帧写盘
const PANEL_POS := Vector2(430, 160)     ## 面板落点：视图菜单右手边，避开左侧工具栏/装饰素材栏

var path: String = ""                    ## 当前底图的路径（OS 绝对路径或 res://；"" = 未载入）
var opacity: float = 1.0                 ## 透明度 0..1
var img_scale: float = 1.0               ## 1.0 = 图片原始像素（与 DecoLayer 的 scale 口径一致，别乘 PPM）
var offset_m: Vector2 = Vector2.ZERO     ## 底图【左上角】的世界坐标（米）
var locked: bool = false                 ## 锁定：面板的 透明度/缩放/偏移 全部置灰
var shown: bool = true                   ## 显示开关（换关卡自动收起后，可从这里再打开）

var _tex: Texture2D = null
var _tex_cache: Dictionary = {}          ## 路径 → ImageTexture（只解一次；_draw 每帧都在跑，别每帧重建）
var _slot_path: String = ""              ## 这份标定属于哪个关卡文件（换关卡就收起，见 _apply_slot）
var _panel: PanelContainer = null
var _body: VBoxContainer = null          ## 面板正文（收起时整块隐藏，只留标题行）
var _btn_fold: Button = null
var _dragging_panel := false             ## 正在拖面板（C8）
var _lbl_file: Label = null
var _chk_shown: CheckBox = null
var _chk_locked: CheckBox = null
var _spins: Array[SpinBox] = []
var _syncing := false
var _dlg: FileDialog = null
var _save_pending := false
var _save_ms := 0

func _ready() -> void:
	_load_session()
	_build_panel()
	EditorApp.doc_changed.connect(_on_doc_changed)
	print("BasemapLayer 已就绪（底图：%s）" % ("未载入" if path == "" else path.get_file()))

# --- 绘制（由 GridCanvas 调用，本节点自己不出手）--------------------------

## 供 GridCanvas._draw() 调用：把底图按当前标定画进【那个 CanvasItem】。
## ★ 只在这里画一次，层序完全由调用点决定 = 背景板之上、网格线之下。
func draw_into(ci: CanvasItem) -> void:
	if not is_active():
		return
	var r := image_rect_px()
	ci.draw_texture_rect(_tex, r, false, Color(1, 1, 1, opacity))
	# 校准中（面板开着且没锁定）给个 1px 外框，方便看边界在哪
	if _panel != null and _panel.visible and not locked:
		ci.draw_rect(r, Color(0.2, 0.9, 1.0, 0.35), false, 1.0)

## 有图、要显示、且不是全透明 → 才算"在用"
func is_active() -> bool:
	return _tex != null and shown and opacity > 0.001

## 底图在画布上占的矩形（画布像素，本节点局部坐标）——绘制与校准外框共用
func image_rect_px() -> Rect2:
	if _tex == null:
		return Rect2()
	return Rect2(GridCanvas.world_to_px(offset_m), _tex.get_size() * img_scale)

# --- 载入 / 清除 ---------------------------------------------------------

## 从「视图 → 载入底图…」进来：选一张 PNG
func ask_load() -> void:
	if _dlg != null and is_instance_valid(_dlg):
		return                                   # 已经开着，别叠第二个
	var d := FileDialog.new()
	EditorApp.prep_window(d)                     # ★ 窗口底透明，否则圆角四角露深灰（E91 C19）
	d.access = FileDialog.ACCESS_FILESYSTEM
	d.file_mode = FileDialog.FILE_MODE_OPEN_FILE
	d.filters = PackedStringArray(["*.png ; 底图 PNG"])
	d.title = "载入底图（只是描图垫，不会写进关卡文件）"
	d.size = Vector2i(860, 560)
	if path != "":
		d.current_path = path
	_dlg = d
	d.file_selected.connect(func(p: String): _close_dlg(p))
	d.canceled.connect(func(): _close_dlg(""))
	get_tree().root.add_child(d)
	d.popup_centered(Vector2i(860, 560))      # 同 editor_app._dialog：不带尺寸会按最小尺寸弹，挤成一条

## 关弹窗：★ 先 hide 再延后摘树 —— 否则会报"父窗口已有独占子窗口"（同 EditorApp._close_dialog）
func _close_dlg(p: String) -> void:
	var d := _dlg
	_dlg = null
	if d == null or not is_instance_valid(d):
		return
	d.hide()
	if p != "":
		load_png.call_deferred(p)
	d.queue_free()

## 载入一张 PNG（OS 绝对路径或 res:// 都行）；成功返回 true
func load_png(p: String) -> bool:
	var tex := _load_tex(p)
	if tex == null:
		push_warning("底图载入失败：%s" % p)
		return false
	path = p
	_tex = tex
	_slot_path = EditorApp.current_path       # ★ 记住"这张图标定给哪个关卡文件"
	shown = true
	_sync_panel()
	_redraw()
	_save_session_later()
	print("底图已载入：%s（%d×%d 像素，%s）" % [
		p, int(tex.get_size().x), int(tex.get_size().y), tex.get_class()])
	return true

## 清除（纹理缓存留着，下次载入同一张图仍然秒开）
func clear() -> void:
	path = ""
	_tex = null
	_slot_path = EditorApp.current_path
	_sync_panel()
	_redraw()
	_save_session_later()
	print("底图已清除")

func toggle_panel() -> void:
	if _panel == null:
		return
	_panel.visible = not _panel.visible
	if _panel.visible:
		_sync_panel()
	_redraw()                                  # 校准外框只在面板开着时画

# --- 纹理（CompressedTexture2D 全白坑的绕法 + 缓存）-----------------------

## 取底图纹理并缓存。两条路：
##   res:// 里已导入的图 → load() 拿 CompressedTexture2D → get_image() → ImageTexture（照坑表绕行）
##   磁盘上的任意 PNG   → FileAccess 读字节 → Image.load_png_from_buffer()（根本不产生 CompressedTexture2D）
func _load_tex(p: String) -> Texture2D:
	if p == "":
		return null
	if _tex_cache.has(p):
		return _tex_cache[p]
	var img: Image = null
	if p.begins_with("res://"):
		var t := load(p) as Texture2D
		if t != null:
			img = t.get_image()                # 已导入资源：拿 CPU 侧图像（像素是好的，只坏在绘制路径）
	if img == null or img.is_empty():
		var f := FileAccess.open(p, FileAccess.READ)
		if f == null:
			push_warning("底图读不到：%s" % p)
			return null
		var bytes := f.get_buffer(f.get_length())
		f.close()
		var im := Image.new()
		if im.load_png_from_buffer(bytes) != OK:
			push_warning("底图不是有效的 PNG：%s" % p)
			return null
		img = im
	if img.is_compressed():
		img.decompress()
	var out: Texture2D = ImageTexture.create_from_image(img)
	_tex_cache[p] = out
	return out

## 让父节点（GridCanvas）下一帧重画；底图本身不占自己的画布
func _redraw() -> void:
	var p := get_parent()
	if p is CanvasItem:
		(p as CanvasItem).queue_redraw()

# --- 面板 ---------------------------------------------------------------

func _build_panel() -> void:
	var root: Node = get_tree().current_scene
	if root == null:
		root = get_tree().root
	var cl := CanvasLayer.new()
	cl.name = "BasemapPanelLayer"
	# ★ _ready 期间父节点正在装配子节点，直接 add_child 会报 "Parent node is busy setting up children"
	#   → 延迟一拍挂上去（面板整棵子树先在 cl 上搭好，进树时一起进来）
	root.add_child.call_deferred(cl)

	_panel = PanelContainer.new()
	_panel.name = "BasemapPanel"
	_panel.position = PANEL_POS
	_panel.custom_minimum_size = Vector2(268, 0)
	_panel.visible = false                 # 默认收起；「视图 → 底图面板」或载入后自己弹开
	# 不透明底板：面板压在画布/工具栏上时，底下的网格与按钮不会透过来
	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.14, 0.14, 0.16, 0.97)
	sb.border_color = Color(1, 1, 1, 0.16)
	sb.set_border_width_all(1)
	sb.set_corner_radius_all(4)
	sb.set_content_margin_all(8)
	_panel.add_theme_stylebox_override("panel", sb)
	cl.add_child(_panel)

	var box := VBoxContainer.new()
	_panel.add_child(box)

	# 标题行（C8）：**可拖动 + 可收起**。以前面板钉死在 (430,160) 且不透明吃点击，
	# 正好压在画布中间 —— 被盖住的那块连路都画不出来。现在按住标题/面板空白就能挪走。
	var head := HBoxContainer.new()
	box.add_child(head)
	var title := _text("底图（描图垫 · 不进关卡文件）", 1.0)
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	head.add_child(title)
	_btn_fold = _btn("收起", _toggle_fold)
	head.add_child(_btn_fold)
	_panel.gui_input.connect(_on_panel_input)

	_body = VBoxContainer.new()
	box.add_child(_body)

	_lbl_file = _text("（未载入）", 0.9)
	_body.add_child(_lbl_file)

	var grid := GridContainer.new()
	grid.columns = 2
	_body.add_child(grid)
	_add_row(grid, "透明度", _spin(0.0, 1.0, 0.01, _on_opacity))
	_add_row(grid, "缩放", _spin(0.05, 20.0, 0.01, _on_scale))
	_add_row(grid, "偏移 X (m)", _spin(-4000.0, 4000.0, 0.1, _on_off_x))
	_add_row(grid, "偏移 Y (m)", _spin(-4000.0, 4000.0, 0.1, _on_off_y))

	_body.add_child(_text("偏移 = 图片左上角的世界坐标（米）；缩放 1.0 = 图片原始像素", 0.6))

	_chk_shown = CheckBox.new()
	_chk_shown.text = "显示"
	_chk_shown.toggled.connect(_on_shown)
	_body.add_child(_chk_shown)

	_chk_locked = CheckBox.new()
	_chk_locked.text = "锁定（冻结标定，防误改）"
	_chk_locked.toggled.connect(_on_locked)
	_body.add_child(_chk_locked)

	var row := HBoxContainer.new()
	row.add_child(_btn("载入…", ask_load))
	row.add_child(_btn("清除", clear))
	row.add_child(_btn("关闭", func(): _panel.visible = false))
	_body.add_child(row)

	_sync_panel()

## 收起 / 展开（C8）：收起后只剩一行标题，画布又能点了
func _toggle_fold() -> void:
	if _body == null:
		return
	_body.visible = not _body.visible
	if _btn_fold != null:
		_btn_fold.text = "展开" if not _body.visible else "收起"
	_clamp_panel_pos()

## 拖动面板（C8）：按住标题或面板空白处拖 → 挪开
func _on_panel_input(ev: InputEvent) -> void:
	if ev is InputEventMouseButton and ev.button_index == MOUSE_BUTTON_LEFT:
		_dragging_panel = ev.pressed
		if ev.pressed:
			_panel.move_to_front()
	elif ev is InputEventMouseMotion and _dragging_panel:
		_panel.position += ev.relative
		_clamp_panel_pos()

## 别让面板被拖出屏幕（至少留 60×40 px 抓手）
func _clamp_panel_pos() -> void:
	if _panel == null:
		return
	var vs := get_viewport_rect().size
	if vs.x < 8.0 or vs.y < 8.0:          # 极端小窗（headless 64×64 也要能夹）
		return
	var sz := _panel.size
	_panel.position = Vector2(
		clampf(_panel.position.x, -(sz.x - 60.0), vs.x - 60.0),
		clampf(_panel.position.y, 0.0, vs.y - 40.0))

func _text(s: String, alpha: float) -> Label:
	var l := Label.new()
	l.text = s
	l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	l.custom_minimum_size = Vector2(250, 0)
	l.modulate = Color(1, 1, 1, alpha)
	return l

func _add_row(grid: GridContainer, label_text: String, control: Control) -> void:
	var l := Label.new()
	l.text = label_text
	grid.add_child(l)
	grid.add_child(control)

## ★ 视图设置不是文档数据 → 不记撤销步（绝不调 begin_edit / end_edit）
func _spin(min_v: float, max_v: float, step: float, cb: Callable) -> SpinBox:
	var s := SpinBox.new()
	s.min_value = min_v
	s.max_value = max_v
	s.step = step
	s.value = 0.0
	s.value_changed.connect(cb)            # 值由 _sync_panel() 统一回填
	_spins.append(s)
	return s

func _btn(text: String, cb: Callable) -> Button:
	var b := Button.new()
	b.text = text
	b.pressed.connect(cb)
	return b

## 面板 → 状态（每次同步都把真实状态回填到控件上）
func _sync_panel() -> void:
	if _panel == null:
		return
	_syncing = true
	if _lbl_file != null:
		_lbl_file.text = ("（未载入）" if path == "" else path.get_file()) + ("　［已锁定］" if locked else "")
	if _spins.size() == 4:
		_spins[0].value = opacity
		_spins[1].value = img_scale
		_spins[2].value = offset_m.x
		_spins[3].value = offset_m.y
	if _chk_shown != null:
		_chk_shown.set_pressed_no_signal(shown)
	if _chk_locked != null:
		_chk_locked.set_pressed_no_signal(locked)
	for c in _spins:                        # 锁定 → 三个标定控件一起冻住
		if is_instance_valid(c):
			c.editable = not locked
	_syncing = false

func _on_opacity(v: float) -> void:
	if _syncing:
		return
	opacity = clampf(v, 0.0, 1.0)
	_redraw()
	_save_session_later()

func _on_scale(v: float) -> void:
	if _syncing:
		return
	img_scale = maxf(v, 0.01)
	_redraw()
	_save_session_later()

func _on_off_x(v: float) -> void:
	if _syncing:
		return
	offset_m.x = v
	_redraw()
	_save_session_later()

func _on_off_y(v: float) -> void:
	if _syncing:
		return
	offset_m.y = v
	_redraw()
	_save_session_later()

func _on_shown(on: bool) -> void:
	if _syncing:
		return
	shown = on
	if on and path == "":
		print("底图还没载入：先「视图 → 载入底图…」选一张 PNG")
	if on and _slot_path != EditorApp.current_path:
		# ★ C9 的兜底：正常情况下换关卡时 _apply_slot 已经解绑了，走不到这里。
		#   真走到了也绝不拿旧标定糊弄新关卡 —— 先解绑，再让用户重新载入。
		print("⚠ 这份底图属于另一个关卡：已解绑，请重新载入")
		_apply_slot()
		return
	_redraw()
	_save_session_later()

func _on_locked(on: bool) -> void:
	if _syncing:
		return
	locked = on
	_sync_panel()
	_redraw()
	_save_session_later()
	print("底图已%s" % ("锁定（标定控件置灰）" if on else "解锁"))

# --- 会话设置（只在 user:// 里，绝不进关卡文件）--------------------------

## 换文档（导入 / 打开 / 新建 / 撤销重做）：current_path 在 import_map / _do_new 之后才写，
## 所以延后一拍再看。路径还是同一份关卡（含撤销重做）→ 什么都不做；
## 换了别的关卡 → **丢掉这份标定**（图片文件还在盘上，重新载入一次即可）。
func _on_doc_changed() -> void:
	_apply_slot.call_deferred()

## ★ C9：不管当前是「显示」还是「隐藏」都要解绑。
##   旧版第一行就是 `if not shown: return` —— 于是"先把底图隐藏 → 换关卡 → 再点显示"这条路上
##   标定一直挂着上一关的 `_slot_path`，再点「显示」就把**上一关的底图**当成这一关的显示出来了
##   （几何全错，但看起来"有底图"）。现在语义只有一句：**一份标定只属于一个关卡文件**。
func _apply_slot() -> void:
	if not is_inside_tree():
		return
	if EditorApp.current_path == _slot_path:
		return
	var had := path != "" or _tex != null
	_slot_path = EditorApp.current_path     # 记成"当前关卡（此刻没有标定）"，避免每次 doc_changed 重复解绑
	path = ""
	_tex = null
	offset_m = Vector2.ZERO
	img_scale = 1.0
	locked = false
	shown = false
	_sync_panel()
	_redraw()
	_save_session_later()
	if had:
		print("底图已解绑：换到了另一个关卡文件，上一关的标定不再沿用（要描图请重新载入）")

func _save_session_later() -> void:
	_save_pending = true
	_save_ms = Time.get_ticks_msec()

func _process(_delta: float) -> void:
	if not _save_pending:
		return
	if Time.get_ticks_msec() - _save_ms < SAVE_DEBOUNCE_MS:
		return
	_save_pending = false
	_save_session()

func _exit_tree() -> void:
	if _save_pending:
		_save_pending = false
		_save_session()

func _save_session() -> void:
	var d := {
		"path": path, "opacity": opacity, "scale": img_scale,
		"offset_x": offset_m.x, "offset_y": offset_m.y,
		"locked": locked, "shown": shown, "map": _slot_path,
	}
	var f := FileAccess.open(SESSION_RES, FileAccess.WRITE)
	if f == null:
		push_warning("底图会话设置写入失败：%s" % SESSION_RES)
		return
	f.store_string(JSON.stringify(d, "  ", false))
	f.close()

func _load_session() -> void:
	if not FileAccess.file_exists(SESSION_RES):
		return
	var d = JSON.parse_string(FileAccess.get_file_as_string(SESSION_RES))
	if not (d is Dictionary):
		push_warning("底图会话设置损坏 → 按未载入处理")
		return
	path = String(d.get("path", ""))
	opacity = clampf(float(d.get("opacity", 1.0)), 0.0, 1.0)
	img_scale = maxf(float(d.get("scale", 1.0)), 0.01)
	offset_m = Vector2(float(d.get("offset_x", 0.0)), float(d.get("offset_y", 0.0)))
	locked = bool(d.get("locked", false))
	shown = bool(d.get("shown", true))
	_slot_path = String(d.get("map", ""))
	if path != "":
		_tex = _load_tex(path)
		if _tex == null:
			path = ""                       # 图被删/改名 → 静默当作未载入
