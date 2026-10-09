// Steam Cloud save sync (SteamKit2): login / list / pull / push
using System.Security.Cryptography;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

const uint APPID = 2868840;
string cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "stssync");
Directory.CreateDirectory(cfgDir);
string tokenPath = Path.Combine(cfgDir, "token.json");

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
switch (cmd)
{
    case "login": await Login(); break;
    case "list": await WithCloud(async cloud => await ListFiles(cloud, dump: args.Contains("--dump"))); break;
    case "pull": await WithCloud(async cloud => await Pull(cloud, ArgVal("--dir"))); break;
    case "push": await WithCloud(async cloud => await Push(cloud, ArgVal("--dir"))); break;
    default:
        Console.WriteLine("usage: stssync <login | list [--dump] | pull --dir <D> | push --dir <D>>");
        Console.WriteLine("  login : Steam login (QR once), save refresh token");
        Console.WriteLine("  list  : list cloud files. --dump to check encryption");
        Console.WriteLine("  pull  : cloud -> local dir D (download all, keep rel paths)");
        Console.WriteLine("  push  : local dir D -> cloud (upload all)");
        break;
}

async Task Login()
{
    var client = new SteamClient();
    var mgr = new CallbackManager(client);
    var connected = new TaskCompletionSource();
    mgr.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
    mgr.Subscribe<SteamClient.DisconnectedCallback>(_ => connected.TrySetException(new Exception("disconnected")));
    client.Connect();
    var pump = PumpCallbacks(mgr);
    await connected.Task;

    string qrPng = Path.Combine(cfgDir, "login_qr.png");
    string? lastUrl = null;
    void SaveAndOpen(string url)
    {
        if (url == lastUrl) return; lastUrl = url;
        var data = new QRCoder.QRCodeGenerator().CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.L);
        File.WriteAllBytes(qrPng, new QRCoder.PngByteQRCode(data).GetGraphic(12));
        try { System.Diagnostics.Process.Start("open", $"\"{qrPng}\""); } catch { }
        Console.WriteLine($"QR refreshed. Scan it in Preview with the Steam app. (URL: {url})");
    }

    SteamKit2.Authentication.AuthPollResult? poll = null;
    for (int attempt = 1; attempt <= 6 && poll == null; attempt++)
    {
        try
        {
            var qr = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            { IsPersistentSession = true, DeviceFriendlyName = "stssync (Mac CLI)" });
            Console.WriteLine("\n>>> Scan the QR in Preview with the Steam mobile app (Steam Guard -> Scan QR) <<<");
            SaveAndOpen(qr.ChallengeURL);
            qr.ChallengeURLChanged = () => SaveAndOpen(qr.ChallengeURL);
            poll = await qr.PollingWaitForResultAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[try {attempt}/6] session expired/error ({ex.GetType().Name}), new QR...");
            lastUrl = null; await Task.Delay(500);
        }
    }
    if (poll == null) { Console.WriteLine("login failed (retries exhausted)."); client.Disconnect(); await pump; return; }
    try { File.Delete(qrPng); } catch { }
    var store = new TokenStore(poll.AccountName, poll.RefreshToken, poll.NewGuardData);
    File.WriteAllText(tokenPath, JsonSerializer.Serialize(store));
    if (!OperatingSystem.IsWindows()) try { File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    Console.WriteLine($"\n[ok] logged in. refresh token saved -> {tokenPath}");
    Console.WriteLine($"account: {poll.AccountName} (no scan needed next time; run list/sync directly)");
    client.Disconnect();
    await pump;
}

async Task WithCloud(Func<Cloud, Task> action)
{
    if (!File.Exists(tokenPath)) { Console.WriteLine("run `stssync login` first."); return; }
    var tok = JsonSerializer.Deserialize<TokenStore>(File.ReadAllText(tokenPath))!;

    var client = new SteamClient();
    var mgr = new CallbackManager(client);
    var user = client.GetHandler<SteamUser>()!;
    var unified = client.GetHandler<SteamUnifiedMessages>()!;
    var connected = new TaskCompletionSource();
    var loggedOn = new TaskCompletionSource<EResult>();
    mgr.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
    mgr.Subscribe<SteamClient.DisconnectedCallback>(_ => { connected.TrySetException(new Exception("disconnected")); });
    mgr.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedOn.TrySetResult(cb.Result));

    client.Connect();
    var pump = PumpCallbacks(mgr);
    await connected.Task;
    user.LogOn(new SteamUser.LogOnDetails { Username = tok.AccountName, AccessToken = tok.RefreshToken, ShouldRememberPassword = true });
    var result = await loggedOn.Task;
    if (result != EResult.OK)
    {
        Console.WriteLine($"login failed: {result} (re-run `login` if token expired)");
        client.Disconnect(); await pump; return;
    }
    var cloud = unified.CreateService<Cloud>();
    await action(cloud);
    client.Disconnect();
    await pump;
}

async Task ListFiles(Cloud cloud, bool dump)
{
    uint start = 0; int total = 0;
    CCloud_UserFile? first = null;
    while (true)
    {
        var resp = await cloud.EnumerateUserFiles(new CCloud_EnumerateUserFiles_Request
        { appid = APPID, extended_details = true, count = 500, start_index = start });
        if (resp.Result != EResult.OK) { Console.WriteLine($"EnumerateUserFiles failed: {resp.Result}"); return; }
        var body = resp.Body;
        foreach (var f in body.files)
        {
            first ??= f;
            var ts = DateTimeOffset.FromUnixTimeSeconds((long)f.timestamp).ToLocalTime();
            Console.WriteLine($"  {f.file_size,10}  {ts:yyyy-MM-dd HH:mm:ss}  flags={f.flags}  {f.filename}");
            total++;
        }
        if (body.files.Count == 0 || total >= body.total_files) break;
        start += (uint)body.files.Count;
    }
    Console.WriteLine($"{total} files (appid {APPID})");

    if (dump && first != null)
    {
        Console.WriteLine($"\n=== download first file to check encryption: {first.filename} ===");
        var d = await cloud.ClientFileDownload(new CCloud_ClientFileDownload_Request { appid = APPID, filename = first.filename });
        Console.WriteLine($"encrypted={d.Body.encrypted} use_https={d.Body.use_https} host={d.Body.url_host} sha={Convert.ToHexString(d.Body.sha_file ?? Array.Empty<byte>())}");
        var url = $"{(d.Body.use_https ? "https" : "http")}://{d.Body.url_host}{d.Body.url_path}";
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var h in d.Body.request_headers) req.Headers.TryAddWithoutValidation(h.name, h.value);
        var bytes = await (await http.SendAsync(req)).Content.ReadAsByteArrayAsync();
        var head = bytes.Take(16).ToArray();
        Console.WriteLine($"downloaded {bytes.Length} bytes, first 16B(hex)={Convert.ToHexString(head)}");
        Console.WriteLine($"ascii head: {System.Text.Encoding.ASCII.GetString(head.Select(b => (byte)(b is >= 32 and < 127 ? b : (byte)'.')).ToArray())}");
    }
}

string? ArgVal(string flag)
{
    var i = Array.IndexOf(args, flag);
    return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
}

async Task<List<CCloud_UserFile>> EnumAll(Cloud cloud)
{
    var all = new List<CCloud_UserFile>(); uint start = 0;
    while (true)
    {
        var r = await cloud.EnumerateUserFiles(new CCloud_EnumerateUserFiles_Request
        { appid = APPID, extended_details = true, count = 500, start_index = start });
        if (r.Result != EResult.OK) throw new Exception($"Enumerate failed: {r.Result}");
        all.AddRange(r.Body.files);
        if (r.Body.files.Count == 0 || all.Count >= r.Body.total_files) break;
        start += (uint)r.Body.files.Count;
    }
    return all;
}

byte[] ZipRaw(byte[] raw)
{
    using var ms = new MemoryStream();
    using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
    { var e = z.CreateEntry("data", System.IO.Compression.CompressionLevel.Optimal); using var s = e.Open(); s.Write(raw, 0, raw.Length); }
    return ms.ToArray();
}
byte[] UnzipCloud(byte[] zip)
{
    using var z = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read);
    using var s = z.Entries[0].Open(); using var o = new MemoryStream(); s.CopyTo(o); return o.ToArray();
}
bool ZipMagic(byte[] d) => d.Length >= 4 && d[0] == 0x50 && d[1] == 0x4B && d[2] == 3 && d[3] == 4;

async Task Pull(Cloud cloud, string? dir)
{
    if (dir == null) { Console.WriteLine("--dir <local dir> required"); return; }
    Directory.CreateDirectory(dir);
    var files = await EnumAll(cloud);
    using var http = new HttpClient();
    int ok = 0;
    foreach (var f in files)
    {
        var d = await cloud.ClientFileDownload(new CCloud_ClientFileDownload_Request { appid = APPID, filename = f.filename });
        var url = $"{(d.Body.use_https ? "https" : "http")}://{d.Body.url_host}{d.Body.url_path}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var h in d.Body.request_headers) req.Headers.TryAddWithoutValidation(h.name, h.value);
        var bytes = await (await http.SendAsync(req)).Content.ReadAsByteArrayAsync();
        // Steam Cloud serves zipped files when the stored size differs from the raw size.
        if (d.Body.raw_file_size > 0 && d.Body.raw_file_size != d.Body.file_size && ZipMagic(bytes))
            bytes = UnzipCloud(bytes);
        var outp = Path.Combine(dir, f.filename.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
        await File.WriteAllBytesAsync(outp, bytes);
        ok++;
        if (ok % 20 == 0 || ok == files.Count) Console.WriteLine($"  pull {ok}/{files.Count}");
    }
    Console.WriteLine($"[ok] pull done: {ok} files -> {dir} (decompressed)");
}

async Task Push(Cloud cloud, string? dir)
{
    if (dir == null || !Directory.Exists(dir)) { Console.WriteLine("--dir <existing local dir> required"); return; }
    var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
    using var http = new HttpClient();
    int ok = 0;
    foreach (var path in files)
    {
        var rel = Path.GetRelativePath(dir, path).Replace(Path.DirectorySeparatorChar, '/');
        var raw = await File.ReadAllBytesAsync(path);
        // Steam Cloud takes the SHA1 of the raw file and accepts a zipped payload when it is smaller.
        var sha = SHA1.HashData(raw);
        var zipped = ZipRaw(raw);
        var payload = zipped.Length < raw.Length ? zipped : raw;
        var begin = await cloud.ClientBeginFileUpload(new CCloud_ClientBeginFileUpload_Request
        {
            appid = APPID,
            filename = rel,
            file_size = (uint)payload.Length,
            raw_file_size = (uint)raw.Length,
            file_sha = sha,
            time_stamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            can_encrypt = false,
        });
        if (begin.Result == EResult.DuplicateRequest) { ok++; continue; }  // already up-to-date
        bool uploaded = true;
        foreach (var b in begin.Body.block_requests)
        {
            var url = $"{(b.use_https ? "https" : "http")}://{b.url_host}{b.url_path}";
            var body = b.explicit_body_data is { Length: > 0 } ? b.explicit_body_data
                       : payload.Skip((int)b.block_offset).Take((int)b.block_length).ToArray();
            var verb = (int)b.http_method switch { 1 => "GET", 2 => "POST", 3 => "HEAD", 4 => "PUT", 5 => "DELETE", 6 => "OPTIONS", 7 => "PATCH", _ => "PUT" };
            var m = new HttpRequestMessage(new HttpMethod(verb), url)
            { Content = new ByteArrayContent(body) };
            foreach (var h in b.request_headers)
                if (!m.Headers.TryAddWithoutValidation(h.name, h.value)) m.Content.Headers.TryAddWithoutValidation(h.name, h.value);
            var resp = await http.SendAsync(m);
            if (!resp.IsSuccessStatusCode) uploaded = false;
        }
        var commit = await cloud.ClientCommitFileUpload(new CCloud_ClientCommitFileUpload_Request
        { appid = APPID, filename = rel, file_sha = sha, transfer_succeeded = uploaded });
        if (commit.Body.file_committed) { ok++; if (ok % 20 == 0) Console.WriteLine($"  push {ok}/{files.Length}"); }
        else Console.WriteLine($"  [warn] commit failed: {rel}");
    }
    Console.WriteLine($"[ok] push done: {ok}/{files.Length} files -> cloud");
}

Task PumpCallbacks(CallbackManager mgr) => Task.Run(() =>
{
    for (int i = 0; i < 6000; i++) mgr.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
});

record TokenStore(string AccountName, string RefreshToken, string? GuardData);
