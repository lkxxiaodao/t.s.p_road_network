class_name LayoutStore
extends RefCounted

## 界面布局的持久化（**会话级，不进关卡文件**）：`user://ui_layout.json`。
##
## 口径：`docs/ui/UI-R0 §2.3` —— 右侧坞宽度、各段高度比例、各段折叠状态，关掉编辑器再打开要保留。
## ★ 2026-10-27 起坞只有 **2 段**（属性 / 大纲；装饰素材搬去画布浮窗了），
##   旧文件里存着 3 个值 → 长度对不上 → 按下面的"校验不过就回默认"处理，不会崩。
## ★ 只记"外观"，不记任何 `Doc` 内容；删掉这个文件 = 回到默认布局，不会影响任何数据。
## ★ 读盘**一律做校验**：坏 JSON / 缺字段 / 越界值统统回落到默认值，绝不因为一个配置文件把编辑器搞崩。

const PATH := "user://ui_layout.json"
const VERSION := 1
const SECTIONS := 2                       ## 属性 / 大纲（装饰素材 2026-10-27 搬到画布浮窗，不在这里）

## 默认值（R0 §2.3）：坞宽 420，Inspector 62% / Outline 38%
const DEFAULT_DOCK_W := 420
const DEFAULT_RATIOS := [0.62, 0.38]
const DEFAULT_FOLDED := [false, false]


static func defaults() -> Dictionary:
	return {
		"version": VERSION,
		"dock_w": DEFAULT_DOCK_W,
		"ratios": (DEFAULT_RATIOS as Array).duplicate(),
		"folded": (DEFAULT_FOLDED as Array).duplicate(),
	}


## 读盘 + 校验。任何异常都回默认值（返回的字典一定是完整的）。
static func load_layout() -> Dictionary:
	var out := defaults()
	if not FileAccess.file_exists(PATH):
		return out
	var f := FileAccess.open(PATH, FileAccess.READ)
	if f == null:
		return out
	var raw := f.get_as_text()
	f.close()
	var parsed: Variant = JSON.parse_string(raw)
	if not (parsed is Dictionary):
		return out
	var d: Dictionary = parsed

	var w: float = float(d.get("dock_w", DEFAULT_DOCK_W))
	out["dock_w"] = clampf(w, float(UITokens.DOCK_W_MIN), float(UITokens.DOCK_W_MAX))

	var rs: Variant = d.get("ratios", [])
	if rs is Array and (rs as Array).size() == SECTIONS:
		var cleaned: Array = []
		var sum := 0.0
		for v in (rs as Array):
			var x: float = maxf(float(v), 0.01)
			cleaned.append(x)
			sum += x
		if sum > 0.0:
			for i in SECTIONS:
				cleaned[i] = float(cleaned[i]) / sum     # 归一化：手改坏了也不会把某段压成 0
			out["ratios"] = cleaned

	var fs: Variant = d.get("folded", [])
	if fs is Array and (fs as Array).size() == SECTIONS:
		var folded: Array = []
		for v in (fs as Array):
			folded.append(bool(v))
		out["folded"] = folded
	return out


static func save_layout(d: Dictionary) -> bool:
	var f := FileAccess.open(PATH, FileAccess.WRITE)
	if f == null:
		push_warning("[LayoutStore] 写不了 %s" % PATH)
		return false
	f.store_string(JSON.stringify(d, "\t"))
	f.close()
	return true


## 重置：删掉配置文件（下次启动就是默认布局），并返回默认值
static func reset() -> Dictionary:
	if FileAccess.file_exists(PATH):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(PATH))
	return defaults()


# ============================================================
# 属性面板内部小节的折叠状态（P3-1）
# ============================================================
#
# 与上面那份**分开存**：坞的折叠 = "哪一段面板"，这里 = "面板里哪个小节"。
# 合成一份会让两边互相踩（坞的 SECTIONS 是定长数组，小节的键是开放集合）。
# ★ 同样只记外观，删掉这个文件只是所有小节回到"全展开"。

const FOLDS_PATH := "user://ui_panel_folds.json"


static func load_folds() -> Dictionary:
	var out := {}
	if not FileAccess.file_exists(FOLDS_PATH):
		return out
	var f := FileAccess.open(FOLDS_PATH, FileAccess.READ)
	if f == null:
		return out
	var raw := f.get_as_text()
	f.close()
	var parsed: Variant = JSON.parse_string(raw)
	if not (parsed is Dictionary):
		return out
	for k in (parsed as Dictionary):
		out[String(k)] = bool((parsed as Dictionary)[k])    # 键一律转 String、值一律转 bool
	return out


static func save_folds(d: Dictionary) -> bool:
	var f := FileAccess.open(FOLDS_PATH, FileAccess.WRITE)
	if f == null:
		push_warning("[LayoutStore] 写不了 %s" % FOLDS_PATH)
		return false
	f.store_string(JSON.stringify(d, "\t"))
	f.close()
	return true
