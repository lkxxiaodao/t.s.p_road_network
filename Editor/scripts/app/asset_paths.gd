class_name AssetPaths
extends RefCounted

## 素材根目录解析（唯一口径）。
##
## 发布策略（方案 3，2026-10-28 定）：**装饰素材不进包**，安装目录自带一个可写的 `assets/deco/`，
## 用户（或我们）往里丢 png 就能用，不必重新导出程序。
##
## | 运行环境 | 扫哪些根（按优先级） | 说明 |
## | --- | --- | --- |
## | 编辑器里跑（`OS.has_feature("editor")`） | `res://assets/` | 开发时素材就在工程目录，改完立刻能看 |
## | 导出版（exe） | `<exe 同目录>/assets/` → `res://assets/` | 外部优先；`res://` 只是兜底（包里通常没有 deco） |
##
## ★ 关卡文件（`.spdeco.json`）里的 `asset` 存的是**相对 `assets/` 的路径**（如 `deco/tree.png`），
##   所以外部目录必须镜像成 `<exe 同目录>/assets/deco/tree.png` —— 两份来源共用同一份关卡数据（E06 约定）。
## ★ **不能对磁盘上的 png 用 `load()`**：Godot 的 `load()` 只认"导入过的资源"（包内 `.ctex` + remap），
##   外部图必须 `Image.load_from_file()` → `ImageTexture`（2026-10-28 用 PCKPacker 实测）。

const SUB := "assets"        ## 素材根目录名（关卡里 asset 字段相对它）
const DECO := "deco"         ## 装饰素材子目录名


## 候选根列表（按优先级，末尾不带斜杠）。发布版只收"真的存在"的外部目录。
static func roots() -> Array:
	var out: Array = []
	if not OS.has_feature("editor"):
		var ext := external_assets_dir()
		if ext != "" and DirAccess.dir_exists_absolute(ext):
			out.append(ext)
	out.append("res://" + SUB)
	return out


## 外部素材根（绝对路径，<exe 同目录>/assets）。编辑器里返回空串 —— 免得往 Godot 安装目录里建东西。
static func external_assets_dir() -> String:
	if OS.has_feature("editor"):
		return ""
	var exe := OS.get_executable_path()
	if exe == "":
		return ""
	return exe.get_base_dir().path_join(SUB)


## 外部装饰素材目录（给提示文案/日志用；编辑器里返回 res:// 那个）
static func deco_dir_for_hint() -> String:
	var ext := external_assets_dir()
	if ext != "":
		return ext.path_join(DECO)
	return "res://%s/%s" % [SUB, DECO]


## 首次运行时把 `<exe 同目录>/assets/deco/` 建出来，用户直接往里丢图即可
static func ensure_external_dir() -> void:
	var d := deco_dir_for_hint()
	if not d.begins_with("/") and not d.contains(":/"):
		return                                   # res:// 之类不动
	if DirAccess.dir_exists_absolute(d):
		return
	if DirAccess.make_dir_recursive_absolute(d) == OK:
		print("已创建装饰素材目录：%s（丢 png 进去后点素材面板的「↻」）" % d)


static func join(root: String, rel: String) -> String:
	if rel == "":
		return root
	if root.ends_with("/"):
		return root + rel
	return root + "/" + rel


## 这个相对路径在任一候选根里存在吗
static func exists(asset_rel: String) -> bool:
	if asset_rel == "":
		return false
	for r in roots():
		if FileAccess.file_exists(join(r, asset_rel)):
			return true
	return false


## 按候选根顺序取第一份贴图；都没有返回 null（调用方画红叉占位）。
## 包内走 `load()`（资源已导入）；外部走 `Image.load_from_file()`（磁盘 png，没导入过）。
static func load_texture(asset_rel: String) -> Texture2D:
	if asset_rel == "":
		return null
	for r in roots():
		var p := join(r, asset_rel)
		if r.begins_with("res://") or r.begins_with("user://"):
			var t := load(p) as Texture2D
			if t == null:
				continue
			# ★ 本环境的 CompressedTexture2D 画出来是纯白（CPU 侧数据正常）→ 统一转 ImageTexture 绕行
			var img := t.get_image()
			if img != null and not img.is_empty():
				return ImageTexture.create_from_image(img)
			return t
		if not FileAccess.file_exists(p):
			continue
		var img2 := Image.load_from_file(p)
		if img2 == null or img2.is_empty():
			continue
		img2.generate_mipmaps()                  # 外部图没有导入设置，缩小显示时靠它防摩尔纹
		return ImageTexture.create_from_image(img2)
	return null
