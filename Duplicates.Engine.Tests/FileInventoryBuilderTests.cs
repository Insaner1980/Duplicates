using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Xunit.Sdk;

namespace Duplicates.Engine.Tests;

public sealed partial class FileInventoryBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Duplicates.Engine.Tests", Guid.NewGuid().ToString("N"));

    public FileInventoryBuilderTests()
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
    public void Build_RecursiveScope_CollectsNestedFilesAndDirectories()
    {
        string topLevel = WriteFile("top.txt", "top");
        string nested = WriteFile("nested/deeper/file.bin", "nested");

        FileInventory inventory = Build(new AnalysisScope { IncludedFolders = [_root] }, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { topLevel, nested }.Order(StringComparer.OrdinalIgnoreCase),
            inventory.Files.Select(static file => file.FullPath).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(inventory.Directories, directory => directory.FullPath == _root && directory.Depth == 0);
        Assert.Contains(inventory.Directories, directory => directory.FullPath == Path.Combine(_root, "nested") && directory.Depth == 1);
        Assert.Contains(inventory.Directories, directory => directory.FullPath == Path.Combine(_root, "nested", "deeper") && directory.Depth == 2);
    }

    [Fact]
    public void Build_NonRecursiveScope_ExcludesNestedFilesAndDirectories()
    {
        string topLevel = WriteFile("top.txt", "top");
        WriteFile("nested/file.bin", "nested");

        FileInventory inventory = Build(new AnalysisScope { IncludedFolders = [_root], IncludeSubfolders = false }, TestContext.Current.CancellationToken);

        InventoryFile file = Assert.Single(inventory.Files);
        Assert.Equal(topLevel, file.FullPath);
        InventoryDirectory directory = Assert.Single(inventory.Directories);
        Assert.Equal(_root, directory.FullPath);
    }

    [Fact]
    public void Build_OverlappingRoots_DeduplicatesCanonicalPathsAndPreservesIncludedRoots()
    {
        string nestedFolder = Path.Combine(_root, "nested");
        string nestedFile = WriteFile("nested/file.bin", "content");

        FileInventory inventory = Build(new AnalysisScope
        {
            IncludedFolders = [_root, nestedFolder.ToUpperInvariant(), _root],
        }, TestContext.Current.CancellationToken);

        InventoryFile file = Assert.Single(inventory.Files);
        Assert.Equal(nestedFile, file.FullPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, inventory.Directories.Count);
        Assert.Equal(2, inventory.IncludedRootPaths.Count);
        Assert.Contains(_root, inventory.IncludedRootPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(nestedFolder, inventory.IncludedRootPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ExplicitFileReachedThroughRoot_IsIncludedOnce()
    {
        string file = WriteFile("nested/file.bin", "content");

        FileInventory inventory = Build(new AnalysisScope
        {
            IncludedFolders = [_root],
            IncludedFiles = [file.ToUpperInvariant()],
        }, TestContext.Current.CancellationToken);

        InventoryFile inventoryFile = Assert.Single(inventory.Files);
        Assert.Equal(file, inventoryFile.FullPath);
    }

    [Fact]
    public void Build_ExactAndAncestorExclusions_RemoveOnlyMatchingPaths()
    {
        string included = WriteFile("included.txt", "keep");
        WriteFile("excluded/exact.txt", "remove");
        WriteFile("excluded/nested/child.txt", "remove");
        string prefixSibling = WriteFile("excluded-more/keep.txt", "keep");
        string exactExcluded = WriteFile("exact.txt", "remove");

        FileInventory inventory = Build(new AnalysisScope
        {
            IncludedFolders = [_root],
            ExcludedPaths = [Path.Combine(_root, "excluded"), exactExcluded],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { included, prefixSibling }.Order(StringComparer.OrdinalIgnoreCase),
            inventory.Files.Select(static file => file.FullPath).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(inventory.Directories, directory => directory.FullPath == Path.Combine(_root, "excluded"));
    }

    [Fact]
    public void Build_MissingExcludedPath_ReportsSkippedPath()
    {
        string missingExcludedPath = Path.Combine(_root, "missing-exclusion");

        FileInventory inventory = Build(new AnalysisScope
        {
            IncludedFolders = [_root],
            ExcludedPaths = [missingExcludedPath],
        }, TestContext.Current.CancellationToken);

        SkippedPath skipped = Assert.Single(inventory.SkippedPaths);
        Assert.Equal(missingExcludedPath, skipped.Path);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Reason));
    }

    [Fact]
    public void Build_IgnoreHiddenAndSystemFiles_ExcludesFlaggedFiles()
    {
        string visible = WriteFile("visible.txt", "visible");
        string hidden = WriteFile("hidden.txt", "hidden");
        string system = WriteFile("system.txt", "system");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);

        FileInventory inventory = Build(new AnalysisScope { IncludedFolders = [_root] }, TestContext.Current.CancellationToken);

        InventoryFile file = Assert.Single(inventory.Files);
        Assert.Equal(visible, file.FullPath);
    }

    [Fact]
    public void Build_PhysicalChildCountIsCapturedBeforeEntryFilters()
    {
        string visible = WriteFile("visible.txt", "visible");
        string hidden = WriteFile("hidden.txt", "hidden");
        string excluded = WriteFile("excluded.txt", "excluded");
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        FileInventory inventory = Build(new AnalysisScope
        {
            IncludedFolders = [_root],
            ExcludedPaths = [excluded],
            IncludeSubfolders = false,
        }, TestContext.Current.CancellationToken);

        InventoryDirectory root = Assert.Single(inventory.Directories);
        Assert.Equal(4, root.PhysicalChildCount);
        InventoryFile file = Assert.Single(inventory.Files);
        Assert.Equal(visible, file.FullPath);
    }

    [Fact]
    public void Build_ReparsePoint_CapturesPathWithoutTraversingTarget()
    {
        string target = Path.Combine(_root, "target");
        string link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        string targetFile = WriteFile("target/file.txt", "target");

        if (!TryCreateDirectorySymbolicLink(link, target))
        {
            throw SkipException.ForSkip("A directory symbolic-link fixture cannot be created on this Windows host.");
        }

        FileInventory inventory = Build(new AnalysisScope { IncludedFolders = [_root] }, TestContext.Current.CancellationToken);

        Assert.Contains(link, inventory.ReparsePointPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(inventory.Files, file => file.FullPath == targetFile);
        Assert.DoesNotContain(inventory.Files, file => file.FullPath == Path.Combine(link, "file.txt"));
    }

    [Fact]
    public void Build_InaccessibleDirectory_ReportsSkippedPathAndContinues()
    {
        string available = WriteFile("available.txt", "available");
        string blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(blocked);
        WriteFile("blocked/hidden.txt", "blocked");

        using SafeFileHandle handle = TryLockDirectory(blocked);
        if (handle.IsInvalid)
        {
            throw SkipException.ForSkip("An exclusive directory-handle fixture cannot be created on this Windows host.");
        }

        FileInventory inventory = Build(new AnalysisScope { IncludedFolders = [_root] }, TestContext.Current.CancellationToken);

        Assert.Contains(inventory.Files, file => file.FullPath == available);
        Assert.Contains(inventory.SkippedPaths, skipped => string.Equals(skipped.Path, blocked, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            -1,
            inventory.Directories.Single(directory =>
                string.Equals(directory.FullPath, blocked, StringComparison.OrdinalIgnoreCase)).PhysicalChildCount);
    }

    [Fact]
    public void Build_CancelledBeforeEnumeration_ThrowsOperationCanceledException()
    {
        WriteFile("file.txt", "content");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => Build(new AnalysisScope { IncludedFolders = [_root] }, cancellationSource.Token));
    }

    [Fact]
    public void Build_ProgressReportsEnumerationAndDone()
    {
        WriteFile("file.txt", "content");
        var reports = new List<AnalysisProgress>();

        FileInventory inventory = FileInventoryBuilder.Build(
            new AnalysisScope { IncludedFolders = [_root] },
            new CapturingProgress<AnalysisProgress>(reports.Add),
            CancellationToken.None);

        Assert.Single(inventory.Files);
        Assert.Equal(AnalysisPhase.Enumerating, reports[0].Phase);
        Assert.Equal(AnalysisPhase.Done, reports[^1].Phase);
        Assert.Equal(1, reports[^1].ItemsDiscovered);
        Assert.Equal(1, reports[^1].ItemsProcessed);
    }

    private static FileInventory Build(AnalysisScope scope, CancellationToken cancellationToken = default)
    {
        return FileInventoryBuilder.Build(scope, progress: null, cancellationToken);
    }

    private string WriteFile(string relativePath, string contents)
    {
        string fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        return Path.GetFullPath(fullPath);
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink creation depends on the current Windows developer-mode privilege.
            return false;
        }
    }

    private static SafeFileHandle TryLockDirectory(string path)
    {
        const uint genericRead = 0x80000000;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;

        return CreateFileW(path, genericRead, 0, IntPtr.Zero, openExisting, fileFlagBackupSemantics, IntPtr.Zero);
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public CapturingProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
