using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>Pieces of the cards that open beside a tile on hover.</summary>
internal static class PulseCards
{
    public const double Width = 272;

    public static Border Card(params UIElement[] parts)
    {
        var stack = new StackPanel();
        foreach (var p in parts) stack.Children.Add(p);
        var card = new Border { Width = Width, Padding = new Thickness(15, 14, 15, 14), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = stack, IsHitTestVisible = false };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = ThemeResources.Lap.IsDark ? 0.55 : 0.18, Color = Colors.Black };
        return card;
    }

    public static FrameworkElement Head(string icon, string title, string? chip = null, FrameworkElement? trailing = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var glyph = SettingsPage.Glyph(icon, 17, "Brush.Text", stroke: 1.9);
        glyph.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        if (trailing is not null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            row.Children.Add(trailing);
        }
        else if (chip is not null)
        {
            var c = SettingsPage.Badge(chip);
            c.Height = 20;
            DockPanel.SetDock(c, Dock.Right);
            row.Children.Add(c);
        }
        row.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text")));
        return row;
    }

    /// <summary>"Current session · resets in 1 h 12 min", a bar, and "64% used · 36% left".</summary>
    public static FrameworkElement Meter(string title, UsageWindow? window, DateTimeOffset now)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var head = new DockPanel();
        var when = new TextBlock { Text = window is null ? "" : ResetText(window.ResetsAt, now), FontSize = 11.5 };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        DockPanel.SetDock(when, Dock.Right);
        head.Children.Add(when);
        head.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text")));
        stack.Children.Add(head);

        double used = window?.UsedAt(now) ?? 0;
        var track = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 4), ClipToBounds = true };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3) }.With(b => b.SetResourceReference(Border.BackgroundProperty, "Brush.LapTrack")));
        var colour = Usage.LevelOf(used) switch { UsageLevel.Hot => PulseTileView.Red, UsageLevel.Warn => PulseTileView.Amber, _ => PulseTileView.Green };
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(colour), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill);
        track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * used / 100;
        stack.Children.Add(track);

        var value = new TextBlock
        {
            Text = window is null ? Lang.T("Not known yet", "غير معروف بعد") : Lang.T($"{used:0}% used · {100 - used:0}% left", $"‏{used:0}% مستخدم · {100 - used:0}% متبقٍ"),
            FontSize = 11.5,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        stack.Children.Add(value);
        return stack;
    }

    /// <summary>A coloured dot and a line ("Working · Amir_Lab").</summary>
    public static FrameworkElement Status(Color dot, string text)
    {
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 12, 0, 0) };
        row.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(dot), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Width - 70 }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text")));
        row.Child = line;
        return row;
    }

    public static TextBlock Note(string text, double top = 10)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), LineHeight = 16 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        return t;
    }

    public static TextBlock Body(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 18 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        return t;
    }

    /// <summary>A dashed box ("Drop a file here").</summary>
    public static FrameworkElement DropBox(string text)
    {
        var grid = new Grid { Height = 60, Margin = new Thickness(0, 10, 0, 0) };
        var dash = new Rectangle { RadiusX = 12, RadiusY = 12, StrokeThickness = 1.5, StrokeDashArray = [4, 3] };
        dash.SetResourceReference(Shape.StrokeProperty, "Brush.Line2");
        grid.Children.Add(dash);
        grid.Children.Add(new TextBlock { Text = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3")));
        return grid;
    }

    /// <summary>"resets in 1 h 12 min", "resets Thu 00:00", or "reset 5 min ago".</summary>
    public static string ResetText(DateTimeOffset? resets, DateTimeOffset now)
    {
        if (resets is not { } r) return "";
        var left = r - now;
        if (left <= TimeSpan.Zero) return Lang.T("has reset", "تجدّد");
        if (left < TimeSpan.FromHours(1)) return Lang.T($"resets in {Math.Max(1, (int)left.TotalMinutes)} min", $"يتجدّد بعد {Math.Max(1, (int)left.TotalMinutes)} د");
        if (left < TimeSpan.FromHours(24))
            return Lang.T($"resets in {(int)left.TotalHours} h {left.Minutes} min", $"يتجدّد بعد {(int)left.TotalHours} س {left.Minutes} د");
        var local = r.ToLocalTime();
        var culture = Lang.IsArabic ? CultureInfo.GetCultureInfo("ar") : CultureInfo.GetCultureInfo("en-GB");
        return Lang.T("resets ", "يتجدّد ") + local.ToString("ddd HH:mm", culture);
    }

    /// <summary>"12 min ago" style text for when numbers were read.</summary>
    public static string Age(DateTimeOffset? when, DateTimeOffset now) => when is { } w ? Lang.Ago(w, now) : "";

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? Lang.T($"{(int)t.TotalHours} h {t.Minutes} min", $"{(int)t.TotalHours} س {t.Minutes} د")
        : Lang.T($"{t.Minutes} min {t.Seconds} s", $"{t.Minutes} د {t.Seconds} ث");
}
