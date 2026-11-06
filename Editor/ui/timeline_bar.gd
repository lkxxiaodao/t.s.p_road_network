class_name TimelineBar
extends Control

## 相位条（P3-2）：把一个方案里各阶段的绿灯时长**按比例**画成一条横条。
##
## ★ 只"看"不改数：点某一段 = 展开/收起该阶段的勾选表，走的是原来那个「展开」按钮的同一个回调
##   （`_open_stage_toggle`）。本控件不碰 `Doc`、不占撤销步、不进指纹。
##
## ★ 为什么值得画：阶段本质是"一圈时间怎么切"，用列表表达看不出比例（30s+30s 和 50s+10s 长得一样）。
##   条子一眼就能看出哪段吃掉了大半周期。

signal stage_picked(index: int)

const H := 40.0               ## 条高
const MIN_LABEL_W := 40.0     ## 段宽低于这个就不写字（只留色块，免得糊成一团）

var _stages: Array = []       ## [{seconds: float, label: String, warn: bool}]
var _active := -1
var _total := 0.0


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP
	mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	custom_minimum_size = Vector2(0, H)
	add_theme_constant_override("outline_size", 0)


func set_stages(list: Array, active: int = -1) -> void:
	_stages = list
	_active = active
	_total = 0.0
	for s in _stages:
		_total += maxf(float(s.get("seconds", 0.0)), 0.0)
	custom_minimum_size = Vector2(0, H)
	tooltip_text = "点一段 = 展开该阶段的勾选表（保护 / 让行）"
	queue_redraw()


func _draw() -> void:
	var w := size.x
	var h := H
	# 底：没有一个阶段（理论上不会）就画一条灰带，别留黑洞
	if _stages.is_empty() or _total <= 0.0:
		draw_rect(Rect2(0, 0, w, h), UITokens.SURFACE_2)
		draw_rect(Rect2(0, 0, w, h), UITokens.SURFACE_3, false, 1.0)
		return

	var font: Font = get_theme_font("font", "Label")
	var fs := UITokens.T_CAP
	var x := 0.0
	for i in _stages.size():
		var sec := maxf(float(_stages[i].get("seconds", 0.0)), 0.0)
		var seg := w * sec / _total
		var r := Rect2(x, 0, seg, h)
		# 交替底色：橙一档 + 灰一档，相邻两段一定分得开
		var col: Color = UITokens.ORANGE_100 if i % 2 == 0 else UITokens.SURFACE_3
		if bool(_stages[i].get("warn", false)):
			col = UITokens.RED_50
		draw_rect(r, col)
		if i == _active:
			draw_rect(r.grow(-1.0), UITokens.SEL_RING, false, 2.0)
		if i > 0:
			draw_line(Vector2(x, 3.0), Vector2(x, h - 3.0), UITokens.SURFACE_1, 1.0)
		if seg >= MIN_LABEL_W:
			var label := String(_stages[i].get("label", str(i + 1)))
			var ink: Color = UITokens.INK_1
			var tw := font.get_string_size(label, HORIZONTAL_ALIGNMENT_LEFT, -1, fs).x
			var cx := x + (seg - tw) * 0.5
			draw_string(font, Vector2(cx, h * 0.46), label,
				HORIZONTAL_ALIGNMENT_LEFT, -1, fs, ink)
			var sec_txt := "%.0fs" % sec
			var sw := font.get_string_size(sec_txt, HORIZONTAL_ALIGNMENT_LEFT, -1, fs).x
			draw_string(font, Vector2(x + (seg - sw) * 0.5, h * 0.46 + float(fs) + 2.0), sec_txt,
				HORIZONTAL_ALIGNMENT_LEFT, -1, fs, UITokens.INK_2)
		x += seg
	# 外框
	draw_rect(Rect2(0, 0, w, h), UITokens.SURFACE_3, false, 1.0)


func _gui_input(ev: InputEvent) -> void:
	if not (ev is InputEventMouseButton):
		return
	var mb := ev as InputEventMouseButton
	if mb.button_index != MOUSE_BUTTON_LEFT or not mb.pressed:
		return
	if _stages.is_empty() or _total <= 0.0:
		return
	var idx := _index_at(mb.position.x)
	if idx >= 0:
		stage_picked.emit(idx)
		accept_event()


## 按 x 落在哪一段（各段宽度按秒数比例分）
func _index_at(px: float) -> int:
	var acc := 0.0
	for i in _stages.size():
		var sec := maxf(float(_stages[i].get("seconds", 0.0)), 0.0)
		acc += size.x * sec / _total
		if px <= acc:
			return i
	return _stages.size() - 1
