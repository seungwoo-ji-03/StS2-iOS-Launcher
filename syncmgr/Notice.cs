// A short, non-blocking on-screen message (top of the screen, fades after a few seconds). Safe to call from
// any thread; shown on the main thread.
using Godot;

public partial class Notice : CanvasLayer
{
    public static bool Ko => OS.GetLocaleLanguage() == "ko";

    public static void Show(string text, float seconds = 7f)
    {
        Callable.From(() =>
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            var n = new Notice { Layer = 128 };
            tree.Root.AddChild(n);
            n.Build(text, seconds);
        }).CallDeferred();
    }

    void Build(string text, float seconds)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.78f), ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 10, ContentMarginBottom = 10 };
        style.SetCornerRadiusAll(10);
        panel.AddThemeStyleboxOverride("panel", style);
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(760, 0) };
        label.AddThemeFontSizeOverride("font_size", 26);
        panel.AddChild(label);
        panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.Position += new Vector2(0, 24);
        AddChild(panel);
        var tw = CreateTween();
        tw.TweenInterval(seconds);
        tw.TweenProperty(panel, "modulate:a", 0f, 0.6f);
        tw.TweenCallback(Callable.From(QueueFree));
    }
}
