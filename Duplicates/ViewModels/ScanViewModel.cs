using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed partial class ScanViewModel : ObservableObject
{
    private readonly DuplicateScanner _scanner;
    private readonly ISettingsService _settingsService;
    private readonly ResultsStore _resultsStore;
    private readonly PathScopeViewModel _pathScope;
    private readonly IAppOperationCoordinator _operationCoordinator;
    private AppSettings? _pendingSettings;
    private CancellationTokenSource? _scanCancellation;
    private DateTimeOffset _scanStartedAt;

    public ScanViewModel(
        DuplicateScanner scanner,
        ISettingsService settingsService,
        ResultsStore resultsStore,
        PathScopeViewModel pathScope,
        IAppOperationCoordinator? operationCoordinator = null)
    {
        _scanner = scanner;
        _settingsService = settingsService;
        _resultsStore = resultsStore;
        _pathScope = pathScope;
        _operationCoordinator = operationCoordinator ?? new AppOperationCoordinator();
        _pathScope.PropertyChanged += PathScopeChanged;
        _settingsService.SettingsChanged += SettingsChanged;
        _operationCoordinator.ActiveOperationChanged += OperationChanged;
        ResetFromSettings();
    }

    public event EventHandler<ScanResult>? ScanCompleted;

    public PathScopeViewModel PathScope => _pathScope;

    public bool IncludeSubfolders
    {
        get => PathScope.IncludeSubfolders;
        set => PathScope.IncludeSubfolders = value;
    }

    [ObservableProperty]
    public partial double MinSizeValue { get; set; } = 1d;

    [ObservableProperty]
    public partial double MaxSizeValue { get; set; } = ByteSizeInput.NoMaximum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryFiltersVisibility))]
    [NotifyPropertyChangedFor(nameof(CustomExtensionsVisibility))]
    public partial int SelectedFileFilterIndex { get; set; }

    [ObservableProperty]
    public partial string CustomExtensionsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ImagesSelected { get; set; }

    [ObservableProperty]
    public partial bool VideoSelected { get; set; }

    [ObservableProperty]
    public partial bool AudioSelected { get; set; }

    [ObservableProperty]
    public partial bool DocumentsSelected { get; set; }

    [ObservableProperty]
    public partial bool ArchivesSelected { get; set; }

    [ObservableProperty]
    public partial bool CodeSelected { get; set; }

    public bool IgnoreHiddenFiles
    {
        get => PathScope.IgnoreHiddenFiles;
        set => PathScope.IgnoreHiddenFiles = value;
    }

    public bool IgnoreSystemFiles
    {
        get => PathScope.IgnoreSystemFiles;
        set => PathScope.IgnoreSystemFiles = value;
    }

    [ObservableProperty]
    public partial bool VerifyByteByByte { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupVisibility))]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string PhaseText { get; set; } = "Ready";

    [ObservableProperty]
    public partial string FilesDiscoveredText { get; set; } = "0";

    [ObservableProperty]
    public partial string FilesProcessedText { get; set; } = "0";

    [ObservableProperty]
    public partial string BytesProcessedText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "0s";

    [ObservableProperty]
    public partial string EtaText { get; set; } = "-";

    [ObservableProperty]
    public partial string CurrentFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOpen))]
    public partial string StatusMessage { get; set; } = string.Empty;

    public Visibility SetupVisibility => IsScanning ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ProgressVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CategoryFiltersVisibility => SelectedFileFilterIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CustomExtensionsVisibility => SelectedFileFilterIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsStatusOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    public void ResetFromSettings() => ApplySettings(_settingsService.Current);

    private void ApplySettings(AppSettings settings)
    {
        MinSizeValue = ByteSizeInput.FromBytes(settings.DefaultMinSizeBytes);
        PathScope.IncludeSubfolders = settings.DefaultIncludeSubfolders;
        PathScope.IgnoreHiddenFiles = settings.IgnoreHiddenFiles;
        PathScope.IgnoreSystemFiles = settings.IgnoreSystemFiles;
        VerifyByteByByte = settings.VerifyByteByByte;
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        var cancellation = new CancellationTokenSource();
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(AppOperationKind.ExactScan),
                cancellation.Cancel,
                out IAppOperationLease? lease))
        {
            cancellation.Dispose();
            StatusMessage = "Another operation is already running.";
            return;
        }

        IsScanning = true;
        StatusMessage = string.Empty;
        _scanStartedAt = DateTimeOffset.UtcNow;
        _scanCancellation = cancellation;
        ScanResult? completedResult = null;
        AnalysisScope? completedScope = null;
        DateTimeOffset completedAt = default;

        try
        {
            ScanOptions options = BuildScanOptions();
            var progress = new Progress<ScanProgress>(UpdateProgress);
            ScanResult result = await Task.Run(
                () => _scanner.ScanAsync(options, progress, _scanCancellation.Token),
                _scanCancellation.Token);
            completedResult = result;
            completedScope = BuildResultScope(options);
            completedAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            IsScanning = false;
            StartScanCommand.NotifyCanExecuteChanged();
            lease!.Dispose();
        }

        if (completedResult is not null)
        {
            _resultsStore.SetResult(completedResult, completedScope!, completedAt);
            ScanCompleted?.Invoke(this, completedResult);
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCancellation?.Cancel();
    }

    [RelayCommand]
    private void UseAnySize()
    {
        MinSizeValue = 0d;
        MaxSizeValue = ByteSizeInput.NoMaximum;
    }

    [RelayCommand]
    private void UseOneKilobyteMinimum()
    {
        MinSizeValue = 1024d;
    }

    [RelayCommand]
    private void UseOneMegabyteMinimum()
    {
        MinSizeValue = 1_048_576d;
    }

    private bool CanStartScan()
    {
        return !IsScanning &&
            _operationCoordinator.ActiveOperation is null &&
            PathScope.HasIncludedPaths;
    }

    private ScanOptions BuildScanOptions()
    {
        long minSize = ByteSizeInput.ToBytes(
            MinSizeValue,
            _settingsService.Current.DefaultMinSizeBytes);
        long maxSize = ByteSizeInput.ToBytes(
            MaxSizeValue,
            long.MaxValue,
            noValueMeansMaximum: true);

        return new ScanOptions
        {
            Folders = PathScope.IncludedPaths
                .Where(static path => path.Kind == ScopePathKind.Folder)
                .Select(static path => path.FullPath)
                .ToArray(),
            Files = PathScope.IncludedPaths
                .Where(static path => path.Kind == ScopePathKind.File)
                .Select(static path => path.FullPath)
                .ToArray(),
            ExcludedPaths = PathScope.ExcludedPaths.Select(static path => path.FullPath).ToArray(),
            IncludeSubfolders = PathScope.IncludeSubfolders,
            MinSizeBytes = minSize,
            MaxSizeBytes = maxSize,
            TypeFilter = BuildFileTypeFilter(),
            IgnoreHiddenFiles = PathScope.IgnoreHiddenFiles,
            IgnoreSystemFiles = PathScope.IgnoreSystemFiles,
            VerifyByteByByte = VerifyByteByByte,
            MaxHashingConcurrency = _settingsService.Current.MaxHashingConcurrency,
        };
    }

    private FileTypeFilter BuildFileTypeFilter()
    {
        return SelectedFileFilterIndex switch
        {
            1 => FileTypeFilter.ForCategories(GetSelectedCategories()),
            2 => FileTypeFilter.ForCustomExtensions(CustomExtensionsText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)),
            _ => FileTypeFilter.All,
        };
    }

    private static AnalysisScope BuildResultScope(ScanOptions options) => new()
    {
        IncludedFolders = options.Folders.ToArray(),
        IncludedFiles = options.Files.ToArray(),
        ExcludedPaths = options.ExcludedPaths.ToArray(),
        IncludeSubfolders = options.IncludeSubfolders,
        IgnoreHiddenFiles = options.IgnoreHiddenFiles,
        IgnoreSystemFiles = options.IgnoreSystemFiles,
    };

    private IEnumerable<FileTypeCategory> GetSelectedCategories()
    {
        if (ImagesSelected)
        {
            yield return FileTypeCategory.Images;
        }

        if (VideoSelected)
        {
            yield return FileTypeCategory.Video;
        }

        if (AudioSelected)
        {
            yield return FileTypeCategory.Audio;
        }

        if (DocumentsSelected)
        {
            yield return FileTypeCategory.Documents;
        }

        if (ArchivesSelected)
        {
            yield return FileTypeCategory.Archives;
        }

        if (CodeSelected)
        {
            yield return FileTypeCategory.Code;
        }
    }

    private void UpdateProgress(ScanProgress progress)
    {
        PhaseText = progress.Phase switch
        {
            ScanPhase.Enumerating => "Finding files...",
            ScanPhase.GroupingBySize => "Grouping by size...",
            ScanPhase.PartialHashing => "Reading file headers...",
            ScanPhase.FullHashing => "Hashing files...",
            ScanPhase.Verifying => "Verifying matches...",
            ScanPhase.Done => "Done",
            _ => "Scanning...",
        };

        FilesDiscoveredText = progress.FilesDiscovered.ToString("N0", CultureInfo.InvariantCulture);
        FilesProcessedText = progress.FilesProcessed.ToString("N0", CultureInfo.InvariantCulture);
        BytesProcessedText = ByteFormatter.Format(progress.BytesProcessed);
        CurrentFilePath = progress.CurrentFilePath ?? string.Empty;
        TimeSpan elapsed = DateTimeOffset.UtcNow - _scanStartedAt;
        ElapsedText = elapsed.TotalSeconds < 1 ? "<1s" : $"{Math.Floor(elapsed.TotalSeconds):N0}s";

        IsProgressIndeterminate = progress.TotalBytesToProcess <= 0 || progress.Phase is ScanPhase.Enumerating or ScanPhase.GroupingBySize;
        ProgressValue = progress.TotalBytesToProcess <= 0
            ? 0
            : Math.Clamp(progress.BytesProcessed * 100d / progress.TotalBytesToProcess, 0, 100);
        EtaText = BuildEta(progress, elapsed);
    }

    private static string BuildEta(ScanProgress progress, TimeSpan elapsed)
    {
        if (progress.TotalBytesToProcess <= 0 || progress.BytesProcessed <= 0)
        {
            return "-";
        }

        double remainingRatio = (progress.TotalBytesToProcess - progress.BytesProcessed) / (double)progress.BytesProcessed;
        TimeSpan remaining = TimeSpan.FromTicks((long)(elapsed.Ticks * remainingRatio));
        return remaining.TotalSeconds < 1 ? "<1s" : $"{Math.Ceiling(remaining.TotalSeconds):N0}s";
    }

    partial void OnIsScanningChanged(bool value)
    {
        StartScanCommand.NotifyCanExecuteChanged();
        TryApplyPendingSettings();
    }

    private void OperationChanged(object? sender, EventArgs e)
    {
        TryApplyPendingSettings();
        StartScanCommand.NotifyCanExecuteChanged();
    }

    private void PathScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PathScopeViewModel.IncludeSubfolders) or
            nameof(PathScopeViewModel.IgnoreHiddenFiles) or
            nameof(PathScopeViewModel.IgnoreSystemFiles))
        {
            OnPropertyChanged(e.PropertyName);
        }

        if (e.PropertyName == nameof(PathScopeViewModel.HasIncludedPaths))
        {
            StartScanCommand.NotifyCanExecuteChanged();
        }
    }

    private void SettingsChanged(object? sender, AppSettings settings)
    {
        _pendingSettings = settings;
        TryApplyPendingSettings();
    }

    private void TryApplyPendingSettings()
    {
        if (_pendingSettings is not AppSettings settings ||
            IsScanning ||
            _operationCoordinator.ActiveOperation is not null)
        {
            return;
        }

        _pendingSettings = null;
        ApplySettings(settings);
    }
}
