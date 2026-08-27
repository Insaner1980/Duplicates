using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

public sealed class ResultsLinkReplacementTests
{
    [Fact]
    public void SurvivorSelection_IsIndependentFromPreviewAndPersistsThroughSearchAndSort()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup("one", "two", "three")), new AnalysisScope(), DateTimeOffset.UtcNow);
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        group.ClearSelection();
        DuplicateFileViewModel survivor = group.Files[0];

        viewModel.SelectedFile = group.Files[1];
        survivor.IsLinkSurvivor = true;
        viewModel.SearchText = "three";
        viewModel.SelectedSortIndex = 2;
        viewModel.SearchText = string.Empty;

        Assert.Same(group.Files[1], viewModel.SelectedFile);
        Assert.True(survivor.IsLinkSurvivor);
        Assert.Same(survivor, group.LinkSurvivor);
    }

    [Fact]
    public void DeleteSelectionAndCanonicalRemovalClearSurvivor()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup("one", "two", "three")));
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        group.ClearSelection();
        DuplicateFileViewModel survivor = group.Files[0];
        survivor.IsLinkSurvivor = true;

        survivor.IsSelected = true;
        Assert.Null(group.LinkSurvivor);

        survivor.IsSelected = false;
        survivor.IsLinkSurvivor = true;
        viewModel.ExcludeFileCommand.Execute(survivor);
        Assert.Null(group.LinkSurvivor);
    }

    [Fact]
    public void SelectedDeleteRowCannotBecomeLinkSurvivor()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(NewGroup("one", "two")));
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        DuplicateFileViewModel selected = Assert.Single(group.Files, static file => file.IsSelected);

        selected.IsLinkSurvivor = true;

        Assert.False(selected.IsLinkSurvivor);
        Assert.Null(group.LinkSurvivor);
    }

    [Fact]
    public void CreateLinkReplacementSnapshot_GroupsSelectionsAndBuildsUnconditionalFullPathConfirmation()
    {
        var store = new ResultsStore();
        var viewModel = NewViewModel(store);
        store.SetResult(NewResult(
            NewGroup("a", "b"),
            NewGroup("c", "d", "e")));
        foreach (DuplicateGroupViewModel group in viewModel.Groups)
        {
            group.ClearSelection();
            group.Files[0].IsLinkSurvivor = true;
            foreach (DuplicateFileViewModel duplicate in group.Files.Skip(1))
            {
                duplicate.IsSelected = true;
            }
        }

        ExactLinkReplacementSnapshot snapshot = viewModel.CreateLinkReplacementSnapshot(LinkReplacementMode.SymbolicLink);

        Assert.Equal(2, snapshot.Groups.Count);
        Assert.Equal([1, 2], snapshot.Groups.Select(static group => group.Duplicates.Count));
        Assert.All(snapshot.Groups, group => Assert.Contains(group.Survivor.FullPath, snapshot.ConfirmationText, StringComparison.Ordinal));
        Assert.Contains("3 selected", snapshot.ConfirmationText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recycle Bin", snapshot.ConfirmationText, StringComparison.Ordinal);
        Assert.Contains("Developer Mode", snapshot.ConfirmationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_SelectionDriftAfterConfirmationAbortsBeforeService()
    {
        var store = new ResultsStore();
        var links = new FakeFileLinkService();
        var viewModel = NewViewModel(store, links);
        store.SetResult(NewResult(NewGroup("one", "two", "three")));
        PrepareFirstGroup(viewModel);
        ExactLinkReplacementSnapshot snapshot = viewModel.CreateLinkReplacementSnapshot(LinkReplacementMode.HardLink);
        Assert.True(viewModel.IsLinkReplacementSnapshotCurrent(snapshot));

        viewModel.Groups[0].Files[2].IsSelected = false;

        Assert.False(viewModel.IsLinkReplacementSnapshotCurrent(snapshot));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.ReplaceWithLinksAsync(snapshot, CancellationToken.None));
        Assert.Equal(0, links.CallCount);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_SessionSwapImmediatelyBeforeDispatchAborts()
    {
        var store = new ResultsStore();
        var links = new FakeFileLinkService();
        var viewModel = NewViewModel(store, links);
        store.SetResult(NewResult(NewGroup("one", "two")));
        PrepareFirstGroup(viewModel);
        ExactLinkReplacementSnapshot snapshot = viewModel.CreateLinkReplacementSnapshot(LinkReplacementMode.HardLink);
        viewModel.BeforeLinkDispatch = () => store.SetResult(NewResult(NewGroup("other", "copy")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.ReplaceWithLinksAsync(snapshot, CancellationToken.None));

        Assert.Equal(0, links.CallCount);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_SessionSwapDuringServiceDoesNotReconcileNewResults()
    {
        var store = new ResultsStore();
        var links = new FakeFileLinkService();
        var viewModel = NewViewModel(store, links);
        store.SetResult(NewResult(NewGroup("one", "two")));
        PrepareFirstGroup(viewModel);
        ExactLinkReplacementSnapshot snapshot = viewModel.CreateLinkReplacementSnapshot(LinkReplacementMode.HardLink);
        links.Handler = (groups, _, _, _) =>
        {
            store.SetResult(NewResult(NewGroup("new-survivor", "new-copy")));
            LinkReplacementFile acted = groups[0].Duplicates[0];
            return Task.FromResult(new FileOperationSummary(
                [new FileOperationResult(acted.FullPath, acted.FullPath, null)],
                acted.ExpectedLength));
        };

        await viewModel.ReplaceWithLinksAsync(snapshot, CancellationToken.None);

        Assert.Equal(2, Assert.Single(viewModel.Groups).Files.Count);
        Assert.Contains(viewModel.Groups[0].Files, static file => file.FileName == "new-copy.bin");
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_PartialSuccessRemovesOnlyCommittedRowsAndRefreshesCurrentSummaries()
    {
        var store = new ResultsStore();
        var links = new FakeFileLinkService();
        var viewModel = NewViewModel(store, links);
        store.SetResult(NewResult(NewGroup("survivor", "success", "failure", sizeBytes: 10)));
        PrepareFirstGroup(viewModel);
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        DuplicateFileViewModel successful = group.Files[1];
        DuplicateFileViewModel failed = group.Files[2];
        viewModel.SelectedFile = successful;
        links.Handler = (_, _, _, _) => Task.FromResult(new FileOperationSummary(
        [
            new FileOperationResult(successful.FullPath, successful.FullPath, null),
            new FileOperationResult(
                failed.FullPath,
                null,
                new FileActionFailure(failed.FullPath, "Still in use.")),
        ], 10));
        ExactLinkReplacementSnapshot snapshot = viewModel.CreateLinkReplacementSnapshot(LinkReplacementMode.HardLink);

        FileOperationSummary summary = await viewModel.ReplaceWithLinksAsync(snapshot, CancellationToken.None);

        Assert.Equal(2, summary.Results.Count);
        Assert.DoesNotContain(group.Files, file => file.FullPath == successful.FullPath);
        Assert.Contains(group.Files, file => file.FullPath == failed.FullPath && file.IsSelected);
        Assert.Null(viewModel.SelectedFile);
        Assert.Equal("2 identical files, 10 B reclaimable", group.FilesSummaryText);
        Assert.Equal("10 B", group.WastedText);
        Assert.Equal(1, viewModel.TotalDuplicateFiles);
        Assert.Equal(10, viewModel.TotalReclaimableBytes);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_HoldsCoordinatorLeaseAndReleasesOnFailure()
    {
        var store = new ResultsStore();
        var links = new FakeFileLinkService
        {
            Handler = (_, _, _, _) => throw new IOException("link service failed"),
        };
        var coordinator = new AppOperationCoordinator();
        var viewModel = NewViewModel(store, links, coordinator);
        store.SetResult(NewResult(NewGroup("one", "two")));
        PrepareFirstGroup(viewModel);
        ExactLinkReplacementSnapshot snapshot = viewModel.CreateLinkReplacementSnapshot(LinkReplacementMode.HardLink);

        await Assert.ThrowsAsync<IOException>(() =>
            viewModel.ReplaceWithLinksAsync(snapshot, CancellationToken.None));

        Assert.Null(coordinator.ActiveOperation);
        Assert.False(viewModel.IsDeleting);
    }

    [Fact]
    public void CompetingCoordinatorOperationDisablesActionsExportAndNewScan()
    {
        var store = new ResultsStore();
        var coordinator = new AppOperationCoordinator();
        var viewModel = NewViewModel(store, coordinator: coordinator);
        store.SetResult(NewResult(NewGroup("one", "two")));
        PrepareFirstGroup(viewModel);
        Assert.True(viewModel.CanReplaceWithLinks);
        Assert.True(viewModel.CanExport);
        Assert.True(viewModel.CanStartNewScan);
        Assert.True(viewModel.CanMutateSelection);
        Assert.True(viewModel.AutoSelectKeepNewestCommand.CanExecute(null));
        Assert.True(viewModel.ClearSelectionCommand.CanExecute(null));
        Assert.True(viewModel.ExcludeFileCommand.CanExecute(viewModel.Groups[0].Files[0]));
        Assert.True(viewModel.Groups[0].Files[0].CanMutateSelection);
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun),
            static () => { },
            out IAppOperationLease? lease));

        Assert.False(viewModel.CanDelete);
        Assert.False(viewModel.CanMove);
        Assert.False(viewModel.CanReplaceWithLinks);
        Assert.False(viewModel.CanExport);
        Assert.False(viewModel.CanStartNewScan);
        Assert.False(viewModel.CanMutateSelection);
        Assert.False(viewModel.AutoSelectKeepNewestCommand.CanExecute(null));
        Assert.False(viewModel.ClearSelectionCommand.CanExecute(null));
        Assert.False(viewModel.ExcludeFileCommand.CanExecute(viewModel.Groups[0].Files[0]));
        Assert.All(viewModel.Groups[0].Files, file => Assert.False(file.CanMutateSelection));
        Assert.All(viewModel.Groups[0].Files, file => Assert.False(file.CanBeLinkSurvivor));
        DuplicateFileViewModel survivor = viewModel.Groups[0].Files[0];
        DuplicateFileViewModel selectedDuplicate = viewModel.Groups[0].Files[1];
        survivor.IsLinkSurvivor = false;
        selectedDuplicate.IsSelected = false;
        Assert.True(survivor.IsLinkSurvivor);
        Assert.True(selectedDuplicate.IsSelected);
        lease!.Dispose();

        Assert.True(viewModel.CanReplaceWithLinks);
        Assert.True(viewModel.CanMutateSelection);
        Assert.True(viewModel.Groups[0].Files[0].CanMutateSelection);
    }

    private static ResultsViewModel NewViewModel(
        ResultsStore store,
        FakeFileLinkService? links = null,
        IAppOperationCoordinator? coordinator = null) => new(
            store,
            new FakeFileActionService(),
            new FakeSettingsService(),
            new FakeResultExportService(),
            links ?? new FakeFileLinkService(),
            coordinator ?? new AppOperationCoordinator());

    private static void PrepareFirstGroup(ResultsViewModel viewModel)
    {
        DuplicateGroupViewModel group = Assert.Single(viewModel.Groups);
        group.ClearSelection();
        group.Files[0].IsLinkSurvivor = true;
        foreach (DuplicateFileViewModel duplicate in group.Files.Skip(1))
        {
            duplicate.IsSelected = true;
        }
    }

    private static ScanResult NewResult(params DuplicateGroup[] groups) => new()
    {
        Groups = groups,
        TotalFilesScanned = groups.Sum(static group => group.Files.Count),
        TotalDuplicateFiles = groups.Sum(static group => group.Files.Count - 1),
        TotalReclaimableBytes = groups.Sum(static group => group.WastedBytes),
        Elapsed = TimeSpan.Zero,
        SkippedPaths = [],
    };

    private static DuplicateGroup NewGroup(
        string first,
        string second,
        string? third = null,
        long sizeBytes = 1)
    {
        string root = "C:\\Task16Results";
        var files = new List<FileEntry>
        {
            NewFile(root, first, sizeBytes, 1),
            NewFile(root, second, sizeBytes, 2),
        };
        if (third is not null)
        {
            files.Add(NewFile(root, third, sizeBytes, 3));
        }

        return new DuplicateGroup
        {
            ContentHash = 123,
            SizeBytes = sizeBytes,
            Files = files,
        };
    }

    private static FileEntry NewFile(string root, string name, long sizeBytes, int minute) => new()
    {
        FullPath = Path.Combine(root, $"{name}.bin"),
        FileName = $"{name}.bin",
        DirectoryPath = root,
        Extension = ".bin",
        SizeBytes = sizeBytes,
        CreatedUtc = new DateTime(2026, 8, 8, 8, minute, 0, DateTimeKind.Utc),
        ModifiedUtc = new DateTime(2026, 8, 8, 9, minute, 0, DateTimeKind.Utc),
    };
}

internal sealed class FakeFileLinkService : IFileLinkService
{
    public int CallCount { get; private set; }

    public Func<
        IReadOnlyList<LinkReplacementGroup>,
        LinkReplacementMode,
        IProgress<FileOperationProgress>?,
        CancellationToken,
        Task<FileOperationSummary>>? Handler
    { get; set; }

    public Task<FileOperationSummary> ReplaceWithLinksAsync(
        IReadOnlyList<LinkReplacementGroup> groups,
        LinkReplacementMode mode,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return Handler?.Invoke(groups, mode, progress, cancellationToken) ??
            Task.FromResult(new FileOperationSummary(
                groups.SelectMany(static group => group.Duplicates)
                    .Select(static file => new FileOperationResult(file.FullPath, file.FullPath, null))
                    .ToArray(),
                groups.SelectMany(static group => group.Duplicates).Sum(static file => file.ExpectedLength)));
    }
}
