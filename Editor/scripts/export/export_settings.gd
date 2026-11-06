class_name ExportSettings
extends RefCounted

## 外部格式导出的用户设置（只在 user://，不写进 .spmap / 工程目录）。

const PATH := "user://export_settings.json"

static func defaults() -> Dictionary:
	return {
		"format_version": 1,
		"output_dir": "",
		"filename": "",
		"spmap": true,
		"shp": false,
		"net": false,
		"rou": false,
		"add": false,
		"geo": {"lon": null, "lat": null, "projection": 1},
		"lane_width_m": 3.5,
		"netconvert_path": "",
	}

static func load_settings() -> Dictionary:
	var out := defaults()
	if not FileAccess.file_exists(PATH):
		return out
	var f := FileAccess.open(PATH, FileAccess.READ)
	if f == null:
		push_warning("导出设置读不到：%s" % PATH)
		return out
	var raw := f.get_as_text()
	f.close()
	var parsed: Variant = JSON.parse_string(raw)
	if not (parsed is Dictionary):
		push_warning("导出设置不是 JSON 对象，已用默认值")
		return out
	var d: Dictionary = parsed
	for key in out.keys():
		if d.has(key):
			out[key] = d[key]
	if d.get("geo") is Dictionary:
		var geo: Dictionary = d["geo"]
		var merged_geo: Dictionary = out["geo"]
		for gkey in merged_geo.keys():
			if geo.has(gkey):
				merged_geo[gkey] = geo[gkey]
		out["geo"] = merged_geo
	if not (out.get("geo") is Dictionary):
		out["geo"] = defaults()["geo"]
	if not (out["lane_width_m"] is float or out["lane_width_m"] is int):
		out["lane_width_m"] = 3.5
	return out

static func save_settings(d: Dictionary) -> bool:
	var f := FileAccess.open(PATH, FileAccess.WRITE)
	if f == null:
		push_warning("导出设置写不进去：%s" % PATH)
		return false
	f.store_string(JSON.stringify(d, "\t"))
	f.close()
	return true
