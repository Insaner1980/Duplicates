using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class ResultsViewModelTests
{
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
    public async Task DeleteFileAsync_DeletesOnlyRequestedUnselectedFile()
    {
        var store = new ResultsStore();
        var fileActions = new FakeFileActionService();
        var viewModel = NewViewModel(store, fileActions);
        store.SetResult(NewResult(NewGroup(1, "one", "older.txt", "newest.txt")));
        DuplicateFileViewModel target = viewModel.Groups[0].Files[1];
        IReadOnlyList<DuplicateFileViewModel>? requestedFiles = null;
        fileActions.NextSummary = new DeleteSummary(1, target.SizeBytes, []);
        fileActions.OnDelete = (files, _) => requestedFiles = files;

        DeleteSummary summary = await viewModel.DeleteFileAsync(target, CancellationToken.None);

        Assert.False(target.IsSelected);
        Assert.Same(fileActions.NextSummary, summary);
        Assert.NotNull(requestedFiles);
        Assert.Collection(requestedFiles, file => Assert.Same(target, file));
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
        fileActions.OnDelete = (files, progress) =>
        {
            progress?.Report(new DeleteProgress(1, files.Count, files[0].FullPath, files[0].SizeBytes));
            progress?.Report(new DeleteProgress(files.Count, files.Count, files[^1].FullPath, files.Sum(file => file.SizeBytes)));
        };

        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("2 of 2 files processed", viewModel.DeleteProgressText);
        Assert.Equal(100, viewModel.DeleteProgressValue);
        Assert.Contains(failed.FullPath, viewModel.DeleteFailureDetailsText);
        Assert.Contains("Access denied", viewModel.DeleteFailureDetailsText);
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
    public void DuplicateFileDisplayDirectoryPathShortensLongPathsFromTheMiddle()
    {
        var group = new DuplicateGroupViewModel(NewGroup(1, "a-very-long-folder-name-with-many-segments\\another-long-folder-name\\nested\\deeper\\deepest", "long-file-name.txt", "copy.txt"));
        DuplicateFileViewModel file = group.Files[0];

        Assert.Contains("...", file.DisplayDirectoryPath);
        Assert.EndsWith("deepest", file.DisplayDirectoryPath, StringComparison.Ordinal);
        Assert.True(file.DisplayDirectoryPath.Length < file.DirectoryPath.Length);
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

    private static ResultsViewModel NewViewModel(ResultsStore store, FakeFileActionService? fileActions = null)
    {
        return new ResultsViewModel(store, fileActions ?? new FakeFileActionService(), new FakeSettingsService());
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
}
