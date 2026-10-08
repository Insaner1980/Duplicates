using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.ViewModels;

public sealed partial class VideoOptimizerViewModel : ObservableObject
{
    private static readonly FileTypeFilter VideoFilter =
        FileTypeFilter.ForCategories([FileTypeCategory.Video]);
    private readonly IVideoOptimizerService _optimizerService;
    private readonly IFileActionService _fileActionService;
    private readonly IAppOperationCoordinator _operationCoordinator;
    private readonly Func<AnalysisScope, CancellationToken, FileInventory> _inventoryBuilder;
    private readonly Func<InventoryFile, bool> _snapshotRechecker;
    private readonly Func<Action<double>, IProgress<double>> _progressFactory;
    private CancellationTokenSource? _optimizationCancellation;
    private int _activeFileIndex = -1;
    private long _runGeneration;
    private long _queueRefreshGeneration;

    public VideoOptimizerViewModel(
        IVideoOptimizerService optimizerService,
        PathScopeViewModel pathScope,
        IFileActionService fileActionService,
        IAppOperationCoordinator operationCoordinator)
        : this(
            optimizerService,
            pathScope,
            fileActionService,
            operationCoordinator,
            static (scope, cancellationToken) =>
                FileInventoryBuilder.Build(scope, progress: null, cancellationToken),
            IsCurrentOrdinarySnapshot)
    {
    }

    internal VideoOptimizerViewModel(
        IVideoOptimizerService optimizerService,
        PathScopeViewModel pathScope,
        IFileActionService fileActionService,
        IAppOperationCoordinator operationCoordinator,
        Func<AnalysisScope, CancellationToken, FileInventory> inventoryBuilder,
        Func<InventoryFile, bool>? snapshotRechecker = null,
        Func<Action<double>, IProgress<double>>? progressFactory = null)
    {
        _optimizerService = optimizerService ?? throw new ArgumentNullException(nameof(optimizerService));
        PathScope = pathScope ?? throw new ArgumentNullException(nameof(pathScope));
        _fileActionService = fileActionService ?? throw new ArgumentNullException(nameof(fileActionService));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
        _inventoryBuilder = inventoryBuilder ?? throw new ArgumentNullException(nameof(inventoryBuilder));
        _snapshotRechecker = snapshotRechecker ?? IsCurrentOrdinarySnapshot;
        _progressFactory = progressFactory ?? (static callback => new Progress<double>(callback));
        PathScope.PropertyChanged += PathScopeChanged;
        _operationCoordinator.ActiveOperationChanged += OperationChanged;
        Queue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(QueueVisibility));
    }

    public PathScopeViewModel PathScope { get; }

    public ObservableCollection<VideoOptimizationQueueItemViewModel> Queue { get; } = [];

    public Visibility ProgressVisibility => IsOptimizing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility QueueVisibility => Queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool CanStartOptimization =>
        !IsOptimizing &&
        _operationCoordinator.ActiveOperation is null &&
        PathScope.HasIncludedPaths;

    public bool CanEditQueue => !IsOptimizing;

    public bool CanRefreshQueue => !IsOptimizing;

    public bool IsStatusOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetIndex))]
    public partial VideoOptimizationPreset Preset { get; set; } = VideoOptimizationPreset.Balanced;

    public int PresetIndex
    {
        get => EnumSelection.ToIndex(Preset);
        set
        {
            if (EnumSelection.TryFromIndex(value, out VideoOptimizationPreset preset))
            {
                Preset = preset;
            }
        }
    }

    [ObservableProperty]
    public partial bool HardwareAccelerationEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool KeepOutputWhenNotSmaller { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOptimization))]
    [NotifyPropertyChangedFor(nameof(CanEditQueue))]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    public partial bool IsOptimizing { get; set; }

    [ObservableProperty]
    public partial string CurrentPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double CurrentFileProgress { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial int ProcessedCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOpen))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [RelayCommand(CanExecute = nameof(CanStartOptimization))]
    private async Task OptimizeVideosAsync()
    {
        if (!CanStartOptimization)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        IAppOperationLease? lease = null;

        try
        {
            if (!_operationCoordinator.TryAcquire(
                    new AppOperationDescriptor(AppOperationKind.VideoOptimization, false),
                    cancellation.Cancel,
                    out lease))
            {
                StatusSeverity = InfoBarSeverity.Informational;
                StatusMessage = "Another operation is already running.";
                return;
            }

            long runGeneration = Interlocked.Increment(ref _runGeneration);
            Interlocked.Increment(ref _queueRefreshGeneration);
            AnalysisScope scope = BuildScopeSnapshot();
            VideoOptimizationOptions options = BuildOptionsSnapshot();
            _optimizationCancellation = cancellation;
            IsOptimizing = true;
            CurrentPath = string.Empty;
            CurrentFileProgress = 0;
            ProgressValue = 0;
            ProcessedCount = 0;
            TotalCount = 0;
            StatusMessage = string.Empty;

            CancellationToken cancellationToken = cancellation.Token;
            FileInventory inventory = await Task.Run(
                () => _inventoryBuilder(scope, cancellationToken),
                cancellationToken);
            VideoOptimizationQueueItemViewModel[] frozenQueue = BuildFrozenQueue(inventory, options);
            ReplaceQueue(frozenQueue);

            TotalCount = frozenQueue.Length;
            if (frozenQueue.Length == 0)
            {
                StatusSeverity = InfoBarSeverity.Informational;
                StatusMessage = "No supported video files were found.";
                return;
            }

            bool recoveryRequired = await ProcessVideoQueueAsync(
                frozenQueue, options, runGeneration, cancellationToken);

            if (!recoveryRequired)
            {
                int committed = Queue.Count(static item => item.Outcome is
                    VideoOptimizationOutcome.Succeeded or
                    VideoOptimizationOutcome.KeptWithoutSaving);
                int failed = Queue.Count(static item => item.Outcome is not null) - committed;
                StatusSeverity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                StatusMessage = failed == 0
                    ? $"{committed:N0} videos optimized."
                    : $"{ProcessedCount:N0} videos processed; {committed:N0} optimized and {failed:N0} not published.";
            }
        }
        catch (VideoOptimizationCancellationException exception)
        {
            ApplyCancellationRecovery(exception);
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = "Optimization cancelled, but manual recovery is required.";
        }
        catch (OperationCanceledException)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "Video optimization cancelled.";
        }
        catch (Exception)
        {
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = "Video optimization failed unexpectedly.";
        }
        finally
        {
            _activeFileIndex = -1;
            _optimizationCancellation = null;
            IsOptimizing = false;
            try
            {
                lease?.Dispose();
            }
            catch (Exception)
            {
                StatusSeverity = InfoBarSeverity.Error;
                StatusMessage = "Video optimization finished, but the operation gate could not be released cleanly.";
            }
            finally
            {
                cancellation.Dispose();
                OptimizeVideosCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task<bool> ProcessVideoQueueAsync(
        VideoOptimizationQueueItemViewModel[] frozenQueue,
        VideoOptimizationOptions options,
        long runGeneration,
        CancellationToken cancellationToken)
    {
        bool recoveryRequired = false;
        for (int index = 0; index < frozenQueue.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int fileIndex = index;
            VideoOptimizationQueueItemViewModel item = frozenQueue[fileIndex];
            _activeFileIndex = fileIndex;
            CurrentPath = item.SourcePath;
            CurrentFileProgress = 0;
            item.Progress = 0;

            if (!_snapshotRechecker(item.InventoryFile))
            {
                ApplyResult(item, new VideoOptimizationResult(
                    VideoOptimizationOutcome.SourceChanged,
                    item.SourcePath,
                    null,
                    null,
                    null,
                    null,
                    0,
                    "The source file changed since it was queued.",
                    []));
                ProcessedCount = index + 1;
                ProgressValue = Math.Max(ProgressValue, ProcessedCount * 100d / frozenQueue.Length);
                continue;
            }

            var request = new VideoOptimizationRequest(
                item.SourcePath,
                item.SizeBytes,
                item.ModifiedUtc,
                item.DestinationPath,
                options);
            IProgress<double> fileProgress = _progressFactory(value =>
                UpdateProgress(runGeneration, fileIndex, frozenQueue.Length, item, value));
            VideoOptimizationResult result = await _optimizerService.OptimizeAsync(
                request,
                fileProgress,
                cancellationToken);
            ApplyResult(item, result);
            ProcessedCount = index + 1;
            if (result.Outcome is VideoOptimizationOutcome.Succeeded or
                VideoOptimizationOutcome.KeptWithoutSaving)
            {
                CurrentFileProgress = 100;
                item.Progress = 100;
            }

            ProgressValue = Math.Max(ProgressValue, ProcessedCount * 100d / frozenQueue.Length);
            if (result.Outcome == VideoOptimizationOutcome.RecoveryRequired)
            {
                recoveryRequired = true;
                StatusSeverity = InfoBarSeverity.Error;
                StatusMessage = "Optimization stopped because manual recovery is required.";
                break;
            }
        }

        return recoveryRequired;
    }

    [RelayCommand]
    private void CancelOptimization() => _optimizationCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRefreshQueue))]
    private async Task RefreshQueueAsync()
    {
        if (!CanRefreshQueue)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _queueRefreshGeneration);
        AnalysisScope scope = BuildScopeSnapshot();
        VideoOptimizationOptions options = BuildOptionsSnapshot();
        try
        {
            FileInventory inventory = await Task.Run(
                () => _inventoryBuilder(scope, CancellationToken.None),
                CancellationToken.None);
            if (generation != Volatile.Read(ref _queueRefreshGeneration) || IsOptimizing)
            {
                return;
            }

            ReplaceQueue(BuildFrozenQueue(inventory, options));
        }
        catch (Exception)
        {
            if (generation == Volatile.Read(ref _queueRefreshGeneration) && !IsOptimizing)
            {
                StatusSeverity = InfoBarSeverity.Error;
                StatusMessage = "The video queue could not be refreshed.";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseOutput))]
    private void OpenOutput(VideoOptimizationQueueItemViewModel? item)
    {
        if (CanUseOutput(item))
        {
            _fileActionService.OpenFile(item!.OutputPath!);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseOutput))]
    private void RevealOutput(VideoOptimizationQueueItemViewModel? item)
    {
        if (CanUseOutput(item))
        {
            _fileActionService.RevealInExplorer(item!.OutputPath!);
        }
    }

    private AnalysisScope BuildScopeSnapshot() => new()
    {
        IncludedFolders = PathScope.IncludedPaths
            .Where(static path => path.Kind == ScopePathKind.Folder)
            .Select(static path => path.FullPath)
            .ToArray(),
        IncludedFiles = PathScope.IncludedPaths
            .Where(static path => path.Kind == ScopePathKind.File)
            .Select(static path => path.FullPath)
            .ToArray(),
        ExcludedPaths = PathScope.ExcludedPaths
            .Select(static path => path.FullPath)
            .ToArray(),
        IncludeSubfolders = PathScope.IncludeSubfolders,
        IgnoreHiddenFiles = PathScope.IgnoreHiddenFiles,
        IgnoreSystemFiles = PathScope.IgnoreSystemFiles,
    };

    private VideoOptimizationOptions BuildOptionsSnapshot() => new(
        Preset,
        HardwareAccelerationEnabled,
        KeepOutputWhenNotSmaller);

    private static VideoOptimizationQueueItemViewModel[] BuildFrozenQueue(
        FileInventory inventory,
        VideoOptimizationOptions options)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return inventory.Files
            .Where(static file =>
                (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 &&
                VideoFilter.Matches(file.Extension))
            .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.FullPath, StringComparer.Ordinal)
            .Select(file => new VideoOptimizationQueueItemViewModel(
                file,
                ReserveDestination(file.FullPath, reserved),
                options))
            .ToArray();
    }

    private void ReplaceQueue(IEnumerable<VideoOptimizationQueueItemViewModel> items)
    {
        Queue.Clear();
        foreach (VideoOptimizationQueueItemViewModel item in items)
        {
            Queue.Add(item);
        }
    }

    private static string ReserveDestination(string sourcePath, HashSet<string> reserved)
    {
        string directory = Path.GetDirectoryName(sourcePath) ??
            throw new InvalidDataException("The video path has no parent directory.");
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        int suffix = 1;
        while (true)
        {
            string name = suffix == 1
                ? $"{stem}.optimized.mp4"
                : $"{stem}.optimized ({suffix}).mp4";
            string candidate = Path.Combine(directory, name);
            if (!reserved.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate))
            {
                reserved.Add(candidate);
                return candidate;
            }

            suffix++;
        }
    }

    internal static bool IsCurrentOrdinarySnapshot(InventoryFile file)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(file.FullPath);
            var current = new FileInfo(file.FullPath);
            current.Refresh();
            return current.Exists &&
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 &&
                current.Length == file.SizeBytes &&
                current.LastWriteTimeUtc == file.ModifiedUtc;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateProgress(
        long runGeneration,
        int fileIndex,
        int totalCount,
        VideoOptimizationQueueItemViewModel item,
        double value)
    {
        if (runGeneration != Volatile.Read(ref _runGeneration) ||
            _activeFileIndex != fileIndex ||
            fileIndex < 0 ||
            fileIndex >= Queue.Count ||
            !ReferenceEquals(Queue[fileIndex], item))
        {
            return;
        }

        double current = Math.Clamp(value, 0, 100);
        item.Progress = Math.Max(item.Progress, current);
        CurrentFileProgress = Math.Max(CurrentFileProgress, current);

        double aggregate = (fileIndex + (current / 100d)) * 100d / totalCount;
        ProgressValue = Math.Max(ProgressValue, aggregate);
    }

    private void ApplyCancellationRecovery(VideoOptimizationCancellationException exception)
    {
        if (_activeFileIndex < 0 || _activeFileIndex >= Queue.Count)
        {
            return;
        }

        VideoOptimizationQueueItemViewModel item = Queue[_activeFileIndex];
        ApplyResult(item, new VideoOptimizationResult(
            VideoOptimizationOutcome.RecoveryRequired,
            item.SourcePath,
            null,
            null,
            exception.SourceMedia,
            exception.OutputMedia,
            0,
            "Video optimization was cancelled, but the temporary output requires manual recovery.",
            exception.RecoveryPaths));
    }

    private void ApplyResult(
        VideoOptimizationQueueItemViewModel item,
        VideoOptimizationResult result)
    {
        item.Apply(result);
        OpenOutputCommand.NotifyCanExecuteChanged();
        RevealOutputCommand.NotifyCanExecuteChanged();
    }

    private static bool CanUseOutput(VideoOptimizationQueueItemViewModel? item) =>
        item is
        {
            Outcome: VideoOptimizationOutcome.Succeeded or VideoOptimizationOutcome.KeptWithoutSaving,
            OutputPath: not null and not "",
        };

    partial void OnIsOptimizingChanged(bool value)
    {
        OptimizeVideosCommand.NotifyCanExecuteChanged();
        RefreshQueueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditQueue));
        OnPropertyChanged(nameof(CanRefreshQueue));
    }

    partial void OnPresetChanged(VideoOptimizationPreset value)
    {
        if (IsOptimizing)
        {
            return;
        }

        VideoOptimizationOptions options = BuildOptionsSnapshot();
        foreach (VideoOptimizationQueueItemViewModel item in Queue)
        {
            item.UpdateOptions(options);
        }
    }

    private void OperationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanStartOptimization));
        OptimizeVideosCommand.NotifyCanExecuteChanged();
    }

    private void PathScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PathScopeViewModel.HasIncludedPaths))
        {
            OnPropertyChanged(nameof(CanStartOptimization));
            OptimizeVideosCommand.NotifyCanExecuteChanged();
        }
    }
}

public sealed partial class VideoOptimizationQueueItemViewModel : ObservableObject
{
    private VideoOptimizationOptions _options;

    public VideoOptimizationQueueItemViewModel(
        InventoryFile file,
        string destinationPath,
        VideoOptimizationOptions? options = null)
    {
        InventoryFile = file ?? throw new ArgumentNullException(nameof(file));
        DestinationPath = destinationPath ?? throw new ArgumentNullException(nameof(destinationPath));
        _options = options ?? new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false);
    }

    internal InventoryFile InventoryFile { get; }

    public string SourcePath => InventoryFile.FullPath;

    public long SizeBytes => InventoryFile.SizeBytes;

    public DateTime ModifiedUtc => InventoryFile.ModifiedUtc;

    public string DestinationPath { get; }

    public string SizeText => FormatBytes(SizeBytes);

    public string SnapshotText => $"{SizeText} · {ModifiedUtc.ToLocalTime():g}";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputActionsVisibility))]
    public partial VideoOptimizationOutcome? Outcome { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputActionsVisibility))]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    public partial long? OutputSizeBytes { get; set; }

    [ObservableProperty]
    public partial long SavedBytes { get; set; }

    [ObservableProperty]
    public partial VideoMediaInfo? SourceMedia { get; set; }

    [ObservableProperty]
    public partial VideoMediaInfo? OutputMedia { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> RecoveryPaths { get; set; } = [];

    public string RecoveryPathsText => string.Join(Environment.NewLine, RecoveryPaths);

    public string SourceSummary => SourceMedia is null
        ? "Source media has not been inspected."
        : $"{SourceMedia.VideoCodec} · {SourceMedia.Width} × {SourceMedia.Height} · " +
          $"{SourceMedia.Duration:g} · {FormatBits(SourceMedia.TotalBitrate)}";

    public string TargetSummary => OutputMedia is null
        ? _options.Preset switch
        {
            VideoOptimizationPreset.Smaller => "Smaller target · MP4 / H.264 · up to 1280 × 720 · up to 30 fps",
            VideoOptimizationPreset.Balanced => "Balanced target · MP4 / H.264 · up to 1920 × 1080 · up to 60 fps",
            VideoOptimizationPreset.HighQuality => "High quality target · MP4 / H.264 · up to 3840 × 2160 · up to 60 fps",
            _ => "MP4 / H.264 target",
        }
        : $"{OutputMedia.ContainerCodec} / {OutputMedia.VideoCodec} · " +
          $"{OutputMedia.Width} × {OutputMedia.Height} · " +
          $"{(double)OutputMedia.FramesPerSecondNumerator / OutputMedia.FramesPerSecondDenominator:N2} fps";

    public string OutputSummary => OutputSizeBytes is null
        ? string.Empty
        : $"{FormatBytes(OutputSizeBytes.Value)} · {FormatSavings(SavedBytes)}";

    public Visibility OutputActionsVisibility =>
        Outcome is VideoOptimizationOutcome.Succeeded or VideoOptimizationOutcome.KeptWithoutSaving &&
        !string.IsNullOrWhiteSpace(OutputPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public void Apply(VideoOptimizationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Outcome = result.Outcome;
        OutputPath = result.OutputPath;
        OutputSizeBytes = result.OutputSizeBytes;
        SavedBytes = result.SavedBytes;
        SourceMedia = result.SourceMedia;
        OutputMedia = result.OutputMedia;
        Detail = result.Detail;
        RecoveryPaths = result.RecoveryPaths;
        OnPropertyChanged(nameof(RecoveryPathsText));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(OutputSummary));
    }

    internal void UpdateOptions(VideoOptimizationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        OnPropertyChanged(nameof(TargetSummary));
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Abs((double)value);
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private static string FormatBits(uint value) => value == 0
        ? "Unknown bitrate"
        : $"{value / 1_000_000d:N1} Mbps";

    private static string FormatSavings(long value) => value >= 0
        ? $"{FormatBytes(value)} saved"
        : $"{FormatBytes(-value)} larger";
}
