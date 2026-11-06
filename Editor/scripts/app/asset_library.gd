class_name AssetLibrary
extends RefCounted

## 装饰素材库：扫目录 + 按文件名自动分组，零硬编码。
## 约定：`<素材根>/deco/<分组>_<种类>[_<状态>].png`（基名第一个 `_` 前 = 分组，其余 = 种类）
## 返回：`{ "tree": [ {"asset": "deco/tree_oak.png", "label": "oak"}, ... ], ... }`
##
## ★ 素材根不固定（2026-10-28 方案 3）：发布版读 `<exe 同目录>/assets/deco`（用户可往里加图），
##   编辑器里读 `res://assets/deco`。所有路径规则集中在 `AssetPaths`，这里只管"扫 + 分组"。
## ★ 多个根里的同名素材只收第一个（外部优先）—— 关卡里的 `asset` 是相对 `assets/` 的路径，
##   两个来源指向同一个 key，不能出现两条重复项。

const DECO_SUBDIR := "deco"

static func scan_deco() -> Dictionary:
	var groups := {}
	var bad := 0
	var seen := {}                                   # asset 相对路径 → 已收（跨根去重）
	for root in AssetPaths.roots():
		var d := DirAccess.open(AssetPaths.join(root, DECO_SUBDIR))
		if d == null:
			continue                                 # 这个根没有 deco 目录：正常，跳过
		for fname in d.get_files():
			if not fname.ends_with(".png"):
				continue                             # 跳过 .import 与其它格式
			var base := fname.get_basename()          # "tree_oak"
			var parts := base.split("_", false)
			if parts.size() < 2:
				bad += 1
				continue                             # 不符合"分组_种类"
			var asset := "%s/%s" % [DECO_SUBDIR, fname]
			if seen.has(asset):
				continue
			seen[asset] = true
			var group := String(parts[0])
			var label := "_".join(parts.slice(1))
			if not groups.has(group):
				groups[group] = []
			(groups[group] as Array).append({"asset": asset, "label": label})
	var keys: Array = groups.keys()
	keys.sort()                                      # 分组名排序，顺序稳定
	var out := {}
	for g in keys:
		var items: Array = groups[g]
		items.sort_custom(func(a, b): return String(a["label"]) < String(b["label"]))
		out[g] = items
	if bad > 0:
		print("素材库：%d 个文件不符合『分组_种类』命名，已跳过" % bad)
	return out
