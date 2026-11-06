class_name CurvePreview
extends Control

## 需求曲线小折线（P3-2）：只看形状，**不读也不写 Doc**（数据由调用方塞进来）。
##
## ★ 为什么要有它：时段表是一串数字，两段曲线的差别（一个平稳、一个早晚高峰）在表里看不出来。
##   折线只回答一个问题："这条曲线长什么样"。
##
## ★ 不做横轴刻度：时刻范围随数据变（0~86400 也可能只有 0~60），刻度会一直在变、反而更乱。
##   只画两条参考线（0 与最大值）并在右上角标峰值。

const H := 76.0            ## 控件高
const PAD := 6.0           ## 内边距（留给峰值文字）

var _times: Array = []
var _rates: Array = []


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE     # 纯展示，别抢画布/面板的点击
	custom_minimum_size = Vector2(0, H)


func set_data(times: Array, rates: Array) -> void:
	_times = times
	_rates = rates
	queue_redraw()


func _draw() -> void:
	var w := size.x
	var h := H
	draw_rect(Rect2(0, 0, w, h), UITokens.SURFACE_2)
	draw_rect(Rect2(0, 0, w, h), UITokens.SURFACE_3, false, 1.0)
	if _times.size() < 2 or _times.size() != _rates.size():
		var font: Font = get_theme_font("font", "Label")
		draw_string(font, Vector2(PAD, h * 0.5 + 5.0), "（少于两段，画不出曲线）",
			HORIZONTAL_ALIGNMENT_LEFT, -1, UITokens.T_CAP, UITokens.INK_3)
		return

	var t0 := float(_times[0])
	var t1 := float(_times[_times.size() - 1])
	var span: float = maxf(t1 - t0, 1.0)
	var vmax := 0.0
	for v in _rates:
		vmax = maxf(vmax, float(v))
	vmax = maxf(vmax, 0.0001)

	var plot := Rect2(PAD, PAD, w - PAD * 2.0, h - PAD * 2.0 - 4.0)
	# 基线（0 流量）
	draw_line(Vector2(plot.position.x, plot.end.y), Vector2(plot.end.x, plot.end.y),
		UITokens.SURFACE_3, 1.0)

	var pts: PackedVector2Array = PackedVector2Array()
	for i in _times.size():
		var x: float = plot.position.x + plot.size.x * (float(_times[i]) - t0) / span
		var y: float = plot.end.y - plot.size.y * clampf(float(_rates[i]) / vmax, 0.0, 1.0)
		pts.append(Vector2(x, y))
	# 折线下方的浅色填充：先画一圈到底的多边形，再压线
	var fill := pts.duplicate()
	fill.append(Vector2(plot.end.x, plot.end.y))
	fill.append(Vector2(plot.position.x, plot.end.y))
	draw_colored_polygon(fill, Color(UITokens.ORANGE_300.r, UITokens.ORANGE_300.g,
		UITokens.ORANGE_300.b, 0.25))
	draw_polyline(pts, UITokens.ORANGE_600, 2.0, true)
	for p in pts:
		draw_circle(p, 2.5, UITokens.ORANGE_700)

	var font2: Font = get_theme_font("font", "Label")
	var tip := "峰值 %.2f" % vmax
	draw_string(font2, Vector2(w - PAD - 52.0, PAD + 10.0), tip,
		HORIZONTAL_ALIGNMENT_LEFT, -1, UITokens.T_CAP, UITokens.INK_2)
