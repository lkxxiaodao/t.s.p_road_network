class_name SumoTlsModel
extends RefCounted

## 专业信号方案的数据操作层（**纯函数，零 UI、零信号**）。
##
## 所有函数都直接改传进来的 `SumoProfile`，调用方负责 `begin_edit()` / `end_edit()` 与 `touch()`。
## 数据形状见 `docs/sumo-profile-schema.md` §5.3：
##
##   tls: [ { node, programs: [
##       { id, type, offset, activate_at, params:{...},
##         phases: [ { duration, name, min_dur?, max_dur?, states:{ turn_id → "G"/"g"/"y"/"r"/"o" } } ] } ] } ]
##
## ★ `states` 只存**非红**的灯位（缺省 = 红），文件小一半，导出时缺的位补 'r'。

## 点格子时的循环顺序。五态覆盖日常；`u`（红+黄）不做（技术方案06 §7.2）。
const STATE_CYCLE := ["G", "g", "y", "r", "o"]
const STATE_LABEL := {"G": "绿", "g": "让", "y": "黄", "r": "红", "o": "灭"}
const PROGRAM_TYPES := ["static", "actuated", "delay_based"]
const PROGRAM_TYPE_LABELS := ["定时（固定配时）", "感应（看车流自适应）", "延展（走完绿再切）"]

const DEFAULT_YELLOW := 3.0
const DEFAULT_ALL_RED := 2.0


# ============================================================
# 读
# ============================================================

static func entry_of(profile: SumoProfile, node_id: int) -> Dictionary:
	if profile == null:
		return {}
	for row in profile.tls:
		if int(row.get("node", -1)) == node_id:
			return row
	return {}


static func has_entry(profile: SumoProfile, node_id: int) -> bool:
	return not entry_of(profile, node_id).is_empty()


## 该节点有没有"能拿去导出"的方案
static func has_program(profile: SumoProfile, node_id: int) -> bool:
	return not programs_of(profile, node_id).is_empty()


static func programs_of(profile: SumoProfile, node_id: int) -> Array:
	var entry := entry_of(profile, node_id)
	if entry.is_empty():
		return []
	return entry.get("programs", [])


static func program_of(profile: SumoProfile, node_id: int, pi: int) -> Dictionary:
	var programs := programs_of(profile, node_id)
	if pi < 0 or pi >= programs.size():
		return {}
	return programs[pi]


static func phases_of(profile: SumoProfile, node_id: int, pi: int) -> Array:
	return program_of(profile, node_id, pi).get("phases", [])


static func state_at(profile: SumoProfile, node_id: int, pi: int, phase_i: int, turn_id: int) -> String:
	var phases := phases_of(profile, node_id, pi)
	if phase_i < 0 or phase_i >= phases.size():
		return "r"
	var states: Dictionary = phases[phase_i].get("states", {})
	return String(states.get(turn_id, "r"))


static func cycle_seconds(profile: SumoProfile, node_id: int, pi: int) -> float:
	var total := 0.0
	for phase in phases_of(profile, node_id, pi):
		total += float(phase.get("duration", 0.0))
	return total


## 有没有灯位在该方案里从头到尾没绿过（导出端会自动补，但用户应该知道）
static func never_green_turns(profile: SumoProfile, node_id: int, pi: int, link_turns: Array) -> Array:
	var green := {}
	for phase in phases_of(profile, node_id, pi):
		var states: Dictionary = phase.get("states", {})
		for k in states.keys():
			var code := String(states[k])
			if code == "G" or code == "g":
				green[int(k)] = true
	var out: Array = []
	for tid in link_turns:
		var t := int(tid)
		if t >= 0 and not green.has(t):
			out.append(t)
	return out


# ============================================================
# 写：结构
# ============================================================

## 拿到（必要时创建）该节点的 tls 记录。**默认给一套空方案**，用户点一下就有一行可编辑。
static func ensure_entry(profile: SumoProfile, node_id: int) -> Dictionary:
	var entry := entry_of(profile, node_id)
	if not entry.is_empty():
		return entry
	entry = {"node": node_id, "programs": []}
	profile.tls.append(entry)
	profile.touch()
	return entry


static func add_program(profile: SumoProfile, node_id: int) -> int:
	var entry := ensure_entry(profile, node_id)
	var programs: Array = entry["programs"]
	var used := {}
	for program in programs:
		used[String(program.get("id", "0"))] = true
	var next_id := "0"
	var n := 0
	while used.has(next_id):
		n += 1
		next_id = str(n)
	programs.append({
		"id": next_id,
		"type": "static",
		"offset": 0.0,
		"activate_at": 0.0 if programs.is_empty() else 600.0 * float(programs.size()),
		"params": {},
		"phases": [],
	})
	profile.touch()
	return programs.size() - 1


static func duplicate_program(profile: SumoProfile, node_id: int, pi: int) -> int:
	var entry := ensure_entry(profile, node_id)
	var programs: Array = entry["programs"]
	if pi < 0 or pi >= programs.size():
		return -1
	var copy: Dictionary = (programs[pi] as Dictionary).duplicate(true)
	var used := {}
	for program in programs:
		used[String(program.get("id", "0"))] = true
	var suffix := 1
	var new_id := String(copy.get("id", "0")) + " 副本"
	while used.has(new_id):
		suffix += 1
		new_id = "%s 副本%d" % [String(copy.get("id", "0")), suffix]
	copy["id"] = new_id
	programs.append(copy)
	profile.touch()
	return programs.size() - 1


static func remove_program(profile: SumoProfile, node_id: int, pi: int) -> bool:
	var entry := entry_of(profile, node_id)
	if entry.is_empty():
		return false
	var programs: Array = entry.get("programs", [])
	if pi < 0 or pi >= programs.size():
		return false
	programs.remove_at(pi)
	if programs.is_empty():
		# 一套不剩 → 整条记录清掉（否则会留下"有记录但没方案"的空壳，导出时白跑一遍）
		profile.tls.erase(entry)
	profile.touch()
	return true


static func clear(profile: SumoProfile, node_id: int) -> void:
	var entry := entry_of(profile, node_id)
	if entry.is_empty():
		return
	profile.tls.erase(entry)
	profile.touch()


# ============================================================
# 写：字段
# ============================================================

static func set_program_field(profile: SumoProfile, node_id: int, pi: int, key: String, value) -> void:
	var program := program_of(profile, node_id, pi)
	if program.is_empty():
		return
	program[key] = value
	profile.touch()


static func set_param(profile: SumoProfile, node_id: int, pi: int, key: String, value) -> void:
	var program := program_of(profile, node_id, pi)
	if program.is_empty():
		return
	var params: Dictionary = program.get("params", {})
	params[key] = value
	program["params"] = params
	profile.touch()


static func set_phase_field(profile: SumoProfile, node_id: int, pi: int, phase_i: int,
		key: String, value) -> void:
	var phases := phases_of(profile, node_id, pi)
	if phase_i < 0 or phase_i >= phases.size():
		return
	phases[phase_i][key] = value
	profile.touch()


static func set_link_state(profile: SumoProfile, node_id: int, pi: int, phase_i: int,
		turn_id: int, state: String) -> bool:
	var phases := phases_of(profile, node_id, pi)
	if phase_i < 0 or phase_i >= phases.size() or turn_id < 0:
		return false
	var states: Dictionary = phases[phase_i].get("states", {})
	if state == "r":
		states.erase(turn_id)          # 红是缺省，不存
	else:
		states[turn_id] = state
	phases[phase_i]["states"] = states
	profile.touch()
	return true


## 点格子的循环：G → g → y → r → o → G
static func next_state(current: String) -> String:
	var i := STATE_CYCLE.find(current)
	if i < 0:
		return STATE_CYCLE[0]
	return STATE_CYCLE[(i + 1) % STATE_CYCLE.size()]


## 在 after_index 之后插入一个阶段（复制上一段的灯态，方便微调）。返回新阶段下标。
static func add_phase(profile: SumoProfile, node_id: int, pi: int, after_index: int) -> int:
	var phases := phases_of(profile, node_id, pi)
	if phases.is_empty():
		var program := program_of(profile, node_id, pi)
		if program.is_empty():
			return -1
		phases.append({"duration": 20.0, "name": "阶段 1", "states": {}})
		program["phases"] = phases
		profile.touch()
		return 0
	var at := clampi(after_index + 1, 0, phases.size())
	var template: Dictionary = phases[clampi(after_index, 0, phases.size() - 1)]
	var fresh: Dictionary = template.duplicate(true)
	fresh["name"] = "阶段 %d" % (phases.size() + 1)
	phases.insert(at, fresh)
	profile.touch()
	return at


static func remove_phase(profile: SumoProfile, node_id: int, pi: int, phase_i: int) -> bool:
	var phases := phases_of(profile, node_id, pi)
	if phase_i < 0 or phase_i >= phases.size():
		return false
	phases.remove_at(phase_i)
	profile.touch()
	return true


static func move_phase(profile: SumoProfile, node_id: int, pi: int, phase_i: int, dir: int) -> int:
	var phases := phases_of(profile, node_id, pi)
	var to := phase_i + dir
	if phase_i < 0 or phase_i >= phases.size() or to < 0 or to >= phases.size():
		return phase_i
	var item: Dictionary = phases[phase_i]
	phases.remove_at(phase_i)
	phases.insert(to, item)
	profile.touch()
	return to


# ============================================================
# 从基础模式一键生成
# ============================================================

## 把 `.spmap` 的阶段性方案（protected / permitted）翻成一套专业方案。
##
## 每个阶段展开成「绿 → 黄 → 全红」三跳，和基础模式导出 `.net.xml` 时的做法完全一致
## （`SumoTlLogicPatcher` 同口径），所以用户切到专业模式看到的就是"熟悉的那套灯"，
## 只需要在此基础上微调，而不是从零编十个灯位。
##
## `link_turns`：探针给的该路口全部灯位对应的 turn id（去重后的数组）。
## 返回生成的阶段数。
static func build_from_basic(profile: SumoProfile, node_id: int, stages: Array,
		link_turns: Array, yellow: float = DEFAULT_YELLOW,
		all_red: float = DEFAULT_ALL_RED) -> int:
	if stages.is_empty():
		return 0
	var phases: Array = []
	var index := 1
	for stage in stages:
		var protected: Array = stage.get("protected", [])
		var permitted: Array = stage.get("permitted", [])
		var green := {}
		for tid in link_turns:
			var t := int(tid)
			if protected.has(t):
				green[t] = "G"
			elif permitted.has(t):
				green[t] = "g"
		phases.append({
			"duration": maxf(1.0, float(stage.get("duration", 20.0))),
			"name": "阶段 %d 绿" % index,
			"states": green.duplicate(true),
		})
		var amber := {}
		for t in green.keys():
			amber[t] = "y"
		if yellow > 0.0:
			phases.append({"duration": yellow, "name": "阶段 %d 黄" % index, "states": amber})
		if all_red > 0.0:
			phases.append({"duration": all_red, "name": "阶段 %d 全红" % index, "states": {}})
		index += 1

	# 覆盖原有方案（一键生成 = "拿基础方案的现状重来一遍"，不该叠在旧方案上）
	var entry := ensure_entry(profile, node_id)
	entry["programs"] = [{
		"id": "0",
		"type": "static",
		"offset": 0.0,
		"activate_at": 0.0,
		"params": {},
		"phases": phases,
	}]
	profile.touch()
	return phases.size()
