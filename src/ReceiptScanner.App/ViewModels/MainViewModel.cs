using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReceiptScanner.Core.Imaging;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int PreviewWidth = 640;

    /// <summary>The scanner can stay silent while it feeds, so this only offers a choice. It never ends a scan.</summary>
    public static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromMinutes(5);

    private readonly IScanBackend _backend;
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly AppSettings _settings;
    private readonly IPlatformActions _platform;
    private readonly TimeSpan _stallThreshold;
    private readonly SemaphoreSlim _scannerGate = new(1, 1);

    private FolderService _folders;
    private CancellationTokenSource? _stopWaiting;
    private LatestScan? _latest;
    private FolderTileViewModel? _renaming;
    private bool _creatingFolder;

    public MainViewModel(
        IScanBackend backend,
        AppPaths paths,
        AppLog log,
        AppSettings settings,
        IPlatformActions platform,
        TimeSpan? stallThreshold = null)
    {
        _backend = backend;
        _paths = paths;
        _log = log;
        _settings = settings;
        _platform = platform;
        _stallThreshold = stallThreshold ?? DefaultStallThreshold;

        Directory.CreateDirectory(settings.BaseFolder);
        _folders = new FolderService(settings.BaseFolder);

        _selectedDpi = settings.Dpi;
        _selectedColor = ColorChoices.First(c => c.Value == settings.Color);
        _selectedFormat = FormatChoices.First(c => c.Value == settings.Format);
        _selectedLongPaperMethod = LongPaperChoices.First(c => c.Value == settings.LongPaper);
        _trimBlankSpace = settings.TrimBlankSpace;
        _baseFolder = _folders.BaseFolder;

        var target = settings.TargetFolder;
        var start = target is not null && Directory.Exists(target) && _folders.IsInsideBase(target) ? target : _folders.BaseFolder;
        _targetFolder = Path.GetFullPath(start);
        _currentFolder = _targetFolder;
        Refresh();
    }

    public bool IsSimulated => _backend.IsSimulated;

    public IReadOnlyList<int> DpiChoices => AppSettings.AllowedDpi;

    public IReadOnlyList<Choice<ColorMode>> ColorChoices { get; } =
    [
        new("Gray", ColorMode.Gray),
        new("Color", ColorMode.Color),
    ];

    public IReadOnlyList<Choice<OutputFormat>> FormatChoices { get; } =
    [
        new("PDF", OutputFormat.Pdf),
        new("PNG", OutputFormat.Png),
        new("PDF + PNG", OutputFormat.Both),
    ];

    public IReadOnlyList<Choice<LongPaperStrategy>> LongPaperChoices { get; } =
    [
        new("Automatic", LongPaperStrategy.Auto),
        new("Brother's dialog on every scan", LongPaperStrategy.BrotherDialog),
    ];

    public ObservableCollection<FolderTileViewModel> Tiles { get; } = [];

    public ObservableCollection<BreadcrumbViewModel> Breadcrumbs { get; } = [];

    public ObservableCollection<string> ErrorSteps { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] private int _selectedDpi;
    [ObservableProperty] private Choice<ColorMode> _selectedColor;
    [ObservableProperty] private Choice<OutputFormat> _selectedFormat;
    [ObservableProperty] private Choice<LongPaperStrategy> _selectedLongPaperMethod;
    [ObservableProperty] private bool _trimBlankSpace;
    [ObservableProperty] private string _baseFolder;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScanButtonText))] private string _targetFolder;
    [ObservableProperty] private string _currentFolder;
    [ObservableProperty] private bool _hasNoFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(IsIdleHintVisible))]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(IsIdleHintVisible))]
    [NotifyPropertyChangedFor(nameof(CanUseLatestActions))]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private bool _isProcessing;

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _showStopWaiting;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdleHintVisible))] private bool _hasError;
    [ObservableProperty] private string _errorTitle = "";
    [ObservableProperty] private bool _errorOffersLongPaperChoice;

    [ObservableProperty] private string _notice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseLatestActions))]
    private bool _hasLatest;

    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string _latestTitle = "";
    [ObservableProperty] private string _latestDetails = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(IsIdleHintVisible))]
    [NotifyPropertyChangedFor(nameof(CanUseLatestActions))]
    private bool _needsReview;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanScan))] private bool _isMoving;

    [ObservableProperty] private string _scannerStatus = "Checking scanner";
    [ObservableProperty] private bool _scannerFound;

    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private string _settingsMessage = "";
    [ObservableProperty] private bool _isSettingsBusy;

    [ObservableProperty] private bool _isNamePromptOpen;
    [ObservableProperty] private string _namePromptTitle = "";
    [ObservableProperty] private string _namePromptText = "";
    [ObservableProperty] private string _namePromptError = "";

    public bool CanScan => !IsScanning && !IsProcessing && !NeedsReview && !IsMoving;

    public bool IsIdleHintVisible => CanScan && !HasError;

    /// <summary>Rotate, Move, Rescan and Delete apply once a scan has been saved.</summary>
    public bool CanUseLatestActions => HasLatest && !IsProcessing && !NeedsReview;

    public string TargetName => _folders.Breadcrumb(TargetFolder)[^1].Name;

    public string ScanButtonText => IsScanning ? "Scanning" : IsProcessing ? "Saving" : $"Scan to {TargetName}";

    partial void OnSelectedDpiChanged(int value)
    {
        _settings.Dpi = value;
        SaveSettings();
    }

    partial void OnSelectedColorChanged(Choice<ColorMode> value)
    {
        _settings.Color = value.Value;
        SaveSettings();
    }

    partial void OnSelectedFormatChanged(Choice<OutputFormat> value)
    {
        _settings.Format = value.Value;
        SaveSettings();
    }

    partial void OnSelectedLongPaperMethodChanged(Choice<LongPaperStrategy> value)
    {
        _settings.LongPaper = value.Value;
        SaveSettings();
    }

    partial void OnTrimBlankSpaceChanged(bool value)
    {
        _settings.TrimBlankSpace = value;
        SaveSettings();
    }

    /// <summary>Checks for the scanner now, then keeps checking only while it's missing.</summary>
    public async Task MonitorScannerAsync(CancellationToken cancellationToken)
    {
        await ProbeScannerAsync(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!ScannerFound)
                {
                    await ProbeScannerAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
    }

    [RelayCommand]
    public async Task ProbeScannerAsync(CancellationToken cancellationToken = default)
    {
        // A scan already in progress holds the scanner, and shows its own status.
        if (!await _scannerGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var status = await _backend.ProbeAsync(cancellationToken);
            ScannerFound = status.Found;
            ScannerStatus = status.Summary;
            if (status.Detail is not null)
            {
                _log.Info($"Probe: {status.Summary}: {status.Detail}");
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        finally
        {
            _scannerGate.Release();
        }
    }

    [RelayCommand]
    public Task ScanAsync() => RunScanAsync(allowUnconfirmed: false);

    [RelayCommand]
    public Task ScanAnywayAsync() => RunScanAsync(allowUnconfirmed: true);

    [RelayCommand]
    public void StopWaiting() => _stopWaiting?.Cancel();

    private async Task RunScanAsync(bool allowUnconfirmed)
    {
        if (!CanScan)
        {
            return;
        }

        ClearError();
        Notice = "";
        IsScanning = true;
        ShowStopWaiting = false;
        ProgressText = "Starting the scanner";

        var folder = TargetFolder;
        var rawPath = _paths.NewRawPath();
        var startedAt = DateTime.Now;
        _stopWaiting = new CancellationTokenSource();
        var lastActivity = DateTime.UtcNow;
        var dataSeen = false;

        var progress = new Progress<ScanProgress>(p =>
        {
            lastActivity = DateTime.UtcNow;
            ShowStopWaiting = false;
            if (p.DataFlowing)
            {
                dataSeen = true;
                ProgressText = $"Scanning. {p.MillimetersReceived:0} mm so far. Keep supporting the receipt.";
            }
            else if (!dataSeen)
            {
                ProgressText = "Feeding the receipt. The scanner may finish feeding before the image appears.";
            }
        });

        using var watchDone = new CancellationTokenSource();
        var watch = WatchForStallAsync(() => lastActivity, watchDone.Token);

        ScanOutcome outcome;
        await _scannerGate.WaitAsync();
        try
        {
            var request = new ScanRequest(
                SelectedDpi,
                SelectedColor.Value,
                rawPath,
                SelectedLongPaperMethod.Value,
                _paths.LongPaperProfile,
                allowUnconfirmed);
            _log.Info($"Scan started: {request.Dpi} dpi, {request.Color}, method {request.Strategy}, allow unconfirmed: {allowUnconfirmed}");
            outcome = await _backend.ScanAsync(request, progress, _stopWaiting.Token);
        }
        catch (Exception ex)
        {
            _log.Error($"Scan threw {ex.GetType().Name}: {ex.Message}");
            outcome = new ScanOutcome.Failed(ScanErrorCode.Unknown, ex.Message, null);
        }
        finally
        {
            _scannerGate.Release();
            await watchDone.CancelAsync();
            await watch;
            _stopWaiting.Dispose();
            _stopWaiting = null;
            IsScanning = false;
            ShowStopWaiting = false;
            ProgressText = "";
        }

        if (outcome is ScanOutcome.Failed failed)
        {
            ReportFailure(failed);
            TryDelete(rawPath);
            return;
        }

        ScannerFound = true;
        ScannerStatus = _backend.IsSimulated ? "Simulated DS-640" : "DS-640 ready";
        await ProcessAsync((ScanOutcome.Completed)outcome, folder, startedAt);
    }

    private async Task WatchForStallAsync(Func<DateTime> lastActivity, CancellationToken cancellationToken)
    {
        try
        {
            var interval = TimeSpan.FromMilliseconds(Math.Clamp(_stallThreshold.TotalMilliseconds / 4, 20, 5000));
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (DateTime.UtcNow - lastActivity() >= _stallThreshold)
                {
                    ShowStopWaiting = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The scan ended.
        }
    }

    private void ReportFailure(ScanOutcome.Failed failed)
    {
        var description = ScanErrorText.Describe(failed.Code);
        _log.Error($"Scan failed: {failed.Code}; detail: {failed.Detail}; Long Paper: {failed.LongPaper?.Method} confirmed {failed.LongPaper?.Confirmed}");
        ErrorTitle = description.Title;
        ErrorSteps.Clear();
        foreach (var step in description.Steps)
        {
            ErrorSteps.Add(step);
        }

        ErrorOffersLongPaperChoice = failed.Code == ScanErrorCode.LongPaperUnconfirmed;
        HasError = true;
        if (failed.Code is ScanErrorCode.ScannerNotFound or ScanErrorCode.Disconnected or ScanErrorCode.DriverMissing)
        {
            ScannerFound = false;
            ScannerStatus = description.Title;
        }
    }

    private async Task ProcessAsync(ScanOutcome.Completed completed, string folder, DateTime timestamp)
    {
        IsProcessing = true;
        ClearLatest();
        var image = completed.Image;
        var trim = TrimBlankSpace;
        var format = SelectedFormat.Value;
        try
        {
            var result = await Task.Run(() =>
            {
                if (!image.IsIntact())
                {
                    throw new InvalidDataException("The scan data doesn't match its reported size.");
                }

                var analysis = ImageAnalysis.Analyze(image);
                var assessment = Completeness.Assess(image, analysis, completed.Status);
                var region = trim ? analysis.TrimRegion : PixelRegion.Full(image);
                var preview = ImageOps.BuildPreview(image, region, PreviewWidth);
                SavedReceipt? saved = assessment.IsClean ? ReceiptStore.Save(image, region, folder, format, timestamp) : null;
                return (assessment, region, preview, saved);
            });

            _latest = new LatestScan(image, result.region, folder, format, timestamp, result.assessment) { Saved = result.saved };
            HasLatest = true;
            PreviewImage = ToBitmap(result.preview);
            UpdateLatestText();
            _log.Info($"Assessed: clean {result.assessment.IsClean}, reasons [{string.Join(",", result.assessment.Reasons)}], kept {result.region.Width}x{result.region.Height} px");

            if (result.saved is null)
            {
                NeedsReview = true;
                foreach (var message in result.assessment.Messages)
                {
                    Warnings.Add(message);
                }
            }
            else
            {
                Notice = $"Saved to {TargetNameOf(folder)}";
                Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OutOfMemoryException)
        {
            HandleProcessingFailure(ex, image);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void HandleProcessingFailure(Exception ex, RawImage image)
    {
        _log.Error($"Processing failed: {ex.GetType().Name}: {ex.Message}");
        if (_latest is not null || image.IsIntact())
        {
            // The scan itself is fine, so it stays available for another save attempt.
            _latest ??= new LatestScan(image, PixelRegion.Full(image), TargetFolder, SelectedFormat.Value, DateTime.Now, new CompletenessResult([]));
            HasLatest = true;
            NeedsReview = true;
            UpdateLatestText();
            Warnings.Add($"The receipt couldn't be saved: {ex.Message}");
        }
        else
        {
            TryDelete(image.Path);
            ErrorTitle = "The scan couldn't be processed. Nothing was saved";
            ErrorSteps.Clear();
            ErrorSteps.Add("Scan the receipt again.");
            ErrorOffersLongPaperChoice = false;
            HasError = true;
        }
    }

    /// <summary>Saves a held scan. Scans with warnings get _UNVERIFIED in the name so they stay identifiable.</summary>
    [RelayCommand]
    public async Task KeepAsync()
    {
        if (_latest is not { Saved: null } latest)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            var unverified = !latest.Assessment.IsClean;
            var saved = await Task.Run(() => ReceiptStore.Save(latest.Image, latest.Region, TargetFolder, SelectedFormat.Value, latest.Timestamp, unverified));
            latest.Saved = saved;
            latest.Format = SelectedFormat.Value;
            NeedsReview = false;
            Warnings.Clear();
            UpdateLatestText();
            Notice = $"Saved to {TargetName}" + (unverified ? ", marked UNVERIFIED" : "");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Error($"Save failed: {ex.GetType().Name}: {ex.Message}");
            Warnings.Clear();
            Warnings.Add($"The receipt couldn't be saved: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public void Discard()
    {
        ClearLatest();
        Notice = "Scan discarded. Nothing was saved.";
    }

    [RelayCommand]
    public async Task RotateAsync()
    {
        if (_latest is not { } latest || IsProcessing)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            var newPath = _paths.NewRawPath();
            var (rotated, preview, saved) = await Task.Run(() =>
            {
                var image = ImageOps.Rotate(latest.Image, latest.Region, 1, newPath);
                var region = PixelRegion.Full(image);
                var receipt = latest.Saved is null ? null : ReceiptStore.Replace(image, region, latest.Saved, latest.Format);
                return (image, ImageOps.BuildPreview(image, region, PreviewWidth), receipt);
            });

            TryDelete(latest.Image.Path);
            latest.Image = rotated;
            latest.Region = PixelRegion.Full(rotated);
            latest.Saved = saved;
            PreviewImage = ToBitmap(preview);
            UpdateLatestText();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OutOfMemoryException or OverflowException)
        {
            _log.Error($"Rotate failed: {ex.GetType().Name}: {ex.Message}");
            Notice = $"The receipt couldn't be rotated: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public void BeginMove()
    {
        if (_latest?.Saved is not null)
        {
            IsMoving = true;
            Notice = "";
        }
    }

    [RelayCommand]
    public void CancelMove() => IsMoving = false;

    [RelayCommand]
    public void MoveHere() => MoveLatestTo(CurrentFolder);

    private void MoveLatestTo(string folder)
    {
        IsMoving = false;
        if (_latest?.Saved is not { } saved)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(saved.Folder), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
        {
            Notice = $"Already in {TargetNameOf(folder)}";
            return;
        }

        try
        {
            _latest.Saved = ReceiptStore.Move(saved, folder);
            UpdateLatestText();
            Notice = $"Moved to {TargetNameOf(folder)}";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Move failed: {ex.GetType().Name}: {ex.Message}");
            Notice = $"The receipt couldn't be moved: {ex.Message}";
        }
    }

    /// <summary>Sends the saved receipt to the Recycle Bin, then scans again into the same folder.</summary>
    [RelayCommand]
    public async Task RescanAsync()
    {
        if (RecycleLatest())
        {
            await RunScanAsync(allowUnconfirmed: false);
        }
    }

    [RelayCommand]
    public void DeleteLatest()
    {
        if (RecycleLatest())
        {
            Notice = "Moved to the Recycle Bin.";
        }
    }

    private bool RecycleLatest()
    {
        if (_latest?.Saved is not { } saved)
        {
            return false;
        }

        try
        {
            ReceiptStore.Recycle(saved);
            ClearLatest();
            Refresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Recycle failed: {ex.GetType().Name}: {ex.Message}");
            Notice = $"The receipt couldn't be moved to the Recycle Bin, so it was kept: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    public void TileClicked(FolderTileViewModel tile)
    {
        if (IsMoving)
        {
            MoveLatestTo(tile.Path);
            return;
        }

        SetTarget(tile.Path);
    }

    [RelayCommand]
    public void OpenTile(FolderTileViewModel tile) => Navigate(tile.Path, retarget: !IsMoving);

    [RelayCommand]
    public void BreadcrumbClicked(BreadcrumbViewModel crumb) => Navigate(crumb.Path, retarget: !IsMoving);

    private void Navigate(string folder, bool retarget)
    {
        CurrentFolder = Path.GetFullPath(folder);
        if (retarget)
        {
            SetTarget(CurrentFolder);
        }

        Refresh();
    }

    private void SetTarget(string folder)
    {
        TargetFolder = Path.GetFullPath(folder);
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(ScanButtonText));
        foreach (var tile in Tiles)
        {
            tile.IsTarget = PathEquals(tile.Path, TargetFolder);
        }

        _settings.TargetFolder = TargetFolder;
        SaveSettings();
    }

    /// <summary>Reloads tiles and breadcrumbs, recovering if folders were changed outside the app.</summary>
    public void Refresh()
    {
        try
        {
            if (!Directory.Exists(CurrentFolder))
            {
                CurrentFolder = _folders.BaseFolder;
            }

            if (!Directory.Exists(TargetFolder))
            {
                Notice = "The selected folder no longer exists, so scans now go to the base folder.";
                SetTarget(_folders.BaseFolder);
            }

            // Rebuilding unchanged tiles could swallow a click, because Refresh also runs when the window
            // regains focus, which can happen between a tile's press and release.
            var entries = _folders.List(CurrentFolder);
            if (!Tiles.Select(t => t.Entry).SequenceEqual(entries))
            {
                Tiles.Clear();
                foreach (var entry in entries)
                {
                    Tiles.Add(new FolderTileViewModel(entry));
                }
            }

            foreach (var tile in Tiles)
            {
                tile.IsTarget = PathEquals(tile.Path, TargetFolder);
            }

            HasNoFolders = Tiles.Count == 0;

            var crumbs = _folders.Breadcrumb(CurrentFolder);
            Breadcrumbs.Clear();
            for (var i = 0; i < crumbs.Count; i++)
            {
                Breadcrumbs.Add(new BreadcrumbViewModel(crumbs[i].Name, crumbs[i].Path, i == crumbs.Count - 1));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Couldn't read folders: {ex.GetType().Name}: {ex.Message}");
            Notice = $"Couldn't read that folder: {ex.Message}";
        }
    }

    [RelayCommand]
    public void BeginNewFolder()
    {
        _creatingFolder = true;
        _renaming = null;
        NamePromptTitle = "New folder";
        NamePromptText = "";
        NamePromptError = "";
        IsNamePromptOpen = true;
    }

    [RelayCommand]
    public void BeginRename(FolderTileViewModel tile)
    {
        _creatingFolder = false;
        _renaming = tile;
        NamePromptTitle = "Rename folder";
        NamePromptText = tile.Name;
        NamePromptError = "";
        IsNamePromptOpen = true;
    }

    [RelayCommand]
    public void CancelNamePrompt() => IsNamePromptOpen = false;

    [RelayCommand]
    public void CommitNamePrompt()
    {
        try
        {
            if (_creatingFolder)
            {
                var created = _folders.Create(CurrentFolder, NamePromptText);
                Refresh();
                if (!IsMoving)
                {
                    SetTarget(created.Path);
                }
            }
            else if (_renaming is { } tile)
            {
                var renamed = _folders.Rename(tile.Path, NamePromptText);
                RemapPaths(tile.Path, renamed);
                Refresh();
            }

            IsNamePromptOpen = false;
        }
        catch (FolderNameException ex)
        {
            NamePromptError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NamePromptError = ex.Message;
        }
    }

    [RelayCommand]
    public void DeleteFolder(FolderTileViewModel tile)
    {
        try
        {
            _folders.DeleteEmpty(tile.Path);
            if (IsSameOrInside(TargetFolder, tile.Path))
            {
                SetTarget(CurrentFolder);
            }

            Notice = $"Deleted {tile.Name}";
            Refresh();
        }
        catch (Exception ex) when (ex is FolderNameException or IOException or UnauthorizedAccessException)
        {
            Notice = ex.Message;
        }
    }

    [RelayCommand]
    public void OpenTileInFileManager(FolderTileViewModel tile) => _platform.OpenInFileManager(tile.Path);

    [RelayCommand]
    public void OpenSettings()
    {
        SettingsMessage = "";
        IsSettingsOpen = true;
    }

    [RelayCommand]
    public void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    public async Task ChooseBaseFolderAsync()
    {
        var picked = await _platform.PickFolderAsync(BaseFolder);
        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        _folders = new FolderService(picked);
        BaseFolder = _folders.BaseFolder;
        _settings.BaseFolder = BaseFolder;
        CurrentFolder = BaseFolder;
        SetTarget(BaseFolder);
        Refresh();
    }

    [RelayCommand]
    public async Task SetUpLongPaperAsync()
    {
        ClearError();
        IsSettingsBusy = true;
        SettingsMessage = "Follow Brother's dialog: set Document Size to Long Paper, then click OK.";
        await _scannerGate.WaitAsync();
        try
        {
            var result = await _backend.SetupLongPaperAsync(_paths.LongPaperProfile);
            SettingsMessage = result.Message;
            _log.Info($"Long Paper setup: success {result.Success}; {result.Message}");
        }
        finally
        {
            _scannerGate.Release();
            IsSettingsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenLongPaperSetupAsync()
    {
        IsSettingsOpen = true;
        await SetUpLongPaperAsync();
    }

    [RelayCommand]
    public async Task RunDriverCheckAsync()
    {
        IsSettingsBusy = true;
        SettingsMessage = "Follow Brother's dialog: set Document Size to Long Paper, then click OK.";
        var report = Path.Combine(_log.Folder, $"twain-capabilities-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md");
        await _scannerGate.WaitAsync();
        try
        {
            var result = await _backend.RunDriverCheckAsync(report);
            SettingsMessage = result.Success ? $"Report saved in the log folder: {Path.GetFileName(report)}" : result.Message;
            _log.Info($"Driver check: success {result.Success}; {result.Message}");
        }
        finally
        {
            _scannerGate.Release();
            IsSettingsBusy = false;
        }
    }

    [RelayCommand]
    public void OpenLogFolder() => _platform.OpenInFileManager(_log.Folder);

    [RelayCommand]
    public void DismissError() => ClearError();

    private void ClearError()
    {
        HasError = false;
        ErrorOffersLongPaperChoice = false;
        ErrorSteps.Clear();
    }

    private void ClearLatest()
    {
        if (_latest is not null)
        {
            TryDelete(_latest.Image.Path);
            _latest = null;
        }

        HasLatest = false;
        NeedsReview = false;
        IsMoving = false;
        Warnings.Clear();
        PreviewImage = null;
        LatestTitle = "";
        LatestDetails = "";
    }

    private void UpdateLatestText()
    {
        if (_latest is not { } latest)
        {
            return;
        }

        var dpi = latest.Image.Dpi;
        var size = $"{latest.Region.Width * 25.4 / dpi:0} x {latest.Region.Height * 25.4 / dpi:0} mm, {dpi} dpi";
        if (latest.Saved is { } saved)
        {
            LatestTitle = saved.BaseName;
            LatestDetails = $"{size}\nIn {TargetNameOf(saved.Folder)}";
        }
        else
        {
            LatestTitle = "Check this scan";
            LatestDetails = $"{size}\nNot saved yet";
        }
    }

    private string TargetNameOf(string folder) =>
        _folders.IsInsideBase(folder) ? _folders.Breadcrumb(folder)[^1].Name : Path.GetFileName(folder);

    private void RemapPaths(string oldPath, string newPath)
    {
        string Remap(string path) => newPath + path[oldPath.Length..];

        if (IsSameOrInside(TargetFolder, oldPath))
        {
            SetTarget(Remap(Path.GetFullPath(TargetFolder)));
        }

        if (IsSameOrInside(CurrentFolder, oldPath))
        {
            CurrentFolder = Remap(Path.GetFullPath(CurrentFolder));
        }

        if (_latest?.Saved is { } saved && IsSameOrInside(saved.Folder, oldPath))
        {
            _latest.Saved = saved with { Folder = Remap(saved.Folder), Files = saved.Files.Select(Remap).ToList() };
            UpdateLatestText();
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder);
        return PathEquals(full, root) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static Bitmap ToBitmap(PreviewImage preview)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(preview.Width, preview.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var stride = preview.Width * 4;
        for (var y = 0; y < preview.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(preview.Bgra, y * stride, buffer.Address + (y * buffer.RowBytes), stride);
        }

        return bitmap;
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save(_paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Settings couldn't be saved: {ex.GetType().Name}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp files are also cleared at the next launch.
        }
    }

    private sealed class LatestScan(RawImage image, PixelRegion region, string folder, OutputFormat format, DateTime timestamp, CompletenessResult assessment)
    {
        public RawImage Image { get; set; } = image;

        public PixelRegion Region { get; set; } = region;

        public string Folder { get; } = folder;

        public OutputFormat Format { get; set; } = format;

        public DateTime Timestamp { get; } = timestamp;

        public CompletenessResult Assessment { get; } = assessment;

        public SavedReceipt? Saved { get; set; }
    }
}
