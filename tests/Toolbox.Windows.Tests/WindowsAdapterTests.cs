using Microsoft.Win32;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Settings;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Wheel;

namespace Toolbox.Windows.Tests;

public class ScreenClampTests
{
    private static Native.RECT Rect(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Theory]
    // Single 1920×1040 work area: centred when there is room, pushed inside near edges.
    [InlineData(960, 520, 0, 0, 1920, 1040, 760, 320)]
    [InlineData(10, 10, 0, 0, 1920, 1040, 0, 0)]
    [InlineData(1910, 1030, 0, 0, 1920, 1040, 1520, 640)]
    // Secondary monitor to the left with negative coordinates.
    [InlineData(-100, 500, -2560, 0, 0, 1400, -400, 300)]
    // Monitor above the primary.
    [InlineData(500, -50, 0, -1080, 1920, 0, 300, -400)]
    public void Wheel_stays_inside_the_monitor_work_area(int px, int py, int l, int t, int r, int b, int expectedLeft, int expectedTop)
    {
        var (left, top) = ScreenGeometry.ClampCentered(new Native.POINT { X = px, Y = py }, 400, Rect(l, t, r, b));
        Assert.Equal((expectedLeft, expectedTop), (left, top));
    }

    [Fact]
    public void Wheel_larger_than_the_work_area_aligns_to_its_corner()
    {
        var (left, top) = ScreenGeometry.ClampCentered(new Native.POINT { X = 100, Y = 100 }, 600, Rect(0, 0, 500, 400));
        Assert.Equal((0, 0), (left, top));
    }
}

public class ShellIntegrationTests : IDisposable
{
    private readonly string _root = $@"Software\FileToolboxTests\{Guid.NewGuid():N}\Classes";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root.Split(@"\Classes")[0], throwOnMissingSubKey: false);

    [Fact]
    public void Context_menu_registers_per_extension_and_removes_cleanly()
    {
        ShellIntegration.SetContextMenu(true, _root);
        Assert.True(ShellIntegration.IsContextMenuRegistered(_root));
        using (var key = Registry.CurrentUser.OpenSubKey($@"{_root}\SystemFileAssociations\.jpg\shell\FileToolbox\command"))
        {
            var command = (string)key!.GetValue("")!;
            Assert.Contains("--open \"%1\"", command);
        }
        ShellIntegration.SetContextMenu(false, _root);
        Assert.False(ShellIntegration.IsContextMenuRegistered(_root));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{_root}\SystemFileAssociations\.png\shell\FileToolbox"));
    }
}

public class InstanceChannelTests
{
    [Fact]
    public async Task Arguments_reach_the_running_instance()
    {
        // Uses the real per-user pipe name: skip when the app itself is running on this machine.
        if (System.Diagnostics.Process.GetProcessesByName("AmirLap").Length > 0) return;
        var received = new TaskCompletionSource<string[]>();
        using var channel = new InstanceChannel();
        channel.Start(args => received.TrySetResult(args));
        await Task.Delay(200);
        Assert.True(InstanceChannel.TrySend(["--open", @"C:\photos\تقرير.pdf"]));
        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["--open", @"C:\photos\تقرير.pdf"], args);
    }
}

public class WheelModelTests
{
    [Fact]
    public void Pdf_payload_gets_the_pdf_convert_ring_and_its_own_tools_ring()
    {
        var files = new[] { new ProbedFile(@"C:\a.pdf", FileKind.Pdf, 2048, null) };
        var rings = WheelModels.Build(files, new AppSettings());
        Assert.Equal(["DOCX", "JPG", "PNG", "TXT"], rings[0].Segments.Select(s => s.Label));
        // Owner, 13 Nov 2026: the Tools ring differs per kind of file and shows no dead tiles; shared tools keep their seat.
        Assert.Equal(
            [
                CapabilityMap.Ids.PdfEdit, CapabilityMap.Ids.Metadata, CapabilityMap.Ids.PdfSign, CapabilityMap.Ids.PdfDark,
                CapabilityMap.Ids.Background, CapabilityMap.Ids.PdfPages, CapabilityMap.Ids.OcrSearchablePdf, CapabilityMap.Ids.OcrImageText,
            ],
            rings[1].Segments.Select(s => s.ActionId));
        Assert.DoesNotContain(rings[1].Segments, s => s.ActionId == CapabilityMap.Ids.Compress);
        Assert.True(rings[1].Segments.Single(s => s.ActionId == CapabilityMap.Ids.PdfDark).Enabled);
        var image = WheelModels.Build([new ProbedFile(@"C:\a.png", FileKind.Png, 2048, null)], new AppSettings())[1];
        Assert.Equal(CapabilityMap.Ids.Metadata, image.Segments[1].ActionId);
        Assert.Equal(CapabilityMap.Ids.Background, image.Segments[4].ActionId);
        Assert.Equal(CapabilityMap.Ids.OcrImageText, image.Segments[7].ActionId);
    }

    [Fact]
    public void Same_format_segment_is_disabled_in_place()
    {
        var files = new[] { new ProbedFile(@"C:\a.jpg", FileKind.Jpeg, 2048, null) };
        var convert = WheelModels.Build(files, new AppSettings())[0];
        var jpg = convert.Segments.Single(s => s.ActionId == CapabilityMap.Ids.ImageToJpg);
        Assert.False(jpg.Enabled);
        Assert.Equal("Already JPG", jpg.Detail);
        Assert.Equal(2, convert.Segments.ToList().IndexOf(jpg)); // position is stable
    }

    [Fact]
    public void Idle_text_shows_size_and_name_or_count()
    {
        var one = new[] { new ProbedFile(@"C:\Page 001.pdf", FileKind.Pdf, 1_153_434, null) };
        Assert.Equal(("1.1 MB", "Page 001.pdf"), WheelModels.IdleText(one));
        var mixed = new[] { new ProbedFile(@"C:\a.png", FileKind.Png, 1024, null), new ProbedFile(@"C:\b.xyz", FileKind.Unknown, 1024, "Unsupported") };
        Assert.Equal("1 of 2 supported", WheelModels.IdleText(mixed).Secondary);
    }

    [Theory]
    [InlineData("Convert to JPG", "Converting to JPG")]
    [InlineData("Crop Image", "Cropping Image")]
    [InlineData("Add Background", "Adding Background")]
    [InlineData("Something else", "Something else")]
    public void Progressive_titles(string title, string expected) => Assert.Equal(expected, JobText.Progressive(title));
}
