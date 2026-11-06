extends PanelContainer
class_name OutlinePanel

## 左侧对象大纲：按「路 / 路口 / 连接 / 装饰」四组列出全部对象；双击条目 → 相机居中 + 选中它。
##
## ★ 只读 Doc，不另存任何快照：要什么现从 EditorApp.doc 取，重建即丢即取。
## ★ 重建时机：`Doc` 没有"内容变了"的信号（画路 / 删对象都不发 `doc_changed`），
##   所以「0.25s 轮询便宜签名 + 0.08s 防抖」统一收在 **EditorApp**，本面板只订阅
##   `EditorApp.content_changed` → 直接重建。**别再在这里自己写轮询。**
##   拖节点（位置变了、标签不变）不会触发重建，增删对象 / 改类型才会。
## 连接类型的中文显示（与 property_panel 的 TURN_LABELS 同值；那边是面板私有常量，取不到，只能各留一份）
const TURN_CN := {
	"Straight": "直行", "Left": "左转", "Right": "右转",
	"UTurn": "掉头", "Crosswalk": "人行横道",
}

var _tree: Tree = null
var _syncing := false                    ## 防止「树选中 ↔ EditorApp 选中」互相回推
var _items: Dictionary = {}              ## "kind:id" → TreeItem（重建时重填，供高亮定位用）
## 大纲"树长什么样"的签名。★ 只在结构真的变了才重填 Tree。
##   原来每次 `content_changed` 都 `_tree.clear()` 重建 —— 而专业档 `profile.touch()`
##   也会发这个信号（改 SUMO 配置 / 勾开关），于是整棵树白重建一遍（看起来闪一下）。
##   2026-11-06 老板报"调整右侧栏会闪"，这里是第二个受影响的面板。
var _sig := 0

## ★ 每组最多列这么多条（D6）：大纲是导航用的，千级连接时整份建 Tree 要 40 ms 以上，
##   而这种规模本来也没法用鼠标点。分组标题仍然显示**真实总数**，超出部分补一行说明。
const MAX_ITEMS := 500

func _ready() -> void:
	custom_minimum_size = Vector2(188, 0)
	_build_ui()
	# ★ 换文档必须**强制**重建：新旧文档签名可能恰好相同，但 _items 里存的是旧 TreeItem
	EditorApp.doc_changed.connect(func() -> void:
		_sig = 0
		_do_recalc())                                   # 整份换文档：导入 / 撤销 / 新建
	EditorApp.content_changed.connect(_do_recalc)      # 内容变了：画路 / 删对象 / 改类型（EditorApp 统一轮询来的）
	EditorApp.selection_changed.connect(_sync_selection)
	_do_recalc()

func _build_ui() -> void:
	var box := VBoxContainer.new()
	add_child(box)
	# ★ 2026-10-26 P2-2：标题由右侧坞的段头负责（"▾ 大纲"），这里不再自带一个 —— 否则一行里两个标题
	_tree = Tree.new()
	_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_tree.hide_root = true
	_tree.columns = 1
	_tree.select_mode = Tree.SELECT_ROW
	_tree.allow_reselect = true           # 再点同一项也要有反馈（双击要用）
	_tree.item_selected.connect(_on_item_selected)
	_tree.item_activated.connect(_on_item_activated)
	box.add_child(_tree)

# --- 重建 ---------------------------------------------------------------

## 大纲树的内容签名：只放会改变**行文本**的字段（id / 类型 / 端点 / 素材），不含坐标、不含专业档。
## 用整数累加而不是拼长字符串：千级对象时每次 0.25 s 轮询都要算一遍。
func _outline_sig(d: Doc) -> int:
	var h := 17
	h = (h * 131 + d.roads.size()) & 0x7FFFFFFF
	h = (h * 131 + d.nodes.size()) & 0x7FFFFFFF
	h = (h * 131 + d.turns.size()) & 0x7FFFFFFF
	h = (h * 131 + d.decos.size()) & 0x7FFFFFFF
	for r in d.roads:
		h = (h * 131 + int(r.road_id) * 7 + int(r.a_node) * 11 + int(r.b_node) * 13) & 0x7FFFFFFF
	for n in d.nodes:
		h = (h * 131 + int(n.id) * 7 + hash(String(n.get("type", "")))) & 0x7FFFFFFF
	for t in d.turns:
		h = (h * 131 + int(t.id) * 7 + hash(String(t.get("type", "")))
			+ int(t.src_lane) * 11 + int(t.dst_lane) * 13) & 0x7FFFFFFF
	for c in d.decos:
		h = (h * 131 + int(c.id) * 7 + hash(String(c.get("asset", "")))) & 0x7FFFFFFF
	return h


func _do_recalc() -> void:
	if not is_inside_tree() or _tree == null:
		return
	var d: Doc = EditorApp.doc
	if d == null:
		return
	# ★ 先比签名：只改坐标 / 只改专业档（SUMO 配置、开关）都不该重填 Tree ——
	#   重填会 clear 再逐条 add，肉眼就是"闪一下"，而且白花时间。
	var sig := _outline_sig(d)
	if sig == _sig and not _items.is_empty():
		return
	_sig = sig
	_items.clear()
	_tree.clear()
	var root := _tree.create_item()

	# 1) 路
	var g := _group(root, "路", d.roads.size())
	var roads: Array = _head_sorted(d.roads, "road_id")
	var shown := 0
	for r in roads:
		shown += 1
		var it := _tree.create_item(g)
		it.set_text(0, "路 #%d  %d→%d" % [int(r.road_id), int(r.a_node), int(r.b_node)])
		it.set_metadata(0, {"kind": "road", "id": int(r.road_id)})
		_items["road:%d" % int(r.road_id)] = it
	_more_row(g, d.roads.size() - shown)

	# 2) 节点（列全部 nodes[]：起点/终点/普通路口/信号路口在 schema 里都是 nodes[]，只列"真路口"会漏）
	var gn := _group(root, "节点", d.nodes.size())
	var nodes: Array = _head_sorted(d.nodes, "id")
	shown = 0
	for n in nodes:
		shown += 1
		var it2 := _tree.create_item(gn)
		it2.set_text(0, "节点 #%d  %s" % [int(n.id), String(n.get("type", "?"))])
		it2.set_metadata(0, {"kind": "junction", "id": int(n.id)})
		_items["junction:%d" % int(n.id)] = it2
	_more_row(gn, d.nodes.size() - shown)

	# 3) 连接
	var gt := _group(root, "连接", d.turns.size())
	var turns: Array = _head_sorted(d.turns, "id")
	shown = 0
	for t in turns:
		shown += 1
		var it3 := _tree.create_item(gt)
		var ty := String(t.get("type", "?"))
		it3.set_text(0, "连接 #%d  %s  %d→%d" % [
			int(t.id), String(TURN_CN.get(ty, ty)), int(t.src_lane), int(t.dst_lane)])
		it3.set_metadata(0, {"kind": "turn", "id": int(t.id)})
		_items["turn:%d" % int(t.id)] = it3
	_more_row(gt, d.turns.size() - shown)

	# 4) 装饰
	var gd := _group(root, "装饰", d.decos.size())
	var decos: Array = _head_sorted(d.decos, "id")
	shown = 0
	for dc in decos:
		shown += 1
		var it4 := _tree.create_item(gd)
		it4.set_text(0, "装饰 #%d  %s" % [
			int(dc.id), String(dc.get("asset", "")).get_file()])
		it4.set_metadata(0, {"kind": "deco", "id": int(dc.id)})
		_items["deco:%d" % int(dc.id)] = it4
	_more_row(gd, d.decos.size() - shown)

	_sync_selection()

## 取"按 id 升序的前 MAX_ITEMS 条"。
## ★ doc 里的四张表本来就是 id 升序（id 自增、只删不重排）→ 先花 O(n) 判一下，
##   是就原地 slice（不整份 duplicate、也不跑 5 千次 sort_custom 比较）；
##   只有外部导入的文件乱了序才真的排序。这一步在 5313 条连接时能省掉 ~6 ms。
func _head_sorted(items: Array, key: String) -> Array:
	var asc := true
	var prev := -9223372036854775807
	for it in items:
		var v := int((it as Dictionary).get(key, 0))
		if v < prev:
			asc = false
			break
		prev = v
	var limit: int = mini(items.size(), MAX_ITEMS)
	if asc:
		return items.slice(0, limit)
	var cp: Array = items.duplicate()
	cp.sort_custom(func(a, b): return int(a.get(key, 0)) < int(b.get(key, 0)))
	return cp.slice(0, limit)

## 超出上限时补一行说明（不可选，点不到任何东西）
func _more_row(g: TreeItem, hidden: int) -> void:
	if hidden <= 0:
		return
	var it := _tree.create_item(g)
	it.set_text(0, "…… 还有 %d 条未列出（每组最多列 %d 条）" % [hidden, MAX_ITEMS])
	it.set_selectable(0, false)

func _group(root: TreeItem, name_txt: String, count: int) -> TreeItem:
	var g := _tree.create_item(root)
	g.set_text(0, "%s (%d)" % [name_txt, count])
	g.set_selectable(0, false)          # 分组行不可选，免得双击分组跑到空白处
	return g

# --- 交互 ---------------------------------------------------------------

## 单击：只做选中（右侧属性面板跟着变），不动相机
func _on_item_selected() -> void:
	if _syncing:
		return
	var md := _meta()
	if md.is_empty():
		return
	_syncing = true
	EditorApp.select_object(String(md["kind"]), int(md["id"]), false)
	_syncing = false

## 双击（或回车）：选中 + 相机居中
func _on_item_activated() -> void:
	var md := _meta()
	if md.is_empty():
		return
	_syncing = true
	EditorApp.select_object(String(md["kind"]), int(md["id"]), true)
	_syncing = false

func _meta() -> Dictionary:
	var it := _tree.get_selected()
	if it == null:
		return {}
	var md = it.get_metadata(0)
	if not (md is Dictionary):
		return {}                       # 分组行 / 空行
	return md

## 画布上选了东西 → 大纲把对应条目点亮并滚过去（不重建）
func _sync_selection() -> void:
	if _tree == null or _syncing:
		return
	var key := ""
	if EditorApp.selected_deco >= 0:
		key = "deco:%d" % EditorApp.selected_deco
	elif EditorApp.selected_turn >= 0:
		key = "turn:%d" % EditorApp.selected_turn
	elif EditorApp.selected_junction >= 0:
		key = "junction:%d" % EditorApp.selected_junction
	elif EditorApp.selected_road >= 0:
		key = "road:%d" % EditorApp.selected_road
	_syncing = true
	if key != "" and _items.has(key):
		var it: TreeItem = _items[key]
		if it != null:
			var g := it.get_parent()
			if g != null:
				g.collapsed = false          # 分组被收起时先展开，否则滚过去也看不见
			it.select(0)
			_tree.scroll_to_item(it)
	else:
		_tree.deselect_all()
	_syncing = false
