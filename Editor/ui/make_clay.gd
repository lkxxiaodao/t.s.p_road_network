extends SceneTree

## 一次性生成器：画出粘土 9-patch 软垫图 → `ui/tex/*.png`。
##
## 用法（工程根目录）：
##   "<Godot console>" --headless --path Editor --script res://ui/make_clay.gd
##
## 为什么用 9-patch 而不是 StyleBoxFlat：
##   `StyleBoxFlat` 只有「一层外投影 + 单色边框」，做不出 R0 §1.4 要的
##   **上内高光（受光）+ 下内底影（转折）** 双色内影；硬要做得在控件上再叠 `_draw`，
##   侵入性太大。9-patch 把三层（外投影 / 本体 / 内高光+内底影）一次性烘进图里，
##   运行时只是一个 `StyleBoxTexture`，性能也更好（R0 §1.4「默认走 9-patch」）。
##
## 图的构造（64×64）：
##   - 本体圆角矩形：外接 44×44 居中，圆角 = radius 参数
##   - 外投影：本体向下偏 3 px、向外 8 px 渐隐（二次衰减，近似高斯）
##   - 上内高光：本体上边缘内 3 px 混白（α 由内向外衰减）
##   - 下内底影：本体下边缘内 4 px 混深褐
##   切图：`texture_margin = 20`、`expand_margin = 10`（投影画在 rect 外面，不影响布局）
##
## 4× 超采样后再用 LANCZOS 缩回，边缘才不毛。

const OUT_DIR := "res://ui/tex"
const CANVAS := 80
const SS := 4
const BODY_HALF := 26.0     ## 本体半宽（80 画布四边各留 14 px 给投影；22 时投影会被裁）
const SHADOW_OFF := Vector2(0, 3)
const SHADOW_BLUR := 8.0

const FILL_SURFACE := Color("#FFFDF9")
const FILL_ORANGE := Color("#E0762A")
const FILL_ORANGE_HOVER := Color("#C25F1C")
const FILL_SUNKEN := Color("#F7F2EA")
const FILL_DISABLED := Color("#ECE4D9")
const BORDER := Color("#ECE4D9")
const BORDER_ORANGE := Color("#C25F1C")
const INK_SHADOW := Color(140.0 / 255.0, 105.0 / 255.0, 70.0 / 255.0, 1.0)


func _initialize() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))

	var made: int = 0

	# --- 次按钮 / 面板：近白本体 ---
	# rest：投影 6、模糊大（离地感）
	made += _emit("clay_surface_rest", 14.0, FILL_SURFACE, BORDER, 0.18, SHADOW_BLUR, 6.0, 0.85, 0.10, false)
	# hover：投影 4、更贴地（"按下去一点点"）
	made += _emit("clay_surface_hover", 14.0, FILL_SURFACE, Color("#FFB273"), 0.20, SHADOW_BLUR, 4.0, 0.85, 0.08, false)
	# pressed：无外投影 + 整体内凹感
	made += _emit("clay_surface_pressed", 14.0, FILL_SUNKEN, Color("#FFB273"), 0.0, SHADOW_BLUR, 0.0, 0.0, 0.18, true)

	# --- 主按钮：橙 ---
	made += _emit("clay_orange_rest", 14.0, FILL_ORANGE, BORDER_ORANGE, 0.18, SHADOW_BLUR, 6.0, 0.30, 0.10, false)
	made += _emit("clay_orange_hover", 14.0, FILL_ORANGE_HOVER, BORDER_ORANGE, 0.20, SHADOW_BLUR, 4.0, 0.30, 0.08, false)
	made += _emit("clay_orange_pressed", 14.0, Color("#A94D12"), Color("#8A4212"), 0.0, SHADOW_BLUR, 0.0, 0.0, 0.22, true)

	# --- 面板卡片（大圆角，只留投影，态由内容表达）---
	made += _emit("clay_panel", 20.0, FILL_SURFACE, BORDER, 0.18, SHADOW_BLUR, 6.0, 0.85, 0.10, false)

	# --- 凹槽（内影在上/左，无外投影）---
	made += _emit("clay_sunken", 10.0, FILL_SUNKEN, BORDER, 0.0, SHADOW_BLUR, 0.0, 0.0, 0.16, true)

	# --- 危险按钮：近白本体 + 红边 ---
	made += _emit("clay_danger", 14.0, FILL_SURFACE, Color("#E2564A"), 0.14, SHADOW_BLUR, 5.0, 0.85, 0.10, false)

	# --- 禁用：平涂无影 ---
	made += _emit("clay_disabled", 14.0, FILL_DISABLED, Color(0, 0, 0, 0), 0.0, SHADOW_BLUR, 0.0, 0.0, 0.0, false)

	# --- 焦点环（只有橙描边 + 浅橙外扩，中间透明）---
	made += _emit("clay_focus", 14.0, Color(0, 0, 0, 0), Color("#F58F3C"), 0.0, SHADOW_BLUR, 0.0, 0.0, 0.0, false, 2.0)

	# --- 输入框聚焦：白底 + 2px 橙边（空环版不能用在这儿，会没有底色）---
	made += _emit("clay_surface_focus", 14.0, FILL_SURFACE, Color("#F58F3C"), 0.0, SHADOW_BLUR, 0.0, 0.85, 0.06, false, 2.0)

	print("[make_clay] 生成 %d 张 → %s" % [made, OUT_DIR])
	quit(0)


## 生成一张。返回 1 表示成功。
func _emit(name: String, radius: float, fill: Color, border: Color,
		shadow_alpha: float, shadow_blur: float, shadow_size: float,
		highlight_alpha: float, inner_alpha: float, inset: bool,
		border_w: float = 1.2) -> int:
	var n: int = CANVAS * SS
	var img: Image = Image.create(n, n, false, Image.FORMAT_RGBA8)
	img.fill(Color(0, 0, 0, 0))

	var half := BODY_HALF * float(SS)
	var r: float = minf(radius, BODY_HALF) * float(SS)
	var blur: float = shadow_blur * float(SS)
	var off := SHADOW_OFF * float(SS)
	var c := float(n) * 0.5
	var bw: float = border_w * float(SS)
	var hi_w: float = 3.0 * float(SS)    ## 上高光带的厚度
	var lo_w: float = 4.0 * float(SS)    ## 下内底影带的厚度
	var top_y: float = -(half - hi_w)
	var bot_y: float = half - lo_w

	for y in range(n):
		for x in range(n):
			var p := Vector2(float(x) + 0.5 - c, float(y) + 0.5 - c)
			var d: float = _sdf(p, half, r)
			if d <= 0.0:
				var col := fill
				if border.a > 0.0 and d > -bw:
					col = border
				else:
					# 上内高光：**只在上边那一条带里**（早先写成 "p.y<0 且贴边"，
					# 结果左右两侧的上半段也被点亮，橙块两边出现白竖条）
					if highlight_alpha > 0.0 and p.y < top_y:
						var t: float = clampf(1.0 - (-d) / hi_w, 0.0, 1.0)
						t = t * t                       # 平方衰减：只有最贴边的一点点被点亮
						if t > 0.0:
							col = col.lerp(Color(1, 1, 1, 1), highlight_alpha * t)
					# 内底影：常态在下缘；凹槽（inset）反过来，影在上缘
					if inner_alpha > 0.0:
						if inset and p.y < -(half - lo_w):
							var ti: float = clampf(1.0 - (-d) / lo_w, 0.0, 1.0)
							if ti > 0.0:
								col = col.lerp(INK_SHADOW, inner_alpha * ti * ti)
						elif not inset and p.y > bot_y:
							var ti2: float = clampf(1.0 - (-d) / lo_w, 0.0, 1.0)
							if ti2 > 0.0:
								col = col.lerp(INK_SHADOW, inner_alpha * ti2 * ti2)
				img.set_pixel(x, y, col)
			elif shadow_alpha > 0.0 and shadow_size > 0.0:
				# 外投影：二次衰减到 reach 处正好为 0（不要硬切），且上方比下方淡
				var ds: float = _sdf(p - off, half, r)
				var reach: float = blur * (0.55 + 0.45 * (shadow_size / SHADOW_BLUR))
				if ds > 0.0 and ds < reach:
					var k: float = 1.0 - ds / reach
					var a: float = shadow_alpha * k * k
					if p.y < 0.0:
						a *= 0.45
					img.set_pixel(x, y, Color(INK_SHADOW.r, INK_SHADOW.g, INK_SHADOW.b, a))
	var out: Image = img.duplicate()
	out.resize(CANVAS, CANVAS, Image.INTERPOLATE_LANCZOS)
	var path := "%s/%s.png" % [OUT_DIR, name]
	var err: int = out.save_png(path)
	if err != OK:
		print("[make_clay] 写盘失败 %s err=%d" % [path, err])
		return 0
	return 1


## 圆角矩形的有符号距离场：<0 在内部
func _sdf(p: Vector2, half: float, r: float) -> float:
	var q := Vector2(absf(p.x) - (half - r), absf(p.y) - (half - r))
	var outside: float = Vector2(maxf(q.x, 0.0), maxf(q.y, 0.0)).length()
	var inside: float = minf(maxf(q.x, q.y), 0.0)
	return outside + inside - r
