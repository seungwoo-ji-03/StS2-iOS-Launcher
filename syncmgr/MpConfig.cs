// Runtime multiplayer config: lets the iPhone read the bridge address / netId from a JSON file in the
// app's user container instead of having them baked into the AOT build. Edit the file in the iOS Files
// app to change networks (LAN IP, Tailscale IP, public IP) without rebuilding.
//
// File: user://mp_config.json  (globalized to <container>/Documents/mp_config.json on iOS)
//   { "ip": "100.x.y.z", "port": 33771, "netId": "76561198XXXXXXXXX" }
//
// cecilpatch --mp-config rewrites NJoinFriendScreen.FastMpJoin so the hardcoded IP and clientId become
// MpConfig.Ip(<baked>) / MpConfig.NetId(<baked>). The baked value (from --mp-join / --mp-netid) is
// passed as the fallback, so a missing or invalid file field keeps the compiled-in default.
using System;
using System.IO;
using System.Text.Json;
using Godot;

public static class MpConfig
{
    static bool _loaded;
    static bool _haveIp, _haveNetId, _havePort;
    static string _ip;
    static ulong _netId;
    static ushort _port;

    static string Path => ProjectSettings.GlobalizePath("user://mp_config.json");

    static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(Path)) { GD.Print($"[mpcfg] no {Path}; using baked defaults"); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            var r = doc.RootElement;
            if (r.TryGetProperty("ip", out var ipEl) && ipEl.ValueKind == JsonValueKind.String)
            {
                var v = ipEl.GetString();
                if (!string.IsNullOrWhiteSpace(v)) { _ip = v.Trim(); _haveIp = true; }
            }
            if (r.TryGetProperty("port", out var portEl) && portEl.TryGetUInt16(out var p) && p != 0) { _port = p; _havePort = true; }
            if (r.TryGetProperty("netId", out var nEl))
            {
                string ns = nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : nEl.ToString();
                if (ulong.TryParse(ns, out var n) && n != 0) { _netId = n; _haveNetId = true; }
            }
            GD.Print($"[mpcfg] loaded ip={(_haveIp ? _ip : "(default)")} port={(_havePort ? _port.ToString() : "(default)")} netId={(_haveNetId ? _netId.ToString() : "(default)")}");
        }
        catch (Exception e) { GD.Print($"[mpcfg] parse failed ({e.Message}); using baked defaults"); }
    }

    // Each returns the file value when present, else the baked fallback the call site was compiled with.
    public static string Ip(string fallback) { Load(); return _haveIp ? _ip : fallback; }
    public static ulong NetId(ulong fallback) { Load(); return _haveNetId ? _netId : fallback; }
    public static ushort Port(ushort fallback) { Load(); return _havePort ? _port : fallback; }
}
