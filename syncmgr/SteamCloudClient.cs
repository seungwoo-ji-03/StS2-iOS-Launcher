// Minimal NativeAOT-safe Steam CM client over WebSocket. Messages come from gen/ (Google.Protobuf); framing is hand-rolled.
// Flow: GetCMListForConnect -> wss://<cm>/cmsocket/ -> ClientLogon(refresh token) -> ServiceMethod(Cloud.*).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using SteamCmProto;
using SteamCloudProto;

// EResult 108: too many unfinished operations server-side. Transient; abort this sync and retry later.
public sealed class TooManyPendingException : Exception { public TooManyPendingException(string what) : base(what + ": Steam Cloud busy (TooManyPending)") { } }

public sealed class SteamCloudClient : IDisposable
{
    const uint ProtoMask = 0x80000000;
    const int EMsg_Multi = 1, EMsg_ServiceMethodResponse = 147, EMsg_ServiceMethodCall = 151,
              EMsg_ClientHeartBeat = 703, EMsg_ClientLogOnResponse = 751, EMsg_ClientLogon = 5514;
    const uint CurrentProtocol = 65581;

    readonly ClientWebSocket _ws = new();
    readonly HttpClient _http = new();
    ulong _steamid;
    int _sessionid;
    ulong _jobseq = 1;
    readonly Action<string> _log;

    readonly Dictionary<ulong, TaskCompletionSource<(int eresult, byte[] body)>> _pending = new();
    CancellationTokenSource _rxCts;
    TaskCompletionSource<int> _logonTcs;
    volatile bool _rxDead;

    public SteamCloudClient(Action<string> log = null) { _log = log ?? (_ => { }); }

    // Returns the logon EResult (1 = OK). Transport failures throw.
    public async Task<int> ConnectAndLogonAsync(string accountName, string refreshToken, ulong steamid, CancellationToken ct)
    {
        string endpoint = await PickCmAsync(ct);
        if (endpoint == null) throw new IOException("no CM endpoint");
        var uri = new Uri($"wss://{endpoint}/cmsocket/");
        _log($"[cm] connecting {uri}");
        await _ws.ConnectAsync(uri, ct);
        _rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var logonTcs = new TaskCompletionSource<int>();
        _ = Task.Run(() => ReceiveLoop(logonTcs, _rxCts.Token));
        _logonTcs = logonTcs;

        // No channel encryption on wss. obfuscated_private_ip (a stable "login id") must be set; omitting it
        // can trip logon throttling or AccessDenied.
        uint loginId = 0x0D05A710 ^ (uint)(steamid & 0xFFFFFFFF);
        var logon = new CMsgClientLogon
        {
            AccountName = accountName,
            AccessToken = refreshToken,
            ProtocolVersion = CurrentProtocol,
            ClientOsType = 4294967295, // Unknown
            CellId = 0,
            ClientLanguage = "english",
            ClientPackageVersion = 1771,
            ShouldRememberPassword = false,
            SupportsRateLimitResponse = true,
            ClientSuppliedSteamId = steamid,
            DeprecatedObfustucatedPrivateIp = loginId,
            ObfuscatedPrivateIp = new CMsgIPAddress { V4 = loginId },
            MachineName = "sts2ios", // shown in Steam's authorized devices list
        };
        await SendAsync(EMsg_ClientLogon, new CMsgProtoBufHeader { Steamid = 0x0110000100000000UL, ClientSessionid = 0 }, logon.ToByteArray(), ct);

        var res = await logonTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        if (res != 1) { _log($"[cm] logon eresult={res}"); return res; }
        _log($"[cm] logged on OK steamid={_steamid} session={_sessionid}");
        _ = Task.Run(() => HeartbeatLoop(_rxCts.Token));
        return 1;
    }

    // SteamClient-audience refresh tokens renew only over an authenticated CM session (the WebAPI path returns
    // Expired/63), so GenerateAccessTokenForApp is sent as a ServiceMethod. A non-empty refresh_token in the
    // response replaces and invalidates the old one. Returns the new token, or null to keep the current one.
    // Call after ConnectAndLogonAsync.
    public async Task<string> RenewTokenAsync(string refreshToken, ulong steamid, CancellationToken ct)
    {
        try
        {
            var req = new SteamAuthProto.CAuthentication_AccessToken_GenerateForApp_Request
            { RefreshToken = refreshToken, Steamid = steamid, RenewalType = 1 };
            var (er, body) = await CallAsync("Authentication.GenerateAccessTokenForApp#1", req.ToByteArray(), ct);
            if (er != 1) { _log($"[cm] renew eresult={er}"); return null; }
            var resp = SteamAuthProto.CAuthentication_AccessToken_GenerateForApp_Response.Parser.ParseFrom(body);
            if (!string.IsNullOrEmpty(resp.RefreshToken)) { _log("[cm] token renewed (new refresh token issued)"); return resp.RefreshToken; }
            _log("[cm] renew ok, no new refresh token (keep current — normal)");
        }
        catch (Exception e) { _log($"[cm] renew failed: {e.Message}"); }
        return null;
    }

    public static long TokenExpUnix(string jwt) => JwtExp(jwt);

    static long JwtExp(string jwt)
    {
        var p = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p)));
        return doc.RootElement.GetProperty("exp").GetInt64();
    }

    async Task<string> PickCmAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync("https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&format=json&maxcount=20", ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        // Random pick among the least-loaded few, so a reconnect usually lands on a different CM.
        var list = new List<(double load, string ep)>();
        foreach (var s in doc.RootElement.GetProperty("response").GetProperty("serverlist").EnumerateArray())
        {
            if (s.GetProperty("type").GetString() != "websockets") continue;
            list.Add((s.TryGetProperty("load", out var l) ? l.GetDouble() : 0, s.GetProperty("endpoint").GetString()));
        }
        if (list.Count == 0) return null;
        var top = list.OrderBy(x => x.load).Take(4).ToList();
        return top[Random.Shared.Next(top.Count)].ep;
    }

    public async Task<CCloud_EnumerateUserFiles_Response> EnumerateAsync(uint appid, CancellationToken ct)
    {
        var all = new CCloud_EnumerateUserFiles_Response();
        uint start = 0;
        while (true)
        {
            var req = new CCloud_EnumerateUserFiles_Request { Appid = appid, ExtendedDetails = true, Count = 500, StartIndex = start };
            var (er, body) = await CallAsync("Cloud.EnumerateUserFiles#1", req.ToByteArray(), ct);
            if (er != 1) throw new Exception($"EnumerateUserFiles eresult={er}");
            var resp = CCloud_EnumerateUserFiles_Response.Parser.ParseFrom(body);
            all.Files.Add(resp.Files);
            all.TotalFiles = resp.TotalFiles;
            start += (uint)resp.Files.Count;
            if (resp.Files.Count == 0 || start >= resp.TotalFiles) break;
        }
        return all;
    }

    public async Task<byte[]> DownloadAsync(uint appid, string filename, CancellationToken ct)
    {
        var req = new CCloud_ClientFileDownload_Request { Appid = appid, Filename = filename };
        var (er, body) = await CallAsync("Cloud.ClientFileDownload#1", req.ToByteArray(), ct);
        if (er != 1) throw new Exception($"ClientFileDownload eresult={er}");
        var d = CCloud_ClientFileDownload_Response.Parser.ParseFrom(body);
        var scheme = d.UseHttps ? "https" : "http";
        var url = $"{scheme}://{d.UrlHost}{d.UrlPath}";
        using var m = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var h in d.RequestHeaders) m.Headers.TryAddWithoutValidation(h.Name, h.Value);
        using var r = await _http.SendAsync(m, ct);
        r.EnsureSuccessStatusCode();
        var bytes = await r.Content.ReadAsByteArrayAsync(ct);
        // Cloud files may be a single-entry zip (PC client entry "z", ours "data"); the game wants raw JSON.
        // Saves start with '{', so the PK magic check is unambiguous.
        return UnzipIfZipped(bytes);
    }

    public static bool IsZip(byte[] b) => b != null && b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;

    public static byte[] UnzipIfZipped(byte[] bytes)
    {
        if (!IsZip(bytes)) return bytes;
        try
        {
            using var ms = new MemoryStream(bytes, false);
            using var z = new ZipArchive(ms, ZipArchiveMode.Read);
            if (z.Entries.Count < 1) return bytes;
            using var s = z.Entries[0].Open();
            using var outMs = new MemoryStream();
            s.CopyTo(outMs);
            return outMs.ToArray();
        }
        catch { return bytes; }
    }

    // Every write opens a pending server-side operation; unfinished ones accumulate until Steam answers 108.
    // Each push runs inside one app upload batch that is always completed, so an interrupted push leaves at
    // most one open batch. Throws TooManyPendingException if Steam still refuses after backing off.
    public async Task<ulong> BeginBatchAsync(uint appid, IEnumerable<string> uploads, IEnumerable<string> deletes, CancellationToken ct)
    {
        var req = new CCloud_BeginAppUploadBatch_Request { Appid = appid, MachineName = "sts2ios", ClientId = 0 };
        req.FilesToUpload.Add(uploads);
        req.FilesToDelete.Add(deletes);
        for (int attempt = 0; ; attempt++)
        {
            var (er, body) = await CallAsync("Cloud.BeginAppUploadBatch#1", req.ToByteArray(), ct);
            if (er == 1) return CCloud_BeginAppUploadBatch_Response.Parser.ParseFrom(body).BatchId;
            if (er == EResult_TooManyPending)
            {
                if (attempt >= 2) throw new TooManyPendingException("BeginAppUploadBatch");
                await BackoffAsync("begin batch", attempt, ct);
                continue;
            }
            throw new Exception($"BeginAppUploadBatch eresult={er}");
        }
    }

    public async Task CompleteBatchAsync(uint appid, ulong batchId, int eresult, CancellationToken ct)
    {
        var req = new CCloud_CompleteAppUploadBatch_Request { Appid = appid, BatchId = batchId, BatchEresult = (uint)eresult };
        var (er, _) = await CallAsync("Cloud.CompleteAppUploadBatchBlocking#1", req.ToByteArray(), ct);
        if (er != 1) _log($"[cm] complete batch {batchId}: eresult={er}");
    }

    // Begin/PUT/Commit with SHA1 of the raw bytes; the payload is zip("data") when smaller. Returns false if not committed.
    public async Task<bool> UploadAsync(uint appid, string filename, byte[] raw, ulong batchId, CancellationToken ct)
    {
        var sha = System.Security.Cryptography.SHA1.HashData(raw);
        var zipped = ZipRaw(raw);
        var payload = zipped.Length < raw.Length ? zipped : raw;
        var begReq = new CCloud_ClientBeginFileUpload_Request
        {
            Appid = appid,
            Filename = filename,
            FileSize = (uint)payload.Length,
            RawFileSize = (uint)raw.Length,
            FileSha = ByteString.CopyFrom(sha),
            TimeStamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CanEncrypt = false,
        };
        if (batchId != 0) begReq.UploadBatchId = batchId;
        var (ber, bbody) = await CallAsync("Cloud.ClientBeginFileUpload#1", begReq.ToByteArray(), ct);
        if (ber == 29) return true; // DuplicateRequest: the cloud already has this content
        if (ber == EResult_TooManyPending) throw new TooManyPendingException("BeginFileUpload " + filename);
        if (ber != 1) throw new Exception($"BeginFileUpload {filename} eresult={ber}");
        var beg = CCloud_ClientBeginFileUpload_Response.Parser.ParseFrom(bbody);
        bool uploaded = true;
        foreach (var b in beg.BlockRequests)
        {
            var url = $"{(b.UseHttps ? "https" : "http")}://{b.UrlHost}{b.UrlPath}";
            byte[] body = b.ExplicitBodyData.Length > 0 ? b.ExplicitBodyData.ToByteArray()
                        : payload.AsSpan((int)b.BlockOffset, (int)b.BlockLength).ToArray();
            var verb = (int)b.HttpMethod switch { 1 => "GET", 2 => "POST", 3 => "HEAD", 4 => "PUT", 5 => "DELETE", 6 => "OPTIONS", 7 => "PATCH", _ => "PUT" };
            using var m = new HttpRequestMessage(new HttpMethod(verb), url) { Content = new ByteArrayContent(body) };
            foreach (var h in b.RequestHeaders)
                if (!m.Headers.TryAddWithoutValidation(h.Name, h.Value)) m.Content.Headers.TryAddWithoutValidation(h.Name, h.Value);
            using var resp = await _http.SendAsync(m, ct);
            if (!resp.IsSuccessStatusCode) { uploaded = false; _log($"[cm] upload block {filename} HTTP {(int)resp.StatusCode}"); }
        }
        // Commit even after a failed transfer (TransferSucceeded=false) so the pending upload is closed.
        var comReq = new CCloud_ClientCommitFileUpload_Request { Appid = appid, Filename = filename, FileSha = ByteString.CopyFrom(sha), TransferSucceeded = uploaded };
        var (cer, cbody) = await CallAsync("Cloud.ClientCommitFileUpload#1", comReq.ToByteArray(), ct);
        if (cer != 1) throw new Exception($"CommitFileUpload {filename} eresult={cer}");
        bool committed = CCloud_ClientCommitFileUpload_Response.Parser.ParseFrom(cbody).FileCommitted;
        if (!committed) _log($"[cm] upload {filename} not committed (transfer ok={uploaded})");
        return committed && uploaded;
    }

    // batchId 0 wraps the delete in its own batch. forget = Steam's FileForget (cloud only; other devices keep
    // their local copy), which is how the game trims run history to its quota.
    public async Task DeleteAsync(uint appid, string filename, ulong batchId, CancellationToken ct, bool forget = false)
    {
        bool own = batchId == 0;
        if (own) batchId = await BeginBatchAsync(appid, Array.Empty<string>(), new[] { filename }, ct);
        int result = 1;
        try
        {
            var req = new CCloud_ClientDeleteFile_Request { Appid = appid, Filename = filename, IsExplicitDelete = !forget, UploadBatchId = batchId };
            var (der, _) = await CallAsync("Cloud.ClientDeleteFile#1", req.ToByteArray(), ct);
            if (der == 9) return; // FileNotFound
            if (der == EResult_TooManyPending) { result = der; throw new TooManyPendingException("ClientDeleteFile " + filename); }
            if (der != 1) { result = der; throw new Exception($"ClientDeleteFile {filename} eresult={der}"); }
        }
        finally { if (own) await CompleteBatchAsync(appid, batchId, result, ct); }
    }

    const int EResult_TooManyPending = 108;
    async Task BackoffAsync(string what, int attempt, CancellationToken ct)
    {
        int secs = 2 << attempt;
        _log($"[cm] {what}: TooManyPending, retrying in {secs}s");
        await Task.Delay(TimeSpan.FromSeconds(secs), ct);
    }

    static byte[] ZipRaw(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        { var e = z.CreateEntry("data", CompressionLevel.Optimal); using var s = e.Open(); s.Write(raw, 0, raw.Length); }
        return ms.ToArray();
    }

    async Task<(int eresult, byte[] body)> CallAsync(string method, byte[] reqBody, CancellationToken ct)
    {
        ulong jobid = _jobseq++;
        var tcs = new TaskCompletionSource<(int, byte[])>();
        lock (_pending) _pending[jobid] = tcs;
        var hdr = new CMsgProtoBufHeader { Steamid = _steamid, ClientSessionid = _sessionid, JobidSource = jobid, TargetJobName = method };
        try
        {
            if (_rxDead) throw new IOException("CM connection closed");
            await SendAsync(EMsg_ServiceMethodCall, hdr, reqBody, ct);
            // Long timeout: on cold boot the receive loop competes with asset preload for pool threads.
            // A dead connection still fails fast because ReceiveLoop faults pending calls on exit.
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
        finally { lock (_pending) _pending.Remove(jobid); }
    }

    readonly SemaphoreSlim _sendLock = new(1, 1);
    async Task SendAsync(int emsg, CMsgProtoBufHeader header, byte[] body, CancellationToken ct)
    {
        byte[] hb = header.ToByteArray();
        var buf = new byte[8 + hb.Length + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)emsg | ProtoMask);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), hb.Length);
        hb.CopyTo(buf, 8);
        body.CopyTo(buf, 8 + hb.Length);
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(buf, WebSocketMessageType.Binary, true, ct); }
        finally { _sendLock.Release(); }
    }

    async Task ReceiveLoop(TaskCompletionSource<int> logonTcs, CancellationToken ct)
    {
        var acc = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                acc.SetLength(0);
                WebSocketReceiveResult r;
                do { r = await _ws.ReceiveAsync(chunk, ct); if (r.MessageType == WebSocketMessageType.Close) return; acc.Write(chunk, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Binary) Dispatch(acc.ToArray(), logonTcs);
            }
        }
        catch (Exception e) { if (!ct.IsCancellationRequested) _log($"[cm] rx loop ended: {e.Message}"); }
        finally
        {
            _rxDead = true;
            _logonTcs?.TrySetException(new IOException("CM connection closed during logon"));
            List<TaskCompletionSource<(int, byte[])>> waiting;
            lock (_pending) { waiting = new(_pending.Values); _pending.Clear(); }
            foreach (var w in waiting) w.TrySetException(new IOException("CM connection closed"));
        }
    }

    // Packets are dispatched in isolation so one unparsable server message cannot end the receive loop.
    void Dispatch(byte[] pkt, TaskCompletionSource<int> logonTcs)
    {
        try { DispatchCore(pkt, logonTcs); }
        catch (Exception e) { _log($"[cm] skip packet ({pkt.Length}B): {e.Message}"); }
    }

    void DispatchCore(byte[] pkt, TaskCompletionSource<int> logonTcs)
    {
        if (pkt.Length < 8) return;
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(pkt);
        int emsg = (int)(raw & 0x7FFFFFFF);
        int hlen = BinaryPrimitives.ReadInt32LittleEndian(pkt.AsSpan(4));
        if (hlen < 0 || 8 + hlen > pkt.Length) return;
        var header = CMsgProtoBufHeader.Parser.ParseFrom(pkt, 8, hlen);
        int bodyOff = 8 + hlen, bodyLen = pkt.Length - bodyOff;

        if (emsg == EMsg_Multi)
        {
            var multi = CMsgMulti.Parser.ParseFrom(pkt, bodyOff, bodyLen);
            byte[] payload = multi.MessageBody.ToByteArray();
            if (multi.SizeUnzipped > 0) payload = Gunzip(payload);
            int p = 0;
            while (p + 4 <= payload.Length)
            {
                int sub = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(p)); p += 4;
                if (sub < 0 || p + sub > payload.Length) break;
                var one = new byte[sub]; Array.Copy(payload, p, one, 0, sub); p += sub;
                Dispatch(one, logonTcs);
            }
            return;
        }
        if (emsg == EMsg_ClientLogOnResponse)
        {
            var resp = CMsgClientLogonResponse.Parser.ParseFrom(pkt, bodyOff, bodyLen);
            _steamid = header.Steamid; _sessionid = header.ClientSessionid;
            logonTcs.TrySetResult(resp.Eresult);
            return;
        }
        if (emsg == EMsg_ServiceMethodResponse)
        {
            var b = new byte[bodyLen]; Array.Copy(pkt, bodyOff, b, 0, bodyLen);
            TaskCompletionSource<(int, byte[])> tcs = null;
            lock (_pending) if (_pending.Remove(header.JobidTarget, out var t)) tcs = t;
            tcs?.TrySetResult((header.Eresult, b));
            return;
        }
        // Other EMsgs (account info, licenses, ...) are ignored.
    }

    async Task HeartbeatLoop(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(9000, ct); await SendAsync(EMsg_ClientHeartBeat, new CMsgProtoBufHeader { Steamid = _steamid, ClientSessionid = _sessionid }, new CMsgClientHeartBeat().ToByteArray(), ct); } }
        catch { }
    }

    static byte[] Gunzip(byte[] data)
    {
        using var inp = new MemoryStream(data);
        using var gz = new GZipStream(inp, CompressionMode.Decompress);
        using var outp = new MemoryStream();
        gz.CopyTo(outp);
        return outp.ToArray();
    }

    public void Dispose() { try { _rxCts?.Cancel(); } catch { } _ws.Dispose(); _http.Dispose(); }
}
