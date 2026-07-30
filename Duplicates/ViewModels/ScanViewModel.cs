using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine;
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
    private CancellationTokenSource? _scanCancellation;
    private DateTimeOffset _scanStartedAt;

    public ScanViewModel(DuplicateScanner scanner, ISettingsService settingsService, ResultsStore resultsStore)
    {
        _scanner = scanner;
        _settingsService = settingsService;
        _resultsStore = resultsStore;
        Folders.CollectionChanged += FoldersChanged;
        _settingsService.SettingsChanged += SettingsChanged;
        ResetFromSettings();
    }

    public event EventHandler<ScanResult>? ScanCompleted;

    public ObservableCollection<ScanFolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    public partial bool IncludeSubfolders { get; set; } = true;

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

    [ObservableProperty]
    public partial bool IgnoreHiddenFiles { get; set; } = true;

    [ObservableProperty]
    public partial bool IgnoreSystemFiles { get; set; } = true;

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

    public bool HasFolders => Folders.Count > 0;

    public Visibility EmptyFoldersVisibility => HasFolders ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CategoryFiltersVisibility => SelectedFileFilterIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CustomExtensionsVisibility => SelectedFileFilterIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsStatusOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    public void ResetFromSettings()
    {
        AppSettings settings = _settingsService.Current;
        MinSizeValue = ByteSizeInput.FromBytes(settings.DefaultMinSizeBytes);
        IgnoreHiddenFiles = settings.IgnoreHiddenFiles;
        IgnoreSystemFiles = settings.IgnoreSystemFiles;
        VerifyByteByByte = settings.VerifyByteByByte;
    }

    public void AddFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var item = new ScanFolderViewModel(folder);
        if (Folders.Any(existing =>
            string.Equals(existing.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Folders.Add(item);
    }

    [RelayCommand]
    private void RemoveFolder(ScanFolderViewModel folder)
    {
        Folders.Remove(folder);
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        IsScanning = true;
        StatusMessage = string.Empty;
        _scanStartedAt = DateTimeOffset.Now;
        _scanCancellation = new CancellationTokenSource();

        try
        {
            ScanOptions options = BuildScanOptions();
            var progress = new Progress<ScanProgress>(UpdateProgress);
            ScanResult result = await _scanner.ScanAsync(options, progress, _scanCancellation.Token);
            _resultsStore.SetResult(result);
            ScanCompleted?.Invoke(this, result);
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
        return !IsScanning && Folders.Count > 0;
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
            Folders = Folders.Select(static folder => folder.FullPath).ToArray(),
            IncludeSubfolders = IncludeSubfolders,
            MinSizeBytes = minSize,
            MaxSizeBytes = maxSize,
            TypeFilter = BuildFileTypeFilter(),
            IgnoreHiddenFiles = IgnoreHiddenFiles,
            IgnoreSystemFiles = IgnoreSystemFiles,
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
        TimeSpan elapsed = DateTimeOffset.Now - _scanStartedAt;
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
    }

    private void FoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(EmptyFoldersVisibility));
        StartScanCommand.NotifyCanExecuteChanged();
    }

    private void SettingsChanged(object? sender, AppSettings settings)
    {
        if (!IsScanning)
        {
            ResetFromSettings();
        }
    }
}
