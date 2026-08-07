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
    private CancellationTokenSource? _analysisCancellation;

    public AnalysisViewModel(
        IAnalysisService analysisService,
        AnalysisSessionStore sessionStore,
        PathScopeViewModel pathScope)
    {
        _analysisService = analysisService;
        _sessionStore = sessionStore;
        _pathScope = pathScope;
        _pathScope.PropertyChanged += PathScopeChanged;
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
        ToolKind.SimilarImages or ToolKind.SimilarVideos => "Balanced similarity matching is used.",
        ToolKind.MusicDuplicates => "Track durations may differ by up to 2 seconds.",
        _ => "No additional options are required.",
    };

    public Visibility OptionsVisibility => Tool is ToolKind.EmptyFolders or ToolKind.EmptyFiles
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility LargeFileOptionsVisibility => Tool == ToolKind.BigFiles
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TemporaryFileOptionsVisibility => Tool == ToolKind.TemporaryFiles
        ? Visibility.Visible
        : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial double LargeFileMinimumSizeValue { get; set; } = DefaultLargeFileMinimumSizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial double TemporaryFileMinimumAgeDays { get; set; } = DefaultTemporaryFileMinimumAgeDays;

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
    }

    [RelayCommand(CanExecute = nameof(CanStartAnalysis))]
    private async Task StartAnalysisAsync()
    {
        if (IsAnalyzing)
        {
            return;
        }

        IsAnalyzing = true;
        StatusMessage = string.Empty;
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;
        ToolKind tool = Tool;
        AnalysisScope scope = BuildScope();
        ToolOptions toolOptions = BuildToolOptions(tool);

        try
        {
            var progress = new Progress<AnalysisProgress>(UpdateProgress);
            AnalysisResult result = await _analysisService.RunAsync(
                tool,
                scope,
                toolOptions,
                progress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _sessionStore.SetCompleted(tool, scope, toolOptions, result);
            AnalysisCompleted?.Invoke(this, _sessionStore.CurrentSession!);
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

    private bool CanStartAnalysis() => !IsAnalyzing && PathScope.HasIncludedPaths;

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

    private ToolOptions BuildToolOptions(ToolKind tool) => tool switch
    {
        ToolKind.BigFiles => new LargeFileToolOptions(GetLargeFileMinimumSizeBytes()),
        ToolKind.TemporaryFiles => new TemporaryFileToolOptions(
            TimeSpan.FromDays(GetTemporaryFileMinimumAgeDays()),
            DateTime.UtcNow),
        ToolKind.SimilarImages => new SimilarImageToolOptions(10),
        ToolKind.SimilarVideos => new SimilarVideoToolOptions(10),
        ToolKind.MusicDuplicates => new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
        ToolKind.EmptyFolders or ToolKind.EmptyFiles or ToolKind.InvalidLinks or
            ToolKind.BrokenFiles or ToolKind.BadExtensions or ToolKind.BadNames => new NoToolOptions(),
        _ => throw new ArgumentException($"{tool} is not a read-only analysis tool.", nameof(tool)),
    };

    private long GetLargeFileMinimumSizeBytes() => ByteSizeInput.ToBytes(
        LargeFileMinimumSizeValue,
        DefaultLargeFileMinimumSizeBytes);

    private double GetTemporaryFileMinimumAgeDays() =>
        double.IsFinite(TemporaryFileMinimumAgeDays) &&
        TemporaryFileMinimumAgeDays < TimeSpan.MaxValue.TotalDays
            ? Math.Max(0, TemporaryFileMinimumAgeDays)
            : DefaultTemporaryFileMinimumAgeDays;

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

    partial void OnIsAnalyzingChanged(bool value) => StartAnalysisCommand.NotifyCanExecuteChanged();

    private void PathScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PathScopeViewModel.HasIncludedPaths))
        {
            StartAnalysisCommand.NotifyCanExecuteChanged();
        }
    }
}
