using System.Collections;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Tests;

public sealed class StorageAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.Engine.Tests",
        Guid.NewGuid().ToString("N"));

    public StorageAnalyzerTests()
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
    public async Task LargeFiles_UsesInclusiveThresholdAndSnapshotMetadata()
    {
        DateTime modifiedUtc = new(2026, 8, 7, 12, 30, 0, DateTimeKind.Utc);
        FileInventory inventory = NewInventory(
            files:
            [
                NewFile(@"C:\scan\below.bin", 999),
                NewFile(@"C:\scan\exact.ISO", 1_000, modifiedUtc),
            ],
            skippedPaths: [new SkippedPath { Path = @"C:\scan\locked", Reason = "Access denied" }]);

        AnalysisResult result = await new LargeFileAnalyzer().AnalyzeAsync(
            inventory,
            1_000,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(@"C:\scan\exact.ISO", finding.FullPath);
        Assert.Equal(PathFindingKind.File, finding.Kind);
        Assert.Equal(1_000, finding.SizeBytes);
        Assert.Equal(modifiedUtc, finding.ModifiedUtc);
        Assert.Equal("At least 1,000 bytes", finding.Reason);
        Assert.Null(finding.Suggestion);
        Assert.Equal(".iso", Assert.Single(finding.Metadata).Value);
        Assert.Empty(result.Groups);
        Assert.Same(inventory.SkippedPaths, result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task LargeFiles_SortsByDescendingSizeThenCanonicalPathOrder()
    {
        FileInventory inventory = NewInventory(
            files:
            [
                NewFile(@"C:\scan\z.bin", 200),
                NewFile(@"C:\scan\a.bin", 300),
                NewFile(@"C:\scan\A.bin", 200),
                NewFile(@"C:\scan\a.bin", 200),
            ]);

        AnalysisResult result = await new LargeFileAnalyzer().AnalyzeAsync(
            inventory,
            0,
            CancellationToken.None);

        Assert.Equal(
            [@"C:\scan\a.bin", @"C:\scan\A.bin", @"C:\scan\a.bin", @"C:\scan\z.bin"],
            result.Findings.Select(static finding => finding.FullPath));
        Assert.All(result.Findings, static finding => Assert.Equal("At least 0 bytes", finding.Reason));
    }

    [Fact]
    public async Task LargeFiles_UsesBuilderInventoryForExplicitAndExcludedFiles()
    {
        string includedFolder = Path.Combine(_root, "included");
        Directory.CreateDirectory(includedFolder);
        string included = WriteBytes(Path.Combine(includedFolder, "included.bin"), 12);
        string excluded = WriteBytes(Path.Combine(includedFolder, "excluded.bin"), 12);
        string explicitFile = WriteBytes(Path.Combine(_root, "explicit.bin"), 12);
        FileInventory inventory = new FileInventoryBuilder().Build(
            new AnalysisScope
            {
                IncludedFolders = [includedFolder],
                IncludedFiles = [explicitFile],
                ExcludedPaths = [excluded],
            },
            progress: null,
            CancellationToken.None);

        AnalysisResult result = await new LargeFileAnalyzer().AnalyzeAsync(
            inventory,
            12,
            CancellationToken.None);

        Assert.Equal(
            new[] { explicitFile, included }.Order(StringComparer.OrdinalIgnoreCase),
            result.Findings.Select(static finding => finding.FullPath).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Findings, finding => finding.FullPath == excluded);
    }

    [Fact]
    public async Task LargeFiles_RejectsNegativeThresholdAndCancellation()
    {
        var analyzer = new LargeFileAnalyzer();
        FileInventory inventory = NewInventory(files: [NewFile(@"C:\scan\large.bin", 100)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            analyzer.AnalyzeAsync(inventory, -1, CancellationToken.None));
        using var cancellationSource = new CancellationTokenSource();
        FileInventory cancelBetweenEntries = NewInventory(
            files: new CancelBeforeSecondItemList<InventoryFile>(
                [NewFile(@"C:\scan\first.bin", 100), NewFile(@"C:\scan\second.bin", 100)],
                cancellationSource));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            analyzer.AnalyzeAsync(cancelBetweenEntries, 0, cancellationSource.Token));
    }

    [Fact]
    public async Task LargeFiles_ExcludesReparsePointInventoryEntries()
    {
        FileInventory inventory = NewInventory(
            files: [NewFile(@"C:\scan\linked.bin", 100, attributes: FileAttributes.ReparsePoint)],
            directories: [NewDirectory(@"C:\scan\folder")],
            reparsePointPaths: [@"C:\scan\linked.bin"]);

        AnalysisResult result = await new LargeFileAnalyzer().AnalyzeAsync(
            inventory,
            0,
            CancellationToken.None);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task EmptyFiles_IncludesOnlyZeroLengthFilesWithSnapshotMetadata()
    {
        DateTime modifiedUtc = new(2026, 8, 7, 13, 15, 0, DateTimeKind.Utc);
        FileInventory inventory = NewInventory(
            files:
            [
                NewFile(@"C:\scan\nonempty.txt", 1),
                NewFile(@"C:\scan\empty.TXT", 0, modifiedUtc),
            ],
            skippedPaths: [new SkippedPath { Path = @"C:\scan\locked", Reason = "Access denied" }]);

        AnalysisResult result = await new EmptyFileAnalyzer().AnalyzeAsync(
            inventory,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(@"C:\scan\empty.TXT", finding.FullPath);
        Assert.Equal(PathFindingKind.File, finding.Kind);
        Assert.Equal(0, finding.SizeBytes);
        Assert.Equal(modifiedUtc, finding.ModifiedUtc);
        Assert.Equal("File is empty", finding.Reason);
        Assert.Null(finding.Suggestion);
        Assert.Equal(".txt", Assert.Single(finding.Metadata).Value);
        Assert.Empty(result.Groups);
        Assert.Same(inventory.SkippedPaths, result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task EmptyFiles_SortsByCanonicalPathAndExcludesDirectoriesAndReparsePoints()
    {
        FileInventory inventory = NewInventory(
            files:
            [
                NewFile(@"C:\scan\z.txt", 0),
                NewFile(@"C:\scan\a.txt", 0),
                NewFile(@"C:\scan\A.txt", 0),
                NewFile(@"C:\scan\linked.txt", 0, attributes: FileAttributes.ReparsePoint),
            ],
            directories: [NewDirectory(@"C:\scan\empty-directory")],
            reparsePointPaths: [@"C:\scan\linked.txt"]);

        AnalysisResult result = await new EmptyFileAnalyzer().AnalyzeAsync(
            inventory,
            CancellationToken.None);

        Assert.Equal(
            [@"C:\scan\A.txt", @"C:\scan\a.txt", @"C:\scan\z.txt"],
            result.Findings.Select(static finding => finding.FullPath));
    }

    [Fact]
    public async Task EmptyFiles_HonorsCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        FileInventory inventory = NewInventory(
            files: new CancelBeforeSecondItemList<InventoryFile>(
                [NewFile(@"C:\scan\first.txt", 0), NewFile(@"C:\scan\second.txt", 0)],
                cancellationSource));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new EmptyFileAnalyzer().AnalyzeAsync(inventory, cancellationSource.Token));
    }

    [Fact]
    public async Task EmptyFolders_IncludesOnlyPhysicalLeavesAndExcludesIncludedRoot()
    {
        FileInventory inventory = NewInventory(
            directories:
            [
                NewDirectory(@"C:\scan", depth: 0, physicalChildCount: 1),
                NewDirectory(@"C:\scan\parent", depth: 1, physicalChildCount: 1),
                NewDirectory(@"C:\scan\parent\empty", depth: 2, physicalChildCount: 0),
                NewDirectory(@"C:\scan\inaccessible", depth: 1, physicalChildCount: -1),
            ],
            includedRootPaths: [@"C:\scan"],
            skippedPaths: [new SkippedPath { Path = @"C:\scan\inaccessible", Reason = "Access denied" }]);

        AnalysisResult result = await new EmptyFolderAnalyzer().AnalyzeAsync(
            inventory,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(@"C:\scan\parent\empty", finding.FullPath);
        Assert.Equal(PathFindingKind.Directory, finding.Kind);
        Assert.Equal(0, finding.SizeBytes);
        Assert.Equal("Folder is empty", finding.Reason);
        Assert.Null(finding.Suggestion);
        Assert.Equal("2", Assert.Single(finding.Metadata).Value);
        Assert.Empty(result.Groups);
        Assert.Same(inventory.SkippedPaths, result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task EmptyFolders_DirectoryContainingExcludedFileIsNotEmpty()
    {
        string candidate = Path.Combine(_root, "candidate");
        string excluded = WriteBytes(Path.Combine(candidate, "excluded.txt"), 1);
        FileInventory inventory = new FileInventoryBuilder().Build(
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [excluded],
            },
            progress: null,
            CancellationToken.None);

        AnalysisResult result = await new EmptyFolderAnalyzer().AnalyzeAsync(
            inventory,
            CancellationToken.None);

        Assert.Empty(inventory.Files);
        Assert.Equal(
            1,
            inventory.Directories.Single(directory => directory.FullPath == candidate).PhysicalChildCount);
        Assert.DoesNotContain(result.Findings, finding => finding.FullPath == candidate);
    }

    [Fact]
    public async Task EmptyFolders_DirectoryContainingEmptyChildIsNotEmpty()
    {
        string parent = Path.Combine(_root, "parent");
        string emptyChild = Path.Combine(parent, "empty");
        Directory.CreateDirectory(emptyChild);
        FileInventory inventory = new FileInventoryBuilder().Build(
            new AnalysisScope { IncludedFolders = [_root] },
            progress: null,
            CancellationToken.None);

        AnalysisResult result = await new EmptyFolderAnalyzer().AnalyzeAsync(
            inventory,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(emptyChild, finding.FullPath);
        Assert.DoesNotContain(result.Findings, item => item.FullPath == parent);
    }

    [Fact]
    public async Task EmptyFolders_SortsDeepestFirstAndExcludesLinkDirectories()
    {
        FileInventory inventory = NewInventory(
            directories:
            [
                NewDirectory(@"C:\scan\shallow", depth: 1, physicalChildCount: 0),
                NewDirectory(@"C:\scan\z-deep", depth: 3, physicalChildCount: 0),
                NewDirectory(@"C:\scan\a-deep", depth: 3, physicalChildCount: 0),
                NewDirectory(
                    @"C:\scan\linked",
                    depth: 4,
                    physicalChildCount: 0,
                    attributes: FileAttributes.Directory | FileAttributes.ReparsePoint),
            ],
            reparsePointPaths: [@"C:\scan\linked"]);

        AnalysisResult result = await new EmptyFolderAnalyzer().AnalyzeAsync(
            inventory,
            CancellationToken.None);

        Assert.Equal(
            [@"C:\scan\a-deep", @"C:\scan\z-deep", @"C:\scan\shallow"],
            result.Findings.Select(static finding => finding.FullPath));
    }

    [Fact]
    public async Task EmptyFolders_HonorsCancellationBetweenDirectories()
    {
        using var cancellationSource = new CancellationTokenSource();
        FileInventory inventory = NewInventory(
            directories: new CancelBeforeSecondItemList<InventoryDirectory>(
                [
                    NewDirectory(@"C:\scan\first", physicalChildCount: 0),
                    NewDirectory(@"C:\scan\second", physicalChildCount: 0),
                ],
                cancellationSource));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new EmptyFolderAnalyzer().AnalyzeAsync(inventory, cancellationSource.Token));
    }

    [Theory]
    [InlineData("cache.TMP")]
    [InlineData("cache.temp")]
    [InlineData("cache.partial")]
    [InlineData("cache.part")]
    [InlineData("cache.crdownload")]
    [InlineData("cache.download")]
    [InlineData("cache.dmp")]
    [InlineData("cache.chk")]
    [InlineData("~$document.docx")]
    [InlineData("backup~")]
    public async Task TemporaryFiles_MatchesEveryApprovedNamePatternCaseInsensitively(string fileName)
    {
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        InventoryFile file = NewFile($@"C:\scan\{fileName}", 25, utcNow.AddDays(-7));
        FileInventory inventory = NewInventory(files: [file]);

        AnalysisResult result = await new TemporaryFileAnalyzer().AnalyzeAsync(
            inventory,
            new TemporaryFileOptions(TimeSpan.FromDays(7), utcNow),
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(file.FullPath, finding.FullPath);
        Assert.Equal(PathFindingKind.File, finding.Kind);
        Assert.Equal(25, finding.SizeBytes);
        Assert.Equal(file.ModifiedUtc, finding.ModifiedUtc);
        Assert.Equal("Temporary file at least 7 days old", finding.Reason);
        Assert.Equal(file.Extension, Assert.Single(finding.Metadata).Value);
        Assert.Empty(result.Groups);
        Assert.Same(inventory.SkippedPaths, result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task TemporaryFiles_UsesInclusiveAgeBoundaryAndRejectsFreshOrUnapprovedNames()
    {
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        FileInventory inventory = NewInventory(
            files:
            [
                NewFile(@"C:\scan\exact.tmp", 1, utcNow.AddDays(-7)),
                NewFile(@"C:\scan\fresh.tmp", 1, utcNow.AddDays(-7).AddTicks(1)),
                NewFile(@"C:\scan\backup.bak", 1, utcNow.AddDays(-30)),
                NewFile(@"C:\scan\backup.old", 1, utcNow.AddDays(-30)),
                NewFile(@"C:\scan\ordinary~middle.txt", 1, utcNow.AddDays(-30)),
                NewFile(
                    @"C:\scan\linked.tmp",
                    1,
                    utcNow.AddDays(-30),
                    FileAttributes.ReparsePoint),
            ],
            skippedPaths: [new SkippedPath { Path = @"C:\scan\locked", Reason = "Access denied" }]);

        AnalysisResult result = await new TemporaryFileAnalyzer().AnalyzeAsync(
            inventory,
            new TemporaryFileOptions(TimeSpan.FromDays(7), utcNow),
            CancellationToken.None);

        Assert.Equal(@"C:\scan\exact.tmp", Assert.Single(result.Findings).FullPath);
        Assert.Same(inventory.SkippedPaths, result.SkippedPaths);
    }

    [Fact]
    public async Task TemporaryFiles_SortsByCanonicalPathAndAcceptsZeroAge()
    {
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        FileInventory inventory = NewInventory(
            files:
            [
                NewFile(@"C:\scan\z.tmp", 1, utcNow),
                NewFile(@"C:\scan\a.tmp", 1, utcNow),
                NewFile(@"C:\scan\A.tmp", 1, utcNow),
            ]);

        AnalysisResult result = await new TemporaryFileAnalyzer().AnalyzeAsync(
            inventory,
            new TemporaryFileOptions(TimeSpan.Zero, utcNow),
            CancellationToken.None);

        Assert.Equal(
            [@"C:\scan\A.tmp", @"C:\scan\a.tmp", @"C:\scan\z.tmp"],
            result.Findings.Select(static finding => finding.FullPath));
        Assert.All(
            result.Findings,
            static finding => Assert.Equal("Temporary file at least 0 days old", finding.Reason));
    }

    [Fact]
    public async Task TemporaryFiles_RejectsNegativeAgeAndHonorsCancellation()
    {
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        var analyzer = new TemporaryFileAnalyzer();
        FileInventory inventory = NewInventory(files: [NewFile(@"C:\scan\one.tmp", 1, utcNow)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => analyzer.AnalyzeAsync(
            inventory,
            new TemporaryFileOptions(TimeSpan.FromTicks(-1), utcNow),
            CancellationToken.None));

        using var cancellationSource = new CancellationTokenSource();
        FileInventory cancelBetweenEntries = NewInventory(
            files: new CancelBeforeSecondItemList<InventoryFile>(
                [NewFile(@"C:\scan\first.tmp", 1, utcNow), NewFile(@"C:\scan\second.tmp", 1, utcNow)],
                cancellationSource));
        await Assert.ThrowsAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(
            cancelBetweenEntries,
            new TemporaryFileOptions(TimeSpan.Zero, utcNow),
            cancellationSource.Token));
    }

    [Fact]
    public async Task TemporaryFiles_RejectsAgeThatWouldUnderflowCapturedUtcNow()
    {
        DateTime utcNow = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var analyzer = new TemporaryFileAnalyzer();

        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => analyzer.AnalyzeAsync(
            NewInventory(),
            new TemporaryFileOptions(TimeSpan.FromDays(1_000_000), utcNow),
            CancellationToken.None));
        Assert.Equal("options", exception.ParamName);
    }

    private static FileInventory NewInventory(
        IReadOnlyList<InventoryFile>? files = null,
        IReadOnlyList<InventoryDirectory>? directories = null,
        IReadOnlyList<string>? includedRootPaths = null,
        IReadOnlyList<string>? reparsePointPaths = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new(
            files ?? [],
            directories ?? [],
            includedRootPaths ?? [],
            reparsePointPaths ?? [],
            skippedPaths ?? []);

    private static InventoryFile NewFile(
        string path,
        long sizeBytes,
        DateTime? modifiedUtc = null,
        FileAttributes attributes = FileAttributes.Normal) => new(
            path,
            Path.GetFileName(path),
            Path.GetExtension(path).ToLowerInvariant(),
            Path.GetDirectoryName(path) ?? string.Empty,
            sizeBytes,
            new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
            modifiedUtc ?? new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
            attributes);

    private static InventoryDirectory NewDirectory(
        string path,
        int depth = 0,
        int physicalChildCount = 0,
        FileAttributes attributes = FileAttributes.Directory) => new(
        path,
        Path.GetFileName(path),
        Path.GetDirectoryName(path) ?? string.Empty,
        depth,
        physicalChildCount,
        attributes);

    private static string WriteBytes(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return Path.GetFullPath(path);
    }

    private sealed class CancelBeforeSecondItemList<T>(
        IReadOnlyList<T> items,
        CancellationTokenSource cancellationSource) : IReadOnlyList<T>
    {
        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator() => Enumerate().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<T> Enumerate()
        {
            yield return items[0];
            cancellationSource.Cancel();
            for (int index = 1; index < items.Count; index++)
            {
                yield return items[index];
            }
        }
    }
}
