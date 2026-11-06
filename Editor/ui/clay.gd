class_name Clay
extends RefCounted

## 控件工厂 —— **P0-2 起，面板里不许再出现裸 `new()`**。
##
## 为什么要有这一层：
##   1. 统一样式来源（样式在 Theme 的类型变体里，工厂只负责"选哪个变体 + 摆内边距"）；
##   2. 一致性自检能机械执行：`grep "Button.new\|CheckBox.new"` 只应出现在本文件；
##   3. P2/P3 重排界面时改一处就全改，不用逐个面板点。
##
## 口径：`docs/ui/UI-R0` §3（按钮家族 / 输入 / 标签分组 / 浮层）。
## ⚠ 本文件只造控件、不改 Doc、不碰 EditorApp，任何地方都可安全调用。

# ============================================================
# 面板
# ============================================================

## 粘土卡（默认 PanelContainer = clay_panel 9-patch）
static func plate(pad: int = UITokens.S4, sep: int = UITokens.S2) -> PanelContainer:
	var p := PanelContainer.new()
	p.add_theme_constant_override("margin_left", pad)
	p.add_theme_constant_override("margin_top", pad)
	p.add_theme_constant_override("margin_right", pad)
	p.add_theme_constant_override("margin_bottom", pad)
	var v := VBoxContainer.new()
	v.add_theme_constant_override("separation", sep)
	v.name = "Body"
	p.add_child(v)
	return p


## 凹槽（分组底 / 输入框底 / 只读信息块）
static func inset(pad: int = UITokens.S2) -> PanelContainer:
	var p := PanelContainer.new()
	p.theme_type_variation = "SunkenPanel"
	p.add_theme_constant_override("margin_left", pad)
	p.add_theme_constant_override("margin_top", pad)
	p.add_theme_constant_override("margin_right", pad)
	p.add_theme_constant_override("margin_bottom", pad)
	return p


## 贴窗口的大台面（无投影无描边）
static func flat() -> PanelContainer:
	var p := PanelContainer.new()
	p.theme_type_variation = "FlatPanel"
	return p


# ============================================================
# 按钮
# ============================================================

## 按下回弹（R0 §4「按钮悬停/按下」）：scale 0.98 再弹回，只补 scale、不动布局。
## ★ 统一挂在这里，面板里就不用每个按钮自己接 `button_down`。
static func _press_feedback(b: Button) -> Button:
	b.button_down.connect(func() -> void: Motion.press(b))
	return b


## kind: "secondary"(默认) / "primary" / "ghost" / "danger"
static func btn(text: String, kind: String = "secondary") -> Button:
	var b := Button.new()
	b.text = text
	match kind:
		"primary":
			b.theme_type_variation = "PrimaryButton"
		"ghost":
			b.theme_type_variation = "GhostButton"
		"danger":
			b.theme_type_variation = "DangerButton"
		_:
			b.theme_type_variation = ""
	return _press_feedback(b)


static func btn_primary(text: String) -> Button:
	return btn(text, "primary")


static func btn_ghost(text: String) -> Button:
	return btn(text, "ghost")


## 图标按钮（40×40 或 32×32 圆底）。没有图标素材时先用文字占位（"道""连""车"…）
static func icon_btn(label: String, box: int = 36) -> Button:
	var b := Button.new()
	b.text = label
	b.theme_type_variation = "IconButton"
	b.custom_minimum_size = Vector2(box, box)
	return _press_feedback(b)


## 胶囊开关（替代 CheckBox）：关=灰底灰字，开=浅橙底橙字橙边
static func pill(text: String, on: bool = false) -> Button:
	var b := Button.new()
	b.text = text
	b.toggle_mode = true
	b.button_pressed = on
	b.theme_type_variation = "PillToggle"
	return _press_feedback(b)


# ============================================================
# 字段行（P3 属性面板统一用这个，修 E91 C17 的列宽抖动）
# ============================================================

## 一行 = [固定 112px 字段名][字段控件][可选单位]
## ★ 列宽取 `UITokens.FIELD_W`（字号 15 下装 7 个汉字）。写死 96 是字号 12 时代的数，
##   现在「独立非机动车道」会被截成「独立非机…」。
static func field_row(label_text: String, field: Control, unit: String = "") -> HBoxContainer:
	var h := HBoxContainer.new()
	h.add_theme_constant_override("separation", UITokens.S2)
	var l := Label.new()
	l.text = label_text
	l.theme_type_variation = "MutedLabel"
	l.custom_minimum_size = Vector2(UITokens.FIELD_W, 0)
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	l.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	l.tooltip_text = label_text
	h.add_child(l)
	field.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	h.add_child(field)
	if unit != "":
		var u := Label.new()
		u.text = unit
		u.theme_type_variation = "HintLabel"
		u.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		h.add_child(u)
	return h


## 分隔线
static func sep() -> HSeparator:
	return HSeparator.new()
