using System.Windows;
using System.Windows.Controls;
using Toolbox.Core.Sound;
using Toolbox.Windows.Home;
using Toolbox.Windows.Pulse;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Sound;

/// <summary>
/// The Sound tile's card on Pulse's dock: a quick mixer. The main output (switchable when there are
/// a few), and the apps playing now with a slider each, so the volume can be changed right there.
/// It stays open while the pointer is on it, and updates in place.
/// </summary>
internal sealed class SoundCard
{
    private const int MaxRows = 5;

    private readonly ISoundControl _control;
    private readonly Action _openMixer;
    private readonly StackPanel _rows = new();
    private readonly ContentControl _outputs = new();
    private readonly TextBlock _chip;
    private readonly TextBlock _empty;
    private readonly TextBlock _more;
    private readonly Dictionary<string, AppRow> _byKey = new(StringComparer.Ordinal);
    private string _outputsKey = "";

    public SoundCard(ISoundControl control, Action openMixer)
    {
        _control = control;
        _openMixer = openMixer;
        var open = SoundViews.IconButton("lap-open", Lang.T("Open the mixer", "فتح المازج"), openMixer, 26);
        _chip = SoundViews.Text("", 11.5, "Brush.Text3");
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _chip.Margin = new Thickness(0, 0, 6, 0);
        trailing.Children.Add(_chip);
        trailing.Children.Add(open);
        _empty = PulseCards.Body(Lang.T("Nothing is playing right now.", "لا شيء يعمل الآن."));
        _more = PulseCards.Note("", 6);
        Element = PulseCards.Card(
            PulseCards.Head("lap-mixer", Lang.T("Imp Studio", "استوديو إمب"), trailing: trailing),
            _outputs,
            _empty,
            _rows,
            _more,
            PulseCards.Note(Lang.T("Turn the wheel over the tile for everything; click it to mute.", "أدر العجلة فوق المربع لكل شيء، وانقره للكتم.")));
        Element.IsHitTestVisible = true;
        DockWindow.MarkInteractive(Element);
        Update();
    }

    public Border Element { get; }

    /// <summary>True while one of its sliders is held.</summary>
    public bool Busy => _byKey.Values.Any(r => r.Busy);

    public void Update()
    {
        var snap = _control.Current;
        _chip.Text = snap.Default is null ? "" : SoundViews.PercentText(snap.Master, snap.MasterMuted);
        RenderOutputs(snap);
        var apps = SoundApps.Group(snap.Sessions);
        var shown = apps.Where(a => a.Playing).Take(MaxRows).ToList();
        int hidden = apps.Count(a => a.Playing) - shown.Count;
        var keys = shown.Select(a => a.Key).ToList();
        if (!keys.SequenceEqual(_rows.Children.OfType<AppRow>().Select(r => r.Key)) && !Busy)
        {
            _rows.Children.Clear();
            foreach (var gone in _byKey.Keys.Except(keys).ToList()) _byKey.Remove(gone);
            foreach (var app in shown)
            {
                if (!_byKey.TryGetValue(app.Key, out var row)) _byKey[app.Key] = row = new AppRow(app, _control, compact: true);
                _rows.Children.Add(row);
            }
        }
        foreach (var app in apps)
            if (_byKey.TryGetValue(app.Key, out var row)) row.Update(app);
        _empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _more.Text = hidden > 0 ? Lang.T($"And {hidden} more in the mixer.", $"و{hidden} غيرها في المازج.") : "";
        _more.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderOutputs(SoundSnapshot snap)
    {
        var key = string.Join("|", snap.Devices.Select(d => d.Id)) + "#" + snap.DefaultId + snap.CanSwitchOutput;
        if (key == _outputsKey) return;
        _outputsKey = key;
        if (snap.CanSwitchOutput && snap.Devices.Count > 1)
        {
            var choice = SoundViews.OutputPicker(snap, id => _control.SetOutput(id), room: 30);
            choice.Margin = new Thickness(0, 10, 0, 4);
            choice.HorizontalAlignment = HorizontalAlignment.Left;
            _outputs.Content = choice;
        }
        else
        {
            _outputs.Content = snap.Default is { } d ? PulseCards.Note(d.Label, 8) : null;
        }
    }
}
