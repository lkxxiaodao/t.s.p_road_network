class_name SumoRunModel
extends RefCounted

## SUMO 运行配置（`.sumo.json` 的 `config` 段）的纯数据层（S5）。
##
## **零信号、零 UI 依赖**，与 `SumoNetModel` / `SumoFacilitiesModel` 同口径：
## 面板只调这里，这里只读写 `SumoProfile.config`。
##
## ## 五个字段（对应 `.sumocfg` 的五行）
## | 键 | 落点 | 说明 |
## | --- | --- | --- |
## | `begin` | `<time><begin>` | 开始时刻（秒） |
## | `end` | `<time><end>` | 结束时刻（秒）—— 试跑就跑这么久 |
## | `step_length` | `<time><step-length>` | 仿真步长（秒） |
## | `seed` | `<random_number><seed>` | 随机种子（同种子同结果） |
## | `teleport` | `<processing><time-to-teleport>` | 堵多久就把车传送走（秒） |
##
## ## ★ 与默认值相同 = **删键**
## 文件里只留"跟默认不一样"的那些（与 `SumoNetModel.set_netconvert_option` 同口径）。
## 五个都是默认值时 `config` 整段不写 —— 但 `SumoProfile.defaults()` 会补回默认值，
## 所以读的时候永远拿得到数，不会出现"没配就是 0 秒"。
##
## ## ★ SpinBox 的 min 必须能被 step 整除（E10 §8-10）
## 面板按 `RANGES` 建控件；这里的下限都刻意选成 step 的整数倍
## （比如 teleport 用 `0 + 10k`，不用 `-1 + 10k`，否则用户输 300 会被吸到 299）。

const KEYS := ["begin", "end", "step_length", "seed", "teleport"]

## 缺省值。★ 直接引用 `SumoProfile.DEFAULT_CONFIG` —— 只此一份，别在面板里再抄一遍
## （抄两份的下场是"面板显示 3600、导出的 sumocfg 里却是别的数"）。
const DEFAULTS := SumoProfile.DEFAULT_CONFIG

## [min, max, step]。`min % step == 0` 是硬要求。
const RANGES := {
	"begin": [0.0, 86400.0, 10.0],
	"end": [10.0, 86400.0, 10.0],
	"step_length": [0.1, 10.0, 0.1],
	"seed": [0.0, 2147483647.0, 1.0],
	"teleport": [0.0, 3600.0, 10.0],
}

const LABELS := {
	"begin": "开始时刻",
	"end": "结束时刻",
	"step_length": "仿真步长",
	"seed": "随机种子",
	"teleport": "传送阈值",
}

const UNITS := {
	"begin": "s",
	"end": "s",
	"step_length": "s",
	"seed": "",
	"teleport": "s",
}

const TIPS := {
	"begin": "从第几秒开始仿真。一般是 0。",
	"end": "跑到第几秒。点「试跑」就按这个时长跑。",
	"step_length": "每步多少秒。默认 1；调小更精细但更慢。",
	"seed": "随机种子。同一个种子 + 同一份文件 = 同一次结果，方便对比。",
	"teleport": "车堵在路口超过这么多秒会被 SUMO 传送走（避免死锁卡住仿真）。0 = 不等待。",
}


# ============================================================
# 读
# ============================================================

## 当前值（没配过 → 默认值）
static func value(profile: SumoProfile, key: String) -> float:
	if profile != null and profile.config.has(key):
		return float(profile.config[key])
	return float(DEFAULTS.get(key, 0.0))


static func is_default(profile: SumoProfile, key: String) -> bool:
	return is_equal_approx(value(profile, key), float(DEFAULTS.get(key, 0.0)))


## 有几项不是默认值（面板显示"已改 N 项"）
static func changed_count(profile: SumoProfile) -> int:
	var n := 0
	for key in KEYS:
		if not is_default(profile, String(key)):
			n += 1
	return n


## 给"试跑结果"标题用的一句话：`0 ~ 3600 s，步长 1 s，种子 42`
static func describe(profile: SumoProfile) -> String:
	return "%.0f ~ %.0f s，步长 %s s，种子 %d" % [
		value(profile, "begin"), value(profile, "end"),
		_num(value(profile, "step_length")), int(value(profile, "seed")),
	]


# ============================================================
# 写
# ============================================================

## 改一个字段。夹进 `RANGES`；与默认值相同就**删键**。返回是否真的变了。
static func set_value(profile: SumoProfile, key: String, raw: float) -> bool:
	if profile == null or not KEYS.has(key):
		return false
	var range_v: Array = RANGES.get(key, [0.0, 1.0, 1.0])
	var v: float = clampf(raw, float(range_v[0]), float(range_v[1]))
	# 吸附到 step 网格（与 SpinBox 行为一致，免得文件里出现 3599.9999）
	var step: float = float(range_v[2])
	if step > 0.0:
		v = float(range_v[0]) + roundf((v - float(range_v[0])) / step) * step

	if key == "seed":
		v = floorf(v)

	if is_equal_approx(v, value(profile, key)):
		return false

	if is_equal_approx(v, float(DEFAULTS.get(key, 0.0))):
		profile.config.erase(key)         # 回到默认 = 不留这个键
	else:
		# ★ seed 存 **int**：GDScript 的 float 会被 `JSON.stringify` 写成 `7.0`，
		#   而 C# 侧 `System.Text.Json` 的整数类型接不住 7.0（严格模式，抛 JsonException）
		#   → 整个 config 被丢弃、sumocfg 悄悄回到默认值。S5 踩过一次。
		profile.config[key] = int(round(v)) if key == "seed" else v
	return true


## 一键恢复默认（清掉 config 里这五个键）
static func reset(profile: SumoProfile) -> bool:
	if profile == null:
		return false
	var changed := false
	for key in KEYS:
		if profile.config.has(key):
			profile.config.erase(key)
			changed = true
	return changed


## 把 end 自动抬到 begin 之后（面板上改 begin 时调）。
## 返回是否动了 end。**不做静默夹取** —— 这是用户能看见的联动。
static func keep_span_valid(profile: SumoProfile) -> bool:
	if profile == null:
		return false
	var begin := value(profile, "begin")
	var end := value(profile, "end")
	if end > begin:
		return false
	return set_value(profile, "end", begin + 10.0)


# ============================================================
# 内部
# ============================================================

static func _num(v: float) -> String:
	return "%.1f" % v if not is_equal_approx(v, roundf(v)) else "%.0f" % v
