// Just enough of the Godot API for SyncManager.cs to compile and run on the desktop.
using System;
using System.IO;
namespace Godot
{
    public class MainLoop { }
    public class SceneTree : MainLoop { public Node Root = new Node(); }
    public class Node
    {
        public const long NotificationApplicationPaused = 1015;
        public enum ProcessModeEnum { Inherit, Pausable, WhenPaused, Always, Disabled }
        public ProcessModeEnum ProcessMode { get; set; }
        public static class MethodName { public static readonly string AddChild = "add_child"; }
        public virtual void _Ready() { }
        public virtual void _Notification(int what) { }
        public void CallDeferred(string method, params object[] args) { }
    }
    public class FileAccess { }   // exists in Godot; keeps name clashes with System.IO visible in tests
    public class Semaphore { }
    public class Mutex { }
    public static class Engine { public static MainLoop GetMainLoop() => null; }
    public static class GD { public static Action<string> Sink = Console.WriteLine; public static void Print(params object[] a) => Sink(string.Concat(a)); }
    public static class Time { public static string GetDatetimeStringFromSystem() => DateTime.Now.ToString("s"); }
    public static class ProjectSettings
    {
        public static string UserDir = Path.GetTempPath();
        public static string GlobalizePath(string p) => p.StartsWith("user://") ? Path.Combine(UserDir, p.Substring(7)) : p;
    }
}
