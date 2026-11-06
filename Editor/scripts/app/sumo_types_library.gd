class_name SumoTypesLibrary
extends RefCounted

## 车型库的**全局持久化**（跨关卡、跨会话）。
##
## ## 为什么要有它
##
## 车型定义（车长 / 限速 / 加减速 / 风格 / 颜色）是"这个项目常用的车队"，不是某一关的私有物。
## 但专业档 `.sumo.json` 是**跟着关卡文件**走的 —— 用户新建关卡、或者配完车型没保存关卡就关软件，
## 车型库就没了，下次得重建（老板 2026-11-05 反馈）。
##
## 所以这里再存一份到 `user://`，**与关卡无关**：
##
## | 场景 | 车型库从哪来 |
## | --- | --- |
## | 打开有关卡专业档的 `.spmap` | 用关卡里的（可跨关卡不同车队） |
## | 打开没有专业档的 `.spmap` / 新建关卡 | 从本全局库带入 |
## | 配完车型没保存关卡就关软件 | 本全局库已经写过了，不丢 |
##
## 判据是"用户改过车型库"，而不是"打开了哪个文件" —— 载入关卡时会重置同步签名，
## 避免"打开旧关卡把新车型库覆盖掉"。
##
## 只存 `vehicle_types`。**进口构成 `lane_mix` 不进来**：它绑定具体车道 id，是关卡数据。

const PATH := "user://sumo_vehicle_library.json"
const FORMAT_VERSION := 1


## 读全局车型库；文件不存在 / 坏了都返回空数组（绝不因为一个缓存文件挡住启动）
static func load_types() -> Array:
	if not FileAccess.file_exists(PATH):
		return []
	var f := FileAccess.open(PATH, FileAccess.READ)
	if f == null:
		push_warning("全局车型库读不到：%s" % PATH)
		return []
	var raw := f.get_as_text()
	f.close()

	var parsed: Variant = JSON.parse_string(raw)
	if not (parsed is Dictionary):
		push_warning("全局车型库不是 JSON 对象，已忽略")
		return []

	var d: Dictionary = parsed
	var rows: Variant = d.get("vehicle_types", [])
	if not (rows is Array):
		return []

	# 形状兜底：只收对象，且必须有 id（没 id 的条目引用不了，留着只会误导）
	var out: Array = []
	for row in (rows as Array):
		if not (row is Dictionary):
			continue
		var r: Dictionary = row
		if String(r.get("id", "")).strip_edges() == "":
			continue
		out.append(r.duplicate(true))
	return out


## 写全局车型库。空数组 = 删文件（不留一个空壳）
static func save_types(rows: Array) -> void:
	if rows.is_empty():
		if FileAccess.file_exists(PATH):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
		return

	var f := FileAccess.open(PATH, FileAccess.WRITE)
	if f == null:
		push_warning("全局车型库写不进去：%s" % PATH)
		return
	f.store_string(JSON.stringify({
		"format_version": FORMAT_VERSION,
		"vehicle_types": rows,
	}, "  ", false))
	f.close()
