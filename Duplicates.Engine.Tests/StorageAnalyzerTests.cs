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
            files: new CancelBeforeSecondItemList(
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
            files: new CancelBeforeSecondItemList(
                [NewFile(@"C:\scan\first.txt", 0), NewFile(@"C:\scan\second.txt", 0)],
                cancellationSource));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new EmptyFileAnalyzer().AnalyzeAsync(inventory, cancellationSource.Token));
    }

    private static FileInventory NewInventory(
        IReadOnlyList<InventoryFile>? files = null,
        IReadOnlyList<InventoryDirectory>? directories = null,
        IReadOnlyList<string>? reparsePointPaths = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new(
            files ?? [],
            directories ?? [],
            [],
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

    private static InventoryDirectory NewDirectory(string path) => new(
        path,
        Path.GetFileName(path),
        Path.GetDirectoryName(path) ?? string.Empty,
        0,
        FileAttributes.Directory);

    private static string WriteBytes(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return Path.GetFullPath(path);
    }

    private sealed class CancelBeforeSecondItemList(
        IReadOnlyList<InventoryFile> items,
        CancellationTokenSource cancellationSource) : IReadOnlyList<InventoryFile>
    {
        public int Count => items.Count;

        public InventoryFile this[int index] => items[index];

        public IEnumerator<InventoryFile> GetEnumerator() => Enumerate().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<InventoryFile> Enumerate()
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
