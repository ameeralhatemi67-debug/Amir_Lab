using Toolbox.Core.Inputs;
using Toolbox.Core.Output;

namespace Toolbox.Core.Tests;

public class OutputNamerTests
{
    [Theory]
    [InlineData("", null, null, "Page 001.jpg")]
    [InlineData(" Cropped", null, null, "Page 001 Cropped.jpg")]
    [InlineData("", 3, 12, "Page 001 - Page 03.jpg")]
    [InlineData("", 7, 120, "Page 001 - Page 007.jpg")]
    public void Default_pattern_matches_reference_style(string suffix, int? page, int? count, string expected)
    {
        var name = OutputNamer.Compose(OutputNamer.DefaultPattern, new NameParts("Page 001", suffix, ".jpg", page, count));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void Custom_tokens_expand()
    {
        var name = OutputNamer.Compose("{date}_{name}_{w}x{h}", new NameParts("Logo", "", ".png", Width: 512, Height: 256, Date: new DateTime(2026, 10, 20)));
        Assert.Equal("2026-10-20_Logo_512x256.png", name);
    }

    [Theory]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData("CON", "_CON")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("تقرير: الربع", "تقرير_ الربع")]
    public void Sanitize_keeps_unicode_and_removes_invalid(string input, string expected)
    {
        Assert.Equal(expected, OutputNamer.Sanitize(input));
    }

    [Fact]
    public void Collision_index_goes_before_extension()
    {
        Assert.Equal("Report (2).pdf", OutputNamer.WithCollisionIndex("Report.pdf", 2));
        Assert.Equal("Report.pdf", OutputNamer.WithCollisionIndex("Report.pdf", 1));
    }
}

public class OutputManagerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("ftb-out-");
    private readonly TempJournal _journal;

    public OutputManagerTests()
    {
        _journal = new TempJournal(Path.Combine(_dir.FullName, "journal.txt"));
    }

    public void Dispose()
    {
        foreach (var f in _dir.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
        _dir.Delete(true);
    }

    private OutputManager Manager(string? fallback = null) =>
        new(() => new OutputPolicy { FallbackFolder = fallback ?? Path.Combine(_dir.FullName, "fallback") }, _journal);

    [Fact]
    public void Commit_never_overwrites_existing_files()
    {
        var existing = Path.Combine(_dir.FullName, "Photo.webp");
        File.WriteAllText(existing, "original");
        var manager = Manager();

        var slot = manager.CreateSlot(_dir.FullName, new OutputSpec("Photo", "", FileKind.WebP));
        File.WriteAllText(slot.TempPath, "new");
        var record = manager.Commit(slot, new OutputVerification(true, null));

        Assert.Equal(Path.Combine(_dir.FullName, "Photo (2).webp"), record.Path);
        Assert.Equal("original", File.ReadAllText(existing));
        Assert.False(File.Exists(slot.TempPath));
    }

    [Fact]
    public void Temp_file_is_writable_by_encoders_and_removed_on_discard()
    {
        var manager = Manager();
        var slot = manager.CreateSlot(_dir.FullName, new OutputSpec("Doc", " Cropped", FileKind.Png));
        Assert.EndsWith(TempJournal.TempExtension, slot.TempPath);
        // Encoders open with create/truncate; this must succeed.
        File.WriteAllText(slot.TempPath, "data");

        manager.Discard(slot);
        Assert.False(File.Exists(slot.TempPath));
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*.png"));
    }

    [Fact]
    public void Refuses_to_commit_a_failed_verification()
    {
        var manager = Manager();
        var slot = manager.CreateSlot(_dir.FullName, new OutputSpec("X", "", FileKind.Jpeg));
        Assert.Throws<InvalidOperationException>(() => manager.Commit(slot, OutputVerification.Fail("broken")));
        manager.Discard(slot);
    }

    [Fact]
    public void Falls_back_when_destination_cannot_be_written()
    {
        // A path under a file (not a folder) can never be created: simulates an unwritable destination.
        var blocker = Path.Combine(_dir.FullName, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var unwritable = Path.Combine(blocker, "sub");
        var manager = Manager();

        var slot = manager.CreateSlot(unwritable, new OutputSpec("Scan", "", FileKind.Pdf));
        Assert.True(slot.UsedFallback);
        Assert.StartsWith(Path.Combine(_dir.FullName, "fallback"), slot.TempPath);
        manager.Discard(slot);
    }

    [Fact]
    public void Journal_cleanup_removes_orphaned_temp_files_only()
    {
        var manager = Manager();
        var slot = manager.CreateSlot(_dir.FullName, new OutputSpec("Crash", "", FileKind.Png));
        File.WriteAllText(slot.TempPath, "partial");
        // Simulate a crash: a new journal instance reads the persisted list.
        var survivor = new TempJournal(Path.Combine(_dir.FullName, "journal.txt"));
        Assert.Equal(1, survivor.CleanupOrphans());
        Assert.False(File.Exists(slot.TempPath));
    }
}
