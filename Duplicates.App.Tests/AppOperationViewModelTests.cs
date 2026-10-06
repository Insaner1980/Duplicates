using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

public sealed class AppOperationViewModelTests
{
    [Fact]
    public async Task ScanCompletionEventFiresAfterCoordinatorLeaseRelease()
    {
        string root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "one.txt"), "same");
            File.WriteAllText(Path.Combine(root, "two.txt"), "same");
            var scope = new PathScopeViewModel();
            scope.AddFolder(root);
            var coordinator = new AppOperationCoordinator();
            var viewModel = new ScanViewModel(
                new Duplicates.Engine.DuplicateScanner(),
                new FakeSettingsService(),
                new ResultsStore(),
                scope,
                coordinator);
            AppOperationDescriptor? operationObservedByCompletion = new(AppOperationKind.ExactScan);
            viewModel.ScanCompleted += (_, _) => operationObservedByCompletion = coordinator.ActiveOperation;

            await viewModel.StartScanCommand.ExecuteAsync(null);

            Assert.Null(operationObservedByCompletion);
            Assert.Null(coordinator.ActiveOperation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalysisCompletionEventFiresAfterCoordinatorLeaseRelease()
    {
        var coordinator = new AppOperationCoordinator();
        var service = new ImmediateAnalysisService();
        var scope = new PathScopeViewModel();
        scope.IncludedPaths.Add(new ScopePathViewModel("C:\\Task16Analysis", Duplicates.Models.ScopePathKind.Folder));
        var viewModel = new AnalysisViewModel(service, new AnalysisSessionStore(), scope, coordinator);
        AppOperationDescriptor? operationObservedByCompletion = new(AppOperationKind.AnalysisRun);
        viewModel.AnalysisCompleted += (_, _) => operationObservedByCompletion = coordinator.ActiveOperation;

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        Assert.Null(operationObservedByCompletion);
        Assert.Null(coordinator.ActiveOperation);
    }

    [Fact]
    public void SharedCoordinatorDisablesBothStartCommands()
    {
        var coordinator = new AppOperationCoordinator();
        var scope = new PathScopeViewModel();
        scope.IncludedPaths.Add(new ScopePathViewModel("C:\\Task16Scope", Duplicates.Models.ScopePathKind.Folder));
        var scan = new ScanViewModel(
            new Duplicates.Engine.DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore(),
            scope,
            coordinator);
        var analysis = new AnalysisViewModel(
            new ImmediateAnalysisService(),
            new AnalysisSessionStore(),
            scope,
            coordinator);
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.ExactResultsAction),
            static () => { },
            out IAppOperationLease? lease));

        Assert.False(scan.StartScanCommand.CanExecute(null));
        Assert.False(analysis.StartAnalysisCommand.CanExecute(null));

        lease!.Dispose();
        Assert.True(scan.StartScanCommand.CanExecute(null));
        Assert.True(analysis.StartAnalysisCommand.CanExecute(null));
    }

    [Fact]
    public void AnalysisResultsBlocksMutationExportAndNewAnalysisDuringCompetingOperation()
    {
        var store = new AnalysisSessionStore();
        var coordinator = new AppOperationCoordinator();
        var viewModel = new AnalysisResultsViewModel(
            store,
            new FakeFileActionService(),
            new FakeResultExportService(),
            new ImmediateAnalysisService(),
            null,
            coordinator);
        store.SetCompleted(
            ToolKind.EmptyFiles,
            new AnalysisScope(),
            new AnalysisResult
            {
                Findings = [new PathFinding { FullPath = "C:\\Task16\\empty.bin", Kind = PathFindingKind.File, Reason = "File is empty" }],
                Groups = [],
                SkippedPaths = [],
                Elapsed = TimeSpan.Zero,
            });
        viewModel.Findings[0].IsSelected = true;
        Assert.True(viewModel.CanMutateSelection);
        Assert.True(viewModel.ClearSelectionCommand.CanExecute(null));
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.ExactScan),
            static () => { },
            out IAppOperationLease? lease));

        Assert.False(viewModel.CanActOnSelection);
        Assert.False(viewModel.CanExport);
        Assert.False(viewModel.CanStartNewAnalysis);
        Assert.False(viewModel.CanMutateSelection);
        Assert.False(viewModel.ClearSelectionCommand.CanExecute(null));

        lease!.Dispose();
        Assert.True(viewModel.CanStartNewAnalysis);
        Assert.True(viewModel.CanMutateSelection);
        Assert.True(viewModel.ClearSelectionCommand.CanExecute(null));
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-Task16-VM-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class ImmediateAnalysisService : IAnalysisService
    {
        public Task<AnalysisResult> RunAsync(
            ToolKind tool,
            AnalysisScope scope,
            ToolOptions toolOptions,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AnalysisResult
            {
                Findings = [],
                Groups = [],
                SkippedPaths = [],
                Elapsed = TimeSpan.Zero,
            });
        }

        public Task<bool> RevalidateSimilarityItemAsync(
            ToolKind tool,
            SimilarityItem item,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public IReadOnlyList<SimilarityGroup> RegroupSimilarityItems(
            ToolKind tool,
            ToolOptions options,
            IReadOnlyList<SimilarityItem> items) => [];
    }
}
