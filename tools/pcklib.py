"""Godot GDPC v3 pack reader/writer and ECFG (project.binary) codec, shared by the pck tools."""

import hashlib
import struct

CHUNK = 8 * 1024 * 1024


class Pack:
    """Read-only view of a .pck: header fields plus a directory of entries."""

    def __init__(self, path):
        self.path = path
        self.f = open(path, "rb")  # noqa: SIM115
        f = self.f
        if f.read(4) != b"GDPC":
            raise ValueError(f"{path}: not a Godot pack")
        self.ver = struct.unpack("<I", f.read(4))[0]
        if self.ver < 3:
            raise ValueError(f"{path}: pack format {self.ver} unsupported (need 3)")
        self.godot_ver = struct.unpack("<III", f.read(12))
        self.flags = struct.unpack("<I", f.read(4))[0]
        self.file_base = struct.unpack("<Q", f.read(8))[0]
        dir_off = struct.unpack("<Q", f.read(8))[0]
        f.seek(dir_off)
        n = struct.unpack("<I", f.read(4))[0]
        self.entries = []  # dicts: path, ofs (absolute), size, md5, flags
        for _ in range(n):
            sl = struct.unpack("<I", f.read(4))[0]
            path = f.read(sl).rstrip(b"\0").decode("utf-8")
            ofs, size = struct.unpack("<QQ", f.read(16))
            md5 = f.read(16)
            fl = struct.unpack("<I", f.read(4))[0]
            self.entries.append(
                {
                    "path": path,
                    "ofs": self.file_base + ofs,
                    "size": size,
                    "md5": md5,
                    "flags": fl,
                }
            )
        self.by_path = {e["path"]: e for e in self.entries}

    def read(self, path):
        e = self.by_path[path]
        self.f.seek(e["ofs"])
        return self.f.read(e["size"])

    def copy_to(self, e, out):
        self.f.seek(e["ofs"])
        left = e["size"]
        while left:
            b = self.f.read(min(CHUNK, left))
            out.write(b)
            left -= len(b)

    def close(self):
        self.f.close()


def write_pack(src, out_path, replace):
    """Rewrite `src` to `out_path`. `replace(entry) -> bytes | None` supplies new content for an entry
    (None keeps the original bytes). Returns the list of replaced paths."""
    replaced = []
    with open(out_path, "wb") as out:
        out.write(b"GDPC")
        out.write(struct.pack("<I", src.ver))
        out.write(struct.pack("<III", *src.godot_ver))
        out.write(struct.pack("<I", src.flags))
        out.write(struct.pack("<Q", src.file_base))
        dir_pos = out.tell()
        out.write(struct.pack("<Q", 0))
        out.write(b"\0" * (src.file_base - out.tell()))
        new = []
        for e in src.entries:
            ofs = out.tell() - src.file_base
            data = replace(e)
            if data is None:
                src.copy_to(e, out)
                new.append((e["path"], ofs, e["size"], e["md5"], e["flags"]))
            else:
                out.write(data)
                new.append(
                    (e["path"], ofs, len(data), hashlib.md5(data).digest(), e["flags"])
                )
                replaced.append(e["path"])
            pad = (16 - out.tell() % 16) % 16
            out.write(b"\0" * pad)
        dir_off = out.tell()
        out.write(struct.pack("<I", len(new)))
        for path, ofs, size, md5, fl in new:
            enc = path.encode("utf-8")
            enc += b"\0" * (((len(enc) + 3) & ~3) - len(enc))
            out.write(
                struct.pack("<I", len(enc))
                + enc
                + struct.pack("<QQ", ofs, size)
                + md5
                + struct.pack("<I", fl)
            )
        out.seek(dir_pos)
        out.write(struct.pack("<Q", dir_off))
    return replaced


# ECFG (project.binary) layout:
#   b'ECFG' + u32 count + count * (u32 keylen + key + u32 vlen + variant[vlen])
# Variant encoding (Godot marshalls): u32 type, then payload. Supported here: bool(1), int(2, 32-bit),
# float(3, 32-bit), string(4: u32 len + utf8 + pad to 4). Anything else is kept as raw bytes.
T_BOOL, T_INT, T_FLOAT, T_STRING = 1, 2, 3, 4


def ecfg_decode(data):
    if data[:4] != b"ECFG":
        raise ValueError("not an ECFG blob")
    n = struct.unpack_from("<I", data, 4)[0]
    p = 8
    items = []  # (key, raw_variant_bytes)
    for _ in range(n):
        kl = struct.unpack_from("<I", data, p)[0]
        p += 4
        key = data[p : p + kl].decode("utf-8")
        p += kl
        vl = struct.unpack_from("<I", data, p)[0]
        p += 4
        items.append((key, data[p : p + vl]))
        p += vl
    return items


def ecfg_encode(items):
    out = bytearray(b"ECFG" + struct.pack("<I", len(items)))
    for key, raw in items:
        k = key.encode("utf-8")
        out += struct.pack("<I", len(k)) + k + struct.pack("<I", len(raw)) + raw
    return bytes(out)


def variant(value):
    if isinstance(value, bool):
        return struct.pack("<II", T_BOOL, int(value))
    if isinstance(value, int):
        return struct.pack("<Ii", T_INT, value)
    if isinstance(value, float):
        return struct.pack("<If", T_FLOAT, value)
    if isinstance(value, str):
        s = value.encode("utf-8")
        return struct.pack("<II", T_STRING, len(s)) + s + b"\0" * ((4 - len(s) % 4) % 4)
    raise TypeError(f"unsupported ECFG value {value!r}")


def variant_repr(raw):
    t = struct.unpack_from("<I", raw)[0] & 0xFFFF
    if t == T_BOOL:
        return bool(struct.unpack_from("<I", raw, 4)[0])
    if t == T_INT and len(raw) == 8:
        return struct.unpack_from("<i", raw, 4)[0]
    if t == T_FLOAT and len(raw) == 8:
        return struct.unpack_from("<f", raw, 4)[0]
    if t == T_STRING:
        n = struct.unpack_from("<I", raw, 4)[0]
        return raw[8 : 8 + n].decode("utf-8", "replace")
    return f"<type {t}, {len(raw)} bytes>"


def ecfg_set(data, overrides):
    """Return ECFG bytes with `overrides` (key -> python value) replacing or appending entries."""
    items = ecfg_decode(data)
    index = {k: i for i, (k, _) in enumerate(items)}
    for key, value in overrides.items():
        raw = variant(value)
        if key in index:
            items[index[key]] = (key, raw)
        else:
            index[key] = len(items)
            items.append((key, raw))
    return ecfg_encode(items)
