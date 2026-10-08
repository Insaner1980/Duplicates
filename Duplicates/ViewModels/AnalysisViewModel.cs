using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.ViewModels;

public sealed partial class AnalysisViewModel : ObservableObject
{
    private const long DefaultLargeFileMinimumSizeBytes = 1_073_741_824;
    private const double DefaultTemporaryFileMinimumAgeDays = 7;

    private readonly IAnalysisService _analysisService;
    private readonly AnalysisSessionStore _sessionStore;
    private readonly PathScopeViewModel _pathScope;
    private readonly IAppOperationCoordinator _operationCoordinator;
    private readonly ISettingsService? _settingsService;
    private AppSettings? _pendingSettings;
    private CancellationTokenSource? _analysisCancellation;

    public AnalysisViewModel(
        IAnalysisService analysisService,
        AnalysisSessionStore sessionStore,
        PathScopeViewModel pathScope,
        IAppOperationCoordinator? operationCoordinator = null,
        ISettingsService? settingsService = null)
    {
        LargeFileMinimumSizeEditor = new ByteSizeEditorViewModel(
            () => LargeFileMinimumSizeValue,
            value => LargeFileMinimumSizeValue = value);
        _analysisService = analysisService;
        _sessionStore = sessionStore;
        _pathScope = pathScope;
        _operationCoordinator = operationCoordinator ?? new AppOperationCoordinator();
        _settingsService = settingsService;
        _pathScope.PropertyChanged += PathScopeChanged;
        _operationCoordinator.ActiveOperationChanged += OperationChanged;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged += SettingsChanged;
            ApplySettings(_settingsService.Current);
        }

        SelectTool(ToolKind.EmptyFolders);
    }

    public event EventHandler<AnalysisSession>? AnalysisCompleted;

    public PathScopeViewModel PathScope => _pathScope;

    public ToolKind Tool { get; private set; }

    public string Title => ToolDescriptor.For(Tool).Title;

    public string Subtitle => ToolDescriptor.For(Tool).Subtitle;

    public string OptionsSummary => Tool switch
    {
        ToolKind.BigFiles => $"Files at least {ByteFormatter.Format(GetLargeFileMinimumSizeBytes())} are included.",
        ToolKind.TemporaryFiles => string.Format(
            CultureInfo.InvariantCulture,
            "Files at least {0:N0} days old are included.",
            GetTemporaryFileMinimumAgeDays()),
        ToolKind.SimilarImages => $"{ImageSimilarityPreset} image matching allows a Hamming distance up to {GetImageMaximumHammingDistance()}.",
        ToolKind.SimilarVideos => $"{VideoSimilarityPreset} five-frame Windows media matching allows a mean frame distance up to {GetVideoMaximumMeanFrameDistance()}.",
        ToolKind.MusicDuplicates => "Track durations may differ by up to 2 seconds.",
        _ => "No additional options are required.",
    };

    public Visibility OptionsVisibility => Tool is ToolKind.BigFiles or ToolKind.TemporaryFiles or
        ToolKind.SimilarImages or ToolKind.SimilarVideos
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LargeFileOptionsVisibility => Tool == ToolKind.BigFiles
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TemporaryFileOptionsVisibility => Tool == ToolKind.TemporaryFiles
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SimilarImageOptionsVisibility => Tool == ToolKind.SimilarImages
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SimilarVideoOptionsVisibility => Tool == ToolKind.SimilarVideos
        ? Visibility.Visible
        : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial double LargeFileMinimumSizeValue { get; set; } = DefaultLargeFileMinimumSizeBytes;

    public ByteSizeEditorViewModel LargeFileMinimumSizeEditor { get; }

    partial void OnLargeFileMinimumSizeValueChanged(double value) => LargeFileMinimumSizeEditor.Refresh();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial double TemporaryFileMinimumAgeDays { get; set; } = DefaultTemporaryFileMinimumAgeDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    [NotifyPropertyChangedFor(nameof(ImageSimilarityPresetIndex))]
    public partial SimilarityPreset ImageSimilarityPreset { get; set; } = SimilarityPreset.Balanced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    [NotifyPropertyChangedFor(nameof(VideoSimilarityPresetIndex))]
    public partial SimilarityPreset VideoSimilarityPreset { get; set; } = SimilarityPreset.Balanced;

    public int ImageSimilarityPresetIndex
    {
        get => EnumSelection.ToIndex(ImageSimilarityPreset);
        set
        {
            if (EnumSelection.TryFromIndex(value, out SimilarityPreset preset))
            {
                ImageSimilarityPreset = preset;
            }
        }
    }

    public int VideoSimilarityPresetIndex
    {
        get => EnumSelection.ToIndex(VideoSimilarityPreset);
        set
        {
            if (EnumSelection.TryFromIndex(value, out SimilarityPreset preset))
            {
                VideoSimilarityPreset = preset;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupVisibility))]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    public partial string PhaseText { get; set; } = "Ready";

    [ObservableProperty]
    public partial string ItemsDiscoveredText { get; set; } = "0";

    [ObservableProperty]
    public partial string ItemsProcessedText { get; set; } = "0";

    [ObservableProperty]
    public partial string BytesProcessedText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string CurrentPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOpen))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    public Visibility SetupVisibility => IsAnalyzing ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ProgressVisibility => IsAnalyzing ? Visibility.Visible : Visibility.Collapsed;

    public bool IsStatusOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    public void SelectTool(ToolKind tool)
    {
        _ = BuildToolOptions(tool);
        if (IsAnalyzing && tool != Tool)
        {
            return;
        }

        Tool = tool;
        OnPropertyChanged(nameof(Tool));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(OptionsSummary));
        OnPropertyChanged(nameof(OptionsVisibility));
        OnPropertyChanged(nameof(LargeFileOptionsVisibility));
        OnPropertyChanged(nameof(TemporaryFileOptionsVisibility));
        OnPropertyChanged(nameof(SimilarImageOptionsVisibility));
        OnPropertyChanged(nameof(SimilarVideoOptionsVisibility));
    }

    [RelayCommand(CanExecute = nameof(CanStartAnalysis))]
    private async Task StartAnalysisAsync()
    {
        if (IsAnalyzing)
        {
            return;
        }

        ToolKind tool = Tool;
        AnalysisRunOptions runOptions = BuildRunOptions();
        bool usesMediaFingerprintCache =
            runOptions.UseMediaFingerprintCache &&
            tool is ToolKind.SimilarImages or ToolKind.SimilarVideos;
        var cancellation = new CancellationTokenSource();
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(
                    AppOperationKind.AnalysisRun,
                    usesMediaFingerprintCache),
                cancellation.Cancel,
                out IAppOperationLease? lease))
        {
            cancellation.Dispose();
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "Another operation is already running.";
            return;
        }

        IsAnalyzing = true;
        StatusMessage = string.Empty;
        _analysisCancellation = cancellation;
        CancellationToken cancellationToken = _analysisCancellation.Token;
        AnalysisScope scope = BuildScope();
        IToolOptions toolOptions = BuildToolOptions(tool);
        AnalysisResult? completedResult = null;

        try
        {
            var progress = new Progress<AnalysisProgress>(value =>
            {
                if (ReferenceEquals(_analysisCancellation, cancellation) && !cancellation.IsCancellationRequested)
                {
                    UpdateProgress(value);
                }
            });
            AnalysisResult result = await _analysisService.RunAsync(
                tool,
                scope,
                toolOptions,
                runOptions,
                progress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PhaseText = "Done";
            completedResult = result;
        }
        catch (OperationCanceledException)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = ex.Message;
        }
        finally
        {
            _analysisCancellation.Dispose();
            _analysisCancellation = null;
            IsAnalyzing = false;
            StartAnalysisCommand.NotifyCanExecuteChanged();
            lease!.Dispose();
        }

        if (completedResult is not null)
        {
            _sessionStore.SetCompleted(tool, scope, toolOptions, completedResult);
            AnalysisCompleted?.Invoke(this, _sessionStore.CurrentSession!);
        }
    }

    [RelayCommand]
    private void CancelAnalysis() => _analysisCancellation?.Cancel();

    [RelayCommand]
    private void SetLargeFileMinimumSizeToAny() => LargeFileMinimumSizeValue = 0;

    [RelayCommand]
    private void SetLargeFileMinimumSizeTo100Mb() => LargeFileMinimumSizeValue = 104_857_600;

    [RelayCommand]
    private void SetLargeFileMinimumSizeTo1Gb() => LargeFileMinimumSizeValue = DefaultLargeFileMinimumSizeBytes;

    [RelayCommand]
    private void SetLargeFileMinimumSizeTo10Gb() => LargeFileMinimumSizeValue = 10_737_418_240;

    private bool CanStartAnalysis() =>
        !IsAnalyzing &&
        _operationCoordinator.ActiveOperation is null &&
        PathScope.HasIncludedPaths;

    private AnalysisScope BuildScope() => new()
    {
        IncludedFolders = PathScope.IncludedPaths
            .Where(static path => path.Kind == ScopePathKind.Folder)
            .Select(static path => path.FullPath)
            .ToArray(),
        IncludedFiles = PathScope.IncludedPaths
            .Where(static path => path.Kind == ScopePathKind.File)
            .Select(static path => path.FullPath)
            .ToArray(),
        ExcludedPaths = PathScope.ExcludedPaths.Select(static path => path.FullPath).ToArray(),
        IncludeSubfolders = PathScope.IncludeSubfolders,
        IgnoreHiddenFiles = PathScope.IgnoreHiddenFiles,
        IgnoreSystemFiles = PathScope.IgnoreSystemFiles,
    };

    private IToolOptions BuildToolOptions(ToolKind tool) => tool switch
    {
        ToolKind.BigFiles => new LargeFileToolOptions(GetLargeFileMinimumSizeBytes()),
        ToolKind.TemporaryFiles => BuildTemporaryFileOptions(),
        ToolKind.SimilarImages => new SimilarImageToolOptions(GetImageMaximumHammingDistance()),
        ToolKind.SimilarVideos => new SimilarVideoToolOptions(GetVideoMaximumMeanFrameDistance()),
        ToolKind.MusicDuplicates => new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
        ToolKind.EmptyFolders or ToolKind.EmptyFiles or ToolKind.InvalidLinks or
            ToolKind.BrokenFiles or ToolKind.BadExtensions or ToolKind.BadNames => new NoToolOptions(),
        _ => throw new ArgumentException($"{tool} is not a read-only analysis tool.", nameof(tool)),
    };

    private AnalysisRunOptions BuildRunOptions()
    {
        if (_settingsService is null)
        {
            return new AnalysisRunOptions(1, UseMediaFingerprintCache: true);
        }

        AppSettings settings = _settingsService.Current;
        int maximumConcurrency = settings.MaxMediaConcurrency is 1 or 2 or 4
            ? settings.MaxMediaConcurrency.Value
            : Math.Clamp(Environment.ProcessorCount, 1, 2);
        return new AnalysisRunOptions(maximumConcurrency, settings.UseMediaFingerprintCache);
    }

    private long GetLargeFileMinimumSizeBytes() => ByteSizeInput.ToBytes(
        LargeFileMinimumSizeValue,
        DefaultLargeFileMinimumSizeBytes);

    private int GetImageMaximumHammingDistance() => ImageSimilarityPreset switch
    {
        SimilarityPreset.Strict => 4,
        SimilarityPreset.Broad => 12,
        _ => 8,
    };

    private int GetVideoMaximumMeanFrameDistance() => VideoSimilarityPreset switch
    {
        SimilarityPreset.Strict => 5,
        SimilarityPreset.Broad => 13,
        _ => 9,
    };

    private TemporaryFileToolOptions BuildTemporaryFileOptions()
    {
        DateTime utcNow = DateTime.UtcNow;
        double normalizedDays = GetTemporaryFileMinimumAgeDays(utcNow);
        TemporaryFileMinimumAgeDays = normalizedDays;
        return new TemporaryFileToolOptions(TimeSpan.FromDays(normalizedDays), utcNow);
    }

    private void ApplySettings(AppSettings settings)
    {
        LargeFileMinimumSizeValue = settings.DefaultLargeFileMinimumBytes;
        TemporaryFileMinimumAgeDays = settings.DefaultTemporaryFileMinimumAgeDays;
        ImageSimilarityPreset = settings.DefaultImageSimilarity;
        VideoSimilarityPreset = settings.DefaultVideoSimilarity;
    }

    private double GetTemporaryFileMinimumAgeDays() => GetTemporaryFileMinimumAgeDays(DateTime.UtcNow);

    private double GetTemporaryFileMinimumAgeDays(DateTime utcNow)
    {
        double normalizedDays =
            double.IsFinite(TemporaryFileMinimumAgeDays) &&
            TemporaryFileMinimumAgeDays < TimeSpan.MaxValue.TotalDays
                ? Math.Max(0, TemporaryFileMinimumAgeDays)
                : DefaultTemporaryFileMinimumAgeDays;
        return TimeSpan.FromDays(normalizedDays) <= utcNow - DateTime.MinValue
            ? normalizedDays
            : DefaultTemporaryFileMinimumAgeDays;
    }

    private void UpdateProgress(AnalysisProgress progress)
    {
        PhaseText = progress.Phase switch
        {
            AnalysisPhase.Enumerating => "Finding items...",
            AnalysisPhase.Inspecting => "Inspecting items...",
            AnalysisPhase.Comparing => "Comparing items...",
            AnalysisPhase.Done => "Done",
            _ => "Analyzing...",
        };
        ItemsDiscoveredText = progress.ItemsDiscovered.ToString("N0", CultureInfo.InvariantCulture);
        ItemsProcessedText = progress.ItemsProcessed.ToString("N0", CultureInfo.InvariantCulture);
        BytesProcessedText = ByteFormatter.Format(progress.BytesProcessed);
        CurrentPath = progress.CurrentPath ?? string.Empty;
        IsProgressIndeterminate = progress.TotalBytes <= 0 || progress.Phase == AnalysisPhase.Enumerating;
        ProgressValue = progress.TotalBytes <= 0
            ? 0
            : Math.Clamp(progress.BytesProcessed * 100d / progress.TotalBytes, 0, 100);
    }

    partial void OnIsAnalyzingChanged(bool value)
    {
        StartAnalysisCommand.NotifyCanExecuteChanged();
        TryApplyPendingSettings();
    }

    private void OperationChanged(object? sender, EventArgs e)
    {
        TryApplyPendingSettings();
        StartAnalysisCommand.NotifyCanExecuteChanged();
    }

    private void PathScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PathScopeViewModel.HasIncludedPaths))
        {
            StartAnalysisCommand.NotifyCanExecuteChanged();
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
            IsAnalyzing ||
            _operationCoordinator.ActiveOperation is not null)
        {
            return;
        }

        _pendingSettings = null;
        ApplySettings(settings);
    }
}
