class_name ArrowAssets
extends RefCounted

## 导向箭头贴图的唯一入口 —— **转向组合 → 贴图** 的映射与尺寸表。
##
## 口径：`docs/ui/UI-R0 §8.3` / `UI-R2 §2.4`。
##
## ★ 2026-10-26 改口径：**一条车道只画一个箭头**，形状由这条车道"有哪些转向"决定。
##   旧做法是"有几种转向就前后排几支"（直+左+右 = 三支首尾相接），
##   现在素材里已经有组合图（直行左转 / 直行右转 / 左右转 / 直左右），
##   所以直接把组合折成一个键去查表，路面更接近真实导向箭头。
##
## 组合键字母**顺序固定 S→L→R→U**（不是按用户点选顺序，保证同一组合永远同一个键）：
##   S = Straight 直行、L = Left 左转、R = Right 右转、U = UTurn 掉头
##   例：直行 + 右转 → "SR"；直行 + 左转 + 右转 → "SLR"
##
## 尺寸不是拍脑袋定的：按"墨水等效"给的基准高度（屏幕像素，zoom≈1），
## 再由**实测长宽比**反算宽度；组合箭头天然更宽，超出车道时按宽封顶（见 `fit()`）。
##
## ⚠ 素材必须由 Godot 导入且**开 mipmaps**（800~900 px 缩到 30~50 px，不开必有锯齿）。

const DIR := "res://assets/arrows"
const BAKE_H := 128            ## 烘焙高度：目标 30~50 px，留 3 倍余量
const MAX_W_MUL := 0.90        ## 单支箭头最大宽度 = 车道宽 × 该系数（留两侧呼吸）

## 组合键 → {file, rot180, len}
##   len    = 100% 缩放下的**屏幕像素高度**（不是画布像素，调用方乘 k）
##   rot180 = 素材朝向与"尖朝上"约定**相反**时才开（烘焙时旋 180°）。
##            2026-10-26 实测：10 张素材全部原生"尖朝上"，所以**全部是 false**；
##            掉头那张原本按旧口径开了 180°，老板实机验证发现指向反了 → 已改回 false。
##   宽高比**不写死**：`tex()` 烘焙时从实际像素量出来，素材换了不用改表
const SPEC := {
	"S": {"file": "arrow_straight.png", "rot180": false, "len": 40.0},
	"L": {"file": "arrow_left.png", "rot180": false, "len": 35.0},
	"R": {"file": "arrow_right.png", "rot180": false, "len": 35.0},
	"U": {"file": "arrow_uturn.png", "rot180": false, "len": 28.0},
	"SL": {"file": "arrow_straight_left.png", "rot180": false, "len": 36.0},
	"SR": {"file": "arrow_straight_right.png", "rot180": false, "len": 36.0},
	"LR": {"file": "arrow_left_right.png", "rot180": false, "len": 32.0},
	"SLR": {"file": "arrow_straight_left_right.png", "rot180": false, "len": 34.0},
	"LU": {"file": "arrow_left_uturn.png", "rot180": false, "len": 30.0},
	"SU": {"file": "arrow_straight_uturn.png", "rot180": false, "len": 30.0},
}

## Doc 的 turn 类型 → 组合键字母
const LETTER := {"Straight": "S", "Left": "L", "Right": "R", "UTurn": "U"}
## 拼键顺序（固定，别按传入顺序）
const LETTER_ORDER := ["S", "L", "R", "U"]

static var _tex_cache: Dictionary = {}
static var _aspect_cache: Dictionary = {}


## turn 类型集合 → 组合键。
## 接受 Array（元素是 "Straight"/"Left"...），重复/未知类型自动忽略。
static func key_for(types: Array) -> String:
	var key := ""
	for letter in LETTER_ORDER:
		for ty in types:
			if String(LETTER.get(String(ty), "")) == letter:
				key += letter
				break
	if key == "":
		return ""
	return _degrade(key)


## 素材没覆盖的组合（目前只有"四种转向同时存在"这种极端情况）逐级降级：
## 先丢掉头 U、再丢 R、再丢 L —— 保住"直行"这条最主干的语义，别画错方向。
static func _degrade(key: String) -> String:
	if SPEC.has(key):
		return key
	for drop in ["U", "R", "L"]:
		var cand := key.replace(drop, "")
		if cand != "" and SPEC.has(cand):
			return cand
	return "S"


## 取烘焙好的贴图（首次调用时烘焙，之后走缓存）。素材缺失返回 null —— 调用方要降级。
static func tex(key: String) -> Texture2D:
	if _tex_cache.has(key):
		return _tex_cache[key]
	var spec: Dictionary = SPEC.get(key, {})
	if spec.is_empty():
		return null
	var path: String = "%s/%s" % [DIR, String(spec["file"])]
	var src: Texture2D = load(path)
	if src == null:
		push_warning("[ArrowAssets] 素材缺失：%s" % path)
		return null
	var img: Image = src.get_image()
	if img == null:
		push_warning("[ArrowAssets] 取不到像素：%s" % path)
		return null
	img = img.duplicate()
	img.convert(Image.FORMAT_RGBA8)
	# rot180：只有素材朝向与"尖朝上"约定相反时才开（当前全表 false）。
	# 统一成尖朝上之后，运行期绘制就只有"按 fwd 旋转"一条路径，没有任何分支。
	if bool(spec["rot180"]):
		img.rotate_180()
	var ratio: float = float(img.get_width()) / float(maxi(img.get_height(), 1))
	var w: int = maxi(1, int(round(float(BAKE_H) * ratio)))
	img.resize(w, BAKE_H, Image.INTERPOLATE_LANCZOS)
	var out: ImageTexture = ImageTexture.create_from_image(img)
	_tex_cache[key] = out
	_aspect_cache[key] = ratio
	return out


## 素材的宽高比（宽 / 高）。素材缺失时退回旧矢量的 0.375，保证不崩。
static func aspect_of(key: String) -> float:
	if not _aspect_cache.has(key):
		tex(key)
	return float(_aspect_cache.get(key, 0.375))


## 100% 缩放下的屏幕像素高度
static func len_px(key: String) -> float:
	var spec: Dictionary = SPEC.get(key, {})
	return float(spec.get("len", 32.0))


## 算出这支箭头实际该画多大 → Vector2(高, 宽)，单位是**画布像素**（已乘 k）。
## `avail_w` = 车道内可用宽度（画布像素），传 0 或负数表示不限制。
## ★ 组合箭头是"更宽"而不是"更长"：宽度超了就地封顶、高度跟着缩，
##   这样它永远不会跨进相邻车道（真实路面上的组合箭头也是扁宽的）。
static func fit(key: String, k: float, avail_w: float) -> Vector2:
	var a := maxf(aspect_of(key), 0.01)
	var h := len_px(key) * k
	var w := h * a
	if avail_w > 1.0 and w > avail_w:
		w = avail_w
		h = w / a
	return Vector2(h, w)
