using System.Runtime.InteropServices;
using System.Reflection;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Tests;

public sealed class SimilarImageAnalyzerTests : IDisposable
{
    private static readonly DateTime FixedModifiedUtc = new(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.Engine.Tests",
        Guid.NewGuid().ToString("N"));

    public SimilarImageAnalyzerTests()
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonpositiveMaximumConcurrency(int maximumConcurrency)
    {
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
            Activator.CreateInstance(
                typeof(SimilarImageAnalyzer),
                new FakeImageSampleProvider(),
                maximumConcurrency));

        var error = Assert.IsType<ArgumentOutOfRangeException>(failure.InnerException);
        Assert.Equal(nameof(maximumConcurrency), error.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task AnalyzeAsync_UsesConfiguredBoundedConcurrency(int maximumConcurrency)
    {
        InventoryFile[] files = Enumerable.Range(0, maximumConcurrency + 2)
            .Select(index => WriteInventoryFile($"bound-{index:00}.jpg", 1))
            .ToArray();
        var provider = new BlockingImageSampleProvider(files.Select(static file => file.FullPath));
        Task<AnalysisResult> run = new SimilarImageAnalyzer(provider, maximumConcurrency).AnalyzeAsync(
            NewInventory(files),
            new SimilarImageOptions(8),
            CancellationToken.None);

        Exception? entryFailure = await Record.ExceptionAsync(() =>
            provider.WaitForEntriesAsync(maximumConcurrency).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        provider.CompleteAll(Sample(100, 100, Filled(0), "JPEG"));
        AnalysisResult result = await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Null(entryFailure);
        Assert.Equal(maximumConcurrency, provider.MaximumObserved);
        Assert.Empty(result.SkippedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_ParallelCancellationPropagatesInsteadOfBecomingSkips()
    {
        InventoryFile[] files = Enumerable.Range(0, 3)
            .Select(index => WriteInventoryFile($"cancel-parallel-{index:00}.jpg", 1))
            .ToArray();
        var provider = new BlockingImageSampleProvider(files.Select(static file => file.FullPath));
        using var cancellation = new CancellationTokenSource();
        Task<AnalysisResult> run = new SimilarImageAnalyzer(provider, 2).AnalyzeAsync(
            NewInventory(files),
            new SimilarImageOptions(8),
            cancellation.Token);

        Exception? entryFailure = await Record.ExceptionAsync(() =>
            provider.WaitForEntriesAsync(2).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Null(entryFailure);
        Assert.Equal(2, provider.CancellationCount);
    }

    [Fact]
    public void GroupingBoundary_PropagatesCancellation()
    {
        MethodInfo? buildGroups = typeof(SimilarImageAnalyzer)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(method => method.Name == "BuildGroups" && method.GetParameters().Length == 3);
        Assert.NotNull(buildGroups);
        Type? candidateType = typeof(SimilarImageAnalyzer).GetNestedType("Candidate", BindingFlags.NonPublic);
        Assert.NotNull(candidateType);
        Array candidates = Array.CreateInstance(candidateType, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
            buildGroups.Invoke(
                null,
                [candidates, new SimilarImageOptions(8), cancellation.Token]));

        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task AnalyzeAsync_ParallelCompletionOrderDoesNotChangeSkipOrder()
    {
        InventoryFile[] files = Enumerable.Range(0, 4)
            .Select(index => WriteInventoryFile($"ordered-{index:00}.jpg", 1))
            .Reverse()
            .ToArray();
        var provider = new BlockingImageSampleProvider(files.Select(static file => file.FullPath));
        Task<AnalysisResult> run = new SimilarImageAnalyzer(provider, 4).AnalyzeAsync(
            NewInventory(files),
            new SimilarImageOptions(8),
            CancellationToken.None);

        Exception? entryFailure = await Record.ExceptionAsync(() =>
            provider.WaitForEntriesAsync(4).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        foreach (InventoryFile file in files.OrderByDescending(static file => file.FullPath, StringComparer.Ordinal))
        {
            provider.Complete(file.FullPath, Sample(0, 100, Filled(0), "JPEG"));
        }

        AnalysisResult result = await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Null(entryFailure);
        Assert.Equal(
            files.Select(static file => file.FullPath).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase),
            result.SkippedPaths.Select(static skip => skip.Path));
    }

    [Fact]
    public async Task AnalyzeAsync_GroupsSamePixelsAcrossEncodingAndResizeWithoutAutomaticSelectionState()
    {
        InventoryFile jpg = WriteInventoryFile("small.jpg", 10);
        InventoryFile png = WriteInventoryFile("large.png", 20);
        InventoryFile unrelated = WriteInventoryFile("unrelated.webp", 30);
        var provider = new FakeImageSampleProvider();
        provider.Cached[jpg.FullPath] = (_, _) => Task.FromResult(Sample(100, 50, Filled(90), "JPEG"));
        provider.Cached[png.FullPath] = (_, _) => Task.FromResult(Sample(400, 200, Filled(90), "PNG"));
        provider.Cached[unrelated.FullPath] = (_, _) => Task.FromResult(Sample(100, 50, Gradient(), "WEBP"));

        AnalysisResult result = await new SimilarImageAnalyzer(provider).AnalyzeAsync(
            NewInventory([unrelated, jpg, png]),
            new SimilarImageOptions(8),
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Equal("image-0001", group.Id);
        Assert.Equal(png.FullPath, group.ReferenceItem.FullPath);
        Assert.Equal([png.FullPath, jpg.FullPath], group.Items.Select(static item => item.FullPath));
        Assert.Empty(result.Findings);
        Assert.Empty(result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Equal([png.FullPath, jpg.FullPath, unrelated.FullPath], provider.CachedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_FiltersToOrdinaryImageInventoryAndPreservesInventorySkipsFirst()
    {
        InventoryFile image = WriteInventoryFile("photo.PNG", 4);
        InventoryFile text = WriteInventoryFile("notes.txt", 4);
        InventoryFile linked = NewInventoryFile(
            Path.Combine(_root, "linked.jpg"),
            4,
            FileAttributes.ReparsePoint);
        InventoryFile directory = NewInventoryFile(
            Path.Combine(_root, "folder.jpg"),
            0,
            FileAttributes.Directory);
        var provider = new FakeImageSampleProvider();
        provider.Cached[image.FullPath] = (_, _) => Task.FromException<ImageSample>(new IOException("decode"));
        var inventorySkip = new SkippedPath { Path = "inventory", Reason = "Existing" };

        AnalysisResult result = await new SimilarImageAnalyzer(provider).AnalyzeAsync(
            NewInventory([text, linked, directory, image], [inventorySkip]),
            new SimilarImageOptions(8),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.SkippedPaths.Count);
        Assert.Same(inventorySkip, result.SkippedPaths[0]);
        Assert.Equal(image.FullPath, result.SkippedPaths[1].Path);
        Assert.Equal("Could not decode image.", result.SkippedPaths[1].Reason);
        Assert.Equal([image.FullPath], provider.CachedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_MapsInvalidSamplesAndExpectedProviderFailuresThenContinues()
    {
        InventoryFile invalid = WriteInventoryFile("a.png", 1);
        InventoryFile io = WriteInventoryFile("b.jpg", 1);
        InventoryFile com = WriteInventoryFile("c.webp", 1);
        InventoryFile valid = WriteInventoryFile("d.bmp", 1);
        var provider = new FakeImageSampleProvider();
        provider.Cached[invalid.FullPath] = (_, _) => Task.FromResult(Sample(0, 10, Filled(1), "PNG"));
        provider.Cached[io.FullPath] = (_, _) => Task.FromException<ImageSample>(new IOException("io"));
        provider.Cached[com.FullPath] = (_, _) => Task.FromException<ImageSample>(new COMException("com"));
        provider.Cached[valid.FullPath] = (_, _) => Task.FromResult(Sample(10, 10, Filled(1), "BMP"));

        AnalysisResult result = await new SimilarImageAnalyzer(provider).AnalyzeAsync(
            NewInventory([valid, com, io, invalid]),
            new SimilarImageOptions(8),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal([invalid.FullPath, io.FullPath, com.FullPath], result.SkippedPaths.Select(static skip => skip.Path));
        Assert.All(result.SkippedPaths, static skip => Assert.Equal("Could not decode image.", skip.Reason));
        Assert.Equal([invalid.FullPath, io.FullPath, com.FullPath, valid.FullPath], provider.CachedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_ChangedBeforeDecodeWinsWithoutProviderCall()
    {
        InventoryFile file = WriteInventoryFile("changed-before.jpg", 2);
        File.WriteAllBytes(file.FullPath, [1, 2, 3]);
        var provider = new FakeImageSampleProvider();

        AnalysisResult result = await new SimilarImageAnalyzer(provider).AnalyzeAsync(
            NewInventory([file]),
            new SimilarImageOptions(8),
            CancellationToken.None);

        SkippedPath skipped = Assert.Single(result.SkippedPaths);
        Assert.Equal(file.FullPath, skipped.Path);
        Assert.Equal("File changed since scan.", skipped.Reason);
        Assert.Empty(provider.CachedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_ChangedDuringFailedDecodeWinsOverProviderFailure()
    {
        InventoryFile file = WriteInventoryFile("changed-during.png", 2);
        var provider = new FakeImageSampleProvider();
        provider.Cached[file.FullPath] = (path, _) =>
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            return Task.FromException<ImageSample>(new IOException("decode"));
        };

        AnalysisResult result = await new SimilarImageAnalyzer(provider).AnalyzeAsync(
            NewInventory([file]),
            new SimilarImageOptions(8),
            CancellationToken.None);

        SkippedPath skipped = Assert.Single(result.SkippedPaths);
        Assert.Equal("File changed since scan.", skipped.Reason);
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellationBeforeAndAfterProviderCall()
    {
        InventoryFile file = WriteInventoryFile("cancel.jpg", 1);
        using var before = new CancellationTokenSource();
        before.Cancel();
        var unused = new FakeImageSampleProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SimilarImageAnalyzer(unused).AnalyzeAsync(
                NewInventory([file]),
                new SimilarImageOptions(8),
                before.Token));
        Assert.Empty(unused.CachedPaths);

        using var after = new CancellationTokenSource();
        var provider = new FakeImageSampleProvider();
        provider.Cached[file.FullPath] = (_, _) =>
        {
            after.Cancel();
            return Task.FromResult(Sample(10, 10, Filled(1), "JPEG"));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SimilarImageAnalyzer(provider).AnalyzeAsync(
                NewInventory([file]),
                new SimilarImageOptions(8),
                after.Token));
        Assert.Single(provider.CachedPaths);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(13)]
    public async Task AnalyzeAsync_RejectsUnsupportedDistanceBeforeProviderWork(int maximumDistance)
    {
        InventoryFile file = WriteInventoryFile("distance.jpg", 1);
        var provider = new FakeImageSampleProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new SimilarImageAnalyzer(provider).AnalyzeAsync(
                NewInventory([file]),
                new SimilarImageOptions(maximumDistance),
                CancellationToken.None));

        Assert.Empty(provider.CachedPaths);
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 5, false)]
    [InlineData(8, 8, true)]
    [InlineData(8, 9, false)]
    [InlineData(12, 12, true)]
    [InlineData(12, 13, false)]
    public void Regroup_UsesEveryPresetBoundary(int maximumDistance, int actualDistance, bool grouped)
    {
        IReadOnlyList<SimilarityGroup> groups = new SimilarImageAnalyzer(new FakeImageSampleProvider()).Regroup(
            [EvidenceItem(@"C:\images\a.jpg", 0), EvidenceItem(@"C:\images\b.jpg", LowBits(actualDistance))],
            new SimilarImageOptions(maximumDistance));

        Assert.Equal(grouped, groups.Count == 1);
    }

    [Fact]
    public void Regroup_IncludesTheExactSymmetricAspectBoundaryAndExcludesBeyondIt()
    {
        var analyzer = new SimilarImageAnalyzer(new FakeImageSampleProvider());
        SimilarityItem square = EvidenceItem(@"C:\images\square.jpg", 0, width: 19, height: 19);

        Assert.Single(analyzer.Regroup(
            [square, EvidenceItem(@"C:\images\boundary.jpg", 0, width: 20, height: 19)],
            new SimilarImageOptions(0)));
        Assert.Empty(analyzer.Regroup(
            [square, EvidenceItem(@"C:\images\beyond.jpg", 0, width: 21, height: 19)],
            new SimilarImageOptions(0)));
    }

    [Fact]
    public void Regroup_BandIndexFindsAThresholdTwelvePairDifferingInEveryFiveBitBand()
    {
        ulong oneBitPerFiveBitBand = 0;
        for (int bit = 0; bit <= 55; bit += 5)
        {
            oneBitPerFiveBitBand |= 1UL << bit;
        }

        SimilarityGroup group = Assert.Single(new SimilarImageAnalyzer(new FakeImageSampleProvider()).Regroup(
            [EvidenceItem(@"C:\images\a.jpg", 0), EvidenceItem(@"C:\images\b.jpg", oneBitPerFiveBitBand)],
            new SimilarImageOptions(12)));

        Assert.Equal("12", group.Items[1].Metadata["HammingDistance"]);
    }

    [Fact]
    public void Regroup_UsesConnectedComponentsEvenWhenAReferenceRelativePairExceedsTheThreshold()
    {
        SimilarityItem reference = EvidenceItem(@"C:\images\a.jpg", 0, width: 300, height: 300);
        SimilarityItem bridge = EvidenceItem(@"C:\images\b.jpg", LowBits(8), width: 200, height: 200);
        SimilarityItem tail = EvidenceItem(@"C:\images\c.jpg", LowBits(16), width: 100, height: 100);

        SimilarityGroup group = Assert.Single(new SimilarImageAnalyzer(new FakeImageSampleProvider()).Regroup(
            [tail, reference, bridge],
            new SimilarImageOptions(8)));

        Assert.Equal([reference.FullPath, bridge.FullPath, tail.FullPath], group.Items.Select(static item => item.FullPath));
        Assert.Equal("16", group.Items[2].Metadata["HammingDistance"]);
        Assert.Equal(75d, group.Items[2].SimilarityPercent);
    }

    [Fact]
    public void Regroup_RebuildsStableReferencesOrderingIdsMetadataScoresAndEvidence()
    {
        SimilarityItem zReference = EvidenceItem(@"C:\z\ref.jpg", 0, width: 300, height: 200, size: 20, format: "JPEG");
        SimilarityItem zNear = EvidenceItem(@"C:\z\near.png", 1UL << 7, width: 150, height: 100, size: 50, format: "PNG");
        SimilarityItem zFar = EvidenceItem(@"C:\z\far.webp", 3UL, width: 150, height: 100, size: 60, format: "WEBP");
        SimilarityItem aBySize = EvidenceItem(@"C:\a\large.png", 0xFFFF000000000000, width: 100, height: 100, size: 200, format: "PNG");
        SimilarityItem aSmaller = EvidenceItem(@"C:\a\small.jpg", 0xFFFF000000000001, width: 100, height: 100, size: 100, format: "JPEG");

        IReadOnlyList<SimilarityGroup> groups = new SimilarImageAnalyzer(new FakeImageSampleProvider()).Regroup(
            [zFar, aSmaller, zNear, zReference, aBySize],
            new SimilarImageOptions(4));

        Assert.Equal(2, groups.Count);
        Assert.Equal(["image-0001", "image-0002"], groups.Select(static group => group.Id));
        Assert.Equal(aBySize.FullPath, groups[0].ReferenceItem.FullPath);
        Assert.Equal(zReference.FullPath, groups[1].ReferenceItem.FullPath);
        Assert.Equal([zReference.FullPath, zNear.FullPath, zFar.FullPath], groups[1].Items.Select(static item => item.FullPath));
        Assert.Equal([100d, 98.4375d, 96.875d], groups[1].Items.Select(static item => item.SimilarityPercent));
        Assert.Equal("Image", groups[1].Metadata["Type"]);
        Assert.Equal("4", groups[1].Metadata["MaximumHammingDistance"]);
        Assert.Equal(
            ["Width", "Height", "Format", "HammingDistance"],
            groups[1].Items[1].Metadata.Keys);
        Assert.Equal("150", groups[1].Items[1].Metadata["Width"]);
        Assert.Equal("100", groups[1].Items[1].Metadata["Height"]);
        Assert.Equal("PNG", groups[1].Items[1].Metadata["Format"]);
        Assert.Equal("1", groups[1].Items[1].Metadata["HammingDistance"]);
        Assert.Equal(zNear.Evidence, groups[1].Items[1].Evidence);
    }

    [Fact]
    public void Regroup_SplitsAChainAndChoosesAReplacementReferenceFromSurvivors()
    {
        var analyzer = new SimilarImageAnalyzer(new FakeImageSampleProvider());
        SimilarityItem oldReference = EvidenceItem(@"C:\images\a.jpg", 0, width: 300, height: 300);
        SimilarityItem bridge = EvidenceItem(@"C:\images\b.jpg", LowBits(8), width: 200, height: 200, size: 30);
        SimilarityItem tail = EvidenceItem(@"C:\images\c.jpg", LowBits(16), width: 100, height: 100, size: 40);

        Assert.Empty(analyzer.Regroup([oldReference, tail], new SimilarImageOptions(8)));
        SimilarityGroup regrouped = Assert.Single(analyzer.Regroup([bridge, tail], new SimilarImageOptions(8)));

        Assert.Equal(bridge.FullPath, regrouped.ReferenceItem.FullPath);
        Assert.Equal("0", regrouped.ReferenceItem.Metadata["HammingDistance"]);
        Assert.Equal(100d, regrouped.ReferenceItem.SimilarityPercent);
    }

    [Fact]
    public async Task RevalidateAsync_UsesOnlyFreshSamplingAndRequiresExactEvidenceEquality()
    {
        var provider = new FakeImageSampleProvider();
        SimilarityItem item = EvidenceItem(@"C:\images\photo.jpg", 0, width: 100, height: 50, format: "JPEG");
        provider.Fresh[item.FullPath] = (_, _) => Task.FromResult(Sample(100, 50, Filled(0), "JPEG"));

        Assert.True(await new SimilarImageAnalyzer(provider).RevalidateAsync(item, CancellationToken.None));

        provider.Fresh[item.FullPath] = (_, _) => Task.FromResult(Sample(100, 50, Filled(128), "JPEG"));
        Assert.False(await new SimilarImageAnalyzer(provider).RevalidateAsync(item, CancellationToken.None));
        Assert.Empty(provider.CachedPaths);
        Assert.Equal([item.FullPath, item.FullPath], provider.FreshPaths);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsInvalidFreshShapeAndPropagatesProviderFailureAndCancellation()
    {
        SimilarityItem item = EvidenceItem(@"C:\images\photo.jpg", 0);
        var invalid = new FakeImageSampleProvider();
        invalid.Fresh[item.FullPath] = (_, _) => Task.FromResult(Sample(10, 10, new byte[1023], "JPEG"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SimilarImageAnalyzer(invalid).RevalidateAsync(item, CancellationToken.None));

        var failed = new FakeImageSampleProvider();
        failed.Fresh[item.FullPath] = (_, _) => Task.FromException<ImageSample>(new IOException("decode"));
        await Assert.ThrowsAsync<IOException>(() =>
            new SimilarImageAnalyzer(failed).RevalidateAsync(item, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new FakeImageSampleProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SimilarImageAnalyzer(cancelled).RevalidateAsync(item, cancellation.Token));
        Assert.Empty(cancelled.FreshPaths);
    }

    private InventoryFile WriteInventoryFile(string name, int size)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(static value => (byte)value).ToArray());
        File.SetLastWriteTimeUtc(path, FixedModifiedUtc);
        return NewInventoryFile(path, size, FileAttributes.Normal);
    }

    private static InventoryFile NewInventoryFile(string path, long size, FileAttributes attributes)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new InventoryFile(
            path,
            Path.GetFileName(path),
            Path.GetExtension(path).ToLowerInvariant(),
            Path.GetDirectoryName(path) ?? string.Empty,
            size,
            info.Exists ? info.CreationTimeUtc : FixedModifiedUtc,
            info.Exists ? info.LastWriteTimeUtc : FixedModifiedUtc,
            attributes);
    }

    private static FileInventory NewInventory(
        IReadOnlyList<InventoryFile> files,
        IReadOnlyList<SkippedPath>? skips = null) => new(files, [], [], [], skips ?? []);

    private static ImageSample Sample(int width, int height, byte[] luminance, string format) =>
        new(width, height, luminance, format);

    private static SimilarityItem EvidenceItem(
        string path,
        ulong hash,
        int width = 100,
        int height = 100,
        long size = 10,
        string format = "JPEG") => new()
        {
            FullPath = path,
            SizeBytes = size,
            ModifiedUtc = FixedModifiedUtc,
            SimilarityPercent = -1,
            Metadata = new Dictionary<string, string> { ["Stale"] = "discard" },
            Evidence = new ImageSimilarityEvidence(hash, width, height, format),
        };

    private static ulong LowBits(int count) => count == 64 ? ulong.MaxValue : (1UL << count) - 1;

    private static byte[] Filled(byte value)
    {
        var bytes = new byte[1024];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static byte[] Gradient()
    {
        var bytes = new byte[1024];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bytes[(y * 32) + x] = (byte)(10 + (((x + y) * 180) / 62));
            }
        }

        return bytes;
    }

    private sealed class FakeImageSampleProvider : IImageSampleProvider
    {
        public Dictionary<string, Func<string, CancellationToken, Task<ImageSample>>> Cached { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Func<string, CancellationToken, Task<ImageSample>>> Fresh { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> CachedPaths { get; } = [];

        public List<string> FreshPaths { get; } = [];

        public Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken)
        {
            CachedPaths.Add(path);
            return Cached.TryGetValue(path, out Func<string, CancellationToken, Task<ImageSample>>? handler)
                ? handler(path, cancellationToken)
                : Task.FromResult(Sample(100, 100, Filled(0), "JPEG"));
        }

        public Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken)
        {
            FreshPaths.Add(path);
            return Fresh.TryGetValue(path, out Func<string, CancellationToken, Task<ImageSample>>? handler)
                ? handler(path, cancellationToken)
                : Task.FromResult(Sample(100, 100, Filled(0), "JPEG"));
        }
    }

    private sealed class BlockingImageSampleProvider(IEnumerable<string> paths) : IImageSampleProvider
    {
        private readonly BlockingProviderGate<ImageSample> _gate = new(paths);

        public int MaximumObserved => _gate.MaximumObserved;

        public int CancellationCount => _gate.CancellationCount;

        public Task WaitForEntriesAsync(int count) => _gate.WaitForEntriesAsync(count);

        public void Complete(string path, ImageSample sample) => _gate.Complete(path, sample);

        public void CompleteAll(ImageSample sample) => _gate.CompleteAll(_ => sample);

        public Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken) =>
            _gate.GetAsync(path, cancellationToken);

        public Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

internal sealed class BlockingProviderGate<T>(IEnumerable<string> paths)
{
    private readonly IReadOnlyDictionary<string, TaskCompletionSource<T>> _completions = paths.ToDictionary(
        static path => path,
        static _ => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously),
        StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _entries = new(0);
    private int _active;
    private int _cancellationCount;
    private int _maximumObserved;

    public int CancellationCount => Volatile.Read(ref _cancellationCount);

    public int MaximumObserved => Volatile.Read(ref _maximumObserved);

    public async Task<T> GetAsync(string path, CancellationToken cancellationToken)
    {
        int active = Interlocked.Increment(ref _active);
        UpdateMaximum(active);
        _entries.Release();
        try
        {
            return await _completions[path].Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancellationCount);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public async Task WaitForEntriesAsync(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await _entries.WaitAsync();
        }
    }

    public void Complete(string path, T value) => _completions[path].TrySetResult(value);

    public void CompleteAll(Func<string, T> valueFactory)
    {
        foreach ((string path, TaskCompletionSource<T> completion) in _completions)
        {
            completion.TrySetResult(valueFactory(path));
        }
    }

    private void UpdateMaximum(int active)
    {
        int observed = Volatile.Read(ref _maximumObserved);
        while (active > observed)
        {
            int prior = Interlocked.CompareExchange(ref _maximumObserved, active, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }
}
