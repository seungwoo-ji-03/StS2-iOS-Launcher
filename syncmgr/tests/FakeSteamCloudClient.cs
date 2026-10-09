// In-memory stand-in for SteamCloudClient with the same public surface.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamCloudProto;

public sealed class TooManyPendingException : Exception { public TooManyPendingException(string w) : base(w) { } }

public sealed class SteamCloudClient : IDisposable
{
    public static bool Busy;                 // simulate Steam answering writes with TooManyPending (108)
    public static int OpenBatches, BatchesBegun;
    public static readonly Dictionary<string, byte[]> Cloud = new();
    public static readonly List<string> Calls = new();
    public static int LogonResult = 1;
    public SteamCloudClient(Action<string> log = null) { }

    public Task<int> ConnectAndLogonAsync(string a, string t, ulong s, CancellationToken ct) => Task.FromResult(LogonResult);
    public Task<string> RenewTokenAsync(string t, ulong s, CancellationToken ct) => Task.FromResult<string>(null);
    public Task<CCloud_EnumerateUserFiles_Response> EnumerateAsync(uint appid, CancellationToken ct)
    {
        var r = new CCloud_EnumerateUserFiles_Response();
        foreach (var kv in Cloud) r.Files.Add(new CCloud_UserFile { Filename = kv.Key, FileSha = Sha(kv.Value), Timestamp = 1, FileSize = (uint)kv.Value.Length });
        r.TotalFiles = (uint)r.Files.Count;
        return Task.FromResult(r);
    }
    public Task<byte[]> DownloadAsync(uint appid, string f, CancellationToken ct) { Calls.Add("down " + f); return Task.FromResult(Cloud[f]); }
    public Task<ulong> BeginBatchAsync(uint appid, IEnumerable<string> up, IEnumerable<string> del, CancellationToken ct)
    { if (Busy) throw new TooManyPendingException("begin"); OpenBatches++; BatchesBegun++; return Task.FromResult((ulong)BatchesBegun); }
    public Task CompleteBatchAsync(uint appid, ulong batch, int eresult, CancellationToken ct) { OpenBatches--; Calls.Add($"complete {eresult}"); return Task.CompletedTask; }
    public Task<bool> UploadAsync(uint appid, string f, byte[] raw, ulong batch, CancellationToken ct)
    { if (batch == 0) throw new Exception("upload outside a batch"); Calls.Add("up " + f); Cloud[f] = raw; return Task.FromResult(true); }
    public Task DeleteAsync(uint appid, string f, ulong batch, CancellationToken ct, bool forget = false)
    { if (batch == 0) throw new Exception("delete outside a batch"); Calls.Add((forget ? "forget " : "del ") + f); Cloud.Remove(f); return Task.CompletedTask; }
    public static bool IsZip(byte[] b) => b != null && b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;
    public void Dispose() { }
    static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b)).ToLowerInvariant();
}
