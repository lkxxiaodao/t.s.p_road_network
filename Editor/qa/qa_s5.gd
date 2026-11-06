extends Node

## S5 复验：**走真实控件**改运行配置 → 导出 .sumocfg → 真跑 sumo → 回统计 → 搬到别处再跑。
##
## 跑法：
##   godot --headless --path Editor res://qa/qa_s5.tscn > qa_s5.log 2>&1
##   grep -E "\[FAIL\]|SCRIPT ERROR|ERROR:" qa_s5.log      # 期望 0
##
## ★ 本脚本的"试跑"是**真起 sumo 子进程**（后台线程 + 轮询），耗时取决于仿真时长。
##   默认把 end 改成 240 s，示例关卡约 2~4 秒跑完。

var panel: Node = null
var fails := 0


func _mark(text: String) -> void:
	print("\n[QA] " + text)


func _ok(text: String, cond: bool) -> void:
	if cond:
		print("  ✓ " + text)
	else:
		fails += 1
		print("  [FAIL] " + text)


func _settle(extra: float = 0.0) -> void:
	await get_tree().process_frame
	await get_tree().create_timer(0.45 + extra).timeout
	await get_tree().process_frame


func _ready() -> void:
	await get_tree().process_frame
	await get_tree().process_frame

	# ============ 准备 ============
	_mark("载入示例关卡 + 切专业模式，运行配置归零")
	EditorApp.import_map(ProjectSettings.globalize_path("res://../docs/examples/01-1-first-crossing.spmap"))
	EditorApp.set_edition(Edition.Level.PRO)
	EditorApp.profile.config.clear()
	EditorApp.profile.touch()
	await _settle()

	panel = VBoxContainer.new()
	panel.set_script(load("res://scripts/property_panel.gd"))
	panel.size = Vector2(396, 1800)
	add_child(panel)
	EditorApp.set_mode(EditorMode.Kind.ROAD)      # 关卡设置
	await _settle()

	# ============ 面板 ============
	_mark("关卡面板 · 「SUMO 运行配置」小节")
	_ok("小节出现", _find_button("SUMO 运行配置") != null)
	for label in ["开始时刻", "结束时刻", "仿真步长", "随机种子", "传送阈值"]:
		_ok("有「%s」输入框" % label, _find_control_under(label, "SpinBox") != null)

	_mark("改结束时刻 3600 → 240（试跑会跑这么久）")
	var end_spin: SpinBox = _find_control_under("结束时刻", "SpinBox")
	if end_spin != null:
		end_spin.value = 240.0
		await _settle()
	_ok("config.end = 240", is_equal_approx(SumoRunModel.value(EditorApp.profile, "end"), 240.0))
	_ok("默认值没被写进文件（还是 3600 的项不落盘）",
		not EditorApp.profile.config.has("begin"))

	_mark("改随机种子 → 7")
	var seed_spin: SpinBox = _find_control_under("随机种子", "SpinBox")
	if seed_spin != null:
		seed_spin.value = 7.0
		await _settle()
	_ok("config.seed = 7", is_equal_approx(SumoRunModel.value(EditorApp.profile, "seed"), 7.0))

	# ============ 导出 ============
	_mark("导出 net + rou + sumocfg")
	var out_dir := OS.get_environment("TEMP").path_join("qa_s5_export")
	DirAccess.make_dir_recursive_absolute(out_dir)
	var base := out_dir.path_join("qa5")
	for ext in [".net.xml", ".rou.xml", ".sumocfg", ".add.xml", ".tll.xml"]:
		if FileAccess.file_exists(base + ext):
			DirAccess.remove_absolute(base + ext)
	var res: Dictionary = EditorApp.export_with_options({
		"spmap": false, "shp": false,
		"net": true, "rou": true, "add": false, "sumocfg": true,
		"outputBase": base,
		"netconvertPath": "",
		"netconvertSearchDir": ProjectSettings.globalize_path("res://../Tools/sumo/bin"),
		"laneWidthM": 3.5,
		"includeCrosswalks": true,
		"yellowDuration": 3.0,
		"allRedDuration": 2.0,
	})
	print("[QA] 导出结果：%s" % str(res.get("results", [])))

	var cfg_path := base + ".sumocfg"
	_ok("sumocfg 生成了", FileAccess.file_exists(cfg_path))
	var text := ""
	if FileAccess.file_exists(cfg_path):
		text = FileAccess.get_file_as_string(cfg_path)
	_ok("net/rou 用相对路径（只有文件名）",
		text.contains('value="qa5.net.xml"') and text.contains('value="qa5.rou.xml"'))
	_ok("文件里没有绝对路径", not text.contains(out_dir))
	_ok("end = 240 写进去了", text.contains('<end value="240"'))
	_ok("seed = 7 写进去了", text.contains('<seed value="7"'))
	_ok("统计开关打开", text.contains("duration-log.statistics"))
	_ok("没有附加文件时不写 additional-files 段", not text.contains("additional-files"))

	# ============ 真跑 ============
	_mark("▶ 试跑（真起 sumo 子进程）")
	var r: Dictionary = await EditorApp.run_sumo_and_wait(cfg_path)
	_ok("试跑成功（%s）" % String(r.get("error", "")), bool(r.get("ok", false)))
	var s: Dictionary = r.get("stats", {})
	print("[QA] 统计：%s" % EditorApp.sumo_stats_text(r))
	_ok("进车 > 0（实际 %d）" % int(s.get("inserted", 0)), int(s.get("inserted", 0)) > 0)
	_ok("平均速度 > 0（实际 %.2f）" % float(s.get("speed", 0.0)), float(s.get("speed", 0.0)) > 0.0)
	_ok("跑到 240 s（实际 %.0f）" % float(s.get("ended_at", 0.0)),
		is_equal_approx(float(s.get("ended_at", 0.0)), 240.0))
	_ok("统计样本数 > 0", int(s.get("statistics_sample", 0)) > 0)
	_ok("没有 Error 行", not s.has("raw_errors"))

	# ============ 整夹搬移 ============
	_mark("把整个导出文件夹复制到别处再跑（验收项：整夹可搬移）")
	var moved_dir := OS.get_environment("TEMP").path_join("qa_s5_moved")
	_clear_dir(moved_dir)
	DirAccess.make_dir_recursive_absolute(moved_dir)
	_copy_files(out_dir, moved_dir)
	_ok("搬移后的 cfg 存在", FileAccess.file_exists(moved_dir.path_join("qa5.sumocfg")))
	var r2: Dictionary = await EditorApp.run_sumo_and_wait(moved_dir.path_join("qa5.sumocfg"))
	_ok("搬到别处仍能跑（%s）" % String(r2.get("error", "")), bool(r2.get("ok", false)))
	var s2: Dictionary = r2.get("stats", {})
	_ok("两次进车数一致（%d / %d）" % [int(s.get("inserted", 0)), int(s2.get("inserted", 0))],
		int(s.get("inserted", 0)) == int(s2.get("inserted", 0)))

	# ============ 恢复默认 ============
	_mark("「恢复默认」→ config 清空")
	# ★ 文案要够长：面板里还有个「权重恢复默认 40/20/20/20」，只搜"恢复默认"会点错
	var reset_btn: Button = _find_button("恢复默认（0~3600")
	if reset_btn != null:
		reset_btn.pressed.emit()
		await _settle()
	_ok("config 里没有自定义键了", EditorApp.profile.config.is_empty())
	_ok("读出来仍是默认 3600", is_equal_approx(SumoRunModel.value(EditorApp.profile, "end"), 3600.0))

	# ============ 清场 ============
	EditorApp.profile.config.clear()
	EditorApp.profile.touch()
	print("\n[QA] === 完成：失败 %d 项 ===" % fails)
	get_tree().quit(1 if fails > 0 else 0)


# ============================================================
# 小工具
# ============================================================

func _find_all(root: Node, cls: String) -> Array:
	return Array(root.find_children("*", cls, true, false))


func _find_button(text: String) -> Button:
	for b in _find_all(panel, "Button"):
		if String((b as Button).text).contains(text):
			return b as Button
	return null


## "字段名 + 值控件"两列：按同一行里有这个字段名来筛
func _find_control_under(label_text: String, cls: String) -> Control:
	for row in _find_all(panel, "HBoxContainer"):
		var hit := false
		for c in (row as Node).get_children():
			if c is Label and String((c as Label).text).contains(label_text):
				hit = true
				break
		if not hit:
			continue
		for c in (row as Node).find_children("*", cls, true, false):
			return c as Control
	return null


func _clear_dir(dir_path: String) -> void:
	var d := DirAccess.open(dir_path)
	if d == null:
		return
	d.list_dir_begin()
	var name := d.get_next()
	while name != "":
		if not d.current_is_dir():
			DirAccess.remove_absolute(dir_path.path_join(name))
		name = d.get_next()
	d.list_dir_end()


func _copy_files(from_dir: String, to_dir: String) -> void:
	var d := DirAccess.open(from_dir)
	if d == null:
		return
	d.list_dir_begin()
	var name := d.get_next()
	while name != "":
		if not d.current_is_dir():
			DirAccess.copy_absolute(from_dir.path_join(name), to_dir.path_join(name))
		name = d.get_next()
	d.list_dir_end()
