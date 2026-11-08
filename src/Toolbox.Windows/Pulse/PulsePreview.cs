using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>
/// A small desktop with the dock on its edge, as the settings place it: used on the Pulse page
/// (it follows every change) and on the Dashboard's card.
/// </summary>
internal sealed class PulsePreview : Grid, IIdleMotion
{
    private readonly Border _desk = new() { CornerRadius = new CornerRadius(12), ClipToBounds = true };
    private readonly Grid _stage = new();

    public PulsePreview(double height = 132)
    {
        Height = height;
        FlowDirection = FlowDirection.LeftToRight; // screen edges are physical: right stays right in Arabic
        _desk.Child = _stage;
        Children.Add(_desk);
    }

    public void SetRunning(bool running) { } // still: it only follows the settings

    public void Update(PulseSettings s)
    {
        bool dark = ThemeResources.Lap.IsDark;
        _desk.Background = dark
            ? new LinearGradientBrush(Color.FromRgb(0x14, 0x1B, 0x3C), Color.FromRgb(0x3C, 0x15, 0x32), 30)
            : new LinearGradientBrush(Color.FromRgb(0xC3, 0xD2, 0xF6), Color.FromRgb(0xF2, 0xD3, 0xE2), 30);
        _stage.Children.Clear();

        // A window on the desktop, and the taskbar.
        var window = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(dark ? Color.FromArgb(0xE6, 0x0D, 0x11, 0x17) : Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(40, 20, 120, 34) };
        _stage.Children.Add(window);
        var taskbar = new Border { Height = 14, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(dark ? Color.FromArgb(0xCC, 0x1E, 0x1F, 0x26) : Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF)) };
        _stage.Children.Add(taskbar);

        string edge = s.Edge is "left" or "top" or "bottom" ? s.Edge : "right";
        bool vertical = edge is "left" or "right";
        double k = PulseSettings.ScaleOf(s.Size), tile = 15 * k * Math.Clamp(Height / 132, 1, 1.35);
        var strip = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
        foreach (var id in s.ActiveTiles.Take(8))
        {
            var face = new Grid { Width = tile, Height = tile, Margin = new Thickness(2) };
            if (PulseTiles.HasRing(id))
            {
                face.Children.Add(new Ellipse { StrokeThickness = 2, Stroke = new SolidColorBrush(id == PulseTiles.Codex ? PulseTileView.Amber : PulseTileView.Green) });
            }
            else
            {
                var square = new Border { CornerRadius = new CornerRadius(4 * k) };
                square.SetResourceReference(Border.BackgroundProperty, "Brush.Line2");
                face.Children.Add(square);
            }
            face.Children.Add(SettingsPage.Glyph(DockWindow.IconOf(id), tile * 0.6, "Brush.Text", stroke: 2.2));
            strip.Children.Add(face);
        }
        var dock = new Border
        {
            Child = strip,
            Padding = vertical ? new Thickness(3, 5, 3, 5) : new Thickness(5, 3, 5, 3),
            Background = new SolidColorBrush(dark ? Color.FromArgb(0xD0, 0x2A, 0x2B, 0x32) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = edge switch
            {
                "left" => new CornerRadius(0, 10, 10, 0),
                "right" => new CornerRadius(10, 0, 0, 10),
                "top" => new CornerRadius(0, 0, 10, 10),
                _ => new CornerRadius(10, 10, 0, 0),
            },
            HorizontalAlignment = edge switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Left },
            VerticalAlignment = edge switch { "top" => VerticalAlignment.Top, "bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Top },
            Opacity = s.Show == "fade" ? 0.55 : 1,
        };
        dock.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        _stage.Children.Add(dock);

        // Along the edge where the settings put it (the taskbar is not part of the edge).
        _stage.SizeChanged -= OnStageSize;
        _stage.SizeChanged += OnStageSize;
        _stage.Tag = (dock, s.Position, edge, s.Show == "slide");
        Place();
    }

    private void OnStageSize(object sender, SizeChangedEventArgs e) => Place();

    private void Place()
    {
        if (_stage.Tag is not ValueTuple<Border, double, string, bool> t || _stage.ActualWidth <= 0) return;
        var (dock, pos, edge, slide) = t;
        dock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = dock.DesiredSize;
        double w = _stage.ActualWidth, h = _stage.ActualHeight - 14;
        bool vertical = edge is "left" or "right";
        double along = vertical ? Math.Clamp(pos * h - d.Height / 2, 0, Math.Max(0, h - d.Height)) : Math.Clamp(pos * w - d.Width / 2, 0, Math.Max(0, w - d.Width));
        double slideOff = slide ? (vertical ? d.Width : d.Height) - 4 : 0;
        dock.Margin = edge switch
        {
            "left" => new Thickness(-slideOff, along, 0, 0),
            "right" => new Thickness(0, along, -slideOff, 0),
            "top" => new Thickness(along, -slideOff, 0, 0),
            _ => new Thickness(along, 0, 0, 14 - slideOff),
        };
    }
}
