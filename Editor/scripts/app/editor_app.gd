extends Node

signal mode_changed(kind: int)
signal doc_changed()
signal selection_changed()
## ★ 文档**内容**变更广播（画路 / 删对象 / 改类型这类 Doc 内部改动）。
##   为什么需要它：`Doc` 是纯数据类（RefCounted），**一个 signal 都没有**，而 `doc_changed`
##   只在整份换文档（导入 / 撤销 / 新建）时发 —— 视图拿不到"内容变了"的事件。
##   所以统一在这里做：**0.25s 轮询便宜签名 → 0.08s 防抖 → 广播**。
##   ★ 各视图只许订阅本信号，**不许再各自写轮询**（否则 N 个轮询器同时扫全图，签名口径还不一致）。
signal content_changed()
## ★ 能力级（基础 / 专业）切换。只影响**界面可见性**，不影响数据与导出（技术方案06 §6.1）。
signal edition_changed(level: int)
## ★ SUMO 探针出结果了（成功或失败都发；失败原因看 `sumo_probe.error`）。
signal sumo_probe_changed()
## ★ 一键试跑出结果了（成功或失败都发；失败原因看 `sumo_run.error`）。
signal sumo_run_changed()

var mode: int = EditorMode.Kind.ROAD
var doc := Doc.new()
## SUMO 专业档（`<关卡>.sumo.json`）。**编辑器私有，不进 .spmap**。
## ★ 永远不是 null：空档 = `SumoProfile.empty()`，省得各处判空。
var profile: SumoProfile = SumoProfile.empty()
## 基础 / 专业（`Edition.Level`）。会话级偏好，只决定"界面上露出多少东西"。
var edition: int = Edition.Level.BASIC
## netconvert 探针（真实 link 表）。慢查询，结果按内容指纹缓存。
var sumo_probe := SumoProbe.new()
## 一键试跑（S5）：后台跑 `sumo -c <cfg>` 并解析统计。**不缓存**——点一次就跑一次。
var sumo_run := SumoRun.new()
## 专业配时矩阵里"当前这一行"对应的转向：画布据此高亮连接线。
## ★ 独立于 `selected_turn`（那会让属性面板切走，配时面板就没了）。-1 = 不高亮。
var sumo_highlight_turn: int = -1
var road_mode := RoadMode.new()
var connect_mode := ConnectMode.new()
var deco_mode := DecoMode.new()
var traffic_mode := TrafficMode.new()
var selected_road: int = -1
var selected_turn: int = -1     ## 选中的连接 id（连接模式用）；-1 = 无
var selected_junction: int = -1   ## 选中的路口节点 id（连接模式用）；-1 = 无
var selected_deco: int = -1      ## 选中的装饰 id（装饰模式用）；-1 = 无
var selected_island: int = -1    ## 选中的安全岛 id（E92 G2 · N6b）；-1 = 无
## ★ 多选层（矩形框选路）：只装 road id。selected_* 仍是"最后选中项"，其它模式/面板的读法一律不改。
var selection: Array = []
var traffic_lane: int = -1     ## 车辆模式选中的车道（-1 = 无）
## 选中的检测设施 id（S3）；空串 = 无。
## ★ 与 traffic_lane 联动：点设施会顺带选中它所在的车道（属性面板才有内容可显示）。
var selected_facility: String = ""
var traffic_turn: int = -1     ## 车辆模式选中的过街转向（-1 = 无）
var deco_layer: Node2D = null      ## DecoLayer 自己注册进来（它的 _ready 里设置）
var current_asset: String = ""     ## 装饰模式选中的素材（相对 assets/ 的路径，如 "deco/tree_oak.png"）
var clipboard: Dictionary = {}          ## 复制粘贴的剪贴板：{"kind": "deco"/"road", "data": {...}}
var current_path := ""                          ## 当前关卡文件（OS 绝对路径；空 = 还没存过）
var _pending_proceed := Callable()              ## "先保存再继续"时，保存成功后要接着做的事
var new_road_template: Dictionary = {}  ## 新建路的默认横断面（空 = 用 Doc.default_template()）
var _paste_n: int = 0                   ## 连续粘贴计数（每次多偏一格，避免叠在一起）
var pending_src_lane: int = -1              ## 手动画连接：已选的进口车道 id（-1 = 未选）
var junction_candidates: Dictionary = {}    ## 选中路口的 SimCore 候选：{ "src-dst" -> type }
var validation_errors: int = -1     ## 校验错误数（只算 Error）；-1 = 尚未校验
## 上一次校验的**原始条目**（Error + Warning，含 Code/Message/Path/Severity）。
## ★ P4-1 的校验清单直接读它 —— 不再重跑校验，也不新增任何校验规则。
var validation_error_items: Array = []
var _content_poll := 0.0                    ## 内容变更：轮询计时
var _content_sig := ""                      ## 内容变更：上次的便宜签名
var _content_timer: SceneTreeTimer = null    ## 内容变更：防抖计时器（非 null = 已有待广播）
var recent_files: Array = []                ## ★ 最近文件（OS 路径，最新在前，最多 RECENT_MAX 条；B20）
var _autosave_acc := 0.0                    ## ★ 自动保存：计时累加（秒）
var _autosave_warned := false               ## ★ 自动保存：同一个失败原因只弹一次对话框
var _recover_prompted := false               ## ★ 崩溃恢复：启动时只问一次

const TMP_RES := "user://_export_tmp.spmap"     ## 临时文件（SimCore 的 Load 只吃文件路径）

## ★ 这些校验项只属于“游戏原始格式”（关卡信息 / 评分锚点 / 指标权重），
##   外部格式（shp / net.xml）不消费它们，所以不能因为它们拦住外部导出。
const GAME_ONLY_VALIDATION_CODES := [
	"anchors_unset",
	"anchor_ordering_inverted",
	"weights_invalid",
	"weights_not_normalized",
]
const GAME_ONLY_VALIDATION_PATHS := ["meta", "game_rules"]

# ★ 自动保存 / 崩溃恢复 / 最近文件（B20）。全部落在 user://，不进 .spmap、不进工程目录。
## 自动保存目录：last.spmap + last.spdeco.json（同目录同名，恢复时走正常导入路径能自动配上装饰）
const AUTOSAVE_DIR := "user://autosave"
const AUTOSAVE_SPMAP := "user://autosave/last.spmap"
const AUTOSAVE_META := "user://autosave/last.meta.json"      ## 旁注：原路径 / 脏标记 / 时间 / 校验错误数
const AUTOSAVE_FLAG := "user://autosave/session.flag"        ## 运行中标记：它还在 = 上次没正常退出
const AUTOSAVE_INTERVAL_SEC := 300.0                        ## 自动保存间隔（5 分钟）
const RECENT_FILE := "user://recent_files.json"              ## 最近文件配置
const RECENT_MAX := 8                                        ## 最近文件上限（条）

const MAX_UNDO := 100      ## 撤销栈上限
const COALESCE_MS := 300   ## 连续微调合并窗口（毫秒）：按住 SpinBox 箭头只算一步
const CONTENT_POLL_SEC := 0.25       ## 文档内容变更：轮询间隔
const CONTENT_DEBOUNCE_SEC := 0.08   ## 文档内容变更：广播防抖窗口（E99 配方）

## 连接自动生成的会话级开关（原 const SKIP_UTURN / SKIP_CROSSWALK）
## ★ 只活在内存里，不写进 .spmap（schema 没这两个字段）；改勾【不会】自动重推已生成的连接，要再按一次 G
var include_uturn: bool = false      ## 含掉头（默认不勾）
var include_crosswalk: bool = true   ## 含过街（默认勾）

## ★ 图层开关（B19）。**会话级**：不进 .spmap / .spdeco，也不写 user://（重开编辑器 = 全开）。
##   读取点：网格/底图在 grid_canvas.gd::_draw()，车道/连接/冲突/标注在 road_layer.gd::_draw()，
##   装饰在 DecoLayer.visible（唯一能整块关的一层，由 apply_layer_visibility() 落实）。
var show_grid: bool = true           ## 网格（含坐标轴与原点十字；背景板不受它管）
var show_lanes: bool = true          ## 车道（含路口补面）
var show_turns: bool = true          ## 连接（转向曲线）
var show_decos: bool = true          ## 装饰
var show_annotations: bool = true    ## 标注（进口转向箭头 + 节点白点）
var show_basemap: bool = true        ## 底图（描图垫）
var help_panel: Node = null          ## F1 键位表浮层（help_panel.gd 自己注册进来，同 deco_layer 的手法）

var _undo: Array = []
var _redo: Array = []
var _pending: Dictionary = {}        ## 当前手势"改动前"的快照（空 = 没在记）
var _pending_tag: String = ""        ## 当前手势的 tag（决定"能不能并进上一步"）
var _last_tag: String = ""           ## 上一步落定时的 tag（同一 tag 才允许合并）
var _last_end_ms: int = 0            ## 上次落定时间（用于合并连续微调）
var _title_base := ""            ## 标题栏原文（首次 _process 时抓取）
var _title_dirty := false        ## 上一次标题里的脏状态，变了才 set
var _bridge: Node = null
var _export_dialog: Window = null
var _type_dialog: Window = null       ## 车型库弹窗（2026-11-05：车型定义从面板挪进弹窗）
## 全局车型库同步：`_types_lib_sig` 是"上次写盘时的车型库签名"。
## ★ 载入关卡后要把它重置成载入后的签名，否则打开旧关卡会把新车型库覆盖掉。
var _types_lib_sig: String = ""
var _types_lib_acc: float = 0.0
var _inv_node: Dictionary = {}      ## 文件 node id → 实时 node id
var _inv_lane: Dictionary = {}      ## 文件 lane id → 实时 lane id
var _inv_turn: Dictionary = {}      ## 文件 turn id → 实时 turn id
var _fwd_node: Dictionary = {}      ## 实时 node id → 文件 node id
var _fwd_lane: Dictionary = {}      ## 实时 lane id → 文件 lane id（专业档存盘用）
var _fwd_turn: Dictionary = {}      ## 实时 turn id → 文件 turn id（专业档存盘用）

## 内置对话框（FileDialog / AcceptDialog）的中文化。见 `ui/builtin_zh.gd`。
const BuiltinZh := preload("res://ui/builtin_zh.gd")

func _ready() -> void:
	BuiltinZh.install()                        # ★ 必须最早：内置对话框的默认按钮文案在**创建时**查表
	AssetPaths.ensure_external_dir()            # ★ 发布版：第一次运行时建好 <exe 同目录>/assets/deco/
	get_tree().auto_accept_quit = false        # ★ 自己接管关窗，否则 Godot 直接关掉
	_content_sig = content_signature()         # ★ 初始化签名，避免启动时空广播一次
	# ★ 车型库在启动时就带上全局库，并**立刻对齐同步签名**。
	#   少了这一步，_process 第一次同步时 profile 还是空的 → 会把全局库清空（致命）。
	profile.vehicle_types = SumoTypesLibrary.load_types()
	_reset_types_library_sig()
	# ★ 能力级（基础 / 专业）：会话级偏好，只影响界面可见性
	edition = Edition.load_level()
	print("能力级：%s（Ctrl+Shift+E 切换；专业档在打开关卡时随 .sumo.json 载入）"
		% Edition.label(edition))
	# ★ 配时矩阵的行高亮属于"选中"，任何选中变化都该清掉它 ——
	#   挂在这里而不是逐个 select_* 里写，保证以后新增选中入口不会漏（2026-11-04）。
	selection_changed.connect(clear_sumo_highlight)
	# ★ 自动保存 / 崩溃恢复 / 最近文件（B20）：顺序不能颠倒 —— 先判上次的标记，再落本次的标记
	_autosave_begin_session()
	_load_recent()
	_apply_dpi_scale()          # ★ 必须在最小窗 sizing 之前：那里的数字要乘系统缩放
	_apply_min_window_size()


## 高 DPI（P5 复查）：Windows 上 Godot 4 默认 `allow_hidpi=true`，声明 DPI-aware 之后
## 窗口与界面都按**物理像素** 1:1 画 —— 系统缩放 150% 的屏上，字会比 100% 屏上小 1/3。
## 这里把窗口的 `content_scale_factor` 设成系统缩放，UI 就改按**逻辑单位**绘制：
## 物理上大小与 100% 屏一致，而且仍然是原生分辨率（不模糊）。
## 100% 缩放下 `sc = 1.0`，这里什么也不做 —— 不影响任何现有布局。
func _apply_dpi_scale() -> void:
	if DisplayServer.get_name() == "headless":
		return
	var w := get_window()
	if w == null:
		return
	var sc := DisplayServer.screen_get_max_scale()
	if sc <= 1.01:
		return
	w.content_scale_factor = sc
	print("高 DPI：系统缩放 %.2f → 界面按逻辑单位绘制（窗口 %.0f×%.0f 逻辑）" % [
		sc, w.size.x / sc, w.size.y / sc])


## 窗口最小尺寸 —— 由**外壳各栏的实际尺寸**推出来，不是拍的数字：
##   宽 = 左栏 + 图层列 + 画布最小 + 坞最小 + 两条缝
##   高 = 顶栏 + 上下文条 + 画布最小 + 提示条 + 状态栏 + 缝
## 低于这个尺寸就会出现"工具互相遮挡 / 文字被栏裁掉"，所以必须设死；
## 高于它随便拉（横竖比 16:9、16:10、4:3 都只是画布变大变小，各栏靠锚点自适应）。
func _apply_min_window_size() -> void:
	if DisplayServer.get_name() == "headless":
		return
	var canvas_min_w := 460
	var canvas_min_h := 240
	var w: int = UITokens.TOOL_RAIL_W + UITokens.S2 + UITokens.LAYER_RAIL_W + UITokens.S2 \
		+ canvas_min_w + UITokens.DOCK_W_MIN + UITokens.S2
	var h: int = UITokens.TOP_BAR_H + UITokens.S3 + UITokens.CONTEXT_BAR_H + UITokens.S3 \
		+ canvas_min_h + UITokens.HINT_BAR_H + UITokens.BAR_GAP + UITokens.STATUS_BAR_H
	# ★ 最小尺寸是**物理像素**：高 DPI 下界面按逻辑单位画，两者差一个 `content_scale_factor`
	var sc: float = 1.0
	if get_window() != null:
		sc = maxf(1.0, get_window().content_scale_factor)
	DisplayServer.window_set_min_size(Vector2i(int(round(float(w) * sc)), int(round(float(h) * sc))))
	print("窗口最小尺寸：%d × %d（逻辑 %d × %d，缩放 %.2f）" % [
		int(round(float(w) * sc)), int(round(float(h) * sc)), w, h, sc])

## 标题栏跟着脏标记走（改了就在末尾加 *）；顺带轮询文档内容变更
func _process(delta: float) -> void:
	var w := get_window()          # ★ headless / --script 下可能为 null，别直接 w.title
	if w != null:
		if _title_base == "":
			_title_base = w.title
		if doc.dirty != _title_dirty:
			_title_dirty = doc.dirty
			w.title = _title_base + (" *" if _title_dirty else "")
	# ★ 自动保存（B20）：到点就写。序列化走的是深拷贝（见 autosave_now），所以
	#   哪怕这一帧正在拖动也不会动到实时 id，不需要为手势让路。
	_autosave_acc += delta
	if _autosave_acc >= AUTOSAVE_INTERVAL_SEC:
		_autosave_acc = 0.0
		autosave_now()
	# ★ 全局车型库：车型定义不该跟着关卡存亡 —— 0.5 s 节流比较签名，变了才写
	#   （`user://sumo_vehicle_library.json`，见 SumoTypesLibrary 的注释）
	_types_lib_acc += delta
	if _types_lib_acc >= 0.5:
		_types_lib_acc = 0.0
		_sync_types_library()
	# ★ SUMO 探针：只在"有任务在跑"时轮询，平时零开销
	if sumo_probe.busy:
		if sumo_probe.poll(get_export_bridge()):
			sumo_probe_changed.emit()
			if sumo_probe.error != "":
				print("SUMO 探针失败：%s" % sumo_probe.error)
			else:
				print("SUMO 探针完成：%d 个信号灯位 / %d 条边" % [
					(sumo_probe.result.get("tls", []) as Array).size(),
					(sumo_probe.result.get("edges", []) as Array).size()])
	# ★ 一键试跑：同样只在"有任务在跑"时轮询（S5）
	if sumo_run.busy:
		if sumo_run.poll(get_export_bridge()):
			# ★ 先把摘要算成字符串再 emit：emit 会同步唤醒 await 的面板，面板可能立刻发起
			#   下一次试跑并清空 sumo_run.result —— 那之后再读就是空的了（日志会打成"（没有统计）"）。
			var err := sumo_run.error
			var summary := "" if err != "" else _sumo_stats_line(sumo_run.stats())
			sumo_run_changed.emit()
			if err != "":
				print("试跑失败：%s" % err)
			else:
				print("试跑完成：%s" % summary)
	# ★ 内容变更轮询（Doc 没有变化信号，只能这样发现；见 content_changed 的注释）
	_content_poll += delta
	if _content_poll < CONTENT_POLL_SEC:
		return
	_content_poll = 0.0
	var s := content_signature()
	if s != _content_sig:
		_content_sig = s
		_schedule_content_broadcast()

## 便宜签名：只覆盖"大纲 / 错误列表会显示的东西"——各类数量 + 各对象 id + 类型首字母。
## ★ 故意不含坐标：拖节点不该触发重建。
## ★ 2026-10-24 W6（D1）：改成**算术哈希**。原来每个对象做一次 "%d%s" 格式化，
##   5312 条连接时一次要 5.6 ms —— 而它每 0.25 s 就要算一次。
func content_signature() -> String:
	if doc == null:
		return ""
	var h := 17
	h = h * 31 + doc.nodes.size()
	h = h * 31 + doc.roads.size()
	h = h * 31 + doc.turns.size()
	h = h * 31 + doc.decos.size()
	# ★ 专业档的改动也要能触发重建：只取 revision 计数，不 stringify 整份专业档
	#   （它每 0.25 s 被调一次，不能干重活）
	h = h * 31 + profile.revision
	for n in doc.nodes:
		h = (h * 131 + int(n.id) * 7 + _type_seed(n.get("type", ""))) & 0x7FFFFFFF
	for t in doc.turns:
		h = (h * 131 + int(t.id) * 7 + _type_seed(t.get("type", ""))) & 0x7FFFFFFF
	return "%d/%d/%d/%d/%d" % [doc.nodes.size(), doc.roads.size(),
		doc.turns.size(), doc.decos.size(), h]

## 类型名的哈希种子。★ 用引擎自带的 `hash()`（C++ 侧算），比 "转 String → unicode_at(0)" 便宜一半；
##   而且比旧版"只取首字母"更不容易撞（Unsignalized / Signalized 本来就只差一个字母）。
static func _type_seed(v) -> int:
	return hash(v)

## E99 防抖：0.08s 内多次变化只广播一次
func _schedule_content_broadcast() -> void:
	if _content_timer != null:
		return                                  # 已有待广播的
	var t := get_tree()
	if t == null:                               # 极端情况（未进树 / 自测脚手架）→ 直接广播
		content_changed.emit()
		return
	_content_timer = t.create_timer(CONTENT_DEBOUNCE_SEC)
	_content_timer.timeout.connect(func():
		_content_timer = null
		content_changed.emit())

# --- 图层开关（B19；会话级，不写文件）------------------------------------

## 图层开关的唯一入口：面板勾选 → 这里。六个键写死，避免各处再各判一套。
func set_layer(key: String, on: bool) -> void:
	match key:
		"grid": show_grid = on
		"lanes": show_lanes = on
		"turns": show_turns = on
		"decos": show_decos = on
		"annotations": show_annotations = on
		"basemap": show_basemap = on
		_:
			push_warning("图层开关：不认识的键 %s" % key)
			return
	apply_layer_visibility()

## 只有「装饰」要显式落实（独立节点 → visible）；其余五层各自的 _draw 每帧读开关，不用通知。
func apply_layer_visibility() -> void:
	if deco_layer != null and is_instance_valid(deco_layer):
		deco_layer.visible = show_decos

## 面板构建时回显用（键名与 set_layer 一致）
func layer_enabled(key: String) -> bool:
	match key:
		"grid": return show_grid
		"lanes": return show_lanes
		"turns": return show_turns
		"decos": return show_decos
		"annotations": return show_annotations
		"basemap": return show_basemap
	return true

# --- 能力级：基础 / 专业（技术方案06 §6.1）--------------------------------

## 切换能力级。★ 只改界面可见性：不改数据、不改导出、不置脏。
func set_edition(level: int) -> void:
	if level != Edition.Level.BASIC and level != Edition.Level.PRO:
		return
	if edition == level:
		return
	edition = level
	Edition.save_level(level)
	apply_edition_visibility()
	edition_changed.emit(level)
	print("能力级切换为：%s" % Edition.label(level))
	toast("已切换到%s模式" % Edition.label(level), "info")

func toggle_edition() -> void:
	set_edition(Edition.Level.PRO if edition == Edition.Level.BASIC else Edition.Level.BASIC)

func is_pro() -> bool:
	return edition == Edition.Level.PRO

## 让所有"仅专业模式可见"的节点跟着开/关。
## ★ 约定：专业专属的界面节点把自己 `add_to_group("pro_only")`，这里统一管，
##   面板不许各自判断 edition（否则漏一个就出现"基础模式下只露半个专业面板"）。
func apply_edition_visibility() -> void:
	if get_tree() == null:
		return
	var want := is_pro()
	for n in get_tree().get_nodes_in_group("pro_only"):
		if n is CanvasItem and is_instance_valid(n):
			(n as CanvasItem).visible = want

func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_CLOSE_REQUEST:
		if doc.dirty:
			_ask_save_on_quit()
		else:
			get_tree().quit()

## 有未保存改动时，关窗前问一句
func _ask_save_on_quit() -> void:
	var dlg := ConfirmationDialog.new()
	dlg.title = "退出前保存"
	prep_window(dlg)
	dlg.dialog_text = "有未保存的改动。要先保存吗？"
	dlg.ok_button_text = "保存并退出"
	dlg.add_button("不保存并退出", true, "discard")
	dlg.canceled.connect(func(): _close_dialog(dlg))
	dlg.confirmed.connect(func(): _close_dialog(dlg, _quit_after_save))
	dlg.custom_action.connect(func(action: String):
		if action == "discard":
			_close_dialog(dlg, func(): get_tree().quit()))
	get_tree().root.add_child(dlg)
	dlg.popup_centered()

func _quit_after_save() -> void:
	if current_path == "":
		_pending_proceed = func(): get_tree().quit()
		ask_save_as()                        # 还没存过 → 先另存为
		return
	if _save_to(current_path):
		get_tree().quit()
		return
	# ★ 保存失败就别静默留在原地：用户点了"保存并退出"却没反应，会以为卡死
	var n: int = int(validation_errors)
	var why: String = ("有 %d 条校验错误" % n) if n > 0 else "写盘失败或校验没能通过"
	_alert("保存失败，未退出", "%s，关卡文件没有更新，程序也留在原处。\n\n请看 Output 里的 [Error] 逐条修完再关窗。" % why)


func set_mode(k: int) -> void:
	mode = k
	# ★ 切模式清一次选中：避免上个模式选中的路/连接/路口残留，占着 Delete 与状态栏
	selected_road = -1
	selected_turn = -1
	selected_junction = -1
	pending_src_lane = -1
	selected_deco = -1
	traffic_lane = -1
	traffic_turn = -1
	selected_facility = ""
	junction_candidates = {}
	selection.clear()                 # ★ 多选层：切模式一并清掉
	sumo_highlight_turn = -1          # ★ 配时矩阵的行高亮也是"选中"，切模式必须一起清（2026-11-04）
	clear_sumo_tint()
	selection_changed.emit()
	mode_changed.emit(k)
	print("模式切换为：", k)

## ★ 兼容层（多选批）：单选入口同时把多选收敛成这一条（road_id < 0 = 清空多选）。
##   多选本身走 set_selection_roads()，不经过这里 —— 所以 selected_road 永远 = "最后选中项"。
func select_road(road_id: int) -> void:
	var was_single: bool = selection.size() == 1 and int(selection[0]) == road_id
	var same: bool = selected_road == road_id and (was_single or (road_id < 0 and selection.is_empty()))
	selected_road = road_id
	selection = [road_id] if road_id >= 0 else []
	if same:
		return
	selection_changed.emit()

func select_turn(turn_id: int) -> void:
	selection.clear()                 # ★ 选连接 = 离开"多选路"语境
	selected_island = -1              # ★ 连接与岛互斥（面板只看一个）
	if selected_turn == turn_id:
		return
	selected_turn = turn_id
	selection_changed.emit()

func select_junction(node_id: int) -> void:
	selection.clear()
	selected_island = -1
	if selected_junction == node_id:
		return
	selected_junction = node_id
	pending_src_lane = -1
	if doc.can_be_signalized(node_id):
		junction_candidates = fetch_candidates(node_id)
	else:
		junction_candidates = {}          # 自由端没有转向可选，别白写一次临时文件
	selection_changed.emit()

func select_deco(id: int) -> void:
	selection.clear()
	if selected_deco == id:
		return
	selected_deco = id
	selection_changed.emit()

## 选中安全岛（E92 G2 · N6b）
func select_island(id: int) -> void:
	selection.clear()
	selected_island = id
	selected_turn = -1
	selection_changed.emit()

# --- 撤销 / 重做（整份 Doc 快照栈）---------------------------------------

## 一次撤销 = 「关卡文档 + SUMO 专业档」的整体快照。
## ★ 为什么必须带上专业档：S1/S2 起专业档也是用户编辑的内容，
##   只回滚 Doc 会让"撤销三步"之后配时还停在三步前 —— 那是最难查的一类错位。
func _snapshot_all() -> Dictionary:
	return {"doc": doc.snapshot(), "profile": profile.clone()}


## 用整体快照覆盖当前状态（撤销 / 重做）
func _restore_all(s: Dictionary) -> void:
	var p = s.get("profile", null)
	if p is SumoProfile:
		profile = p
	else:
		profile = SumoProfile.empty()
	set_doc(Doc.from_snapshot(s.get("doc", {})))


## 整体快照比较（"这一步到底改没改"）
func _snapshot_equal(a: Dictionary, b: Dictionary) -> bool:
	if not Doc.snapshot_equal(a.get("doc", {}), b.get("doc", {})):
		return false
	var pa = a.get("profile", null)
	var pb = b.get("profile", null)
	if not (pa is SumoProfile) or not (pb is SumoProfile):
		return pa == pb
	return SumoProfile.equal(pa, pb)


## 手势开始：记下"改动之前"的状态（先不压栈；重复调用只认第一次）
## tag = "哪个控件在连续微调"：只有【同一个 tag】的连续操作才允许并成一步撤销。
##   ① 传了 tag 就用它（面板里的 SpinBox 传 "spin:<instance_id>" 粒度最准）；
##   ② 不传 → 自动兜底 = "结构签名 + 当前选中对象"（见 _auto_tag）。
func begin_edit(tag: String = "") -> void:
	if _pending.is_empty():
		_pending = _snapshot_all()
		_pending_tag = tag if tag != "" else _auto_tag()

## 自动 tag：结构签名（各类对象数量 + 四个自增计数器）+ 当前选中的那个对象。
## ★ 两次快速删除会改 lanes/roads/nodes/turns/decos 数量或 next_*_id → tag 不同 → 各占一步
##   （这就是要修的 bug；四种对象都算进来，删两条路、两个连接、两个装饰都不会被并掉）；
##   而按住 SpinBox 连点是"原地改值"：数量、计数器、选中都不变 → tag 相同 → 并成一步。
## ★ 故意不用"当前焦点控件"来判：焦点是隐式状态，画布操作时也可能挂着焦点，
##   很容易把两次删除误判成"同一控件"。
func _auto_tag() -> String:
	return "%d/%d/%d/%d/%d/%d|%d/%d/%d/%d/%d|%d,%d,%d,%d,%d" % [
		doc.lanes.size(), doc.roads.size(), doc.nodes.size(), doc.turns.size(), doc.decos.size(),
		doc.islands.size(),
		doc.next_lane_id, doc.next_road_id, doc.next_turn_id, doc.next_deco_id, doc.next_island_id,
		selected_road, selected_turn, selected_junction, selected_deco, selected_island]

## 手势结束：这一步"真改了东西"才压进撤销栈（没改就不占一步）
func end_edit() -> void:
	if _pending.is_empty():
		return
	_drain_signal_repair()
	var p := _pending
	var tag := _pending_tag
	_pending = {}
	_pending_tag = ""
	# ★ dirty 只跟着快照走（供 undo/redo 回放），【不】参与"这一步算不算改动"的判定：
	#   否则"点一下节点但没拖动"（_finish_node_drag 无条件置 dirty）也会白占一步撤销
	if _snapshot_equal(p, _snapshot_all()):
		return                                   # 状态没变（比如只是点了下选路）→ 不记
	var now := Time.get_ticks_msec()
	# ★ 合并条件多看一条"同一个 tag"：连续微调才并，两次快速删除（tag 不同）各占一步。
	# ★ 合并时【保留更早那份快照】（不覆盖上一步的 p）—— 否则一次 Ctrl+Z 只退回上一个 tick，
	#   等于把"连点十下"吞成"最后一下"，跟没合并一样糟。
	var can_merge := tag != "" and tag == _last_tag \
		and not _undo.is_empty() and now - _last_end_ms < COALESCE_MS
	if not can_merge:
		_undo.append(p)
		if _undo.size() > MAX_UNDO:
			_undo.pop_front()
	_last_tag = tag
	_last_end_ms = now
	_redo.clear()

## 把"因车道重建而丢了相位"的信号路口重新点亮（连接 + 相位都重推）
func _drain_signal_repair() -> void:
	if doc.signal_repair_queue.is_empty():
		return
	var ids: Array = doc.signal_repair_queue.duplicate()
	doc.signal_repair_queue.clear()
	for nid in ids:
		var n := doc.find_node(int(nid))
		if n.is_empty() or not doc.can_be_signalized(int(nid)):
			continue
		var before_turns := _turn_ids_now()      # ★ 失败要把"本次重推出来的 turn"撤掉（B6）
		var added := derive_turns_at(int(nid))
		if apply_phases_at(int(nid)):
			n["type"] = "Signalized"
			doc.dirty = true
			print("节点 %d：新增连接 %d 条 → 相位已补齐" % [int(nid), added])
		else:
			var rolled := _rollback_turns_added_since(before_turns)
			print("节点 %d：相位重推失败 → 本次新增的 %d 条连接已一并撤销（节点仍为普通路口）" % [
				int(nid), rolled])


## 撤销/重做**还能退几步**（只读，UI 用来置灰按钮；不改任何状态）
func undo_depth() -> int:
	return _undo.size()


func redo_depth() -> int:
	return _redo.size()


func undo() -> void:
	if _undo.is_empty():
		print("没有可撤销的操作")
		toast("没有可撤销的操作", "warn")
		return
	_redo.append(_snapshot_all())
	_restore_all(_undo.pop_back())
	print("撤销（可重做 %d 步）" % _redo.size())

func redo() -> void:
	if _redo.is_empty():
		print("没有可重做的操作")
		toast("没有可重做的操作", "warn")
		return
	_undo.append(_snapshot_all())
	_restore_all(_redo.pop_back())
	print("重做（可撤销 %d 步）" % _undo.size())

## 换一份文档（撤销 / 重做 / 导入都走这里）：清掉全部选中态并广播
func set_doc(d: Doc) -> void:
	doc = d
	selected_road = -1
	selected_turn = -1
	selected_junction = -1
	selected_deco = -1
	selected_island = -1              # ★ 同其它选中项（撤销/重做后岛 id 可能已不存在）
	traffic_lane = -1
	traffic_turn = -1
	pending_src_lane = -1
	junction_candidates = {}
	validation_errors = -1
	validation_error_items = []      # ★ 换文档：上一次的校验清单也一起作废
	selection.clear()                 # ★ 换文档（含撤销/重做）→ 多选作废
	_pending = {}
	_pending_tag = ""
	_last_tag = ""                    # ★ 换文档/撤销重做 → 合并链断掉，别把下一步并进上一步
	invalidate_temp_spmap()           # ★ 换文档 → 临时快照缓存作废（C2；id 反查表也要跟着重算）
	sumo_probe.invalidate()           # ★ 换文档 → 探针结果（真实 link 表）一并作废
	selection_changed.emit()
	doc_changed.emit()

func select_traffic_lane(lane_id: int) -> void:
	if traffic_lane == lane_id and traffic_turn < 0:
		# ★ 同一条车道的"空转"也要把设施选中清掉 —— 否则点了设施之后再点它所在的车道，
		#   设施仍保持选中（高亮不消失）。这和 S1b 记的"select_junction(同一 id) 空转不广播"是同一类坑。
		if selected_facility != "":
			selected_facility = ""
			selection_changed.emit()
		return
	selected_facility = ""            # ★ 选车道 = 取消设施选中（否则面板会以为还在编辑那个设施）
	traffic_lane = lane_id
	traffic_turn = -1
	selection.clear()                 # ★ 与 select_turn / select_junction / select_deco 同口径：
	                                  #   选中对象换了，多选路就作废 —— 否则批量面板会一直压住车道面板
	selection_changed.emit()

## 选中检测设施（S3）。同时把它的车道也选上 —— 面板是按车道展示的。
## ★ 与其它选中项一样：清掉多选路语境，否则批量面板会压住车道面板。
func select_facility(facility_id: String) -> void:
	if selected_facility == facility_id:
		return
	selected_facility = facility_id
	if facility_id != "":
		var row := SumoFacilitiesModel.find(profile, facility_id)
		var lane_id := int(row.get("lane", -1))
		if lane_id >= 0:
			traffic_lane = lane_id
			traffic_turn = -1
	selection.clear()
	selection_changed.emit()


func select_traffic_turn(turn_id: int) -> void:
	if traffic_turn == turn_id and traffic_lane < 0:
		return
	selected_facility = ""
	traffic_turn = turn_id
	traffic_lane = -1
	selection.clear()                 # ★ 同上
	selection_changed.emit()

## 场景里的 EditorBridge 节点（C#）。懒查找，避开 Autoload 早于主场景就绪的问题。
func get_bridge() -> Node:
	if _bridge == null or not is_instance_valid(_bridge):
		var sc := get_tree().current_scene
		if sc != null:
			_bridge = sc.get_node_or_null("EditorBridge")
	return _bridge

# --- 导出 / 导入 ---------------------------------------------------------

## 把一段 JSON 写到临时 .spmap，返回 OS 路径；失败返回 ""
func _write_temp(json: String) -> String:
	var f := FileAccess.open(TMP_RES, FileAccess.WRITE)
	if f == null:
		push_error("无法写临时文件：", TMP_RES)
		return ""
	f.store_string(json)
	f.close()
	return ProjectSettings.globalize_path(TMP_RES)

## 序列化当前 Doc 并落临时文件；顺带缓存 id 反查表（供回调值翻译用）
## ★ 一般不要直接调它 —— 走 `_cached_temp_spmap()`，否则一遍导出 + 写盘白做（C2）。
func write_temp_spmap() -> String:
	var ex := doc.to_export()
	_capture_id_maps(ex)
	return _write_temp(ex["json"])

## 把 to_export() 的双向 id 表收进 App（桥接回调用反查、专业档存盘用正查）
func _capture_id_maps(ex: Dictionary) -> void:
	_inv_node = ex["inv_node"]
	_inv_lane = ex["inv_lane"]
	_inv_turn = ex["inv_turn"]
	_fwd_node = ex["fwd_node"]
	_fwd_lane = ex.get("fwd_lane", {})
	_fwd_turn = ex.get("fwd_turn", {})

# --- 临时快照缓存（C2）---------------------------------------------------
## 桥接的三个查询（冲突 / 候选 / 自动相位）都要一份"当前文档的临时 .spmap"。
## 原来每次查询都 `to_export()`（深拷贝 + renumber + stringify）+ 写盘 ——
## 选一个信号路口 = 2 轮，属性面板每重建一次（勾一个相位勾选框）再来 1 轮。
## 现在按"内容指纹"缓存：内容没变就直接复用上次那份文件。
var _tmp_sig := 0                 ## 缓存对应的内容指纹；0 = 没有有效缓存
var _tmp_os := ""                 ## 上次写出的临时文件 OS 路径

## 所有"要一份当前文档临时文件"的地方都走这里。
## 命中条件：指纹相同 + 缓存路径还在 + 文件还在盘上。
func _cached_temp_spmap() -> String:
	var sig := _temp_export_sig()
	if _tmp_sig == sig and _tmp_os != "" and FileAccess.file_exists(_tmp_os):
		return _tmp_os
	var p := write_temp_spmap()
	if p == "":
		_tmp_sig = 0
		return ""
	_tmp_sig = sig
	_tmp_os = p
	return p

## 清掉缓存（导出/导入这类会改写同一个临时文件或换文档的路径必须调）
func invalidate_temp_spmap() -> void:
	_tmp_sig = 0
	_tmp_os = ""
	_conflict_key = ""
	_conflict_pairs = []

## 影响"导出内容里被桥接查询用到的那部分"的指纹：节点坐标/类型、车道、连接、信号的**结构**。
## ★ 故意不含 meta / demand / game_rules / decos：三个查询都不看它们（校验走 export_map 自己的临时文件）。
## ★ 也故意不含**相位的数值**（offset / 时长 / start_time / 放行集）：ConflictsAt / DeriveTurns /
##   SuggestPhases 三个查询都不读相位，只看几何与连接。这样"勾一个相位勾选框"不会白白重导一次
##   （E91 C2 的那个 960 ms 就是这么来的）。相位本身照样会被 export_map 完整写进正式文件。
## ★ 加了新字段、且桥接查询会读它 → 必须加进这里，否则会拿到过期快照（E91 C2）。
func _temp_export_sig() -> int:
	var h := 17
	for n in doc.nodes:
		h = (h * 131 + int(n.id) * 7 + _mm(float(n.x)) * 13 + _mm(float(n.y)) * 17
			+ _type_seed(n.get("type", ""))) & 0x7FFFFFFF
	for l in doc.lanes:
		h = (h * 131 + int(l.id) * 7 + int(l.get("road", -1)) * 11
			+ int(l.from_node) * 13 + int(l.to_node) * 17 + _type_seed(l.get("type", ""))
			+ _mm(float(l.get("length", 0.0))) + _mm(float(l.get("speed_limit", 0.0))) * 3) & 0x7FFFFFFF
	for t in doc.turns:
		h = (h * 131 + int(t.id) * 7 + int(t.node) * 11 + int(t.src_lane) * 13
			+ int(t.dst_lane) * 17 + _type_seed(t.get("type", ""))
			+ _mm(float(t.get("ratio", 1.0)))) & 0x7FFFFFFF
	for s in doc.signals:                     # 信号只取"结构"：哪个节点、几套方案、每套几段
		h = (h * 131 + int(s.get("node", -1)) * 7) & 0x7FFFFFFF
		var plans: Array = s.get("plans", [])
		h = (h * 131 + plans.size()) & 0x7FFFFFFF
		for pl in plans:
			h = (h * 131 + (pl.get("stages", []) as Array).size()) & 0x7FFFFFFF
	return h

## 米/秒/比例 → 毫米级整数（避免浮点直接参与哈希时被"看起来一样"的尾差骗到）
static func _mm(v: float) -> int:
	return int(round(v * 1000.0))

## 导出：序列化 → 写临时文件 → 交 SimCore 校验 → 无 Error 才落正式文件

## ★ 校验用的就是即将落盘的同一份字符串（口径零漂移）
## 把“写临时文件 + 校验”单独抽出来，供 .spmap 与多格式导出共用。
## ★ 返回值把“游戏原始格式专属”的校验项单独分组（external_ok），
##   外部格式不消费 meta / game_rules，不能因为它们被拦住。
func _validated_export_json(json: String) -> Dictionary:
	var tmp_os := _write_temp(json)
	# ★ 导出改写了同一个临时文件 → 快照缓存作废（否则"导出后又撤销回原状"会拿到导出那份内容）
	invalidate_temp_spmap()
	if tmp_os == "":
		return {
			"ok": false, "external_ok": false, "errors": [], "structural_errors": [],
			"game_only": [], "error_count": 0, "tmp_os": "",
		}

	var errs: Array = []
	var b := get_bridge()
	if b == null:
		# ★ 没有桥接 = 校验根本跑不了。此时【绝不落盘】：宁可保存失败，也不能写出没校验过的文件。
		validation_errors = -1
		validation_error_items = []
		push_error("找不到 EditorBridge 节点（Main 下应有一个名为 EditorBridge 的子节点）；已中止导出")
		return {
			"ok": false, "external_ok": false, "errors": [], "structural_errors": [],
			"game_only": [], "error_count": -1, "tmp_os": tmp_os,
		}
	var errs_json: String = b.ValidateFile(tmp_os)
	var parsed = JSON.parse_string(errs_json)
	if not (parsed is Array):
		# ★ 校验器返回了非数组（序列化/桥接异常）→ 同样按"没校验成"处理，不落盘
		validation_errors = -1
		validation_error_items = []
		push_error("校验结果不是数组，已中止导出：", errs_json)
		return {
			"ok": false, "external_ok": false, "errors": [], "structural_errors": [],
			"game_only": [], "error_count": -1, "tmp_os": tmp_os,
		}
	errs = parsed

	var err_count := 0
	var structural_err_count := 0
	var structural_errors: Array = []
	var game_only: Array = []
	for e in errs:
		if not (e is Dictionary):
			continue
		var is_error := String(e.get("Severity", "")) == "Error"
		var is_game_only := _is_game_only_validation(e)
		if is_error:
			err_count += 1
			if not is_game_only:
				structural_err_count += 1
				structural_errors.append(e)
		if is_game_only:
			game_only.append(e)
	validation_errors = err_count
	validation_error_items = errs.duplicate(true)     # ★ 校验清单的数据源（P4-1）

	return {
		"ok": err_count == 0,
		"external_ok": structural_err_count == 0,
		"errors": errs,
		"structural_errors": structural_errors,
		"game_only": game_only,
		"error_count": err_count,
		"tmp_os": tmp_os,
	}


## 这条校验是否只属于“游戏原始格式”（不含外部格式消费的路网/信号/需求）。
static func _is_game_only_validation(e: Variant) -> bool:
	if not (e is Dictionary):
		return false
	var code := String(e.get("Code", ""))
	if GAME_ONLY_VALIDATION_CODES.has(code):
		return true
	var path := String(e.get("Path", ""))
	for prefix in GAME_ONLY_VALIDATION_PATHS:
		var p := String(prefix)
		if path == p or path.begins_with(p + "."):
			return true
	return false


## 序列化 + 顺手刷新「文件 id → 实时 id」的反查表（校验清单点一条定位要用）。
## ★ 用 `to_export()` 而不是 `to_dict()`：两者的 json **逐字节相同**（同样 normalize → 深拷贝 → renumber → schema），
##   但 `to_export()` 带回 inv_* 三张表。不刷新的后果：清单里 `lanes[12]` 反查不到实时 id → 点了没反应。
func _export_json_with_inv() -> String:
	var ex := doc.to_export()
	_capture_id_maps(ex)
	return String(ex["json"])


func export_map(final_path: String) -> Dictionary:
	var json := _export_json_with_inv()
	var v := _validated_export_json(json)
	if not bool(v.ok):
		if int(v.get("error_count", 0)) > 0:
			print("导出中止：%d 条错误，未落盘" % int(v.error_count))
		return {"ok": false, "errors": v.errors, "path": ""}

	var errs: Array = v.errors
	var g := FileAccess.open(final_path, FileAccess.WRITE)
	if g == null:
		push_error("无法写目标文件：", final_path)
		return {"ok": false, "errors": errs, "path": ""}
	g.store_string(json)
	g.close()
	doc.dirty = false
	print("导出成功：%s（%d 条提示，0 条错误）" % [final_path, errs.size()])
	write_deco_file(final_path)
	write_sumo_profile_file(final_path)           # ★ 专业档成对落盘（空档会删旧文件）
	return {"ok": true, "errors": errs, "path": final_path}


## 把专业档体检条目并进「校验问题」清单（SimCore 的在前、专业档的在后）。
## ★ 不另造一套 UI：顶栏的"N 处错误"、清单浮窗、点一条定位全都复用它。
## ★ 分两次调（导出前 5 条 / 导出后 4 条），所以不能写成一个"整体替换"。
func _merge_sumo_findings(list: Array) -> void:
	if list.is_empty():
		return
	for f in list:
		if not (f is Dictionary):
			continue
		var fd: Dictionary = f
		validation_error_items.append(fd)
		if String(fd.get("Severity", "")) == "Error" and int(validation_errors) >= 0:
			validation_errors = int(validation_errors) + 1
		print("[体检] %s %s：%s" % [
			String(fd.get("Severity", "")), String(fd.get("Code", "")),
			String(fd.get("Message", ""))])


## 多格式导出：同一份已校验快照 → 可选 .spmap + shp/net.xml。
## ★ .spmap 仍要求“零 Error”；外部格式只要求“非游戏字段零 Error”。
## 返回 {
##   ok, results: [{format,ok,path,error,warnings}],
##   errors: 拦住外部格式的结构错误,
##   spmap_errors: 拦住 .spmap 的全部错误,
##   game_only: meta / game_rules 专属校验项（供 UI 说明已忽略）,
##   external_ok, spmap_ok, spmap_selected, external_selected
## }
func export_with_options(opts: Dictionary) -> Dictionary:
	var json := _export_json_with_inv()
	var v := _validated_export_json(json)

	# ★ 专业档随导出传给 C#（按**文件 id** 重映射，与 tmp .spmap 同口径）。
	#   空专业档 → 传空串 → 不生成 .tll.xml，导出行为与基础模式完全一致。
	if profile.has_content():
		var as_file := profile.remapped(_fwd_node, _fwd_lane, _fwd_turn)
		opts["sumoProfile"] = JSON.stringify(as_file.to_dict())
	else:
		opts["sumoProfile"] = ""

	# ★ S6 导出前体检：只看 `.spmap` + `.sumo.json` 两份数据的 5 条规则
	#   （要真实 link 表 / 车道长度的 4 条在 C# 侧 `SumoCheck`，导出后才跑）。
	# ★ Path 用文件 id（`turns[12].sumo`），所以必须在这里调（`_fwd_*` 刚建好）。
	var sumo_pre: Array = SumoCheckModel.run(doc, profile, _fwd_node, _fwd_lane, _fwd_turn)
	var sumo_all: Array = sumo_pre.duplicate()      # 导出前 5 条 + C# 侧 4 条，一起给界面
	var sumo_pre_errors := 0
	for f in sumo_pre:
		if String((f as Dictionary).get("Severity", "")) == "Error":
			sumo_pre_errors += 1
	_merge_sumo_findings(sumo_pre)                  # 立刻进「校验问题」清单（早退分支也要有）

	# ★ 先按**用户原始选择**算这两个标志，再让体检去改 opts ——
	#   否则"体检把 net 关掉"会让 external_selected 变成 false，走到"至少选一种格式"的早退分支。
	var base := String(opts.get("outputBase", "")).strip_edges()
	if base.to_lower().ends_with(".spmap"):
		base = base.substr(0, base.length() - 6)

	var spmap_selected := bool(opts.get("spmap", false))
	var external_selected := (
		bool(opts.get("shp", false)) or bool(opts.get("net", false))
	)

	# 有 Error 就不产出 SUMO 产物（.spmap 与 .shp 照常 —— 专业档坏了不该挡住存档）
	var sumo_blocked := sumo_pre_errors > 0
	if sumo_blocked:
		var skipped: Array[String] = []
		for key in ["net", "rou", "add", "sumocfg"]:
			if bool(opts.get(key, false)):
				opts[key] = false
				skipped.append(String(key))
		if not skipped.is_empty():
			print("专业档体检：%d 条错误，已跳过 SUMO 产物（%s）" % [
				sumo_pre_errors, ",".join(PackedStringArray(skipped))])

	var result_base := {
		"results": [],
		"sumo_findings": sumo_pre,
		"errors": v.structural_errors,
		"spmap_errors": v.errors,
		"game_only": v.game_only,
		"external_ok": v.external_ok,
		"spmap_ok": true,
		"spmap_selected": spmap_selected,
		"external_selected": external_selected,
	}

	if base == "":
		var bad_path := result_base.duplicate(true)
		bad_path["ok"] = false
		bad_path["error"] = "输出路径为空。"
		return bad_path
	if not spmap_selected and not external_selected:
		var no_format := result_base.duplicate(true)
		no_format["ok"] = false
		no_format["error"] = "至少选一种导出格式。"
		return no_format

	var out_dir := base.get_base_dir()
	if out_dir != "" and not DirAccess.dir_exists_absolute(out_dir):
		DirAccess.make_dir_recursive_absolute(out_dir)

	var results: Array = []
	var all_ok := true
	var spmap_ok := true

	# ★ 体检拦下了 SUMO 产物 → 明确告诉用户"为什么没生成"，别让它静默消失
	if sumo_blocked:
		all_ok = false
		results.append({
			"format": "sumo", "ok": false, "path": "", "warnings": [],
			"error": "%d 条专业档错误，已跳过全部 SUMO 产物（见「校验问题」清单）" % sumo_pre_errors,
		})

	if spmap_selected:
		if not bool(v.ok):
			spmap_ok = false
			all_ok = false
			var n := int(v.get("error_count", 0))
			var why: String
			if n >= 0:
				why = "原生 .spmap 校验未通过：%d 条错误（未写出）。" % n
			else:
				why = "原生 .spmap 未写出：校验未执行（EditorBridge 不可用）。"
			results.append({
				"format": "spmap",
				"ok": false,
				"path": "",
				"warnings": [],
				"error": why,
			})
		else:
			var spmap_path := base + ".spmap"
			var g := FileAccess.open(spmap_path, FileAccess.WRITE)
			if g == null:
				push_error("无法写目标文件：", spmap_path)
				spmap_ok = false
				all_ok = false
				results.append({
					"format": "spmap", "ok": false, "path": "", "warnings": [],
					"error": "无法写目标文件：" + spmap_path,
				})
			else:
				g.store_string(json)
				g.close()
				doc.dirty = false
				print("导出成功：%s（%d 条提示，0 条错误）" % [spmap_path, v.errors.size()])
				write_deco_file(spmap_path)
				write_sumo_profile_file(spmap_path)   # ★ 专业档成对落盘
				current_path = spmap_path
				_record_recent(current_path)
				results.append({
					"format": "spmap", "ok": true, "path": spmap_path, "error": "", "warnings": [],
				})

	if external_selected:
		if not bool(v.external_ok):
			all_ok = false
			var structural: Array = v.structural_errors
			var why: String
			if not structural.is_empty():
				why = "外部格式未写出：%d 条结构校验错误。" % structural.size()
			else:
				why = "外部格式未写出：校验未执行（缺少临时文件或 EditorBridge 不可用）。"
			results.append({
				"format": "external",
				"ok": false,
				"path": "",
				"warnings": [],
				"error": why,
			})
		else:
			var bridge := get_export_bridge()
			if bridge == null:
				results.append({
					"format": "external",
					"ok": false,
					"path": "",
					"error": "找不到 MapExportBridge 节点（Main 下应有一个名为 MapExportBridge 的子节点）。",
					"warnings": [],
				})
				all_ok = false
			else:
				var res_json: String = bridge.ExportFormats(v.tmp_os, base, JSON.stringify(opts))
				var parsed = JSON.parse_string(res_json)
				if parsed is Dictionary and parsed.get("results") is Array:
					var derived: Array = parsed.results
					results.append_array(derived)
					# ★ S6：C# 侧体检（读产物的 4 条）。Path 用的是文件 id，能直接点定位。
					var post = parsed.get("sumoFindings", [])
					if post is Array:
						sumo_all.append_array(post)
						_merge_sumo_findings(post)
					if not bool(parsed.get("ok", false)):
						all_ok = false
					if String(parsed.get("error", "")) != "":
						all_ok = false
						results.append({
							"format": "external",
							"ok": false,
							"path": "",
							"error": String(parsed.error),
							"warnings": [],
						})
				else:
					all_ok = false
					results.append({
						"format": "external",
						"ok": false,
						"path": "",
						"error": "导出桥返回的不是 JSON 对象：" + res_json,
						"warnings": [],
					})

	for r in results:
		var ok := bool(r.get("ok", false))
		if ok:
			print("[导出] %s → %s" % [String(r.get("format", "?")), String(r.get("path", ""))])
		else:
			print("[导出] %s 失败：%s" % [String(r.get("format", "?")), String(r.get("error", ""))])
		for w in r.get("warnings", []):
			print("[导出] %s 警告：%s" % [String(r.get("format", "?")), String(w)])

	return {
		"ok": all_ok,
		"results": results,
		"sumo_findings": sumo_all,
		"errors": v.structural_errors,
		"spmap_errors": v.errors,
		"game_only": v.game_only,
		"external_ok": v.external_ok,
		"spmap_ok": spmap_ok,
		"spmap_selected": spmap_selected,
		"external_selected": external_selected,
	}


## 打开“导出为其他格式”面板
## 车型库变了就同步到全局库（跨关卡、跨会话保留）。
## ★ 用签名比较，不是每帧都写；载入关卡后 `_reset_types_library_sig()` 会把签名对齐，
##   这样"只是打开了一个关卡"不会触发写盘。
func _sync_types_library() -> void:
	if profile == null:
		return
	var sig := JSON.stringify(profile.vehicle_types)
	if sig == _types_lib_sig:
		return
	_types_lib_sig = sig
	SumoTypesLibrary.save_types(profile.vehicle_types.duplicate(true))
	print("全局车型库已更新：%d 种车型 → %s" % [profile.vehicle_types.size(), SumoTypesLibrary.PATH])


## 把同步签名对齐到当前状态（载入关卡 / 新建之后调），避免误写回全局库
func _reset_types_library_sig() -> void:
	_types_lib_sig = JSON.stringify(profile.vehicle_types) if profile != null else ""


## 车型库弹窗：新建车型 / 改车长、限速、加减速、风格（全局，所有进口共享）
## ★ 弹窗只改 `profile.vehicle_types`；"哪个进口用多少"在车道面板里配。
func open_type_dialog() -> void:
	if _type_dialog != null and is_instance_valid(_type_dialog):
		_type_dialog.show()
		_type_dialog.move_to_foreground()
		_type_dialog.call("rebuild")
		return
	var script := load("res://scripts/ui/sumo_type_dialog.gd")
	if script == null:
		push_error("找不到车型库弹窗 res://scripts/ui/sumo_type_dialog.gd")
		return
	_type_dialog = script.new()
	get_tree().root.add_child(_type_dialog)
	_type_dialog.popup_centered(Vector2i(780, 680))


func open_export_dialog() -> void:
	if _export_dialog != null and is_instance_valid(_export_dialog):
		_export_dialog.show()
		_export_dialog.move_to_foreground()
		return
	var script := load("res://scripts/export/export_dialog.gd")
	if script == null:
		push_error("找不到导出面板脚本 res://scripts/export/export_dialog.gd")
		return
	_export_dialog = script.new()
	get_tree().root.add_child(_export_dialog)
	_export_dialog.popup_centered(Vector2i(780, 780))


## 导出桥（C#）：MapExportBridge
func get_export_bridge() -> Node:
	var sc := get_tree().current_scene
	if sc == null:
		return null
	return sc.get_node_or_null("MapExportBridge")


func detect_netconvert(hint_path: String) -> Dictionary:
	var b := get_export_bridge()
	if b == null:
		return {"ok": false, "error": "找不到 MapExportBridge 节点。", "path": "", "version": ""}
	var res_json: String = b.DetectNetconvert(
		hint_path,
		ProjectSettings.globalize_path("res://../Tools/sumo/bin"))
	var parsed = JSON.parse_string(res_json)
	if parsed is Dictionary:
		return parsed
	return {"ok": false, "error": "检测结果不是 JSON 对象：" + res_json, "path": "", "version": ""}

# --- SUMO 探针（专业模式的地基；技术方案06 §3 D3）-------------------------

## 探针用的内容指纹。★ 与 `_temp_export_sig()` 同源：探针看到的路网，
## 必须和桥接查询（冲突/候选/相位）看到的是同一份，否则灯序会对不上。
func sumo_probe_signature() -> int:
	# ★ S4：netconvert 开关会改变 link 表（尤其「左侧通行」「合并路口」），
	#   必须进签名，否则改完开关拿到的还是旧探针结果（E91 C2 的同一类坑）。
	var h := _temp_export_sig()
	for key in SumoNetModel.NETCONVERT_KEYS:
		h = (h * 131 + (1 if SumoNetModel.netconvert_option(profile, key) else 2)) & 0x7FFFFFFF
	return h

## 探针选项：netconvert 路径来自导出设置，车道宽同源，搜索目录同导出面板
## ★ 只带 `netconvert` 段：探针只跑 netconvert 生成网络，不读配时/车型/设施，
##   也就**不需要** id 翻译（省掉一次 remapped 与它的 orphan 副作用）。
func sumo_probe_options() -> Dictionary:
	var s := ExportSettings.load_settings()
	var nc := {}
	for key in SumoNetModel.NETCONVERT_KEYS:
		nc[key] = SumoNetModel.netconvert_option(profile, key)
	return {
		"net": true,
		"outputBase": "",
		"laneWidthM": float(s.get("lane_width_m", 3.5)),
		"netconvertPath": String(s.get("netconvert_path", "")),
		"netconvertSearchDir": ProjectSettings.globalize_path("res://../Tools/sumo/bin"),
		"yellowDuration": 3.0,
		"allRedDuration": 2.0,
		"sumoProfile": JSON.stringify({"format_version": SumoProfile.FORMAT_VERSION, "netconvert": nc}),
	}

## 发起探针（已有新鲜结果就直接复用）。返回 true = 已发起或可直接复用。
## ★ 慢查询：调用方**不要**紧接着读结果，应先摆占位文本，等 `sumo_probe_changed`。
func request_sumo_probe(force: bool = false) -> bool:
	if not force and sumo_probe.has_result_for(sumo_probe_signature()):
		return true
	var bridge := get_export_bridge()
	if bridge == null:
		push_warning("SUMO 探针：找不到 MapExportBridge 节点")
		return false
	var tmp := _cached_temp_spmap()
	if tmp == "":
		push_warning("SUMO 探针：拿不到当前关卡的临时快照")
		return false
	return sumo_probe.request(bridge, tmp, sumo_probe_options(), sumo_probe_signature())

## 探针结果（未算出 / 失败时是空字典或 {ok:false}）。失败原因看 `sumo_probe.error`。
func sumo_probe_result() -> Dictionary:
	return sumo_probe.result

# --- 一键试跑（S5）-------------------------------------------------------
# 参数全部来自导出设置：netconvert 与 sumo 在同一个 bin 目录，直接拿它的目录当搜索路径。
# 用户没指定 netconvert 路径时留空，C# 侧会自己按 SUMO_HOME / PATH / 常见安装位置找。

## sumo 可执行文件所在的 bin 目录（由 netconvert 路径推导；空 = 让 C# 自己找）
func _sumo_bin_dir() -> String:
	var nc := String(ExportSettings.load_settings().get("netconvert_path", "")).strip_edges()
	return nc.get_base_dir() if nc != "" else ProjectSettings.globalize_path("res://../Tools/sumo/bin")


## 发起一次试跑（立即返回）。成功发起返回 true；失败原因看 `sumo_run.error`。
## ★ 慢查询：调用方**不要**紧接着读结果，等 `sumo_run_changed`（或 `await run_sumo_and_wait`）。
func run_sumo(cfg_path: String) -> bool:
	var bridge := get_export_bridge()
	if bridge == null:
		sumo_run.error = "找不到 MapExportBridge 节点"
		return false
	var bin := _sumo_bin_dir()
	return sumo_run.start(bridge, cfg_path, "", "", bin)


## 发起并等结果（异步）。返回 {ok, error, stats, tail, ...}。
## 面板直接 `await` 它，不用自己管信号与忙标志。
func run_sumo_and_wait(cfg_path: String) -> Dictionary:
	if not run_sumo(cfg_path):
		return {"ok": false, "error": sumo_run.error, "stats": {}}
	# ★ 先判一次 busy：任务可能在 _process 之前就结束了（缓存/立即失败）
	while sumo_run.busy:
		await sumo_run_changed
	if not sumo_run.result.is_empty():
		return sumo_run.result
	return {"ok": false, "error": sumo_run.error if sumo_run.error != "" else "试跑没有返回结果", "stats": {}}


## 用 sumo-gui 打开（立即返回，不等窗口关闭）。返回 {ok, error, exe}。
func open_sumo_gui(cfg_path: String) -> Dictionary:
	var bridge := get_export_bridge()
	if bridge == null:
		return {"ok": false, "error": "找不到 MapExportBridge 节点"}
	var raw = bridge.OpenSumoGui(cfg_path, "", "", _sumo_bin_dir())
	var parsed = JSON.parse_string(String(raw))
	return parsed if parsed is Dictionary else {"ok": false, "error": String(raw)}


## 一行统计摘要（print 与面板共用这一份文案，别两处各写一遍）
func _sumo_stats_line(s: Dictionary) -> String:
	if s.is_empty():
		return "（没有统计）"
	return "进车 %d 辆，剩 %d 辆在路｜均速 %.2f m/s｜行程 %.2f s｜等灯 %.2f s｜时间损失 %.2f s｜传送 %d" % [
		int(s.get("inserted", 0)), int(s.get("running", 0)), float(s.get("speed", 0.0)),
		float(s.get("duration", 0.0)), float(s.get("waitingtime", 0.0)),
		float(s.get("timeloss", 0.0)), int(s.get("teleports", 0))]


## 给界面用的多行统计（试跑结果面板）
func sumo_stats_text(r: Dictionary) -> String:
	var s = r.get("stats", {})
	if not (s is Dictionary) or (s as Dictionary).is_empty():
		return "（没有统计）"
	var d: Dictionary = s
	var lines: Array[String] = []
	lines.append("跑到 %.0f s：进车 %d 辆，剩 %d 辆在路，传送 %d 次" % [
		float(d.get("ended_at", 0.0)), int(d.get("inserted", 0)),
		int(d.get("running", 0)), int(d.get("teleports", 0))])
	var sample := int(d.get("statistics_sample", 0))
	if sample <= 0 or int(d.get("inserted", 0)) <= 0:
		lines.append("⚠ 一辆车都没跑起来 —— 检查需求曲线（车流来自需求，不是车型）")
		return "\n".join(PackedStringArray(lines))
	lines.append("平均速度 %.2f m/s｜行程 %.2f s｜等灯 %.2f s｜时间损失 %.2f s" % [
		float(d.get("speed", 0.0)), float(d.get("duration", 0.0)),
		float(d.get("waitingtime", 0.0)), float(d.get("timeloss", 0.0))])
	if int(d.get("teleports", 0)) > 0:
		lines.append("⚠ 有 %d 辆车被传送 —— 多半是路口死锁或路线走不通" % int(d.get("teleports", 0)))
	return "\n".join(PackedStringArray(lines))

# --- 配时矩阵 → 画布的联动（2026-11-04）----------------------------------

## 矩阵里点某一行时高亮的转向（蓝线）。-1 = 不高亮。
## ★ 独立于 `selected_turn`：那会让属性面板切走，配时矩阵就没了。
func clear_sumo_highlight() -> void:
	if sumo_highlight_turn == -1:
		return
	sumo_highlight_turn = -1

## 当前阶段的灯态：turn_id → "G"/"g"/"y"/"r"/"o"。
## ★ 由配时矩阵在每次重建后推过来，画布据此给连接线染色。空 = 不染色（用转向色）。
var sumo_tint: Dictionary = {}
var sumo_tint_rev: int = 0        ## 染色变化计数（进 RoadLayer 的视图签名）

func set_sumo_tint(states: Dictionary) -> void:
	sumo_tint = states.duplicate()
	sumo_tint_rev += 1

func clear_sumo_tint() -> void:
	if sumo_tint.is_empty():
		return
	sumo_tint = {}
	sumo_tint_rev += 1

## 某条转向当前阶段的灯态；"" = 不染色
func sumo_turn_state(turn_id: int) -> String:
	return String(sumo_tint.get(turn_id, ""))

## 由 .spmap 路径推出"同目录同名"的 .spdeco.json 路径
static func deco_path_for(spmap_path: String) -> String:
	return spmap_path.get_base_dir().path_join(
		spmap_path.get_file().get_basename() + ".spdeco.json")

## 写装饰文件（同目录同名）；失败只告警，不阻断 .spmap 导出
func write_deco_file(spmap_path: String) -> bool:
	var dp := deco_path_for(spmap_path)
	var f := FileAccess.open(dp, FileAccess.WRITE)
	if f == null:
		push_warning("装饰文件写入失败：%s（.spmap 已正常导出）" % dp)
		return false
	f.store_string(JSON.stringify(doc.to_deco_dict(), "  ", false))
	f.close()
	print("装饰已写入：%s（%d 个）" % [dp, doc.decos.size()])
	return true

## 由 .spmap 路径推出"同目录同名"的 `.sumo.json` 路径（SUMO 专业档）
static func sumo_profile_path_for(spmap_path: String) -> String:
	return spmap_path.get_base_dir().path_join(
		spmap_path.get_file().get_basename() + ".sumo.json")

## 写专业档（同目录同名）。
## ★ 有内容才写；**没内容时把旧文件删掉** —— 否则用户清空专业内容后，
##   下一次导出还会读到旧 `.sumo.json`，表现为"删不掉的配时"。
## ★ 写之前按 `fwd_*` 把实时 id 翻成文件 id（专业档存的是文件 id）。
func write_sumo_profile_file(spmap_path: String) -> bool:
	var pp := sumo_profile_path_for(spmap_path)
	if not profile.has_content():
		if FileAccess.file_exists(pp):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(pp))
			print("专业档为空，已删除旧文件：%s" % pp)
		return true
	var as_file := profile.remapped(_fwd_node, _fwd_lane, _fwd_turn)
	if not as_file.orphans.is_empty():
		print("专业档：%d 条引用在当前关卡里找不到目标，已跳过（校验会提示）" % as_file.orphans.size())
	as_file.orphans = []                    # ★ orphans 是运行时诊断，不进文件
	var f := FileAccess.open(pp, FileAccess.WRITE)
	if f == null:
		push_warning("专业档写入失败：%s（.spmap 已正常导出）" % pp)
		return false
	f.store_string(JSON.stringify(as_file.to_dict(), "  ", false))
	f.close()
	print("专业档已写入：%s" % pp)
	return true

## 载入专业档（同目录同名）；没有文件 = 空档。
## ★ 必须在 `_cached_temp_spmap()` **之后**调：反查表 `_inv_*` 在那里才建好。
func load_sumo_profile_for(spmap_path: String) -> void:
	profile = SumoProfile.empty()
	var pp := sumo_profile_path_for(spmap_path)
	if not FileAccess.file_exists(pp):
		# ★ 没有关卡自己的专业档 → 仍把**全局车型库**带上（车型定义跨关卡复用）
		profile.vehicle_types = SumoTypesLibrary.load_types()
		print("无同名 .sumo.json，专业档为空（车型库从全局库带入 %d 种）" % profile.vehicle_types.size())
		_reset_types_library_sig()
		return
	var loaded := SumoProfile.from_json_text(FileAccess.get_file_as_string(pp))
	if loaded == null:
		push_warning("专业档读不进来（形状不对）：%s" % pp)
		return
	profile = loaded.remapped(_inv_node, _inv_lane, _inv_turn)
	if not profile.orphans.is_empty():
		print("专业档：%d 条引用在当前关卡里找不到目标（已记入孤儿表，未丢数据）"
			% profile.orphans.size())
	print("专业档已载入：%s" % pp)
	_reset_types_library_sig()      # ★ 只是"打开"，不算用户改过 → 不写回全局库

## 导入：读文件 → 新 Doc（含反展开）→ 替换当前文档
func import_map(path: String) -> bool:
	if not FileAccess.file_exists(path):
		push_error("导入失败：文件不存在 ", path)
		return false
	var txt := FileAccess.get_file_as_string(path)
	var d := Doc.from_json_text(txt)
	if d == null:
		push_error("导入失败：JSON 里不是对象 ", path)
		return false
	set_doc(d)                                   # ★ 统一清选中态 / pending 并广播
	_undo.clear()
	_redo.clear()                                # ★ 换了文件 → 历史作废，Ctrl+Z 不该退回上一个文件
	# 同目录同名 .spdeco.json：有就一并载入，没有就空列表
	var dp := deco_path_for(path)
	if FileAccess.file_exists(dp):
		var list := Doc.decos_from_json_text(FileAccess.get_file_as_string(dp))
		doc.set_decos(list)
		print("装饰已载入：%s（%d 个）" % [dp, list.size()])
	else:
		print("无同名 .spdeco.json，装饰列表为空")
	# ★ 导入后立刻走一遍"导出快照"（只读，不落盘）：
	#   ① 文件里有坏引用时，renumber 的兜底 warning 当场出现在 Output，而不是等用户按导出才暴露；
	#   ② 顺带把 id 反查表（_inv_* / _fwd_node）建好 —— 选路口、生成连接都要用。
	_cached_temp_spmap()
	# ★ 旧数据迁移：`turns[].ratio` 是可选字段，S2 前的文件没有它 →
	#   按转向类型补默认权重（有值的绝不覆盖，见 Doc.fill_default_ratios）
	doc.fill_default_ratios()
	# ★ 专业档必须在这之后载入：它要用 `_inv_*`（文件 id → 实时 id）做重映射
	load_sumo_profile_for(path)
	print("导入成功：节点 %d 条 车道 %d 条 转向 %d 条 路 %d 条" % [
		doc.nodes.size(), doc.lanes.size(), doc.turns.size(), doc.roads.size()])
	return true

# --- 自动推导：连接 + 相位 -------------------------------------------------

## 自动推导【该路口】的连接（调 TurnGenerator），并入 doc.turns；返回新增条数
func derive_turns_at(node_id: int) -> int:
	var b := get_bridge()
	if b == null:
		push_error("找不到 EditorBridge 节点")
		return 0
	var tmp := _cached_temp_spmap()
	if tmp == "":
		return 0
	var res = JSON.parse_string(b.DeriveTurns(tmp))
	if not (res is Dictionary) or not res.get("ok", false):
		push_error("自动生成连接失败：", res if res is Dictionary else "返回不是对象")
		return 0

	var existing := {}
	for t in doc.turns:
		existing["%d-%d" % [int(t.src_lane), int(t.dst_lane)]] = true

	# 1) 先把这个路口的候选全收下来（id 翻回实时 id）
	var cand: Array = []
	for t in res.get("turns", []):
		var node := int(_inv_node.get(int(t.node), -1))
		if node != node_id:
			continue
		var src := int(_inv_lane.get(int(t.src_lane), -1))
		var dst := int(_inv_lane.get(int(t.dst_lane), -1))
		if src < 0 or dst < 0:
			continue
		cand.append({"src": src, "dst": dst, "type": String(t.type)})

	# 2) 编辑期策略过滤（掉头/过街，由关卡设置面板的两个勾控制）；★ 过滤后一条不剩 → 就不过滤
	#    （否则 V 形锐角路口会被清成 0 条，连信号都标不上）
	#    include_uturn 默认 false（不含掉头）；include_crosswalk 默认 true（含过街）
	var kept: Array = []
	for c in cand:
		var ty := String(c["type"])
		if not include_uturn and ty == "UTurn":
			continue
		if not include_crosswalk and ty == "Crosswalk":
			continue
		kept.append(c)
	if kept.is_empty():
		kept = cand

	# 3) 同类才连：src/dst 的 lane.type 必须相同（A1）
	#    ★ 这一层放在上面"一条不剩就不过滤"的兜底【之后】：
	#      兜底只负责"别把路口清空"，不该把跨车道类型的连接放回来。
	#    人行道↔人行道的过街仍是 Crosswalk 桶，靠 turns[].type 判定，不新增类型。
	var same_type: Array = []
	for c in kept:
		var sl := doc.find_lane(int(c["src"]))
		var dl := doc.find_lane(int(c["dst"]))
		if sl.is_empty() or dl.is_empty():
			continue                    # 车道已不存在 → 不连
		var st := String(sl.get("type", ""))
		var dt := String(dl.get("type", ""))
		if st != dt and not _lt_motor_pair(st, dt):
			continue                    # 跨车道类型（如 Driving ↔ Sidewalk）→ 不连
		same_type.append(c)

	# 4) 过街只留"正对面"（A1-α）：行人是从一侧人行道走到【对侧】，
	#    两条人行道挂在路口的"臂"应当分处路口两侧（臂方向夹角 > 120°）；
	#    同侧拐弯（夹角≈90°）不是过街，丢掉。★ 只作用于 Crosswalk，其余转向原样放过。
	var opposite: Array = []
	var best_by_src := {}                 # 进口 lane → 该进口最正对的过街（保底用）
	for c in same_type:
		if String(c["type"]) != "Crosswalk":
			opposite.append(c)            # 非过街：原样放过
			continue
		var l_in := doc.find_lane(int(c["src"]))
		var l_out := doc.find_lane(int(c["dst"]))
		if l_in.is_empty() or l_out.is_empty():
			continue
		var arm_in: Vector2 = doc.node_pos(int(l_in["from_node"])) - doc.node_pos(int(l_in["to_node"]))
		var arm_out: Vector2 = doc.node_pos(int(l_out["to_node"])) - doc.node_pos(int(l_out["from_node"]))
		var facing: float = arm_in.normalized().dot(arm_out.normalized())
		if facing < -0.5:                 # 两臂夹角 > 120° → 正对面
			opposite.append(c)
			continue
		var skey := int(c["src"])
		if not best_by_src.has(skey) or facing > float(best_by_src[skey]["dot"]):
			best_by_src[skey] = {"c": c, "dot": facing}
	# ★ 保底：某进口的过街被阈值全砍掉时，补回它最正对的那一条（锐角路口别把过街清空）
	var has_cw := {}
	for c in opposite:
		if String(c["type"]) == "Crosswalk":
			has_cw[int(c["src"])] = true
	for skey in best_by_src:
		if not has_cw.has(skey):
			opposite.append(best_by_src[skey]["c"])

	# 5) 左转专用道只走左转/掉头（E92 G1）：直行/右转不该从它出去，否则和 Driving 没区别。
	#    ★ 只作用于"这次自动生成"的候选，**不动既有 turn**（手建的 / 导入的保持原样）。
	#    ★ 兜底同上：某个路口如果只有左转道，裁完会 0 条 → 那就整批别裁。
	var left_only: Array = []
	var dropped := 0
	for c in opposite:
		var sl := doc.find_lane(int(c["src"]))
		if not sl.is_empty() and String(sl.get("type", "")) == "SharedLeftTurn":
			var ty := String(c["type"])
			if ty != "Left" and ty != "UTurn":
				dropped += 1
				continue
		left_only.append(c)
	if left_only.is_empty() and dropped > 0:
		left_only = opposite
		dropped = 0
	if dropped > 0:
		print("节点 %d：左转专用道不生成 %d 条直行/右转连接（要让它直行就改成普通 Driving 车道）" % [
			node_id, dropped])
	opposite = left_only

	# 6) 并入（去重）
	var added := 0
	for c in opposite:
		var key := "%d-%d" % [int(c["src"]), int(c["dst"])]
		if existing.has(key):
			continue
		doc.turns.append({
			"id": doc.next_turn_id, "src_lane": int(c["src"]), "dst_lane": int(c["dst"]),
			"node": node_id, "type": String(c["type"]), "ratio": 1.0,
		})
		doc.next_turn_id += 1
		existing[key] = true
		added += 1
	if added > 0:
		doc.dirty = true
	return added

## 进口道拓宽（E92 G4 / N5）：给"进 at_node 的那一向"多一条车道。
## 做法 = 在 at_node 前 dist_m 处插一个节点，**只把靠路口那段**加宽，远段保持原样
## （渐变段/taper 的视觉留给表现层；数据上就是"两段不同横断面"）。
## ★ 只加"进这个节点"的方向：现实里出口道不拓宽（与左转专用道的口径一致）。
func widen_approach(road_id: int, at_node: int, dist_m: float) -> bool:
	var road := doc.find_road(road_id)
	if road.is_empty():
		print("拓宽失败：道路 #%d 不存在" % road_id)
		return false
	var tm: Dictionary = (road.template as Dictionary).duplicate(true)
	var into_b: bool = int(road.b_node) == at_node        # 正向 a→b = 进 b
	if bool(tm.get("oneway", false)) and not into_b:
		print("拓宽失败：这条路是单向的（只生成 a→b），进不了 a 端 → 这一端没有进口可拓宽")
		return false
	if into_b:
		tm["lanes_fwd"] = int(tm.get("lanes_fwd", 1)) + 1
	else:
		tm["lanes_rev"] = int(tm.get("lanes_rev", 1)) + 1
	begin_edit("widen:%d:%d" % [road_id, at_node])
	var res := doc.split_road_near_node(road_id, at_node, dist_m, tm)
	if not bool(res.get("ok", false)):
		end_edit()                                        # 没改 → 不占撤销步
		print("拓宽失败：", String(res.get("msg", "")))
		return false
	# 选中"靠路口的那一段"，方便紧接着勾左转道 / 看横断面
	selection.clear()
	selected_road = int(res.get("near_road", -1))
	selected_turn = -1
	selected_junction = -1
	selected_deco = -1
	end_edit()                                            # 内部会 _drain_signal_repair（重推相位）
	selection_changed.emit()
	print("已拓宽：靠节点 #%d 的 %.0f m 路段多一条进口车道（远段不变；左转专用道请在这段上单独勾）" % [
		at_node, dist_m])
	return true

## 左转专用道（E92 G1）的唯一例外：它左转之后接的是对面路的**普通出口车道**，
## 所以允许 SharedLeftTurn ↔ Driving 互通；否则必须两边都拓宽才生得出左转连接。
## ★ 只开这一对 —— Driving ↔ Bus 等既有口径一个字不动（改了会动到现有关卡的生成结果）。
static func _lt_motor_pair(a: String, b: String) -> bool:
	return (a == "SharedLeftTurn" and b == "Driving") or (a == "Driving" and b == "SharedLeftTurn")

## 连接模式：给【当前选中的路口】自动生成连接（E04 条目 9）
## 若该路口是信号路口，顺带重算相位，保证相位覆盖新连接
func generate_connections() -> int:
	if selected_junction < 0:
		print("请先在连接模式点选一个路口，再生成连接")
		return 0
	if not doc.can_be_signalized(selected_junction):
		print("节点 %d 不是路口（只连了 %d 条路）" % [
			selected_junction, doc.road_count_at(selected_junction)])
		return 0
	var added := derive_turns_at(selected_junction)
	if String(doc.find_node(selected_junction).get("type", "")) == "Signalized":
		apply_phases_at(selected_junction)      # 相位跟着新连接重算
	var total := 0
	for t in doc.turns:
		if int(t.node) == selected_junction:
			total += 1
	print("节点 %d：新增连接 %d 条（该路口现共 %d 条）" % [selected_junction, added, total])
	toast("路口 #%d：新增连接 %d 条（现共 %d 条）" % [selected_junction, added, total], "ok")
	return added

## 冲突查询结果缓存（C2）：键 = 快照指纹 + 节点。
## ★ 属性面板每重建一次就查一次（勾一个相位勾选框也算一次），而相位改动不改变冲突结果
##   → 同一份快照下同一个节点直接复用上次那 692 对，不再让 SimCore 重算一遍。
var _conflict_key := ""
var _conflict_pairs: Array = []

## 取"某路口"的互冲对（★ 判定全部来自 SimCore 的 ConflictMatrix，经桥接 ConflictsAt）
## 返回 [[实时 turn id, 实时 turn id], ...]；桥接不可用 / 失败 → 空数组（面板就当没冲突）
func fetch_conflict_pairs(node_id: int) -> Array:
	var out: Array = []
	if node_id < 0:
		return out
	var b := get_bridge()
	if b == null:
		return out
	var tmp := _cached_temp_spmap()
	if tmp == "":
		return out
	var file_node := int(_fwd_node.get(node_id, -1))
	if file_node < 0:
		return out
	var key := "%d|%d" % [_tmp_sig, node_id]
	if key == _conflict_key:
		return _conflict_pairs.duplicate(true)
	var res = JSON.parse_string(b.ConflictsAt(tmp, file_node))
	if not (res is Dictionary) or not res.get("ok", false):
		push_error("取冲突对失败：", res if res is Dictionary else "返回不是对象")
		return out
	for p in res.get("pairs", []):
		var a := int(_inv_turn.get(int(p[0]), -1))
		var c := int(_inv_turn.get(int(p[1]), -1))
		if a >= 0 and c >= 0:
			out.append([a, c])
	_conflict_key = key
	_conflict_pairs = out.duplicate(true)
	return out

## 取"某路口"的 SimCore 候选转向（只读，不并入 doc）：{ "src-dst" -> type }
## ★ 类型由 SimCore 判（TurnGenerator）；返回的 id 是"文件 id"，翻回实时 id 后再用
func fetch_candidates(node_id: int) -> Dictionary:
	var out := {}
	if node_id < 0:
		return out
	var b := get_bridge()
	if b == null:
		return out
	var tmp := _cached_temp_spmap()
	if tmp == "":
		return out
	var res = JSON.parse_string(b.DeriveTurns(tmp))
	if not (res is Dictionary) or not res.get("ok", false):
		push_error("取候选转向失败：", res if res is Dictionary else "返回不是对象")
		return out
	for t in res.get("turns", []):
		var node := int(_inv_node.get(int(t.node), -1))
		if node != node_id:
			continue
		var src := int(_inv_lane.get(int(t.src_lane), -1))
		var dst := int(_inv_lane.get(int(t.dst_lane), -1))
		if src < 0 or dst < 0:
			continue
		out["%d-%d" % [src, dst]] = String(t.type)
	return out

## 手动画连接：点了一条车道。
##   还没选进口 → 记下它；已选进口 → 这次点的若是出口且组合在候选里 → 建一条连接
func on_lane_clicked(lane_id: int) -> void:
	if selected_junction < 0:
		return
	var lane := doc.find_lane(lane_id)
	if lane.is_empty():
		return
	var is_import := int(lane.to_node) == selected_junction
	if pending_src_lane < 0:
		if is_import:
			pending_src_lane = lane_id
			selection_changed.emit()
			print("手动画连接：进口 lane %d 已选，再点一条出口车道" % lane_id)
		else:
			print("请先点一条【进口】车道（to_node 指向路口的那些）")
		return
	if not (int(lane.from_node) == selected_junction):
		print("已选进口 lane %d；请点一条【出口】车道" % pending_src_lane)
		return
	var key := "%d-%d" % [pending_src_lane, lane_id]
	var src := pending_src_lane
	pending_src_lane = -1
	if not junction_candidates.has(key):
		print("这组进口→出口不在 SimCore 候选里（类型不同类/几何不合理），未创建")
		selection_changed.emit()
		return
	var tid := doc.add_turn(selected_junction, src, lane_id, String(junction_candidates[key]))
	if tid >= 0:
		print("手动画连接：turn %d（%s）" % [tid, junction_candidates[key]])
	else:
		print("该连接已存在或数据不自洽，未创建")
	selection_changed.emit()

## 取消半途的手动画连接（按 Esc 或点空白）
func cancel_pending() -> void:
	if pending_src_lane < 0:
		return
	pending_src_lane = -1
	selection_changed.emit()
	print("已取消手动画连接")

## 自动生成【该路口】的相位（调 PhaseAutoGenerator），写入 doc.signals；返回是否成功
## ★ 只补不覆盖（B05）：该路口【已有】信号时原地改 doc.signals[i]，只把"还没被任何阶段引用"的
##   新连接追加进最后一个阶段的 permitted —— 手调的 duration / offset / 阶段顺序一概不碰。
##   拆路 / 弯弧 / 改车道数后由 _drain_signal_repair 调到这里，配时不会再被抹掉。
## 只保留"阶段里还有现存 turn"的方案 / 阶段（B3：重建时保留旧附加方案用）
## ★ 旧方案里的 turn 若已被重建掉，留着就是 dangling；宁可丢该阶段并告警，也不写坏引用。
func _prune_plans_for_live_turns(plans: Array) -> Array:
	var live := {}
	for t in doc.turns:
		live[int(t.id)] = true
	var out: Array = []
	for pl in plans:
		var stages: Array = []
		for st in (pl as Dictionary).get("stages", []):
			var pr: Array = []
			for v in st.get("protected", []):
				if live.has(int(v)):
					pr.append(int(v))
			var pm: Array = []
			for v in st.get("permitted", []):
				if live.has(int(v)):
					pm.append(int(v))
			if pr.is_empty() and pm.is_empty():
				continue
			var s2: Dictionary = (st as Dictionary).duplicate(true)
			s2["protected"] = pr
			s2["permitted"] = pm
			stages.append(s2)
		if stages.is_empty():
			continue
		var p2: Dictionary = (pl as Dictionary).duplicate(true)
		p2["stages"] = stages
		out.append(p2)
	return out

## ★ 整段摘空后的重建（B06b）：相位被摘空时数值已存进 doc.signal_tuning_stash，重建时就地套回
##   offset / start_time / duration（段数相等才套 duration）；turn id 全换新，旧相位不留。
##   overwrite = true 才允许整份重算（★ 仍保留旧 offset），留给"重新自动生成"入口用。
##   plan_i = 0 用 SimCore 的 2 阶段方案、1 用 4 阶段方案（Suggest 顺序稳定：先 2 后 4）。
func apply_phases_at(node_id: int, overwrite: bool = false, plan_i: int = 0) -> bool:
	# ★ 该路口一条转向都没有 → SimCore 的 Suggest 必抛异常；先优雅失败（不打红字）
	var total := 0
	for t in doc.turns:
		if int(t.node) == node_id:
			total += 1
	if total == 0:
		print("节点 %d：没有任何转向，无法生成相位" % node_id)
		return false
	# ★ 已有信号且不是强制重算（且没换方案）→ 只补不覆盖（连临时文件都不用写，更不碰 SimCore）
	var si := doc.find_signal_index(node_id)
	if si >= 0 and not overwrite and plan_i == 0:
		var ids: Array = []
		for t in doc.turns:
			if int(t.node) == node_id:
				ids.append(int(t.id))
		var r: Dictionary = doc.upsert_signal_stage_turns(node_id, ids)
		var stage_no := int(r.get("stage", 0))
		if stage_no > 0:                       # 有阶段可挂 → 就地补完，已存相位一个字段都没动
			var added := int(r.get("added", 0))
			if added > 0:
				doc.dirty = true
				print("节点 %d：新增连接 %d 条 → 已并入阶段 %d 的 permitted" % [
					node_id, added, stage_no])
			return true
		# plans 为空 / 方案里一个阶段都没有 = 异常数据 → 掉到下面按原逻辑重建（offset 归零）
		push_warning("节点 %d：信号存在但 plans 里没有任何阶段（异常数据）→ 按原逻辑整份重建，offset 归零" % node_id)
	var b := get_bridge()
	if b == null:
		push_error("找不到 EditorBridge 节点")
		return false
	var tmp := _cached_temp_spmap()        # ★ 刚并入 turns → 指纹变了 → 会自动重写
	if tmp == "":
		return false
	var file_node := int(_fwd_node.get(node_id, -1))     # ★ 传"文件 id"给 SimCore
	if file_node < 0:
		push_error("apply_phases_at：节点 %d 不在导出快照里" % node_id)
		return false
	var res = JSON.parse_string(b.SuggestPhases(tmp, file_node))
	if not (res is Dictionary) or not res.get("ok", false):
		push_error("自动生成相位失败：", res if res is Dictionary else "返回不是对象")
		return false
	var plans: Array = res.get("plans", [])
	if plans.is_empty():
		push_error("自动生成相位：候选集为空")
		return false
	if plan_i < 0 or plan_i >= plans.size():
		push_warning("节点 %d：SimCore 只给出 %d 套方案，取不到第 %d 套 → 保持原样" % [
			node_id, plans.size(), plan_i + 1])
		return false
	var p: Dictionary = plans[plan_i]      # [0] = 两阶段方案（[1] 是四阶段）
	# ★ 相位里的 turn id 也是"文件 id"，翻回实时 id
	var stages: Array = []
	for st in p.get("stages", []):
		var s2: Dictionary = st.duplicate(true)
		s2["protected"] = _inv_ids(st.get("protected", []))
		s2["permitted"] = _inv_ids(st.get("permitted", []))
		stages.append(s2)
	# ★ 暂存配时（只留数值：turn id 已全换新，旧相位留着必成 dangling）
	#   offset 取值顺序：暂存值 → overwrite 时的旧值 → 0
	#   换方案（plan_i ≠ 0）时不动暂存袋 —— 段数本来就对不上，留着给下次重建用
	var stash: Dictionary = doc.take_tuning_stash(node_id) if plan_i == 0 else {}
	var keep_offset := 0.0
	if stash.has("offset"):
		keep_offset = float(stash["offset"])
	elif si >= 0 and overwrite:
		keep_offset = float(doc.signals[si].get("offset", 0.0))
	var start_time := float(p.get("start_time", 0.0))
	var durs: Array = stash.get("durations", [])
	var fused := false
	if not durs.is_empty() and durs.size() == stages.size():
		for i in stages.size():
			stages[i]["duration"] = maxf(float(durs[i]), 1.0)      # type 一律用 Suggest 给的
		start_time = float(stash.get("start_time", start_time))
		fused = true
	var dtxt: Array = []
	for st in stages:
		dtxt.append("%.0f" % float(st.get("duration", 0.0)))
	if not durs.is_empty():
		if fused:
			print("节点 %d：已沿用上次配时（offset=%.1f，阶段 %s）" % [node_id, keep_offset, "/".join(dtxt)])
		else:
			print("节点 %d：暂存配时 %d 段 ≠ 新相位 %d 段 → 只恢复 offset=%.1f，duration 用 Suggest 默认（%s）" % [
				node_id, durs.size(), stages.size(), keep_offset, "/".join(dtxt)])
	# ★ 保留附加方案（B3）：手改/导入的 plans[1..] 不能因为一次重建就静默消失。
	#   只保留"阶段里还有现存 turn"的部分；引用已失效的方案丢弃并告警。
	var extra_plans: Array = []
	if si >= 0:
		var old_plans: Array = doc.signals[si].get("plans", [])
		if old_plans.size() > 1:
			var before_n := old_plans.size() - 1
			extra_plans = _prune_plans_for_live_turns(old_plans.slice(1))
			if extra_plans.size() < before_n:
				push_warning("节点 %d：%d 个附加方案里有阶段引用的连接已不存在 → 丢弃 %d 个方案" % [
					node_id, before_n, before_n - extra_plans.size()])
	var new_plans: Array = [{
		"start_time": start_time,
		"stages": stages,
	}]
	if not extra_plans.is_empty():
		new_plans.append_array(extra_plans)
		print("节点 %d：已保留 %d 个附加方案（时变配时）" % [node_id, extra_plans.size()])
	doc.remove_signal(node_id)
	doc.signals.append({
		"node": node_id,
		"offset": keep_offset,
		"plans": new_plans,
	})
	doc.dirty = true
	return true

## 把一排"文件 turn id"翻回实时 id（翻不到的丢掉）
func _inv_ids(ids) -> Array:
	var out: Array = []
	for v in ids:
		var k := int(v)
		if _inv_turn.has(k):
			out.append(int(_inv_turn[k]))
	return out

# --- 自动保存 / 崩溃恢复（B20）-------------------------------------------
## 三个文件的分工（全在 user://autosave/，与工程目录、关卡目录都无关）：
##   last.spmap / last.spdeco.json  —— 内容（成对，恢复时走正常导入路径能自动带上装饰）
##   last.meta.json                 —— 旁注：当时指向哪个关卡文件、脏不脏、什么时候存的、校验错误几条
##   session.flag                   —— 运行中标记：启动时它还在 = 上次没正常退出（正常退出会在 _exit_tree 删掉）
## ★★ 铁律：自动保存【不改 current_path】、【不清 doc.dirty】—— 它只是一份影子备份。

## 启动：建目录 → 判"上次是否正常退出" → 落本次的运行中标记
func _autosave_begin_session() -> void:
	if DirAccess.make_dir_recursive_absolute(AUTOSAVE_DIR) != OK \
			and not DirAccess.dir_exists_absolute(AUTOSAVE_DIR):
		push_warning("自动保存目录建不出来（%s）：自动保存不可用" % AUTOSAVE_DIR)
		return
	# ★ 先看旧标记再写新标记，顺序反了就永远判不出"上次崩过"
	var crashed := FileAccess.file_exists(AUTOSAVE_FLAG)
	_write_text(AUTOSAVE_FLAG, "running %s" % Time.get_datetime_string_from_system())
	if crashed:
		call_deferred("_prompt_recover")     # 延后一拍：别和场景初始化抢弹窗

## 正常退出（关窗 / quit() 两条路都会走到这儿）→ 删标记，下次启动就不会误报崩溃
func _exit_tree() -> void:
	if FileAccess.file_exists(AUTOSAVE_FLAG):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(AUTOSAVE_FLAG))

## 有没有可恢复的自动保存
func has_recoverable_autosave() -> bool:
	return FileAccess.file_exists(AUTOSAVE_SPMAP)

## 读旁注（坏文件 / 没文件 → 空字典，不炸）
func _read_autosave_meta() -> Dictionary:
	if not FileAccess.file_exists(AUTOSAVE_META):
		return {}
	var d = JSON.parse_string(FileAccess.get_file_as_string(AUTOSAVE_META))
	return d if d is Dictionary else {}

## 上次没正常退出 → 问一句要不要恢复；没有自动保存文件就静默跳过（没什么可救的）
func _prompt_recover() -> void:
	if _recover_prompted:
		return
	_recover_prompted = true
	if not has_recoverable_autosave():
		print("上次没有正常退出，但没找到自动保存文件，不做恢复提示")
		return
	var meta := _read_autosave_meta()
	var when := String(meta.get("saved_at", "?"))
	var src := String(meta.get("source_path", ""))
	var errs := int(meta.get("validation_errors", -1))
	var tail := "可直接继续编辑。"
	if errs > 0:
		tail = "内容有 %d 条校验错误，导出前需要修。" % errs
	elif errs < 0:
		tail = "（这次没跑成校验，恢复后请自己按一次导出看看。）"
	var dlg := ConfirmationDialog.new()
	dlg.title = "上次没有正常退出"
	prep_window(dlg)
	dlg.dialog_text = "检测到上次未正常退出。\n\n自动保存于：%s\n原文件：%s\n%s" % [
		when, (src if src != "" else "（当时还没存过盘）"), tail]
	dlg.ok_button_text = "恢复"
	dlg.add_button("丢弃", true, "discard")
	dlg.canceled.connect(func(): _close_dialog(dlg))
	dlg.confirmed.connect(func(): _close_dialog(dlg, restore_autosave))
	dlg.custom_action.connect(func(action: String):
		if action == "discard":
			_close_dialog(dlg, func():
				print("已丢弃上次的自动保存（文件仍留在 %s，下次启动不会再问）" % AUTOSAVE_SPMAP)))
	get_tree().root.add_child(dlg)
	dlg.popup_centered()

## 恢复：★ 走正常导入路径（open_map_file → import_map：format_version 检查 + JSON 合法性 + 装饰配对），
## 再把 current_path 与脏标记还原成"自动保存那一刻"的样子（否则会把影子文件当成当前关卡）。
func restore_autosave() -> bool:
	if not has_recoverable_autosave():
		push_error("恢复失败：没有自动保存文件 %s" % AUTOSAVE_SPMAP)
		return false
	var meta := _read_autosave_meta()
	var src := String(meta.get("source_path", ""))
	# ★ 走正常导入路径；失败就整件事放弃 —— 绝不"假装恢复成功"再让人 Ctrl+S 覆盖原文件
	if not open_map_file(AUTOSAVE_SPMAP):
		push_error("恢复失败：自动保存文件读不进来（当前文档未改动）")
		_alert("恢复失败", "自动保存文件读不进来（可能已损坏）。\n\n当前文档没有被改动，影子文件仍留在：\n%s" % AUTOSAVE_SPMAP)
		return false
	current_path = src                       # ★ 还原成原关卡（没存过盘 = 空，之后 Ctrl+S 会走另存为）
	doc.dirty = bool(meta.get("dirty", true))
	_undo.clear()
	_redo.clear()                            # ★ 恢复 = 换了一份文档，历史作废（与导入同口径）
	print("已恢复自动保存（保存于 %s）｜节点 %d 车道 %d 装饰 %d｜当前关卡文件：%s｜脏标记：%s" % [
		String(meta.get("saved_at", "?")), doc.nodes.size(), doc.lanes.size(), doc.decos.size(),
		(current_path if current_path != "" else "（未保存）"), ("脏" if doc.dirty else "干净")])
	return true

## 自动保存一份（force = true 时连"什么都没动"也照写，自测用）。
## ★ 不碰 current_path、不碰 doc.dirty。
func autosave_now(force: bool = false) -> bool:
	if doc == null:
		return false
	if not force and not doc.dirty and current_path == "":
		return false                          # 刚打开/刚新建、一个字没改 → 没有可救的东西
	# ★ 直接在 live doc 上序列化：`to_dict()` 内部本来就在【深拷贝】上 renumber（不动实时 id），
	#   以前外面又套了一层 `from_snapshot(snapshot())` = 每 5 分钟白拷两遍整图（D4）。
	#   唯一副作用是 `_normalize_int_fields()` 会顺手把 meta.chapter / lanes.lat_index 这类
	#   "该是整数" 的字段在 live doc 上转成 int —— 本来就该是 int，且不涉及 id。
	# ★ 改用 `to_export()`：一次拿到 json + 双向 id 表，专业档存盘要用正查表（fwd_*）。
	#   两条路径写的 JSON 逐字节相同（都是 `_schema_dict` + 2 空格缩进）。
	var ex := doc.to_export()
	_capture_id_maps(ex)
	var json := String(ex["json"])
	if not _write_text(AUTOSAVE_SPMAP, json):
		_autosave_failed("关卡内容写不进 %s" % AUTOSAVE_SPMAP)
		return false
	if not write_deco_file(AUTOSAVE_SPMAP):
		_autosave_failed("装饰文件写不进 %s" % deco_path_for(AUTOSAVE_SPMAP))
		return false
	if not write_sumo_profile_file(AUTOSAVE_SPMAP):
		_autosave_failed("专业档写不进 %s" % sumo_profile_path_for(AUTOSAVE_SPMAP))
		return false
	# ★ 顺手用 SimCore 校验一次影子文件，把错误条数记进旁注 —— 恢复时能提前告诉用户"这份内容有 N 条错"
	var errs := _validate_file_quiet(AUTOSAVE_SPMAP)
	var meta := {
		"source_path": current_path,
		"dirty": doc.dirty,
		"saved_at": Time.get_datetime_string_from_system(false, true),
		"validation_errors": errs,
		"nodes": doc.nodes.size(),
		"lanes": doc.lanes.size(),
		"decos": doc.decos.size(),
	}
	if not _write_text(AUTOSAVE_META, JSON.stringify(meta, "  ", false)):
		_autosave_failed("旁注写不进 %s" % AUTOSAVE_META)
		return false
	print("自动保存 → %s（节点 %d / 车道 %d / 装饰 %d；校验错误 %d）" % [
		AUTOSAVE_SPMAP, doc.nodes.size(), doc.lanes.size(), doc.decos.size(), errs])
	_autosave_warned = false                 # ★ 这次成功 → 下次失败要重新提示（否则一次失败后永远静音）
	return true

## 用 SimCore 校验一个已在盘上的文件，返回 Error 条数；-1 = 桥接不可用 / 校验没跑成
func _validate_file_quiet(path: String) -> int:
	var b := get_bridge()
	if b == null:
		return -1
	var errs_json: String = b.ValidateFile(ProjectSettings.globalize_path(path))
	var parsed = JSON.parse_string(errs_json)
	if not (parsed is Array):
		return -1
	var n := 0
	for e in parsed:
		if e.get("Severity", "") == "Error":
			n += 1
	return n

## 只能点"确定"的告警框：统一走 _close_dialog，避免"父窗口已有独占子窗口"
func _alert(title: String, text: String) -> void:
	var dlg := AcceptDialog.new()
	dlg.title = title
	prep_window(dlg)
	dlg.dialog_text = text
	dlg.confirmed.connect(func(): _close_dialog(dlg))
	dlg.canceled.connect(func(): _close_dialog(dlg))
	get_tree().root.add_child(dlg)
	dlg.popup_centered()

## 写盘失败必须让人知道：打日志 + 弹一次对话框（成功后复位，见 autosave_now）
func _autosave_failed(msg: String) -> void:
	push_error("自动保存失败：" + msg)
	if _autosave_warned:
		return
	_autosave_warned = true
	_alert("自动保存失败",
		"%s\n\n这次没存成，请先手动 Ctrl+S 存一份。\n（同一次失败只提示一次；下次自动保存成功后，再失败会重新提示）" % msg)

## 写一个文本文件（user:// 与 OS 路径都吃）；失败返回 false，绝不静默
func _write_text(path: String, txt: String) -> bool:
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f == null:
		return false
	f.store_string(txt)
	f.close()
	return true

# --- 最近文件（B20）------------------------------------------------------

## 读 user://recent_files.json（{"format_version":1, "files":[...]}）；坏文件只告警 + 空列表，不炸
func _load_recent() -> void:
	recent_files = []
	if not FileAccess.file_exists(RECENT_FILE):
		return
	var d = JSON.parse_string(FileAccess.get_file_as_string(RECENT_FILE))
	if not (d is Dictionary):
		push_warning("最近文件配置不是对象，已忽略：%s" % RECENT_FILE)
		return
	for p in d.get("files", []):
		var s := String(p)
		if s != "" and not recent_files.has(s):
			recent_files.append(s)
	_trim_recent()

## 落盘（失败只告警：最近文件丢了不算事故，别打断用户干活）
func _save_recent() -> void:
	if not _write_text(RECENT_FILE, JSON.stringify(
			{"format_version": 1, "files": recent_files}, "  ", false)):
		push_warning("最近文件配置写不进：%s" % RECENT_FILE)

## 记一条（最新在前、去重、裁到 RECENT_MAX 条），并落盘
func _record_recent(path: String) -> void:
	if path == "" or path.begins_with("user://") or path.begins_with("res://"):
		return                                  # 影子文件/内置路径不进"最近"
	recent_files.erase(path)
	recent_files.push_front(path)
	_trim_recent()
	_save_recent()

func _trim_recent() -> void:
	while recent_files.size() > RECENT_MAX:
		recent_files.pop_back()

## 点"最近"里的第 index 条：文件没了 → 剔掉并告警；有未保存改动 → 先问（与 Ctrl+O 同口径）
func open_recent(index: int) -> bool:
	if index < 0 or index >= recent_files.size():
		return false
	var p := String(recent_files[index])
	if not FileAccess.file_exists(p):
		push_warning("最近文件已不存在，已从列表移除：%s" % p)
		recent_files.remove_at(index)
		_save_recent()
		return false
	if doc.dirty:
		_ask_unsaved("打开最近文件", func(): open_map_file(p))
		return true
	open_map_file(p)
	return true

# --- 文件：打开 / 另存为 / 保存 / 新建 -----------------------------------
## ★ 所有弹窗 / 子窗口都要先过这里（E91 C19，2026-10-28）。
##   内嵌子窗口的视口默认用项目的 clear color（深灰 #4C4C4C）填底；圆角面板盖不满四个角，
##   于是四角露出深灰块（看起来像"圆角卡片底下垫了一张黑纸"）。窗口底透明后，角上只剩主题里的阴影。
func prep_window(w: Window) -> void:
	if w != null:
		w.transparent_bg = true

## 关掉一个弹窗：★ 必须立刻从 root 摘掉 —— queue_free 是延迟的，摘晚了新弹窗会报

## "Attempting to make child window exclusive, but the parent window already has another exclusive child"
func _close_dialog(dlg: Window, on_done: Callable = Callable()) -> void:
	dlg.hide()                       # 立刻隐藏：解除独占占用，但不摘树
	if on_done.is_valid():
		on_done.call_deferred()      # 后续动作延后一拍（那时事件派发已结束）
	dlg.queue_free()                 # 帧末自然释放

func _dialog(mode: int, title: String, on_pick: Callable) -> void:
	var d := FileDialog.new()
	prep_window(d)
	d.access = FileDialog.ACCESS_FILESYSTEM
	d.file_mode = mode
	d.filters = PackedStringArray(["*.spmap ; 关卡文件"])
	d.title = title
	d.size = Vector2i(760, 520)
	if current_path != "":
		d.current_path = current_path            # 从上次的目录开始
	d.file_selected.connect(func(p: String): _close_dialog(d, on_pick.bind(p)))
	d.canceled.connect(func():
		# ★ 取消 = 放弃"先保存再继续"（退出 / 打开 / 新建都靠这个 Callable）：
		#   不清掉的话，下次随便一次 Ctrl+S 成功就会把当时那个"继续"偷偷执行掉（比如直接退出程序）。
		_pending_proceed = Callable()
		_close_dialog(d))
	get_tree().root.add_child(d)
	# ★ 显式把尺寸传给 popup_centered：不带参数的版本会按"内容最小尺寸"弹，
	#   结果文件框挤成一条（文件名字段被压到 130 px、筛选下拉被截），2026-10-28 实测。
	d.popup_centered(Vector2i(760, 520))

## Ctrl+O / "导入文件"：打开一个 .spmap
func ask_open() -> void:
	if doc.dirty:
		_ask_unsaved("打开新关卡", func(): _open_dialog())
		return
	_open_dialog()

func _open_dialog() -> void:
	_dialog(FileDialog.FILE_MODE_OPEN_FILE, "打开关卡文件",
		func(p: String): open_map_file(p))

## Ctrl+S：有路径就直接存，没有就当"另存为"
func save_current() -> void:
	if current_path == "":
		ask_save_as()
		return
	_save_to(current_path)

## Ctrl+Shift+S / "导出文件"：另存为
func ask_save_as() -> void:
	_dialog(FileDialog.FILE_MODE_SAVE_FILE, "导出关卡文件",
		func(p: String): _save_to(p))

## 新建空白关卡
func new_document() -> void:
	if doc.dirty:
		_ask_unsaved("新建关卡", func(): _do_new())
		return
	_do_new()

func _do_new() -> void:
	# ★ 顺序要紧：**先清专业档，再换文档**。
	#   `set_doc()` 会立刻广播 → 属性面板重建；如果这时还是"空文档 + 上关卡的公交线路"，
	#   线路的「起点」下拉会因为新文档一条车道都没有而 `select()` 越界报错
	#   （S3 复验 2026-11-05 抓到的 ERROR）。
	#   ★ 新建关卡：信号方案 / 进口构成都是关卡数据 → 清空；
	#   **但车型库是"这个项目的车队"，跨关卡保留**（从全局库带回，老板 2026-11-05 要求）
	profile = SumoProfile.empty()
	profile.vehicle_types = SumoTypesLibrary.load_types()
	_reset_types_library_sig()
	set_doc(Doc.new())
	_undo.clear()
	_redo.clear()                                # ★ 换文档 → 历史作废
	current_path = ""
	print("已新建空白关卡（%d 节点 %d 车道）" % [doc.nodes.size(), doc.lanes.size()])
	toast("已新建空白关卡", "info")

## 落盘（导出流程不变：序列化 → 校验 → 无 Error 才写 + 同目录 .spdeco.json）
func _save_to(raw_path: String) -> bool:
	var path := raw_path
	if not path.ends_with(".spmap"):
		path += ".spmap"                          # ★ FileDialog 不会自动补后缀
	var res := export_map(path)
	for e in res.errors:
		print("[%s] %s  %s  @ %s" % [
			e.get("Severity", "?"), e.get("Code", "?"),
			e.get("Message", ""), e.get("Path", "")])
	if not res.ok:
		var bad := int(validation_errors)
		toast("保存中止：%s，未落盘 —— 点状态栏「校验」看清单" % ("%d 条校验错误" % bad if bad > 0 else "校验没跑成"),
			"error")
		return false
	current_path = path
	print("当前关卡文件：", current_path)
	toast("已保存：%s" % path.get_file(), "ok")
	_record_recent(current_path)                  # ★ 最近文件（B20）
	if _pending_proceed.is_valid():               # 先保存再继续的那个"继续"
		var cb := _pending_proceed
		_pending_proceed = Callable()
		cb.call()
	return true

## 走版本检查的打开入口（E07 文档 §文件对话框与版本检查）
## ★ 返回 false = 没打开成功：此时【不碰 current_path、不记最近文件、不动当前 doc】。
##   否则用户接着按 Ctrl+S 会把旧文档写进那个失败文件的路径（覆盖它）。
func open_map_file(path: String) -> bool:
	var txt := FileAccess.get_file_as_string(path)
	if txt == "":
		push_error("打开失败：读不到文件 " + path)
		toast("打开失败：读不到文件", "error")
		return false
	var head = JSON.parse_string(txt)
	if not (head is Dictionary):
		push_error("打开失败：不是有效的 JSON 对象 " + path)
		toast("打开失败：不是有效的 JSON", "error")
		return false
	var ver := int(head.get("format_version", 1))
	if ver != 1:
		_confirm_version(ver, path)
		return false
	if not import_map(path):
		push_error("打开中止：内容导入失败，当前文档与文件路径均未改动 " + path)
		toast("打开中止：内容导入失败，当前关卡没有改动", "error")
		return false
	current_path = path
	_record_recent(current_path)                  # ★ 最近文件（B20）
	print("当前关卡文件：", current_path)
	toast("已打开：%s" % path.get_file(), "ok")
	return true

func _confirm_version(ver: int, path: String) -> void:
	var dlg := ConfirmationDialog.new()
	dlg.title = "格式版本不匹配"
	prep_window(dlg)
	dlg.dialog_text = "这个文件的 format_version = %d，本编辑器只支持 1，可能不兼容。仍要打开吗？" % ver
	dlg.ok_button_text = "仍要打开"
	dlg.canceled.connect(func(): _close_dialog(dlg))
	dlg.confirmed.connect(func(): _close_dialog(dlg, _open_ok.bind(path)))
	get_tree().root.add_child(dlg)
	dlg.popup_centered()

func _open_ok(path: String) -> void:
	if not import_map(path):
		push_error("打开中止：内容导入失败，当前文档与文件路径均未改动 " + path)
		return
	current_path = path
	_record_recent(current_path)                  # ★ 最近文件（B20）
	print("当前关卡文件：", current_path)

## 有未保存改动时问一句：保存并继续 / 不保存继续 / 取消
func _ask_unsaved(context: String, on_proceed: Callable) -> void:
	var dlg := ConfirmationDialog.new()
	dlg.title = context
	prep_window(dlg)
	dlg.dialog_text = "有未保存的改动。要先保存吗？"
	dlg.ok_button_text = "保存并继续"
	dlg.add_button("不保存继续", true, "discard")
	dlg.canceled.connect(func(): _close_dialog(dlg))
	dlg.confirmed.connect(func(): _close_dialog(dlg, _proceed_after_save.bind(on_proceed)))
	dlg.custom_action.connect(func(action: String):
		if action == "discard":
			_close_dialog(dlg, on_proceed))
	get_tree().root.add_child(dlg)
	dlg.popup_centered()

func _proceed_after_save(on_proceed: Callable) -> void:
	if current_path == "":
		_pending_proceed = on_proceed        # 还没存过 → 先弹另存为，成功了再继续
		ask_save_as()
		return
	if _save_to(current_path):
		on_proceed.call()

# --- 复制 / 粘贴（Ctrl+C / Ctrl+V）---------------------------------------

## 复制当前选中的（路口优先，其次装饰，其次道路）
func copy_selection() -> void:
	# ★ 路口优先（B21）：连接模式选中路口时 selected_deco / selected_road 都是 -1，
	#   所以放在最前面不会改变原来"装饰优先、其次道路"的优先级。
	if selected_junction >= 0:
		var j := doc.copy_junction_snapshot(selected_junction)
		if not j.is_empty():
			clipboard = {"kind": "junction", "data": j}
			_paste_n = 0
			print("已复制路口 #%d（%d 条路 / %d 条车道 / %d 条连接 / %s；Ctrl+V 粘贴）" % [
				selected_junction, (j["roads"] as Array).size(), (j["lanes"] as Array).size(),
				(j["turns"] as Array).size(),
				"含相位" if not (j["signal"] as Dictionary).is_empty() else "无相位"])
			toast("已复制路口 #%d（Ctrl+V 粘贴）" % selected_junction, "info")
			# ★ 需求不随包复制（A4）：复制这一刻就说清楚，别等粘完发现新路口没车
			var pc: Dictionary = j.get("profiles", {})
			var pct: int = int(pc.get("demand", 0)) + int(pc.get("pedestrians", 0)) + int(pc.get("bicycles", 0))
			if pct > 0:
				print("　⚠ 需求曲线不随路口包复制：机动车 %d / 行人 %d / 非机动车 %d 条，需在新路口重配" % [
					int(pc.get("demand", 0)), int(pc.get("pedestrians", 0)), int(pc.get("bicycles", 0))])
			return
	if selected_deco >= 0:
		var d := doc.copy_deco_snapshot(selected_deco)
		if not d.is_empty():
			clipboard = {"kind": "deco", "data": d}
			_paste_n = 0
			print("已复制装饰 #%d（Ctrl+V 粘贴）" % selected_deco)
			toast("已复制装饰 #%d（Ctrl+V 粘贴）" % selected_deco, "info")
			return
	if selected_road >= 0:
		var r := doc.copy_road_snapshot(selected_road)
		if not r.is_empty():
			clipboard = {"kind": "road", "data": r}
			_paste_n = 0
			print("已复制道路 #%d（Ctrl+V 粘贴）" % selected_road)
			toast("已复制道路 #%d（Ctrl+V 粘贴）" % selected_road, "info")
			return
	print("没有可复制的东西：先选中一条路或一个装饰")
	toast("没有可复制的东西：先选中一条路或一个装饰", "warn")

## 粘贴剪贴板内容（每次多偏一格）
func paste_clipboard() -> void:
	if clipboard.is_empty():
		print("剪贴板是空的")
		toast("剪贴板是空的：先 Ctrl+C 复制一个对象", "warn")
		return
	_paste_n += 1
	var off := Vector2(5.0, -5.0) * float(_paste_n)
	# ★ 路口包（B21）：整包按"包宽 + 20 m"往东让开，连粘按包宽递推 ——
	#   叠在原路口上等于没粘，偏移必须大于包本身。
	if String(clipboard.get("kind", "")) == "junction":
		var joff := _junction_paste_offset(clipboard["data"], _paste_n)
		begin_edit()                     # ★ 一次粘贴 = 一步撤销（按钮/快捷键两个入口都算）
		var nid := doc.paste_junction(clipboard["data"], joff)
		end_edit()
		if nid >= 0:
			set_mode(EditorMode.Kind.CONNECT)
			select_junction(nid)
			print("粘贴路口 → 新节点 #%d（整体偏移 %.0f, %.0f m）" % [nid, joff.x, joff.y])
			toast("已粘贴路口 → 新节点 #%d" % nid, "info")
		return
	if String(clipboard.get("kind", "")) == "deco":
		var did := doc.paste_deco(clipboard["data"], off)
		if did >= 0:
			set_mode(EditorMode.Kind.DECO)
			select_deco(did)
			print("粘贴装饰 → #%d" % did)
			toast("已粘贴装饰 #%d" % did, "info")
		return
	var rid := doc.paste_road(clipboard["data"], off)
	if rid >= 0:
		set_mode(EditorMode.Kind.ROAD)
		select_road(rid)
		print("粘贴道路 → #%d" % rid)
		toast("已粘贴道路 #%d" % rid, "info")

## 粘贴路口的整体偏移：按快照里节点相对坐标的包围盒宽度 + 间隙往东让，连续粘贴按宽度递推
func _junction_paste_offset(payload: Dictionary, n: int) -> Vector2:
	var lo := Vector2.ZERO
	var hi := Vector2.ZERO
	var first := true
	for d in payload.get("nodes", []):
		var p := Vector2(float(d.get("dx", 0.0)), float(d.get("dy", 0.0)))
		if first:
			lo = p
			hi = p
			first = false
		else:
			lo = lo.min(p)
			hi = hi.max(p)
	var gap := maxf(hi.x - lo.x, 20.0) + 20.0
	return Vector2(gap * float(n), 0.0)

# --- 批量建曲线（B21；A6"按比例批量建曲线"）------------------------------

## 把 (kind, 源 key) 的曲线抄给同组（scope = "road" / "junction"）。
## ★ 一次操作 = 一步撤销；存写只经 doc.batch_write_profile → doc.write_profile。
## ★ 不发 selection_changed（会把正在按的按钮建掉，同 B12 的批量限速）—— 由调用方 _refresh_later。
func batch_profile(kind: String, src_key: int, scope: String) -> int:
	begin_edit()
	var keys: Array = doc.profile_group(kind, src_key, scope)
	var n := doc.batch_write_profile(kind, src_key, keys)
	end_edit()
	var what := "行人过街" if kind == "pedestrians" else ("非机动车流" if kind == "bicycles" else "机动车流")
	if keys.size() <= 1:
		print("批量建曲线：%s 在作用域「%s」里没有同组目标（只找到 %d 个位置），一条都没写" % [
			what, scope, keys.size()])
	else:
		print("批量建曲线：%s → %d 个位置（作用域「%s」，源 #%d）" % [what, n, scope, src_key])
	return n

## 连接模式选中路口时用：拿"该路口第一条已有曲线的位置"当源，套给该路口全部进口 / 全部过街。
## ★ 为什么不让人先选源：路口面板里没有"源车道"这个概念，第一条有曲线的进口是最不意外的默认。
func batch_profile_from_junction(node_id: int, kind: String) -> int:
	var src := _first_profile_key_at(node_id, kind)
	if src < 0:
		print("批量建曲线：路口 %d 还没有任何%s曲线 —— 先去车辆模式给一条%s配上，再来批量" % [
			node_id, ("行人过街" if kind == "pedestrians" else "机动车流"),
			("过街连接" if kind == "pedestrians" else "进口车道")])
		return 0
	return batch_profile(kind, src, "junction")

## 该路口里"第一条已经有曲线"的 key：pedestrians → Crosswalk turn；demand/bicycles → 进口车道
func _first_profile_key_at(node_id: int, kind: String) -> int:
	if kind == "pedestrians":
		for t in doc.turns:
			if int(t.node) == node_id and String(t.type) == "Crosswalk" \
					and not doc.find_profile("pedestrians", int(t.id)).is_empty():
				return int(t.id)
		return -1
	for l in doc.lanes:
		if int(l.to_node) != node_id:
			continue
		if kind == "demand" and not Doc.MOTOR_LANE_TYPES.has(String(l.get("type", ""))):
			continue
		if not doc.find_profile(kind, int(l.id)).is_empty():
			return int(l.id)
	return -1

## 当前所有 turn 的 id 集合（回滚时的"操作前基准"）
func _turn_ids_now() -> Dictionary:
	var out := {}
	for t in doc.turns:
		out[int(t.id)] = true
	return out

## 只删"本次操作新增"的连接（按 id 逐条比，操作前就存在的一条都不碰）。
## delete_turn 会顺带清相位死引用与需求记录，所以这里不重复做。
func _rollback_turns_added_since(before: Dictionary) -> int:
	var gone: Array = []
	for t in doc.turns:
		if not before.has(int(t.id)):
			gone.append(int(t.id))
	for tid in gone:
		doc.delete_turn(int(tid))
	return gone.size()

## 把某节点设成指定类型（面板下拉 / 双击都走这里）。
## ★ Signalized 仍与"补连接 + 补相位"绑定：相位失败一律回退，
##   绝不留下"有信号无相位"这种导出必被拦的状态。
func set_node_type(node_id: int, new_type: String) -> void:
	var n := doc.find_node(node_id)
	if n.is_empty() or String(n.type) == new_type:
		selection_changed.emit()              # 让面板把下拉回显成真实值
		return
	var rc := doc.road_count_at(node_id)
	if (new_type == "Origin" or new_type == "Destination") and rc >= 2:
		print("节点 %d 连了 %d 条路，是路口，不能标起点/终点" % [node_id, rc])
		selection_changed.emit()
		return
	if new_type == "Signalized":
		if not doc.can_be_signalized(node_id):
			print("节点 %d 只连了 %d 条路；标不了信号路口" % [node_id, rc])
			selection_changed.emit()
			return
		var was_dirty := doc.dirty                 # ★ 回滚时脏标记一起回滚（"没成功"不该占一步撤销）
		var before_turns := _turn_ids_now()        # ★ 记下"本次操作前就有"的连接 id
		var added := derive_turns_at(node_id)
		n["type"] = "Signalized"
		if not apply_phases_at(node_id):
			n["type"] = "Unsignalized"        # ★ 回退
			doc.remove_signal(node_id)
			var rolled := _rollback_turns_added_since(before_turns)   # ★ 本次新增的连接一并撤销
			doc.dirty = was_dirty
			print("节点 %d：相位生成失败，已回退为普通路口（该路口可能没有可用转向）｜本次新增的 %d 条连接已一并撤销" % [
				node_id, rolled])
			selection_changed.emit()
			return
		doc.dirty = true
		print("节点 %d → 信号路口｜自动生成连接 %d 条，相位已生成" % [node_id, added])
		toast("路口 #%d → 信号路口：生成连接 %d 条，相位已生成" % [node_id, added], "ok")
	else:
		# ★ 转成非信号类型前先把配时数值收进暂存袋（A3）：否则用户手调的 offset/时长直接消失，
		#   只剩 Ctrl+Z 能救。重新标信号时 apply_phases_at 会尽量套回（段数相等才套 duration）。
		var stashed := doc.stash_signal_tuning(node_id)
		doc.remove_signal(node_id)            # 非信号类型：相位记录一并清掉
		n["type"] = new_type
		doc.dirty = true
		print("节点 %d → %s%s" % [node_id, new_type,
			"｜手调配时已暂存（重新标信号时尽量套回）" if stashed else ""])
	# ★ 2026-10-28 交互修订：**只有连接模式才允许路口被选中**。
	#   道路模式下双击节点只是"切类型"（用户要的是改类型，不是选中路口）；
	#   以前这里无条件 `select_junction`，于是道路模式双击节点就把路口选中了，
	#   面板跳到"路口"、切到连接模式又会冒出进口/出口提示 —— 老板报的"路口很难点"就是这套副作用。
	if mode == EditorMode.Kind.CONNECT:
		select_junction(node_id)
	else:
		selection_changed.emit()

## ★ 双击节点：普通路口 ↔ 信号路口（两件事的绑定见 set_node_type）
func toggle_junction_signal(node_id: int) -> void:
	if not doc.can_be_signalized(node_id):
		print("节点 %d 只连了 %d 条路；再画一条经过它，它才算路口" % [
			node_id, doc.road_count_at(node_id)])
		return
	var cur := String(doc.find_node(node_id).get("type", ""))
	set_node_type(node_id, "Unsignalized" if cur == "Signalized" else "Signalized")

# --- 视图：缩放到全部（Home）/ 键位表（F1）（B19）-------------------------

## 全部内容的包围盒（米）。内容 = 节点 + 装饰 —— 道路/车道/连接都由节点派生，
## 节点全在框里就一定能全在框里。没有任何内容 → Rect2()（位置 0、尺寸 0）。
func content_rect_m() -> Rect2:
	var first := true
	var lo := Vector2.ZERO
	var hi := Vector2.ZERO
	for n in doc.nodes:
		var p := Vector2(float(n.x), float(n.y))
		if first:
			lo = p
			hi = p
			first = false
		else:
			lo = lo.min(p)
			hi = hi.max(p)
	for d in doc.decos:
		var p := Vector2(float(d.x), float(d.y))
		if first:
			lo = p
			hi = p
			first = false
		else:
			lo = lo.min(p)
			hi = hi.max(p)
	if first:
		return Rect2()
	return Rect2(lo, hi - lo)

## Home：缩放到"全部内容"，并把相机中心挪到内容中心。
## 空文档 → 回原点、zoom = 1（给一个确定的家，而不是什么都不做）。
## ★ 相机一律当动态对象用（var cam = ...，不写 : Camera2D）—— 要读的是 EditorCamera 自己的
##   zoom_min / zoom_max，写成静态类型就取不到脚本属性了。
func fit_all(margin_ratio: float = 0.08) -> void:
	var sc := get_tree().current_scene
	if sc == null:
		return
	var cam = sc.get_node_or_null("EditorCamera")
	if cam == null:
		push_error("缩放到全部：找不到 EditorCamera 节点")
		return
	var r := content_rect_m()
	var vp: Vector2 = cam.get_viewport_rect().size
	if r.size.x < 0.001 and r.size.y < 0.001:
		cam.zoom = Vector2.ONE
		cam.global_position = GridCanvas.world_to_px(r.position)
		if cam.has_method("_clamp_position"):
			cam._clamp_position()
		print("缩放到全部：没有内容 → 回到原点，缩放 100%")
		return
	# 需要的 zoom = 视口像素 / 内容的像素尺寸（内容米数 × PPM = 世界像素）
	var wx: float = maxf(r.size.x, 0.001) * GridCanvas.PPM
	var wy: float = maxf(r.size.y, 0.001) * GridCanvas.PPM
	var z: float = minf(vp.x / wx, vp.y / wy) * (1.0 - clampf(margin_ratio, 0.0, 0.5))
	var zmin: float = float(cam.get("zoom_min")) if cam.get("zoom_min") != null else 0.01
	var zmax: float = float(cam.get("zoom_max")) if cam.get("zoom_max") != null else 8.0
	z = clampf(z, zmin, zmax)
	cam.zoom = Vector2(z, z)
	cam.global_position = GridCanvas.world_to_px(r.position + r.size * 0.5)
	if cam.has_method("_clamp_position"):
		cam._clamp_position()
	print("缩放到全部：内容 %.1f × %.1f m → 缩放 %d%%" % [r.size.x, r.size.y, int(round(z * 100.0))])

## F1：显隐键位表（面板自己注册到 help_panel；没挂面板时给一句提示，不静默）
func toggle_help() -> void:
	if help_panel != null and is_instance_valid(help_panel):
		help_panel.toggle_visible()
		return
	print("没找到键位表面板：Main.tscn 里应有一个挂 help_panel.gd 的 CanvasLayer/HelpPanel 节点")

# --- 大纲 → 选中 / 相机跳转（对象大纲批）-----------------------------------

## 大纲用：把相机中心移到某个世界坐标（米）。范围夹取交给相机自己（EditorCamera._clamp_position）。
func focus_world(p_m: Vector2) -> void:
	var sc := get_tree().current_scene
	if sc == null:
		return
	var cam := sc.get_node_or_null("EditorCamera") as Camera2D
	if cam == null:
		return
	cam.global_position = GridCanvas.world_to_px(p_m)
	if cam.has_method("_clamp_position"):
		cam._clamp_position()

## 大纲用：按对象种类选中它（kind = "road" / "junction" / "turn" / "deco"）。
## ★ 先 set_mode 再 select_x —— set_mode 会把所有选中清空，顺序反了就白选。
## focus_camera = true 时顺带把相机居中到该对象（双击走这条；单击只选中不移动视野）。
func select_object(kind: String, id: int, focus_camera: bool = true) -> void:
	var target := Vector2.ZERO
	var has_target := false
	match kind:
		"road":
			set_mode(EditorMode.Kind.ROAD)
			select_road(id)
			var r := doc.find_road(id)
			if not r.is_empty():
				target = (doc.node_pos(int(r.a_node)) + doc.node_pos(int(r.b_node))) * 0.5
				has_target = true
		"junction":
			set_mode(EditorMode.Kind.CONNECT)
			select_junction(id)
			if not doc.find_node(id).is_empty():
				target = doc.node_pos(id)
				has_target = true
		"turn":
			set_mode(EditorMode.Kind.CONNECT)
			select_turn(id)
			var t := doc.find_turn(id)
			if not t.is_empty():
				var nid := int(t.get("node", -1))
				if nid >= 0 and not doc.find_node(nid).is_empty():
					target = doc.node_pos(nid)     # 连接没有自己的坐标 → 居中到它所在路口
					has_target = true
		"deco":
			set_mode(EditorMode.Kind.DECO)
			select_deco(id)
			var dc := doc.find_deco(id)
			if not dc.is_empty():
				target = Vector2(float(dc.get("x", 0.0)), float(dc.get("y", 0.0)))
				has_target = true
		_:
			push_warning("select_object：不认识的种类 %s" % kind)
			return
	if focus_camera and has_target:
		focus_world(target)

# --- 轻提示 / 校验清单（P4-1）---------------------------------------------

## 推一条界面提示。★ 与既有 `print()` **双写**，不是替代：Output 里的日志一条不少。
## kind = "info" / "ok" / "warn" / "error"（只影响左边那颗点的颜色）
func toast(text: String, kind: String = "info") -> void:
	var host := _find_ui("Toast")
	if host != null and host.has_method("push"):
		host.push(text, kind)


## 打开「校验问题」浮窗（列上一次校验的条目，点一条定位）
func open_validation_drawer() -> void:
	var d := _find_ui("ValidationDrawer")
	if d == null:
		print("没找到校验清单浮窗：Main.tscn 的 CanvasLayer 下应有一个挂 validation_drawer.gd 的节点")
		return
	if d.has_method("open_and_refresh"):
		d.open_and_refresh()


## 状态栏 / 顶栏的「校验」胶囊：点一下切一次
func toggle_validation_drawer() -> void:
	var d := _find_ui("ValidationDrawer")
	if d == null:
		print("没找到校验清单浮窗：Main.tscn 的 CanvasLayer 下应有一个挂 validation_drawer.gd 的节点")
		return
	var c := d as Control
	if c != null and c.visible:
		c.hide()
		return
	open_validation_drawer()


## 场景里按名字找一个 UI 节点（先从 CanvasLayer 下找，找不到再递归）
func _find_ui(node_name: String) -> Node:
	var sc := get_tree().current_scene
	if sc == null:
		return null
	var direct := sc.get_node_or_null("CanvasLayer/" + node_name)
	if direct != null:
		return direct
	return sc.find_child(node_name, true, false)


## 这条校验能不能定位到画布对象（Path 形如 `lanes[12].x` / `turns[3].type` / `nodes[7]`）
func can_locate_validation(e: Dictionary) -> bool:
	return not _resolve_validation_target(e).is_empty()


## 点校验清单里的一条 → 选中该对象并把相机移过去（走的就是大纲双击那条路，不新增能力）
func locate_validation(e: Dictionary) -> void:
	var t := _resolve_validation_target(e)
	if t.is_empty():
		return
	match String(t["kind"]):
		"junction":
			select_object("junction", int(t["id"]), true)
		"turn":
			select_object("turn", int(t["id"]), true)
		"lane":
			var lid := int(t["id"])
			set_mode(EditorMode.Kind.TRAFFIC)
			select_traffic_lane(lid)
			var l := doc.find_lane(lid)
			if not l.is_empty():
				var a := doc.node_pos(int(l.get("from_node", -1)))
				var b := doc.node_pos(int(l.get("to_node", -1)))
				focus_world((a + b) * 0.5)


## 把校验错误里的 Path 翻成"画布上的对象"。
## ★ 只认三张表：nodes / lanes / turns —— `.spmap` 里也只有这三张是画布对象。
##   signals 是按节点汇总的，meta / game_rules / demand 没有画布位置 → 一律返回 {}，清单里就不给点。
func _resolve_validation_target(e: Dictionary) -> Dictionary:
	var path := String(e.get("Path", ""))
	var lb := path.find("[")
	if lb <= 0:
		return {}
	var rb := path.find("]", lb)
	if rb <= lb:
		return {}
	var table := path.substr(0, lb)
	var digits := path.substr(lb + 1, rb - lb - 1)
	if not digits.is_valid_int():
		return {}
	var file_id := int(digits)
	match table:
		"nodes":
			var nid := int(_inv_node.get(file_id, -1))
			return {"kind": "junction", "id": nid} if nid >= 0 else {}
		"turns":
			var tid := int(_inv_turn.get(file_id, -1))
			return {"kind": "turn", "id": tid} if tid >= 0 else {}
		"lanes":
			var lid := int(_inv_lane.get(file_id, -1))
			return {"kind": "lane", "id": lid} if lid >= 0 else {}
	return {}


# --- 多选（框选路）/ 批量操作（多选批）------------------------------------
## ★ 口径：selection 只装 road id，且只在"选的是路"时有意义（选连接/路口/装饰都会清空它）。
##   selected_road / selected_turn / ... 仍是"最后选中项"，其它模式与面板的读法一律不改。

## 当前多选里的路 id（已过滤掉被删的）—— 返回副本，调用方可随便改
func selection_roads() -> Array:
	var out: Array = []
	for rid in selection:
		if not doc.find_road(int(rid)).is_empty():
			out.append(int(rid))
	return out


## 设定多选（框选走这里）：selected_road 落到"最后一条"，状态栏/单选面板读它照旧
func set_selection_roads(ids: Array) -> void:
	var keep: Array = []
	for rid in ids:
		if not doc.find_road(int(rid)).is_empty() and not keep.has(int(rid)):
			keep.append(int(rid))
	selection = keep
	selected_road = int(keep[keep.size() - 1]) if not keep.is_empty() else -1
	selection_changed.emit()
	var txt: Array = []
	for rid in keep:
		txt.append(str(int(rid)))
	print("框选：%d 条路（#%s）" % [keep.size(), ",".join(txt)])

## Ctrl+单击一条路：在选中集里加/减（框完微调用）
func toggle_road_in_selection(road_id: int) -> void:
	if doc.find_road(road_id).is_empty():
		return
	var ids := selection_roads()
	if ids.has(road_id):
		ids.erase(road_id)
	else:
		ids.append(road_id)
	set_selection_roads(ids)


## 别处把这些路删了（或撤销回来）→ 剔掉已不存在的，剩下的仍保持选中
func prune_selection() -> void:
	var keep: Array = []
	for rid in selection:
		if not doc.find_road(int(rid)).is_empty():
			keep.append(int(rid))
	var sel_dead: bool = selected_road >= 0 and doc.find_road(selected_road).is_empty()
	if keep.size() == selection.size() and not sel_dead:
		return
	selection = keep
	if not keep.is_empty():
		if not keep.has(selected_road):
			selected_road = int(keep[keep.size() - 1])
	elif doc.find_road(selected_road).is_empty():
		selected_road = -1
	selection_changed.emit()

## 批量改限速：★ 走 Doc.set_road_template_field —— 原地改，不重建 lane，不动 turn / 相位 / 需求
func set_selection_speed(v: float) -> int:
	var ids := selection_roads()
	if ids.is_empty():
		return 0
	begin_edit()
	var n := 0
	for rid in ids:
		if doc.set_road_template_field(int(rid), "speed_limit", v):
			n += 1
	end_edit()
	print("批量：限速 %.2f m/s → %d 条路" % [v, n])
	toast("批量：限速 %.2f m/s → %d 条路" % [v, n], "info")
	return n

## 批量套模板：keys 只覆盖给出的字段（每条路其余字段不动）
## ★ 与单条一致走 Doc.rebuild_road —— 会重建 lane，引用旧车道的 turn 会被删（一条撤销可整体回退）
func apply_selection_template(keys: Dictionary) -> int:
	var ids := selection_roads()
	if ids.is_empty():
		return 0
	begin_edit()
	var n := 0
	for rid in ids:
		var road := doc.find_road(int(rid))
		if road.is_empty():
			continue
		var t: Dictionary = (road.template as Dictionary).duplicate(true)
		for k in keys:
			t[k] = keys[k]
		doc.rebuild_road(int(rid), t)
		n += 1
	end_edit()
	new_road_template = keys.duplicate(true)     # 与单条预设一致：顺手记成"新建路默认"
	print("批量：套模板（%d 个字段）→ %d 条路" % [keys.size(), n])
	toast("批量：套模板 → %d 条路（车道已重建，连接需重新生成）" % n, "warn")
	return n

## 批量删：整体一个撤销步；删完把已不存在的剔掉，剩下的仍选中
func delete_selection() -> void:
	var ids := selection_roads()
	if ids.is_empty():
		return
	begin_edit()
	for rid in ids:
		doc.delete_road(int(rid))
	end_edit()
	var keep: Array = []
	for rid in ids:
		if not doc.find_road(int(rid)).is_empty():
			keep.append(int(rid))
	selection = keep
	selected_road = int(keep[keep.size() - 1]) if not keep.is_empty() else -1
	selection_changed.emit()
	print("批量删除：%d 条路 → 剩 %d 条仍选中" % [ids.size(), keep.size()])
	toast("已删除 %d 条路（Ctrl+Z 可撤销）" % (ids.size() - keep.size()), "info")
