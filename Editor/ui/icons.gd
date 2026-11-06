class_name UIIcons
extends RefCounted

## 控件必需的基础图标 —— **只解决"浅色底上默认白色图标看不见"这一件事**。
##
## 业务图标（道路/连接/车辆/装饰…约 40 个线性图标）在 P0-2 的 `ui/icons.gd` 里做，
## 那个是 SVG 资源管线；本文件是**运行时用 Image 现画**的 4 个功能性图形
## （三角/圆点），因为它们必须在主题生成时就有，不能等素材。
##
## 为什么必须自己画：Godot 内置的 `grabber` / `arrow` / `up` / `down` 全是**白色**图形，
## 配深色主题没问题，压到奶油底（#F4EFE8）上直接消失。这是 P0-1 验收时肉眼抓到的第一个坑。

const SS := 4   ## 超采样倍率（先画 4 倍大再缩，边缘才不糊）


## 三角（用于 SpinBox 上下、Tree 折叠箭头、OptionButton 下拉）
static func triangle(size: int, color: Color, point_up: bool = true) -> ImageTexture:
	var n: int = size * SS
	var img: Image = Image.create(n, n, false, Image.FORMAT_RGBA8)
	img.fill(Color(0, 0, 0, 0))
	for y in range(n):
		var t: float = float(y) / float(maxi(n - 1, 1))
		if not point_up:
			t = 1.0 - t
		var half: float = (float(n) * 0.5) * t
		var x0: int = int(ceil(float(n) * 0.5 - half))
		var x1: int = int(floor(float(n) * 0.5 + half))
		for x in range(maxi(x0, 0), mini(x1 + 1, n)):
			img.set_pixel(x, y, color)
	return _done(img, size)


## 朝右的三角（Tree 收起状态用）
static func triangle_right(size: int, color: Color) -> ImageTexture:
	return _rotated_right(size, color)


## 圆点（滑杆滑块）
static func circle(size: int, fill: Color, border: Color, border_w: float = 1.5) -> ImageTexture:
	var n: int = size * SS
	var img: Image = Image.create(n, n, false, Image.FORMAT_RGBA8)
	img.fill(Color(0, 0, 0, 0))
	var c: float = float(n) * 0.5
	var r: float = c - float(SS)
	var bw: float = border_w * float(SS)
	for y in range(n):
		for x in range(n):
			var d: float = Vector2(float(x) + 0.5 - c, float(y) + 0.5 - c).length()
			if d <= r - bw:
				img.set_pixel(x, y, fill)
			elif d <= r:
				img.set_pixel(x, y, border)
	return _done(img, size)


## 勾（复选框选中）—— 两段线，超采样手绘
static func check(size: int, color: Color, thick: float = 2.0) -> ImageTexture:
	var n: int = size * SS
	var img: Image = Image.create(n, n, false, Image.FORMAT_RGBA8)
	img.fill(Color(0, 0, 0, 0))
	var w: int = maxi(1, int(thick * float(SS)))
	var p0 := Vector2(float(n) * 0.18, float(n) * 0.52)
	var p1 := Vector2(float(n) * 0.42, float(n) * 0.76)
	var p2 := Vector2(float(n) * 0.84, float(n) * 0.24)
	_segment(img, p0, p1, w, color)
	_segment(img, p1, p2, w, color)
	return _done(img, size)


## 叉（关闭 / 删除）：两条对角线
static func cross(size: int, color: Color, thick: float = 2.0) -> ImageTexture:
	var n: int = size * SS
	var img: Image = Image.create(n, n, false, Image.FORMAT_RGBA8)
	img.fill(Color(0, 0, 0, 0))
	var w: int = maxi(1, int(thick * float(SS)))
	var lo: float = float(n) * 0.26
	var hi: float = float(n) * 0.74
	_segment(img, Vector2(lo, lo), Vector2(hi, hi), w, color)
	_segment(img, Vector2(hi, lo), Vector2(lo, hi), w, color)
	return _done(img, size)


static func _segment(img: Image, a: Vector2, b: Vector2, w: int, c: Color) -> void:
	var steps: int = int(a.distance_to(b) * 2.0) + 1
	for i in range(steps + 1):
		var p: Vector2 = a.lerp(b, float(i) / float(maxi(steps, 1)))
		for dy in range(-w, w + 1):
			for dx in range(-w, w + 1):
				var x: int = int(p.x) + dx
				var y: int = int(p.y) + dy
				if x >= 0 and y >= 0 and x < img.get_width() and y < img.get_height():
					if Vector2(float(dx), float(dy)).length() <= float(w):
						img.set_pixel(x, y, c)


## 圆角方框（复选框）：可带白勾 = 选中态
static func box(size: int, fill: Color, border: Color, with_check: bool = false) -> ImageTexture:
	var n: int = size * SS
	var img: Image = Image.create(n, n, false, Image.FORMAT_RGBA8)
	img.fill(Color(0, 0, 0, 0))
	var bw: float = float(SS)
	var r: float = 3.0 * float(SS)
	var lo: float = bw * 0.5
	var hi: float = float(n) - bw * 0.5
	for y in range(n):
		for x in range(n):
			var px: float = float(x) + 0.5
			var py: float = float(y) + 0.5
			if not _in_round_rect(px, py, lo, lo, hi, hi, r):
				continue
			if not _in_round_rect(px, py, lo + bw, lo + bw, hi - bw, hi - bw, maxf(r - bw, 0.5)):
				img.set_pixel(x, y, border)
			else:
				img.set_pixel(x, y, fill)
	if with_check:
		var w: int = maxi(1, int(2.0 * float(SS)))
		_segment(img, Vector2(float(n) * 0.26, float(n) * 0.52),
			Vector2(float(n) * 0.44, float(n) * 0.70), w, Color.WHITE)
		_segment(img, Vector2(float(n) * 0.44, float(n) * 0.70),
			Vector2(float(n) * 0.76, float(n) * 0.32), w, Color.WHITE)
	return _done(img, size)


static func _in_round_rect(px: float, py: float, x0: float, y0: float, x1: float, y1: float, r: float) -> bool:
	if px < x0 or px > x1 or py < y0 or py > y1:
		return false
	var cx: float = clampf(px, x0 + r, x1 - r)
	var cy: float = clampf(py, y0 + r, y1 - r)
	return Vector2(px - cx, py - cy).length() <= r


static func _rotated_right(size: int, color: Color) -> ImageTexture:
	var n: int = size * SS
	var img: Image = Image.create(n, n, false, Image.FORMAT_RGBA8)
	img.fill(Color(0, 0, 0, 0))
	# 尖朝右：列 x 的可见 y 范围随 x 变宽
	for x in range(n):
		var t: float = float(x) / float(maxi(n - 1, 1))
		var half: float = (float(n) * 0.5) * t
		var y0: int = int(ceil(float(n) * 0.5 - half))
		var y1: int = int(floor(float(n) * 0.5 + half))
		for y in range(maxi(y0, 0), mini(y1 + 1, n)):
			img.set_pixel(x, y, color)
	return _done(img, size)


static func _done(img: Image, size: int) -> ImageTexture:
	img.resize(size, size, Image.INTERPOLATE_LANCZOS)
	return ImageTexture.create_from_image(img)
