using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.App.Tests;

public sealed class AnalysisViewModelTests
{
    [Fact]
    public async Task SuccessfulRunMovesFromSetupThroughProgressToStoredResults()
    {
        var service = new FakeAnalysisService
        {
            Run = async (_, _, _, progress, _) =>
            {
                progress?.Report(new AnalysisProgress(AnalysisPhase.Inspecting, 12, 5, 40, 100, @"C:\scan\current.tmp"));
                await Task.Delay(20);
                return NewResult([NewFinding(@"C:\scan\old.tmp", 25)]);
            },
        };
        var store = new AnalysisSessionStore();
        var scope = NewScope();
        var viewModel = new AnalysisViewModel(service, store, scope);
        viewModel.SelectTool(ToolKind.TemporaryFiles);

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);

        Assert.True(SpinWait.SpinUntil(() => viewModel.IsAnalyzing, TimeSpan.FromSeconds(1)));
        Assert.Equal(Visibility.Collapsed, viewModel.SetupVisibility);
        Assert.Equal(Visibility.Visible, viewModel.ProgressVisibility);
        Assert.True(SpinWait.SpinUntil(() => viewModel.CurrentPath == @"C:\scan\current.tmp", TimeSpan.FromSeconds(1)));

        await run;

        AnalysisSession session = Assert.IsType<AnalysisSession>(store.CurrentSession);
        Assert.Equal(ToolKind.TemporaryFiles, session.Tool);
        Assert.Equal(@"C:\scan", Assert.Single(session.Scope.IncludedFolders));
        Assert.Equal(@"C:\scan\single.txt", Assert.Single(session.Scope.IncludedFiles));
        Assert.Equal(@"C:\scan\excluded", Assert.Single(session.Scope.ExcludedPaths));
        Assert.Equal("5", viewModel.ItemsProcessedText);
        Assert.Equal("40 B", viewModel.BytesProcessedText);
        Assert.False(viewModel.IsAnalyzing);
        Assert.Equal(Visibility.Visible, viewModel.SetupVisibility);
    }

    [Fact]
    public async Task CancellationKeepsLastSuccessfulSession()
    {
        var store = new AnalysisSessionStore();
        AnalysisSession previous = StorePreviousResult(store);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = async (_, _, _, _, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return NewResult();
            },
        };
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.EmptyFiles);

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        await started.Task;
        viewModel.CancelAnalysisCommand.Execute(null);
        await run;

        Assert.Same(previous, store.CurrentSession);
        Assert.Equal("Analysis cancelled.", viewModel.StatusMessage);
        Assert.Equal(InfoBarSeverity.Informational, viewModel.StatusSeverity);
    }

    [Fact]
    public async Task CancellationBeforeNormalServiceReturnKeepsPreviousSessionAndDoesNotComplete()
    {
        var store = new AnalysisSessionStore();
        AnalysisSession previous = StorePreviousResult(store);
        var serviceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseService = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) =>
            {
                serviceStarted.SetResult();
                return releaseService.Task;
            },
        };
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.EmptyFiles);
        int completionCount = 0;
        viewModel.AnalysisCompleted += (_, _) => completionCount++;

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        await serviceStarted.Task;
        viewModel.CancelAnalysisCommand.Execute(null);
        releaseService.SetResult(NewResult([NewFinding(@"C:\scan\late.txt", 10)]));
        await run;

        Assert.Same(previous, store.CurrentSession);
        Assert.Equal(0, completionCount);
        Assert.Equal("Analysis cancelled.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailedRunShowsErrorAndKeepsLastSuccessfulSession()
    {
        var store = new AnalysisSessionStore();
        AnalysisSession previous = StorePreviousResult(store);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => throw new IOException("The file is locked."),
        };
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.BrokenFiles);

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        Assert.Same(previous, store.CurrentSession);
        Assert.True(viewModel.IsStatusOpen);
        Assert.Equal("The file is locked.", viewModel.StatusMessage);
        Assert.Equal(InfoBarSeverity.Error, viewModel.StatusSeverity);
    }

    [Fact]
    public async Task OnlyOneAnalysisCanRunAtATime()
    {
        var release = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => release.Task,
        };
        var viewModel = new AnalysisViewModel(service, new AnalysisSessionStore(), NewScope());
        viewModel.SelectTool(ToolKind.BadNames);

        Task first = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => viewModel.IsAnalyzing, TimeSpan.FromSeconds(1)));
        Task second = viewModel.StartAnalysisCommand.ExecuteAsync(null);

        Assert.Equal(1, service.CallCount);
        Assert.False(viewModel.StartAnalysisCommand.CanExecute(null));

        release.SetResult(NewResult());
        await Task.WhenAll(first, second);
        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task ActiveRunRejectsToolSwitchAndKeepsVisibleToolConsistent()
    {
        var release = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => release.Task,
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.BadNames);

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => viewModel.IsAnalyzing, TimeSpan.FromSeconds(1)));

        viewModel.SelectTool(ToolKind.BigFiles);

        Assert.Equal(ToolKind.BadNames, viewModel.Tool);
        Assert.Equal("Bad names", viewModel.Title);
        Assert.False(viewModel.StartAnalysisCommand.CanExecute(null));
        Assert.Equal(1, service.CallCount);

        release.SetResult(NewResult());
        await run;
        Assert.Equal(ToolKind.BadNames, Assert.IsType<AnalysisSession>(store.CurrentSession).Tool);
    }

    [Fact]
    public async Task SuccessfulResultPreservesSkippedPathDetails()
    {
        AnalysisResult result = NewResult(
            skippedPaths: [new SkippedPath { Path = @"C:\scan\locked.txt", Reason = "Access denied" }]);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => Task.FromResult(result),
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.InvalidLinks);

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        SkippedPath skipped = Assert.Single(Assert.IsType<AnalysisSession>(store.CurrentSession).Result.SkippedPaths);
        Assert.Equal(@"C:\scan\locked.txt", skipped.Path);
        Assert.Equal("Access denied", skipped.Reason);
    }

    private static PathScopeViewModel NewScope()
    {
        var scope = new PathScopeViewModel
        {
            IncludeSubfolders = false,
            IgnoreHiddenFiles = false,
            IgnoreSystemFiles = true,
        };
        Assert.True(scope.AddFolder(@"C:\scan"));
        Assert.True(scope.AddFile(@"C:\scan\single.txt"));
        Assert.True(scope.ExcludePath(@"C:\scan\excluded"));
        return scope;
    }

    private static AnalysisSession StorePreviousResult(AnalysisSessionStore store)
    {
        var scope = new AnalysisScope { IncludedFolders = [@"C:\previous"] };
        store.SetCompleted(ToolKind.BigFiles, scope, NewResult([NewFinding(@"C:\previous\large.iso", 900)]));
        return Assert.IsType<AnalysisSession>(store.CurrentSession);
    }

    private static AnalysisResult NewResult(
        IReadOnlyList<PathFinding>? findings = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new()
        {
            Findings = findings ?? [],
            Groups = [],
            SkippedPaths = skippedPaths ?? [],
            Elapsed = TimeSpan.FromSeconds(1),
        };

    private static PathFinding NewFinding(string path, long size) => new()
    {
        FullPath = path,
        Kind = PathFindingKind.File,
        Reason = "Test finding",
        SizeBytes = size,
    };

    private sealed class FakeAnalysisService : IAnalysisService
    {
        public Func<ToolKind, AnalysisScope, ToolOptions, IProgress<AnalysisProgress>?, CancellationToken, Task<AnalysisResult>> Run { get; init; } =
            (_, _, _, _, _) => Task.FromResult(NewResult());

        public int CallCount { get; private set; }

        public Task<AnalysisResult> RunAsync(
            ToolKind tool,
            AnalysisScope scope,
            ToolOptions toolOptions,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Run(tool, scope, toolOptions, progress, cancellationToken);
        }
    }
}
