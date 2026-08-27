using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Tests;

public sealed class BrokenFileAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.Engine.Tests",
        Guid.NewGuid().ToString("N"));

    public BrokenFileAnalyzerTests()
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
    public async Task AnalyzeAsync_IgnoresReadableUnknownZeroByteAndNonOrdinaryFiles()
    {
        InventoryFile unknown = WriteInventoryFile("readable.bin", [1, 2, 3]);
        InventoryFile empty = WriteInventoryFile("empty.bin", []);
        InventoryFile reparse = WriteInventoryFile("link.bin", [4], FileAttributes.ReparsePoint);
        InventoryFile directory = NewInventoryFile(
            Path.Combine(_root, "folder.bin"),
            1,
            FileAttributes.Directory);
        var probe = new FakeProbe
        {
            Handler = (_, _, _) => Task.FromResult(new FileProbeResult(FileProbeStatus.Valid, null, null)),
        };

        AnalysisResult result = await new BrokenFileAnalyzer(probe).AnalyzeAsync(
            NewInventory([directory, reparse, empty, unknown]),
            CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Groups);
        Assert.Empty(result.SkippedPaths);
        Assert.Equal([unknown.FullPath], probe.Paths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsHeaderReadFailureWithInventorySnapshot()
    {
        InventoryFile file = WriteInventoryFile("locked.bin", [1, 2, 3]);
        using var locked = new FileStream(file.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var probe = new FakeProbe();

        AnalysisResult result = await new BrokenFileAnalyzer(probe).AnalyzeAsync(
            NewInventory([file]),
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(file.FullPath, finding.FullPath);
        Assert.Equal(PathFindingKind.File, finding.Kind);
        Assert.Equal("Unreadable or malformed file.", finding.Reason);
        Assert.Null(finding.Suggestion);
        Assert.Equal(file.SizeBytes, finding.SizeBytes);
        Assert.Equal(file.CreatedUtc, finding.CreatedUtc);
        Assert.Equal(file.ModifiedUtc, finding.ModifiedUtc);
        Assert.Equal("Header", finding.Metadata["Validator"]);
        Assert.Equal("HeaderReadFailure", finding.Metadata["ErrorType"]);
        Assert.False(finding.Metadata.ContainsKey("DetectedType"));
        Assert.Empty(probe.Paths);
    }

    [Fact]
    public async Task AnalyzeAsync_MapsInvalidAndUnsupportedResultsWithStableMetadataAndReasons()
    {
        InventoryFile image = WriteInventoryFile("image.png", PngBytes);
        InventoryFile media = WriteInventoryFile("track.mp3", "ID3\u0004\0\0\0\0\0\0"u8.ToArray());
        InventoryFile zip = WriteInventoryFile("archive.zip", [0x50, 0x4B, 0x03, 0x04, 1]);
        InventoryFile codec = WriteInventoryFile("codec.mp4", Ftyp("isom"));
        InventoryFile protectedZip = WriteInventoryFile("protected.zip", [0x50, 0x4B, 0x03, 0x04, 2]);
        var probe = new FakeProbe
        {
            Handler = (path, _, _) => Task.FromResult(Path.GetFileName(path) switch
            {
                "image.png" => new FileProbeResult(FileProbeStatus.Invalid, "ImageDecodeFailure", "localized image detail"),
                "track.mp3" => new FileProbeResult(FileProbeStatus.Invalid, "MediaOpenFailure", "localized media detail"),
                "archive.zip" => new FileProbeResult(FileProbeStatus.Invalid, "ZipCentralDirectoryFailure", "localized zip detail"),
                "codec.mp4" => new FileProbeResult(FileProbeStatus.UnsupportedOrProtected, "CodecUnavailable", "localized codec detail"),
                "protected.zip" => new FileProbeResult(FileProbeStatus.UnsupportedOrProtected, "PasswordProtected", "localized password detail"),
                _ => throw new InvalidOperationException(),
            }),
        };

        AnalysisResult result = await new BrokenFileAnalyzer(probe).AnalyzeAsync(
            NewInventory([protectedZip, zip, media, image, codec]),
            CancellationToken.None);

        Assert.Equal(
            [zip.FullPath, codec.FullPath, image.FullPath, protectedZip.FullPath, media.FullPath],
            result.Findings.Select(static finding => finding.FullPath));
        Assert.Equal(
            [
                ("ZIP", "Zip", "ZipCentralDirectoryFailure", "Unreadable or malformed file."),
                ("MP4", "Media", "CodecUnavailable", "Unsupported or protected."),
                ("PNG", "Image", "ImageDecodeFailure", "Unreadable or malformed file."),
                ("ZIP", "Zip", "PasswordProtected", "Unsupported or protected."),
                ("MP3", "Media", "MediaOpenFailure", "Unreadable or malformed file."),
            ],
            result.Findings.Select(static finding => (
                finding.Metadata["DetectedType"],
                finding.Metadata["Validator"],
                finding.Metadata["ErrorType"],
                finding.Reason)));
        Assert.DoesNotContain(result.Findings, finding => finding.Reason.Contains("localized", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsFilesChangedBeforeValidationBySizeOrModifiedTime()
    {
        InventoryFile sizeChanged = WriteInventoryFile("size.bin", [1, 2]);
        InventoryFile timeChanged = WriteInventoryFile("time.bin", [3, 4]);
        File.WriteAllBytes(sizeChanged.FullPath, [1, 2, 5]);
        File.SetLastWriteTimeUtc(timeChanged.FullPath, timeChanged.ModifiedUtc.AddSeconds(5));
        var probe = new FakeProbe();

        AnalysisResult result = await new BrokenFileAnalyzer(probe).AnalyzeAsync(
            NewInventory([timeChanged, sizeChanged]),
            CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Equal(
            [sizeChanged.FullPath, timeChanged.FullPath],
            result.SkippedPaths.Select(static skipped => skipped.Path));
        Assert.All(result.SkippedPaths, static skipped => Assert.Equal("File changed since scan.", skipped.Reason));
        Assert.Empty(probe.Paths);
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsFilesChangedBySizeOrModifiedTimeDuringProbe()
    {
        InventoryFile sizeChanged = WriteInventoryFile("during-size.bin", [1, 2]);
        InventoryFile timeChanged = WriteInventoryFile("during-time.bin", [3, 4]);
        var probe = new FakeProbe
        {
            Handler = (path, _, _) =>
            {
                if (path == sizeChanged.FullPath)
                {
                    File.WriteAllBytes(path, [1, 2, 9]);
                }
                else
                {
                    File.SetLastWriteTimeUtc(path, timeChanged.ModifiedUtc.AddSeconds(5));
                }

                return Task.FromResult(new FileProbeResult(FileProbeStatus.Invalid, "HeaderReadFailure", null));
            },
        };

        AnalysisResult result = await new BrokenFileAnalyzer(probe).AnalyzeAsync(
            NewInventory([timeChanged, sizeChanged]),
            CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Equal(
            [sizeChanged.FullPath, timeChanged.FullPath],
            result.SkippedPaths.Select(static skipped => skipped.Path));
        Assert.All(result.SkippedPaths, static skipped => Assert.Equal("File changed since scan.", skipped.Reason));
    }

    [Fact]
    public async Task AnalyzeAsync_AppendsProviderFailuresAfterInventorySkipsAndContinuesInPathOrder()
    {
        InventoryFile first = WriteInventoryFile("a.bin", [1]);
        InventoryFile second = WriteInventoryFile("b.bin", [2]);
        var existing = new SkippedPath { Path = "inventory-skip", Reason = "Existing reason" };
        var probe = new FakeProbe
        {
            Handler = (path, _, _) => path == first.FullPath
                ? Task.FromException<FileProbeResult>(new IOException("provider failure"))
                : Task.FromResult(new FileProbeResult(FileProbeStatus.Invalid, "HeaderReadFailure", null)),
        };

        AnalysisResult result = await new BrokenFileAnalyzer(probe).AnalyzeAsync(
            NewInventory([second, first], [existing]),
            CancellationToken.None);

        Assert.Equal(second.FullPath, Assert.Single(result.Findings).FullPath);
        Assert.Equal(2, result.SkippedPaths.Count);
        Assert.Same(existing, result.SkippedPaths[0]);
        Assert.Equal(first.FullPath, result.SkippedPaths[1].Path);
        Assert.Equal("Could not validate file.", result.SkippedPaths[1].Reason);
        Assert.Equal([first.FullPath, second.FullPath], probe.Paths);
    }

    [Fact]
    public async Task AnalyzeAsync_ProviderFailureAfterMutationReportsChangedSnapshot()
    {
        InventoryFile file = WriteInventoryFile("mutated.bin", [1, 2]);
        var probe = new FakeProbe
        {
            Handler = (path, _, _) =>
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                return Task.FromException<FileProbeResult>(new IOException("provider failed"));
            },
        };

        AnalysisResult result = await new BrokenFileAnalyzer(probe).AnalyzeAsync(
            NewInventory([file]),
            CancellationToken.None);

        Assert.Empty(result.Findings);
        SkippedPath skipped = Assert.Single(result.SkippedPaths);
        Assert.Equal(file.FullPath, skipped.Path);
        Assert.Equal("File changed since scan.", skipped.Reason);
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellationBeforeWorkAndAfterProbe()
    {
        InventoryFile file = WriteInventoryFile("cancel.bin", [1]);
        var before = new FakeProbe();
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BrokenFileAnalyzer(before).AnalyzeAsync(NewInventory([file]), alreadyCancelled.Token));
        Assert.Empty(before.Paths);

        using var duringProbe = new CancellationTokenSource();
        var probe = new FakeProbe
        {
            Handler = (_, _, _) =>
            {
                duringProbe.Cancel();
                return Task.FromResult(new FileProbeResult(FileProbeStatus.Invalid, "HeaderReadFailure", null));
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BrokenFileAnalyzer(probe).AnalyzeAsync(NewInventory([file]), duringProbe.Token));
        Assert.Single(probe.Paths);
    }

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private InventoryFile WriteInventoryFile(
        string fileName,
        byte[] bytes,
        FileAttributes attributes = FileAttributes.Normal)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, bytes);
        return NewInventoryFile(path, bytes.LongLength, attributes);
    }

    private static InventoryFile NewInventoryFile(string path, long sizeBytes, FileAttributes attributes)
    {
        var file = new FileInfo(path);
        return new InventoryFile(
            path,
            Path.GetFileName(path),
            Path.GetExtension(path).ToLowerInvariant(),
            Path.GetDirectoryName(path) ?? string.Empty,
            sizeBytes,
            file.Exists ? file.CreationTimeUtc : DateTime.UnixEpoch,
            file.Exists ? file.LastWriteTimeUtc : DateTime.UnixEpoch,
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

    private static byte[] Ftyp(string majorBrand)
    {
        byte[] major = System.Text.Encoding.ASCII.GetBytes(majorBrand);
        return [0, 0, 0, 16, 0x66, 0x74, 0x79, 0x70, .. major, 0, 0, 0, 0];
    }

    private sealed class FakeProbe : IFileFormatProbe
    {
        public Func<string, DetectedFileType?, CancellationToken, Task<FileProbeResult>> Handler { get; init; } =
            (_, _, _) => Task.FromResult(new FileProbeResult(FileProbeStatus.Valid, null, null));

        public List<string> Paths { get; } = [];

        public Task<FileProbeResult> ProbeAsync(
            string path,
            DetectedFileType? detectedType,
            CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Handler(path, detectedType, cancellationToken);
        }
    }
}
