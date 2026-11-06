class_name RoadMarks
extends RefCounted

## 国标标线绘制工具 —— **纯绘制，不碰 Doc、不改状态**。
##
## 口径：`docs/ui/UI-R2` §2（线型与尺寸）、§3（等比简化三规则）。
## 三条铁律（R2 §3）：
##   1. **最小可见宽 1.0 px**：`w_canvas = max(w_m × PPM, 1/zoom)`，屏幕宽永远 ≥1 px；
##   2. **虚线退化**：屏幕段长 < 6 px 时不画虚线段，改 1 px 实线（否则缩远了是摩尔纹）；
##   3. **层级**：标线画在路面之上、连接曲线之下。
##
## 注意单位：本文件所有 `draw_*` 的宽度参数都是**画布像素**（Node2D 局部坐标），
## 不是屏幕像素。换算只在 `w_px()` 里做，别在调用处再乘 zoom。

# =====================================================================
# ★★★ 可调区：标线的尺寸与位置全在这一节 ★★★
#   改完**不用重跑主题、不用改别的文件**，直接 F5 就能看效果。
#   单位统一是「米」（画布像素由 `w_px()` / `PPM = 4 px/m` 换算，别自己乘）。
#   每个常量下面写了"调大 / 调小会怎样"，按需改数值即可。
# =====================================================================

# --- 双黄线（对向车行道分界线，GB 5768.3 §4.2）---
const W_DIVIDER := 0.15        ## 单根黄线宽。国标 0.10~0.15；调小 → 远看会淡成一整条
const DY_GAP_M := 0.15         ## 两根黄线之间的**净间距**。调大 → 中间露出的路面变宽，更加"双线"
const CENTER_TRIM_EXTRA_M := 0.3  ## 中心线比"道路绘制末端"再多让出的余量（米）。
## ★ 中心线长度 = **该路口的车道绘制末端** + 本值，见 `center_trim_m()`。
##   以前这里写死过一个近似值（`车道宽×1.5 + 横道宽`），估算固定按"3 车道"假设 ——
##   一旦某个路口加了车道（路口变大），黄线就会插进路口里。现在改成读**实际退让量**，加多少车道都跟着走。
## 调大 → 黄线更短、离路口更远；调小 → 更贴近路口（0 = 正好到路口边缘）

# --- 停止线（GB 5768.3 §4.2）---
const W_STOP := 0.40           ## 停止线宽（沿车流方向）。国标 0.40，别小于 0.30 否则看不清
const STOP_SETBACK_M := 0.0    ## 停止线从"车道可视末端"再往路口外退多少米。
## ★ 这个数同时决定它和人行横道的**前后关系**：人行横道占 5.25~9.25 m（含下面 CROSS_EXTRA_M），
##   所以 STOP_SETBACK_M 至少要 > 4.0 才不会埋进条纹里；调大 → 停止线更远离横道

# --- 人行横道（GB 5768.3 §4.9）---
const CROSS_WIDTH_M := 3.0     ## 横道沿**车流方向**的宽度（= 条纹长度）。国标 3.0~6.0；调大 → 横道更"深"
const W_CROSS_STRIPE := 0.40   ## 单条条纹宽。国标 0.40~0.45
const GAP_CROSS_STRIPE := 0.60 ## 条纹间距。国标 0.60；改这个会改条纹**条数**（宽度不变）
const CROSS_EXTRA_M := 0.0     ## 横道整体再往路口外挪多少米。正数 → 更远离路口中心
## 默认 0 = 横道内缘刚好贴路口边缘（视觉最干净）；若想留一段"缓冲带"可以给 0.5~1.0

# --- 导向箭头（位置）---
const ARROW_TO_STOP_GAP_M := 3.0  ## 箭头**尖端**离停止线多远（GB 5768.3：不小于 3 m）
## 箭头的纵向位置完全由停止线推出来（见 `arrow_tip_offset_m()`），
## 所以你调 STOP_SETBACK_M 或 CROSS_EXTRA_M 时，箭头会跟着停止线一起挪，不用两边对齐。

## 车行道边缘线（白实线 0.2 m）—— 画在**机动车道组的外缘**（车行道与人行道/非机动车道之间）
const W_EDGE := 0.2

## 白虚线 / 边缘线**整体向道路中心线内收**多少（画布像素，`PPM = 4` → 1 px = 0.25 m）
## 用途：国标线宽只有 0.15~0.20 m，正好压在两车道接缝上时视觉偏"毛"；内收一点点更干净。
## ★ 所有同类线**同向内收同一个值**，所以"虚线之间仍相隔 1 个车道宽"不受影响（间距不变，整体平移）。
const MARK_PULL_IN_PX := 1.0

# --- 虚线周期（米，城市道路 60 km/h 以下）---
const DASH_ON_M := 2.0
const DASH_OFF_M := 4.0

# --- 颜色 ---
const C_WHITE := Color(1.0, 1.0, 1.0, 0.85)   ## 条纹比停止线淡一档，免得盖住连接曲线
const C_YELLOW := Color("#F2C230")

const CROSS_MIN_SEG_PX := 6.0   ## 屏幕段长低于这个 → 虚线退化成实线
const CROSS_MIN_W_PX := 1.2     ## 屏幕线宽低于这个 → 同样退化


## 米 → 画布像素，并保证屏幕上至少 1 px。
static func w_px(w_m: float, zoom: float) -> float:
	return maxf(w_m * float(GridCanvas.PPM), 1.0 / maxf(zoom, 0.0001))


## 停止线**中心**离"车道可视末端"多远（米，往路口外为正）。停止线与箭头共用这一个基准。
static func stop_center_offset_m() -> float:
	return W_STOP * 0.5 + STOP_SETBACK_M


## 导向箭头**尖端**离"车道可视末端"多远（米）。= 停止线外缘 + 国标间距。
static func arrow_tip_offset_m() -> float:
	return stop_center_offset_m() + W_STOP * 0.5 + ARROW_TO_STOP_GAP_M


## 中心线（双黄线）每端该从节点往里截掉多少米。
## `inset_m` = 该路口的**实际退让量**（= 车道画到哪儿为止，由 `_junction_inset_m()` 给）。
## ★ 这就是"黄线长度 = 道路绘制长度"的落法：路口变大 → inset 变大 → 黄线自动缩短，不会插进路口。
static func center_trim_m(inset_m: float) -> float:
	return maxf(inset_m, 0.0) + CENTER_TRIM_EXTRA_M


## 实线
static func line(ci: CanvasItem, p0: Vector2, p1: Vector2,
		w_m: float, c: Color, zoom: float) -> void:
	ci.draw_line(p0, p1, c, w_px(w_m, zoom), true)


## 虚线（不等长周期：国标是 2 m 实 + 4 m 空，不能用 Godot 的 draw_dashed_line —— 那条是等长的）
static func dashed(ci: CanvasItem, p0: Vector2, p1: Vector2,
		w_m: float, c: Color, zoom: float) -> void:
	var pw := w_px(w_m, zoom)
	var seg := DASH_ON_M * float(GridCanvas.PPM)
	if seg * zoom < CROSS_MIN_SEG_PX or pw * zoom < CROSS_MIN_W_PX:
		ci.draw_line(p0, p1, c, pw, true)      # 退化：远景画实线，别出摩尔纹
		return
	var total := p0.distance_to(p1)
	if total < 0.5:
		return
	var dir := (p1 - p0) / total
	var gap := DASH_OFF_M * float(GridCanvas.PPM)
	var t := 0.0
	while t < total:
		var a := p0 + dir * t
		var b := p0 + dir * minf(t + seg, total)
		ci.draw_line(a, b, c, pw, true)
		t += seg + gap


## 停止线：以 `center` 为心、沿 `nrm`（垂直于车流）铺一条白实线，总长 = 2 × half_len。
static func stop_line(ci: CanvasItem, center: Vector2, nrm: Vector2,
		half_len: float, zoom: float) -> void:
	if half_len <= 0.5:
		return
	var n := nrm.normalized()
	line(ci, center - n * half_len, center + n * half_len, W_STOP, C_WHITE, zoom)


## 双黄实线（对向分界）：沿 p0→p1 画两条平行黄线，中心距 = (线宽 + 净间距) / 2 × 2。
static func double_yellow(ci: CanvasItem, p0: Vector2, p1: Vector2, zoom: float) -> void:
	var d := p1 - p0
	if d.length() < 0.5:
		return
	var offset := (W_DIVIDER + DY_GAP_M) * 0.5 * float(GridCanvas.PPM)
	var n := Vector2(-d.y, d.x).normalized() * offset
	line(ci, p0 + n, p1 + n, W_DIVIDER, C_YELLOW, zoom)
	line(ci, p0 - n, p1 - n, W_DIVIDER, C_YELLOW, zoom)


## 人行横道条纹：`center` 是横道中心、`along` 是车流方向、`span` 是过街方向的半长。
## 条纹**平行于车流方向**（国标），所以每条是"沿 along 长 CROSS_WIDTH_M、沿 span 方向宽 W_CROSS_STRIPE"，
## 沿 span 方向每隔 (0.45+0.6) m 排一条。
static func crosswalk(ci: CanvasItem, center: Vector2, along: Vector2, span: float, zoom: float) -> void:
	var a := along.normalized()
	var s := Vector2(-a.y, a.x)
	var half_len := CROSS_WIDTH_M * 0.5 * float(GridCanvas.PPM)
	var step := (W_CROSS_STRIPE + GAP_CROSS_STRIPE) * float(GridCanvas.PPM)
	var pw := w_px(W_CROSS_STRIPE, zoom)
	if step < 0.5:
		return
	var t := -span
	while t <= span:
		var c := center + s * t
		ci.draw_line(c - a * half_len, c + a * half_len, C_WHITE, pw, true)
		t += step
