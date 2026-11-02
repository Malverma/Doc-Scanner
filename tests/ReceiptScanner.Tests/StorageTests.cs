using ReceiptScanner.Core.Imaging;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Tests;

public class ReceiptStoreTests
{
    private static readonly DateTime When = new(2026, 11, 2, 14, 30, 5);

    [Theory]
    [InlineData(OutputFormat.Pdf, new[] { ".pdf" })]
    [InlineData(OutputFormat.Png, new[] { ".png" })]
    [InlineData(OutputFormat.Both, new[] { ".pdf", ".png" })]
    public void Saves_requested_formats_with_timestamped_names(OutputFormat format, string[] extensions)
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Sub("raw"), 50, 50, (_, _) => 100);
        var folder = dir.Sub("out");

        var saved = ReceiptStore.Save(image, PixelRegion.Full(image), folder, format, When);

        Assert.Equal("receipt_2026-11-02_14-30-05", saved.BaseName);
        Assert.Equal(extensions.Length, Directory.GetFiles(folder).Length);
        foreach (var extension in extensions)
        {
            Assert.True(File.Exists(Path.Combine(folder, saved.BaseName + extension)));
        }
    }

    [Fact]
    public void Same_second_never_overwrites()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Sub("raw"), 50, 50, (_, _) => 100);
        var folder = dir.Sub("out");

        var first = ReceiptStore.Save(image, PixelRegion.Full(image), folder, OutputFormat.Pdf, When);
        var second = ReceiptStore.Save(image, PixelRegion.Full(image), folder, OutputFormat.Pdf, When);

        Assert.NotEqual(first.BaseName, second.BaseName);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void Unverified_scans_say_so_in_the_name()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Sub("raw"), 50, 50, (_, _) => 100);

        var saved = ReceiptStore.Save(image, PixelRegion.Full(image), dir.Sub("out"), OutputFormat.Pdf, When, unverified: true);

        Assert.EndsWith("_UNVERIFIED", saved.BaseName);
    }

    [Fact]
    public void Truncated_scan_data_is_refused_and_leaves_nothing_in_the_folder()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Sub("raw"), 50, 50, (_, _) => 100);
        using (var stream = File.OpenWrite(image.Path))
        {
            stream.SetLength(1000);
        }

        var folder = dir.Sub("out");

        Assert.Throws<InvalidDataException>(() => ReceiptStore.Save(image, PixelRegion.Full(image), folder, OutputFormat.Both, When));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void A_failed_write_leaves_no_partial_files()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Sub("raw"), 50, 50, (_, _) => 100);
        var folder = dir.Sub("out");

        // Region runs past the image, so encoding fails midway.
        Assert.ThrowsAny<Exception>(() => ReceiptStore.Save(image, new PixelRegion(0, 0, 50, 500), folder, OutputFormat.Pdf, When));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void Move_renames_on_collision_and_keeps_every_file()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Sub("raw"), 50, 50, (_, _) => 100);
        var a = dir.Sub("a");
        var b = dir.Sub("b");
        var first = ReceiptStore.Save(image, PixelRegion.Full(image), a, OutputFormat.Both, When);
        ReceiptStore.Save(image, PixelRegion.Full(image), b, OutputFormat.Both, When);

        var moved = ReceiptStore.Move(first, b);

        Assert.Empty(Directory.GetFiles(a));
        Assert.Equal(4, Directory.GetFiles(b).Length);
        Assert.All(moved.Files, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public void Replace_overwrites_the_same_receipt()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Sub("raw"), 50, 80, (_, _) => 100);
        var folder = dir.Sub("out");
        var saved = ReceiptStore.Save(image, PixelRegion.Full(image), folder, OutputFormat.Png, When);
        var before = File.ReadAllBytes(saved.Files[0]);

        var rotated = ImageOps.Rotate(image, PixelRegion.Full(image), 1, Path.Combine(dir.Path, "rot.raw"));
        ReceiptStore.Replace(rotated, PixelRegion.Full(rotated), saved, OutputFormat.Png);

        Assert.Single(Directory.GetFiles(folder));
        Assert.NotEqual(before, File.ReadAllBytes(saved.Files[0]));
    }
}

public class FolderServiceTests
{
    [Fact]
    public void Lists_folders_sorted_with_receipt_counts()
    {
        using var dir = new TempDir();
        var service = new FolderService(dir.Path);
        var gas = dir.Sub("Gas");
        dir.Sub("apple");
        dir.Sub(".hidden");
        Directory.CreateDirectory(Path.Combine(gas, "Sub"));
        File.WriteAllText(Path.Combine(gas, "a.pdf"), "");
        File.WriteAllText(Path.Combine(gas, "a.png"), "");
        File.WriteAllText(Path.Combine(gas, "b.pdf"), "");
        File.WriteAllText(Path.Combine(gas, "b.pdf.partial"), "");
        File.WriteAllText(Path.Combine(gas, "notes.txt"), "");

        var list = service.List(dir.Path);

        Assert.Equal(["apple", "Gas"], list.Select(f => f.Name));
        Assert.Equal(2, list[1].ReceiptCount);
        Assert.True(list[1].HasSubfolders);
        Assert.False(list[0].HasSubfolders);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..\\evil")]
    [InlineData("what?")]
    [InlineData("CON")]
    [InlineData("com1.txt")]
    public void Invalid_names_are_rejected(string name) =>
        Assert.Throws<FolderNameException>(() => FolderService.ValidateName(name));

    [Theory]
    [InlineData("Grocery", "Grocery")]
    [InlineData("  Gas 2026 ", "Gas 2026")]
    [InlineData("Trailing.", "Trailing")]
    [InlineData("Console", "Console")]
    public void Valid_names_are_cleaned(string name, string expected) =>
        Assert.Equal(expected, FolderService.ValidateName(name));

    [Fact]
    public void Paths_outside_the_base_are_refused()
    {
        using var dir = new TempDir();
        var inner = dir.Sub("base");
        var sibling = dir.Sub("base-other");
        var service = new FolderService(inner);

        Assert.False(service.IsInsideBase(sibling));
        Assert.False(service.IsInsideBase(Path.Combine(inner, "..", "base-other")));
        Assert.Throws<InvalidOperationException>(() => service.List(sibling));
    }

    [Fact]
    public void Duplicate_names_are_refused()
    {
        using var dir = new TempDir();
        var service = new FolderService(dir.Path);
        service.Create(dir.Path, "Gas");

        Assert.Throws<FolderNameException>(() => service.Create(dir.Path, "Gas"));
    }

    [Fact]
    public void Only_empty_folders_can_be_deleted()
    {
        using var dir = new TempDir();
        var service = new FolderService(dir.Path);
        var full = dir.Sub("Full");
        File.WriteAllText(Path.Combine(full, "a.pdf"), "");
        var empty = dir.Sub("Empty");

        Assert.Throws<FolderNameException>(() => service.DeleteEmpty(full));
        Assert.True(File.Exists(Path.Combine(full, "a.pdf")));

        service.DeleteEmpty(empty);
        Assert.False(Directory.Exists(empty));

        Assert.Throws<FolderNameException>(() => service.DeleteEmpty(dir.Path));
    }

    [Fact]
    public void Rename_allows_changing_only_case_and_refuses_collisions()
    {
        using var dir = new TempDir();
        var service = new FolderService(dir.Path);
        var gas = dir.Sub("Gas");
        dir.Sub("Food");

        Assert.Throws<FolderNameException>(() => service.Rename(gas, "Food"));
        var renamed = service.Rename(gas, "GAS");

        Assert.True(Directory.Exists(renamed));
    }

    [Fact]
    public void Breadcrumb_runs_from_base_to_folder()
    {
        using var dir = new TempDir();
        var service = new FolderService(dir.Path);
        var deep = Path.Combine(dir.Path, "2026", "Gas");
        Directory.CreateDirectory(deep);

        var crumbs = service.Breadcrumb(deep);

        Assert.Equal([Path.GetFileName(dir.Path), "2026", "Gas"], crumbs.Select(c => c.Name));
        Assert.Equal(deep, crumbs[^1].Path);
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "s.json");
        new AppSettings { BaseFolder = "/x", Dpi = 600, Color = ColorMode.Color, Format = OutputFormat.Both, TrimBlankSpace = false }.Save(path);

        var loaded = AppSettings.Load(path, "/default");

        Assert.Equal(("/x", 600, ColorMode.Color, OutputFormat.Both, false), (loaded.BaseFolder, loaded.Dpi, loaded.Color, loaded.Format, loaded.TrimBlankSpace));
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(0)]
    public void Unsupported_dpi_falls_back_to_300(int dpi)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "s.json");
        File.WriteAllText(path, $$"""{"Dpi": {{dpi}}}""");

        Assert.Equal(300, AppSettings.Load(path, "/default").Dpi);
    }

    [Fact]
    public void Corrupt_file_gives_defaults()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "s.json");
        File.WriteAllText(path, "{not json");

        var loaded = AppSettings.Load(path, "/default");

        Assert.Equal(("/default", 300, ColorMode.Gray, OutputFormat.Pdf, true), (loaded.BaseFolder, loaded.Dpi, loaded.Color, loaded.Format, loaded.TrimBlankSpace));
    }

    [Fact]
    public void Never_offers_above_the_optical_limit() => Assert.DoesNotContain(1200, AppSettings.AllowedDpi);
}

public class ErrorTextTests
{
    [Fact]
    public void Jam_steps_say_to_open_the_cover_before_pulling()
    {
        var steps = ScanErrorText.Describe(ScanErrorCode.Jam).Steps;

        Assert.Contains("Open the Top Cover before pulling", steps[0]);
    }

    [Fact]
    public void Every_error_code_has_recovery_steps()
    {
        foreach (var code in Enum.GetValues<ScanErrorCode>())
        {
            Assert.NotEmpty(ScanErrorText.Describe(code).Steps);
        }
    }
}
