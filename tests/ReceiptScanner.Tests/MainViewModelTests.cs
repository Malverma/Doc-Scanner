using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReceiptScanner.App.ViewModels;
using ReceiptScanner.App.Views;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Tests;

public sealed class FakePlatform : IPlatformActions
{
    public string? PickResult { get; set; }

    public string? Opened { get; private set; }

    public Task<string?> PickFolderAsync(string? startFolder) => Task.FromResult(PickResult);

    public void OpenInFileManager(string folder) => Opened = folder;

    public void ShowFile(string path) => Opened = path;

    public void OpenFile(string path) => Opened = path;
}

public class MainViewModelTests
{
    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();

        public Rig(SimulatedBehavior behavior = SimulatedBehavior.Normal, double lengthMm = 300, TimeSpan? stall = null, OutputFormat format = OutputFormat.Pdf)
        {
            Paths = new AppPaths(_dir.Sub("data"));
            Base = _dir.Sub("Receipts");
            Directory.CreateDirectory(Path.Combine(Base, "Grocery"));
            Directory.CreateDirectory(Path.Combine(Base, "Gas"));
            Directory.CreateDirectory(Path.Combine(Base, "Gas", "2026"));
            Backend = new SimulatedBackend(behavior, lengthMm, TimeSpan.Zero);
            Vm = new MainViewModel(
                Backend,
                Paths,
                new AppLog(Paths.LogFolder),
                new AppSettings { BaseFolder = Base, Format = format, Dpi = 200 },
                Platform,
                stall);
        }

        public AppPaths Paths { get; }

        public string Base { get; }

        public SimulatedBackend Backend { get; }

        public FakePlatform Platform { get; } = new();

        public MainViewModel Vm { get; }

        public string[] Receipts(string folder = "") =>
            Directory.GetFiles(Path.Combine(Base, folder)).Select(Path.GetFileName).ToArray()!;

        public FolderTileViewModel Tile(string name) => Vm.Tiles.First(t => t.Name == name);

        public void Dispose() => _dir.Dispose();
    }

    [AvaloniaFact]
    public async Task Saved_scan_is_a_real_file_shown_by_path_and_in_the_list()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));

        await rig.Vm.ScanAsync();

        var path = Assert.Single(Directory.GetFiles(Path.Combine(rig.Base, "Grocery")));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal(path, rig.Vm.LatestPath);
        Assert.Equal(path, Assert.Single(Assert.Single(rig.Vm.Receipts).Files));
        Assert.Equal("Receipts in Grocery", rig.Vm.ReceiptsHeading);

        rig.Vm.ShowLatestInFileManager();

        Assert.Equal(path, rig.Platform.Opened);
    }

    [AvaloniaFact]
    public async Task List_follows_the_selected_folder()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));
        await rig.Vm.ScanAsync();

        rig.Vm.TileClicked(rig.Tile("Gas"));

        Assert.Empty(rig.Vm.Receipts);
        Assert.True(rig.Vm.HasNoReceipts);

        rig.Vm.TileClicked(rig.Tile("Grocery"));

        Assert.Single(rig.Vm.Receipts);
        Assert.False(rig.Vm.HasNoReceipts);
    }

    [AvaloniaFact]
    public async Task Moving_the_latest_receipt_from_the_list_keeps_rotate_on_the_moved_file()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));
        await rig.Vm.ScanAsync();

        rig.Vm.MoveReceiptTo(rig.Vm.Receipts[0], Path.Combine(rig.Base, "Gas"));

        Assert.Empty(rig.Receipts("Grocery"));
        Assert.Single(rig.Receipts("Gas"));
        Assert.Contains("Gas", rig.Vm.LatestPath, StringComparison.Ordinal);

        await rig.Vm.RotateAsync();

        Assert.Empty(rig.Receipts("Grocery"));
        Assert.Single(rig.Receipts("Gas"));
    }

    [AvaloniaFact]
    public async Task Rotate_keeps_the_buttons_on_screen_and_clears_its_status()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();
        var seen = new List<string>();
        rig.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.BusyText) && rig.Vm.BusyText.Length > 0)
            {
                seen.Add(rig.Vm.BusyText);
                Assert.True(rig.Vm.ShowLatestActions);
            }
        };

        await rig.Vm.RotateAsync();

        Assert.Equal(["Rotating", "Saving the rotated receipt"], seen);
        Assert.Empty(rig.Vm.BusyText);
        Assert.True(rig.Vm.CanUseLatestActions);
        Assert.Contains("Rotate finished", ReadLog(rig), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Failed_rotate_leaves_the_receipt_and_preview_alone()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();
        var file = Assert.Single(Directory.GetFiles(rig.Base));
        var before = await File.ReadAllBytesAsync(file);
        var preview = rig.Vm.PreviewImage;
        var details = rig.Vm.LatestDetails;

        // Holding the output blocks the replace on Windows. Removing the folder blocks it everywhere.
        var hidden = rig.Base + "-hidden";
        Directory.Move(rig.Base, hidden);
        await rig.Vm.RotateAsync();
        Directory.Move(hidden, rig.Base);

        Assert.Contains("unchanged", rig.Vm.Notice, StringComparison.Ordinal);
        Assert.Same(preview, rig.Vm.PreviewImage);
        Assert.Equal(details, rig.Vm.LatestDetails);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.False(rig.Vm.IsProcessing);
        Assert.Empty(rig.Vm.BusyText);
    }

    [AvaloniaFact]
    public async Task Deleting_the_latest_receipt_from_the_list_clears_the_panel()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();

        rig.Vm.DeleteReceipt(rig.Vm.Receipts[0]);

        Assert.Empty(rig.Receipts());
        Assert.False(rig.Vm.HasLatest);
        Assert.Empty(rig.Vm.Receipts);
    }

    [AvaloniaFact]
    public async Task Clicking_the_real_rotate_button_finishes_and_the_window_responds()
    {
        using var rig = new Rig();
        var window = new MainWindow { DataContext = rig.Vm, Width = 1040, Height = 680 };
        window.Show();
        await rig.Vm.ScanAsync();
        Dispatcher.UIThread.RunJobs();
        var before = rig.Vm.LatestDetails;

        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Rotate" && b.IsEffectivelyVisible);
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((rig.Vm.IsProcessing || rig.Vm.LatestDetails == before) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(rig.Vm.IsProcessing);
        Assert.NotEqual(before, rig.Vm.LatestDetails);
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dragging_a_receipt_row_onto_a_folder_tile_moves_it()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));
        await rig.Vm.ScanAsync();
        var window = new MainWindow { DataContext = rig.Vm, Width = 1040, Height = 680 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Point CenterOf(Visual visual) => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;
        var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("receipt"));
        var tile = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is FolderTileViewModel { Name: "Gas" });

        window.MouseDown(CenterOf(row), MouseButton.Left);
        window.MouseMove(CenterOf(row) + new Point(30, 0), RawInputModifiers.LeftMouseButton);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(CenterOf(tile), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(CenterOf(tile), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(rig.Receipts("Gas"));
        Assert.Empty(rig.Receipts("Grocery"));
        window.Close();
    }

    private static string ReadLog(Rig rig)
    {
        using var stream = new FileStream(Directory.GetFiles(rig.Paths.LogFolder).Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [AvaloniaFact]
    public async Task Scan_ending_with_paper_left_is_held_and_not_saved()
    {
        using var rig = new Rig(SimulatedBehavior.PaperLeft);

        await rig.Vm.ScanAsync();

        Assert.True(rig.Vm.NeedsReview);
        Assert.Empty(rig.Receipts());
        Assert.Contains(rig.Vm.Warnings, w => w.Contains("still in the scanner", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Scan_shorter_than_the_expected_length_is_held_and_not_saved()
    {
        using var rig = new Rig(lengthMm: 300);
        rig.Vm.ExpectedLengthText = "400";

        await rig.Vm.ScanAsync();

        Assert.True(rig.Vm.NeedsReview);
        Assert.Empty(rig.Receipts());
    }

    [AvaloniaFact]
    public async Task Scan_longer_than_the_expected_length_saves_whole()
    {
        using var rig = new Rig(lengthMm: 300);
        rig.Vm.ExpectedLengthText = "100";

        await rig.Vm.ScanAsync();

        Assert.False(rig.Vm.NeedsReview);
        Assert.Single(rig.Receipts());
    }

    [AvaloniaTheory]
    [InlineData("10")]
    [InlineData("abc")]
    [InlineData("3000")]
    public void Bad_scan_size_shows_an_error_and_keeps_the_last_good_value(string bad)
    {
        using var rig = new Rig();
        rig.Vm.ExpectedLengthText = "400";
        Assert.Empty(rig.Vm.ScanSizeError);

        rig.Vm.ExpectedLengthText = bad;

        Assert.NotEmpty(rig.Vm.ScanSizeError);
        Assert.Equal(400, AppSettings.Load(rig.Paths.SettingsFile, rig.Base).ExpectedLengthMm);

        rig.Vm.ExpectedLengthText = "";

        Assert.Empty(rig.Vm.ScanSizeError);
        Assert.Null(AppSettings.Load(rig.Paths.SettingsFile, rig.Base).ExpectedLengthMm);
    }

    [AvaloniaFact]
    public async Task Clean_scan_saves_straight_to_the_selected_folder_and_updates_its_count()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));
        Assert.Equal("Scan to Grocery", rig.Vm.ScanButtonText);

        await rig.Vm.ScanAsync();

        Assert.Single(rig.Receipts("Grocery"));
        Assert.Empty(rig.Receipts());
        Assert.Equal("1 receipt", rig.Tile("Grocery").CountText);
        Assert.True(rig.Tile("Grocery").IsTarget);
        Assert.True(rig.Vm.HasLatest);
        Assert.False(rig.Vm.NeedsReview);
        Assert.True(rig.Vm.CanUseLatestActions);
        Assert.NotNull(rig.Vm.PreviewImage);
        Assert.False(rig.Vm.HasError);
    }

    [AvaloniaFact]
    public async Task Jam_saves_nothing_and_gives_top_cover_steps()
    {
        using var rig = new Rig(SimulatedBehavior.Jam);

        await rig.Vm.ScanAsync();

        Assert.Empty(rig.Receipts());
        Assert.True(rig.Vm.HasError);
        Assert.Contains("Top Cover", rig.Vm.ErrorSteps[0]);
        Assert.False(rig.Vm.HasLatest);
        Assert.Empty(Directory.GetFiles(rig.Paths.TempFolder));
    }

    [AvaloniaFact]
    public async Task A_new_scan_works_after_a_jam()
    {
        using var rig = new Rig(SimulatedBehavior.Jam);
        await rig.Vm.ScanAsync();

        rig.Backend.Behavior = SimulatedBehavior.Normal;
        await rig.Vm.ScanAsync();

        Assert.False(rig.Vm.HasError);
        Assert.Single(rig.Receipts());
    }

    [AvaloniaFact]
    public async Task Disconnect_marks_scanner_missing_then_recovers_without_a_restart()
    {
        using var rig = new Rig(SimulatedBehavior.Disconnect);

        await rig.Vm.ScanAsync();
        Assert.False(rig.Vm.ScannerFound);
        Assert.Empty(rig.Receipts());

        rig.Backend.Behavior = SimulatedBehavior.Normal;
        await rig.Vm.ScanAsync();
        Assert.True(rig.Vm.ScannerFound);
        Assert.Single(rig.Receipts());
    }

    [AvaloniaFact]
    public async Task No_paper_shows_a_prompt()
    {
        using var rig = new Rig(SimulatedBehavior.NoPaper);

        await rig.Vm.ScanAsync();

        Assert.Equal("No receipt detected", rig.Vm.ErrorTitle);
        Assert.Empty(rig.Receipts());
    }

    [AvaloniaFact]
    public async Task Over_length_receipt_is_held_and_blocks_the_next_scan_until_reviewed()
    {
        using var rig = new Rig(SimulatedBehavior.OverLength);

        await rig.Vm.ScanAsync();

        Assert.True(rig.Vm.NeedsReview);
        Assert.Empty(rig.Receipts());
        Assert.False(rig.Vm.CanScan);
        Assert.Contains(rig.Vm.Warnings, w => w.Contains("72 in"));

        rig.Vm.Discard();

        Assert.Empty(rig.Receipts());
        Assert.True(rig.Vm.CanScan);
        Assert.Empty(Directory.GetFiles(rig.Paths.TempFolder));
    }

    [AvaloniaFact]
    public async Task Saving_a_held_scan_marks_it_unverified()
    {
        using var rig = new Rig(SimulatedBehavior.OverLength);
        await rig.Vm.ScanAsync();

        await rig.Vm.KeepAsync();

        var file = Assert.Single(rig.Receipts());
        Assert.Contains("_UNVERIFIED", file);
        Assert.False(rig.Vm.NeedsReview);
    }

    [AvaloniaFact]
    public async Task Unconfirmed_long_paper_stops_and_offers_a_choice()
    {
        using var rig = new Rig(SimulatedBehavior.Unconfirmed);

        await rig.Vm.ScanAsync();

        Assert.True(rig.Vm.ErrorOffersLongPaperChoice);
        Assert.Empty(rig.Receipts());

        await rig.Vm.ScanAnywayAsync();

        Assert.True(rig.Vm.NeedsReview);
        Assert.Contains(rig.Vm.Warnings, w => w.Contains("Long Paper"));
        Assert.Empty(rig.Receipts());
    }

    [AvaloniaFact]
    public async Task Long_paper_setup_clears_the_block()
    {
        using var rig = new Rig(SimulatedBehavior.Unconfirmed);
        await rig.Vm.ScanAsync();

        await rig.Vm.OpenLongPaperSetupAsync();
        await rig.Vm.ScanAsync();

        Assert.False(rig.Vm.HasError);
        Assert.Single(rig.Receipts());
    }

    [AvaloniaFact]
    public async Task Stalled_scan_only_offers_stop_and_never_ends_by_itself()
    {
        using var rig = new Rig(SimulatedBehavior.Stall, stall: TimeSpan.FromMilliseconds(100));

        var scan = rig.Vm.ScanAsync();
        await WaitUntil(() => rig.Vm.ShowStopWaiting);

        Assert.True(rig.Vm.IsScanning);
        Assert.False(scan.IsCompleted);

        rig.Vm.StopWaiting();
        await scan;

        Assert.Equal(ScanErrorText.Describe(ScanErrorCode.StoppedByUser).Title, rig.Vm.ErrorTitle);
        Assert.Empty(rig.Receipts());
    }

    [AvaloniaFact]
    public async Task Rotate_rewrites_the_saved_file()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();
        var file = Path.Combine(rig.Base, rig.Receipts()[0]);
        var before = await File.ReadAllBytesAsync(file);
        var details = rig.Vm.LatestDetails;

        await rig.Vm.RotateAsync();

        Assert.Single(rig.Receipts());
        Assert.NotEqual(before, await File.ReadAllBytesAsync(file));
        Assert.NotEqual(details, rig.Vm.LatestDetails);
    }

    [AvaloniaFact]
    public async Task Move_sends_the_receipt_to_the_next_clicked_tile_without_changing_the_target()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();

        rig.Vm.BeginMove();
        Assert.False(rig.Vm.CanScan);
        rig.Vm.TileClicked(rig.Tile("Gas"));

        Assert.Empty(rig.Receipts());
        Assert.Single(rig.Receipts("Gas"));
        Assert.False(rig.Vm.IsMoving);
        Assert.Equal(Path.GetFullPath(rig.Base), rig.Vm.TargetFolder);
    }

    [AvaloniaFact]
    public async Task Move_can_navigate_into_a_subfolder_first()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();

        rig.Vm.BeginMove();
        rig.Vm.OpenTile(rig.Tile("Gas"));
        rig.Vm.MoveHere();

        Assert.Single(rig.Receipts("Gas"));
        Assert.Equal(Path.GetFullPath(rig.Base), rig.Vm.TargetFolder);
    }

    [AvaloniaFact]
    public async Task Delete_removes_the_receipt_from_the_folder()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();

        rig.Vm.DeleteLatest();

        Assert.Empty(rig.Receipts());
        Assert.False(rig.Vm.HasLatest);
    }

    [AvaloniaFact]
    public async Task Rescan_replaces_the_previous_receipt()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();

        await rig.Vm.RescanAsync();

        Assert.Single(rig.Receipts());
        Assert.True(rig.Vm.HasLatest);
    }

    [AvaloniaFact]
    public async Task Next_scan_clears_the_previous_temp_file()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();
        await rig.Vm.ScanAsync();

        Assert.Single(Directory.GetFiles(rig.Paths.TempFolder));
        Assert.Equal(2, rig.Receipts().Length);
    }

    [AvaloniaFact]
    public void Opening_a_tile_navigates_and_retargets_and_breadcrumb_goes_back()
    {
        using var rig = new Rig();

        rig.Vm.OpenTile(rig.Tile("Gas"));

        Assert.Equal("Scan to Gas", rig.Vm.ScanButtonText);
        Assert.Equal("2026", Assert.Single(rig.Vm.Tiles).Name);
        Assert.Equal(2, rig.Vm.Breadcrumbs.Count);

        rig.Vm.BreadcrumbClicked(rig.Vm.Breadcrumbs[0]);

        Assert.Equal("Scan to Receipts", rig.Vm.ScanButtonText);
        Assert.Equal(2, rig.Vm.Tiles.Count);
    }

    [AvaloniaFact]
    public void New_folder_becomes_the_target_and_bad_names_stay_in_the_prompt()
    {
        using var rig = new Rig();
        rig.Vm.BeginNewFolder();
        rig.Vm.NamePromptText = "a/b";
        rig.Vm.CommitNamePrompt();

        Assert.True(rig.Vm.IsNamePromptOpen);
        Assert.NotEmpty(rig.Vm.NamePromptError);

        rig.Vm.NamePromptText = "Meals";
        rig.Vm.CommitNamePrompt();

        Assert.False(rig.Vm.IsNamePromptOpen);
        Assert.True(Directory.Exists(Path.Combine(rig.Base, "Meals")));
        Assert.True(rig.Tile("Meals").IsTarget);
        Assert.Equal("Scan to Meals", rig.Vm.ScanButtonText);
    }

    [AvaloniaFact]
    public async Task Renaming_the_target_keeps_scans_and_the_latest_receipt_working()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));
        await rig.Vm.ScanAsync();

        rig.Vm.BeginRename(rig.Tile("Grocery"));
        rig.Vm.NamePromptText = "Food";
        rig.Vm.CommitNamePrompt();

        Assert.Equal("Scan to Food", rig.Vm.ScanButtonText);
        rig.Vm.DeleteLatest();
        Assert.False(rig.Vm.HasLatest);
        Assert.Empty(rig.Receipts("Food"));

        await rig.Vm.ScanAsync();
        Assert.Single(rig.Receipts("Food"));
    }

    [AvaloniaFact]
    public async Task Folder_with_receipts_cannot_be_deleted()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));
        await rig.Vm.ScanAsync();

        rig.Vm.DeleteFolder(rig.Tile("Grocery"));

        Assert.Single(rig.Receipts("Grocery"));
        Assert.Contains("empty", rig.Vm.Notice);
    }

    [AvaloniaFact]
    public void Deleting_the_target_folder_falls_back_to_the_current_folder()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));

        rig.Vm.DeleteFolder(rig.Tile("Grocery"));

        Assert.Equal("Scan to Receipts", rig.Vm.ScanButtonText);
    }

    [AvaloniaFact]
    public void Target_removed_outside_the_app_falls_back_to_the_base_folder()
    {
        using var rig = new Rig();
        rig.Vm.TileClicked(rig.Tile("Grocery"));
        Directory.Delete(Path.Combine(rig.Base, "Grocery"));

        rig.Vm.Refresh();

        Assert.Equal("Scan to Receipts", rig.Vm.ScanButtonText);
        Assert.Contains("no longer exists", rig.Vm.Notice);
    }

    [AvaloniaFact]
    public void Refresh_keeps_tiles_when_nothing_changed_and_picks_up_outside_changes()
    {
        using var rig = new Rig();
        var before = rig.Tile("Grocery");

        rig.Vm.Refresh();
        Assert.Same(before, rig.Tile("Grocery"));

        File.WriteAllText(Path.Combine(rig.Base, "Grocery", "added.pdf"), "");
        rig.Vm.Refresh();
        Assert.Equal("1 receipt", rig.Tile("Grocery").CountText);
    }

    [AvaloniaFact]
    public async Task Changing_the_base_folder_takes_effect()
    {
        using var rig = new Rig();
        using var other = new TempDir();
        other.Sub("Taxes");
        rig.Platform.PickResult = other.Path;

        await rig.Vm.ChooseBaseFolderAsync();

        Assert.Equal("Taxes", Assert.Single(rig.Vm.Tiles).Name);
        Assert.Equal(Path.GetFullPath(other.Path), rig.Vm.TargetFolder);
    }

    [AvaloniaFact]
    public async Task Trim_setting_controls_the_saved_size()
    {
        using var rig = new Rig();
        await rig.Vm.ScanAsync();
        var trimmed = rig.Vm.LatestDetails;
        rig.Vm.TrimBlankSpace = false;

        await rig.Vm.ScanAsync();

        Assert.NotEqual(trimmed, rig.Vm.LatestDetails);
        Assert.StartsWith("216 x 300", rig.Vm.LatestDetails);
    }

    [AvaloniaFact]
    public async Task Pressing_space_scans_but_not_while_a_dialog_is_open()
    {
        using var rig = new Rig();
        var window = new MainWindow { DataContext = rig.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        rig.Vm.BeginNewFolder();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(rig.Vm.IsScanning);
        Assert.Empty(rig.Receipts());
        rig.Vm.CancelNamePrompt();

        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        await WaitUntil(() => rig.Receipts().Length == 1);

        window.Close();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
