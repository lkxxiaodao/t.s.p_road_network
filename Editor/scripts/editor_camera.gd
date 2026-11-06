extends Camera2D
class_name EditorCamera

## 编辑器相机：中键拖动平移，滚轮缩放；活动范围限制在 ±4000 米内。

const LIMIT_METERS := 8000.0                       ## 相机中心离原点的最大距离（米）
const LIMIT_PX := LIMIT_METERS * GridCanvas.PPM    ## 换算成世界像素（= 16000）

@export var zoom_factor: float = 1.1      ## 每档滚轮的缩放倍率
@export var zoom_min: float = 0.05
@export var zoom_max: float = 8.0

var _panning := false

func _ready() -> void:
	_clamp_position()

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		if event.pressed:
			# ★ C13：鼠标压在面板/按钮上时别抢滚轮与中键（在右侧属性面板上滚轮会缩放画布，很反直觉）。
			#   只看"按下那一刻"：中键拖起来之后划过面板照旧平移，手势不中断。
			if _mouse_over_ui():
				return
			match event.button_index:
				MOUSE_BUTTON_MIDDLE:
					_panning = true
					get_viewport().set_input_as_handled()
				MOUSE_BUTTON_WHEEL_UP:
					_zoom_at(get_viewport().get_mouse_position(), zoom_factor)
					get_viewport().set_input_as_handled()
				MOUSE_BUTTON_WHEEL_DOWN:
					_zoom_at(get_viewport().get_mouse_position(), 1.0 / zoom_factor)
					get_viewport().set_input_as_handled()
		elif event.button_index == MOUSE_BUTTON_MIDDLE:
			_panning = false
			get_viewport().set_input_as_handled()
	elif event is InputEventMouseMotion and _panning:
		global_position -= event.relative / zoom
		_clamp_position()
		get_viewport().set_input_as_handled()

## 鼠标下面有没有"会吃事件的 UI"。
## ★ `mouse_filter = IGNORE` 的（比如铺满全屏的 HelpPanel、纯 Label）不会被算进来，
##   所以画布区域仍然是 null → 滚轮照旧缩放。
func _mouse_over_ui() -> bool:
	var vp := get_viewport()
	if vp == null:
		return false
	var c := vp.gui_get_hovered_control()
	return c != null

## 以屏幕坐标为锚点缩放，保证光标下的世界点不移动
func _zoom_at(screen_pos: Vector2, factor: float) -> void:
	var view_center := get_viewport_rect().size * 0.5
	var offset := screen_pos - view_center
	var new_zoom: Vector2 = (zoom * factor).clamp(
		Vector2(zoom_min, zoom_min), Vector2(zoom_max, zoom_max))
	# P' = P + (S-C)·(1/Z - 1/Z')
	global_position += offset * (Vector2.ONE / zoom - Vector2.ONE / new_zoom)
	zoom = new_zoom
	_clamp_position()

## 把相机中心夹在 ±LIMIT_PX 内（左右上下一致）
func _clamp_position() -> void:
	global_position = global_position.clamp(
		Vector2(-LIMIT_PX, -LIMIT_PX), Vector2(LIMIT_PX, LIMIT_PX))
