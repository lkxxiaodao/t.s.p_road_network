class_name UIThemeBuilder
extends RefCounted

## 主题生成器 —— 把 `UITokens` 的令牌铺成一份完整的 `Theme`。
##
## 用法：`ui/make_theme.gd` 跑一次 → 落盘 `ui/editor_theme.tres`。
## 改令牌后**必须重跑**（tres 是产物，不是源文件）。
##
## 口径：`docs/ui/UI-R0-设计规范.md` §1 / §3。
## 本阶段（P0-1）**只用 `StyleBoxFlat`**（外投影 + 描边）表达粘土；
## 内高光/内底影是 P0-2 的 9-patch 干的活，届时**整体替换**这些 StyleBox，不是叠加（R0 §1.4）。
##
## ⚠ 本文件不引用 EditorApp / 场景，可被任何地方安全调用。

const REGULAR := UITokens.FONT_REGULAR
const MEDIUM := UITokens.FONT_MEDIUM


static func build() -> Theme:
	var t := Theme.new()
	t.default_font = load(REGULAR)
	t.default_font_size = UITokens.T_BODY

	_build_base(t)
	_build_labels(t)
	_build_panels(t)
	_build_buttons(t)
	_build_inputs(t)
	_build_toggles(t)
	_build_lists(t)
	_build_popups(t)
	_build_scrollbars(t)
	_build_splits(t)
	_build_tabs(t)
	_build_dialogs(t)
	_build_misc(t)
	_build_menubutton(t)
	_build_icons(t)
	return t


# ============================================================
# 工具
# ============================================================

## 造一个 StyleBoxFlat。margins = Vector4(left, top, right, bottom)。
static func _sb(bg: Color, r: int,
		border: Color = Color(0, 0, 0, 0), border_w: int = 0,
		margins: Vector4 = Vector4(16, 8, 16, 8),
		shadow: Color = Color(0, 0, 0, 0), shadow_size: int = 0,
		shadow_off: Vector2i = Vector2i.ZERO, expand: int = 0) -> StyleBoxFlat:
	var s := StyleBoxFlat.new()
	s.bg_color = bg
	s.corner_radius_top_left = r
	s.corner_radius_top_right = r
	s.corner_radius_bottom_left = r
	s.corner_radius_bottom_right = r
	if border_w > 0:
		s.border_width_left = border_w
		s.border_width_top = border_w
		s.border_width_right = border_w
		s.border_width_bottom = border_w
		s.border_color = border
	s.content_margin_left = margins.x
	s.content_margin_top = margins.y
	s.content_margin_right = margins.z
	s.content_margin_bottom = margins.w
	if shadow_size > 0:
		s.shadow_color = shadow
		s.shadow_size = shadow_size
		s.shadow_offset = shadow_off
	if expand > 0:
		s.expand_margin_left = expand
		s.expand_margin_top = expand
		s.expand_margin_right = expand
		s.expand_margin_bottom = expand
	s.anti_aliasing = true
	return s


## 粘土 9-patch（P0-2 起，所有"需要厚度"的控件都走这里）
##
## 图是 80×80：本体 52×52 居中、四边各留 14 px 给外投影；圆角最外 20（面板档）。
## 所以 texture_margin 必须 ≥ 14（投影）+ 20（圆角）= 34，取 **36** 才不会把圆角切进拉伸区。
## 早先按 64 画布 + margin 26 做过一版 —— 本体 22 + 投影 8 顶到画布边，**投影直接被裁**，
## 看起来只有"一点点边"，粘土感全无；改 80 画布后才有真正的离地阴影。
##
## `expand_margin = 14` 让投影画到控件 rect 外面（纯绘制，不影响布局）。
static func _tex(name: String, margins: Vector4) -> StyleBoxTexture:
	var s := StyleBoxTexture.new()
	s.texture = load("res://ui/tex/%s.png" % name)
	s.texture_margin_left = 36
	s.texture_margin_top = 36
	s.texture_margin_right = 36
	s.texture_margin_bottom = 36
	s.expand_margin_left = 14
	s.expand_margin_top = 14
	s.expand_margin_right = 14
	s.expand_margin_bottom = 14
	s.content_margin_left = margins.x
	s.content_margin_top = margins.y
	s.content_margin_right = margins.z
	s.content_margin_bottom = margins.w
	s.axis_stretch_horizontal = StyleBoxTexture.AXIS_STRETCH_MODE_STRETCH
	s.axis_stretch_vertical = StyleBoxTexture.AXIS_STRETCH_MODE_STRETCH
	return s


# ============================================================
# 基线：默认字体/字号 + 焦点
# ============================================================

static func _build_base(t: Theme) -> void:
	# 焦点环：9-patch 的橙色空环（2 px 描边，中间透明），expand 让它画到控件外面
	var focus := _tex("clay_focus", Vector4(0, 0, 0, 0))
	t.set_stylebox("focus", "Control", focus)


# ============================================================
# Label
# ============================================================

static func _build_labels(t: Theme) -> void:
	t.set_color("font_color", "Label", UITokens.INK_1)
	t.set_font("font", "Label", load(REGULAR))
	t.set_font_size("font_size", "Label", UITokens.T_BODY)
	t.set_constant("line_spacing", "Label", 2)

	# 次级/提示文字用类型变体（控件设 theme_type_variation 即可）
	t.set_type_variation("MutedLabel", "Label")
	t.set_color("font_color", "MutedLabel", UITokens.INK_2)

	t.set_type_variation("HintLabel", "Label")
	t.set_color("font_color", "HintLabel", UITokens.INK_3)

	# 键帽（F1 键位表，P4-2）：SURFACE_1 底 + 底边 2px 深一档，模拟"键的厚度"（R0 §3.4）
	t.set_type_variation("KeyCap", "Label")
	var cap := _sb(UITokens.SURFACE_1, UITokens.R_SM, UITokens.SURFACE_3, UITokens.BORDER_W,
		Vector4(UITokens.S2, 2, UITokens.S2, 2))
	cap.border_width_bottom = 2
	t.set_stylebox("normal", "KeyCap", cap)
	t.set_color("font_color", "KeyCap", UITokens.INK_1)
	t.set_font("font", "KeyCap", load(MEDIUM))
	t.set_font_size("font_size", "KeyCap", UITokens.T_CAP)

	t.set_type_variation("TitleLabel", "Label")
	t.set_color("font_color", "TitleLabel", UITokens.INK_1)
	t.set_font("font", "TitleLabel", load(MEDIUM))
	t.set_font_size("font_size", "TitleLabel", UITokens.T_TITLE)

	t.set_type_variation("DisplayLabel", "Label")
	t.set_color("font_color", "DisplayLabel", UITokens.INK_1)
	t.set_font("font", "DisplayLabel", load(MEDIUM))
	t.set_font_size("font_size", "DisplayLabel", UITokens.T_DISPLAY)

	# 数值/坐标：等宽数字，状态栏与属性面板用
	t.set_type_variation("MonoLabel", "Label")
	t.set_color("font_color", "MonoLabel", UITokens.INK_1)
	t.set_font("font", "MonoLabel", UITokens.tnum_font())
	t.set_font_size("font_size", "MonoLabel", UITokens.T_BODY)

	t.set_type_variation("ErrorLabel", "Label")
	t.set_color("font_color", "ErrorLabel", UITokens.RED_500)

	# RichTextLabel 跟随正文
	t.set_color("default_color", "RichTextLabel", UITokens.INK_1)
	t.set_font("normal_font", "RichTextLabel", load(REGULAR))
	t.set_font("bold_font", "RichTextLabel", load(MEDIUM))
	t.set_font("mono_font", "RichTextLabel", UITokens.tnum_font())
	t.set_font_size("normal_font_size", "RichTextLabel", UITokens.T_BODY)
	t.set_font_size("bold_font_size", "RichTextLabel", UITokens.T_BODY)
	t.set_font_size("mono_font_size", "RichTextLabel", UITokens.T_BODY)


# ============================================================
# 面板 / 卡片
# ============================================================

static func _build_panels(t: Theme) -> void:
	var card := _tex("clay_panel", Vector4(UITokens.S4, UITokens.S4, UITokens.S4, UITokens.S4))
	t.set_stylebox("panel", "Panel", card)
	t.set_stylebox("panel", "PanelContainer", card)
	t.set_stylebox("panel", "ScrollContainer", _sb(Color(0, 0, 0, 0), 0, Color(0, 0, 0, 0), 0,
		Vector4(0, 0, 0, 0)))

	# 贴在窗口上的"大台面"（不投影、不描边）
	t.set_type_variation("FlatPanel", "PanelContainer")
	t.set_stylebox("panel", "FlatPanel", _sb(UITokens.SURFACE_0, 0,
		Color(0, 0, 0, 0), 0, Vector4(0, 0, 0, 0)))

	# 凹槽（输入框底、分组底）—— 9-patch 的 inset 版（内影在上缘）
	t.set_type_variation("SunkenPanel", "PanelContainer")
	t.set_stylebox("panel", "SunkenPanel", _tex("clay_sunken",
		Vector4(UITokens.S3, UITokens.S2, UITokens.S3, UITokens.S2)))

	# 卡片头（40px 高的可点标题行）
	t.set_type_variation("CardHeaderPanel", "PanelContainer")
	t.set_stylebox("panel", "CardHeaderPanel", _sb(UITokens.SURFACE_1, UITokens.R_LG,
		Color(0, 0, 0, 0), 0,
		Vector4(UITokens.S3, UITokens.S2, UITokens.S3, UITokens.S2)))

	# Toast（P4-1）：浮在画布上的轻提示，要和画布拉开层次 —— 近白面 + 描边 + 投影。
	# ★ 左右内边距 S3、上下 S2：一行字的胶囊高 ≈ 40，连按 20 次也不会堆成一堵墙。
	t.set_type_variation("ToastPanel", "PanelContainer")
	t.set_stylebox("panel", "ToastPanel", _sb(UITokens.SURFACE_1, UITokens.R_MD,
		UITokens.SURFACE_3, UITokens.BORDER_W,
		Vector4(UITokens.S3, UITokens.S2, UITokens.S4, UITokens.S2),
		UITokens.SHADOW_REST, 10, Vector2i(0, 2)))


# ============================================================
# 按钮家族
# ============================================================

static func _build_buttons(t: Theme) -> void:
	# --- 次按钮（Button 默认）高 32 ---
	var sec_m := Vector4(UITokens.S4, 6, UITokens.S4, 6)
	t.set_stylebox("normal", "Button", _tex("clay_surface_rest", sec_m))
	t.set_stylebox("hover", "Button", _tex("clay_surface_hover", sec_m))
	t.set_stylebox("pressed", "Button", _tex("clay_surface_pressed", sec_m))
	t.set_stylebox("disabled", "Button", _tex("clay_disabled", sec_m))
	t.set_stylebox("focus", "Button", _tex("clay_focus", Vector4(0, 0, 0, 0)))
	t.set_color("font_color", "Button", UITokens.INK_1)
	t.set_color("font_hover_color", "Button", UITokens.INK_1)
	t.set_color("font_pressed_color", "Button", UITokens.INK_1)
	t.set_color("font_focus_color", "Button", UITokens.INK_1)
	t.set_color("font_hover_pressed_color", "Button", UITokens.INK_1)
	t.set_color("font_disabled_color", "Button", UITokens.INK_3)
	t.set_font("font", "Button", load(REGULAR))
	t.set_font_size("font_size", "Button", UITokens.T_BODY)
	t.set_constant("h_separation", "Button", UITokens.S2)

	# --- 主按钮（每屏最多一个）高 36 ---
	var pri_m := Vector4(UITokens.S5, 8, UITokens.S5, 8)
	t.set_type_variation("PrimaryButton", "Button")
	t.set_stylebox("normal", "PrimaryButton", _tex("clay_orange_rest", pri_m))
	t.set_stylebox("hover", "PrimaryButton", _tex("clay_orange_hover", pri_m))
	t.set_stylebox("pressed", "PrimaryButton", _tex("clay_orange_pressed", pri_m))
	t.set_stylebox("disabled", "PrimaryButton", _tex("clay_disabled", pri_m))
	t.set_stylebox("focus", "PrimaryButton", _tex("clay_focus", Vector4(0, 0, 0, 0)))
	t.set_color("font_color", "PrimaryButton", Color.WHITE)
	t.set_color("font_hover_color", "PrimaryButton", Color.WHITE)
	t.set_color("font_pressed_color", "PrimaryButton", Color.WHITE)
	t.set_color("font_focus_color", "PrimaryButton", Color.WHITE)
	t.set_color("font_disabled_color", "PrimaryButton", UITokens.INK_3)
	t.set_font("font", "PrimaryButton", load(MEDIUM))
	t.set_font_size("font_size", "PrimaryButton", UITokens.T_BODY)

	# --- 幽灵按钮（面板内的轻操作）---
	var gho_m := Vector4(UITokens.S3, 6, UITokens.S3, 6)
	t.set_type_variation("GhostButton", "Button")
	t.set_stylebox("normal", "GhostButton", _sb(Color(0, 0, 0, 0), UITokens.R_MD,
		Color(0, 0, 0, 0), 0, gho_m))
	t.set_stylebox("hover", "GhostButton", _sb(UITokens.SURFACE_2, UITokens.R_MD,
		Color(0, 0, 0, 0), 0, gho_m))
	t.set_stylebox("pressed", "GhostButton", _sb(UITokens.SURFACE_3, UITokens.R_MD,
		Color(0, 0, 0, 0), 0, gho_m))
	t.set_stylebox("disabled", "GhostButton", _sb(Color(0, 0, 0, 0), UITokens.R_MD,
		Color(0, 0, 0, 0), 0, gho_m))
	t.set_stylebox("focus", "GhostButton", _sb(Color(0, 0, 0, 0), UITokens.R_MD,
		UITokens.ORANGE_500, UITokens.FOCUS_W, Vector4(0, 0, 0, 0), Color(0, 0, 0, 0), 0,
		Vector2i.ZERO, UITokens.FOCUS_EXPAND))
	t.set_color("font_color", "GhostButton", UITokens.INK_2)
	t.set_color("font_hover_color", "GhostButton", UITokens.INK_1)
	t.set_color("font_pressed_color", "GhostButton", UITokens.INK_1)
	t.set_color("font_disabled_color", "GhostButton", UITokens.INK_3)
	t.set_font("font", "GhostButton", load(REGULAR))
	t.set_font_size("font_size", "GhostButton", UITokens.T_LABEL)

	# --- 危险按钮 ---
	t.set_type_variation("DangerButton", "Button")
	t.set_stylebox("normal", "DangerButton", _tex("clay_danger", sec_m))
	t.set_stylebox("hover", "DangerButton", _tex("clay_danger", sec_m))
	t.set_stylebox("pressed", "DangerButton", _tex("clay_surface_pressed", sec_m))
	t.set_stylebox("disabled", "DangerButton", _tex("clay_disabled", sec_m))
	t.set_stylebox("focus", "DangerButton", _tex("clay_focus", Vector4(0, 0, 0, 0)))
	t.set_color("font_color", "DangerButton", UITokens.RED_500)
	t.set_color("font_hover_color", "DangerButton", UITokens.RED_500)
	t.set_color("font_pressed_color", "DangerButton", UITokens.RED_500)
	t.set_color("font_disabled_color", "DangerButton", UITokens.INK_3)

	# --- 图标按钮（圆底）---
	var icon_m := Vector4(6, 6, 6, 6)
	t.set_type_variation("IconButton", "Button")
	t.set_stylebox("normal", "IconButton", _sb(Color(0, 0, 0, 0), UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, icon_m))
	t.set_stylebox("hover", "IconButton", _sb(UITokens.SURFACE_2, UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, icon_m))
	t.set_stylebox("pressed", "IconButton", _sb(UITokens.ORANGE_50, UITokens.R_PILL,
		UITokens.ORANGE_300, UITokens.BORDER_W, icon_m))
	t.set_stylebox("disabled", "IconButton", _sb(Color(0, 0, 0, 0), UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, icon_m))
	t.set_stylebox("focus", "IconButton", _sb(Color(0, 0, 0, 0), UITokens.R_PILL,
		UITokens.ORANGE_500, UITokens.FOCUS_W, Vector4(0, 0, 0, 0), Color(0, 0, 0, 0), 0,
		Vector2i.ZERO, UITokens.FOCUS_EXPAND))
	t.set_constant("icon_max_width", "IconButton", 20)

	# ★ 工具条的 4 个模式按钮（P4-2）：按下态**透明** —— 橙底由前面那块滑动的 `ModeGlider` 提供，
	#   按钮自己再铺一层就会把滑块盖住、也就"滑"不起来了。测/吸仍用 IconButton（它们的按下态就是要自己亮）。
	t.set_type_variation("ModeButton", "IconButton")
	t.set_stylebox("pressed", "ModeButton", _sb(Color(0, 0, 0, 0), UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, icon_m))

	# 滑动高亮块：工具条模式切换时从旧图标位置滑到新图标位置（R0 §4「模式切换」）
	t.set_type_variation("ModeGlider", "Panel")
	t.set_stylebox("panel", "ModeGlider", _sb(UITokens.ORANGE_50, UITokens.R_PILL,
		UITokens.ORANGE_300, UITokens.BORDER_W, Vector4(0, 0, 0, 0)))

	# --- 分段按钮（2~4 个互斥选项）---
	# ★ 内置图标染深色（2026-10-28）：`FileDialog` 的后退/前进/刷新/新建文件夹/收藏 全是**白色**图形，
	#   压在奶油卡面上等于隐形（只剩一排空圆）。Button 的 `icon_*_color` 是"乘算"，白色 × INK = 深色，
	#   不用另画一套图标。我们自己造的按钮没有图标，不受影响。
	t.set_color("icon_normal_color", "Button", UITokens.INK_2)
	t.set_color("icon_hover_color", "Button", UITokens.INK_1)
	t.set_color("icon_pressed_color", "Button", UITokens.ORANGE_700)
	t.set_color("icon_focus_color", "Button", UITokens.INK_1)
	t.set_color("icon_disabled_color", "Button", UITokens.INK_3)

	t.set_type_variation("SegmentButton", "Button")
	t.set_stylebox("normal", "SegmentButton", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("hover", "SegmentButton", _sb(UITokens.SURFACE_2, UITokens.R_SM,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("pressed", "SegmentButton", _sb(UITokens.SURFACE_1, UITokens.R_SM,
		UITokens.ORANGE_300, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5),
		UITokens.SHADOW_REST, 3, Vector2i(0, 1)))
	t.set_stylebox("disabled", "SegmentButton", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("focus", "SegmentButton", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		UITokens.ORANGE_500, UITokens.FOCUS_W, Vector4(0, 0, 0, 0), Color(0, 0, 0, 0), 0,
		Vector2i.ZERO, UITokens.FOCUS_EXPAND))
	t.set_color("font_color", "SegmentButton", UITokens.INK_2)
	t.set_color("font_pressed_color", "SegmentButton", UITokens.ORANGE_800)
	t.set_color("font_hover_color", "SegmentButton", UITokens.INK_1)
	t.set_font_size("font_size", "SegmentButton", UITokens.T_LABEL)


# ============================================================
# 输入框与数值
# ============================================================

static func _build_inputs(t: Theme) -> void:
	var in_m := Vector4(UITokens.S3, 5, UITokens.S3, 5)
	# 凹陷：9-patch 的 inset 版（内影在上缘，外无投影）
	t.set_stylebox("normal", "LineEdit", _tex("clay_sunken", in_m))
	# 聚焦：白底 + 2px 橙边（不能直接用 clay_focus，那张中间是透明的，文字会浮在画布上）
	t.set_stylebox("focus", "LineEdit", _tex("clay_surface_focus", in_m))
	t.set_stylebox("read_only", "LineEdit", _tex("clay_disabled", in_m))
	t.set_color("font_color", "LineEdit", UITokens.INK_1)
	t.set_color("font_placeholder_color", "LineEdit", UITokens.INK_3)
	t.set_color("font_uneditable_color", "LineEdit", UITokens.INK_3)
	t.set_color("caret_color", "LineEdit", UITokens.ORANGE_600)
	t.set_color("selection_color", "LineEdit", UITokens.ORANGE_100)
	t.set_font("font", "LineEdit", load(REGULAR))
	t.set_font_size("font_size", "LineEdit", UITokens.T_BODY)
	t.set_constant("caret_width", "LineEdit", 1)

	# 数值框：右侧上下小按钮（图标留给 clay.gd / P0-2，这里只定尺寸与配色）
	t.set_color("font_color", "SpinBox", UITokens.INK_1)
	t.set_font("font", "SpinBox", UITokens.tnum_font())
	t.set_font_size("font_size", "SpinBox", UITokens.T_BODY)

	# 下拉
	t.set_stylebox("normal", "OptionButton", _sb(UITokens.SURFACE_1, UITokens.R_SM,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5),
		UITokens.SHADOW_REST, 3, Vector2i(0, 1)))
	t.set_stylebox("hover", "OptionButton", _sb(UITokens.SURFACE_1, UITokens.R_SM,
		UITokens.ORANGE_100, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("pressed", "OptionButton", _sb(UITokens.SURFACE_2, UITokens.R_SM,
		UITokens.ORANGE_300, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("disabled", "OptionButton", _sb(UITokens.SURFACE_3, UITokens.R_SM,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("focus", "OptionButton", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		UITokens.ORANGE_500, UITokens.FOCUS_W, Vector4(0, 0, 0, 0), Color(0, 0, 0, 0), 0,
		Vector2i.ZERO, UITokens.FOCUS_EXPAND))
	t.set_color("font_color", "OptionButton", UITokens.INK_1)
	t.set_color("font_hover_color", "OptionButton", UITokens.INK_1)
	t.set_color("font_pressed_color", "OptionButton", UITokens.INK_1)
	t.set_color("font_disabled_color", "OptionButton", UITokens.INK_3)
	t.set_font("font", "OptionButton", load(REGULAR))
	t.set_font_size("font_size", "OptionButton", UITokens.T_BODY)

	# 滑杆
	t.set_type_variation("SliderBar", "HSlider")
	t.set_stylebox("slider", "HSlider", _sb(UITokens.SURFACE_3, UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, Vector4(0, 3, 0, 3)))
	t.set_stylebox("grabber_area", "HSlider", _sb(UITokens.ORANGE_300, UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, Vector4(0, 3, 0, 3)))
	t.set_stylebox("grabber_area_highlight", "HSlider", _sb(UITokens.ORANGE_500, UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, Vector4(0, 3, 0, 3)))


# ============================================================
# 开关：CheckBox（P0-1 先改样式，P0-2 再换 Pill 图标）
# ============================================================

static func _build_toggles(t: Theme) -> void:
	var m := Vector4(UITokens.S2, 4, UITokens.S2, 4)
	# ⚠ 背景一律透明：CheckBox 在 VBox 里会横向撑满，"选中给橙色底"会变成**整行高亮**，
	#   标签一长就很难看。P0-1 只靠图标+文字色表达状态；P0-2 换 Pill 时由 clay.gd 画胶囊底。
	t.set_stylebox("normal", "CheckBox", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	t.set_stylebox("hover", "CheckBox", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	t.set_stylebox("pressed", "CheckBox", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	t.set_stylebox("hover_pressed", "CheckBox", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	t.set_stylebox("disabled", "CheckBox", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	t.set_stylebox("focus", "CheckBox", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		UITokens.ORANGE_500, UITokens.FOCUS_W, m, Color(0, 0, 0, 0), 0,
		Vector2i.ZERO, UITokens.FOCUS_EXPAND))
	t.set_color("font_color", "CheckBox", UITokens.INK_1)
	t.set_color("font_pressed_color", "CheckBox", UITokens.ORANGE_800)
	t.set_color("font_hover_color", "CheckBox", UITokens.INK_1)
	t.set_color("font_hover_pressed_color", "CheckBox", UITokens.ORANGE_800)
	t.set_color("font_disabled_color", "CheckBox", UITokens.INK_3)
	t.set_font("font", "CheckBox", load(REGULAR))
	t.set_font_size("font_size", "CheckBox", UITokens.T_LABEL)
	t.set_constant("h_separation", "CheckBox", UITokens.S2)
	t.set_constant("check_v_offset", "CheckBox", 0)

	# --- Pill 胶囊开关（替代 CheckBox，R0 §3.1）---
	# 关 = 灰底灰字；开 = 浅橙底 + 橙字 + 橙描边。toggle 的"开"走 pressed 态。
	# 左右内边距 S2=8：胶囊最小宽 ≈ 两字 30 + 16 = 46，配时勾选表一行才排得下
	# （S3=12 时最小宽 54，两颗胶囊 + turn 名会把卡片顶到 226，最小坞宽下溢出）
	var pm := Vector4(UITokens.S2, 3, UITokens.S2, 3)
	t.set_type_variation("PillToggle", "Button")
	# ★ 关态底色必须是**真的灰**（SURFACE_3）。曾经用 SURFACE_2：它和凹槽/卡片的底几乎同色，
	#   1 px 描边在 1080p 上根本看不见 → 一排胶囊看起来就是"一串灰字"（2026-10-27 配时勾选表、
	#   2026-10-28 导出弹窗的格式选择都栽在这）。SURFACE_3 与 SURFACE_2 差一档，形状立刻立住。
	t.set_stylebox("normal", "PillToggle", _sb(UITokens.SURFACE_3, UITokens.R_PILL,
		UITokens.SURFACE_3, UITokens.BORDER_W, pm))
	t.set_stylebox("hover", "PillToggle", _sb(UITokens.SURFACE_2, UITokens.R_PILL,
		UITokens.SURFACE_3, UITokens.BORDER_W, pm))
	t.set_stylebox("pressed", "PillToggle", _sb(UITokens.ORANGE_50, UITokens.R_PILL,
		UITokens.ORANGE_500, UITokens.BORDER_W, pm))
	t.set_stylebox("hover_pressed", "PillToggle", _sb(UITokens.ORANGE_100, UITokens.R_PILL,
		UITokens.ORANGE_500, UITokens.BORDER_W, pm))
	t.set_stylebox("disabled", "PillToggle", _sb(UITokens.SURFACE_2, UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, pm))
	t.set_stylebox("focus", "PillToggle", _tex("clay_focus", Vector4(0, 0, 0, 0)))
	t.set_color("font_color", "PillToggle", UITokens.INK_2)
	t.set_color("font_hover_color", "PillToggle", UITokens.INK_1)
	t.set_color("font_pressed_color", "PillToggle", UITokens.ORANGE_800)
	t.set_color("font_hover_pressed_color", "PillToggle", UITokens.ORANGE_800)
	t.set_color("font_disabled_color", "PillToggle", UITokens.INK_3)
	t.set_font("font", "PillToggle", load(REGULAR))
	t.set_font_size("font_size", "PillToggle", UITokens.T_LABEL)


# ============================================================
# 列表 / 树
# ============================================================

static func _build_lists(t: Theme) -> void:
	var list_panel := _sb(UITokens.SURFACE_1, UITokens.R_SM,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(2, 2, 2, 2))

	# --- ItemList ---
	t.set_stylebox("panel", "ItemList", list_panel)
	t.set_stylebox("focus", "ItemList", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		UITokens.ORANGE_500, UITokens.BORDER_W, Vector4(2, 2, 2, 2)))
	t.set_stylebox("selected", "ItemList", _sb(UITokens.ORANGE_50, UITokens.R_XS,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S2, 2, UITokens.S2, 2)))
	t.set_stylebox("selected_focus", "ItemList", _sb(UITokens.ORANGE_50, UITokens.R_XS,
		UITokens.ORANGE_300, UITokens.BORDER_W, Vector4(UITokens.S2, 2, UITokens.S2, 2)))
	t.set_stylebox("hovered", "ItemList", _sb(UITokens.SURFACE_2, UITokens.R_XS,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S2, 2, UITokens.S2, 2)))
	t.set_stylebox("cursor", "ItemList", _sb(Color(0, 0, 0, 0), UITokens.R_XS,
		UITokens.ORANGE_500, UITokens.BORDER_W, Vector4(UITokens.S2, 2, UITokens.S2, 2)))
	t.set_stylebox("cursor_unfocused", "ItemList", _sb(Color(0, 0, 0, 0), UITokens.R_XS,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(UITokens.S2, 2, UITokens.S2, 2)))
	t.set_color("font_color", "ItemList", UITokens.INK_1)
	t.set_color("font_selected_color", "ItemList", UITokens.ORANGE_800)
	t.set_color("font_hovered_color", "ItemList", UITokens.INK_1)
	t.set_font("font", "ItemList", load(REGULAR))
	t.set_font_size("font_size", "ItemList", UITokens.T_LABEL)
	t.set_constant("v_separation", "ItemList", 4)
	t.set_constant("h_separation", "ItemList", 4)

	# --- Tree（大纲面板）---
	t.set_stylebox("panel", "Tree", list_panel)
	t.set_stylebox("focus", "Tree", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		UITokens.ORANGE_500, UITokens.BORDER_W, Vector4(2, 2, 2, 2)))
	t.set_stylebox("selected", "Tree", _sb(UITokens.ORANGE_50, UITokens.R_XS,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S2, 1, UITokens.S2, 1)))
	t.set_stylebox("selected_focus", "Tree", _sb(UITokens.ORANGE_50, UITokens.R_XS,
		UITokens.ORANGE_300, UITokens.BORDER_W, Vector4(UITokens.S2, 1, UITokens.S2, 1)))
	t.set_stylebox("cursor", "Tree", _sb(Color(0, 0, 0, 0), UITokens.R_XS,
		UITokens.ORANGE_500, UITokens.BORDER_W, Vector4(UITokens.S2, 1, UITokens.S2, 1)))
	t.set_stylebox("cursor_unfocused", "Tree", _sb(Color(0, 0, 0, 0), UITokens.R_XS,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(UITokens.S2, 1, UITokens.S2, 1)))
	# ★ 行悬停（大纲 / 装饰素材两个 Tree 都用）：**必须显式覆盖**。
	#   默认主题的 `hovered` 是「白 7%」（实测 bg=(1,1,1,0.07)），压在 SURFACE_1 的近白卡面上
	#   等于给整行刷了一层白漆 —— 老板 2026-11-03 说的"悬停是白色、很晃眼"就是它。
	#   改成比卡面深一档的 SURFACE_2，和选中态（ORANGE_50）分开：悬停=灰，选中=橙。
	t.set_stylebox("hovered", "Tree", _sb(UITokens.SURFACE_2, UITokens.R_XS,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S2, 1, UITokens.S2, 1)))
	t.set_stylebox("button_hover", "Tree", _sb(UITokens.SURFACE_2, UITokens.R_XS,
		Color(0, 0, 0, 0), 0, Vector4(2, 2, 2, 2)))
	t.set_color("font_color", "Tree", UITokens.INK_1)
	t.set_color("font_selected_color", "Tree", UITokens.ORANGE_800)
	t.set_color("font_disabled_color", "Tree", UITokens.INK_3)
	t.set_color("guide_color", "Tree", UITokens.SURFACE_3)
	t.set_color("relationship_line_color", "Tree", UITokens.SURFACE_3)
	t.set_color("parent_hl_line_color", "Tree", UITokens.ORANGE_300)
	t.set_color("children_hl_line_color", "Tree", UITokens.SURFACE_3)
	t.set_color("drop_position_color", "Tree", UITokens.ORANGE_500)
	t.set_font("font", "Tree", load(REGULAR))
	t.set_font_size("font_size", "Tree", UITokens.T_LABEL)
	t.set_constant("v_separation", "Tree", 4)
	t.set_constant("h_separation", "Tree", 4)
	t.set_constant("item_margin", "Tree", UITokens.S4)


# ============================================================
# 弹出层 / 菜单 / 提示
# ============================================================

## 顶栏菜单（MenuButton）—— **必须显式铺一套**：
## Godot 按类型查主题，`MenuButton` 查不到就落到**默认主题**，不会继承 `Button` 的样式；
## 不铺的话顶栏菜单会顶着默认深色皮肤压在奶油底上（第一次接主题时就是这样）。
static func _build_menubutton(t: Theme) -> void:
	var m := Vector4(UITokens.S3, 6, UITokens.S3, 6)
	# ★★ 三种状态的**内边距必须一样**（2026-10-27 老板报的"悬停变白块、字没了"就是这条）：
	#   normal 原来用 StyleBoxEmpty（0 内边距）→ 按钮最小宽 = 文字宽 = 30 px；
	#   悬停换成带 12+12 内边距的样式 → 内容区只剩 6 px，文字被裁光，看起来就是个空胶囊。
	#   现在 normal 也用透明 StyleBoxFlat 带同样边距，悬停只换底色，宽度纹丝不动。
	t.set_stylebox("normal", "MenuButton", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	# 悬停底原来是 SURFACE_2（#F7F2EA），比顶栏的 SURFACE_0（#F4EFE8）**还浅**，等于白按钮压白底；
	# 改成 SURFACE_3（#ECE4D9，明显深一档）+ 橙字，悬停才有一眼可见的反馈。
	t.set_stylebox("hover", "MenuButton", _sb(UITokens.SURFACE_3, UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	t.set_stylebox("pressed", "MenuButton", _sb(UITokens.ORANGE_50, UITokens.R_SM,
		UITokens.ORANGE_100, UITokens.BORDER_W, m))
	# ★ 漏了 hover_pressed 会**落到默认主题**：弹层打开时菜单变成一块红色（默认主题的 hover 色）。
	t.set_stylebox("hover_pressed", "MenuButton", _sb(UITokens.ORANGE_50, UITokens.R_SM,
		UITokens.ORANGE_300, UITokens.BORDER_W, m))
	t.set_stylebox("disabled", "MenuButton", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		Color(0, 0, 0, 0), 0, m))
	t.set_stylebox("focus", "MenuButton", _sb(Color(0, 0, 0, 0), UITokens.R_SM,
		UITokens.ORANGE_500, UITokens.FOCUS_W, Vector4(0, 0, 0, 0), Color(0, 0, 0, 0), 0,
		Vector2i.ZERO, UITokens.FOCUS_EXPAND))
	for c: String in ["font_color", "font_pressed_color", "font_focus_color"]:
		t.set_color(c, "MenuButton", UITokens.INK_1)
	t.set_color("font_hover_color", "MenuButton", UITokens.ORANGE_800)
	t.set_color("font_hover_pressed_color", "MenuButton", UITokens.ORANGE_800)
	t.set_color("font_disabled_color", "MenuButton", UITokens.INK_3)
	t.set_font("font", "MenuButton", load(REGULAR))
	t.set_font_size("font_size", "MenuButton", UITokens.T_LABEL)


static func _build_popups(t: Theme) -> void:
	var pop := _sb(UITokens.SURFACE_1, UITokens.R_LG,
		UITokens.SURFACE_3, UITokens.BORDER_W,
		Vector4(UITokens.S3, UITokens.S3, UITokens.S3, UITokens.S3),
		UITokens.SHADOW_REST, 10, Vector2i(0, 4))
	t.set_stylebox("panel", "PopupMenu", pop)
	t.set_stylebox("hover", "PopupMenu", _sb(UITokens.ORANGE_50, UITokens.R_SM,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S2, 2, UITokens.S2, 2)))
	t.set_stylebox("separator", "PopupMenu", _sb(UITokens.SURFACE_3, 0,
		Color(0, 0, 0, 0), 0, Vector4(0, 1, 0, 1)))
	t.set_color("font_color", "PopupMenu", UITokens.INK_1)
	t.set_color("font_hover_color", "PopupMenu", UITokens.ORANGE_800)
	t.set_color("font_disabled_color", "PopupMenu", UITokens.INK_3)
	t.set_color("font_separator_color", "PopupMenu", UITokens.INK_2)
	t.set_color("font_accelerator_color", "PopupMenu", UITokens.INK_3)
	t.set_font("font", "PopupMenu", load(REGULAR))
	t.set_font_size("font_size", "PopupMenu", UITokens.T_LABEL)
	t.set_constant("v_separation", "PopupMenu", 6)
	t.set_constant("item_start_padding", "PopupMenu", UITokens.S3)
	t.set_constant("item_end_padding", "PopupMenu", UITokens.S3)

	# Tooltip：深底白字（浅色 UI 上对比更强）
	t.set_stylebox("panel", "TooltipPanel", _sb(UITokens.INK_1, UITokens.R_SM,
		Color(0, 0, 0, 0), 0, Vector4(UITokens.S2, 4, UITokens.S2, 4)))
	t.set_color("font_color", "TooltipLabel", Color(1, 1, 1, 0.95))
	t.set_font("font", "TooltipLabel", load(REGULAR))
	t.set_font_size("font_size", "TooltipLabel", UITokens.T_CAP)


# ============================================================
# 滚动条
# ============================================================

static func _build_scrollbars(t: Theme) -> void:
	var track := _sb(Color(0, 0, 0, 0), UITokens.R_PILL, Color(0, 0, 0, 0), 0, Vector4(2, 0, 2, 0))
	var grab := _sb(UITokens.SURFACE_3, UITokens.R_PILL, Color(0, 0, 0, 0), 0, Vector4(2, 0, 2, 0))
	var grab_hi := _sb(UITokens.INK_3, UITokens.R_PILL, Color(0, 0, 0, 0), 0, Vector4(2, 0, 2, 0))
	for type_name: String in ["VScrollBar", "HScrollBar", "ScrollBar"]:
		t.set_stylebox("scroll", type_name, track)
		t.set_stylebox("grabber", type_name, grab)
		t.set_stylebox("grabber_highlight", type_name, grab_hi)
		t.set_stylebox("grabber_pressed", type_name, grab_hi)


# ============================================================
# 分栏
# ============================================================

static func _build_splits(t: Theme) -> void:
	for type_name: String in ["HSplitContainer", "VSplitContainer", "SplitContainer"]:
		t.set_constant("separation", type_name, 6)
		t.set_constant("minimum_grab_thickness", type_name, 6)


# ============================================================
# Tab
# ============================================================

static func _build_tabs(t: Theme) -> void:
	t.set_stylebox("panel", "TabContainer", _sb(UITokens.SURFACE_1, UITokens.R_LG,
		UITokens.SURFACE_3, UITokens.BORDER_W,
		Vector4(UITokens.S3, UITokens.S3, UITokens.S3, UITokens.S3)))
	for type_name: String in ["TabContainer", "TabBar"]:
		t.set_stylebox("tab_selected", type_name, _sb(UITokens.SURFACE_1, UITokens.R_SM,
			UITokens.ORANGE_300, UITokens.BORDER_W, Vector4(UITokens.S3, 4, UITokens.S3, 4)))
		t.set_stylebox("tab_unselected", type_name, _sb(Color(0, 0, 0, 0), UITokens.R_SM,
			Color(0, 0, 0, 0), 0, Vector4(UITokens.S3, 4, UITokens.S3, 4)))
		t.set_stylebox("tab_hovered", type_name, _sb(UITokens.SURFACE_2, UITokens.R_SM,
			Color(0, 0, 0, 0), 0, Vector4(UITokens.S3, 4, UITokens.S3, 4)))
		t.set_color("font_selected_color", type_name, UITokens.ORANGE_800)
		t.set_color("font_unselected_color", type_name, UITokens.INK_2)
		t.set_color("font_hovered_color", type_name, UITokens.INK_1)
		t.set_font("font", type_name, load(REGULAR))
		t.set_font_size("font_size", type_name, UITokens.T_LABEL)


# ============================================================
# 对话框 / 窗口
# ============================================================

static func _build_dialogs(t: Theme) -> void:
	t.set_stylebox("panel", "AcceptDialog", _sb(UITokens.SURFACE_1, UITokens.R_XL,
		UITokens.SURFACE_3, UITokens.BORDER_W,
		Vector4(UITokens.S5, UITokens.S5, UITokens.S5, UITokens.S5),
		UITokens.SHADOW_REST, 16, Vector2i(0, 6)))
	t.set_stylebox("embedded_border", "Window", _sb(UITokens.SURFACE_1, UITokens.R_XL,
		UITokens.SURFACE_3, UITokens.BORDER_W,
		Vector4(UITokens.S5, UITokens.S5, UITokens.S5, UITokens.S5),
		UITokens.SHADOW_REST, 16, Vector2i(0, 6)))
	t.set_stylebox("embedded_unfocused_border", "Window", _sb(UITokens.SURFACE_1, UITokens.R_XL,
		UITokens.SURFACE_3, UITokens.BORDER_W,
		Vector4(UITokens.S5, UITokens.S5, UITokens.S5, UITokens.S5)))
	t.set_font("title_font", "Window", load(MEDIUM))
	t.set_font_size("title_font_size", "Window", UITokens.T_TITLE)
	t.set_color("title_color", "Window", UITokens.INK_1)
	# ★ 关闭按钮：Godot 内置的 `close` 是**白色**图形，压在奶油卡面上等于隐形（2026-10-28 实测）。
	#   和 P0-1 那批 grabber/arrow 同一个坑，必须换成深色自绘版。
	t.set_icon("close", "Window", UIIcons.cross(16, UITokens.INK_2, 2.0))
	t.set_icon("close_pressed", "Window", UIIcons.cross(16, UITokens.ORANGE_700, 2.0))


# ============================================================
# 其它
# ============================================================

static func _build_misc(t: Theme) -> void:
	# 分隔线
	for type_name: String in ["HSeparator", "VSeparator", "Separator"]:
		t.set_constant("separation", type_name, UITokens.S3)
	t.set_stylebox("separator", "HSeparator", _sb(UITokens.SURFACE_3, 0,
		Color(0, 0, 0, 0), 0, Vector4(0, 1, 0, 1)))
	t.set_stylebox("separator", "VSeparator", _sb(UITokens.SURFACE_3, 0,
		Color(0, 0, 0, 0), 0, Vector4(1, 0, 1, 0)))

	# 进度条
	t.set_stylebox("background", "ProgressBar", _sb(UITokens.SURFACE_3, UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, Vector4(0, 3, 0, 3)))
	t.set_stylebox("fill", "ProgressBar", _sb(UITokens.ORANGE_300, UITokens.R_PILL,
		Color(0, 0, 0, 0), 0, Vector4(0, 3, 0, 3)))
	t.set_color("font_color", "ProgressBar", UITokens.INK_1)

	# FoldableContainer（"文件"折叠框）
	t.set_font("title_font", "FoldableContainer", load(MEDIUM))
	t.set_font_size("title_font_size", "FoldableContainer", UITokens.T_BODY)
	t.set_color("title_color", "FoldableContainer", UITokens.INK_1)
	t.set_stylebox("title_panel", "FoldableContainer", _sb(UITokens.SURFACE_1, UITokens.R_MD,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("title_hover_panel", "FoldableContainer", _sb(UITokens.SURFACE_2, UITokens.R_MD,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("title_collapsed_panel", "FoldableContainer", _sb(UITokens.SURFACE_1, UITokens.R_MD,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5)))
	t.set_stylebox("title_collapsed_hover_panel", "FoldableContainer", _sb(UITokens.SURFACE_2, UITokens.R_MD,
		UITokens.SURFACE_3, UITokens.BORDER_W, Vector4(UITokens.S3, 5, UITokens.S3, 5)))

	# MenuButton / LinkButton 跟随 Button 家族
	t.set_font("font", "MenuButton", load(REGULAR))
	t.set_font_size("font_size", "MenuButton", UITokens.T_LABEL)
	t.set_color("font_color", "LinkButton", UITokens.BLUE_500)
	t.set_color("font_hover_color", "LinkButton", UITokens.ORANGE_600)


# ============================================================
# 基础图标 —— 只解决"默认白色图标在奶油底上消失"（P0-1 验收第一眼抓到的坑）
# ============================================================

static func _build_icons(t: Theme) -> void:
	var ink := UITokens.INK_2
	var ink_hi := UITokens.INK_1
	var off := UITokens.SURFACE_3

	# --- CheckBox：内置的白色方框/勾压奶油底完全看不见，换成自绘的圆角方框 ---
	t.set_icon("unchecked", "CheckBox", UIIcons.box(15, UITokens.SURFACE_1, ink))
	t.set_icon("checked", "CheckBox", UIIcons.box(15, UITokens.ORANGE_500, UITokens.ORANGE_600, true))
	t.set_icon("unchecked_disabled", "CheckBox", UIIcons.box(15, UITokens.SURFACE_3, off))
	t.set_icon("checked_disabled", "CheckBox", UIIcons.box(15, UITokens.SURFACE_3, off, true))
	# 同时把内置图标的着色也调到可用区间（万一某处回退到默认图标）
	t.set_color("icon_normal_color", "CheckBox", ink)
	t.set_color("icon_hover_color", "CheckBox", ink_hi)
	t.set_color("icon_pressed_color", "CheckBox", UITokens.ORANGE_600)
	t.set_color("icon_hover_pressed_color", "CheckBox", UITokens.ORANGE_600)
	t.set_color("icon_disabled_color", "CheckBox", off)

	# --- SpinBox 上下箭头 ---
	t.set_icon("up", "SpinBox", UIIcons.triangle(9, ink, true))
	t.set_icon("down", "SpinBox", UIIcons.triangle(9, ink, false))
	t.set_icon("up_disabled", "SpinBox", UIIcons.triangle(9, off, true))
	t.set_icon("down_disabled", "SpinBox", UIIcons.triangle(9, off, false))

	# --- OptionButton 下拉箭头 ---
	t.set_icon("arrow", "OptionButton", UIIcons.triangle(9, ink, false))
	t.set_icon("arrow_mirrored", "OptionButton", UIIcons.triangle(9, ink, true))

	# --- Tree 展开/收起三角 ---
	t.set_icon("arrow", "Tree", UIIcons.triangle(10, ink, false))            # 已展开 → 朝下
	t.set_icon("arrow_collapsed", "Tree", UIIcons.triangle_right(10, ink))   # 已收起 → 朝右

	# --- 滑杆滑块 ---
	var knob: ImageTexture = UIIcons.circle(18, UITokens.SURFACE_1, UITokens.SURFACE_3, 1.5)
	var knob_hi: ImageTexture = UIIcons.circle(18, Color.WHITE, UITokens.ORANGE_500, 2.0)
	var knob_off: ImageTexture = UIIcons.circle(18, UITokens.SURFACE_3, off, 1.0)
	for type_name: String in ["HSlider", "VSlider", "Slider"]:
		t.set_icon("grabber", type_name, knob)
		t.set_icon("grabber_highlight", type_name, knob_hi)
		t.set_icon("grabber_disabled", type_name, knob_off)

	# --- PopupMenu 勾选 / 单选 ---
	t.set_icon("checked", "PopupMenu", UIIcons.check(16, UITokens.ORANGE_600, 2.0))
	t.set_icon("radio_checked", "PopupMenu", UIIcons.circle(16, UITokens.ORANGE_600, UITokens.ORANGE_600, 0.0))
	t.set_icon("radio_unchecked", "PopupMenu", UIIcons.circle(16, UITokens.SURFACE_1, ink, 1.5))
