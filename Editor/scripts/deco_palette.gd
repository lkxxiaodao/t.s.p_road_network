extends PanelContainer

## 装饰模式左侧素材列表：按"分组 → 种类"折叠显示；点一项 = 选中当前素材。
## ★ C10：以前只在 `mode_changed` 时重扫素材目录，往 assets/deco/ 里新丢一张图必须切一次模式才看得见。
##   现在标题行有「↻」按钮（随时重扫），另外在装饰模式下每次切到本面板所在模式仍会自动重扫一次。

var _tree: Tree
var _empty: Label = null          ## 一条素材都没有时的引导（外置目录路径写在这里）

func _ready() -> void:
	custom_minimum_size = Vector2(200, 0)
	var box := VBoxContainer.new()
	add_child(box)

	# ★ 2026-10-27：本面板已从右侧坞搬进画布浮窗（`FloatWindow`），标题由浮窗头条负责，
	#   这一行只留"点一项即选用"的提示 + 一个小「↻ 重扫」。
	#   （上一版只放一个裸 Button，撑出 32 px 空行，浮窗顶部一大片留白。）
	var head := HBoxContainer.new()
	head.add_theme_constant_override("separation", UITokens.S2)
	box.add_child(head)
	var tip := Label.new()
	tip.text = "点一项即选用"
	tip.theme_type_variation = "HintLabel"
	tip.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	tip.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	head.add_child(tip)
	var btn := Clay.icon_btn("↻", 28)
	btn.tooltip_text = "重新扫描 %s（新加的素材不用切模式）" % AssetPaths.deco_dir_for_hint()
	btn.pressed.connect(_refresh)
	head.add_child(btn)

	_tree = Tree.new()
	_tree.hide_root = true
	_tree.columns = 1
	_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_tree.item_selected.connect(_on_pick)
	box.add_child(_tree)

	# ★ 发布版素材是外置的（方案 3）：一个 png 都没有时必须告诉用户"放哪儿"，
	#   否则装饰模式就是一个空列表，看着像坏了。
	_empty = Label.new()
	_empty.theme_type_variation = "HintLabel"
	_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_empty.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_empty.custom_minimum_size = Vector2(160, 0)
	_empty.visible = false
	box.add_child(_empty)

	EditorApp.mode_changed.connect(func(_k): _refresh())
	_refresh()

func _refresh() -> void:
	visible = EditorApp.mode == EditorMode.Kind.DECO
	if not visible:
		return
	_tree.clear()
	var groups := AssetLibrary.scan_deco()
	if groups.is_empty():
		_tree.visible = false
		if _empty != null:
			_empty.visible = true
			_empty.text = "还没有装饰素材。\n把 png 放到：\n%s\n再点上面的 ↻。\n命名：分组_种类.png（如 tree_oak.png）" \
				% AssetPaths.deco_dir_for_hint()
		return
	if _empty != null:
		_empty.visible = false
	_tree.visible = true
	var root := _tree.create_item()
	for group in groups:
		var g := _tree.create_item(root)
		g.set_text(0, group)
		g.set_selectable(0, false)          # 分组行不可选
		for item in groups[group]:
			var leaf := _tree.create_item(g)
			leaf.set_text(0, String(item["label"]))
			leaf.set_metadata(0, String(item["asset"]))     # 选中后从这里取路径

func _on_pick() -> void:
	var sel := _tree.get_selected()
	if sel == null:
		return
	var a = sel.get_metadata(0)
	if a == null:
		return
	EditorApp.current_asset = String(a)
	print("当前素材：", EditorApp.current_asset)
