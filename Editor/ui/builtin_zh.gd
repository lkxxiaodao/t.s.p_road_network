extends RefCounted

## 把 **Godot 内置控件**（FileDialog / AcceptDialog / 右键菜单…）上的英文原文翻成中文。
##
## ★ 为什么不用"逐个节点改字"：内置对话框的文案是 `RTR("Path:")` 这种**运行时查表**，
##   我们在外面把 Label.text 改成中文，它下一次刷新目录/切模式就又被写回英文。
##   正解是往 `TranslationServer` 塞一份 zh_CN 的 `Translation`，再让界面锁中文 ——
##   所有 `RTR()` 自动命中，包括我们没预料到的字符串。
##
## ★ 只在编辑器进程里装一次（`EditorApp._ready()` 调 `install()`）。
##   编辑器本来就只服务中文用户，所以直接 `set_locale("zh_CN")`，不做多语言切换。

## 源串 → 中文。key 必须与 Godot 源码里的 `RTR("…")` **逐字一致**（区分大小写与标点）。
## 2026-10-28 用探针把 FileDialog 的整棵控件树打印出来核对过（见 E99「配方：让内置对话框说中文」）。
const MAP := {
	# --- FileDialog 主体 ---
	"Path:": "路径：",
	"Favorites:": "收藏夹：",
	"Recent:": "最近：",
	"Directories & Files:": "目录与文件：",
	"Filter:": "筛选：",
	"File:": "文件：",
	"Name:": "名称：",
	"Search": "搜索",
	"Create Folder": "新建文件夹",
	"Could not create folder.": "无法新建文件夹。",
	"Invalid extension, or empty filename.": "扩展名无效，或文件名为空。",
	"Open a File": "打开文件",
	"Open Files": "打开文件",
	"Open Folder": "打开文件夹",
	"Open a Directory": "打开目录",
	"Save a File": "保存文件",
	"Select This Folder": "选择这个文件夹",
	"Select Current Folder": "选择当前文件夹",
	"The path specified is invalid.": "路径无效。",
	"Invalid path.": "路径无效。",
	"Show Hidden Files": "显示隐藏文件",
	"Toggle File Filter": "切换文件筛选",
	"Toggle Favorite": "收藏 / 取消收藏",
	"Go to previous folder": "上一个文件夹",
	"Go to next folder": "下一个文件夹",
	"Go to parent folder": "上一层",
	"Refresh": "刷新",
	"All Files": "所有文件",
	"Open": "打开",
	"Save": "保存",
	# --- 对话框标题 ---
	"Please Confirm...": "请确认",
	"Alert!": "提示",
	"Message": "消息",
	# --- 通用按钮 / 对话框 ---
	"OK": "确定",
	"Cancel": "取消",
	"Close": "关闭",
	"Yes": "是",
	"No": "否",
	"Create": "新建",
	"Delete": "删除",
	"Rename": "重命名",
	"Confirm": "确定",
	"Copy": "复制",
	"Move": "移动",
	"Overwrite": "覆盖",
	"Cut": "剪切",
	"Paste": "粘贴",
	"Select All": "全选",
	"Undo": "撤销",
	"Redo": "重做",
	"Apply": "应用",
	"Add": "添加",
	"Remove": "移除",
	"Erase": "擦除",
	"Select": "选择",
	"Back": "返回",
	"Next": "下一步",
	"Previous": "上一步",
	"Finish": "完成",
}


static func install() -> void:
	var t := Translation.new()
	t.locale = "zh_CN"
	for key in MAP.keys():
		t.add_message(String(key), String(MAP[key]))
	TranslationServer.add_translation(t)
	TranslationServer.set_locale("zh_CN")
