using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.ViewModels;

public sealed partial class ExifRemoverViewModel : ObservableObject
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff",
    };

    private readonly IExifCleanerService _cleanerService;
    private readonly IFileActionService _fileActionService;
    private readonly IAppOperationCoordinator _operationCoordinator;
    private readonly Func<AnalysisScope, CancellationToken, FileInventory> _inventoryBuilder;
    private CancellationTokenSource? _cleaningCancellation;
    private int _activeFileIndex = -1;

    public ExifRemoverViewModel(
        IExifCleanerService cleanerService,
        PathScopeViewModel pathScope,
        IFileActionService fileActionService,
        IAppOperationCoordinator operationCoordinator)
        : this(
            cleanerService,
            pathScope,
            fileActionService,
            operationCoordinator,
            static (scope, cancellationToken) =>
                new FileInventoryBuilder().Build(scope, progress: null, cancellationToken))
    {
    }

    internal ExifRemoverViewModel(
        IExifCleanerService cleanerService,
        PathScopeViewModel pathScope,
        IFileActionService fileActionService,
        IAppOperationCoordinator operationCoordinator,
        Func<AnalysisScope, CancellationToken, FileInventory> inventoryBuilder)
    {
        _cleanerService = cleanerService;
        PathScope = pathScope;
        _fileActionService = fileActionService;
        _operationCoordinator = operationCoordinator;
        _inventoryBuilder = inventoryBuilder;
        PathScope.PropertyChanged += PathScopeChanged;
        _operationCoordinator.ActiveOperationChanged += OperationChanged;
    }

    public PathScopeViewModel PathScope { get; }

    public ObservableCollection<ExifCleanResultViewModel> Results { get; } = [];

    public bool CanStartCleaning =>
        !IsCleaning &&
        _operationCoordinator.ActiveOperation is null &&
        PathScope.HasIncludedPaths;

    public bool IsStatusOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    [ObservableProperty]
    public partial bool RemoveGps { get; set; } = true;

    [ObservableProperty]
    public partial bool RemoveDeviceIdentifiers { get; set; } = true;

    [ObservableProperty]
    public partial bool RemoveDates { get; set; } = true;

    [ObservableProperty]
    public partial bool RemoveAuthorAndDescription { get; set; } = true;

    [ObservableProperty]
    public partial bool RemoveEmbeddedThumbnail { get; set; } = true;

    [ObservableProperty]
    public partial bool RemoveXmpAndIptc { get; set; } = true;

    [ObservableProperty]
    public partial bool ReplaceOriginal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartCleaning))]
    public partial bool IsCleaning { get; set; }

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

    [RelayCommand(CanExecute = nameof(CanStartCleaning))]
    private async Task CleanImagesAsync(bool? replacementConfirmed)
    {
        if (IsCleaning || !PathScope.HasIncludedPaths)
        {
            return;
        }

        if (ReplaceOriginal && replacementConfirmed is not true)
        {
            StatusSeverity = InfoBarSeverity.Warning;
            StatusMessage = "Explicit confirmation is required before replacing original images.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(AppOperationKind.ExifCleaning),
                cancellation.Cancel,
                out IAppOperationLease? lease))
        {
            cancellation.Dispose();
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "Another operation is already running.";
            return;
        }

        try
        {
            AnalysisScope scope = BuildScopeSnapshot();
            ExifCleanOptions options = BuildOptionsSnapshot();
            _cleaningCancellation = cancellation;
            IsCleaning = true;
            Results.Clear();
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
            InventoryFile[] queue = inventory.Files
                .Where(IsSupportedOrdinaryImage)
                .ToArray();
            TotalCount = queue.Length;

            if (queue.Length == 0)
            {
                StatusSeverity = InfoBarSeverity.Informational;
                StatusMessage = "No supported JPEG or TIFF images were found.";
                return;
            }

            bool recoveryRequired = false;
            for (int index = 0; index < queue.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InventoryFile file = queue[index];
                _activeFileIndex = index;
                CurrentPath = file.FullPath;
                CurrentFileProgress = 0;
                var progress = new Progress<double>(value => UpdateProgress(index, queue.Length, value));
                var request = new ExifCleanRequest(
                    file.FullPath,
                    file.SizeBytes,
                    file.ModifiedUtc,
                    options);

                ExifCleanResult result = await _cleanerService.CleanAsync(
                    request,
                    progress,
                    cancellationToken);
                Results.Add(new ExifCleanResultViewModel(result));
                ProcessedCount = index + 1;
                if (result.Outcome == ExifCleanOutcome.Succeeded)
                {
                    CurrentFileProgress = 100;
                }

                ProgressValue = Math.Max(ProgressValue, ProcessedCount * 100d / queue.Length);
                if (result.Outcome == ExifCleanOutcome.RecoveryRequired)
                {
                    recoveryRequired = true;
                    StatusSeverity = InfoBarSeverity.Error;
                    StatusMessage = "Cleaning stopped because manual recovery is required.";
                    break;
                }
            }

            if (!recoveryRequired)
            {
                int succeeded = Results.Count(static result => result.Outcome == ExifCleanOutcome.Succeeded);
                int failed = Results.Count - succeeded;
                StatusSeverity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                StatusMessage = failed == 0
                    ? $"{succeeded:N0} images cleaned."
                    : $"{Results.Count:N0} images processed; {succeeded:N0} cleaned and {failed:N0} failed.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = "Image cleaning cancelled.";
        }
        catch (Exception ex)
        {
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = ex.Message;
        }
        finally
        {
            _activeFileIndex = -1;
            _cleaningCancellation = null;
            cancellation.Dispose();
            IsCleaning = false;
            lease!.Dispose();
            CleanImagesCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelCleaning() => _cleaningCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanUseOutput))]
    private void OpenOutput(ExifCleanResultViewModel? result)
    {
        if (CanUseOutput(result))
        {
            _fileActionService.OpenFile(result!.OutputPath!);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseOutput))]
    private void RevealOutput(ExifCleanResultViewModel? result)
    {
        if (CanUseOutput(result))
        {
            _fileActionService.RevealInExplorer(result!.OutputPath!);
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

    private ExifCleanOptions BuildOptionsSnapshot() => new(
        RemoveGps,
        RemoveDeviceIdentifiers,
        RemoveDates,
        RemoveAuthorAndDescription,
        RemoveEmbeddedThumbnail,
        RemoveXmpAndIptc,
        ReplaceOriginal);

    private void UpdateProgress(int fileIndex, int totalCount, double value)
    {
        double current = Math.Clamp(value, 0, 1) * 100;
        if (_activeFileIndex == fileIndex)
        {
            CurrentFileProgress = current;
        }

        double aggregate = (fileIndex + current / 100d) * 100d / totalCount;
        ProgressValue = Math.Max(ProgressValue, aggregate);
    }

    private static bool IsSupportedOrdinaryImage(InventoryFile file) =>
        SupportedExtensions.Contains(file.Extension) &&
        (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

    private static bool CanUseOutput(ExifCleanResultViewModel? result) =>
        result is
        {
            Outcome: ExifCleanOutcome.Succeeded,
            OutputPath: not null and not "",
        };

    partial void OnIsCleaningChanged(bool value) => CleanImagesCommand.NotifyCanExecuteChanged();

    private void OperationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanStartCleaning));
        CleanImagesCommand.NotifyCanExecuteChanged();
    }

    private void PathScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PathScopeViewModel.HasIncludedPaths))
        {
            OnPropertyChanged(nameof(CanStartCleaning));
            CleanImagesCommand.NotifyCanExecuteChanged();
        }
    }
}

public sealed class ExifCleanResultViewModel
{
    public ExifCleanResultViewModel(ExifCleanResult result)
    {
        Outcome = result.Outcome;
        SourcePath = result.SourcePath;
        OutputPath = result.OutputPath;
        Detail = result.Detail;
        RecoveryPaths = result.RecoveryPaths;
    }

    public ExifCleanOutcome Outcome { get; }

    public string SourcePath { get; }

    public string? OutputPath { get; }

    public string Detail { get; }

    public IReadOnlyList<string> RecoveryPaths { get; }

    public string RecoveryPathsText => string.Join(Environment.NewLine, RecoveryPaths);

    public Visibility OutputActionsVisibility =>
        Outcome == ExifCleanOutcome.Succeeded && !string.IsNullOrWhiteSpace(OutputPath)
            ? Visibility.Visible
            : Visibility.Collapsed;
}
