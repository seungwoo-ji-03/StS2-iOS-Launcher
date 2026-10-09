# Headless Godot: re-encode every extracted desktop texture (.ctex, S3TC/BPTC/lossless) as ASTC.
# Desktop formats are not GPU-decodable on iPhone; Godot would expand them to RGBA8 at load time, which
# costs memory and CPU. Full resolution is kept. Under 1024 px -> ASTC 4x4, larger -> ASTC 8x8.
#
# Run from the transcode work dir (build_pck.py `extract` creates it):
#   godot --headless --path <work> -s res://transcode.gd
# Reads res://t/<i>.ctex (count in res://count.txt), writes res://o/<i>.ctex.
# Textures that cannot be decoded, or HDR ones, are copied unchanged.
extends SceneTree

const FORMAT_VERSION := 1
const DATA_FORMAT_IMAGE := 0
const BIT_HAS_MIPMAPS := 1 << 23
const BASE_FLAGS := 0x0d000000  # DETECT_3D|NORMAL|ROUGHNESS, no stream/mips

func write_ctex(path: String, img: Image, orig_w: int, orig_h: int, flags: int) -> void:
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_8(0x47); f.store_8(0x53); f.store_8(0x54); f.store_8(0x32)  # "GST2"
	f.store_32(FORMAT_VERSION)
	f.store_32(orig_w); f.store_32(orig_h)
	f.store_32(flags)
	f.store_32(0xffffffff)
	f.store_32(0); f.store_32(0); f.store_32(0)
	f.store_32(DATA_FORMAT_IMAGE)
	f.store_16(img.get_width()); f.store_16(img.get_height())
	f.store_32(img.get_mipmap_count())
	f.store_32(img.get_format())
	f.store_buffer(img.get_data())
	f.close()

func copy_raw(src: String, dst: String) -> void:
	var f := FileAccess.open(dst, FileAccess.WRITE)
	f.store_buffer(FileAccess.get_file_as_bytes(src))
	f.close()

func _initialize():
	var n := int(FileAccess.get_file_as_string("res://count.txt").strip_edges())
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://o"))
	var ok := 0; var kept := 0
	var t0 := Time.get_ticks_msec()
	for i in range(n):
		var src := "res://t/%d.ctex" % i
		var dst := "res://o/%d.ctex" % i
		if FileAccess.file_exists(dst):  # resumable
			ok += 1; continue
		var ct := ResourceLoader.load(src) as CompressedTexture2D
		var img: Image = ct.get_image() if ct != null else null
		if img == null:
			copy_raw(src, dst); kept += 1; continue
		var fmt := img.get_format()
		if fmt == Image.FORMAT_RGBAH or fmt == Image.FORMAT_RGBAF or fmt == Image.FORMAT_RGBH or fmt == Image.FORMAT_RGBF:
			copy_raw(src, dst); kept += 1; continue
		if img.is_compressed():
			img.decompress()
		var w := img.get_width(); var h := img.get_height()
		var astc_fmt := Image.ASTC_FORMAT_8x8 if (w >= 1024 or h >= 1024) else Image.ASTC_FORMAT_4x4
		var err := img.compress(Image.COMPRESS_ASTC, Image.COMPRESS_SOURCE_SRGB, astc_fmt)
		if err != OK or not img.is_compressed():
			copy_raw(src, dst); kept += 1; continue
		var flags := BASE_FLAGS
		if img.get_mipmap_count() > 0:
			flags |= BIT_HAS_MIPMAPS
		write_ctex(dst, img, ct.get_width(), ct.get_height(), flags)
		ok += 1
		if i % 250 == 0:
			print("[%d/%d] astc=%d kept=%d  %.0fs" % [i, n, ok, kept, (Time.get_ticks_msec() - t0) / 1000.0])
	print("DONE astc=%d kept=%d total=%.0fs" % [ok, kept, (Time.get_ticks_msec() - t0) / 1000.0])
	quit()
