using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class ResultsViewModelTests
{
    [Fact]
    public async Task DeleteBeforeFirstCancellationPreservesRowsAndReportsCancellation()
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService
        {
            DeleteHandler = (_, _) => Task.FromCanceled<DeleteSummary>(new CancellationToken(canceled: true)),
        };
        var viewModel = NewViewModel(store, actions);
        store.SetResult(NewResult(NewGroup(1, "files", "keep.bin", "delete.bin")));
        DuplicateFileViewModel[] rows = viewModel.Groups[0].Files.ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewModel.DeleteSelectedAsync(CancellationToken.None));

        Assert.Equal(rows, viewModel.Groups[0].Files);
        Assert.False(viewModel.IsDeleting);
        Assert.Contains("cancelled", viewModel.DeleteStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before any file", viewModel.DeleteStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CopyPathPreservesLiteralRowAndReportsNativeFailure(int error)
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup(1, "files", new string('a', 160) + " ä 東京.txt", "other.txt")));
        DuplicateFileViewModel[] rows = viewModel.Groups[0].Files.ToArray();
        viewModel.SelectedFile = rows[1];
        viewModel.DeleteStatusMessage = "Previous export completed.";
        string? copiedPath = null;

        Exception? failure = Record.Exception(() => viewModel.CopyPath(rows[0], path =>
        {
            copiedPath = path;
            if (error == 1) throw new System.Runtime.InteropServices.COMException("Clipboard is busy", unchecked((int)0x800401D0));
            if (error == 2) throw new UnauthorizedAccessException("Clipboard access denied");
            if (error == 3) throw new InvalidOperationException("Clipboard unavailable");
        }));

        Assert.Null(failure);
        Assert.Equal(rows[0].FullPath, copiedPath);
        Assert.Equal(rows, viewModel.Groups[0].Files);
        Assert.Same(rows[1], viewModel.SelectedFile);
        Assert.False(viewModel.IsDeleting);
        if (error != 0)
        {
            Assert.StartsWith("Could not copy path:", viewModel.DeleteStatusMessage, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(false, 5)]
    [InlineData(false, 1155)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    [InlineData(true, 5)]
    [InlineData(true, 1155)]
    public void ShellFailureIsVisibleAndPreservesCanonicalRowsAndSelection(bool reveal, int error)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewViewModel(store, actions);
        store.SetResult(NewResult(NewGroup(1, "files", "ä & copy.txt", "other.txt")));
        DuplicateFileViewModel[] rows = viewModel.Groups[0].Files.ToArray();
        viewModel.SelectedFile = rows[0];
        int selectedCount = viewModel.SelectedFileCount;
        string? requestedPath = null;
        Action<string> launch = path =>
        {
            requestedPath = path;
            if (error != 0)
            {
                throw new System.ComponentModel.Win32Exception(error, "The shell could not open the requested path.");
            }
        };
        actions.OnOpen = launch;
        actions.OnReveal = launch;

        Exception? failure = Record.Exception(() =>
        {
            if (reveal) viewModel.RevealFile(rows[0]);
            else viewModel.OpenFile(rows[0]);
        });

        Assert.Null(failure);
        Assert.Equal(rows[0].FullPath, requestedPath);
        Assert.Equal(rows, viewModel.Groups[0].Files);
        Assert.Same(rows[0], viewModel.SelectedFile);
        Assert.Equal(selectedCount, viewModel.SelectedFileCount);
        Assert.Equal(error != 0, viewModel.IsDeleteStatusOpen);
        Assert.Equal(0, actions.DeleteCallCount);
        Assert.Equal(0, actions.MoveCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactActionsCarryScanModifiedUtcToNativePreflight(bool move)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewViewModel(store, actions);
        store.SetResult(NewResult(NewGroup(1, "files", "a.bin", "b.bin")));
        DuplicateFileViewModel selected = viewModel.SelectedFiles[0];
        IReadOnlyList<FileActionTarget>? targets = null;
        actions.OnDelete = (files, _) => targets = files;
        actions.OnMove = (files, _, _, _) => targets = files;

        if (move)
        {
            await viewModel.MoveSelectedAsync(@"C:\destination", MoveCollisionBehavior.Skip, CancellationToken.None);
        }
        else
        {
            await viewModel.DeleteSelectedAsync(CancellationToken.None);
        }

        Assert.Equal(selected.File.ModifiedUtc, Assert.Single(targets!).ExpectedModifiedUtc);
        ExactFileConstraint survivor = Assert.Single(targets![0].ExpectedExactSurvivors!);
        Assert.EndsWith("b.bin", survivor.FullPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeletionMode.RecycleBin)]
    [InlineData(DeletionMode.Permanent)]
    public async Task ConfirmedDeleteRejectsAChangedModeAndDispatchesTheCapturedMode(DeletionMode mode)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var settings = new FakeSettingsService();
        settings.SetCurrent(new AppSettings { DeletionMode = mode });
        var viewModel = new ResultsViewModel(store, actions, settings, new FakeResultExportService());
        store.SetResult(NewResult(NewGroup(1, "files", "a.bin", "b.bin")));
        ExactDeleteSnapshot snapshot = viewModel.CreateDeleteSnapshot();
        settings.SetCurrent(new AppSettings { DeletionMode = mode == DeletionMode.RecycleBin ? DeletionMode.Permanent : DeletionMode.RecycleBin });

        Assert.False(viewModel.IsDeleteSnapshotCurrent(snapshot));
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.DeleteConfirmedAsync(snapshot, CancellationToken.None));
        Assert.Equal(0, actions.DeleteCallCount);

        settings.SetCurrent(new AppSettings { DeletionMode = mode });
        await viewModel.DeleteConfirmedAsync(snapshot, CancellationToken.None);
        Assert.Equal(mode, actions.LastDeletionMode);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task ConfirmedDeleteRejectsReplacedSessionsAndChangedCanonicalRequests(bool singleFile, int mutation)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewViewModel(store, actions);
        store.SetResult(NewResult(NewGroup(1, "files", "a.bin", "b.bin", "c.bin")));
        DuplicateFileViewModel file = viewModel.Groups[0].Files[0];
        ExactDeleteSnapshot snapshot = viewModel.CreateDeleteSnapshot(singleFile ? file : null);
        switch (mutation)
        {
            case 0:
                store.SetResult(NewResult(NewGroup(2, "replacement", "a.bin", "b.bin")));
                break;
            case 1:
                viewModel.ClearSelectionCommand.Execute(null);
                break;
            case 2:
                viewModel.ExcludeFileCommand.Execute(file);
                break;
        }

        Assert.False(viewModel.IsDeleteSnapshotCurrent(snapshot));
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.DeleteConfirmedAsync(snapshot, CancellationToken.None));
        Assert.Equal(0, actions.DeleteCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteProgressUsesCapturedContextAndRejectsReplacedSession(bool replaceSession)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewViewModel(store, actions);
        store.SetResult(NewResult(NewGroup(1, "one", "a.txt", "b.txt")));
        IProgress<DeleteProgress>? progress = null;
        actions.OnDelete = (_, value) => progress = value;
        var completion = new TaskCompletionSource<DeleteSummary>();
        actions.DeleteHandler = (_, _) => completion.Task;
        var context = new QueuedContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<DeleteSummary> operation;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            operation = viewModel.DeleteSelectedAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        string initial = viewModel.DeleteProgressText;
        await Task.Run(() => progress!.Report(new DeleteProgress(1, 1, "a.txt", 100)), TestContext.Current.CancellationToken);
        Assert.Equal(initial, viewModel.DeleteProgressText);
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            if (replaceSession)
            {
                store.SetResult(NewResult(NewGroup(2, "two", "c.txt", "d.txt")));
            }
            context.Drain();
            Assert.Equal(replaceSession ? string.Empty : "1 of 1 files processed", viewModel.DeleteProgressText);
            completion.SetResult(new DeleteSummary(0, 0, [], []));
            context.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await operation;
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));

        public void Drain()
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }

    [Fact]
    public void NewResults_SelectAllButNewestAndUndoRestoresEmptySelection()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);

        store.SetResult(NewResult(NewGroup(1, "photos", "oldest.png", "newer.png", "newest.png")));

        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        Assert.Equal(2, viewModel.SelectedFileCount);
        Assert.True(group.Files[0].IsSelected);
        Assert.True(group.Files[1].IsSelected);
        Assert.False(group.Files[2].IsSelected);
        Assert.True(viewModel.CanDelete);
        Assert.True(viewModel.UndoSelectionCommand.CanExecute(null));

        viewModel.UndoSelectionCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedFileCount);
        Assert.All(group.Files, static file => Assert.False(file.IsSelected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadDetachesSelectionHandlersFromEveryOldRow(bool emptyReplacement)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewViewModel(store, actions);
        var oldRows = new List<DuplicateFileViewModel>();
        for (int index = 0; index < 3; index++)
        {
            store.SetResult(NewResult(NewGroup((ulong)index, "old", "a.bin", "b.bin", "c.bin")));
            oldRows.Add(viewModel.Groups[0].Files[0]);
            viewModel.ExcludeFileCommand.Execute(viewModel.Groups[0].Files[0]);
            oldRows.Add(viewModel.Groups[0].Files[0]);
        }
        store.SetResult(emptyReplacement ? NewResult() : NewResult(NewGroup(4, "current", "c.bin", "d.bin")));
        int selectionNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ResultsViewModel.SelectedFileCount))
            {
                selectionNotifications++;
            }
        };

        foreach (DuplicateFileViewModel row in oldRows)
        {
            row.IsSelected = false;
        }

        Assert.Equal(0, selectionNotifications);
        Assert.Equal("Keep newest", viewModel.SelectionRuleText);
        Assert.Equal(emptyReplacement ? 0 : 1, viewModel.SelectedFileCount);
        Assert.Equal(0, actions.DeleteCallCount);
        Assert.Equal(0, actions.MoveCallCount);
    }

    [Fact]
    public void BulkSelectionChanges_RefreshTotalsOnceAndManualChangeStillRefreshes()
    {
        const int fileCount = 32;
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        var group = new DuplicateGroup
        {
            ContentHash = 1,
            SizeBytes = 100,
            Files = Enumerable.Range(0, fileCount)
                .Select(index => NewFile(
                    "bulk",
                    $"copy-{index:D2}.bin",
                    100,
                    DateTime.UtcNow.AddMinutes(index)))
                .ToArray(),
        };
        store.SetResult(NewResult(group));
        int selectedFileCountNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ResultsViewModel.SelectedFileCount))
            {
                selectedFileCountNotifications++;
            }
        };

        viewModel.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedFileCount);
        Assert.Equal(1, selectedFileCountNotifications);

        selectedFileCountNotifications = 0;
        viewModel.UndoSelectionCommand.Execute(null);

        Assert.Equal(fileCount - 1, viewModel.SelectedFileCount);
        Assert.Equal(1, selectedFileCountNotifications);

        selectedFileCountNotifications = 0;
        DuplicateFileViewModel selected = viewModel.Groups[0].Files.First(static file => file.IsSelected);
        selected.IsSelected = false;

        Assert.Equal(fileCount - 2, viewModel.SelectedFileCount);
        Assert.Equal(1, selectedFileCountNotifications);
    }

    [Fact]
    public void KeepNewestRule_UsesShortestPathAsDeterministicTieBreaker()
    {
        DateTime modifiedUtc = DateTime.UtcNow;
        var group = new DuplicateGroupViewModel(new DuplicateGroup
        {
            ContentHash = 1,
            SizeBytes = 100,
            Files =
            [
                NewFile("a-very-long-folder-name", "copy.txt", 100, modifiedUtc),
                NewFile("short", "copy.txt", 100, modifiedUtc),
            ],
        });

        group.ApplyKeepNewest();

        Assert.True(group.Files[0].IsSelected);
        Assert.False(group.Files[1].IsSelected);
    }

    [Fact]
    public void ReentrantSelectionChangingCannotSelectEveryMember()
    {
        var group = new DuplicateGroupViewModel(NewGroup(1, "files", "first.bin", "second.bin"));
        group.Files[0].PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(DuplicateFileViewModel.IsSelected))
            {
                group.Files[1].IsSelected = true;
            }
        };

        group.Files[0].IsSelected = true;

        Assert.Equal(1, group.SelectedCount);
        Assert.True(group.Files[0].IsSelected);
        Assert.False(group.Files[1].IsSelected);
    }

    [Fact]
    public void ReentrantBulkSelectionCannotSelectTheChosenSurvivor()
    {
        var group = new DuplicateGroupViewModel(NewGroup(1, "files", "old.bin", "middle.bin", "new.bin"));
        group.ApplyKeepNewest();
        DuplicateFileViewModel survivor = group.Files[2];
        group.Files[0].PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DuplicateFileViewModel.IsSelected) && group.Files[0].IsSelected)
            {
                survivor.IsSelected = true;
            }
        };

        group.ApplyKeepNewest();

        Assert.False(survivor.IsSelected);
        Assert.Equal(2, group.SelectedCount);
    }

    [Fact]
    public void UndoSelectionNeverTemporarilySelectsEveryMember()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup(1, "files", "old.bin", "middle.bin", "new.bin")));
        DuplicateGroupViewModel group = viewModel.Groups[0];
        viewModel.AutoSelectKeepOldestCommand.Execute(null);
        int maximumSelected = 0;
        foreach (DuplicateFileViewModel file in group.Files)
        {
            file.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DuplicateFileViewModel.IsSelected))
                {
                    maximumSelected = Math.Max(maximumSelected, group.SelectedCount);
                }
            };
        }

        viewModel.UndoSelectionCommand.Execute(null);

        Assert.True(maximumSelected < group.Files.Count);
        Assert.False(group.Files[2].IsSelected);
    }

    [Fact]
    public void UndoAfterExcludingSnapshotSurvivorKeepsOneCurrentMember()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup(1, "files", "old.bin", "middle.bin", "new.bin")));
        DuplicateGroupViewModel group = viewModel.Groups[0];
        viewModel.AutoSelectKeepOldestCommand.Execute(null);
        viewModel.ExcludeFileCommand.Execute(group.Files[2]);

        viewModel.UndoSelectionCommand.Execute(null);

        Assert.Equal(1, group.SelectedCount);
        Assert.False(group.Files[1].IsSelected);
        Assert.True(viewModel.CanDelete);
        Assert.False(viewModel.CanUndoSelection);
    }

    [Fact]
    public void DeletionSelectionEnablement_PreservesOneSurvivorAndTracksGroupState()
    {
        var group = new DuplicateGroupViewModel(
            NewGroup(1, "files", "oldest.bin", "newer.bin", "newest.bin"));
        Dictionary<DuplicateFileViewModel, int> toggleNotifications = group.Files
            .ToDictionary(static file => file, static _ => 0);
        foreach (DuplicateFileViewModel file in group.Files)
        {
            file.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == "CanToggleDeletionSelection")
                {
                    toggleNotifications[file]++;
                }
            };
        }

        Assert.All(group.Files, static file => Assert.True(file.CanToggleDeletionSelection));

        group.ApplyKeepNewest();

        Assert.All(group.Files, file => Assert.True(toggleNotifications[file] > 0));
        ResetToggleNotifications();
        Assert.All(
            group.Files.Where(static file => file.IsSelected),
            static file => Assert.True(file.CanToggleDeletionSelection));
        DuplicateFileViewModel survivor = Assert.Single(
            group.Files,
            static file => !file.IsSelected);
        Assert.False(survivor.CanToggleDeletionSelection);

        DuplicateFileViewModel cleared = group.Files.First(static file => file.IsSelected);
        cleared.IsSelected = false;

        Assert.All(group.Files, file => Assert.True(toggleNotifications[file] > 0));
        ResetToggleNotifications();
        DuplicateFileViewModel[] keptFiles = group.Files
            .Where(static file => !file.IsSelected)
            .ToArray();
        Assert.Equal(2, keptFiles.Length);
        Assert.All(keptFiles, static file => Assert.True(file.CanToggleDeletionSelection));

        group.SetCanMutateSelection(false);

        Assert.All(group.Files, file => Assert.True(toggleNotifications[file] > 0));
        Assert.All(group.Files, static file => Assert.False(file.CanToggleDeletionSelection));

        void ResetToggleNotifications()
        {
            foreach (DuplicateFileViewModel file in group.Files)
            {
                toggleNotifications[file] = 0;
            }
        }
    }

    [Fact]
    public void SearchText_PreservesSelectionInMatchingGroups()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(
            NewGroup(1, "a", "keep.txt", "copy.txt"),
            NewGroup(2, "b", "other.txt", "other-copy.txt")));
        viewModel.ClearSelectionCommand.Execute(null);

        DuplicateFileViewModel selected = viewModel.Groups[0].Files[1];
        selected.IsSelected = true;

        viewModel.SearchText = "copy.txt";

        Assert.Equal(1, viewModel.SelectedFileCount);
        Assert.Contains(viewModel.SelectedFiles, file => file.FullPath == selected.FullPath);
    }

    [Fact]
    public void SearchText_KeepsHiddenSelectionsInTotals()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(
            NewGroup(1, "visible", "visible-a.txt", "visible-b.txt"),
            NewGroup(2, "hidden", "hidden-a.txt", "hidden-b.txt")));
        viewModel.ClearSelectionCommand.Execute(null);

        DuplicateFileViewModel selected = viewModel.Groups[1].Files[1];
        selected.IsSelected = true;

        viewModel.SearchText = "visible";

        Assert.Single(viewModel.Groups);
        Assert.Equal(1, viewModel.SelectedFileCount);
        Assert.Contains(viewModel.SelectedFiles, file => file.FullPath == selected.FullPath);
    }

    [Fact]
    public void Sort_PreservesSelection()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(
            NewGroup(1, "small", "a.txt", "a-copy.txt", sizeBytes: 10),
            NewGroup(2, "large", "b.txt", "b-copy.txt", sizeBytes: 20)));
        viewModel.ClearSelectionCommand.Execute(null);

        string selectedPath = viewModel.Groups[0].Files[1].FullPath;
        viewModel.Groups[0].Files[1].IsSelected = true;

        viewModel.SelectedSortIndex = 1;

        Assert.Equal(1, viewModel.SelectedFileCount);
        Assert.Contains(viewModel.SelectedFiles, file => file.FullPath == selectedPath);
    }

    [Fact]
    public async Task DeleteSelectedAsync_RemovesSuccessfulFilesAndKeepsFailuresSelected()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(
            NewGroup(1, "one", "one-a.txt", "one-b.txt"),
            NewGroup(2, "two", "two-a.txt", "two-b.txt")));
        viewModel.ClearSelectionCommand.Execute(null);

        DuplicateFileViewModel successful = viewModel.Groups[0].Files[1];
        DuplicateFileViewModel failed = viewModel.Groups[1].Files[1];
        successful.IsSelected = true;
        failed.IsSelected = true;
        fileActions.NextSummary = new DeleteSummary(
            DeletedCount: 1,
            DeletedBytes: successful.SizeBytes,
            Failures: [new FileActionFailure(failed.FullPath, "Access denied")]);

        DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Same(fileActions.NextSummary, summary);
        Assert.DoesNotContain(viewModel.Groups.SelectMany(group => group.Files), file => file.FullPath == successful.FullPath);
        Assert.Contains(viewModel.Groups.SelectMany(group => group.Files), file => file.FullPath == failed.FullPath && file.IsSelected);
        Assert.Equal(1, viewModel.SelectedFileCount);
    }

    [Fact]
    public async Task DeleteCancellation_ReconcilesSuccessfulExactPathsBeforeRethrowing()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(
            NewGroup(1, "one", "one-keep.txt", "one-delete.txt"),
            NewGroup(2, "two", "two-keep.txt", "two-failed.txt", "two-unattempted.txt")));
        viewModel.ClearSelectionCommand.Execute(null);
        IReadOnlyList<DuplicateFileViewModel> files = viewModel.Groups.SelectMany(group => group.Files).ToArray();
        DuplicateFileViewModel successful = files.Single(file => file.FileName == "one-delete.txt");
        DuplicateFileViewModel failed = files.Single(file => file.FileName == "two-failed.txt");
        DuplicateFileViewModel unattempted = files.Single(file => file.FileName == "two-unattempted.txt");
        successful.IsSelected = true;
        failed.IsSelected = true;
        unattempted.IsSelected = true;
        fileActions.NextDeleteCancellation = new DeleteOperationCanceledException(
            new DeleteSummary(
                1,
                successful.SizeBytes,
                [new FileActionFailure(failed.FullPath, "Access denied")],
                [successful.FullPath]),
            new CancellationToken(canceled: true));

        await Assert.ThrowsAsync<DeleteOperationCanceledException>(() =>
            viewModel.DeleteSelectedAsync(CancellationToken.None));

        Assert.DoesNotContain(
            viewModel.Groups.SelectMany(group => group.Files),
            file => file.FullPath == successful.FullPath);
        Assert.Contains(
            viewModel.Groups.SelectMany(group => group.Files),
            file => file.FullPath == failed.FullPath && file.IsSelected);
        Assert.Contains(
            viewModel.Groups.SelectMany(group => group.Files),
            file => file.FullPath == unattempted.FullPath && file.IsSelected);
        Assert.Equal(2, viewModel.SelectedFileCount);
        Assert.Contains("cancelled", viewModel.DeleteStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsDeleting);
    }

    [Fact]
    public async Task DeleteFileAsync_DeletesOnlyRequestedUnselectedFile()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(NewGroup(1, "one", "older.txt", "newest.txt")));
        DuplicateFileViewModel target = viewModel.Groups[0].Files[1];
        DuplicateFileViewModel survivor = viewModel.Groups[0].Files[0];
        IReadOnlyList<FileActionTarget>? requestedTargets = null;
        fileActions.NextSummary = new DeleteSummary(1, target.SizeBytes, []);
        fileActions.OnDelete = (targets, _) => requestedTargets = targets;

        DeleteSummary summary = await viewModel.DeleteFileAsync(target, CancellationToken.None);

        Assert.False(target.IsSelected);
        Assert.Same(fileActions.NextSummary, summary);
        Assert.NotNull(requestedTargets);
        Assert.Collection(
            requestedTargets,
            requested =>
            {
                Assert.Equal(target.FullPath, requested.FullPath);
                Assert.Equal(target.SizeBytes, requested.SizeBytes);
                Assert.Equal(FileActionTargetKind.File, requested.Kind);
                Assert.Equal(target.File.ModifiedUtc, requested.ExpectedModifiedUtc);
                Assert.Equal(new ExactFileConstraint(survivor.FullPath, survivor.SizeBytes, survivor.File.ModifiedUtc),
                    Assert.Single(requested.ExpectedExactSurvivors!));
            });
        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public async Task DeleteFileAsync_KeepsFileWhenDeletionFails()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(NewGroup(1, "one", "older.txt", "newest.txt")));
        DuplicateFileViewModel target = viewModel.Groups[0].Files[1];
        fileActions.NextSummary = new DeleteSummary(
            0,
            0,
            [new FileActionFailure(target.FullPath, "Access denied")]);

        await viewModel.DeleteFileAsync(target, CancellationToken.None);

        Assert.Contains(viewModel.Groups.SelectMany(group => group.Files), file => file.FullPath == target.FullPath);
        Assert.Contains(target.FullPath, viewModel.DeleteFailureDetailsText);
        Assert.Contains("Access denied", viewModel.DeleteFailureDetailsText);
    }

    [Fact]
    public void UndoSelectionCommand_RestoresSelectionBeforeAutoSelect()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup(1, "a", "old.txt", "new.txt")));
        viewModel.ClearSelectionCommand.Execute(null);

        DuplicateFileViewModel oldFile = viewModel.Groups[0].Files[0];
        DuplicateFileViewModel newFile = viewModel.Groups[0].Files[1];
        newFile.IsSelected = true;

        viewModel.AutoSelectKeepNewestCommand.Execute(null);

        Assert.Contains(viewModel.SelectedFiles, file => file.FullPath == oldFile.FullPath);
        Assert.DoesNotContain(viewModel.SelectedFiles, file => file.FullPath == newFile.FullPath);
        Assert.True(viewModel.UndoSelectionCommand.CanExecute(null));

        viewModel.UndoSelectionCommand.Execute(null);

        Assert.DoesNotContain(viewModel.SelectedFiles, file => file.FullPath == oldFile.FullPath);
        Assert.Contains(viewModel.SelectedFiles, file => file.FullPath == newFile.FullPath);
    }

    [Fact]
    public void ExcludeFileCommand_RemovesFileWithoutDeletingAndDropsOneFileGroups()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(
            NewGroup(1, "two", "two-a.txt", "two-b.txt"),
            NewGroup(2, "three", "three-a.txt", "three-b.txt", "three-c.txt")));
        viewModel.ClearSelectionCommand.Execute(null);

        DuplicateFileViewModel twoFile = viewModel.Groups[0].Files[0];
        DuplicateFileViewModel threeFile = viewModel.Groups[1].Files[0];

        viewModel.ExcludeFileCommand.Execute(twoFile);
        viewModel.ExcludeFileCommand.Execute(threeFile);

        Assert.DoesNotContain(viewModel.Groups, group => group.Files.Any(file => file.FullPath == twoFile.FullPath));
        DuplicateGroupViewModel remainingGroup = Assert.Single(viewModel.Groups);
        Assert.Equal(2, remainingGroup.Files.Count);
        Assert.DoesNotContain(remainingGroup.Files, file => file.FullPath == threeFile.FullPath);
        Assert.Equal(0, viewModel.SelectedFileCount);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void ExclusionWithSearchAndPreviewPreservesCurrentSurvivorWithoutFileActions(int memberCount, bool excludeKeeper)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewViewModel(store, actions);
        store.SetResult(NewResult(NewGroup(1, "files", "old.bin", "middle.bin", memberCount == 3 ? "new.bin" : null)));
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        DuplicateFileViewModel excluded = excludeKeeper ? group.Files[^1] : group.Files[0];
        viewModel.SearchText = "files";
        viewModel.SelectedFile = excluded;
        bool everyMemberSelected = false;
        group.Files.CollectionChanged += (_, _) =>
        {
            everyMemberSelected |= group.Files.Count > 0 && group.SelectedCount == group.Files.Count;
        };

        viewModel.ExcludeFileCommand.Execute(excluded);

        Assert.False(everyMemberSelected);
        Assert.Null(viewModel.SelectedFile);
        Assert.DoesNotContain(group.Files, file => file.FullPath == excluded.FullPath);
        Assert.Equal(memberCount == 3 ? 1 : 0, viewModel.GroupCount);
        Assert.Equal(memberCount == 3 ? 1 : 0, viewModel.SelectedFileCount);
        Assert.Equal(memberCount == 3 ? 100 : 0, viewModel.SelectedBytes);
        if (memberCount == 3)
        {
            Assert.Same(group, Assert.Single(viewModel.Groups));
            Assert.False(group.Files[^1].IsSelected);
            Assert.True(viewModel.CanDelete);
        }
        else
        {
            Assert.Empty(viewModel.Groups);
            Assert.False(viewModel.CanDelete);
        }
        Assert.Equal(0, actions.DeleteCallCount);
        Assert.Equal(0, actions.MoveCallCount);
        Assert.Equal(0, actions.RenameCallCount);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void ExclusionKeepsPreviewOnlyWhileItsUntouchedRowRemainsCanonical(int memberCount, bool excludeKeeper)
    {
        var store = new ResultsStore();
        var actions = new FakeFileActionService();
        var viewModel = NewViewModel(store, actions);
        store.SetResult(NewResult(NewGroup(1, "files", "old.bin", "middle.bin", memberCount == 3 ? "new.bin" : null)));
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        DuplicateFileViewModel excluded = excludeKeeper ? group.Files[^1] : group.Files[0];
        DuplicateFileViewModel previewed = group.Files.First(file => file != excluded);
        viewModel.SelectedFile = previewed;
        viewModel.SearchText = "no matching group";

        viewModel.ExcludeFileCommand.Execute(excluded);

        Assert.Equal(memberCount == 3 ? previewed : null, viewModel.SelectedFile);
        Assert.Equal(memberCount == 3 ? 1 : 0, viewModel.GroupCount);
        Assert.Equal(0, actions.DeleteCallCount);
        Assert.Equal(0, actions.MoveCallCount);
    }

    [Fact]
    public async Task DeleteSelectedAsync_ReportsProgressAndFailureDetails()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(
            NewGroup(1, "one", "one-a.txt", "one-b.txt"),
            NewGroup(2, "two", "two-a.txt", "two-b.txt")));
        viewModel.ClearSelectionCommand.Execute(null);

        viewModel.Groups[0].Files[1].IsSelected = true;
        DuplicateFileViewModel failed = viewModel.Groups[1].Files[1];
        failed.IsSelected = true;
        fileActions.NextSummary = new DeleteSummary(
            DeletedCount: 1,
            DeletedBytes: viewModel.Groups[0].Files[1].SizeBytes,
            Failures: [new FileActionFailure(failed.FullPath, "Access denied")]);
        fileActions.OnDelete = (targets, progress) =>
        {
            progress?.Report(new DeleteProgress(1, targets.Count, targets[0].FullPath, targets[0].SizeBytes));
            progress?.Report(new DeleteProgress(targets.Count, targets.Count, targets[^1].FullPath, targets.Sum(target => target.SizeBytes)));
        };

        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("2 of 2 files processed", viewModel.DeleteProgressText);
        Assert.Equal(100, viewModel.DeleteProgressValue);
        Assert.Contains(failed.FullPath, viewModel.DeleteFailureDetailsText);
        Assert.Contains("Access denied", viewModel.DeleteFailureDetailsText);
    }

    [Fact]
    public void EmptyResultTextDistinguishesAnEmptyScanFromExcludedDuplicateGroups()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult());
        Assert.Equal("No duplicates found", viewModel.EmptyResultsTitle);
        Assert.Equal("No byte-identical duplicate groups were found in this scan.", viewModel.EmptyResultsDescription);

        store.SetResult(NewResult(NewGroup(1, "files", "a.bin", "b.bin")));
        viewModel.ExcludeFileCommand.Execute(viewModel.Groups[0].Files[0]);

        Assert.Equal(Visibility.Visible, viewModel.NoDuplicatesVisibility);
        Assert.Equal("No duplicate groups remain", viewModel.EmptyResultsTitle);
        Assert.Equal("All groups have been removed from these results.", viewModel.EmptyResultsDescription);
        Assert.True(viewModel.CanStartNewScan);
    }

    [Fact]
    public void ResultsExposeSkippedFilesSummaryAndDetails()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);

        store.SetResult(NewResult(
            [new SkippedPath { Path = "C:\\scan\\locked.txt", Reason = "Access denied" },
             new SkippedPath { Path = "C:\\scan\\changed.txt", Reason = "File changed during scan" }],
            NewGroup(1, "one", "one-a.txt", "one-b.txt")));

        Assert.Equal(Visibility.Visible, viewModel.SkippedFilesVisibility);
        Assert.Equal("2 files were skipped", viewModel.SkippedFilesSummaryText);
        Assert.Contains("C:\\scan\\locked.txt", viewModel.SkippedFilesDetailsText);
        Assert.Contains("Access denied", viewModel.SkippedFilesDetailsText);
    }

    [Fact]
    public void DuplicateFileMetadataShowsDirectoryDatesAndSizeWithoutDecorativeSeparators()
    {
        var group = new DuplicateGroupViewModel(NewGroup(1, "photos\\archive", "one.png", "two.png"));
        DuplicateFileViewModel file = group.Files[0];

        Assert.Contains("photos", file.DisplayDirectoryPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Modified", file.MetadataText, StringComparison.Ordinal);
        Assert.Contains("Created", file.MetadataText, StringComparison.Ordinal);
        Assert.Contains(file.SizeText, file.MetadataText, StringComparison.Ordinal);
        Assert.DoesNotContain("·", file.MetadataText, StringComparison.Ordinal);
        Assert.DoesNotContain("—", file.MetadataText, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateFileDisplayDirectoryPathPreservesTheCompleteDirectory()
    {
        var group = new DuplicateGroupViewModel(
            NewGroup(
                1,
                "a-very-long-folder-name-with-many-segments\\another-long-folder-name\\nested\\deeper\\deepest",
                "long-file-name.txt",
                "copy.txt"));
        DuplicateFileViewModel file = group.Files[0];

        Assert.Equal(file.DirectoryPath, file.DisplayDirectoryPath);
        Assert.DoesNotContain("...", file.DisplayDirectoryPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TogglePreviewPaneCommandCollapsesAndRestoresPreviewPane()
    {
        var viewModel = NewViewModel(new ResultsStore());

        Assert.Equal(Visibility.Collapsed, viewModel.PreviewPaneVisibility);

        viewModel.TogglePreviewPaneCommand.Execute(null);

        Assert.Equal(Visibility.Visible, viewModel.PreviewPaneVisibility);

        viewModel.TogglePreviewPaneCommand.Execute(null);

        Assert.Equal(Visibility.Collapsed, viewModel.PreviewPaneVisibility);
    }

    [Fact]
    public void SelectingFileOpensPreviewPane()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup(1, "photos", "one.png", "two.png")));

        viewModel.SelectedFile = viewModel.Groups[0].Files[0];

        Assert.Equal(Visibility.Visible, viewModel.PreviewPaneVisibility);
    }

    [Fact]
    public void SelectingResultRow_OpensPreviewWithoutChangingDeleteSelection()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup(1, "photos", "one.png", "two.png")));
        DuplicateFileViewModel file = viewModel.Groups[0].Files[0];
        bool deleteSelectionBefore = file.IsSelected;

        viewModel.SelectedFile = file;

        Assert.True(viewModel.IsPreviewPaneOpen);
        Assert.Same(file, viewModel.SelectedFile);
        Assert.Equal(deleteSelectionBefore, file.IsSelected);
    }

    [Fact]
    public async Task MoveSelectedAsync_MapsTargetsAndRemovesSuccessfulCanonicalFiles()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(NewGroup(1, "one", "keep.txt", "move.txt")));
        viewModel.ClearSelectionCommand.Execute(null);
        DuplicateFileViewModel selected = viewModel.Groups[0].Files[1];
        DuplicateFileViewModel survivor = viewModel.Groups[0].Files[0];
        selected.IsSelected = true;
        IReadOnlyList<FileActionTarget>? requestedTargets = null;
        fileActions.OnMove = (targets, destination, behavior, _) =>
        {
            requestedTargets = targets;
            Assert.Equal(@"C:\destination", destination);
            Assert.Equal(MoveCollisionBehavior.KeepBoth, behavior);
        };
        fileActions.NextMoveSummary = new FileOperationSummary(
            [new FileOperationResult(selected.FullPath, @"C:\destination\move.txt", null)],
            selected.SizeBytes);

        FileOperationSummary summary = await viewModel.MoveSelectedAsync(
            @"C:\destination",
            MoveCollisionBehavior.KeepBoth,
            CancellationToken.None);

        Assert.Same(fileActions.NextMoveSummary, summary);
        FileActionTarget requested = Assert.Single(requestedTargets!);
        Assert.Equal(selected.FullPath, requested.FullPath);
        Assert.Equal(selected.SizeBytes, requested.SizeBytes);
        Assert.Equal(FileActionTargetKind.File, requested.Kind);
        Assert.Equal(selected.File.ModifiedUtc, requested.ExpectedModifiedUtc);
        Assert.Equal(new ExactFileConstraint(survivor.FullPath, survivor.SizeBytes, survivor.File.ModifiedUtc),
            Assert.Single(requested.ExpectedExactSurvivors!));
        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public async Task MoveSelectedAsync_CancellationReconcilesCompletedItemsBeforeRethrowing()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(NewGroup(1, "one", "first.txt", "second.txt", "keep.txt")));
        viewModel.ClearSelectionCommand.Execute(null);
        DuplicateFileViewModel first = viewModel.Groups[0].Files[0];
        DuplicateFileViewModel second = viewModel.Groups[0].Files[1];
        first.IsSelected = true;
        second.IsSelected = true;
        var completedSummary = new FileOperationSummary(
            [new FileOperationResult(first.FullPath, @"C:\destination\first.txt", null)],
            first.SizeBytes);
        fileActions.NextMoveCancellation = new FileOperationCanceledException(
            completedSummary,
            new CancellationToken(canceled: true));

        FileOperationCanceledException exception = await Assert.ThrowsAsync<FileOperationCanceledException>(() =>
            viewModel.MoveSelectedAsync(
                @"C:\destination",
                MoveCollisionBehavior.Skip,
                CancellationToken.None));

        Assert.Same(completedSummary, exception.Summary);
        DuplicateGroupViewModel remainingGroup = Assert.Single(viewModel.Groups);
        Assert.DoesNotContain(remainingGroup.Files, file => file.FullPath == first.FullPath);
        Assert.Contains(remainingGroup.Files, file => file.FullPath == second.FullPath);
        Assert.Contains("cancelled", viewModel.DeleteStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsDeleting);
    }

    [Fact]
    public async Task ExportAsync_UsesCanonicalExactResultsAndStoredUtcSessionMetadata()
    {
        var store = new ResultsStore();
        var exporter = new FakeResultExportService();
        var viewModel = NewViewModel(store, exporter: exporter);
        DateTimeOffset completedAtUtc = new(2026, 8, 7, 15, 30, 0, TimeSpan.Zero);
        store.SetResult(
            NewResult(
                NewGroup(0x12AB, "visible", "a.txt", "a-copy.txt"),
                NewGroup(0x34CD, "hidden", "b.txt", "b-copy.txt")),
            new AnalysisScope
            {
                IncludedFolders = [@"C:\scan"],
                ExcludedPaths = [@"C:\scan\excluded"],
                IncludeSubfolders = false,
            },
            completedAtUtc);
        viewModel.SearchText = "visible";

        await viewModel.ExportAsync(ResultExportFormat.Json, @"C:\exports\results.json", CancellationToken.None);

        ResultExportSnapshot snapshot = Assert.IsType<ResultExportSnapshot>(exporter.Snapshot);
        Assert.Equal(ToolKind.DuplicateFiles, snapshot.Tool);
        Assert.Equal(completedAtUtc, snapshot.GeneratedAtUtc);
        Assert.Contains("1 folder", snapshot.ScopeSummary, StringComparison.Ordinal);
        Assert.Contains("1 excluded", snapshot.ScopeSummary, StringComparison.Ordinal);
        Assert.Equal(4, snapshot.Items.Count);
        Assert.Contains(snapshot.Items, item => item.GroupId == "00000000000012AB");
        Assert.Contains(snapshot.Items, item => item.FullPath.EndsWith(@"hidden\b-copy.txt", StringComparison.Ordinal));
        Assert.Equal(ResultExportFormat.Json, exporter.Format);
        Assert.Equal(@"C:\exports\results.json", exporter.DestinationPath);
        Assert.False(exporter.OverwriteExisting);

        System.Reflection.MethodInfo? pickerExport = typeof(ResultsViewModel).GetMethod(
            nameof(ResultsViewModel.ExportAsync),
            [typeof(ResultExportFormat), typeof(string), typeof(CancellationToken), typeof(bool)]);
        Assert.NotNull(pickerExport);
        await Assert.IsAssignableFrom<Task>(pickerExport.Invoke(
            viewModel,
            [ResultExportFormat.Csv, @"C:\exports\picked.csv", CancellationToken.None, true]));
        Assert.True(exporter.OverwriteExisting);
    }

    [Fact]
    public async Task AnalysisDelete_RevalidatesEmptyFileBeforeMappingTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string unchangedPath = Path.Combine(root, "empty.txt");
        string changedPath = Path.Combine(root, "changed.txt");
        await File.WriteAllBytesAsync(unchangedPath, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(changedPath, [1], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new AnalysisResult
                {
                    Findings =
                    [
                        NewPathFinding(unchangedPath, 0),
                        NewPathFinding(changedPath, 0),
                    ],
                    Groups = [],
                    SkippedPaths = [],
                    Elapsed = TimeSpan.Zero,
                });
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            Assert.True(viewModel.CanActOnSelection);

            IReadOnlyList<FileActionTarget>? requestedTargets = null;
            fileActions.OnDelete = (targets, _) => requestedTargets = targets;
            fileActions.NextSummary = new DeleteSummary(1, 0, []);

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(
                [new FileActionTarget(unchangedPath, 0, FileActionTargetKind.File)],
                requestedTargets);
            FileActionFailure failure = Assert.Single(summary.Failures);
            Assert.Equal(changedPath, failure.Path);
            Assert.Equal("File changed since scan.", failure.Reason);
            Assert.DoesNotContain(viewModel.Findings, finding => finding.FullPath == unchangedPath);
            Assert.Contains(viewModel.Findings, finding => finding.FullPath == changedPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ToolKind.TemporaryFiles)]
    public async Task AnalysisMutations_UnsupportedToolsFailClosedWithoutCallingFileActions(ToolKind tool)
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "finding.tmp");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                tool,
                new AnalysisScope { IncludedFolders = [root] },
                new AnalysisResult
                {
                    Findings = [NewPathFinding(path, 1)],
                    Groups = [],
                    SkippedPaths = [],
                    Elapsed = TimeSpan.Zero,
                });
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            Assert.False(viewModel.CanActOnSelection);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.DeleteSelectedAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.MoveSelectedAsync(
                    root,
                    MoveCollisionBehavior.Skip,
                    CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.RenameFindingAsync(finding, "renamed.tmp", CancellationToken.None));
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(0, fileActions.MoveCallCount);
            Assert.Equal(0, fileActions.RenameCallCount);
            Assert.Contains(viewModel.Findings, candidate => candidate.FullPath == path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalysisMutations_SimilarityItemsRequireConfiguredRevalidationBeforeFileActions()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string referencePath = Path.Combine(root, "reference.jpg");
        string candidatePath = Path.Combine(root, "candidate.jpg");
        await File.WriteAllBytesAsync(referencePath, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(candidatePath, [2], TestContext.Current.CancellationToken);

        try
        {
            SimilarityItem reference = NewSimilarityItem(referencePath, 100);
            SimilarityItem candidate = NewSimilarityItem(candidatePath, 90);
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.SimilarImages,
                new AnalysisScope { IncludedFolders = [root] },
                new AnalysisResult
                {
                    Findings = [],
                    Groups =
                    [
                        new SimilarityGroup
                        {
                            Id = "group",
                            ReferenceItem = reference,
                            Items = [reference, candidate],
                        },
                    ],
                    SkippedPaths = [],
                    Elapsed = TimeSpan.Zero,
                });
            viewModel.Groups[0].Items.Single(item => !item.IsReference).IsSelected = true;

            Assert.True(viewModel.CanActOnSelection);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.DeleteSelectedAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.MoveSelectedAsync(
                    root,
                    MoveCollisionBehavior.Skip,
                    CancellationToken.None));
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(0, fileActions.MoveCallCount);
            Assert.Single(viewModel.Groups);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalysisMove_CancellationReconcilesCompletedFindingsBeforeRethrowing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string firstPath = Path.Combine(root, "first.txt");
        string secondPath = Path.Combine(root, "second.txt");
        await File.WriteAllBytesAsync(firstPath, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(secondPath, [], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new AnalysisResult
                {
                    Findings =
                    [
                        NewPathFinding(firstPath, 0),
                        NewPathFinding(secondPath, 0),
                    ],
                    Groups = [],
                    SkippedPaths = [],
                    Elapsed = TimeSpan.Zero,
                });
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            var completedSummary = new FileOperationSummary(
                [new FileOperationResult(firstPath, Path.Combine(root, "moved", "first.txt"), null)],
                0);
            fileActions.NextMoveCancellation = new FileOperationCanceledException(
                completedSummary,
                new CancellationToken(canceled: true));

            await Assert.ThrowsAsync<FileOperationCanceledException>(() =>
                viewModel.MoveSelectedAsync(
                    Path.Combine(root, "moved"),
                    MoveCollisionBehavior.Skip,
                    CancellationToken.None));

            Assert.DoesNotContain(viewModel.Findings, finding => finding.FullPath == firstPath);
            Assert.Contains(viewModel.Findings, finding => finding.FullPath == secondPath);
            Assert.Contains("cancelled", viewModel.ActionStatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.IsActionRunning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalysisDelete_ClearsPreviewWhenSuccessfulFindingIsRemoved()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "empty.txt");
        await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, 0, []),
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new AnalysisResult
                {
                    Findings = [NewPathFinding(path, 0)],
                    Groups = [],
                    SkippedPaths = [],
                    Elapsed = TimeSpan.Zero,
                });
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;
            viewModel.SelectedResult = finding;

            await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Empty(viewModel.Findings);
            Assert.Null(viewModel.SelectedResult);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ResultsViewModel NewViewModel(
        ResultsStore store,
        FakeFileActionService? fileActions = null,
        FakeResultExportService? exporter = null)
    {
        return new ResultsViewModel(
            store,
            fileActions ?? new FakeFileActionService(),
            new FakeSettingsService(),
            exporter ?? new FakeResultExportService());
    }

    private static ScanResult NewResult(params DuplicateGroup[] groups)
    {
        return NewResult([], groups);
    }

    private static ScanResult NewResult(IReadOnlyList<SkippedPath> skippedPaths, params DuplicateGroup[] groups)
    {
        return new ScanResult
        {
            Groups = groups,
            TotalFilesScanned = groups.Sum(group => group.Files.Count),
            TotalDuplicateFiles = groups.Sum(group => group.Files.Count - 1),
            TotalReclaimableBytes = groups.Sum(group => group.WastedBytes),
            Elapsed = TimeSpan.FromSeconds(1),
            SkippedPaths = skippedPaths,
        };
    }

    private static DuplicateGroup NewGroup(ulong hash, string folder, string firstFile, string secondFile, string? thirdFile = null, long sizeBytes = 100)
    {
        List<FileEntry> files =
        [
            NewFile(folder, firstFile, sizeBytes, DateTime.UtcNow.AddMinutes(-3)),
            NewFile(folder, secondFile, sizeBytes, DateTime.UtcNow.AddMinutes(-2)),
        ];

        if (thirdFile is not null)
        {
            files.Add(NewFile(folder, thirdFile, sizeBytes, DateTime.UtcNow.AddMinutes(-1)));
        }

        return new DuplicateGroup
        {
            ContentHash = hash,
            SizeBytes = sizeBytes,
            Files = files,
        };
    }

    private static FileEntry NewFile(string folder, string fileName, long sizeBytes, DateTime modifiedUtc)
    {
        string directory = Path.Combine("C:\\scan", folder);
        return new FileEntry
        {
            FullPath = Path.Combine(directory, fileName),
            FileName = fileName,
            DirectoryPath = directory,
            Extension = Path.GetExtension(fileName),
            SizeBytes = sizeBytes,
            CreatedUtc = modifiedUtc.AddDays(-1),
            ModifiedUtc = modifiedUtc,
        };
    }

    private static PathFinding NewPathFinding(string path, long sizeBytes) => new()
    {
        FullPath = path,
        Kind = PathFindingKind.File,
        Reason = "File is empty",
        SizeBytes = sizeBytes,
    };

    private static SimilarityItem NewSimilarityItem(string path, double similarity) => new()
    {
        FullPath = path,
        SizeBytes = 1,
        ModifiedUtc = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
        SimilarityPercent = similarity,
        Evidence = new ImageSimilarityEvidence(0, 100, 100, "JPEG"),
    };
}
