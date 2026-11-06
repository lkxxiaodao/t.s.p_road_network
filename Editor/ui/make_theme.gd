extends SceneTree

## 一次性生成器：把 `ui/tokens.gd` 的令牌铺成 `ui/editor_theme.tres`。
##
## 用法（在工程根目录）：
##   "<Godot console>" --headless --path Editor --script res://ui/make_theme.gd
##
## 什么时候要重跑：
##   - 改了 `ui/tokens.gd` 的任何常量；
##   - 改了 `ui/build_theme.gd` 的控件清单/样式。
## `editor_theme.tres` 是**产物**，别手改 —— 手改会在下次重跑时被覆盖。

const OUT_PATH := "res://ui/editor_theme.tres"


func _initialize() -> void:
	var t: Theme = UIThemeBuilder.build()
	var err: int = ResourceSaver.save(t, OUT_PATH)
	if err != OK:
		print("[make_theme] 失败 err=%d" % err)
		quit(1)
		return
	print("[make_theme] 已生成 %s" % OUT_PATH)
	print("[make_theme] 控件类型 %d 个" % t.get_type_list().size())
	quit(0)
