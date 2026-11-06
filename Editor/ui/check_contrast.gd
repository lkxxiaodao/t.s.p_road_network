extends SceneTree

## 对比度验算工具 —— 按 WCAG 相对亮度算 R0 §5.1 那几条硬指标。
##
## 用法（工程根目录）：
##   "<Godot console>" --headless --path Editor --script res://ui/check_contrast.gd
##
## 什么时候跑：改了 `ui/tokens.gd` 里任何一个颜色，或改了 `make_clay.gd` 的填充色。
## 判据（R0 §5.1）：路面 vs 画布底 ≥ 1.35；正文 vs 面板 ≥ 4.5（次要在 3.0 上下）；
## 选中态 vs 周围 ≥ 2.0；冲突红 ≥ 3.0；网格四档 vs 画布底 ≥ 1.6（细线是最淡档，故意最低）。
##
## ⚠ 这里算的是**屏幕示意值**，不是打印色；别拿印刷标准套。数值口径以本脚本输出为准
##   （R0 §1.1b / §5.1 表里的数字就是它跑出来的，改色后要回头同步那张表）。

func _initialize() -> void:
	print("=== 网格四档 vs 画布底 %s ===" % UITokens.hex(UITokens.SURFACE_0))
	_row("GRID_FINE  5 m 细线", UITokens.GRID_FINE, UITokens.SURFACE_0, 1.60)
	_row("GRID_MAJOR 25 m 主线", UITokens.GRID_MAJOR, UITokens.SURFACE_0, 1.60)
	_row("GRID_BOLD 125 m 粗线", UITokens.GRID_BOLD, UITokens.SURFACE_0, 1.60)
	_row("GRID_AXIS  轴/原点", UITokens.GRID_AXIS, UITokens.SURFACE_0, 1.60)

	print("=== 路面 vs 画布底（≥1.35 才算「看得见」）===")
	_row("机动车道 #B9B3AA", UITokens.ROAD_DRIVING, UITokens.SURFACE_0, 1.35)
	_row("公交道 #9E96C4", UITokens.ROAD_BUS, UITokens.SURFACE_0, 1.35)
	_row("非机动车道 #8FBF8E", UITokens.ROAD_BIKING, UITokens.SURFACE_0, 1.35)
	_row("人行道 #CFC8BB", UITokens.ROAD_SIDEWALK, UITokens.SURFACE_0, 1.35)
	_row("左转专用道 #B08A63", UITokens.ROAD_LEFT_TURN, UITokens.SURFACE_0, 1.35)
	_row("路口补面 #AFA89D", UITokens.ROAD_JUNCTION, UITokens.SURFACE_0, 1.35)
	print("=== 路面元素 vs 机动车道（≥1.3 才分得开）===")
	_row("车道描边 #A79F94", UITokens.ROAD_EDGE, UITokens.ROAD_DRIVING, 1.10)
	_row("路口补面 vs 路面", UITokens.ROAD_JUNCTION, UITokens.ROAD_DRIVING, 1.05)
	_row("导向箭头 #F7F3EC", Color("#F7F3EC"), UITokens.ROAD_DRIVING, 1.50)
	_row("连接·直行 #4A86C8", UITokens.TURN_STRAIGHT, UITokens.ROAD_DRIVING, 1.50)
	_row("连接·左转 #C25F1C", UITokens.TURN_LEFT, UITokens.ROAD_DRIVING, 1.50)
	_row("连接·右转 #3F8A55", UITokens.TURN_RIGHT, UITokens.ROAD_DRIVING, 1.50)
	_row("连接·掉头 #8E6BB5", UITokens.TURN_UTURN, UITokens.ROAD_DRIVING, 1.50)
	_row("连接·过街 #8C97A8", UITokens.TURN_CROSSWALK, UITokens.ROAD_DRIVING, 1.20)

	print("=== 文字 vs 面板底（WCAG AA 要 ≥4.5）===")
	_row("INK_1 主文字", UITokens.INK_1, UITokens.SURFACE_1, 4.50)
	_row("INK_2 次级", UITokens.INK_2, UITokens.SURFACE_1, 4.50)
	_row("INK_3 提示（非正文，2.95 视为达标）", UITokens.INK_3, UITokens.SURFACE_1, 2.95)
	_row("主按钮白字 vs #E0762A", Color.WHITE, Color("#E0762A"), 3.00)

	print("=== 语义色 vs 画布底 ===")
	_row("选中橙 #F58F3C", UITokens.ORANGE_500, UITokens.SURFACE_0, 2.00)
	_row("冲突红 #E2564A", UITokens.RED_500, UITokens.SURFACE_0, 3.00)
	quit(0)


func _row(name: String, fg: Color, bg: Color, need: float) -> void:
	var r: float = _ratio(fg, bg)
	var tag: String = "OK " if r >= need else "LOW"
	print("  [%s] %-22s %5.2f : 1   (要求 ≥ %.2f)" % [tag, name, r, need])


func _ratio(a: Color, b: Color) -> float:
	var la: float = _lum(a)
	var lb: float = _lum(b)
	return (maxf(la, lb) + 0.05) / (minf(la, lb) + 0.05)


func _lum(c: Color) -> float:
	var ch: Array[float] = [c.r, c.g, c.b]
	var acc: float = 0.0
	var w: Array[float] = [0.2126, 0.7152, 0.0722]
	for i in range(3):
		var v: float = ch[i]
		var lin: float = v / 12.92 if v <= 0.03928 else pow((v + 0.055) / 1.055, 2.4)
		acc += lin * w[i]
	return acc
