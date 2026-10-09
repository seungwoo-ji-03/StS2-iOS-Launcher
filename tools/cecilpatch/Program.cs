// Rewrites the game's sts2.dll for iOS: iOS AOT cannot run Harmony/MonoMod and has no Steam
// client, so those paths are stubbed and the launcher's own hooks are injected.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    static ModuleDefinition _module;
    static IAssemblyResolver _resolver;
    static string _inputPath;

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: cecilpatch <--scan|--patch|--game> <sts2.dll> [out.dll]"); return 1; }
        string mode = args[0], input = args[1];
        string output = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? input;
        bool wantSync = args.Contains("--sync");
        bool wantMp = args.Contains("--mp");
        string joinIp = args.FirstOrDefault(a => a.StartsWith("--mp-join="))?.Substring("--mp-join=".Length);
        string joinNetId = args.FirstOrDefault(a => a.StartsWith("--mp-netid="))?.Substring("--mp-netid=".Length);
        string mpName = args.FirstOrDefault(a => a.StartsWith("--mp-name="))?.Substring("--mp-name=".Length);
        string fpsDefault = args.FirstOrDefault(a => a.StartsWith("--fps-default="))?.Substring("--fps-default=".Length);
        bool wantMpConfig = args.Contains("--mp-config");
        bool wantQuitExit = args.Contains("--quit-exit");
        _inputPath = input;

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));
        _resolver = resolver;
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
        var asm = AssemblyDefinition.ReadAssembly(input, rp);
        _module = asm.MainModule;

        // Desktop bridge build: only inject Bridge.Boot() and leave Steam init and everything else intact.
        // Bridge.Boot() itself does nothing unless the game is launched with --bridge.
        if (mode == "--inject-bridge")
        {
            var ip = FindMethod("MegaCrit.Sts2.Core.Nodes.NGame", "InitializePlatform");
            if (ip != null && PrependBridgeBoot(ip)) Console.WriteLine("[bridge] injected Bridge.Boot() at InitializePlatform");
            else Console.WriteLine("[bridge] WARN: InitializePlatform not found (found=" + (ip != null) + ")");
            asm.Write(output);
            Console.WriteLine("written: " + output);
            return 0;
        }

        bool ReferencesHarmony(MethodDefinition m)
        {
            if (!m.HasBody) return false;
            foreach (var i in m.Body.Instructions)
            {
                if (i.Operand is MethodReference mr && (mr.DeclaringType?.FullName?.StartsWith("HarmonyLib.") == true || mr.DeclaringType?.FullName?.StartsWith("MonoMod.") == true)) return true;
                if (i.Operand is TypeReference tr && (tr.FullName.StartsWith("HarmonyLib.") || tr.FullName.StartsWith("MonoMod."))) return true;
                if (i.Operand is FieldReference fr && (fr.DeclaringType?.FullName?.StartsWith("HarmonyLib.") == true || fr.DeclaringType?.FullName?.StartsWith("MonoMod.") == true)) return true;
            }
            return false;
        }

        var hits = new List<MethodDefinition>();
        foreach (var t in _module.GetTypes())
            foreach (var m in t.Methods)
                if (ReferencesHarmony(m)) hits.Add(m);

        Console.WriteLine("=== methods referencing HarmonyLib/MonoMod: " + hits.Count + " ===");
        foreach (var m in hits) Console.WriteLine("  " + m.DeclaringType.FullName + "::" + m.Name);
        if (mode == "--scan") return 0;

        int stubbed = 0;
        foreach (var m in hits) { if (StubNoOp(m)) stubbed++; }
        Console.WriteLine("=== Harmony refs stubbed: " + stubbed + " ===");

        if (mode == "--game")
        {
            // iOS has no Steam client, so platform init is replaced with an immediate success.
            var initPlat = FindMethod("MegaCrit.Sts2.Core.Nodes.NGame", "InitializePlatform");
            if (initPlat != null && StubToTaskTrue(initPlat))
                Console.WriteLine("[game] Steam skip: NGame.InitializePlatform -> Task.FromResult(true)");
            else
                Console.WriteLine("[game] WARN: InitializePlatform not patched (found=" + (initPlat != null) + ")");

            if (wantSync && initPlat != null && PrependSyncBoot(initPlat))
                Console.WriteLine("[game] sync: injected SyncManager.Boot() at InitializePlatform");

            if (wantMp)
            {
                // Force fastmp=true only inside the multiplayer menus so they pick the ENet transport.
                // Forcing it globally (NGame.GameStartup, leaderboards) breaks startup.
                var mpTypes = new[] {
                    "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NJoinFriendScreen",
                    "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu",
                    "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerHostSubmenu",
                };
                int patched = 0;
                foreach (var t in _module.GetTypes())
                {
                    var tn = t.FullName;
                    bool inMp = mpTypes.Any(x => tn == x || tn.StartsWith(x + "/")); // include nested async state machines
                    if (!inMp) continue;
                    foreach (var m in t.Methods)
                        if (m.HasBody) patched += ForceFastMpCallsTrue(m);
                }
                Console.WriteLine(patched > 0
                    ? $"[game] mp: forced fastmp=true at {patched} MP-menu call site(s) (ENet LAN transport)"
                    : "[game] mp: WARN no fastmp call sites patched");
            }

            // FastMpJoin always connects to 127.0.0.1; point it at the host or bridge address instead.
            if (joinIp != null)
            {
                int n = 0;
                foreach (var t in _module.GetTypes().Where(t => t.Name.Contains("FastMpJoin") && t.Name.Contains("d__")))
                    foreach (var m in t.Methods.Where(m => m.Name == "MoveNext" && m.HasBody))
                        foreach (var i in m.Body.Instructions)
                            if (i.OpCode == OpCodes.Ldstr && (string)i.Operand == "127.0.0.1") { i.Operand = joinIp; n++; }
                Console.WriteLine(n > 0 ? $"[game] mp-join: FastMpJoin target 127.0.0.1 -> {joinIp}" : "[game] mp-join: WARN 127.0.0.1 literal not found");
            }

            // FastMpJoin proposes clientId 1000. Through the bridge it must match the bridge's SteamID,
            // which is the id the Steam host assigns, so player references agree on both sides.
            if (joinNetId != null && ulong.TryParse(joinNetId, out var nid))
            {
                int n = 0;
                foreach (var t in _module.GetTypes().Where(t => t.Name.Contains("FastMpJoin") && t.Name.Contains("d__")))
                    foreach (var m in t.Methods.Where(m => m.Name == "MoveNext" && m.HasBody))
                    {
                        var ins = m.Body.Instructions;
                        for (int i = 0; i + 1 < ins.Count; i++)
                            if (ins[i].OpCode == OpCodes.Ldc_I4 && Convert.ToInt32(ins[i].Operand) == 1000 && ins[i + 1].OpCode == OpCodes.Conv_I8)
                            {
                                ins[i].OpCode = OpCodes.Ldc_I8; ins[i].Operand = (long)nid;
                                ins[i + 1].OpCode = OpCodes.Nop; ins[i + 1].Operand = null;
                                n++;
                            }
                    }
                Console.WriteLine(n > 0 ? $"[game] mp-netid: FastMpJoin clientId 1000 -> {nid}" : "[game] mp-netid: WARN clientId literal not found");
            }

            // Route the baked-in IP and netId through MpConfig so user://mp_config.json can override
            // them without a rebuild. This runs after --mp-join/--mp-netid, so their values become the
            // fallbacks.
            if (wantMpConfig)
            {
                var ipRef = ImportSyncmgrMethod("MpConfig", "Ip", 1);
                var netRef = ImportSyncmgrMethod("MpConfig", "NetId", 1);
                int nIp = 0, nNet = 0;
                foreach (var t in _module.GetTypes().Where(t => t.Name.Contains("FastMpJoin") && t.Name.Contains("d__")))
                    foreach (var m in t.Methods.Where(m => m.Name == "MoveNext" && m.HasBody))
                    {
                        var ins = m.Body.Instructions;
                        var il = m.Body.GetILProcessor();
                        // ldstr <ip>  ->  ldstr <ip>; call MpConfig.Ip
                        if (ipRef != null)
                            for (int i = 0; i < ins.Count; i++)
                                if (ins[i].OpCode == OpCodes.Ldstr && ins[i].Operand is string s &&
                                    (s == "127.0.0.1" || System.Net.IPAddress.TryParse(s, out _)))
                                { il.InsertAfter(ins[i], il.Create(OpCodes.Call, ipRef)); nIp++; break; }
                        // ldc.i8 <netId>  ->  ldc.i8 <netId>; call MpConfig.NetId
                        if (netRef != null)
                            for (int i = 0; i < ins.Count; i++)
                                if (ins[i].OpCode == OpCodes.Ldc_I8)
                                { il.InsertAfter(ins[i], il.Create(OpCodes.Call, netRef)); nNet++; break; }
                    }
                Console.WriteLine($"[game] mp-config: routed IP({nIp})/netId({nNet}) through MpConfig (user://mp_config.json overrides)");
            }

            // Without Steam, GetPlayerName falls back to "Test Host"/"Test Client N"/<netId> for every
            // player. Override it only when playerId == GetLocalPlayerId(), so the local player gets
            // mpName whether hosting or joining, and remote players keep their networked names.
            if (mpName != null)
            {
                var t = _module.GetTypes().FirstOrDefault(x => x.Name == "NullPlatformUtilStrategy");
                var m = t?.Methods.FirstOrDefault(x => x.Name == "GetPlayerName" && x.HasBody);
                var getLocal = t?.Methods.FirstOrDefault(x => x.Name == "GetLocalPlayerId" && x.Parameters.Count == 0);
                if (m != null && getLocal != null)
                {
                    var il = m.Body.GetILProcessor();
                    var first = m.Body.Instructions.First();
                    var ldName = il.Create(OpCodes.Ldstr, mpName);
                    il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));                 // playerId
                    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));                 // this
                    il.InsertBefore(first, il.Create(OpCodes.Call, getLocal));          // this.GetLocalPlayerId()
                    il.InsertBefore(first, il.Create(OpCodes.Bne_Un, first));           // playerId != local -> original
                    il.InsertBefore(first, ldName);                                     // == -> return mpName
                    il.InsertBefore(first, il.Create(OpCodes.Ret));
                    Console.WriteLine($"[game] mp-name: GetPlayerName returns \"{mpName}\" for the local player (host or client), original fallback for others");
                }
                else Console.WriteLine("[game] mp-name: WARN GetPlayerName/GetLocalPlayerId not found");
            }

            // The Compatibility renderer (GLES over Metal) runs hot at 60 fps on iOS, so lower the
            // SettingsSave constructor's default FpsLimit. Players can still change it in settings.
            if (fpsDefault != null && int.TryParse(fpsDefault, out var fpsN))
            {
                int n = 0;
                var ss = _module.GetTypes().FirstOrDefault(x => x.Name == "SettingsSave");
                foreach (var c in ss?.Methods.Where(m => m.IsConstructor && m.HasBody) ?? Enumerable.Empty<MethodDefinition>())
                {
                    var ins = c.Body.Instructions;
                    for (int i = 0; i + 1 < ins.Count; i++)
                        if (ins[i + 1].Operand is FieldReference fr && fr.Name == "<FpsLimit>k__BackingField"
                            && (ins[i].OpCode == OpCodes.Ldc_I4_S || ins[i].OpCode == OpCodes.Ldc_I4))
                        { ins[i].OpCode = OpCodes.Ldc_I4; ins[i].Operand = fpsN; n++; }
                }
                Console.WriteLine(n > 0 ? $"[game] fps-default: SettingsSave.FpsLimit default -> {fpsN} ({n} ctor site(s))" : "[game] fps-default: WARN default not found");
            }

            foreach (var (type, name) in new[] {
                ("MegaCrit.Sts2.Core.Debug.OsDebugInfo", "LogSystemInfo"),
            })
            {
                var mm = FindMethod(type, name);
                if (mm != null && StubNoOp(mm)) Console.WriteLine("[game] no-op: " + type + "::" + name);
                else Console.WriteLine("[game] skip (not found): " + type + "::" + name);
            }

            // SceneTree.Quit does not terminate an iOS app, so call SyncManager.QuitSync() just before it
            // in NGame.Quit. Saves have already been written at that point; QuitSync pushes them to the
            // cloud and exits the process. It never returns, and the eval stack stays balanced either way.
            if (wantQuitExit)
            {
                var quitRef = ImportSyncmgrMethod("SyncManager", "QuitSync", 0);
                var ngameQuit = FindMethod("MegaCrit.Sts2.Core.Nodes.NGame", "Quit");
                if (quitRef != null && ngameQuit != null)
                {
                    var il = ngameQuit.Body.GetILProcessor();
                    var target = ngameQuit.Body.Instructions.FirstOrDefault(i =>
                        (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call)
                        && i.Operand is MethodReference mr && mr.Name == "Quit"
                        && mr.DeclaringType?.Name == "SceneTree");
                    if (target != null) { il.InsertBefore(target, il.Create(OpCodes.Call, quitRef)); Console.WriteLine("[game] quit-exit: injected SyncManager.QuitSync() before NGame.Quit -> SceneTree.Quit"); }
                    else Console.WriteLine("[game] quit-exit: WARN SceneTree.Quit call not found in NGame.Quit");
                }
                else Console.WriteLine($"[game] quit-exit: WARN missing quitRef={quitRef != null} NGame.Quit={ngameQuit != null}");
            }

            // LogResourceStats reports the managed heap (GC.GetTotalMemory) instead of OS.GetStaticMemoryUsage.
            var logStats = FindMethod("MegaCrit.Sts2.Core.Nodes.NGame", "LogResourceStats");
            if (logStats != null && SwapStaticMemWithGC(logStats))
                Console.WriteLine("[memdiag] LogResourceStats: OS.GetStaticMemoryUsage -> GC.GetTotalMemory");
            else
                Console.WriteLine("[memdiag] WARN: LogResourceStats not patched (found=" + (logStats != null) + ")");
        }

        asm.Write(output);
        Console.WriteLine("written: " + output);
        return 0;
    }

    static MethodDefinition FindMethod(string typeFullName, string methodName)
    {
        var t = _module.GetTypes().FirstOrDefault(x => x.FullName == typeFullName);
        return t?.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody);
    }

    static bool StubNoOp(MethodDefinition m)
    {
        if (!m.HasBody) return false;
        var il = m.Body.GetILProcessor();
        m.Body.Instructions.Clear();
        m.Body.ExceptionHandlers.Clear();
        m.Body.Variables.Clear();
        var rt = m.ReturnType;
        if (rt.FullName != "System.Void")
        {
            if (rt.IsValueType || rt.IsGenericParameter)
            {
                var v = new VariableDefinition(rt);
                m.Body.Variables.Add(v);
                m.Body.InitLocals = true;
                il.Append(il.Create(OpCodes.Ldloca_S, v));
                il.Append(il.Create(OpCodes.Initobj, rt));
                il.Append(il.Create(OpCodes.Ldloc_0));
            }
            else il.Append(il.Create(OpCodes.Ldnull));
        }
        il.Append(il.Create(OpCodes.Ret));
        return true;
    }

    static bool SwapStaticMemWithGC(MethodDefinition m)
    {
        if (!m.HasBody) return false;
        MethodReference gcRef = null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_inputPath));
        foreach (var name in new[] { "System.Private.CoreLib.dll", "mscorlib.dll", "System.Runtime.dll" })
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            try
            {
                var a = AssemblyDefinition.ReadAssembly(p, new ReaderParameters { AssemblyResolver = _resolver });
                var t = a.MainModule.GetType("System.GC");
                var mm = t?.Methods.FirstOrDefault(x => x.Name == "GetTotalMemory" && x.Parameters.Count == 1);
                if (mm != null) { gcRef = _module.ImportReference(mm); break; }
            }
            catch { }
        }
        if (gcRef == null) return false;
        var il = m.Body.GetILProcessor();
        foreach (var ins in m.Body.Instructions.ToList())
        {
            if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                && ins.Operand is MethodReference mr
                && mr.Name == "GetStaticMemoryUsage")
            {
                var ldFalse = il.Create(OpCodes.Ldc_I4_0);
                il.InsertBefore(ins, ldFalse);
                ins.OpCode = OpCodes.Call;
                ins.Operand = gcRef;
                return true;
            }
        }
        return false;
    }

    static bool StubToTaskTrue(MethodDefinition m)
    {
        TypeDefinition taskDef = null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_inputPath));
        foreach (var name in new[] { "System.Private.CoreLib.dll", "System.Threading.Tasks.dll", "mscorlib.dll", "System.Runtime.dll" })
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            try
            {
                var a = AssemblyDefinition.ReadAssembly(p, new ReaderParameters { AssemblyResolver = _resolver });
                var t = a.MainModule.GetType("System.Threading.Tasks.Task");
                if (t != null && t.Methods.Any(x => x.Name == "FromResult" && x.HasGenericParameters)) { taskDef = t; break; }
            }
            catch { }
        }
        if (taskDef == null) return false;
        var fromResult = taskDef.Methods.FirstOrDefault(x => x.Name == "FromResult" && x.HasGenericParameters && x.Parameters.Count == 1);
        if (fromResult == null) return false;

        var fromResultRef = _module.ImportReference(fromResult);
        var gim = new GenericInstanceMethod(fromResultRef);
        gim.GenericArguments.Add(_module.TypeSystem.Boolean);

        var il = m.Body.GetILProcessor();
        m.Body.Instructions.Clear();
        m.Body.ExceptionHandlers.Clear();
        m.Body.Variables.Clear();
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Call, gim));
        il.Append(il.Create(OpCodes.Ret));
        return true;
    }

    // Replaces `ldstr "fastmp"; call HasArg` with `nop; ldc.i4.1` in one method.
    static int ForceFastMpCallsTrue(MethodDefinition m)
    {
        int n = 0;
        var ins = m.Body.Instructions;
        for (int i = 1; i < ins.Count; i++)
        {
            if ((ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt)
                && ins[i].Operand is MethodReference mr && mr.Name == "HasArg"
                && ins[i - 1].OpCode == OpCodes.Ldstr && (string)ins[i - 1].Operand == "fastmp")
            {
                ins[i - 1].OpCode = OpCodes.Nop; ins[i - 1].Operand = null;
                ins[i].OpCode = OpCodes.Ldc_I4_1; ins[i].Operand = null;
                n++;
            }
        }
        return n;
    }

    // Imports a static method from syncmgr.dll next to the input assembly.
    static MethodReference ImportSyncmgrMethod(string typeName, string methodName, int paramCount)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_inputPath));
        var sp = Path.Combine(dir, "syncmgr.dll");
        if (!File.Exists(sp)) { Console.WriteLine($"[game] WARN syncmgr.dll not found for {typeName}.{methodName}"); return null; }
        try
        {
            var a = AssemblyDefinition.ReadAssembly(sp, new ReaderParameters { AssemblyResolver = _resolver });
            var t = a.MainModule.GetType(typeName);
            var mm = t?.Methods.FirstOrDefault(x => x.Name == methodName && x.IsStatic && x.Parameters.Count == paramCount);
            if (mm == null) { Console.WriteLine($"[game] WARN {typeName}.{methodName}/{paramCount} not in syncmgr.dll"); return null; }
            return _module.ImportReference(mm);
        }
        catch (Exception e) { Console.WriteLine($"[game] WARN import {typeName}.{methodName} failed: {e.Message}"); return null; }
    }

    // Prepends `call SyncManager.Boot()` (syncmgr.dll). If syncmgr.dll is absent, a reference is built by name.
    static bool PrependSyncBoot(MethodDefinition m)
    {
        if (!m.HasBody) return false;
        MethodReference bootRef = null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_inputPath));
        var sp = Path.Combine(dir, "syncmgr.dll");
        if (File.Exists(sp))
        {
            try
            {
                var a = AssemblyDefinition.ReadAssembly(sp, new ReaderParameters { AssemblyResolver = _resolver });
                var t = a.MainModule.GetType("SyncManager");
                var mm = t?.Methods.FirstOrDefault(x => x.Name == "Boot" && x.IsStatic && x.Parameters.Count == 0);
                if (mm != null) bootRef = _module.ImportReference(mm);
            }
            catch { }
        }
        if (bootRef == null)
        {
            var an = _module.AssemblyReferences.FirstOrDefault(r => r.Name == "syncmgr") ?? new AssemblyNameReference("syncmgr", new Version(1, 0, 0, 0));
            if (!_module.AssemblyReferences.Contains(an)) _module.AssemblyReferences.Add(an);
            var tr = new TypeReference("", "SyncManager", _module, an);
            bootRef = new MethodReference("Boot", _module.TypeSystem.Void, tr) { HasThis = false };
        }
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions.FirstOrDefault();
        var call = il.Create(OpCodes.Call, bootRef);
        if (first != null) il.InsertBefore(first, call); else il.Append(call);
        return true;
    }

    // Prepends `call Bridge.Boot()` (bridgemgr.dll). If bridgemgr.dll is absent, a reference is built by name.
    static bool PrependBridgeBoot(MethodDefinition m)
    {
        if (!m.HasBody) return false;
        MethodReference bootRef = null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_inputPath));
        var bp = Path.Combine(dir, "bridgemgr.dll");
        if (File.Exists(bp))
        {
            try
            {
                var a = AssemblyDefinition.ReadAssembly(bp, new ReaderParameters { AssemblyResolver = _resolver });
                var t = a.MainModule.GetType("Bridge");
                var mm = t?.Methods.FirstOrDefault(x => x.Name == "Boot" && x.IsStatic && x.Parameters.Count == 0);
                if (mm != null) bootRef = _module.ImportReference(mm);
            }
            catch { }
        }
        if (bootRef == null)
        {
            var an = _module.AssemblyReferences.FirstOrDefault(r => r.Name == "bridgemgr") ?? new AssemblyNameReference("bridgemgr", new Version(1, 0, 0, 0));
            if (!_module.AssemblyReferences.Contains(an)) _module.AssemblyReferences.Add(an);
            var tr = new TypeReference("", "Bridge", _module, an);
            bootRef = new MethodReference("Boot", _module.TypeSystem.Void, tr) { HasThis = false };
        }
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions.FirstOrDefault();
        var call = il.Create(OpCodes.Call, bootRef);
        if (first != null) il.InsertBefore(first, call); else il.Append(call);
        return true;
    }
}
