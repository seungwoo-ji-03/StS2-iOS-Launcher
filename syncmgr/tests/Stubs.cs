// Stand-in for Notice, which needs the real engine, not the shim.
public static class Notice { public static bool Ko => false; public static void Show(string t, float s = 7f) { } }
