class_name UITokens
extends RefCounted

## 设计令牌 —— **全工程唯一视觉真值**。
##
## 口径来源：`docs/ui/UI-R0-设计规范.md` §1。改任何颜色/圆角/间距/字号，
## **先改那份文档，再改本文件**，然后重跑 `ui/make_theme.gd` 重新生成 `ui/editor_theme.tres`。
##
## 为什么令牌是「脚本里的常量」而不是「主题文件里手填的值」：
##   1. 画布（`grid_canvas.gd` / `road_layer.gd`）不走 Theme —— 它自己 draw，必须能读到同一份色值；
##   2. 20+ 控件变体靠代码生成比手搓 .tres 可靠，且能 diff、能重跑、能回归。
##
## ⚠ 本文件**不引用任何东西**（不含 EditorApp / 不含场景），避免 GDScript 编译依赖环。

# ============================================================
# §1.1 中性（粘土基底）
# ============================================================

const SURFACE_0 := Color("#F4EFE8")   ## 窗口底色（奶油纸），画布外的"桌面"
const SURFACE_1 := Color("#FFFDF9")   ## 面板/卡片正面（近白，带一丝暖）
const SURFACE_2 := Color("#F7F2EA")   ## 面板内次级块（分组底、输入框凹陷底）
const SURFACE_3 := Color("#ECE4D9")   ## 分隔线、按下的凹槽、禁用底

const INK_1 := Color("#3D3630")       ## 主文字（暖深棕，不用纯黑）
const INK_2 := Color("#7A6E63")       ## 次级文字、字段名
const INK_3 := Color("#9E9285")       ## 提示文字、占位符、单位（对比 SUFACE_1 = 3.0，刚过 AA 弱文字线）

# ============================================================
# §1.1b 网格线（D1：偏黑四档，画在路面之下所以可以很黑）
# ============================================================

const GRID_FINE := Color("#C1B8AD")   ## 5 m 细线（只在放大时出现，故意不抢路面）
const GRID_MAJOR := Color("#9C9186")  ## 25 m 主线（"偏黑"的主体档）
const GRID_BOLD := Color("#6E655C")   ## 125 m 粗线（远景分层）
const GRID_AXIS := Color("#3D3630")   ## X/Y 轴 + 原点十字（最黑）

# ============================================================
# §1.2 品牌橙（唯一主强调：选中 / 主操作 / 焦点）
# ============================================================

const ORANGE_50 := Color("#FFF1E6")
const ORANGE_100 := Color("#FFD9BE")
const ORANGE_300 := Color("#FFB273")
const ORANGE_500 := Color("#F58F3C")  ## 主品牌橙
const ORANGE_600 := Color("#E0762A")
const ORANGE_700 := Color("#C25F1C")
const ORANGE_800 := Color("#8A4212")

# ============================================================
# §1.3 语义色（都刻意比橙更冷/更暗，避免与品牌色混淆）
# ============================================================

const RED_500 := Color("#E2564A")     ## 校验错误、冲突连接、危险按钮
const RED_50 := Color("#FDECE9")
const AMBER_500 := Color("#E8A93C")   ## 警告
const AMBER_50 := Color("#FDF4E3")   ## 警告底（色阶预留：目前无使用者）
const GREEN_500 := Color("#4FA96B")   ## 通过、吸附开
const GREEN_50 := Color("#EAF6ED")   ## 通过底（色阶预留：目前无使用者）
const BLUE_500 := Color("#4A86C8")    ## 信息/链接、直行连接曲线

# ============================================================
# §1.4 粘土阴影
# ============================================================
# 只有**外投影**是令牌（`StyleBoxFlat.shadow_*` 用的颜色）；「内高光 + 内底影」不在这里 ——
# 它们直接烘进 9-patch 图（`ui/tex/clay_*.png`，由 `ui/make_clay.gd` 生成），GDScript 侧不再保留色值。
# 悬停/按下不换阴影色（只换底色），所以也没有 HOVER / PRESS 两个档。

const SHADOW_REST := Color(120.0 / 255.0, 90.0 / 255.0, 60.0 / 255.0, 0.14)

# ============================================================
# §1.5 圆角
# ============================================================

const R_XS := 6      ## 标签、色块、勾选框
const R_SM := 10     ## 输入框、小按钮、列表项
const R_MD := 14     ## 常规按钮、下拉、Tab
const R_LG := 20     ## 面板、卡片、弹出层
const R_XL := 28     ## 主面板外框、模态框
const R_PILL := 999  ## 胶囊（状态、图层开关、图标按钮）

# ============================================================
# §1.6 间距（8 基准）
# ============================================================

const S1 := 4
const S2 := 8
const S3 := 12
const S4 := 16
const S5 := 20

# ============================================================
# §1.7 字号与字重（只用 400 / 500）
# ============================================================

## ★ 2026-10-27 整体放大 2~3 px：原来 11~20 在 1080p 上偏小、高 DPI/大屏更难读。
##   放大后框架高度（§2.2）与各栏内边距必须同步长，否则会"字顶出栏"（状态栏/提示条就是这样被裁的）。
const T_DISPLAY := 23   ## 面板大标题
const T_TITLE := 18     ## 区块标题
const T_BODY := 16      ## 正文、按钮文字
const T_LABEL := 15     ## 字段名、Tab、状态栏
const T_CAP := 13       ## 单位、快捷键提示

# 行高与字重**不设令牌**：字重由两份字体文件承载（Regular / Medium），行高由字体度量决定。

const FONT_REGULAR := "res://assets/fonts/SourceHanSansCN-Regular.otf"
const FONT_MEDIUM := "res://assets/fonts/SourceHanSansCN-Medium.otf"

# ============================================================
# §5.1 画布内容（路面 / 车道 / 连接 / 交互反馈）
# ============================================================
# ⚠ 这一组是**给 `road_layer.gd` / `deco_layer.gd` 自己 draw 用的**，不走 Theme。
#   画布上的东西一律"实心平涂 + 细描边"（R0 §0.1 第二条），**不叠投影**——
#   5000 条连接时任何阴影都会糊成一团。

# --- 路面（按车道类型）---
const ROAD_DRIVING := Color("#B9B3AA")     ## 机动车道：暖灰（对画布 1.82:1）
const ROAD_BUS := Color("#9E96C4")         ## 公交专用道：灰紫
const ROAD_BIKING := Color("#8FBF8E")      ## 非机动车道：抹茶绿
const ROAD_SIDEWALK := Color("#CFC8BB")    ## 人行道：暖米（对画布 1.45:1，靠描边区分）
const ROAD_LEFT_TURN := Color("#B08A63")   ## 左转专用道：赭
const ROAD_EDGE := Color("#A79F94")        ## 车道描边：比路面深一档
const ROAD_JUNCTION := Color("#AFA89D")    ## 路口补面：比车道再深一档（一眼看出是路口）

# --- 安全岛 ---
const ROAD_ISLAND := Color("#E8E2D6")      ## 岛面：浅于路面一档
const ROAD_ISLAND_EDGE := Color("#FFFFFF") ## 岛边缘白实线

# --- 连接曲线（按转向类型）---
# ⚠ 掉头**不能用红**：红是"冲突"专用（RED_500），两者同时出现时必须能分开。
const TURN_STRAIGHT := Color("#4A86C8")    ## 直行：蓝
const TURN_LEFT := Color("#C25F1C")        ## 左转：深橙（琥珀 #E8A93C 在暖灰路面上只有 1.01，会消失）
const TURN_RIGHT := Color("#3F8A55")       ## 右转：深绿（浅绿 #4FA96B 只有 1.40，偏弱）
const TURN_UTURN := Color("#8E6BB5")       ## 掉头：紫（UI 里别处不用紫，区分度最高）
const TURN_CROSSWALK := Color("#8C97A8")   ## 人行横道：灰蓝（比机动车转向弱一档）

# --- 交互反馈 ---
const SEL_RING := Color("#F58F3C")         ## 选中描边 = ORANGE_500
const SEL_GLOW := Color("#FFF1E6")         ## 选中光晕 = ORANGE_50（**画在描边下面**才有"晕"）
const NODE_DOT := Color("#FFFDF9")         ## 节点白心（奶油底上靠描边活命）
const NODE_EDGE := Color("#7A6E63")        ## 节点描边
const SNAP_OK := Color("#4FA96B")          ## 吸附命中：绿环
const SNAP_FREE := Color("#E0762A")        ## 未吸附：橙点
const CONFLICT := Color("#E2564A")         ## 冲突 = RED_500（与语义色统一）


# ============================================================
# 描边 / 焦点
# ============================================================

const BORDER_W := 1     ## 常规描边
const FOCUS_W := 2      ## 焦点环（橙 2px + 外扩 2px 浅橙）
const FOCUS_EXPAND := 2

# ============================================================
# §2.2 主框架尺寸（P2 用；先集中在这里，别散在 tscn 里）
# ============================================================

## ★ 高度是从字号推出来的：正文 16 / 标签 15 / 提示 13。经验式 = 字号 × 1.3 + 上下内边距。
##   改字号就要回来看这一组，否则字会顶出栏（16 号正文至少要给 42 才不算挤）。
const TOP_BAR_H := 76          ## 顶栏：正文按钮 16 + 上下各 12 padding
const TOOL_RAIL_W := 68        ## 左栏：图标按钮 52 + 两侧各 8
const ICON_BTN := 52           ## 工具条图标按钮边长
const CONTEXT_BAR_H := 52      ## 上下文条：标签 15 + 上下各 12
const DOCK_W_DEFAULT := 420    ## 坞宽默认（字号 16 时 380 装不下字段名+预设按钮，会被裁右边缘）
const DOCK_W_MIN := 380        ## ★ P3-2 起 380（P3-1 是 360）：配时区的勾选表 + 需求曲线的时段行
const DOCK_W_MAX := 560        ##   最小宽 346 / 372，加 16 内边距 + 12 竖滚动条后 360 会裁掉一截
const DOCK_HEAD_H := 44        ## 坞段头（可点折叠那一行）
const LAYER_RAIL_W := 58       ## 图层胶囊列宽（两个汉字 @13 + 左右 padding）
const STATUS_BAR_H := 42       ## 状态栏：胶囊里 15 号字 + 上下各 6
const HINT_BAR_H := 32         ## 键位提示条：13 号字 + 上下各 6
const BAR_GAP := 4             ## 相邻两条栏之间的缝

## 属性面板内的网格（P3-1）：字段名一列、值一列，**列宽固定**，不随内容跳（E91 C17 的根因就是它）
## ★ 96 px 是字号 12~13 时代的数；现字号 15，一个汉字约 15 px → 96 只装 6.4 字，
##   「独立非机动车道」会被截成「独立非机…」。112 = 7 字 + 一点余量。
const FIELD_W := 112           ## 字段名列宽
const SECTION_HEAD_H := 36     ## 面板内小节头（可点折叠那一行）


## 数字/坐标专用字体：等宽数字变体（`tnum`），否则状态栏坐标跳动时会左右抖。
## R0 §1.7 的硬要求，别用普通字体凑。
static func tnum_font() -> FontVariation:
	var fv := FontVariation.new()
	fv.base_font = load(FONT_REGULAR)
	var feats: Dictionary = {}
	feats["tnum"] = 1
	fv.opentype_features = feats
	return fv


## 十六进制字符串（不带 #），给日志/调试用。
static func hex(c: Color) -> String:
	return "#" + c.to_html(false)
