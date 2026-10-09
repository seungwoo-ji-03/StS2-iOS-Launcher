// Minimal iOS runtime calls via the Objective-C runtime (no ObjC source file, no binding library).
// Everything degrades to a no-op off iOS or if a call fails, so callers never need a platform check.
using System;
using System.Runtime.InteropServices;

static class IosNative
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nuint MsgBeginTask(IntPtr receiver, IntPtr selector, IntPtr handler);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void MsgEndTask(IntPtr receiver, IntPtr selector, nuint task);
    [DllImport(LibSystem, EntryPoint = "_exit")] static extern void SysExit(int code);

    public static bool IsIos => OperatingSystem.IsIOS();

    // UIApplication.beginBackgroundTaskWithExpirationHandler:(nil). iOS then keeps the process running
    // (~30s) after it moves to the background, so a push can finish instead of being frozen mid-upload.
    // Returns 0 (UIBackgroundTaskInvalid) when unavailable. Call from the main thread.
    public static nuint BeginBackgroundTask()
    {
        if (!IsIos) return 0;
        try
        {
            var app = Msg(objc_getClass("UIApplication"), sel_registerName("sharedApplication"));
            if (app == IntPtr.Zero) return 0;
            return MsgBeginTask(app, sel_registerName("beginBackgroundTaskWithExpirationHandler:"), IntPtr.Zero);
        }
        catch { return 0; }
    }

    // Must be called for every task begun, before iOS's deadline, or the app is terminated. Thread-safe.
    public static void EndBackgroundTask(nuint task)
    {
        if (!IsIos || task == 0) return;
        try
        {
            var app = Msg(objc_getClass("UIApplication"), sel_registerName("sharedApplication"));
            if (app != IntPtr.Zero) MsgEndTask(app, sel_registerName("endBackgroundTask:"), task);
        }
        catch { }
    }

    // Terminate without running C++ static destructors / atexit handlers. The game has saved and the push is
    // done by the time this is called; a normal exit() would tear down the engine while FMOD threads still run.
    public static void HardExit(int code)
    {
        try { SysExit(code); } catch { }
        Environment.Exit(code);
    }
}
