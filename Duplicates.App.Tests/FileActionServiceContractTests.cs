using System.Security.Principal;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.VisualBasic.FileIO;
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
        FileActionService service = CreateService(DeletionMode.RecycleBin);
        DateTime operationStartedUtc = DateTime.UtcNow.AddSeconds(-1);

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
        Assert.True(
            WaitForRecycledPath(directoryPath, operationStartedUtc),
            "The empty directory was removed but no matching Recycle Bin metadata was found.");
    }

    [Theory]
    [InlineData(DeletionMode.Permanent)]
    [InlineData(DeletionMode.RecycleBin)]
    public async Task DeleteAsync_RegularNonEmptyDirectoryFailsClosedAndPreservesEntireTree(
        DeletionMode deletionMode)
    {
        using var fixture = new TemporaryDirectory();
        string directoryPath = fixture.CreateDirectory("not-empty");
        string directChild = fixture.WriteFile(Path.Combine("not-empty", "direct.txt"), [1]);
        string childDirectory = fixture.CreateDirectory(Path.Combine("not-empty", "nested"));
        string nestedChild = fixture.WriteFile(Path.Combine("not-empty", "nested", "child.txt"), [2]);
        FileActionService service = CreateService(deletionMode);

        DeleteSummary summary = await service.DeleteAsync(
            [new FileActionTarget(directoryPath, 0, FileActionTargetKind.Directory)],
            null,
            CancellationToken.None);

        Assert.Equal(0, summary.DeletedCount);
        Assert.Equal(directoryPath, Assert.Single(summary.Failures).Path);
        Assert.True(Directory.Exists(directoryPath));
        Assert.True(File.Exists(directChild));
        Assert.True(Directory.Exists(childDirectory));
        Assert.True(File.Exists(nestedChild));
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
    public async Task DeleteAsync_ExpectedMissingLinkRejectsNowValidReplacement()
    {
        using var fixture = new TemporaryDirectory();
        string target = fixture.WriteFile("target.txt", [1]);
        string link = fixture.PathFor("link.txt");
        CreateFileSymbolicLinkOrSkip(link, target);
        FileActionService service = CreateService(DeletionMode.Permanent);

        DeleteSummary summary = await service.DeleteAsync(
            [new FileActionTarget(link, 0, FileActionTargetKind.FileLink, "Link target is missing.")],
            null,
            CancellationToken.None);

        Assert.Equal(0, summary.DeletedCount);
        Assert.Equal(link, Assert.Single(summary.Failures).Path);
        Assert.True(File.Exists(link));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task MoveAsync_ExpectedMissingLinkRejectsNowValidReplacement()
    {
        using var fixture = new TemporaryDirectory();
        string target = fixture.WriteFile("target.txt", [1]);
        string link = fixture.PathFor("link.txt");
        string destination = fixture.CreateDirectory("destination");
        CreateFileSymbolicLinkOrSkip(link, target);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationSummary summary = await service.MoveAsync(
            [new FileActionTarget(link, 0, FileActionTargetKind.FileLink, "Link target is missing.")],
            destination,
            MoveCollisionBehavior.Skip,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.True(File.Exists(link));
        Assert.False(File.Exists(Path.Combine(destination, "link.txt")));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task RenameAsync_ExpectedMissingLinkRejectsNowValidReplacement()
    {
        using var fixture = new TemporaryDirectory();
        string target = fixture.WriteFile("target.txt", [1]);
        string link = fixture.PathFor("link.txt");
        CreateFileSymbolicLinkOrSkip(link, target);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationResult result = await service.RenameAsync(
            new FileActionTarget(link, 0, FileActionTargetKind.FileLink, "Link target is missing."),
            "renamed.txt",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(link));
        Assert.False(File.Exists(fixture.PathFor("renamed.txt")));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task RenameAsync_ExpectedMissingLinkRejectsUnresolvedClassification()
    {
        using var fixture = new TemporaryDirectory();
        string link = fixture.PathFor("loop-link");
        CreateFileSymbolicLinkOrSkip(link, "loop-link");
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationResult result = await service.RenameAsync(
            new FileActionTarget(link, 0, FileActionTargetKind.FileLink, "Link target is missing."),
            "renamed-link",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(fixture.PathFor("renamed-link")));
    }

    [Fact]
    public void PermanentDirectoryLinkDeletionPrimitive_IsNonRecursive()
    {
        using var fixture = new TemporaryDirectory();
        string directory = fixture.CreateDirectory("normal-directory");
        string child = fixture.WriteFile(Path.Combine("normal-directory", "keep.txt"), [1]);
        MethodInfo deleteTarget = typeof(FileActionService).GetMethod(
            "DeleteTarget",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("DeleteTarget was not found.");

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => deleteTarget.Invoke(
            null,
            [new FileActionTarget(directory, 0, FileActionTargetKind.DirectoryLink), RecycleOption.DeletePermanently]));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(child));
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
    public async Task MoveAsync_EmptyDirectoryPreservesTimestampsAndNeverOverwrites()
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.CreateDirectory(Path.Combine("source", "empty"));
        DateTime createdUtc = new(2025, 5, 1, 10, 20, 30, DateTimeKind.Utc);
        DateTime modifiedUtc = new(2026, 6, 2, 11, 21, 31, DateTimeKind.Utc);
        Directory.SetCreationTimeUtc(source, createdUtc);
        Directory.SetLastWriteTimeUtc(source, modifiedUtc);
        string destination = fixture.CreateDirectory("destination");
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationSummary moved = await service.MoveAsync(
            [new FileActionTarget(source, 0, FileActionTargetKind.Directory)],
            destination,
            MoveCollisionBehavior.Skip,
            null,
            CancellationToken.None);

        string movedPath = Path.Combine(destination, "empty");
        Assert.True(Assert.Single(moved.Results).Succeeded);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(movedPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(movedPath));
        Assert.Equal(createdUtc, Directory.GetCreationTimeUtc(movedPath));
        Assert.Equal(modifiedUtc, Directory.GetLastWriteTimeUtc(movedPath));

        string secondSource = fixture.CreateDirectory(Path.Combine("second-source", "empty"));
        File.WriteAllText(Path.Combine(movedPath, "keep.txt"), "keep");
        FileOperationSummary collision = await service.MoveAsync(
            [new FileActionTarget(secondSource, 0, FileActionTargetKind.Directory)],
            destination,
            MoveCollisionBehavior.Skip,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(collision.Results).Succeeded);
        Assert.True(Directory.Exists(secondSource));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(movedPath, "keep.txt")));
    }

    [CrossVolumeFact]
    public async Task MoveAsync_EmptyDirectoryMovesAcrossWritableVolumesWhenAvailable()
    {
        (string SourceRoot, string DestinationRoot)? roots = TryCreateCrossVolumeRoots();
        Assert.True(roots.HasValue, "Two writable volumes became unavailable after test discovery.");

        string sourceRoot = roots.Value.SourceRoot;
        string destinationRoot = roots.Value.DestinationRoot;
        try
        {
            string source = Path.Combine(sourceRoot, "empty");
            Directory.CreateDirectory(source);
            DateTime modifiedUtc = new(2026, 6, 2, 11, 21, 32, DateTimeKind.Utc);
            Directory.SetLastWriteTimeUtc(source, modifiedUtc);
            FileActionService service = CreateService(DeletionMode.Permanent);

            FileOperationSummary summary = await service.MoveAsync(
                [new FileActionTarget(source, 0, FileActionTargetKind.Directory)],
                destinationRoot,
                MoveCollisionBehavior.Skip,
                null,
                CancellationToken.None);

            string movedPath = Path.Combine(destinationRoot, "empty");
            Assert.True(Assert.Single(summary.Results).Succeeded);
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(movedPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(movedPath));
            Assert.Equal(modifiedUtc, Directory.GetLastWriteTimeUtc(movedPath));
        }
        finally
        {
            DeleteFixtureRoot(sourceRoot);
            DeleteFixtureRoot(destinationRoot);
        }
    }

    [Fact]
    public async Task MoveAsync_CancellationAfterFirstItemCarriesCompletedSummary()
    {
        using var fixture = new TemporaryDirectory();
        string first = fixture.WriteFile(Path.Combine("source-a", "first.txt"), [1]);
        string second = fixture.WriteFile(Path.Combine("source-b", "second.txt"), [2]);
        string destination = fixture.CreateDirectory("destination");
        FileActionService service = CreateService(DeletionMode.Permanent);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileOperationProgress>(report =>
        {
            if (report.ProcessedCount == 1)
            {
                cancellation.Cancel();
            }
        });

        FileOperationCanceledException exception = await Assert.ThrowsAsync<FileOperationCanceledException>(() =>
            service.MoveAsync(
                [
                    new FileActionTarget(first, 1, FileActionTargetKind.File),
                    new FileActionTarget(second, 1, FileActionTargetKind.File),
                ],
                destination,
                MoveCollisionBehavior.Skip,
                progress,
                cancellation.Token));

        FileOperationResult completed = Assert.Single(exception.Summary.Results);
        Assert.True(completed.Succeeded);
        Assert.Equal(first, completed.SourcePath);
        Assert.Equal(1, exception.Summary.SucceededBytes);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(Path.Combine(destination, "first.txt")));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(destination, "second.txt")));
    }

    [Fact]
    public async Task DeleteAsync_CancellationAfterFirstItemCarriesCompletedSummary()
    {
        using var fixture = new TemporaryDirectory();
        string first = fixture.WriteFile("first.txt", [1]);
        string second = fixture.WriteFile("second.txt", [2]);
        FileActionService service = CreateService(DeletionMode.Permanent);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<DeleteProgress>(report =>
        {
            if (report.ProcessedCount == 1)
            {
                cancellation.Cancel();
            }
        });

        DeleteOperationCanceledException exception = await Assert.ThrowsAsync<DeleteOperationCanceledException>(() =>
            service.DeleteAsync(
                [
                    new FileActionTarget(first, 1, FileActionTargetKind.File),
                    new FileActionTarget(second, 1, FileActionTargetKind.File),
                ],
                progress,
                cancellation.Token));

        Assert.Equal(1, exception.Summary.DeletedCount);
        Assert.Equal(1, exception.Summary.DeletedBytes);
        Assert.Empty(exception.Summary.Failures);
        Assert.Equal([first], exception.Summary.DeletedPaths);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task DeleteAsync_CancellationBeforeFirstItemKeepsStandardCancellationContract()
    {
        using var fixture = new TemporaryDirectory();
        string path = fixture.WriteFile("file.txt", [1]);
        FileActionService service = CreateService(DeletionMode.Permanent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DeleteAsync(
                [new FileActionTarget(path, 1, FileActionTargetKind.File)],
                null,
                cancellation.Token));

        Assert.IsNotType<DeleteOperationCanceledException>(exception);
        Assert.True(File.Exists(path));
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

    [Fact]
    public async Task RenameAsync_MatchingBadExtensionConstraintRenamesFile()
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.WriteFile(
            "photo.txt",
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var file = new FileInfo(source);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationResult result = await service.RenameAsync(
            new FileActionTarget(
                source,
                file.Length,
                FileActionTargetKind.File,
                ExpectedBadExtensionContent: new BadExtensionContentConstraint(
                    file.LastWriteTimeUtc,
                    "PNG",
                    ".png")),
            "photo.png",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(fixture.PathFor("photo.png")));
    }

    [Fact]
    public async Task RenameAsync_BadExtensionConstraintRejectsSameLengthSignatureRewrite()
    {
        using var fixture = new TemporaryDirectory();
        string source = fixture.WriteFile(
            "photo.txt",
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var file = new FileInfo(source);
        DateTime scanModifiedUtc = file.LastWriteTimeUtc;
        var target = new FileActionTarget(
            source,
            file.Length,
            FileActionTargetKind.File,
            ExpectedBadExtensionContent: new BadExtensionContentConstraint(
                scanModifiedUtc,
                "PNG",
                ".png"));
        File.WriteAllBytes(source, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x00, 0x00, 0x00]);
        File.SetLastWriteTimeUtc(source, scanModifiedUtc);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileOperationResult result = await service.RenameAsync(
            target,
            "photo.png",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("The source no longer matches the scan result.", result.Failure?.Reason);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(fixture.PathFor("photo.png")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("folder\\name.txt")]
    [InlineData("CON.txt")]
    [InlineData("COM1 .txt")]
    [InlineData("LPT9..log")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAndMove_ExpectedModifiedUtcRejectsBoundaryRace(bool move)
    {
        using var fixture = new TemporaryDirectory();
        string path = fixture.WriteFile("changed.bin", [1, 2, 3]);
        DateTime expectedModifiedUtc = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, expectedModifiedUtc.AddSeconds(5));
        var target = new FileActionTarget(
            path,
            3,
            FileActionTargetKind.File,
            ExpectedModifiedUtc: expectedModifiedUtc);
        FileActionService service = CreateService(DeletionMode.Permanent);

        FileActionFailure failure;
        if (move)
        {
            string destination = fixture.CreateDirectory("destination");
            FileOperationSummary summary = await service.MoveAsync(
                [target],
                destination,
                MoveCollisionBehavior.Skip,
                null,
                CancellationToken.None);
            failure = Assert.IsType<FileActionFailure>(Assert.Single(summary.Results).Failure);
            Assert.False(File.Exists(Path.Combine(destination, "changed.bin")));
        }
        else
        {
            DeleteSummary summary = await service.DeleteAsync([target], null, CancellationToken.None);
            failure = Assert.Single(summary.Failures);
        }

        Assert.Equal(path, failure.Path);
        Assert.True(File.Exists(path));
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

    private static bool WaitForRecycledPath(string originalPath, DateTime operationStartedUtc)
    {
        string? root = Path.GetPathRoot(originalPath);
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        string recycleDirectory = Path.Combine(root, "$Recycle.Bin", sid);
        byte[] expectedPath = Encoding.Unicode.GetBytes(originalPath);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                foreach (string metadataPath in Directory.EnumerateFiles(
                    recycleDirectory,
                    "$I*",
                    System.IO.SearchOption.TopDirectoryOnly))
                {
                    if (File.GetLastWriteTimeUtc(metadataPath) < operationStartedUtc)
                    {
                        continue;
                    }

                    byte[] metadata = File.ReadAllBytes(metadataPath);
                    if (metadata.AsSpan().IndexOf(expectedPath) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static (string SourceRoot, string DestinationRoot)? TryCreateCrossVolumeRoots()
    {
        var writableRoots = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
        {
            string candidate = Path.Combine(drive.RootDirectory.FullName, $"Duplicates-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(candidate);
                writableRoots.Add(candidate);
                if (writableRoots.Count == 2)
                {
                    return (writableRoots[0], writableRoots[1]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (string root in writableRoots)
        {
            DeleteFixtureRoot(root);
        }

        return null;
    }

    private static void DeleteFixtureRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
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

public sealed class CrossVolumeFactAttribute : FactAttribute
{
    private const string UnavailableReason =
        "Cross-volume move requires two writable local volumes; this host exposes fewer than two.";

    public CrossVolumeFactAttribute()
    {
        if (!HasTwoWritableVolumes())
        {
            Skip = UnavailableReason;
        }
    }

    private static bool HasTwoWritableVolumes()
    {
        int writableVolumeCount = 0;
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
        {
            string probe = Path.Combine(drive.RootDirectory.FullName, $"Duplicates-probe-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(probe);
                writableVolumeCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                if (Directory.Exists(probe))
                {
                    Directory.Delete(probe, recursive: false);
                }
            }

            if (writableVolumeCount == 2)
            {
                return true;
            }
        }

        return false;
    }
}
