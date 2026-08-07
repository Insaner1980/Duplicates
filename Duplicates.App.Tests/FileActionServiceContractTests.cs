using System.Text;
using System.Text.Json;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Xunit.Sdk;

namespace Duplicates.App.Tests;

public sealed class FileActionServiceContractTests
{
    [Fact]
    public async Task DeleteAsync_PermanentDispatchesFilesAndDirectoriesAndContinuesAfterFailure()
    {
        using var fixture = new TemporaryDirectory();
        string missingPath = fixture.PathFor("missing.txt");
        string filePath = fixture.WriteFile("file.txt", [1, 2, 3]);
        string directoryPath = fixture.CreateDirectory("folder");
        File.WriteAllText(Path.Combine(directoryPath, "child.txt"), "child");
        FileActionService service = CreateService(DeletionMode.Permanent);
        var reports = new List<DeleteProgress>();

        DeleteSummary summary = await service.DeleteAsync(
            [
                new FileActionTarget(missingPath, 0, FileActionTargetKind.File),
                new FileActionTarget(filePath, 3, FileActionTargetKind.File),
                new FileActionTarget(directoryPath, 0, FileActionTargetKind.Directory),
            ],
            new InlineProgress<DeleteProgress>(reports.Add),
            CancellationToken.None);

        Assert.Equal(2, summary.DeletedCount);
        Assert.Equal(3, summary.DeletedBytes);
        Assert.Equal(missingPath, Assert.Single(summary.Failures).Path);
        Assert.False(File.Exists(filePath));
        Assert.False(Directory.Exists(directoryPath));
        Assert.Equal([1, 2, 3], reports.Select(report => report.ProcessedCount));
        Assert.Equal([0L, 3L, 3L], reports.Select(report => report.DeletedBytes));
    }

    [Fact]
    public async Task DeleteAsync_RecycleDispatchesRegularFilesAndDirectories()
    {
        using var fixture = new TemporaryDirectory();
        string filePath = fixture.WriteFile("recycle.txt", [1]);
        string directoryPath = fixture.CreateDirectory("recycle-folder");
        File.WriteAllText(Path.Combine(directoryPath, "child.txt"), "child");
        FileActionService service = CreateService(DeletionMode.RecycleBin);

        DeleteSummary summary = await service.DeleteAsync(
            [
                new FileActionTarget(filePath, 1, FileActionTargetKind.File),
                new FileActionTarget(directoryPath, 0, FileActionTargetKind.Directory),
            ],
            null,
            CancellationToken.None);

        Assert.Equal(2, summary.DeletedCount);
        Assert.Empty(summary.Failures);
        Assert.False(File.Exists(filePath));
        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task DeleteAsync_RecycleDispatchesLinksWithoutFollowingTargets()
    {
        using var fixture = new TemporaryDirectory();
        string fileTarget = fixture.WriteFile("file-target.txt", [1, 2]);
        string directoryTarget = fixture.CreateDirectory("directory-target");
        File.WriteAllText(Path.Combine(directoryTarget, "keep.txt"), "keep");
        string fileLink = fixture.PathFor("file-link.txt");
        string directoryLink = fixture.PathFor("directory-link");
        CreateFileSymbolicLinkOrSkip(fileLink, fileTarget);
        CreateDirectorySymbolicLinkOrSkip(directoryLink, directoryTarget);
        FileActionService service = CreateService(DeletionMode.RecycleBin);

        DeleteSummary summary = await service.DeleteAsync(
            [
                new FileActionTarget(fileLink, 0, FileActionTargetKind.FileLink),
                new FileActionTarget(directoryLink, 0, FileActionTargetKind.DirectoryLink),
            ],
            null,
            CancellationToken.None);

        Assert.Equal(2, summary.DeletedCount);
        Assert.Empty(summary.Failures);
        Assert.False(File.Exists(fileLink));
        Assert.False(Directory.Exists(directoryLink));
        Assert.True(File.Exists(fileTarget));
        Assert.True(Directory.Exists(directoryTarget));
        Assert.True(File.Exists(Path.Combine(directoryTarget, "keep.txt")));
    }

    [Fact]
    public async Task MoveAsync_SkipReportsCollisionWithoutMovingSource()
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.WriteFile(Path.Combine("source", "copy.txt"), [1]);
        string destination = fixture.CreateDirectory("destination");
        string existing = fixture.WriteFile(Path.Combine("destination", "COPY.TXT"), [2]);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationSummary summary = await service.MoveAsync(
            [new FileActionTarget(source, 1, FileActionTargetKind.File)],
            destination,
            MoveCollisionBehavior.Skip,
            null,
            CancellationToken.None);

        FileOperationResult result = Assert.Single(summary.Results);
        Assert.False(result.Succeeded);
        Assert.Null(result.DestinationPath);
        Assert.Equal(source, Assert.IsType<FileActionFailure>(result.Failure).Path);
        Assert.True(File.Exists(source));
        Assert.Equal([2], File.ReadAllBytes(existing));
    }

    [Fact]
    public async Task MoveAsync_KeepBothUsesFirstCaseInsensitiveFreeSuffixAndPreservesTimestamp()
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.WriteFile(Path.Combine("source", "copy.txt"), [1, 2, 3]);
        DateTime modifiedUtc = new(2026, 7, 1, 12, 34, 56, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modifiedUtc);
        string destination = fixture.CreateDirectory("destination");
        fixture.WriteFile(Path.Combine("destination", "COPY.TXT"), [9]);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationSummary summary = await service.MoveAsync(
            [new FileActionTarget(source, 3, FileActionTargetKind.File)],
            destination,
            MoveCollisionBehavior.KeepBoth,
            null,
            CancellationToken.None);

        FileOperationResult result = Assert.Single(summary.Results);
        string expected = Path.Combine(destination, "copy (2).txt");
        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.DestinationPath);
        Assert.False(File.Exists(source));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(expected));
        Assert.Equal(modifiedUtc, File.GetLastWriteTimeUtc(expected));
        Assert.Equal(3, summary.SucceededBytes);
    }

    [Fact]
    public async Task MoveAsync_CancelPreflightsAllCollisionsBeforeCreatingOrMoving()
    {
        using var fixture = new TemporaryDirectory();
        string first = fixture.WriteFile(Path.Combine("source-a", "same.txt"), [1]);
        string second = fixture.WriteFile(Path.Combine("source-b", "unique.txt"), [2]);
        string destination = fixture.CreateDirectory("destination");
        fixture.WriteFile(Path.Combine("destination", "SAME.TXT"), [9]);
        FileActionService service = CreateService(DeletionMode.Permanent);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.MoveAsync(
            [
                new FileActionTarget(first, 1, FileActionTargetKind.File),
                new FileActionTarget(second, 1, FileActionTargetKind.File),
            ],
            destination,
            MoveCollisionBehavior.Cancel,
            null,
            CancellationToken.None));

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(destination, "unique.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-folder")]
    [InlineData("C:\\invalid|folder")]
    public async Task MoveAsync_InvalidDestinationThrowsBeforeMutation(string destination)
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.WriteFile("source.txt", [1]);
        FileActionService service = CreateService(DeletionMode.Permanent);

        await Assert.ThrowsAsync<ArgumentException>(() => service.MoveAsync(
            [new FileActionTarget(source, 1, FileActionTargetKind.File)],
            destination,
            MoveCollisionBehavior.Skip,
            null,
            CancellationToken.None));

        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task MoveAsync_ContinuesAfterPerItemFailureAndReportsEveryAttempt()
    {
        using var fixture = new TemporaryDirectory();
        string missing = fixture.PathFor(Path.Combine("missing", "first.txt"));
        string valid = fixture.WriteFile(Path.Combine("source", "second.txt"), [1, 2]);
        string destination = fixture.PathFor("new-destination");
        FileActionService service = CreateService(DeletionMode.Permanent);
        var reports = new List<FileOperationProgress>();

        FileOperationSummary summary = await service.MoveAsync(
            [
                new FileActionTarget(missing, 1, FileActionTargetKind.File),
                new FileActionTarget(valid, 2, FileActionTargetKind.File),
            ],
            destination,
            MoveCollisionBehavior.Skip,
            new InlineProgress<FileOperationProgress>(reports.Add),
            CancellationToken.None);

        Assert.Equal(2, summary.Results.Count);
        Assert.False(summary.Results[0].Succeeded);
        Assert.True(summary.Results[1].Succeeded);
        Assert.Equal(2, summary.SucceededBytes);
        Assert.Equal([1, 2], reports.Select(report => report.ProcessedCount));
        Assert.Equal([0L, 2L], reports.Select(report => report.SucceededBytes));
        Assert.True(File.Exists(Path.Combine(destination, "second.txt")));
    }

    [Fact]
    public async Task RenameAsync_CollisionReturnsFailureWithoutOverwriting()
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.WriteFile("source.txt", [1]);
        string collision = fixture.WriteFile("TARGET.TXT", [2]);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationResult result = await service.RenameAsync(
            new FileActionTarget(source, 1, FileActionTargetKind.File),
            "target.txt",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.DestinationPath);
        Assert.True(File.Exists(source));
        Assert.Equal([2], File.ReadAllBytes(collision));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("folder\\name.txt")]
    [InlineData("CON.txt")]
    [InlineData("trailing.")]
    public async Task RenameAsync_InvalidWindowsLeafThrowsBeforeMutation(string newName)
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.WriteFile("source.txt", [1]);
        FileActionService service = CreateService(DeletionMode.Permanent);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync(
            new FileActionTarget(source, 1, FileActionTargetKind.File),
            newName,
            CancellationToken.None));

        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ExportAsync_CsvUsesBomRfc4180AndDeterministicRecordOrder()
    {
        using var fixture = new TemporaryDirectory();
        string destination = fixture.PathFor("results.csv");
        var metadata = new Dictionary<string, string>
        {
            ["zeta"] = "last",
            ["alpha"] = "first",
        };
        var items = new List<ResultExportItem>
        {
            NewExportItem(@"C:\\scan\\b.txt", "b", metadata),
            NewExportItem(@"C:\\scan\\a,copy.txt", "A", new Dictionary<string, string>()),
        };
        var skipped = new List<SkippedPath>
        {
            new() { Path = @"C:\\scan\\locked.txt", Reason = "Access \"denied\"" },
        };
        var snapshot = new ResultExportSnapshot(
            ToolKind.BigFiles,
            new DateTimeOffset(2026, 8, 7, 15, 30, 0, TimeSpan.Zero),
            "1 folder, recursive",
            items,
            skipped);
        var service = new ResultExportService();

        Task export = service.ExportAsync(snapshot, ResultExportFormat.Csv, destination, CancellationToken.None);
        items.Clear();
        metadata["alpha"] = "changed";
        skipped.Clear();
        await export;

        byte[] bytes = await File.ReadAllBytesAsync(destination);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        string csv = Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.GetPreamble().Length));
        Assert.StartsWith(
            "record_type,tool,generated_utc,scope_summary,path,kind,reason,suggestion,group_id,similarity_percent,size_bytes,created_utc,modified_utc,metadata,skipped_reason\r\n",
            csv,
            StringComparison.Ordinal);
        Assert.Contains("\"1 folder, recursive\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"C:\\\\scan\\\\a,copy.txt\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Access \"\"denied\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"{\"\"alpha\"\":\"\"first\"\",\"\"zeta\"\":\"\"last\"\"}\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("changed", csv, StringComparison.Ordinal);

        int skippedIndex = csv.IndexOf("skipped_path", StringComparison.Ordinal);
        int groupAIndex = csv.IndexOf(",A,", StringComparison.Ordinal);
        int groupBIndex = csv.IndexOf(",b,", StringComparison.Ordinal);
        Assert.True(skippedIndex < groupAIndex && groupAIndex < groupBIndex);
    }

    [Fact]
    public async Task ExportAsync_JsonIsIndentedAndSortsItemsAndMetadata()
    {
        using var fixture = new TemporaryDirectory();
        string destination = fixture.PathFor("results.json");
        var service = new ResultExportService();
        var snapshot = new ResultExportSnapshot(
            ToolKind.SimilarImages,
            new DateTimeOffset(2026, 8, 7, 15, 30, 0, TimeSpan.Zero),
            "Photos",
            [
                NewExportItem(@"C:\\scan\\z.jpg", "group"),
                NewExportItem(@"C:\\scan\\A.jpg", "GROUP", new Dictionary<string, string>
                {
                    ["zeta"] = "2",
                    ["alpha"] = "1",
                }),
            ],
            [new SkippedPath { Path = @"C:\\scan\\locked.jpg", Reason = "Locked" }]);

        await service.ExportAsync(snapshot, ResultExportFormat.Json, destination, CancellationToken.None);

        string json = await File.ReadAllTextAsync(destination);
        Assert.Contains(Environment.NewLine, json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement exportMetadata = root.GetProperty("metadata");
        Assert.Equal("SimilarImages", exportMetadata.GetProperty("tool").GetString());
        Assert.Equal("2026-08-07T15:30:00.0000000+00:00", exportMetadata.GetProperty("generatedAtUtc").GetString());
        Assert.Equal("Photos", exportMetadata.GetProperty("scopeSummary").GetString());
        JsonElement[] exportedItems = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(@"C:\\scan\\A.jpg", exportedItems[0].GetProperty("fullPath").GetString());
        Assert.Equal(@"C:\\scan\\z.jpg", exportedItems[1].GetProperty("fullPath").GetString());
        Assert.Equal(
            ["alpha", "zeta"],
            exportedItems[0].GetProperty("metadata").EnumerateObject().Select(property => property.Name));
        Assert.Equal(@"C:\\scan\\locked.jpg", root.GetProperty("skippedPaths")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task ExportAsync_ExistingDestinationIsUntouchedAndTemporaryOutputIsRemoved()
    {
        using var fixture = new TemporaryDirectory();
        string destination = fixture.WriteFile("results.json", [7, 8, 9]);
        var service = new ResultExportService();
        var snapshot = new ResultExportSnapshot(
            ToolKind.EmptyFiles,
            DateTimeOffset.UtcNow,
            "Scope",
            [],
            []);

        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(
            snapshot,
            ResultExportFormat.Json,
            destination,
            CancellationToken.None));

        Assert.Equal([7, 8, 9], File.ReadAllBytes(destination));
        Assert.Equal([destination], Directory.GetFiles(fixture.RootPath));
    }

    private static ResultExportItem NewExportItem(
        string path,
        string groupId,
        IReadOnlyDictionary<string, string>? metadata = null) => new(
            path,
            FileActionTargetKind.File,
            "Test reason",
            "Review",
            groupId,
            87.5,
            42,
            new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 2, 11, 0, 0, DateTimeKind.Utc),
            metadata ?? new Dictionary<string, string>());

    private static FileActionService CreateService(DeletionMode deletionMode)
    {
        var settings = new FakeSettingsService();
        settings.SetCurrent(new AppSettings { DeletionMode = deletionMode });
        return new FileActionService(settings);
    }

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"File symbolic links are not available on this host: {ex.Message}");
        }
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Directory symbolic links are not available on this host: {ex.Message}");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string PathFor(string relativePath) => Path.Combine(RootPath, relativePath);

        public string CreateDirectory(string relativePath)
        {
            string path = PathFor(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string WriteFile(string relativePath, byte[] bytes)
        {
            string path = PathFor(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
