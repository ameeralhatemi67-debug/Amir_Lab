using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Home;

/// <summary>One thing to try first: a tool's icon, what to do, and its keys (if any).</summary>
internal sealed record WelcomeTip(string Icon, string Title, string How, IReadOnlyList<string> Keys);

/// <summary>
/// Shown when the installer starts Amir Lap: it is installed (or updated) and running, where it
/// lives (the notification area: a tray app with no window of its own on screen confuses people,
/// who ran the installer again and again), the first things to try with their real shortcuts, and
/// whether to start it with Windows. Before this, "Run with PowerShell" opened and closed a
/// console and nothing else happened.
/// </summary>
internal sealed class WelcomeWindow : GlassWindow
{
    public WelcomeWindow(bool updated, string version, IReadOnlyList<WelcomeTip> tips, bool startsWithWindows, Action<bool> setStartWithWindows, Action openHome)
    {
        Title = updated ? "Amir Lap is updated" : "Amir Lap is installed";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22), FlowDirection = Lang.Flow };

        // The app, and that it worked.
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 16) };
        var icon = new Image { Source = AppIcon.Render(128, ThemeResources.Lap.Accent), Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var check = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 10, 0), Background = new SolidColorBrush(Color.FromRgb(0x22, 0xA6, 0x5A)), Child = SettingsPage.Glyph("lap-check", 13, brush: Brushes.White, stroke: 2.6).With(g => g.FlowDirection = FlowDirection.LeftToRight), VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(check);
        titleRow.Children.Add(new TextBlock
        {
            Text = updated ? Lang.T("Amir Lap is updated", "تم تحديث أمير لاب") : Lang.T("Amir Lap is installed", "تم تثبيت أمير لاب"),
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titles.Children.Add(titleRow);
        titles.Children.Add(new TextBlock
        {
            Text = Lang.T($"Version {version} · ready to use", $"الإصدار {version} · جاهز للاستخدام"),
            Style = SettingsPage.Res("Lap.Help"),
            FontSize = 13,
            Margin = new Thickness(34, 3, 0, 0),
        });
        head.Children.Add(titles);
        MakeDragHandle(head);
        root.Children.Add(head);

        // Where it is: it has no window of its own once this closes.
        var where = new DockPanel();
        var tray = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 12, 0), Child = SettingsPage.Glyph("lap-timer", 19, "Brush.Accent"), VerticalAlignment = VerticalAlignment.Top };
        tray.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tray, Dock.Left);
        where.Children.Add(tray);
        var whereText = new StackPanel();
        whereText.Children.Add(new TextBlock { Text = Lang.T("It's running now", "إنه يعمل الآن"), FontWeight = FontWeights.SemiBold, FontSize = 14 });
        whereText.Children.Add(new TextBlock
        {
            Text = Lang.T(
                "Amir Lap lives in the notification area, by the clock (bottom right; it may be under the ^ arrow). Click its icon to open it, right-click it for every tool. It's in the Start Menu too.",
                "يعيش أمير لاب في منطقة الإشعارات بجانب الساعة (أسفل اليمين، وقد يكون تحت السهم ^). انقر أيقونته لفتحه، وانقر بزر الفأرة الأيمن لكل الأدوات. وهو في قائمة ابدأ أيضًا."),
            TextWrapping = TextWrapping.Wrap,
            Style = SettingsPage.Res("Lap.Help"),
            FontSize = 13,
            Margin = new Thickness(0, 3, 0, 0),
        });
        where.Children.Add(whereText);
        var whereCard = new Border { Child = where, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        whereCard.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        whereCard.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        root.Children.Add(whereCard);

        // The first things to try.
        if (tips.Count > 0)
        {
            root.Children.Add(new TextBlock { Text = Lang.T("Try it", "جرّبه"), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(2, 18, 0, 8) });
            foreach (var tip in tips) root.Children.Add(TipRow(tip));
        }

        // Start with Windows: off unless chosen (a tray app that is gone after a restart is the next confusion).
        var startRow = new DockPanel { Margin = new Thickness(2, 14, 0, 0) };
        var toggle = SettingsPage.Switch(startsWithWindows, setStartWithWindows);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(toggle, Dock.Right);
        startRow.Children.Add(toggle);
        var startText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        startText.Children.Add(new TextBlock { Text = Lang.T("Start Amir Lap with Windows", "تشغيل أمير لاب مع ويندوز"), FontSize = 13.5 });
        startText.Children.Add(new TextBlock { Text = Lang.T("So its shortcuts work after a restart. You can change this in its settings.", "لتعمل اختصاراته بعد إعادة التشغيل. يمكنك تغيير ذلك من إعداداته."), Style = SettingsPage.Res("Lap.Help"), TextWrapping = TextWrapping.Wrap });
        startRow.Children.Add(startText);
        System.Windows.Automation.AutomationProperties.SetName(toggle, Lang.T("Start Amir Lap with Windows", "تشغيل أمير لاب مع ويندوز"));
        root.Children.Add(startRow);

        // What next.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        GotIt = SettingsPage.Button(Lang.T("Got it", "حسنًا"), Close);
        GotIt.Margin = new Thickness(0, 0, 8, 0);
        OpenButton = SettingsPage.Button(Lang.T("Open Amir Lap", "فتح أمير لاب"), () => { Close(); openHome(); }, accent: true);
        buttons.Children.Add(GotIt);
        buttons.Children.Add(OpenButton);
        root.Children.Add(buttons);

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        Content = border;
    }

    internal Button OpenButton { get; }
    internal Button GotIt { get; }

    private static FrameworkElement TipRow(WelcomeTip tip)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var tile = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), Child = SettingsPage.Glyph(tip.Icon, 17, "Brush.Accent").With(g => g.FlowDirection = FlowDirection.LeftToRight), VerticalAlignment = VerticalAlignment.Top };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);
        if (tip.Keys.Count > 0)
        {
            var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 2, 0, 0), FlowDirection = FlowDirection.LeftToRight };
            foreach (var k in tip.Keys)
            {
                var chip = new Border { Padding = new Thickness(7, 2, 7, 3), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1, 1, 1, 2), Margin = new Thickness(4, 0, 0, 4), Child = new TextBlock { Text = k, FontSize = 12, FontWeight = FontWeights.SemiBold } };
                chip.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
                chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
                keys.Children.Add(chip);
            }
            DockPanel.SetDock(keys, Dock.Right);
            row.Children.Add(keys);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = tip.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
        text.Children.Add(new TextBlock { Text = tip.How, Style = SettingsPage.Res("Lap.Help"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        row.Children.Add(text);
        return row;
    }
}
