using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class ExifFileTransactionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates-ExifTransactions",
        Guid.NewGuid().ToString("N"));

    public ExifFileTransactionsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CopyToNewAsync_CapturesCreationHandleIdentityCopiesChunksFlushesAndClosesHandles()
    {
        string sourcePath = Write("source.jpg", Enumerable.Range(0, 300_000).Select(index => (byte)index).ToArray());
        string destinationPath = Path.Combine(_root, "owned.tmp");
        var transactions = new ExifFileTransactions();
        ExifTrackedFile source = transactions.Capture(sourcePath);
        var progress = new List<double>();

        ExifTrackedFile owned = transactions.CreateOwnedNew(destinationPath);
        FileSystemIdentity creationIdentity = owned.Identity;
        owned = await transactions.CopyAndFlushAsync(
            source,
            owned,
            new InlineProgress(progress.Add),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(destinationPath), owned.Path);
        Assert.Equal(creationIdentity, owned.Identity);
        Assert.Equal(owned.Identity, transactions.Capture(destinationPath).Identity);
        Assert.Equal(await File.ReadAllBytesAsync(sourcePath), await File.ReadAllBytesAsync(destinationPath));
        Assert.NotEmpty(progress);
        Assert.Equal(1, progress[^1]);

        string renamed = destinationPath + ".renamed";
        File.Move(destinationPath, renamed);
        File.Delete(renamed);
    }

    [Fact]
    public async Task CopyToNewAsync_CreateNewCollisionNeverOverwritesExistingEntry()
    {
        string sourcePath = Write("source.jpg", [1, 2, 3]);
        string destinationPath = Write("collision.tmp", [9, 9, 9]);
        var transactions = new ExifFileTransactions();

        Assert.ThrowsAny<IOException>(() =>
        {
            _ = transactions.CreateOwnedNew(destinationPath);
        });

        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task CopyAndFlushAsync_CancellationLeavesKnownOwnedIdentityForCallerCleanup()
    {
        string sourcePath = Write("large.jpg", new byte[2 * 1024 * 1024]);
        string destinationPath = Path.Combine(_root, "cancel.tmp");
        var transactions = new ExifFileTransactions();
        ExifTrackedFile owned = transactions.CreateOwnedNew(destinationPath);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transactions.CopyAndFlushAsync(
            transactions.Capture(sourcePath),
            owned,
            new InlineProgress(value =>
            {
                if (value > 0)
                {
                    cancellation.Cancel();
                }
            }),
            cancellation.Token));

        ExifPathProbe probe = transactions.Probe(destinationPath);
        Assert.Equal(ExifPathState.Present, probe.State);
        Assert.Equal(owned.Identity, probe.Identity);
        transactions.DeleteOwned(owned);
        Assert.Equal(ExifPathState.Missing, transactions.Probe(destinationPath).State);
    }

    [Fact]
    public async Task CopyAndFlushAsync_RejectsForeignDestinationWithoutDeletingIt()
    {
        string sourcePath = Write("copy-source.jpg", [1, 2, 3]);
        string destinationPath = Path.Combine(_root, "copy-owned.tmp");
        var transactions = new ExifFileTransactions();
        ExifTrackedFile owned = transactions.CreateOwnedNew(destinationPath);
        string aside = destinationPath + ".aside";
        File.Move(destinationPath, aside);
        File.WriteAllBytes(destinationPath, [9, 9, 9]);
        File.Delete(aside);

        await Assert.ThrowsAnyAsync<IOException>(() => transactions.CopyAndFlushAsync(
            transactions.Capture(sourcePath),
            owned,
            null,
            CancellationToken.None));

        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public void MoveNoOverwrite_PreservesIdentityAndRejectsDestinationCollision()
    {
        string sourcePath = Write("move.tmp", [1]);
        string destinationPath = Path.Combine(_root, "moved.tmp");
        string collisionPath = Write("collision.tmp", [2]);
        var transactions = new ExifFileTransactions();
        ExifTrackedFile source = transactions.Capture(sourcePath);

        ExifMoveResult move = transactions.MoveNoOverwrite(source, destinationPath);
        ExifTrackedFile moved = move.File;

        Assert.Equal(ExifMoveCommitState.Committed, move.CommitState);
        Assert.Equal(source.Identity, moved.Identity);
        Assert.Equal(source.Identity, transactions.Capture(destinationPath).Identity);
        Assert.Equal(ExifPathState.Missing, transactions.Probe(sourcePath).State);
        ExifMoveException collision = Assert.Throws<ExifMoveException>(() =>
        {
            _ = transactions.MoveNoOverwrite(moved, collisionPath);
        });
        Assert.Equal(ExifMoveCommitState.NotCommitted, collision.CommitState);
        Assert.Equal(moved.Identity, collision.ExpectedIdentity);
        Assert.Equal(moved.Path, collision.SourcePath);
        Assert.Equal(collisionPath, collision.DestinationPath);
        Assert.Equal([2], File.ReadAllBytes(collisionPath));
        Assert.Equal(source.Identity, transactions.Capture(destinationPath).Identity);
    }

    [Fact]
    public void MoveAndDelete_RejectEqualSnapshotForeignOccupants()
    {
        string path = Write("owned.tmp", [1, 2, 3]);
        DateTime modifiedUtc = File.GetLastWriteTimeUtc(path);
        var transactions = new ExifFileTransactions();
        ExifTrackedFile owned = transactions.Capture(path);
        string originalAside = path + ".original";
        File.Move(path, originalAside);
        File.WriteAllBytes(path, [9, 8, 7]);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        File.Delete(originalAside);

        Assert.ThrowsAny<IOException>(() =>
        {
            _ = transactions.MoveNoOverwrite(owned, Path.Combine(_root, "foreign-moved.tmp"));
        });
        Assert.ThrowsAny<IOException>(() =>
        {
            transactions.DeleteOwned(owned);
        });
        Assert.Equal([9, 8, 7], File.ReadAllBytes(path));
    }

    [Fact]
    public void CreateOwnedNew_PostCreatePathValidationFailureRemovesCreatedEntry()
    {
        string actualDirectory = Path.Combine(_root, "actual");
        string linkedDirectory = Path.Combine(_root, "linked");
        Directory.CreateDirectory(actualDirectory);
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, actualDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        string linkedPath = Path.Combine(linkedDirectory, "owned.tmp");
        string actualPath = Path.Combine(actualDirectory, "owned.tmp");
        var transactions = new ExifFileTransactions();

        Exception? failure = Record.Exception(() => transactions.CreateOwnedNew(linkedPath));

        Assert.NotNull(failure);
        Assert.False(File.Exists(actualPath));
        Assert.IsAssignableFrom<IOException>(failure);
    }

    [Fact]
    public void CaptureAndDelete_RejectDirectoriesAndReparsePoints()
    {
        string directory = Path.Combine(_root, "directory.jpg");
        Directory.CreateDirectory(directory);
        var transactions = new ExifFileTransactions();

        Assert.ThrowsAny<IOException>(() =>
        {
            _ = transactions.Capture(directory);
        });

        string target = Write("target.jpg", [1]);
        string link = Path.Combine(_root, "link.jpg");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.ThrowsAny<IOException>(() =>
        {
            _ = transactions.Capture(link);
        });
    }

    [Fact]
    public void EntryExistsCaseInsensitive_IncludesFilesAndDirectories()
    {
        _ = Write("Name.CLEAN.JPG", [1]);
        Directory.CreateDirectory(Path.Combine(_root, "Folder.CLEAN.JPG"));
        var transactions = new ExifFileTransactions();

        Assert.True(transactions.EntryExistsCaseInsensitive(Path.Combine(_root, "name.clean.jpg")));
        Assert.True(transactions.EntryExistsCaseInsensitive(Path.Combine(_root, "folder.clean.jpg")));
        Assert.False(transactions.EntryExistsCaseInsensitive(Path.Combine(_root, "vacant.clean.jpg")));
    }

    [Fact]
    public void GuardOwnedPath_HoldsIdentityAndBlocksRename()
    {
        string path = Write("guarded.tmp", [1, 2, 3]);
        string aside = path + ".aside";
        var transactions = new ExifFileTransactions();
        ExifTrackedFile owned = transactions.Capture(path);

        using IDisposable guard = transactions.GuardOwnedPath(owned);

        Assert.ThrowsAny<IOException>(() => File.Move(path, aside));
        Assert.Equal(owned.Identity, transactions.Capture(path).Identity);
    }

    [Fact]
    public void GuardSourceSnapshot_ValidatesTheFullSnapshotAndBlocksMutation()
    {
        string path = Write("source-guard.jpg", [1, 2, 3]);
        var transactions = new ExifFileTransactions();
        ExifTrackedFile stale = transactions.Capture(path);
        File.AppendAllText(path, "changed");

        Assert.Throws<ExifSourceChangedException>(() => transactions.GuardSourceSnapshot(stale));

        ExifTrackedFile current = transactions.Capture(path);
        using IDisposable guard = transactions.GuardSourceSnapshot(current);
        Assert.ThrowsAny<IOException>(() => File.AppendAllText(path, "blocked"));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".aside"));
    }

    [Fact]
    public void MoveSourceNoOverwrite_RejectsChangedSnapshotBeforeRename()
    {
        string sourcePath = Write("source-rename.jpg", [1, 2, 3]);
        string destinationPath = Path.Combine(_root, "source-rollback.jpg");
        var transactions = new ExifFileTransactions();
        ExifTrackedFile stale = transactions.Capture(sourcePath);
        File.AppendAllText(sourcePath, "changed");

        Assert.Throws<ExifSourceChangedException>(() =>
            transactions.MoveSourceNoOverwrite(stale, destinationPath));

        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(destinationPath));
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class InlineProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }
}
