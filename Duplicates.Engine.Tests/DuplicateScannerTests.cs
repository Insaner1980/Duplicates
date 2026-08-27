using Duplicates.Engine;
using Duplicates.Engine.FileEnumeration;
using Duplicates.Engine.Hashing;
using Duplicates.Engine.Models;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Duplicates.Engine.Tests;

public sealed class DuplicateScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Duplicates.Engine.Tests", Guid.NewGuid().ToString("N"));

    public DuplicateScannerTests()
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
    public async Task ScanAsync_ByteIdenticalFiles_ReturnsDuplicateGroupSortedByWastedBytes()
    {
        string largestA = WriteFile("photos/a.bin", 128_000, 17);
        string largestB = WriteFile("backup/a-copy.bin", 128_000, 17);
        string smallA = WriteFile("docs/readme-a.txt", "same text");
        string smallB = WriteFile("docs/readme-b.txt", "same text");
        WriteFile("unique.bin", 64_000, 91);

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(2, result.TotalDuplicateFiles);
        Assert.Equal(128_000 + "same text".Length, result.TotalReclaimableBytes);
        Assert.Equal(128_000, result.Groups[0].SizeBytes);
        Assert.Equal(2, result.Groups[0].Files.Count);
        Assert.Contains(result.Groups[0].Files, file => file.FullPath == largestA);
        Assert.Contains(result.Groups[0].Files, file => file.FullPath == largestB);
        Assert.Contains(result.Groups[1].Files, file => file.FullPath == smallA);
        Assert.Contains(result.Groups[1].Files, file => file.FullPath == smallB);
    }

    [Fact]
    public async Task ScanAsync_SameSizeFilesWithDifferentHeadBytes_AreNotDuplicates()
    {
        WriteFile("one.bin", [1, 2, 3, 4, 5, 6]);
        WriteFile("two.bin", [9, 2, 3, 4, 5, 6]);

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.TotalFilesScanned);
        Assert.Equal(0, result.TotalReclaimableBytes);
    }

    [Fact]
    public async Task ScanAsync_SameFirst64KbButDifferentTail_AreNotDuplicates()
    {
        byte[] first = new byte[80_000];
        byte[] second = new byte[80_000];
        Array.Fill(first, (byte)7, 0, 64 * 1024);
        Array.Fill(second, (byte)7, 0, 64 * 1024);
        first[^1] = 1;
        second[^1] = 2;
        WriteFile("first.bin", first);
        WriteFile("second.bin", second);

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Empty(result.SkippedPaths);
    }

    [Fact]
    public async Task ScanAsync_LockedCandidate_IsSkipped()
    {
        string locked = WriteFile("locked-a.bin", 80_000, 4);
        WriteFile("locked-b.bin", 80_000, 4);

        await using FileStream lockStream = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scanner = new DuplicateScanner();

        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == locked);
    }

    [Fact]
    public async Task ScanAsync_InvalidFolder_IsSkipped()
    {
        string missingFolder = Path.Combine(_root, "missing");
        var scanner = new DuplicateScanner();

        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { Folders = [missingFolder] },
            progress: null,
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingFolder);
    }

    [Fact]
    public async Task ScanAsync_InvalidConcurrency_Throws()
    {
        var scanner = new DuplicateScanner();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => scanner.ScanAsync(NewOptions() with { MaxHashingConcurrency = 3 }, progress: null, CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_HashFailure_IsSkipped()
    {
        string failingPath = WriteFile("failing.bin", 80_000, 8);
        WriteFile("peer.bin", 80_000, 8);
        var scanner = new DuplicateScanner(new FileWalker(), new FailingHasher(failingPath));

        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == failingPath && skipped.Reason == "File changed during scan.");
    }


    [Fact]
    public async Task ScanAsync_HardLinkedSameFile_IsNotReportedAsDuplicate()
    {
        string original = WriteFile("original.bin", 80_000, 12);
        string hardlink = Path.Combine(_root, "hardlink.bin");
        Assert.True(CreateHardLinkW(hardlink, original, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task ScanAsync_ForcedHashCollision_SplitsGroupsByByteVerification()
    {
        string firstA = WriteFile("first-a.bin", 80_000, 1);
        string firstB = WriteFile("first-b.bin", 80_000, 1);
        string secondA = WriteFile("second-a.bin", 80_000, 2);
        string secondB = WriteFile("second-b.bin", 80_000, 2);
        var scanner = new DuplicateScanner(new FileWalker(), new ConstantHasher());

        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Equal(2, result.Groups.Count);
        Assert.Contains(result.Groups, group => group.Files.Any(file => file.FullPath == firstA) && group.Files.Any(file => file.FullPath == firstB));
        Assert.Contains(result.Groups, group => group.Files.Any(file => file.FullPath == secondA) && group.Files.Any(file => file.FullPath == secondB));
    }

    [Fact]
    public async Task ScanAsync_ByteVerificationRentsOneBufferPairPerHashGroup()
    {
        const int fileCount = 64;
        for (int index = 0; index < fileCount; index++)
        {
            WriteFile($"identical-{index:D2}.bin", "same verifier bytes");
        }

        var verificationPool = new CountingArrayPool();
        var scanner = new DuplicateScanner(new FileWalker(), new FileHasher(), verificationPool);

        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { VerifyByteByByte = true },
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(fileCount, group.Files.Count);
        Assert.Equal(fileCount - 1, result.TotalDuplicateFiles);
        Assert.Equal(2, verificationPool.RentCount);
        Assert.Equal(2, verificationPool.ReturnCount);
    }

    [Fact]
    public async Task ScanAsync_DefaultOptions_ExcludeZeroByteFiles()
    {
        WriteFile("empty-a.txt", []);
        WriteFile("empty-b.txt", []);

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(NewOptions(), progress: null, CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalFilesScanned);
    }

    [Fact]
    public async Task ScanAsync_MinSizeZero_IncludesZeroByteDuplicatesWithoutVerifyFailure()
    {
        string emptyA = WriteFile("empty-a.txt", []);
        string emptyB = WriteFile("empty-b.txt", []);

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(NewOptions() with { MinSizeBytes = 0 }, progress: null, CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(0, group.SizeBytes);
        Assert.Equal(0, group.WastedBytes);
        Assert.Contains(group.Files, file => file.FullPath == emptyA);
        Assert.Contains(group.Files, file => file.FullPath == emptyB);
    }

    [Fact]
    public async Task ScanAsync_CustomExtensions_NormalizesExtensionsAndFiltersFiles()
    {
        WriteFile("images/one.JPG", "same image bytes");
        WriteFile("images/two.jpg", "same image bytes");
        WriteFile("notes/one.txt", "same text bytes");
        WriteFile("notes/two.txt", "same text bytes");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { TypeFilter = FileTypeFilter.ForCustomExtensions(["jpg"]) },
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.All(group.Files, file => Assert.Equal(".jpg", file.Extension));
    }

    [Fact]
    public async Task ScanAsync_CategoryFilter_UsesKnownExtensionSets()
    {
        WriteFile("images/one.png", "same image bytes");
        WriteFile("images/two.PNG", "same image bytes");
        WriteFile("archives/one.zip", "same archive bytes");
        WriteFile("archives/two.zip", "same archive bytes");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { TypeFilter = FileTypeFilter.ForCategories([FileTypeCategory.Images]) },
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.All(group.Files, file => Assert.Equal(".png", file.Extension));
    }

    [Fact]
    public async Task ScanAsync_OverlappingFolders_DeduplicatesCanonicalPaths()
    {
        string duplicateA = WriteFile("nested/a.bin", "same");
        string duplicateB = WriteFile("nested/b.bin", "same");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { Folders = [_root, Path.Combine(_root, "nested")] },
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, file => file.FullPath == duplicateA);
        Assert.Contains(group.Files, file => file.FullPath == duplicateB);
    }

    [Fact]
    public async Task ScanAsync_ExplicitIncludedFiles_AreCompared()
    {
        string first = WriteFile("explicit/first.bin", "same explicit bytes");
        string second = WriteFile("explicit/second.bin", "same explicit bytes");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { Folders = [], Files = [first, second] },
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, result.TotalFilesScanned);
        Assert.Contains(group.Files, file => file.FullPath == first);
        Assert.Contains(group.Files, file => file.FullPath == second);
    }

    [Fact]
    public async Task ScanAsync_ExplicitFileAlreadyReachedByFolder_IsCountedOnce()
    {
        string first = WriteFile("folder/first.bin", "same bytes");
        string second = WriteFile("folder/second.bin", "same bytes");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { Files = [first] },
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, result.TotalFilesScanned);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, file => file.FullPath == first);
        Assert.Contains(group.Files, file => file.FullPath == second);
    }

    [Fact]
    public async Task ScanAsync_MissingExplicitFile_IsIgnoredSafely()
    {
        string missing = Path.Combine(_root, "missing.bin");
        var scanner = new DuplicateScanner();

        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { Folders = [], Files = [missing] },
            progress: null,
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalFilesScanned);
        Assert.Empty(result.SkippedPaths);
    }

    [Fact]
    public async Task ScanAsync_ExplicitFiles_RespectAttributeSizeTypeAndExclusionFilters()
    {
        string allowedFirst = WriteFile("allowed/first.txt", "same bytes");
        string allowedSecond = WriteFile("allowed/second.txt", "same bytes");
        string tooSmall = WriteFile("filtered/small.txt", "x");
        string tooLarge = WriteFile("filtered/large.txt", new string('L', 11));
        string wrongType = WriteFile("filtered/type.bin", new string('B', 10));
        string hidden = WriteFile("filtered/hidden.txt", new string('H', 10));
        string system = WriteFile("filtered/system.txt", new string('S', 10));
        string excluded = WriteFile("filtered/excluded.txt", new string('E', 10));
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with
            {
                Folders = [],
                Files = [allowedFirst, allowedSecond, tooSmall, tooLarge, wrongType, hidden, system, excluded],
                ExcludedPaths = [excluded],
                MinSizeBytes = 10,
                MaxSizeBytes = 10,
                TypeFilter = FileTypeFilter.ForCustomExtensions(["txt"]),
            },
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, result.TotalFilesScanned);
        Assert.Contains(group.Files, file => file.FullPath == allowedFirst);
        Assert.Contains(group.Files, file => file.FullPath == allowedSecond);
    }

    [Fact]
    public async Task ScanAsync_ExcludedDirectory_RemovesAllDescendants()
    {
        string includedFirst = WriteFile("included/first.bin", "same bytes");
        string includedSecond = WriteFile("included/second.bin", "same bytes");
        string excludedFirst = WriteFile("excluded/first.bin", "same bytes");
        string excludedSecond = WriteFile("excluded/nested/second.bin", "same bytes");
        string prefixSiblingFirst = WriteFile("excluded-more/first.bin", "different matching bytes");
        string prefixSiblingSecond = WriteFile("excluded-more/second.bin", "different matching bytes");
        string excludedDirectory = Path.Combine(_root, "excluded");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { ExcludedPaths = [excludedDirectory] },
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(4, result.TotalFilesScanned);
        Assert.Contains(result.Groups, group =>
            group.Files.Any(file => file.FullPath == includedFirst) &&
            group.Files.Any(file => file.FullPath == includedSecond));
        Assert.Contains(result.Groups, group =>
            group.Files.Any(file => file.FullPath == prefixSiblingFirst) &&
            group.Files.Any(file => file.FullPath == prefixSiblingSecond));
        Assert.DoesNotContain(result.Groups.SelectMany(group => group.Files), file => file.FullPath == excludedFirst);
        Assert.DoesNotContain(result.Groups.SelectMany(group => group.Files), file => file.FullPath == excludedSecond);
    }

    [Fact]
    public async Task ScanAsync_ExcludedFile_DoesNotRemoveItsSibling()
    {
        string excluded = WriteFile("one.bin", "same bytes");
        string sibling = WriteFile("two.bin", "same bytes");

        var scanner = new DuplicateScanner();
        ScanResult result = await scanner.ScanAsync(
            NewOptions() with { ExcludedPaths = [excluded] },
            progress: null,
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.TotalFilesScanned);
        Assert.DoesNotContain(result.SkippedPaths, path => path.Path == sibling);
    }

    [Fact]
    public async Task ScanAsync_AlreadyCancelled_ThrowsOperationCanceledException()
    {
        WriteFile("one.bin", 80_000, 1);
        WriteFile("two.bin", 80_000, 1);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var scanner = new DuplicateScanner();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => scanner.ScanAsync(NewOptions(), progress: null, cts.Token));
    }

    [Fact]
    public async Task ScanAsync_ProgressReports_AreThrottledForFastScans()
    {
        for (int index = 0; index < 50; index++)
        {
            WriteFile($"unique-{index}.bin", index + 1, (byte)index);
        }

        var reports = new List<ScanProgress>();
        var scanner = new DuplicateScanner();
        var progress = new InlineProgress<ScanProgress>(report => reports.Add(report));

        await scanner.ScanAsync(NewOptions(), progress, CancellationToken.None);

        Assert.True(reports.Count <= 10, $"Expected throttled progress reports, got {reports.Count}.");
        Assert.Contains(reports, report => report.Phase == ScanPhase.Done);
    }

    private ScanOptions NewOptions()
    {
        return new ScanOptions
        {
            Folders = [_root],
        };
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private string WriteFile(string relativePath, string contents)
    {
        return WriteFile(relativePath, System.Text.Encoding.UTF8.GetBytes(contents));
    }

    private string WriteFile(string relativePath, byte[] contents)
    {
        string fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, contents);
        return Path.GetFullPath(fullPath);
    }

    private string WriteFile(string relativePath, int length, byte value)
    {
        byte[] contents = new byte[length];
        Array.Fill(contents, value);
        return WriteFile(relativePath, contents);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private sealed class ConstantHasher : IFileHasher
    {
        public Task<ulong> HashAsync(
            string path,
            long expectedSizeBytes,
            long maxBytesToRead,
            Action<long>? bytesRead,
            CancellationToken cancellationToken)
        {
            bytesRead?.Invoke(Math.Min(expectedSizeBytes, maxBytesToRead));
            return Task.FromResult(42UL);
        }
    }

    private sealed class CountingArrayPool : ArrayPool<byte>
    {
        public int RentCount { get; private set; }

        public int ReturnCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnCount++;
        }
    }

    private sealed class FailingHasher : IFileHasher
    {
        private readonly string _failingPath;

        public FailingHasher(string failingPath)
        {
            _failingPath = failingPath;
        }

        public Task<ulong> HashAsync(
            string path,
            long expectedSizeBytes,
            long maxBytesToRead,
            Action<long>? bytesRead,
            CancellationToken cancellationToken)
        {
            if (string.Equals(path, _failingPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("File changed during scan.");
            }

            bytesRead?.Invoke(Math.Min(expectedSizeBytes, maxBytesToRead));
            return Task.FromResult(99UL);
        }
    }
}
