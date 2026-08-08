using System.Collections;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Tests;

public sealed class BadExtensionAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.Engine.Tests",
        Guid.NewGuid().ToString("N"));

    public BadExtensionAnalyzerTests()
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
    public async Task AnalyzeAsync_CreatesSnapshotFindingAndPreservesMultiDotStem()
    {
        InventoryFile file = WriteInventoryFile("archive.tar.txt", PngBytes);
        FileInventory inventory = NewInventory(
            files: [file],
            skippedPaths: [new SkippedPath { Path = "already-skipped", Reason = "Existing reason" }]);

        AnalysisResult result = await new BadExtensionAnalyzer().AnalyzeAsync(
            inventory,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(file.FullPath, finding.FullPath);
        Assert.Equal(PathFindingKind.File, finding.Kind);
        Assert.Equal("Extension does not match detected file type.", finding.Reason);
        Assert.Equal("archive.tar.png", finding.Suggestion);
        Assert.Equal(file.SizeBytes, finding.SizeBytes);
        Assert.Equal(file.CreatedUtc, finding.CreatedUtc);
        Assert.Equal(file.ModifiedUtc, finding.ModifiedUtc);
        Assert.Equal(".txt", finding.Metadata["CurrentExtension"]);
        Assert.Equal(".png", finding.Metadata["ProperExtension"]);
        Assert.Equal("PNG", finding.Metadata["DetectedType"]);
        Assert.Equal(3, finding.Metadata.Count);
        Assert.Empty(result.Groups);
        Assert.Same(inventory.SkippedPaths, result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesNoneMetadataForExtensionlessFile()
    {
        InventoryFile file = WriteInventoryFile("photo", PngBytes);

        AnalysisResult result = await new BadExtensionAnalyzer().AnalyzeAsync(
            NewInventory(files: [file]),
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal("photo.png", finding.Suggestion);
        Assert.Equal("(none)", finding.Metadata["CurrentExtension"]);
    }

    [Fact]
    public async Task AnalyzeAsync_IgnoresAllowedAndAmbiguousContainerExtensions()
    {
        InventoryFile png = WriteInventoryFile("photo.PNG", PngBytes);
        InventoryFile docx = WriteInventoryFile("report.docx", [0x50, 0x4B, 0x03, 0x04]);
        InventoryFile ambiguousBmff = WriteInventoryFile("clip.bin", Ftyp("isom", "qt  "));
        InventoryFile ambiguousEbml = WriteInventoryFile("movie.bin", EbmlWithoutDocType());

        AnalysisResult result = await new BadExtensionAnalyzer().AnalyzeAsync(
            NewInventory(files: [png, docx, ambiguousBmff, ambiguousEbml]),
            CancellationToken.None);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task AnalyzeAsync_HandlesOnlyRegularFilesAndAppendsReadFailures()
    {
        InventoryFile reparse = WriteInventoryFile(
            "linked.txt",
            PngBytes,
            FileAttributes.ReparsePoint);
        InventoryFile directory = NewInventoryFile(
            Path.Combine(_root, "directory.txt"),
            0,
            FileAttributes.Directory);
        InventoryFile missing = NewInventoryFile(
            Path.Combine(_root, "missing.txt"),
            PngBytes.Length,
            FileAttributes.Normal);
        var existingSkip = new SkippedPath { Path = "first", Reason = "Existing reason" };

        AnalysisResult result = await new BadExtensionAnalyzer().AnalyzeAsync(
            NewInventory(files: [reparse, directory, missing], skippedPaths: [existingSkip]),
            CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.SkippedPaths.Count);
        Assert.Same(existingSkip, result.SkippedPaths[0]);
        Assert.Equal(missing.FullPath, result.SkippedPaths[1].Path);
        Assert.False(string.IsNullOrWhiteSpace(result.SkippedPaths[1].Reason));
    }

    [Fact]
    public async Task AnalyzeAsync_SortsByFullPathIgnoreCaseThenOrdinal()
    {
        InventoryFile lower = WriteInventoryFile("a.txt", PngBytes);
        InventoryFile upper = WriteInventoryFile("A.txt", PngBytes);
        InventoryFile zed = WriteInventoryFile("z.txt", PngBytes);

        AnalysisResult result = await new BadExtensionAnalyzer().AnalyzeAsync(
            NewInventory(files: [zed, lower, upper]),
            CancellationToken.None);

        Assert.Equal(
            [upper.FullPath, lower.FullPath, zed.FullPath],
            result.Findings.Select(static finding => finding.FullPath));
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellationBetweenFiles()
    {
        InventoryFile first = WriteInventoryFile("first.txt", PngBytes);
        InventoryFile second = WriteInventoryFile("second.txt", PngBytes);
        using var cancellationSource = new CancellationTokenSource();
        FileInventory inventory = NewInventory(
            files: new CancelBeforeSecondItemList<InventoryFile>([first, second], cancellationSource));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BadExtensionAnalyzer().AnalyzeAsync(inventory, cancellationSource.Token));
    }

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private InventoryFile WriteInventoryFile(
        string fileName,
        byte[] bytes,
        FileAttributes attributes = FileAttributes.Normal)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, bytes);
        return NewInventoryFile(path, bytes.Length, attributes);
    }

    private static InventoryFile NewInventoryFile(
        string path,
        long sizeBytes,
        FileAttributes attributes)
    {
        var file = new FileInfo(path);
        return new InventoryFile(
            path,
            Path.GetFileName(path),
            Path.GetExtension(path).ToLowerInvariant(),
            Path.GetDirectoryName(path) ?? string.Empty,
            sizeBytes,
            file.Exists ? file.CreationTimeUtc : new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
            file.Exists ? file.LastWriteTimeUtc : new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
            attributes);
    }

    private static FileInventory NewInventory(
        IReadOnlyList<InventoryFile>? files = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new(
            files ?? [],
            [],
            [],
            [],
            skippedPaths ?? []);

    private static byte[] Ftyp(string majorBrand, params string[] compatibleBrands)
    {
        byte[] major = System.Text.Encoding.ASCII.GetBytes(majorBrand);
        byte[] compatible = compatibleBrands.SelectMany(System.Text.Encoding.ASCII.GetBytes).ToArray();
        int size = 16 + compatible.Length;
        return
        [
            (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
            0x66, 0x74, 0x79, 0x70, .. major, 0, 0, 0, 0, .. compatible,
        ];
    }

    private static byte[] EbmlWithoutDocType() =>
        [0x1A, 0x45, 0xDF, 0xA3, 0x82, 0xEC, 0x80];

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
