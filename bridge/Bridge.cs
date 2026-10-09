// Multiplayer bridge that runs inside the desktop game, where Steam is initialized and the game's
// singletons are live.
//
//   friend (Steam lobby) <-> [SteamClient | ENetHost] bridge <-> iPhone (ENet, fastmp)
//
// It reuses the game's transport classes and forwards packets unchanged in both directions; the
// transport is a byte pipe keyed by netId, so nothing is parsed. The iPhone proposes the bridge
// owner's SteamID as its netId (cecilpatch --mp-netid), which is also the id the Steam host assigns
// to this bridge, so player references match end to end.
//
// Enabled by the --bridge launch argument. It joins the first Steam friend found hosting an StS2
// lobby; STS2_FRIEND_STEAMID or STS2_FRIEND_NAME narrow the choice.
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Steamworks;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using GameSteamClient = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient;

public partial class Bridge : Node
{
    const ushort ENetPort = 33771;
    const uint Sts2AppId = 2868840;

    ENetHost _enetHost;           // the iPhone connects here
    GameSteamClient _steamClient; // joins the friend's Steam lobby
    ulong _iphonePeerId;
    ulong _friendSteamId;         // STS2_FRIEND_STEAMID, optional
    string _friendNameHint;       // STS2_FRIEND_NAME, optional
    bool _joinStarted;
    double _discoverAccum;

    static bool _booted;

    // Injected by cecilpatch at the start of NGame.InitializePlatform. The scene tree root may not be
    // ready that early, so the node is added on the first ProcessFrame instead.
    public static void Boot()
    {
        if (_booted) return;
        _booted = true;
        bool on = false;
        foreach (var a in OS.GetCmdlineArgs()) if (a == "--bridge") on = true;
        if (!on) { GD.Print("[bridge] Boot: --bridge not present, skipping"); return; }
        if (Engine.GetMainLoop() is not SceneTree tree) { GD.Print($"[bridge] Boot: main loop is not a SceneTree ({Engine.GetMainLoop()})"); return; }
        Callable cb = default;
        cb = Callable.From(() =>
        {
            tree.Disconnect(SceneTree.SignalName.ProcessFrame, cb);
            tree.Root.AddChild(new Bridge());
            GD.Print("[bridge] Boot: node added to root on first ProcessFrame");
        });
        tree.Connect(SceneTree.SignalName.ProcessFrame, cb);
        GD.Print("[bridge] Boot: --bridge present, install deferred to first ProcessFrame");
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ulong.TryParse(OS.GetEnvironment("STS2_FRIEND_STEAMID"), out _friendSteamId);
        _friendNameHint = OS.GetEnvironment("STS2_FRIEND_NAME");
        _enetHost = new ENetHost(new HostSide(this));
        var err = _enetHost.StartHost(ENetPort, 1);
        GD.Print($"[bridge] ENet host :{ENetPort} started (err={(err.HasValue ? err.Value.ToString() : "none")})");
        TryUpnp();
        // The Steam lobby is joined only after the iPhone connects (see _Process), so the host's
        // InitialGameInfoMessage reaches an iPhone that is already listening. iPhone packets sent while
        // the join is in progress are queued and flushed once the host connection is up.
        GD.Print("[bridge] ready. Waiting for the iPhone to connect; will then join the friend's StS2 lobby.");
    }

    // Ask the router (UPnP IGD) to forward the ENet port and report the public IP, so an iPhone on
    // cellular can reach the bridge directly. This only helps when the bridge's network has a public
    // address; behind CGNAT a relay or VPN is needed instead.
    void TryUpnp()
    {
        try
        {
            var upnp = new Upnp();
            int d = upnp.Discover();
            if (d != (int)Upnp.UpnpResult.Success) { GD.Print($"[bridge] UPnP discover failed ({(Upnp.UpnpResult)d}); port not auto-opened"); return; }
            var gw = upnp.GetGateway();
            if (gw == null || !gw.IsValidGateway()) { GD.Print("[bridge] UPnP: no valid gateway"); return; }
            string ext = upnp.QueryExternalAddress();
            int m1 = upnp.AddPortMapping(ENetPort, ENetPort, "sts2bridge", "UDP", 0);
            GD.Print($"[bridge] UPnP: external IP = {ext}, UDP {ENetPort} mapping result = {(Upnp.UpnpResult)m1}");
            GD.Print($"[bridge] >>> point the iPhone at --mp-join={ext} (if this is a public, non-CGNAT address) <<<");
        }
        catch (Exception e) { GD.Print($"[bridge] UPnP error: {e.Message}"); }
    }

    // Joins the first Steam friend currently in an StS2 lobby. Returns true once a join has started.
    bool TryDiscoverAndJoin()
    {
        try
        {
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                CSteamID f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(f, out FriendGameInfo_t info)) continue;
                if (info.m_gameID.AppID().m_AppId != Sts2AppId) continue;
                if (!info.m_steamIDLobby.IsValid() || !info.m_steamIDLobby.IsLobby()) continue;
                string pname = SteamFriends.GetFriendPersonaName(f);
                if (!string.IsNullOrEmpty(_friendNameHint) && !pname.Contains(_friendNameHint)) continue;
                ulong lobby = info.m_steamIDLobby.m_SteamID;
                GD.Print($"[bridge] found {pname} hosting StS2 (lobby {lobby}); joining...");
                _joinStarted = true;
                _ = JoinByLobby(lobby);
                return true;
            }
        }
        catch (Exception e) { GD.Print($"[bridge] discover error: {e.Message}"); }
        return false;
    }

    async Task JoinByFriendId(ulong friendId)
    {
        _steamClient ??= new GameSteamClient(new ClientSide(this));
        try { await _steamClient.ConnectToLobbyOwnedByFriend(friendId, CancellationToken.None); GD.Print("[bridge] joined friend lobby"); }
        catch (Exception e) { GD.Print($"[bridge] join failed: {e.Message}"); _joinStarted = false; }
    }

    async Task JoinByLobby(ulong lobbyId)
    {
        _steamClient ??= new GameSteamClient(new ClientSide(this));
        try { await _steamClient.ConnectToLobby(lobbyId, CancellationToken.None); GD.Print("[bridge] joined friend lobby"); }
        catch (Exception e) { GD.Print($"[bridge] join failed: {e.Message}"); _joinStarted = false; }
    }

    public override void _Process(double delta)
    {
        _enetHost?.Update();
        _steamClient?.Update();
        // Join the friend's lobby only once the iPhone is connected (see _Ready).
        if (!_joinStarted && IphoneConnected())
        {
            if (_friendSteamId != 0) { GD.Print($"[bridge] iPhone up -> joining friend by SteamID {_friendSteamId}"); _joinStarted = true; _ = JoinByFriendId(_friendSteamId); }
            else { _discoverAccum += delta; if (_discoverAccum >= 1.0) { _discoverAccum = 0; TryDiscoverAndJoin(); } }
        }
    }

    int _up, _down, _dropDown;
    readonly System.Collections.Generic.Queue<(byte[] b, NetTransferMode m, int ch)> _pendingUp = new();

    // FlagsFromMode accepts only Reliable or Unreliable, but ENet can report None (0); map that to Unreliable.
    static NetTransferMode Safe(NetTransferMode m) => m == NetTransferMode.Reliable ? NetTransferMode.Reliable : NetTransferMode.Unreliable;

    void SendToHost(byte[] b, NetTransferMode m, int ch)
    {
        try { _steamClient.SendMessageToHost(b, b.Length, Safe(m), ch); }
        catch (Exception e) { GD.Print($"[bridge] send-to-host error (m{(int)m} ch{ch}): {e.Message}"); }
    }

    void FromIphone(ulong senderId, byte[] bytes, NetTransferMode mode, int channel)
    {
        _iphonePeerId = senderId;
        if (_steamClient != null && _steamClient.IsConnected)
        { SendToHost(bytes, mode, channel); if (_up++ < 20 || _up % 100 == 0) GD.Print($"[bridge] iPhone->host #{_up} {bytes.Length}B ch{channel} m{(int)mode}"); }
        else { _pendingUp.Enqueue((bytes, mode, channel)); if (_pendingUp.Count <= 30) GD.Print($"[bridge] BUFFER iPhone->host {bytes.Length}B m{(int)mode} (host not connected; queued={_pendingUp.Count})"); }
    }

    void FlushPendingUp()
    {
        int flushed = 0;
        while (_pendingUp.Count > 0 && _steamClient != null && _steamClient.IsConnected)
        { var (b, m, ch) = _pendingUp.Dequeue(); SendToHost(b, m, ch); flushed++; }
        if (flushed > 0) GD.Print($"[bridge] flushed {flushed} buffered iPhone->host packets to friend host");
    }

    void FromFriend(byte[] bytes, NetTransferMode mode, int channel)
    {
        if (_iphonePeerId != 0 && IphoneConnected())
        {
            try { _enetHost.SendMessageToClient(_iphonePeerId, bytes, bytes.Length, Safe(mode), channel < 0 ? 0 : channel); }
            catch (Exception e) { GD.Print($"[bridge] send-to-iPhone error (m{(int)mode} ch{channel}): {e.Message}"); return; }
            if (_down++ < 20 || _down % 100 == 0) GD.Print($"[bridge] host->iPhone #{_down} {bytes.Length}B ch{channel} m{(int)mode}");
        }
        else { if (_dropDown++ < 10) GD.Print($"[bridge] DROP host->iPhone {bytes.Length}B: iPhone not connected"); }
    }

    bool IphoneConnected()
    {
        if (_enetHost == null || _iphonePeerId == 0) return false;
        foreach (var id in _enetHost.ConnectedPeerIds) if (id == _iphonePeerId) return true;
        return false;
    }

    // The two legs share one lifecycle: when either side drops, both are disconnected and the bridge
    // returns to waiting for the iPhone. Otherwise the host keeps streaming to a peer that is gone.
    bool _tearing;
    void TearDownRelay(string why)
    {
        if (_tearing) return;
        _tearing = true;
        GD.Print($"[bridge] tearing down relay ({why}); resetting to clean wait state");
        var peer = _iphonePeerId; var sc = _steamClient;
        _steamClient = null; _joinStarted = false; _iphonePeerId = 0;
        _pendingUp.Clear(); _up = _down = _dropDown = 0; _discoverAccum = 0;
        try { sc?.DisconnectFromHost((NetError)0, true); } catch { }
        try { if (peer != 0) _enetHost?.DisconnectClient(peer, (NetError)0, true); } catch { }
        _tearing = false;
    }

    // Separate handlers so packets from each side can be told apart.
    sealed class HostSide : INetHostHandler
    {
        readonly Bridge _b;
        public HostSide(Bridge b) { _b = b; }
        public void OnPeerConnected(ulong peerId) { _b._iphonePeerId = peerId; GD.Print($"[bridge] iPhone connected peer={peerId}"); }
        public void OnPeerDisconnected(ulong peerId, NetErrorInfo info) { GD.Print($"[bridge] iPhone peer {peerId} disconnected"); _b.TearDownRelay("iPhone left"); }
        public void OnDisconnected(NetErrorInfo info) { }
        public void OnPacketReceived(ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel) => _b.FromIphone(senderId, packetBytes, mode, channel);
    }
    sealed class ClientSide : INetClientHandler
    {
        readonly Bridge _b;
        public ClientSide(Bridge b) { _b = b; }
        public void OnConnectedToHost() { GD.Print("[bridge] connected to friend host"); _b.FlushPendingUp(); }
        public void OnDisconnectedFromHost(ulong hostNetId, NetErrorInfo info) { GD.Print($"[bridge] disconnected from friend host {hostNetId}"); _b.TearDownRelay("friend host left"); }
        public void OnPacketReceived(ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel) => _b.FromFriend(packetBytes, mode, channel);
    }
}
