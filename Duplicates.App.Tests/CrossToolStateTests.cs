using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

public sealed class CrossToolStateTests
{
    [Fact]
    public async Task ResetConfirmation_OpeningAndDeclinePreserveCapturedSession()
    {
        var coordinator = new AppOperationCoordinator();
        using var guard = new MainWindowOperationGuard(coordinator);
        object session = new();
        object? currentSession = session;
        var promptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool resetCalled = false;

        Task<bool> confirmation = guard.ConfirmResetAsync(
            async () =>
            {
                promptShown.SetResult();
                return await promptResult.Task;
            },
            () =>
            {
                resetCalled = true;
                if (!ReferenceEquals(currentSession, session))
                {
                    return false;
                }

                currentSession = null;
                return true;
            });

        await promptShown.Task;
        Assert.Same(session, currentSession);
        Assert.False(resetCalled);

        promptResult.SetResult(false);

        Assert.False(await confirmation);
        Assert.Same(session, currentSession);
        Assert.False(resetCalled);
    }

    [Fact]
    public async Task ExactReset_DeclinePreservesStateAndConfirmClearsOnlyCapturedSession()
    {
        var coordinator = new AppOperationCoordinator();
        var store = new ResultsStore();
        var viewModel = NewExactViewModel(store, coordinator: coordinator);
        store.SetResult(NewExactResult(NewExactGroup("one", "a.bin", "b.bin", "c.bin")));
        ExactResultsSession captured = store.CurrentSession!;
        DuplicateFileViewModel selected = viewModel.Groups[0].Files[1];
        selected.IsSelected = true;
        viewModel.SearchText = "b.bin";
        viewModel.SelectedSortIndex = 2;
        viewModel.SelectedFile = selected;
        using var guard = new MainWindowOperationGuard(coordinator);

        bool declined = await guard.ConfirmResetAsync(
            static () => Task.FromResult(false),
            () => viewModel.TryResetSession(captured));

        Assert.False(declined);
        Assert.Same(captured, store.CurrentSession);
        Assert.True(selected.IsSelected);
        Assert.Equal("b.bin", viewModel.SearchText);
        Assert.Equal(2, viewModel.SelectedSortIndex);
        Assert.Same(selected, viewModel.SelectedFile);

        bool confirmed = await guard.ConfirmResetAsync(
            static () => Task.FromResult(true),
            () => viewModel.TryResetSession(captured));

        Assert.True(confirmed);
        Assert.Null(store.CurrentSession);
        Assert.Empty(viewModel.Groups);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(0, viewModel.SelectedSortIndex);
        Assert.Null(viewModel.SelectedFile);
    }

    [Fact]
    public async Task AnalysisReset_DeclinePreservesStateAndConfirmClearsOnlyCapturedSession()
    {
        var coordinator = new AppOperationCoordinator();
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(
            store,
            new FakeFileActionService(),
            new FakeResultExportService(),
            analysisService: null,
            mediaPreviewLoader: null,
            operationCoordinator: coordinator);
        store.SetCompleted(
            ToolKind.EmptyFiles,
            new AnalysisScope(),
            new AnalysisResult
            {
                Findings = [NewFinding(@"C:\scan\empty.bin", 0)],
                Groups = [],
                SkippedPaths = [],
                Elapsed = TimeSpan.Zero,
            });
        AnalysisSession captured = store.CurrentSession!;
        PathFindingViewModel selected = Assert.Single(viewModel.Findings);
        selected.IsSelected = true;
        viewModel.SearchText = "empty";
        viewModel.SelectedSortIndex = 1;
        viewModel.SelectedResult = selected;
        using var guard = new MainWindowOperationGuard(coordinator);

        bool declined = await guard.ConfirmResetAsync(
            static () => Task.FromResult(false),
            () => viewModel.TryResetSession(captured));

        Assert.False(declined);
        Assert.Same(captured, store.CurrentSession);
        Assert.True(selected.IsSelected);
        Assert.Equal("empty", viewModel.SearchText);
        Assert.Equal(1, viewModel.SelectedSortIndex);
        Assert.Same(selected, viewModel.SelectedResult);

        bool confirmed = await guard.ConfirmResetAsync(
            static () => Task.FromResult(true),
            () => viewModel.TryResetSession(captured));

        Assert.True(confirmed);
        Assert.Null(store.CurrentSession);
        Assert.Empty(viewModel.ResultItems);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(0, viewModel.SelectedSortIndex);
        Assert.Null(viewModel.SelectedResult);
    }

    [Fact]
    public async Task ExactGroups_PreviewClearsWhenSuccessfulActionCollapsesItsCanonicalGroup()
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewExactViewModel(store, actions);
        store.SetResult(NewExactResult(NewExactGroup("two-files", "keep.bin", "delete.bin")));
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        DuplicateFileViewModel selected = Assert.Single(group.Files, static file => file.IsSelected);
        DuplicateFileViewModel survivorPreview = Assert.Single(group.Files, static file => !file.IsSelected);
        viewModel.SelectedFile = survivorPreview;
        actions.NextSummary = new DeleteSummary(
            1,
            selected.SizeBytes,
            [],
            [selected.FullPath]);

        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Empty(viewModel.Groups);
        Assert.Null(viewModel.SelectedFile);
    }

    [Fact]
    public void NewAnalysisCommand_RequestsConfirmationWithoutChangingCurrentState()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.EmptyFiles,
            new AnalysisScope(),
            new AnalysisResult
            {
                Findings = [NewFinding(@"C:\scan\empty.bin", 0)],
                Groups = [],
                SkippedPaths = [],
                Elapsed = TimeSpan.Zero,
            });
        AnalysisSession captured = store.CurrentSession!;
        PathFindingViewModel finding = Assert.Single(viewModel.Findings);
        finding.IsSelected = true;
        viewModel.SearchText = "empty";
        viewModel.SelectedSortIndex = 1;
        viewModel.SelectedResult = finding;
        ToolKind? requestedTool = null;
        viewModel.NewAnalysisRequested += (_, tool) => requestedTool = tool;

        viewModel.NewAnalysisCommand.Execute(null);

        Assert.Equal(ToolKind.EmptyFiles, requestedTool);
        Assert.Same(captured, store.CurrentSession);
        Assert.True(finding.IsSelected);
        Assert.Equal("empty", viewModel.SearchText);
        Assert.Equal(1, viewModel.SelectedSortIndex);
        Assert.Same(finding, viewModel.SelectedResult);
    }

    [Fact]
    public async Task ExactGroups_FilterSortAndPartialAction_PreserveCanonicalState()
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewExactViewModel(store, actions);
        store.SetResult(NewExactResult(
            NewExactGroup("visible", "success.bin", "failed.bin", "preview.bin", "keep.bin"),
            NewExactGroup("hidden", "unattempted.bin", "hidden-keep.bin", "hidden-survivor.bin")));
        DuplicateGroupViewModel visible = viewModel.Groups.Single(group => group.Files[0].DirectoryPath.EndsWith("visible"));
        DuplicateGroupViewModel hidden = viewModel.Groups.Single(group => group.Files[0].DirectoryPath.EndsWith("hidden"));
        foreach (DuplicateFileViewModel file in visible.Files.Concat(hidden.Files))
        {
            file.IsSelected = false;
        }

        DuplicateFileViewModel success = visible.Files.Single(file => file.FileName == "success.bin");
        DuplicateFileViewModel failed = visible.Files.Single(file => file.FileName == "failed.bin");
        DuplicateFileViewModel preview = visible.Files.Single(file => file.FileName == "preview.bin");
        DuplicateFileViewModel unattempted = hidden.Files.Single(file => file.FileName == "unattempted.bin");
        success.IsSelected = true;
        failed.IsSelected = true;
        unattempted.IsSelected = true;
        viewModel.SelectedFile = preview;
        viewModel.SearchText = "visible";
        DuplicateGroupViewModel visibleIdentity = Assert.Single(viewModel.Groups);
        viewModel.SelectedSortIndex = 1;
        Assert.Same(visibleIdentity, Assert.Single(viewModel.Groups));
        Assert.Equal(3, viewModel.SelectedFileCount);
        Assert.Equal(300, viewModel.SelectedBytes);
        IReadOnlyList<FileActionTarget>? snapshot = null;
        actions.OnDelete = (targets, _) => snapshot = targets.ToArray();
        actions.NextSummary = new DeleteSummary(
            1,
            success.SizeBytes,
            [new FileActionFailure(failed.FullPath, "Access denied")],
            [success.FullPath]);

        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal(
            new[] { success.FullPath, failed.FullPath, unattempted.FullPath }.Order(StringComparer.OrdinalIgnoreCase),
            snapshot!.Select(static target => target.FullPath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("visible", viewModel.SearchText);
        Assert.Equal(1, viewModel.SelectedSortIndex);
        Assert.Same(preview, viewModel.SelectedFile);
        Assert.Equal(2, viewModel.SelectedFileCount);
        Assert.Contains(viewModel.GetSelectedFiles(), file => ReferenceEquals(file, failed));
        Assert.Contains(viewModel.GetSelectedFiles(), file => ReferenceEquals(file, unattempted));
        viewModel.SearchText = string.Empty;
        Assert.DoesNotContain(viewModel.Groups.SelectMany(static group => group.Files), file => file.FullPath == success.FullPath);
        Assert.Contains(viewModel.Groups.SelectMany(static group => group.Files), file => ReferenceEquals(file, preview));
    }

    [Fact]
    public async Task ExactActionCompletion_DoesNotReconcileIntoReplacementSession()
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewExactViewModel(store, actions);
        store.SetResult(NewExactResult(NewExactGroup(
            "shared",
            "delete.bin",
            "failed.bin",
            "old-survivor.bin")));
        ExactResultsSession initiatingSession = store.CurrentSession!;
        DuplicateFileViewModel successful = viewModel.GetSelectedFiles().Single(file => file.FileName == "delete.bin");
        DuplicateFileViewModel failed = viewModel.GetSelectedFiles().Single(file => file.FileName == "failed.bin");
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DeleteSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<DeleteProgress>? progress = null;
        actions.OnDelete = (_, value) => progress = value;
        actions.DeleteHandler = (_, _) =>
        {
            dispatched.SetResult();
            return release.Task;
        };

        Task<DeleteSummary> deletion = viewModel.DeleteSelectedAsync(CancellationToken.None);
        await dispatched.Task;
        progress!.Report(new DeleteProgress(1, 2, successful.FullPath, successful.SizeBytes));
        store.SetResult(NewExactResult(NewExactGroup(
            "shared",
            "delete.bin",
            "failed.bin",
            "replacement-survivor.bin")));
        ExactResultsSession replacementSession = store.CurrentSession!;
        release.SetResult(new DeleteSummary(
            1,
            successful.SizeBytes,
            [new FileActionFailure(failed.FullPath, "Access denied")],
            [successful.FullPath]));

        await deletion;

        Assert.NotSame(initiatingSession, replacementSession);
        Assert.Same(replacementSession, store.CurrentSession);
        DuplicateGroupViewModel replacementGroup = Assert.Single(viewModel.Groups);
        Assert.Contains(replacementGroup.Files, file => file.FullPath == successful.FullPath);
        Assert.Contains(replacementGroup.Files, file => file.FullPath == failed.FullPath);
        Assert.Equal(string.Empty, viewModel.DeleteStatusMessage);
        Assert.Equal(string.Empty, viewModel.DeleteFailureDetailsText);
        Assert.Equal(string.Empty, viewModel.DeleteProgressText);
        Assert.Equal(0, viewModel.DeleteProgressValue);
    }

    [Fact]
    public async Task ExactMoveCompletion_DoesNotReconcileIntoReplacementSession()
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewExactViewModel(store, actions);
        store.SetResult(NewExactResult(NewExactGroup(
            "shared",
            "move.bin",
            "failed.bin",
            "old-survivor.bin")));
        ExactResultsSession initiatingSession = store.CurrentSession!;
        DuplicateFileViewModel successful = viewModel.GetSelectedFiles().Single(file => file.FileName == "move.bin");
        DuplicateFileViewModel failed = viewModel.GetSelectedFiles().Single(file => file.FileName == "failed.bin");
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<FileOperationSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.MoveHandler = (_, _, _, _) =>
        {
            dispatched.SetResult();
            return release.Task;
        };

        Task<FileOperationSummary> moving = viewModel.MoveSelectedAsync(
            @"C:\destination",
            MoveCollisionBehavior.Skip,
            CancellationToken.None);
        await dispatched.Task;
        store.SetResult(NewExactResult(NewExactGroup(
            "shared",
            "move.bin",
            "failed.bin",
            "replacement-survivor.bin")));
        ExactResultsSession replacementSession = store.CurrentSession!;
        release.SetResult(new FileOperationSummary(
        [
            new FileOperationResult(successful.FullPath, @"C:\destination\move.bin", null),
            new FileOperationResult(
                failed.FullPath,
                null,
                new FileActionFailure(failed.FullPath, "Access denied"))
        ], successful.SizeBytes));

        await moving;

        Assert.NotSame(initiatingSession, replacementSession);
        Assert.Same(replacementSession, store.CurrentSession);
        DuplicateGroupViewModel replacementGroup = Assert.Single(viewModel.Groups);
        Assert.Contains(replacementGroup.Files, file => file.FullPath == successful.FullPath);
        Assert.Contains(replacementGroup.Files, file => file.FullPath == failed.FullPath);
        Assert.Equal(string.Empty, viewModel.DeleteStatusMessage);
        Assert.Equal(string.Empty, viewModel.DeleteFailureDetailsText);
        Assert.Equal(string.Empty, viewModel.DeleteProgressText);
        Assert.Equal(0, viewModel.DeleteProgressValue);
    }

    [Fact]
    public async Task AnalysisActionCompletion_DoesNotReconcileIntoReplacementSession()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-task20-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string successfulPath = Path.Combine(root, "delete.bin");
        string failedPath = Path.Combine(root, "failed.bin");
        await File.WriteAllBytesAsync(successfulPath, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(failedPath, [], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var actions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(
                store,
                actions,
                new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                NewAnalysisResult(findings:
                [
                    NewFinding(successfulPath, 0),
                    NewFinding(failedPath, 0)
                ]));
            AnalysisSession initiatingSession = store.CurrentSession!;
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<DeleteSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            actions.DeleteHandler = (_, _) =>
            {
                dispatched.SetResult();
                return release.Task;
            };

            Task<DeleteSummary> deletion = viewModel.DeleteSelectedAsync(CancellationToken.None);
            await dispatched.Task;
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                NewAnalysisResult(findings:
                [
                    NewFinding(successfulPath, 0),
                    NewFinding(failedPath, 0)
                ]));
            AnalysisSession replacementSession = store.CurrentSession!;
            release.SetResult(new DeleteSummary(
                1,
                0,
                [new FileActionFailure(failedPath, "Access denied")],
                [successfulPath]));

            await deletion;

            Assert.NotSame(initiatingSession, replacementSession);
            Assert.Same(replacementSession, store.CurrentSession);
            Assert.Equal(2, viewModel.Findings.Count);
            Assert.Contains(viewModel.Findings, finding => finding.FullPath == successfulPath);
            Assert.Equal(string.Empty, viewModel.ActionStatusMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalysisMoveCompletion_DoesNotReconcileIntoReplacementSession()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-task20-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string successfulPath = Path.Combine(root, "move.bin");
        string failedPath = Path.Combine(root, "failed.bin");
        await File.WriteAllBytesAsync(successfulPath, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(failedPath, [], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var actions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(
                store,
                actions,
                new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                NewAnalysisResult(findings:
                [
                    NewFinding(successfulPath, 0),
                    NewFinding(failedPath, 0)
                ]));
            AnalysisSession initiatingSession = store.CurrentSession!;
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<FileOperationSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            actions.MoveHandler = (_, _, _, _) =>
            {
                dispatched.SetResult();
                return release.Task;
            };

            string destination = Path.Combine(root, "moved");
            Task<FileOperationSummary> moving = viewModel.MoveSelectedAsync(
                destination,
                MoveCollisionBehavior.Skip,
                CancellationToken.None);
            await dispatched.Task;
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                NewAnalysisResult(findings:
                [
                    NewFinding(successfulPath, 0),
                    NewFinding(failedPath, 0)
                ]));
            AnalysisSession replacementSession = store.CurrentSession!;
            release.SetResult(new FileOperationSummary(
            [
                new FileOperationResult(successfulPath, Path.Combine(destination, "move.bin"), null),
                new FileOperationResult(
                    failedPath,
                    null,
                    new FileActionFailure(failedPath, "Access denied"))
            ], 0));

            await moving;

            Assert.NotSame(initiatingSession, replacementSession);
            Assert.Same(replacementSession, store.CurrentSession);
            Assert.Equal(2, viewModel.Findings.Count);
            Assert.Contains(viewModel.Findings, finding => finding.FullPath == successfulPath);
            Assert.Equal(string.Empty, viewModel.ActionStatusMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResetConfirmation_ReplacementSessionPreventsOldSessionClear()
    {
        var coordinator = new AppOperationCoordinator();
        var store = new ResultsStore();
        var viewModel = NewExactViewModel(store, coordinator: coordinator);
        store.SetResult(NewExactResult(NewExactGroup("old", "a.bin", "b.bin")));
        ExactResultsSession captured = store.CurrentSession!;
        using var guard = new MainWindowOperationGuard(coordinator);
        var promptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> confirmation = guard.ConfirmResetAsync(
            async () =>
            {
                promptShown.SetResult();
                return await promptResult.Task;
            },
            () => viewModel.TryResetSession(captured));
        await promptShown.Task;
        store.SetResult(NewExactResult(NewExactGroup("replacement", "new-a.bin", "new-b.bin")));
        ExactResultsSession replacement = store.CurrentSession!;
        promptResult.SetResult(true);

        Assert.False(await confirmation);
        Assert.Same(replacement, store.CurrentSession);
        Assert.Single(viewModel.Groups);
    }

    [Fact]
    public async Task ResetConfirmation_OperationGenerationChangeWhileOpenPreventsReset()
    {
        var coordinator = new AppOperationCoordinator();
        using var guard = new MainWindowOperationGuard(coordinator);
        var promptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int resetCount = 0;

        Task<bool> confirmation = guard.ConfirmResetAsync(
            async () =>
            {
                promptShown.SetResult();
                return await promptResult.Task;
            },
            () =>
            {
                resetCount++;
                return true;
            });
        await promptShown.Task;
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisResultsAction),
            static () => { },
            out IAppOperationLease? lease));
        lease!.Dispose();
        promptResult.SetResult(true);

        Assert.False(await confirmation);
        Assert.Equal(0, resetCount);
    }

    [Fact]
    public async Task ResetConfirmation_ActiveCoordinatorAndConcurrentDialogPreventReset()
    {
        var coordinator = new AppOperationCoordinator();
        using var guard = new MainWindowOperationGuard(coordinator);
        int promptCount = 0;
        int resetCount = 0;
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisResultsAction),
            static () => { },
            out IAppOperationLease? lease));

        bool blockedByOperation = await guard.ConfirmResetAsync(
            () =>
            {
                promptCount++;
                return Task.FromResult(true);
            },
            () =>
            {
                resetCount++;
                return true;
            });
        lease!.Dispose();

        Assert.False(blockedByOperation);
        Assert.Equal(0, promptCount);
        Assert.Equal(0, resetCount);

        var promptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = guard.ConfirmResetAsync(
            async () =>
            {
                promptCount++;
                promptShown.SetResult();
                return await promptResult.Task;
            },
            () =>
            {
                resetCount++;
                return true;
            });
        await promptShown.Task;
        bool concurrent = await guard.ConfirmResetAsync(
            static () => Task.FromResult(true),
            () =>
            {
                resetCount++;
                return true;
            });
        promptResult.SetResult(false);

        Assert.False(concurrent);
        Assert.False(await first);
        Assert.Equal(1, promptCount);
        Assert.Equal(0, resetCount);
    }

    [Fact]
    public async Task FlatFindings_FilterSortAndPartialAction_PreserveCanonicalState()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-task20-{Guid.NewGuid():N}");
        string visible = Path.Combine(root, "visible");
        string hidden = Path.Combine(root, "hidden");
        Directory.CreateDirectory(visible);
        Directory.CreateDirectory(hidden);
        string successPath = Path.Combine(visible, "success.bin");
        string failedPath = Path.Combine(visible, "failed.bin");
        string previewPath = Path.Combine(visible, "preview.bin");
        string unattemptedPath = Path.Combine(hidden, "unattempted.bin");
        await File.WriteAllBytesAsync(successPath, new byte[10], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(failedPath, new byte[20], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(previewPath, new byte[30], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(unattemptedPath, new byte[40], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var actions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, actions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BigFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new LargeFileToolOptions(1),
                new AnalysisResult
                {
                    Findings =
                    [
                        NewFinding(successPath, 10),
                        NewFinding(failedPath, 20),
                        NewFinding(previewPath, 30),
                        NewFinding(unattemptedPath, 40)
                    ],
                    Groups = [],
                    SkippedPaths = [],
                    Elapsed = TimeSpan.Zero,
                });
            PathFindingViewModel success = viewModel.Findings.Single(item => item.FullPath == successPath);
            PathFindingViewModel failed = viewModel.Findings.Single(item => item.FullPath == failedPath);
            PathFindingViewModel preview = viewModel.Findings.Single(item => item.FullPath == previewPath);
            PathFindingViewModel unattempted = viewModel.Findings.Single(item => item.FullPath == unattemptedPath);
            success.IsSelected = true;
            failed.IsSelected = true;
            unattempted.IsSelected = true;
            viewModel.SelectedResult = preview;
            viewModel.SearchText = "visible";
            PathFindingViewModel[] visibleIdentities = viewModel.Findings.ToArray();
            viewModel.SelectedSortIndex = 2;
            Assert.All(viewModel.Findings, item => Assert.Contains(item, visibleIdentities));
            Assert.Equal(3, viewModel.SelectedItemCount);
            Assert.Equal(70, viewModel.SelectedBytes);
            IReadOnlyList<FileActionTarget>? snapshot = null;
            actions.OnDelete = (targets, _) => snapshot = targets.ToArray();
            actions.NextSummary = new DeleteSummary(
                1,
                10,
                [new FileActionFailure(failedPath, "Access denied")],
                [successPath]);

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(1, summary.DeletedCount);
            Assert.Equal(
                new[] { successPath, failedPath, unattemptedPath }.Order(StringComparer.OrdinalIgnoreCase),
                snapshot!.Select(static target => target.FullPath).Order(StringComparer.OrdinalIgnoreCase));
            Assert.Equal("visible", viewModel.SearchText);
            Assert.Equal(2, viewModel.SelectedSortIndex);
            Assert.Same(preview, viewModel.SelectedResult);
            Assert.Equal(2, viewModel.SelectedItemCount);
            Assert.Equal(60, viewModel.SelectedBytes);
            Assert.Contains(viewModel.GetSelectedFindings(), item => ReferenceEquals(item, failed));
            Assert.Contains(viewModel.GetSelectedFindings(), item => ReferenceEquals(item, unattempted));
            viewModel.SearchText = string.Empty;
            Assert.DoesNotContain(viewModel.Findings, item => item.FullPath == successPath);
            Assert.Contains(viewModel.Findings, item => ReferenceEquals(item, preview));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimilarityGroups_FilterSortAndPartialAction_RebindCanonicalPreview()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-task20-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            SimilarityItem first = WriteVideoItem(root, "a.mp4", 0, width: 300, height: 300);
            SimilarityItem articulation = WriteVideoItem(root, "b.mp4", LowBits(9), width: 200, height: 200);
            SimilarityItem tail = WriteVideoItem(root, "c.mp4", LowBits(18), width: 100, height: 100);
            SimilarityItem otherReference = WriteVideoItem(root, "x.mp4", ulong.MaxValue, width: 200, height: 200);
            SimilarityItem failed = WriteVideoItem(root, "y.mp4", ulong.MaxValue ^ 1, width: 100, height: 100);
            var analysis = new Task20VideoAnalysisService
            {
                Revalidate = item => string.Equals(item.FullPath, failed.FullPath, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<bool>(new IOException("decode"))
                    : Task.FromResult(true),
            };
            string destination = Path.Combine(root, "moved");
            var actions = new FakeFileActionService
            {
                NextMoveSummary = new FileOperationSummary(
                    [new FileOperationResult(articulation.FullPath, Path.Combine(destination, "b.mp4"), null)],
                    articulation.SizeBytes),
            };
            var exporter = new FakeResultExportService();
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(
                store,
                actions,
                exporter,
                analysis,
                new Task20PreviewLoader());
            var skip = new SkippedPath
            {
                Path = Path.Combine(root, "skipped.mp4"),
                Reason = "Could not decode video.",
            };
            store.SetCompleted(
                ToolKind.SimilarVideos,
                new AnalysisScope { IncludedFolders = [root] },
                new SimilarVideoToolOptions(9),
                NewAnalysisResult(
                    groups:
                    [
                        NewVideoGroup(first, articulation, tail),
                        NewVideoGroup(otherReference, failed)
                    ],
                    skippedPaths: [skip]));
            SimilarityGroupViewModel firstGroup = viewModel.Groups.Single(group =>
                group.Items.Any(item => item.FullPath == articulation.FullPath));
            foreach (SimilarityItemViewModel item in firstGroup.Items)
            {
                item.IsSelected = true;
            }

            SimilarityItemViewModel failedViewModel = viewModel.Groups
                .SelectMany(static group => group.Items)
                .Single(item => item.FullPath == failed.FullPath);
            failedViewModel.IsSelected = true;
            await viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(failedViewModel);
            viewModel.SearchText = "y.mp4";
            viewModel.SelectedSortIndex = 1;
            Assert.Single(viewModel.Groups);
            Assert.Equal(4, viewModel.SelectedItemCount);

            FileOperationSummary summary = await viewModel.MoveSelectedAsync(
                destination,
                MoveCollisionBehavior.Skip,
                CancellationToken.None);

            Assert.Single(summary.Results, static result => result.Succeeded);
            FileOperationResult failure = Assert.Single(summary.Results, static result => !result.Succeeded);
            Assert.Equal(failed.FullPath, failure.SourcePath);
            Assert.Equal("Could not decode video.", failure.Failure?.Reason);
            SimilarityGroupViewModel remaining = Assert.Single(viewModel.Groups);
            Assert.Equal(otherReference.FullPath, remaining.ReferenceItem.FullPath);
            SimilarityItemViewModel rebound = remaining.Items.Single(item => item.FullPath == failed.FullPath);
            Assert.True(rebound.IsSelected);
            Assert.NotSame(failedViewModel, rebound);
            Assert.Same(rebound, viewModel.SelectedSimilarityPreviewItem);
            Assert.Equal(failed.FullPath, viewModel.PreviewPath);
            Assert.Equal(1, viewModel.SelectedItemCount);
            Assert.Equal("y.mp4", viewModel.SearchText);
            Assert.Equal(1, viewModel.SelectedSortIndex);
            Assert.Same(skip, store.CurrentSession!.Result.SkippedPaths[0]);

            await viewModel.ExportAsync(
                ResultExportFormat.Json,
                Path.Combine(root, "results.json"),
                CancellationToken.None);
            Assert.NotNull(exporter.Snapshot);
            Assert.Equal(2, exporter.Snapshot.Items.Count);
            Assert.DoesNotContain(exporter.Snapshot.Items, item => item.FullPath == articulation.FullPath);
            SkippedPath exportedSkip = Assert.Single(exporter.Snapshot.SkippedPaths);
            Assert.Equal(skip.Path, exportedSkip.Path);
            Assert.Equal(skip.Reason, exportedSkip.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ResultsViewModel NewExactViewModel(
        ResultsStore store,
        FakeFileActionService? actions = null,
        AppOperationCoordinator? coordinator = null) => new(
            store,
            actions ?? new FakeFileActionService(),
            new FakeSettingsService(),
            new FakeResultExportService(),
            operationCoordinator: coordinator);

    private static ScanResult NewExactResult(params DuplicateGroup[] groups) => new()
    {
        Groups = groups,
        TotalFilesScanned = groups.Sum(static group => group.Files.Count),
        TotalDuplicateFiles = groups.Sum(static group => group.Files.Count - 1),
        TotalReclaimableBytes = groups.Sum(static group => group.WastedBytes),
        Elapsed = TimeSpan.Zero,
        SkippedPaths = [],
    };

    private static DuplicateGroup NewExactGroup(string folder, params string[] names)
    {
        DateTime modified = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        return new DuplicateGroup
        {
            ContentHash = (ulong)StringComparer.Ordinal.GetHashCode(folder),
            SizeBytes = 100,
            Files = names.Select((name, index) => new FileEntry
            {
                FullPath = Path.Combine(@"C:\scan", folder, name),
                FileName = name,
                DirectoryPath = Path.Combine(@"C:\scan", folder),
                Extension = Path.GetExtension(name),
                SizeBytes = 100,
                CreatedUtc = modified.AddDays(-1),
                ModifiedUtc = modified.AddMinutes(index),
            }).ToArray(),
        };
    }

    private static PathFinding NewFinding(string path, long size) => new()
    {
        FullPath = path,
        Kind = PathFindingKind.File,
        Reason = "File is empty",
        SizeBytes = size,
    };

    private static AnalysisResult NewAnalysisResult(
        IReadOnlyList<PathFinding>? findings = null,
        IReadOnlyList<SimilarityGroup>? groups = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new()
        {
            Findings = findings ?? [],
            Groups = groups ?? [],
            SkippedPaths = skippedPaths ?? [],
            Elapsed = TimeSpan.Zero,
        };

    private static SimilarityGroup NewVideoGroup(params SimilarityItem[] items) => Assert.Single(
        SimilarVideoAnalyzer.Regroup(
            items,
            new SimilarVideoOptions(9)));

    private static SimilarityItem WriteVideoItem(
        string root,
        string name,
        ulong hash,
        int width,
        int height)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        DateTime modifiedUtc = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return new SimilarityItem
        {
            FullPath = path,
            SizeBytes = new FileInfo(path).Length,
            ModifiedUtc = modifiedUtc,
            SimilarityPercent = 0,
            Evidence = new VideoSimilarityEvidence(
                hash,
                hash,
                hash,
                hash,
                hash,
                width,
                height,
                (double)width / height,
                TimeSpan.FromSeconds(10),
                100,
                30,
                "H264"),
        };
    }

    private static ulong LowBits(int count) => (1UL << count) - 1;

    private sealed class Task20VideoAnalysisService : IAnalysisService
    {
        public Func<SimilarityItem, Task<bool>> Revalidate { get; init; } = _ => Task.FromResult(true);

        public Task<AnalysisResult> RunAsync(
            ToolKind tool,
            AnalysisScope scope,
            IToolOptions toolOptions,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RevalidateSimilarityItemAsync(
            ToolKind tool,
            SimilarityItem item,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ToolKind.SimilarVideos, tool);
            return Revalidate(item);
        }

        public IReadOnlyList<SimilarityGroup> RegroupSimilarityItems(
            ToolKind tool,
            IToolOptions options,
            IReadOnlyList<SimilarityItem> items)
        {
            Assert.Equal(ToolKind.SimilarVideos, tool);
            var videoOptions = Assert.IsType<SimilarVideoToolOptions>(options);
            return SimilarVideoAnalyzer.Regroup(
                items,
                new SimilarVideoOptions(videoOptions.MaximumMeanFrameDistance));
        }
    }

    private sealed class Task20PreviewLoader : IMediaPreviewLoader
    {
        public Task<MediaPreviewData> LoadAsync(
            ToolKind tool,
            SimilarityItem item,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MediaPreviewData(1, 1, [1, 1, 1, 255]));
    }
}
