class_name Motion
extends RefCounted

## 统一动效 —— 所有补间从这里出，禁止各面板自己写一套时长和曲线。
##
## 口径：`docs/ui/UI-R0` §4。三条硬规矩：
##   1. **只在交互瞬间起补间**，不挂 `_process`（画布上 5000 条连接，任何逐帧补间都会拖垮帧率）；
##   2. 只动 `scale / rotation / modulate / position`，不动 `size`（会触发容器重排，卡）；
##   3. 一屏内同时跑的补间 ≤ 12 个（超了说明用错了地方）。
##
## 这里全是"一次性"补间；画布上没有任何逐帧/循环动画。

const D_FADE := 0.18     ## 容器控件的淡入（`fade_in` 的默认时长）
const D_PRESS := 0.06    ## 按下 60ms 到位
const D_REBOUND := 0.18  ## 松开回弹（轻微过冲）
const D_FADE_IN := 0.15  ## 内容淡入
const D_FADE_OUT := 0.09 ## 旧内容淡出
const D_SLIDE := 0.22    ## 模式切换滑块
const D_POP := 0.12      ## 弹出层 scale
const OVERSHOOT := 1.04  ## 回弹过冲


## 按下回弹（按钮）：缩 0.98 → 弹回 1.0，轻微过冲。
## 调用方在 `button_down` 里调一次即可。
static func press(ctrl: Control) -> void:
	if not ctrl.is_inside_tree():
		return
	_center_pivot(ctrl)
	var tw := ctrl.create_tween()
	tw.set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)
	tw.tween_property(ctrl, "scale", Vector2(0.98, 0.98), D_PRESS)
	tw.tween_property(ctrl, "scale", Vector2(OVERSHOOT, OVERSHOOT), D_REBOUND * 0.5)
	tw.tween_property(ctrl, "scale", Vector2.ONE, D_REBOUND * 0.5)


## 弹出层入场：scale 0.96 → 1 + 淡入，pivot 在触发角
static func pop_in(ctrl: Control, pivot: Vector2 = Vector2.ZERO) -> void:
	ctrl.pivot_offset = pivot
	ctrl.scale = Vector2(0.96, 0.96)
	ctrl.modulate = Color(1, 1, 1, 0)
	var tw := ctrl.create_tween()
	tw.set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)
	tw.tween_property(ctrl, "scale", Vector2.ONE, D_POP)
	tw.parallel().tween_property(ctrl, "modulate:a", 1.0, D_POP)


## 容器里的小控件淡入/淡出（Toast、面板小节都用它）。
## ★ 容器每帧重排子节点会覆盖 `position` 补间，所以在 `VBox/HBox` 里**只补 `modulate`**（README 铁律 23）。
static func fade_in(ctrl: Control, dur: float = D_FADE) -> Tween:
	ctrl.modulate = Color(1, 1, 1, 0)
	var tw := ctrl.create_tween()
	tw.set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_OUT)
	tw.tween_property(ctrl, "modulate:a", 1.0, dur)
	return tw


## 滑动（工具条模式高亮块）：位置补间，`TRANS_BACK` 轻微过冲（R0 §4「模式切换」220ms）
static func slide_to(ctrl: Control, target: Vector2, dur: float = D_SLIDE) -> Tween:
	var tw := ctrl.create_tween()
	tw.set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)
	tw.tween_property(ctrl, "position", target, dur)
	return tw


## 脉冲（校验错误数变化时闪一次红）：`modulate` 亮一下再回来，重复 `times` 次
static func pulse(ctrl: Control, times: int = 2, dur: float = 0.12) -> void:
	if not ctrl.is_inside_tree():
		return
	var tw := ctrl.create_tween()
	for i in times:
		tw.tween_property(ctrl, "modulate", Color(1.35, 0.9, 0.85, 1.0), dur)
		tw.tween_property(ctrl, "modulate", Color(1, 1, 1, 1), dur)


## 淡出（收起小节、Toast 自动消失）；要等它跑完再隐藏就用 `.finished`
static func fade_out(ctrl: Control, dur: float = D_FADE_OUT) -> Tween:
	var tw := ctrl.create_tween()
	tw.set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_IN)
	tw.tween_property(ctrl, "modulate:a", 0.0, dur)
	return tw


static func _center_pivot(ctrl: Control) -> void:
	ctrl.pivot_offset = ctrl.size * 0.5
