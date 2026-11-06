class_name SumoRun
extends RefCounted

## 「一键试跑」的客户端（S5）。
##
## C# 侧 `MapExportBridge.RunSumo` 在后台线程跑 `sumo -c <cfg>`，
## 这里只负责**发起 → 每帧轮询 → 拿结果**，绝不阻塞界面（与 `SumoProbe` 同一套手法，
## README 铁律 25「慢查询」）。
##
## 与探针的区别：探针有结果缓存（按内容指纹），试跑**没有** ——
## 同一个 cfg 跑两次结果应该一样，但用户点"再跑一次"就是要重跑（换种子、看波动）。

var busy: bool = false          ## 有任务在跑（App 的 _process 据此轮询）
var error: String = ""          ## 最近一次失败原因（"" = 没失败）
var result: Dictionary = {}     ## {ok, error, stats:{...}, tail, exe, cfg, exit_code}

var _job: String = ""
var _cfg: String = ""


## 这次试跑用的 cfg（面板显示用）
func cfg_path() -> String:
	return _cfg


func invalidate() -> void:
	busy = false
	error = ""
	result = {}
	_job = ""
	_cfg = ""


## 发起一次（立即返回，不代表跑完）。成功发起返回 true。
func start(bridge: Node, cfg_path: String, exe_path: String,
		sumo_home: String, search_dir: String) -> bool:
	if bridge == null:
		error = "找不到 MapExportBridge 节点"
		return false
	if cfg_path == "":
		error = "还没有 .sumocfg —— 先导出一次（勾上「SUMO .sumocfg」）"
		return false

	var raw = bridge.RunSumo(cfg_path, exe_path, sumo_home, search_dir)
	var parsed = JSON.parse_string(String(raw))
	if not (parsed is Dictionary):
		error = "试跑启动返回的不是 JSON：" + String(raw)
		return false
	if not bool(parsed.get("ok", false)):
		error = String(parsed.get("error", "未知原因"))
		return false

	_job = String(parsed.get("job", ""))
	if _job == "":
		error = "试跑启动失败：没有拿到任务号"
		return false

	_cfg = cfg_path
	busy = true
	error = ""
	result = {}
	return true


## 轮询。返回 true = 本次刚出结果（成功或失败，看 `error` / `result.ok`）。
func poll(bridge: Node) -> bool:
	if not busy or bridge == null:
		return false
	var raw = bridge.PollSumo(_job)
	var parsed = JSON.parse_string(String(raw))
	if not (parsed is Dictionary):
		busy = false
		error = "试跑轮询返回的不是 JSON"
		return true

	var state := String(parsed.get("state", ""))
	if state == "running":
		return false
	busy = false
	if state != "done":
		error = String(parsed.get("error", "试跑失败"))
		return true

	var payload = JSON.parse_string(String(parsed.get("json", "")))
	if not (payload is Dictionary):
		error = "试跑结果不是 JSON 对象"
		return true

	result = payload
	if not bool(result.get("ok", false)):
		error = String(result.get("error", "试跑失败"))
		return true
	return true


## 统计（失败时是空字典）
func stats() -> Dictionary:
	var s = result.get("stats", {})
	return s if s is Dictionary else {}
