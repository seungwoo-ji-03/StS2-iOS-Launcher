// Scenario tests for SyncManager's three-way reconcile. Each case builds a device-vs-cloud situation, runs
// one real sync phase, and checks both sides. Exit code = number of failed checks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;

static class Program
{
    static int _fail, _pass;
    static readonly List<string> Logs = new();
    const string PROG = "profile1/saves/progress.save", RUN = "profile1/saves/current_run.save",
                 PREFS = "profile1/saves/prefs.save", PROF = "profile.save", HIST = "profile1/saves/history/1790000000.run";

    static string Progress(int floors, int wins, params string[] cards) =>
        $"{{\"floors_climbed\":{floors},\"character_stats\":[{{\"wins\":{wins},\"losses\":0}}],\"discovered_cards\":[{string.Join(",", cards.Select(c => "\"" + c + "\""))}],\"total_playtime\":{floors * 10}}}";
    static string Run(long start, long save) => $"{{\"start_time\":{start},\"save_time\":{save},\"map_point_history\":[[{{}},{{}}]]}}";
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    static string Root => Path.Combine(ProjectSettings.UserDir, "default", "1");
    static string LocalFile(string rel) => Path.Combine(Root, rel);
    static string Local(string rel) => File.Exists(LocalFile(rel)) ? File.ReadAllText(LocalFile(rel)) : null;
    static void SetLocal(string rel, string content) { Directory.CreateDirectory(Path.GetDirectoryName(LocalFile(rel))); if (content == null) File.Delete(LocalFile(rel)); else File.WriteAllText(LocalFile(rel), content); }
    static string Cloud(string rel) => SteamCloudClient.Cloud.TryGetValue(rel, out var b) ? Encoding.UTF8.GetString(b) : null;
    static void SetCloud(string rel, string content) { if (content == null) SteamCloudClient.Cloud.Remove(rel); else SteamCloudClient.Cloud[rel] = B(content); }

    static readonly Type SM = typeof(SyncManager);
    static void SetStatic(string f, object v) => SM.GetField(f, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, v);

    static void Reset()
    {
        ProjectSettings.UserDir = Path.Combine(Path.GetTempPath(), "synctests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ProjectSettings.UserDir);
        SteamCloudClient.Cloud.Clear(); SteamCloudClient.Calls.Clear(); SteamCloudClient.LogonResult = 1; Logs.Clear();
        SteamCloudClient.Busy = false; SteamCloudClient.OpenBatches = 0; SteamCloudClient.BatchesBegun = 0;
        string payload = Convert.ToBase64String(B("{\"sub\":\"76561190000000001\",\"exp\":4102444800}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(Path.Combine(ProjectSettings.UserDir, "steam_token.json"), $"{{\"AccountName\":\"test\",\"RefreshToken\":\"x.{payload}.y\"}}");
        SetStatic("_liveWindowClosed", false);
    }

    static bool Run(string phase)
    {
        var ph = Enum.Parse(SM.GetNestedType("Phase", BindingFlags.NonPublic), phase);
        var m = SM.GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        return ((Task<bool>)m.Invoke(new SyncManager(), new[] { ph, (object)CancellationToken.None })).GetAwaiter().GetResult();
    }
    static int RunAndCountBatches() { int b = SteamCloudClient.BatchesBegun; Run("Push"); return SteamCloudClient.BatchesBegun - b; }
    static void Boot() { Run("BootLive"); SetStatic("_liveWindowClosed", true); }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine($"  FAIL {name} {detail}"); foreach (var l in Logs.TakeLast(6)) Console.WriteLine("     | " + l); }
    }
    static bool BackupExists(string rel, string kind) =>
        Directory.Exists(Path.Combine(ProjectSettings.UserDir, "save-backups", rel.Replace('/', '_'))) &&
        Directory.GetFiles(Path.Combine(ProjectSettings.UserDir, "save-backups", rel.Replace('/', '_')), $"*.{kind}.bak").Any();

    // both sides agree on everything -> manifest records the common base
    static void Baseline(params (string rel, string content)[] files)
    {
        foreach (var (rel, c) in files) { SetLocal(rel, c); SetCloud(rel, c); }
        Run("BootLive"); Run("BootHistory"); SetStatic("_liveWindowClosed", false); SteamCloudClient.Calls.Clear();
    }

    static void Case(string name, Action body) { Console.WriteLine(name); Reset(); try { body(); } catch (Exception e) { _fail++; Console.WriteLine($"  FAIL threw {e.InnerException ?? e}"); } }

    static int Main()
    {
        GD.Sink = s => Logs.Add(s);

        Case("S1 played on PC, phone unchanged -> boot pulls before the game loads", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetCloud(PROG, Progress(20, 2, "A", "B"));
            Boot();
            Check("local = cloud", Local(PROG) == Cloud(PROG));
            Check("old local backed up", BackupExists(PROG, "local"));
        });

        Case("S2 played on phone, cloud unchanged -> push uploads", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetLocal(PROG, Progress(15, 2, "A", "C"));
            Run("Push");
            Check("cloud = local", Cloud(PROG) == Local(PROG));
        });

        Case("S3 both changed, phone lacks cloud unlocks (older build) -> not uploaded", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetCloud(PROG, Progress(12, 2, "A", "NEW_BETA_CARD"));
            SetLocal(PROG, Progress(30, 3, "A", "C"));
            Run("Push");
            Check("cloud untouched", Cloud(PROG) == Progress(12, 2, "A", "NEW_BETA_CARD"));
            Check("phone copy kept aside", BackupExists(PROG, "unpushed"));
        });

        Case("S3b both changed, phone is a superset -> uploaded, cloud copy backed up", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetCloud(PROG, Progress(12, 2, "A", "B"));
            SetLocal(PROG, Progress(30, 3, "A", "B", "C"));
            Run("Push");
            Check("cloud = local", Cloud(PROG) == Local(PROG));
            Check("cloud backed up", BackupExists(PROG, "cloud"));
        });

        Case("S4 run ended on phone -> push deletes the cloud run", () =>
        {
            Baseline((RUN, Run(100, 110)));
            SetLocal(RUN, null);
            Run("Push");
            Check("cloud run gone", Cloud(RUN) == null, string.Join(",", SteamCloudClient.Calls));
            Run("BootLive");
            Check("not resurrected on next boot", Local(RUN) == null);
        });

        Case("S5 run ended on PC -> phone retires its stale copy instead of re-uploading", () =>
        {
            Baseline((RUN, Run(100, 110)));
            SetCloud(RUN, null);
            Run("BootLive");
            Check("local run retired", Local(RUN) == null);
            Check("retired copy backed up", BackupExists(RUN, "retired"));
            Check("cloud still has no run", Cloud(RUN) == null);
        });

        Case("S5b run ended on PC, phone kept playing it -> both changed, newer save kept", () =>
        {
            Baseline((RUN, Run(100, 110)));
            SetCloud(RUN, null);
            SetLocal(RUN, Run(100, 150));
            Run("Push");
            Check("phone's continued run uploaded", Cloud(RUN) == Run(100, 150));
        });

        Case("S6 phone has no run, PC started a new one -> pulled, not deleted", () =>
        {
            Baseline((RUN, Run(100, 110)));
            SetLocal(RUN, null); Run("Push");        // phone run ended -> cloud cleared
            SetCloud(RUN, Run(200, 210));             // new run on PC
            Run("BootLive");
            Check("new PC run pulled", Local(RUN) == Run(200, 210));
            Check("cloud kept", Cloud(RUN) == Run(200, 210));
        });

        Case("S7 run quarantined as corrupt by the game -> restored from cloud, not deleted", () =>
        {
            Baseline((RUN, Run(100, 110)));
            SetLocal(RUN, null);
            Thread.Sleep(1100);
            File.WriteAllText(LocalFile("profile1/saves/current_run.1790000001.JSN.corrupt"), "garbage");
            Run("BootLive");
            Check("cloud run kept", Cloud(RUN) == Run(100, 110), string.Join(",", SteamCloudClient.Calls));
            Check("local restored", Local(RUN) == Run(100, 110));
        });

        Case("S8 game already loaded saves -> no live file written", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetCloud(PROG, Progress(20, 2, "A", "B"));
            SetStatic("_liveWindowClosed", true);
            Run("BootLive");
            Check("local untouched", Local(PROG) == Progress(10, 1, "A"));
        });

        Case("S9 push never writes local files", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetCloud(PROG, Progress(20, 2, "A", "B"));
            Run("Push");
            Check("local untouched", Local(PROG) == Progress(10, 1, "A"));
            Check("cloud untouched", Cloud(PROG) == Progress(20, 2, "A", "B"));
        });

        Case("S10 zip left by an old build locally -> replaced from cloud", () =>
        {
            SetCloud(PROG, Progress(10, 1, "A"));
            Directory.CreateDirectory(Path.GetDirectoryName(LocalFile(PROG)));
            File.WriteAllBytes(LocalFile(PROG), new byte[] { 0x50, 0x4B, 3, 4, 9, 9 });
            Boot();
            Check("local is the cloud save", Local(PROG) == Progress(10, 1, "A"));
        });

        Case("S11 prefs changed on phone -> pushed", () =>
        {
            Baseline((PREFS, "{\"fast_mode\":0}"), (PROF, "{\"last_profile\":1}"));
            SetLocal(PREFS, "{\"fast_mode\":2}");
            Run("Push");
            Check("cloud prefs updated", Cloud(PREFS) == "{\"fast_mode\":2}");
        });

        Case("S12 manifest from the old timestamp format is ignored (no deletion)", () =>
        {
            SetCloud(RUN, Run(100, 110));
            File.WriteAllText(Path.Combine(ProjectSettings.UserDir, "sync_manifest.txt"), "1790000000\tprofile1/saves/current_run.save\n");
            Run("BootLive");
            Check("cloud run kept", Cloud(RUN) != null);
            Check("pulled", Local(RUN) == Run(100, 110));
        });

        Case("S13 expired login -> nothing touched, status says auth", () =>
        {
            SetCloud(PROG, Progress(20, 2, "A"));
            SteamCloudClient.LogonResult = 15;
            Check("run reports failure", !Run("BootLive"));
            Check("nothing pulled", Local(PROG) == null);
            Check("status auth", File.ReadAllText(Path.Combine(ProjectSettings.UserDir, "sync_status.txt")).StartsWith("auth"));
        });

        Case("S14 device-local files are never uploaded", () =>
        {
            SetLocal("settings.save", "{}"); SetLocal("profile1/replays/latest.mcr", "{}");
            SetLocal(PROG + ".backup", "{}"); SetLocal("profile1/saves/current_run.1.JSN.corrupt", "{}");
            Run("Push");
            Check("no uploads", !SteamCloudClient.Calls.Any(c => c.StartsWith("up ")), string.Join(",", SteamCloudClient.Calls));
        });

        Case("S15 run history: new phone run uploaded, PC runs downloaded after the live window", () =>
        {
            SetLocal(HIST, "{\"win\":true}");
            SetCloud("profile1/saves/history/1790000999.run", "{\"win\":false}");
            SetStatic("_liveWindowClosed", true);
            Run("BootHistory");
            Check("phone run uploaded", Cloud(HIST) == "{\"win\":true}");
            Check("PC run downloaded", Local("profile1/saves/history/1790000999.run") == "{\"win\":false}");
        });

        Case("S16 half-written save during push -> skipped, never uploaded", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetLocal(PROG, "{\"floors_climbed\":1");
            Run("Push");
            Check("cloud untouched", Cloud(PROG) == Progress(10, 1, "A"));
        });

        Case("S17 idempotent: second sync with nothing changed does nothing", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")), (RUN, Run(1, 2)), (PREFS, "{}"), (HIST, "{}"));
            Run("Push"); Run("BootLive");
            Check("no transfers", !SteamCloudClient.Calls.Any(), string.Join(",", SteamCloudClient.Calls));
        });

        Case("S18 Steam busy (TooManyPending) -> nothing lost, retried next time", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")), (RUN, Run(100, 110)));
            SetLocal(PROG, Progress(15, 2, "A", "C"));
            SetLocal(RUN, null);
            SteamCloudClient.Busy = true;
            Run("Push");
            Check("cloud untouched while busy", Cloud(PROG) == Progress(10, 1, "A") && Cloud(RUN) == Run(100, 110));
            SteamCloudClient.Busy = false;
            Run("Push");
            Check("progress pushed on retry", Cloud(PROG) == Local(PROG));
            Check("ended run deleted on retry", Cloud(RUN) == null);
        });

        Case("S19 every batch that is opened is completed", () =>
        {
            Baseline((PROG, Progress(10, 1, "A")));
            SetLocal(PROG, Progress(11, 1, "A")); SetLocal(HIST, "{}");
            Run("Push");
            Check("one batch", SteamCloudClient.BatchesBegun == 1, $"begun {SteamCloudClient.BatchesBegun}");
            Check("closed", SteamCloudClient.OpenBatches == 0, $"open {SteamCloudClient.OpenBatches}");
            Check("no batch when nothing to write", RunAndCountBatches() == 0);
        });

        Case("S20 cloud history full: new phone runs uploaded, oldest cloud runs forgotten, cap kept", () =>
        {
            for (int i = 1; i <= 100; i++) { var r = $"profile1/saves/history/{1700000000 + i}.run"; SetLocal(r, "{\"r\":" + i + "}"); SetCloud(r, "{\"r\":" + i + "}"); }
            Run("BootHistory");
            for (int i = 101; i <= 103; i++) SetLocal($"profile1/saves/history/{1700000000 + i}.run", "{\"r\":" + i + "}");
            Run("Push");
            int n = SteamCloudClient.Cloud.Keys.Count(k => k.Contains("/history/"));
            Check("cloud stays at 100", n == 100, $"n={n}");
            Check("newest uploaded", Cloud("profile1/saves/history/1700000103.run") != null);
            Check("oldest forgotten", Cloud("profile1/saves/history/1700000001.run") == null);
            Check("oldest kept on the phone", Local("profile1/saves/history/1700000001.run") != null);
            Check("forget, not delete", SteamCloudClient.Calls.Any(c => c.StartsWith("forget ")) && !SteamCloudClient.Calls.Any(c => c.StartsWith("del ")));
        });

        Case("S21 run forgotten by the PC is not re-uploaded", () =>
        {
            Baseline((HIST, "{\"r\":1}"));
            SetCloud(HIST, null);          // PC trimmed it
            Run("Push");
            Check("not re-uploaded", Cloud(HIST) == null, string.Join(",", SteamCloudClient.Calls));
            Check("still on the phone", Local(HIST) != null);
        });

        Case("S22 old phone-only runs outside the cloud window are not uploaded", () =>
        {
            for (int i = 1; i <= 100; i++) SetCloud($"profile1/saves/history/{1790000000 + i}.run", "{\"r\":" + i + "}");
            SetLocal("profile1/saves/history/1600000000.run", "{\"old\":1}");
            Run("Push");
            Check("old run not uploaded", Cloud("profile1/saves/history/1600000000.run") == null);
            Check("no forgets needed", !SteamCloudClient.Calls.Any(c => c.StartsWith("forget ")), string.Join(",", SteamCloudClient.Calls.Take(3)));
        });

        Case("S23 cloud still holds a finished run, no manifest -> deleted in cloud, not pulled", () =>
        {
            SetCloud(RUN, Run(1790264676, 1790266020));
            SetCloud("profile1/saves/history/1790264676.run", "{\"win\":false}");
            Run("BootLive");
            Check("not pulled", Local(RUN) == null);
            Check("deleted in cloud", Cloud(RUN) == null, string.Join(",", SteamCloudClient.Calls));
        });

        Case("S24 stale finished run on the device and in the cloud -> both removed", () =>
        {
            SetLocal("profile1/saves/history/1790264676.run", "{\"win\":false}");
            SetLocal(RUN, Run(1790264676, 1790266020)); SetCloud(RUN, Run(1790264676, 1790266020));
            Run("BootLive");
            Check("local retired", Local(RUN) == null);
            Check("cloud deleted", Cloud(RUN) == null);
        });

        Case("S25 cloud holds a finished run, device has a new live run -> device run uploaded", () =>
        {
            SetCloud(RUN, Run(1790264676, 1790266020)); SetCloud("profile1/saves/history/1790264676.run", "{}");
            SetLocal(RUN, Run(1790300000, 1790300100));
            Run("Push");
            Check("new run in cloud", Cloud(RUN) == Run(1790300000, 1790300100));
            Check("local kept", Local(RUN) == Run(1790300000, 1790300100));
        });

        Case("S26 a live run (no history record) is untouched by the rule", () =>
        {
            SetCloud(RUN, Run(1790300000, 1790300100));
            SetCloud("profile1/saves/history/1790264676.run", "{}");
            Run("BootLive");
            Check("pulled as usual", Local(RUN) == Run(1790300000, 1790300100));
        });

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail;
    }
}
