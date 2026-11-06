class_name SumoProbe
extends RefCounted

## netconvert 探针的客户端（技术方案06 §3 D3）。
##
## C# 侧 `MapExportBridge.ProbeSumo` 在后台线程跑一次 netconvert，这里只负责
## **发起 → 每帧轮询 → 拿结果**，绝不阻塞界面（README 铁律 25 的"慢查询"）。
##
## 为什么需要探针：SUMO 的 `tlLogic.state` 是按 `linkIndex` 排的，而 linkIndex 由
## netconvert 自己决定（含人行横道、自行车、内部车道）。编辑器必须先看见这张真实表，
## 用户在信号灯矩阵里点的格子才对应得上实际生效的灯。
##
## ★ 结果按"内容指纹"缓存：指纹变了（几何/连接变了）就作废，见 `has_result_for()`。

var busy: bool = false          ## 有任务在跑（App 的 _process 据此轮询）
var error: String = ""          ## 最近一次失败原因（"" = 没失败）
var result: Dictionary = {}     ## {ok, tls:[...], edges:[...]}

var _job: String = ""
var _sig: int = 0               ## 这份结果对应的内容指纹；0 = 无有效结果


func invalidate() -> void:
	busy = false
	error = ""
	result = {}
	_job = ""
	_sig = 0


## 这份结果还对得上当前内容吗？
func has_result_for(sig: int) -> bool:
	return _sig != 0 and _sig == sig and not result.is_empty()


## 发起一次（立即返回，不代表算完）。成功发起返回 true。
func request(bridge: Node, spmap_path: String, options: Dictionary, sig: int) -> bool:
	if bridge == null:
		error = "找不到 MapExportBridge 节点"
		return false
	if spmap_path == "":
		error = "拿不到当前关卡的临时快照"
		return false

	var raw = bridge.ProbeSumo(spmap_path, JSON.stringify(options))
	var parsed = JSON.parse_string(String(raw))
	if not (parsed is Dictionary):
		error = "探针启动返回的不是 JSON：" + String(raw)
		return false
	if not bool(parsed.get("ok", false)):
		error = "探针启动失败：" + String(parsed.get("error", "未知原因"))
		return false

	_job = String(parsed.get("job", ""))
	if _job == "":
		error = "探针启动失败：没有拿到任务号"
		return false
	busy = true
	error = ""
	result = {}
	_sig = sig
	return true


## 轮询。返回 true = 本次刚出结果（成功或失败，看 `error` / `result.ok`）。
func poll(bridge: Node) -> bool:
	if not busy or bridge == null:
		return false
	var raw = bridge.ProbeSumoPoll(_job)
	var parsed = JSON.parse_string(String(raw))
	if not (parsed is Dictionary):
		busy = false
		error = "探针轮询返回的不是 JSON"
		return true

	var state := String(parsed.get("state", ""))
	if state == "running":
		return false
	busy = false
	if state != "done":
		error = String(parsed.get("error", "探针失败"))
		return true

	var payload = JSON.parse_string(String(parsed.get("json", "")))
	if not (payload is Dictionary):
		error = "探针结果不是 JSON 对象"
		return true
	result = payload
	if not bool(result.get("ok", false)):
		error = String(result.get("error", "探针失败"))
		_sig = 0                         # 失败不留缓存，下次重试
		return true
	return true


## 某个节点的 link 表（专业信号灯编辑器的行）。找不到返回空数组。
func links_at(node_id: int) -> Array:
	for tls in result.get("tls", []):
		if int(tls.get("node", -1)) == node_id:
			return tls.get("links", [])
	return []


## 某个节点的 tlLogic 记录；找不到返回空字典。
func tls_at(node_id: int) -> Dictionary:
	for tls in result.get("tls", []):
		if int(tls.get("node", -1)) == node_id:
			return tls
	return {}
