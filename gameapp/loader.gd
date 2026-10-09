extends Node

func _ready() -> void:
	var exe_dir := OS.get_executable_path().get_base_dir()
	var pck_path := exe_dir.path_join("sts2game.pck")
	print("[loader] pck: ", pck_path)
	_register_uids_from_pck(pck_path)
	var ok := ProjectSettings.load_resource_pack(pck_path, false)
	print("[loader] pck loaded: ", ok)
	if not ok:
		push_error("[loader] load_resource_pack failed"); return
	get_tree().change_scene_to_file("res://scenes/game.tscn")

func _register_uids_from_pck(pck_path: String) -> void:
	var f := FileAccess.open(pck_path, FileAccess.READ)
	if f == null:
		push_error("[loader] cannot open pck"); return
	f.big_endian = false
	if f.get_32() != 0x43504447:
		push_error("[loader] bad magic"); return
	var version := f.get_32()
	var vmaj := f.get_32(); var vmin := f.get_32(); var vpat := f.get_32()
	var pack_flags := f.get_32()
	var file_base := f.get_64()
	if version >= 3:
		var dir_offset := f.get_64()
		f.seek(dir_offset)
	else:
		for i in 16: f.get_32()
	var count := f.get_32()
	print("[loader] pck v%d %d.%d.%d file_base=%d files=%d" % [version, vmaj, vmin, vpat, file_base, count])
	var uid_off := 0; var uid_size := 0
	for i in count:
		var sl := f.get_32()
		var path := f.get_buffer(sl).get_string_from_utf8()
		var ofs := f.get_64()
		var sz := f.get_64()
		f.get_buffer(16)  # md5
		f.get_32()        # flags
		if path.ends_with(".godot/uid_cache.bin"):
			uid_off = file_base + ofs; uid_size = sz
	if uid_size == 0:
		push_warning("[loader] uid_cache.bin not found (files=%d)" % count); f.close(); return
	f.seek(uid_off)
	var ucount := f.get_32()
	var added := 0
	for i in ucount:
		var id := f.get_64()
		var llen := f.get_32()
		var lp := f.get_buffer(llen).get_string_from_utf8()
		if id != -1 and not lp.is_empty():
			if ResourceUID.has_id(id): ResourceUID.set_id(id, lp)
			else: ResourceUID.add_id(id, lp)
			added += 1
	f.close()
	print("[loader] registered %d/%d UIDs" % [added, ucount])
