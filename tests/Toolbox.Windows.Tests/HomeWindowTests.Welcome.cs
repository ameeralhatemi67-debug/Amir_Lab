using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Windows.Home;

namespace Toolbox.Windows.Tests;

/// <summary>The welcome the installer opens: built off-screen in every theme and language.</summary>
public partial class HomeWindowTests
{
    private static void RenderWelcome(string? snapshots, string theme, string lang)
    {
        bool ar = lang == "ar";
        var tips = new List<WelcomeTip>
        {
            new("lap-toolbox", ar ? "صندوق أدوات الملفات" : "File Toolbox", ar ? "اسحب أي ملف واضغط Shift وأنت تمسكه." : "Drag any file and tap Shift while you hold it: a wheel of tools opens at the pointer.", ["Shift"]),
            new("lap-longshot", ar ? "اللقطة الطويلة" : "Longshot", ar ? "لقطة لصفحة كاملة تتمرّر، أو فيديو لأي منطقة." : "A screenshot of a whole page that scrolls, or a video of any area.", ["Alt+Shift+S", "Alt+Shift+V"]),
            new("lap-pulse", "Pulse", ar ? "الشريط على حافة الشاشة." : "The dock on the edge of your screen: your Claude and Codex limits, and your shortcuts.", []),
        };
        bool? startWithWindows = null;
        var window = new WelcomeWindow(updated: theme == "dark", "0.6.2", tips, startsWithWindows: false, on => startWithWindows = on, () => { });
        Assert.Equal(theme == "dark" ? "Amir Lap is updated" : "Amir Lap is installed", window.Title);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new Border { Child = content };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        Layout(frame, 560, 640);
        Assert.True(content.DesiredSize.Height is > 360 and < 640, $"the welcome fits ({content.DesiredSize.Height:0} px tall)");
        Save(snapshots, frame, 560, 640, $"welcome-{theme}-{lang}");
        Assert.Null(startWithWindows); // nothing is switched on by itself
        window.Close();
    }
}
