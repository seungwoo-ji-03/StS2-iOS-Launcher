// In-app Steam Cloud sync for Slay the Spire 2 on iOS.
// Uses the NativeAOT-safe hand-rolled CM client (SteamCloudClient), not SteamKit2 (protobuf-net needs
// Reflection.Emit, which NativeAOT does not have).
//
// Timing (cecilpatch --sync prepends Boot() to NGame.InitializePlatform, which runs before GameStartup
// reads profile/progress/prefs):
//   1. Boot, blocking (<= LiveBudget): reconcile the live files (everything except run history). The game
//      then loads whatever is on disk, so a pulled file is actually used.
//   2. Boot, background: reconcile run history (many files, only read when viewing history).
//   3. App backgrounded / quit: push. Never writes local files — the game holds its state in memory and
//      would overwrite them on its next save.
//
// Reconcile is three-way per file, with the last agreed content (SHA1) in sync_manifest.txt as the base:
//   local == base, cloud changed  -> take cloud          local changed, cloud == base -> upload local
//   both changed / no base        -> progress comparison, the losing copy is backed up
//   run file gone locally, cloud == base -> the run ended here -> delete it in the cloud
//   run file gone from cloud, local == base -> the run ended elsewhere -> retire the local copy
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class SyncManager : Node
{
    const uint APPID = 2868840;
    static readonly TimeSpan LiveBudget = TimeSpan.FromSeconds(8);
    static readonly TimeSpan HistoryBudget = TimeSpan.FromSeconds(180);
    static readonly TimeSpan PushBudget = TimeSpan.FromSeconds(25);

    static string SaveRoot => ProjectSettings.GlobalizePath("user://default/1");
    static string TokenPath => ProjectSettings.GlobalizePath("user://steam_token.json");
    static string BackupDir => ProjectSettings.GlobalizePath("user://save-backups");
    static string LogPath => ProjectSettings.GlobalizePath("user://sync_log.txt");
    static string ManifestPath => ProjectSettings.GlobalizePath("user://sync_manifest.txt");
    static string StatusPath => ProjectSettings.GlobalizePath("user://sync_status.txt");

    enum Phase { BootLive, BootHistory, Push }

    static readonly SemaphoreSlim RunLock = new(1, 1);
    static readonly object WriteLock = new();
    static readonly object LogLock = new();
    static bool _liveWindowClosed;              // set once the game may have loaded the live files
    static CancellationTokenSource _historyCts;  // background history pull; a push cancels it
    static bool _booted;

    static void Log(string msg)
    {
        GD.Print(msg);
        lock (LogLock)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Move(LogPath, LogPath + ".1", true);
                File.AppendAllText(LogPath, $"{Time.GetDatetimeStringFromSystem()} {msg}\n");
            }
            catch { }
        }
    }

    static void WriteStatus(string state, string detail)
    {
        try { File.WriteAllText(StatusPath, $"{state}\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n{detail}\n"); } catch { }
    }

    // Cecil-injected at NGame.InitializePlatform (main thread, before any save is read).
    public static void Boot()
    {
        if (_booted) { Log("[sync] Boot() already ran this launch; skip"); return; }
        _booted = true;
        try
        {
            var sm = new SyncManager();
            if (Engine.GetMainLoop() is SceneTree tree) tree.Root.CallDeferred(Node.MethodName.AddChild, sm);
            if (!File.Exists(TokenPath)) { Log("[sync] no token; sync disabled"); WriteStatus("disabled", "no token"); CloseLiveWindow(); return; }

            var sw = Stopwatch.StartNew();
            using (var liveCts = new CancellationTokenSource(LiveBudget))
            {
                var live = Task.Run(() => sm.RunAsync(Phase.BootLive, liveCts.Token));
                bool finished = live.Wait(LiveBudget + TimeSpan.FromSeconds(1));
                Log($"[sync] boot live phase {(finished ? "done (" + live.Result + ")" : "timed out, game uses local saves")} in {sw.ElapsedMilliseconds}ms");
            }
            CloseLiveWindow();

            _historyCts = new CancellationTokenSource(HistoryBudget);
            var hct = _historyCts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), hct); Log($"[sync] history phase done ({await sm.RunAsync(Phase.BootHistory, hct)})"); }
                catch (Exception e) { Log($"[sync] history phase stopped: {(e.InnerException ?? e).Message}"); }
            });
        }
        catch (Exception e) { Log($"[sync] boot failed: {e.GetType().Name}: {e.Message}"); CloseLiveWindow(); }
    }

    static void CloseLiveWindow() { lock (WriteLock) _liveWindowClosed = true; }

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    // iOS backgrounding. The main thread must return quickly (watchdog), so the push runs on the pool
    // inside a UIKit background task, which keeps the process alive until it is ended.
    public override void _Notification(int what)
    {
        if (what != NotificationApplicationPaused || !File.Exists(TokenPath)) return;
        _historyCts?.Cancel();
        nuint bg = IosNative.BeginBackgroundTask();
        var push = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(PushBudget);
                Log($"[sync] background push done ({await RunAsync(Phase.Push, cts.Token)})");
            }
            catch (Exception e) { Log($"[sync] background push failed: {(e.InnerException ?? e).Message}"); }
            finally { IosNative.EndBackgroundTask(bg); }
        });
        if (bg == 0) push.Wait(TimeSpan.FromSeconds(4)); // no background task available: keep within the watchdog
    }

    // iOS: SceneTree.Quit() does not end the process. Cecil-injected into NGame.Quit right before
    // GetTree().Quit(); the game has already saved by then. Push (bounded), then exit.
    public static void QuitSync()
    {
        Log("[sync] QuitSync() -> push then exit");
        try
        {
            _historyCts?.Cancel();
            if (File.Exists(TokenPath))
            {
                var sm = new SyncManager();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var task = Task.Run(() => sm.RunAsync(Phase.Push, cts.Token));
                Log(task.Wait(TimeSpan.FromSeconds(13)) ? $"[sync] quit-push done ({task.Result})" : "[sync] quit-push timed out");
            }
        }
        catch (Exception e) { Log($"[sync] quit-push failed: {(e.InnerException ?? e).Message}"); }
        Log("[sync] terminating process");
        IosNative.HardExit(0);
    }

    static (string acct, string token, ulong steamid)? ReadToken()
    {
        foreach (var path in new[] { TokenPath, TokenPath + ".prev" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                string acct = r.GetProperty("AccountName").GetString();
                string tok = r.GetProperty("RefreshToken").GetString();
                var pp = tok.Split('.')[1].Replace('-', '+').Replace('_', '/');
                pp = pp.PadRight(pp.Length + (4 - pp.Length % 4) % 4, '=');
                using var jwt = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pp)));
                ulong steamid = ulong.Parse(jwt.RootElement.GetProperty("sub").GetString());
                if (path != TokenPath) Log("[sync] token file unreadable, using previous copy");
                return (acct, tok, steamid);
            }
            catch (Exception e) { Log($"[sync] token read failed ({Path.GetFileName(path)}): {e.Message}"); }
        }
        return null;
    }

    // A renewal invalidates the old token, so the new one must never be lost: keep the old file as .prev
    // and replace atomically.
    static void SaveToken(string acct, string newToken)
    {
        try
        {
            string guard = null;
            try { using var d = JsonDocument.Parse(File.ReadAllText(TokenPath)); if (d.RootElement.TryGetProperty("GuardData", out var g) && g.ValueKind == JsonValueKind.String) guard = g.GetString(); } catch { }
            var json = "{\"AccountName\":" + JsonSerializer.Serialize(acct) + ",\"RefreshToken\":" + JsonSerializer.Serialize(newToken) + ",\"GuardData\":" + (guard == null ? "null" : JsonSerializer.Serialize(guard)) + "}";
            if (File.Exists(TokenPath)) File.Copy(TokenPath, TokenPath + ".prev", true);
            AtomicWrite(TokenPath, System.Text.Encoding.UTF8.GetBytes(json));
            Log("[sync] renewed refresh token saved");
        }
        catch (Exception e) { Log($"[sync] token save failed: {e.Message}"); }
    }

    // Logon errors that retrying cannot fix (bad / expired / revoked token).
    // 5 InvalidPassword, 15 AccessDenied, 26 Revoked, 27 Expired, 63 AccountLogonDenied, 65 InvalidLoginAuthCode,
    // 71 ExpiredLoginAuthCode, 77 RequirePasswordReEntry, 85 AccountLoginDeniedNeedTwoFactor, 88 TwoFactorCodeMismatch
    static readonly HashSet<int> AuthErrors = new() { 5, 15, 26, 27, 63, 65, 71, 77, 85, 88 };

    async Task<bool> RunAsync(Phase phase, CancellationToken ct)
    {
        try { await RunLock.WaitAsync(ct); }
        catch (OperationCanceledException) { Log($"[sync] {phase}: cancelled while waiting for another sync"); return false; }
        try
        {
            int attempts = phase == Phase.Push ? 2 : 3;
            for (int a = 1; a <= attempts; a++)
            {
                var t = ReadToken(); // re-read every attempt: a renewal in a previous attempt replaced it
                if (t == null) { WriteStatus("error", "token unreadable"); return false; }
                var cli = new SteamCloudClient(Log);
                try
                {
                    int logon = await cli.ConnectAndLogonAsync(t.Value.acct, t.Value.token, t.Value.steamid, ct);
                    if (logon != 1)
                    {
                        if (AuthErrors.Contains(logon))
                        {
                            Log($"[sync] Steam rejected the saved login (eresult={logon}); sign in again");
                            WriteStatus("auth", $"eresult={logon}");
                            Notice.Show(Notice.Ko ? "Steam 클라우드 동기화 중지: 로그인이 만료됐습니다. Mac에서 make login, make provision을 다시 실행하세요."
                                                  : "Steam Cloud sync stopped: the saved login expired. Run make login and make provision on your Mac.");
                            return false;
                        }
                        throw new Exception($"logon eresult={logon}");
                    }
                    try { var nt = await cli.RenewTokenAsync(t.Value.token, t.Value.steamid, ct); if (nt != null) SaveToken(t.Value.acct, nt); } catch { }
                    await Reconcile(cli, phase, ct);
                    WriteStatus("ok", phase.ToString());
                    return true;
                }
                catch (Exception e) when (a < attempts && !ct.IsCancellationRequested)
                {
                    Log($"[sync] {phase} attempt {a}/{attempts} failed: {(e.InnerException ?? e).Message} -> reconnecting");
                }
                catch (Exception e)
                {
                    Log($"[sync] {phase} failed: {(e.InnerException ?? e).Message}");
                    WriteStatus("error", $"{phase}: {(e.InnerException ?? e).Message}");
                    return false;
                }
                finally { cli.Dispose(); }
            }
            return false;
        }
        finally { RunLock.Release(); }
    }

    // Only these paths are Steam Cloud files for this app; anything else (replays, backups, quarantined
    // corrupt files, temp files) is device-local and uploading it is rejected by Steam.
    static readonly Regex CloudFile = new(@"^(profile\.save|profile\d+/saves/[^/]+\.save|profile\d+/saves/history/[^/]+\.run)$");
    static bool IsCloudFile(string rel) => CloudFile.IsMatch(rel);
    static bool IsHistory(string rel) => rel.Contains("/history/");
    static bool IsRunFile(string rel) => Path.GetFileName(rel).StartsWith("current_run");
    static bool InPhase(Phase p, string rel) => p switch { Phase.BootLive => !IsHistory(rel), Phase.BootHistory => IsHistory(rel), _ => true };
    static string LocalPath(string rel) => Path.Combine(SaveRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    sealed class Stats { public int Same, Pulled, Pushed, CloudDeleted, LocalRetired, Kept, Failed; public string FirstFail; }

    // A planned cloud write; executed later inside one upload batch. Raw == null means delete.
    sealed class CloudOp { public string Rel; public byte[] Raw; public string Sha; public bool Forget; }

    // The game keeps only the newest run-history files in Steam Cloud: at most 100 files and 5 MB per profile
    // (SaveManager: OverwriteCloudWithLocalDirectory(historyPath, 5242880, 100)). Older runs are "forgotten"
    // from the cloud but stay on each device. Mirror that exactly, or the phone would re-upload runs the PC
    // trimmed and exceed the quota.
    const int HistoryFileLimit = 100, HistoryByteLimit = 5 * 1024 * 1024;

    // Newest-first (file names are run start timestamps) set of history files that belong in the cloud.
    static HashSet<string> HistoryWindow(IEnumerable<(string rel, long size)> files)
    {
        var keep = new HashSet<string>();
        foreach (var dir in files.GroupBy(f => Path.GetDirectoryName(f.rel)))
        {
            long bytes = 0; int count = 0;
            foreach (var f in dir.OrderByDescending(f => long.TryParse(Path.GetFileNameWithoutExtension(f.rel), out var n) ? n : 0))
            {
                if (count + 1 > HistoryFileLimit || bytes + f.size > HistoryByteLimit) break;
                keep.Add(f.rel); bytes += f.size; count++;
            }
        }
        return keep;
    }

    async Task Reconcile(SteamCloudClient cli, Phase phase, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var resp = await cli.EnumerateAsync(APPID, ct);
        var inPhase = resp.Files.Where(f => IsCloudFile(f.Filename) && InPhase(phase, f.Filename)).ToList();
        var cloud = inPhase.ToDictionary(f => f.Filename, f => (f.FileSha ?? "").ToLowerInvariant());
        var manifest = LoadManifest(out var lastSyncUtc);
        var local = Directory.Exists(SaveRoot)
            ? Directory.GetFiles(SaveRoot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(SaveRoot, p).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(r => IsCloudFile(r) && InPhase(phase, r)).ToHashSet()
            : new HashSet<string>();

        // start times of every run that has a history record (on either side): such a run is over
        _finishedRuns = resp.Files.Where(f => IsHistory(f.Filename)).Select(f => Path.GetFileNameWithoutExtension(f.Filename))
            .Concat(Directory.Exists(SaveRoot) ? Directory.GetFiles(SaveRoot, "*.run", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension) : Enumerable.Empty<string>())
            .ToHashSet();
        // live files first, then run history newest first (file names are unix timestamps)
        var names = cloud.Keys.Union(local).Distinct()
            .OrderBy(r => IsHistory(r) ? 1 : 0).ThenBy(r => IsRunFile(r) ? 0 : 1).ThenByDescending(r => r, StringComparer.Ordinal).ToList();
        var st = new Stats();
        var ops = new List<CloudOp>();
        HashSet<string> window = null;
        if (phase != Phase.BootLive)
        {
            var sizes = inPhase.Where(f => IsHistory(f.Filename)).Select(f => (f.Filename, (long)f.FileSize))
                .Concat(local.Where(r => IsHistory(r) && !cloud.ContainsKey(r)).Select(r => (r, new FileInfo(LocalPath(r)).Length)));
            window = HistoryWindow(sizes);
            foreach (var f in inPhase.Where(f => IsHistory(f.Filename) && !window.Contains(f.Filename)))
                ops.Add(new CloudOp { Rel = f.Filename, Forget = true });   // make room, like the game does
        }
        foreach (var rel in names)
        {
            ct.ThrowIfCancellationRequested();
            if (IsHistory(rel) && window != null && !window.Contains(rel))
            {
                // outside the cloud window: stays on this device only; never pulled back or re-uploaded
                manifest.Remove(rel);
                continue;
            }
            try { await ReconcileFile(cli, phase, rel, cloud.GetValueOrDefault(rel), manifest, lastSyncUtc, st, ops, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested && !IsConnectionFailure(e))
            { st.Failed++; st.FirstFail ??= $"{rel}: {e.Message}"; }
        }
        await ExecuteCloudOps(cli, ops, manifest, st, ct);
        // forget files of this phase that exist neither here nor in the cloud any more
        foreach (var k in manifest.Keys.Where(k => InPhase(phase, k) && !names.Contains(k)).ToList()) manifest.Remove(k);
        SaveManifest(manifest);
        Log($"[sync] {phase}: {names.Count} files in {sw.ElapsedMilliseconds}ms — same {st.Same}, pulled {st.Pulled}, pushed {st.Pushed}, " +
            $"cloud-deleted {st.CloudDeleted}, local-retired {st.LocalRetired}, kept {st.Kept}, failed {st.Failed}{(st.FirstFail != null ? " (" + st.FirstFail + ")" : "")}");
    }

    async Task ReconcileFile(SteamCloudClient cli, Phase phase, string rel, string C, Dictionary<string, string> manifest,
                             DateTime lastSyncUtc, Stats st, List<CloudOp> ops, CancellationToken ct)
    {
        string path = LocalPath(rel);
        byte[] L = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
        if (L != null && !IsValidSave(L))
        {
            // Half-written by the game or not a save at all: never upload it; at boot treat it as missing so
            // the cloud copy replaces it.
            if (phase == Phase.Push) { st.Kept++; return; }
            L = null;
        }
        bool canWriteLocal = phase != Phase.Push;
        if (IsRunFile(rel) && L != null && RunFinished(L))
        {
            // a stale copy of a run that already ended (its history record exists): never upload it, retire it
            if (canWriteLocal && TryWriteLocal(rel, path, null, L)) { st.LocalRetired++; Log($"[sync] {rel}: finished run left on this device -> retired"); }
            else if (!canWriteLocal) { st.Kept++; }
            L = null;
        }
        if (IsRunFile(rel) && !string.IsNullOrEmpty(C) && (L == null || Sha1Hex(L) != C))
        {
            var cloudRun = await cli.DownloadAsync(APPID, rel, ct);
            if (IsValidSave(cloudRun) && RunFinished(cloudRun))
            {
                // the cloud still holds a run that already ended (an earlier delete never went through)
                if (L != null) PlanUpload(ops, rel, L, Sha1Hex(L), null, st);   // this device has a newer, live run
                else { Log($"[sync] {rel}: cloud copy is a finished run -> delete in cloud"); ops.Add(new CloudOp { Rel = rel }); }
                manifest.Remove(rel);
                return;
            }
        }
        string Ls = L == null ? null : Sha1Hex(L);
        manifest.TryGetValue(rel, out var B);
        if (string.IsNullOrEmpty(C)) C = null;

        if (Ls == null && C == null) { manifest.Remove(rel); return; }
        if (Ls != null && Ls == C) { manifest[rel] = C; st.Same++; return; }

        if (Ls == null) // cloud only
        {
            if (IsRunFile(rel) && B != null && B == C && !HasFreshCorrupt(path, lastSyncUtc))
            {
                Log($"[sync] {rel}: run ended on this device -> delete in cloud");
                ops.Add(new CloudOp { Rel = rel });
                return;
            }
            if (!canWriteLocal) { st.Kept++; return; }
            if (await PullInto(cli, rel, path, null, ct)) { manifest[rel] = C; st.Pulled++; } else st.Failed++;
            return;
        }

        if (C == null) // local only
        {
            if (IsHistory(rel) && B != null && B == Ls) { manifest.Remove(rel); st.Kept++; return; } // forgotten by another device
            if (IsRunFile(rel) && B != null && B == Ls)
            {
                // It was in the cloud when we last agreed and nothing changed here since: the run ended on
                // another device. Re-uploading it would resurrect a finished run there.
                if (canWriteLocal && TryWriteLocal(rel, path, null, L)) { manifest.Remove(rel); st.LocalRetired++; Log($"[sync] {rel}: run ended on another device -> local copy retired"); }
                else st.Kept++;
                return;
            }
            PlanUpload(ops, rel, L, Ls, null, st);
            return;
        }

        // both exist and differ
        if (B != null && B == Ls) // only the cloud changed
        {
            if (!canWriteLocal) { st.Kept++; return; }
            if (await PullInto(cli, rel, path, L, ct)) { manifest[rel] = C; st.Pulled++; } else st.Failed++;
            return;
        }

        byte[] cloudBytes = await cli.DownloadAsync(APPID, rel, ct);
        if (!IsValidSave(cloudBytes)) { PlanUpload(ops, rel, L, Ls, null, st); return; }

        Winner w = B != null && B == C ? Winner.Local   // only the local copy changed
                 : Compare(rel, L, cloudBytes);          // both changed, or no common base yet
        if (w == Winner.Local)
        {
            PlanUpload(ops, rel, L, Ls, cloudBytes, st);
            return;
        }
        if (!canWriteLocal) { st.Kept++; return; }
        Backup(rel, L, "local");
        if (TryWriteLocal(rel, path, cloudBytes, L)) { manifest[rel] = C; st.Pulled++; } else st.Kept++;
    }

    async Task<bool> PullInto(SteamCloudClient cli, string rel, string path, byte[] replaced, CancellationToken ct)
    {
        var bytes = await cli.DownloadAsync(APPID, rel, ct);
        if (!IsValidSave(bytes)) { Log($"[sync] {rel}: cloud copy is not a valid save, left alone"); return false; }
        if (replaced != null) Backup(rel, replaced, "local");
        return TryWriteLocal(rel, path, bytes, replaced);
    }

    // Local writes: live files only while the boot window is open (the game has not read them yet).
    // bytes == null retires the file (moved to backups).
    static bool TryWriteLocal(string rel, string path, byte[] bytes, byte[] current)
    {
        lock (WriteLock)
        {
            if (!IsHistory(rel) && _liveWindowClosed) { Log($"[sync] {rel}: not written, game already loaded its saves"); return false; }
            if (bytes == null) { if (current != null) Backup(rel, current, "retired"); try { File.Delete(path); } catch { return false; } return true; }
            AtomicWrite(path, bytes);
            return true;
        }
    }

    void PlanUpload(List<CloudOp> ops, string rel, byte[] raw, string sha, byte[] cloudBytes, Stats st)
    {
        if (Path.GetFileName(rel) == "progress.save" && cloudBytes != null && !ProgressCovers(raw, cloudBytes, out var missing))
        {
            // The cloud has unlocks this copy lacks: typically a newer game build on another device whose
            // content this build dropped when it loaded the save. Uploading would erase them.
            Log($"[sync] progress.save not uploaded: cloud has {missing} this device lacks (newer game version elsewhere?)");
            Backup(rel, raw, "unpushed");
            st.Kept++;
            return;
        }
        if (cloudBytes != null) Backup(rel, cloudBytes, "cloud");
        ops.Add(new CloudOp { Rel = rel, Raw = raw, Sha = sha });
    }

    // Runs the planned writes inside one app upload batch and always completes it, so an interrupted push
    // does not leave pending operations behind (they make Steam refuse writes with TooManyPending).
    async Task ExecuteCloudOps(SteamCloudClient cli, List<CloudOp> ops, Dictionary<string, string> manifest, Stats st, CancellationToken ct)
    {
        if (ops.Count == 0) return;
        ulong batch;
        // forgets first so the quota has room for the uploads
        ops = ops.OrderBy(o => o.Forget ? 0 : 1).ToList();
        try { batch = await cli.BeginBatchAsync(APPID, ops.Where(o => o.Raw != null).Select(o => o.Rel), ops.Where(o => o.Raw == null).Select(o => o.Rel), ct); }
        catch (TooManyPendingException e) { Log($"[sync] {ops.Count} cloud writes postponed: {e.Message}"); st.Kept += ops.Count; return; }
        int result = 1;
        try
        {
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (op.Raw == null) { await cli.DeleteAsync(APPID, op.Rel, batch, ct, op.Forget); manifest.Remove(op.Rel); if (!op.Forget) st.CloudDeleted++; }
                    else if (await cli.UploadAsync(APPID, op.Rel, op.Raw, batch, ct)) { manifest[op.Rel] = op.Sha; st.Pushed++; }
                    else { st.Failed++; st.FirstFail ??= $"{op.Rel}: not committed"; }
                }
                catch (TooManyPendingException e)
                {
                    result = 108;
                    Log($"[sync] stopping cloud writes: {e.Message}");
                    st.Kept += ops.Count - ops.IndexOf(op);
                    break;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && !IsConnectionFailure(e)) { st.Failed++; st.FirstFail ??= $"{op.Rel}: {e.Message}"; }
            }
        }
        catch { result = 2; throw; }
        finally
        {
            // close the batch even when this run is being cancelled
            try { using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await cli.CompleteBatchAsync(APPID, batch, result, done.Token); }
            catch (Exception e) { Log($"[sync] could not complete batch {batch}: {e.Message}"); }
        }
    }

    // A quarantined copy (game renamed an unreadable save to *.corrupt) since the last sync means the file is
    // missing because it broke, not because the run ended.
    static bool HasFreshCorrupt(string path, DateTime lastSyncUtc)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path) + ".";
            return Directory.Exists(dir) && new DirectoryInfo(dir).GetFiles(stem + "*corrupt").Any(f => f.LastWriteTimeUtc >= lastSyncUtc);
        }
        catch { return true; }
    }

    static Dictionary<string, string> LoadManifest(out DateTime lastSyncUtc)
    {
        var d = new Dictionary<string, string>();
        lastSyncUtc = DateTime.MinValue;
        try
        {
            if (!File.Exists(ManifestPath)) return d;
            lastSyncUtc = File.GetLastWriteTimeUtc(ManifestPath);
            foreach (var line in File.ReadAllLines(ManifestPath))
            {
                var i = line.IndexOf('\t'); if (i != 40) continue;   // skips legacy timestamp-format lines
                d[line.Substring(41)] = line.Substring(0, 40);
            }
        }
        catch (Exception e) { Log($"[sync] manifest read failed: {e.Message}"); }
        return d;
    }

    // Runs are serialized (RunLock), so the manifest loaded at the start of a run is still current here.
    static void SaveManifest(Dictionary<string, string> m)
    {
        try { AtomicWrite(ManifestPath, System.Text.Encoding.UTF8.GetBytes(string.Join("\n", m.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Value}\t{kv.Key}")) + "\n")); }
        catch (Exception e) { Log($"[sync] manifest write failed: {e.Message}"); }
    }

    enum Winner { Cloud, Local, Equal }

    Winner Compare(string rel, byte[] local, byte[] cloud)
    {
        string name = Path.GetFileName(rel);
        try
        {
            if (name == "progress.save") return CompareProgress(local, cloud);
            if (name.StartsWith("current_run")) return CompareRun(local, cloud);
            if (name.EndsWith(".run")) return Winner.Local; // finished-run records never change after writing
        }
        catch { }
        return Winner.Equal; // profile/prefs with no common base: cloud wins at boot, nothing pushed
    }

    Winner CompareProgress(byte[] l, byte[] c)
    {
        using var dl = JsonDocument.Parse(l); using var dc = JsonDocument.Parse(c);
        var a = dl.RootElement; var b = dc.RootElement;
        int cmp;
        if ((cmp = CmpLong(Num(a, "floors_climbed"), Num(b, "floors_climbed"))) != 0) return W(cmp);
        if ((cmp = CmpLong(WinsLosses(a), WinsLosses(b))) != 0) return W(cmp);
        if ((cmp = CmpLong(DiscoveredCount(a), DiscoveredCount(b))) != 0) return W(cmp);
        if ((cmp = CmpLong(Num(a, "total_playtime"), Num(b, "total_playtime"))) != 0) return W(cmp);
        return Winner.Equal;
    }

    // Different runs (start_time differs): the newer run wins. Same run: the later save wins.
    Winner CompareRun(byte[] l, byte[] c)
    {
        using var dl = JsonDocument.Parse(l); using var dc = JsonDocument.Parse(c);
        var a = dl.RootElement; var b = dc.RootElement;
        int cmp;
        if ((cmp = CmpLong(Num(a, "start_time"), Num(b, "start_time"))) != 0) return W(cmp);
        if ((cmp = CmpLong(Num(a, "save_time"), Num(b, "save_time"))) != 0) return W(cmp);
        return W(CmpLong(Floors(a), Floors(b)));
    }

    // true if `local` contains every unlock recorded in `cloud` (so uploading loses nothing)
    static bool ProgressCovers(byte[] local, byte[] cloud, out string missing)
    {
        missing = null;
        try
        {
            using var dl = JsonDocument.Parse(local); using var dc = JsonDocument.Parse(cloud);
            foreach (var k in DiscoveredKeys)
            {
                var have = Ids(dl.RootElement, k);
                var lack = Ids(dc.RootElement, k).Where(x => !have.Contains(x)).ToList();
                if (lack.Count > 0) { missing = $"{lack.Count} {k} (e.g. {lack[0]})"; return false; }
            }
            if (WinsLosses(dl.RootElement) < WinsLosses(dc.RootElement)) { missing = "more recorded runs"; return false; }
            return true;
        }
        catch { return true; }
    }

    static readonly string[] DiscoveredKeys = { "discovered_cards", "discovered_relics", "discovered_potions", "discovered_events", "discovered_acts" };
    static HashSet<string> Ids(JsonElement e, string key)
    {
        var s = new HashSet<string>();
        if (e.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray()) s.Add(x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText());
        return s;
    }

    static long WinsLosses(JsonElement e)
    {
        long s = 0;
        if (e.TryGetProperty("character_stats", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var st in cs.EnumerateArray()) s += Num(st, "wins") + Num(st, "losses");
        if (e.TryGetProperty("ancient_stats", out var an) && an.ValueKind == JsonValueKind.Array)
            foreach (var a in an.EnumerateArray())
                if (a.TryGetProperty("character_stats", out var acs) && acs.ValueKind == JsonValueKind.Array)
                    foreach (var st in acs.EnumerateArray()) s += Num(st, "wins") + Num(st, "losses");
        return s;
    }
    static long DiscoveredCount(JsonElement e) => DiscoveredKeys.Sum(k => e.TryGetProperty(k, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.GetArrayLength() : 0);
    static long Floors(JsonElement e)
    {
        if (e.TryGetProperty("map_point_history", out var mph) && mph.ValueKind == JsonValueKind.Array)
        { long s = 0; foreach (var act in mph.EnumerateArray()) if (act.ValueKind == JsonValueKind.Array) s += act.GetArrayLength(); return s; }
        if (e.TryGetProperty("acts", out var acts) && acts.ValueKind == JsonValueKind.Array) return acts.GetArrayLength();
        return 0;
    }
    static long Num(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
    static int CmpLong(long a, long b) => a > b ? 1 : a < b ? -1 : 0;
    static Winner W(int cmp) => cmp > 0 ? Winner.Local : cmp < 0 ? Winner.Cloud : Winner.Equal;

    static string Sha1Hex(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b)).ToLowerInvariant();

    HashSet<string> _finishedRuns = new();

    // A run is over once the game has written its history record, named after the run's start time.
    bool RunFinished(byte[] run)
    {
        try
        {
            using var d = JsonDocument.Parse(run);
            long start = Num(d.RootElement, "start_time");
            return start > 0 && _finishedRuns.Contains(start.ToString());
        }
        catch { return false; }
    }

    // The CM connection is gone: abort the whole run so RunAsync reconnects, instead of failing file by file.
    static bool IsConnectionFailure(Exception e) =>
        e is TimeoutException || e is IOException || e is ObjectDisposedException || e is System.Net.WebSockets.WebSocketException;

    static bool IsValidSave(byte[] b)
    {
        if (b == null || b.Length == 0 || SteamCloudClient.IsZip(b)) return false;
        try { using var _ = JsonDocument.Parse(b); return true; } catch { return false; }
    }

    // Keeps the last 20 copies per file name under user://save-backups/<file>/.
    static void Backup(string rel, byte[] bytes, string src)
    {
        if (bytes == null || IsHistory(rel)) return;
        try
        {
            var dir = Path.Combine(BackupDir, rel.Replace('/', '_'));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{src}.bak"), bytes);
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.bak").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(20)) f.Delete();
        }
        catch { }
    }

    static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".sync.tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
        { fs.Write(bytes, 0, bytes.Length); fs.Flush(true); }
        File.Move(tmp, path, true);
    }
}
