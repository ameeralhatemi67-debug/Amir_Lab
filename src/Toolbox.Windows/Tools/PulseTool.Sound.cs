using System.Windows;
using Toolbox.Core.Pulse;
using Toolbox.Core.Sound;
using Toolbox.Windows.Pulse;
using Toolbox.Windows.Sound;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Pulse's Sound tile, Imp Studio on the dock: the ring is the volume of everything, the mouse wheel
/// over it turns it, a click mutes, and its card is a quick mixer with the apps playing now.
/// </summary>
internal sealed partial class PulseTool
{
    private SoundCard? _soundCard;
    private ISoundControl? _soundCardFor;

    private void StartSound()
    {
        SoundHub.Changed += UpdateSoundTile;
        if (_dock is not null) _dock.TileWheel += OnTileWheel;
        UpdateSoundTile();
    }

    private void StopSound()
    {
        SoundHub.Changed -= UpdateSoundTile;
        if (_dock is not null) _dock.TileWheel -= OnTileWheel;
        _soundCard = null;
        _soundCardFor = null;
    }

    private static ImpStudioTool? Studio => App.FindTool(ImpStudioTool.ToolId) as ImpStudioTool;

    private void UpdateSoundTile()
    {
        if (_dock?.Tile(PulseTiles.Sound) is not { } tile) return;
        if (SoundHub.Control is not { } control)
        {
            tile.SetUsage(0);
            tile.SetLabel(ShortName(PulseTiles.Sound));
            if (_soundCard is not null) { _soundCard = null; _soundCardFor = null; }
            return;
        }
        var snap = control.Current;
        tile.SetUsage(snap.MasterMuted ? 0 : SoundApps.Percent(snap.Master), ThemeResources.Lap.Accent);
        tile.SetLabel(snap.Default is null ? ShortName(PulseTiles.Sound) : SoundViews.PercentText(snap.Master, snap.MasterMuted), snap.MasterMuted ? "Brush.Danger" : "Brush.Text2");
        if (_soundCard is not null && _soundCardFor == control) _soundCard.Update();
    }

    private void OnSoundTile()
    {
        if (Studio is { } studio && SoundHub.Control is not null) studio.ToggleMute();
        else App.ShowHome(ImpStudioTool.ToolId);
    }

    private void OnTileWheel(string id, int delta)
    {
        if (id == PulseTiles.Sound) Studio?.Wheel(delta);
    }

    private FrameworkElement SoundCardFor()
    {
        if (SoundHub.Control is not { } control || Studio is not { } studio)
            return PulseCards.Card(PulseCards.Head("lap-mixer", TileName(PulseTiles.Sound)),
                PulseCards.Body(Lang.T("Imp Studio is off. Click to turn it on in Amir Lap: every app's volume, right here.", "استوديو إمب متوقف. انقر لتشغيله في أمير لاب: مستوى صوت كل تطبيق، هنا مباشرة.")));
        if (_soundCard is null || _soundCardFor != control)
        {
            _soundCard = new SoundCard(control, studio.Open);
            _soundCardFor = control;
        }
        else _soundCard.Update();
        return _soundCard.Element;
    }
}
