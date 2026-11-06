class_name Edition
extends RefCounted

## 「基础 / 专业」两档能力级别（技术方案06 §6.1）。
##
## ★ 与 `EditorMode.Kind`（ROAD / CONNECT / TRAFFIC / DECO）**不是一回事**：
##   `EditorMode` 是"手上拿的什么工具"，`Edition` 是"界面上露出多少东西"。
##   中文都叫"模式"会混，所以代码里一律用 `edition`。
##
## ★ 铁律：切换**只改界面可见性**，不改数据、不改导出。
##   导出用哪套配时由"有没有专业档"决定，不由当前开的是哪一档决定 ——
##   否则用户切回基础模式再导出，专业配时就会被悄悄丢掉。

enum Level { BASIC, PRO }

const PATH := "user://edition.json"       ## 会话级偏好（不进关卡文件）


static func label(level: int) -> String:
	return "专业" if level == Level.PRO else "基础"


## 读盘；坏文件/越界值一律回 BASIC（绝不因为一个偏好文件把编辑器搞崩）
static func load_level() -> int:
	if not FileAccess.file_exists(PATH):
		return Level.BASIC
	var f := FileAccess.open(PATH, FileAccess.READ)
	if f == null:
		return Level.BASIC
	var raw := f.get_as_text()
	f.close()
	var parsed = JSON.parse_string(raw)
	if not (parsed is Dictionary):
		return Level.BASIC
	var d: Dictionary = parsed
	var v := int(d.get("edition", Level.BASIC))
	if v != Level.BASIC and v != Level.PRO:
		return Level.BASIC
	return v


static func save_level(level: int) -> void:
	var f := FileAccess.open(PATH, FileAccess.WRITE)
	if f == null:
		push_warning("能力级偏好写不进去：%s" % PATH)
		return
	f.store_string(JSON.stringify({"version": 1, "edition": level}, "\t"))
	f.close()
