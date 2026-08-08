using System.Text.Json;
using System.Text.Json.Nodes;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class MediaFingerprintCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Duplicates.App.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _cachePath;

    public MediaFingerprintCacheTests()
    {
        Directory.CreateDirectory(_root);
        _cachePath = Path.Combine(_root, "cache", "media-fingerprints-v1.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageHit_UsesCanonicalSnapshotKeyAndReturnsIsolatedCopies()
    {
        string sourcePath = await WriteSourceAsync("image.png", [1, 2, 3]);
        string aliasDirectory = Path.Combine(_root, "alias");
        Directory.CreateDirectory(aliasDirectory);
        string aliasPath = Path.Combine(aliasDirectory, "..", "image.png");
        var cache = new MediaFingerprintCache(_cachePath);
        int calls = 0;
        ImageSample created = Image(17);

        ImageSample first = await cache.GetOrCreateImageAsync(aliasPath, _ =>
        {
            calls++;
            return Task.FromResult(created);
        }, CancellationToken.None);
        first.Luminance32x32[0] = 99;
        created.Luminance32x32[1] = 88;
        ImageSample second = await cache.GetOrCreateImageAsync(sourcePath, _ =>
        {
            calls++;
            return Task.FromResult(Image(42));
        }, CancellationToken.None);
        second.Luminance32x32[2] = 77;
        ImageSample third = await cache.GetOrCreateImageAsync(
            sourcePath, _ => Task.FromResult(Image(43)), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(17, second.Luminance32x32[0]);
        Assert.Equal(17, second.Luminance32x32[1]);
        Assert.Equal(17, third.Luminance32x32[2]);
        Assert.NotSame(first.Luminance32x32, second.Luminance32x32);
        Assert.Equal(Path.GetFullPath(_cachePath), cache.CachePath);
    }

    [Fact]
    public async Task VideoHit_UsesSeparateMediaKeyAndDeepClonesEveryFrame()
    {
        string path = await WriteSourceAsync("media.bin", [1]);
        var cache = new MediaFingerprintCache(_cachePath);
        int imageCalls = 0;
        int videoCalls = 0;
        _ = await cache.GetOrCreateImageAsync(path, _ =>
        {
            imageCalls++;
            return Task.FromResult(Image(3));
        }, CancellationToken.None);
        VideoSample first = await cache.GetOrCreateVideoAsync(path, _ =>
        {
            videoCalls++;
            return Task.FromResult(Video(10));
        }, CancellationToken.None);
        foreach (byte[] frame in first.LuminanceFrames32x32)
        {
            frame[0] = 99;
        }

        VideoSample second = await cache.GetOrCreateVideoAsync(path, _ =>
        {
            videoCalls++;
            return Task.FromResult(Video(40));
        }, CancellationToken.None);

        Assert.Equal(1, imageCalls);
        Assert.Equal(1, videoCalls);
        Assert.Equal([10, 11, 12, 13, 14], second.LuminanceFrames32x32.Select(frame => (int)frame[0]));
        Assert.Equal(5, second.LuminanceFrames32x32.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(first.LuminanceFrames32x32.Zip(second.LuminanceFrames32x32), pair =>
            Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public async Task ImageEntry_MissesAfterLengthChanges()
    {
        string path = await WriteSourceAsync("length.png", [1]);
        var cache = new MediaFingerprintCache(_cachePath);
        int calls = 0;
        Task<ImageSample> Create(CancellationToken _) => Task.FromResult(Image((byte)++calls));

        _ = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);
        await File.WriteAllBytesAsync(path, [1, 2]);
        ImageSample refreshed = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(2, refreshed.Luminance32x32[0]);
    }

    [Fact]
    public async Task ImageEntry_MissesAfterLastWriteTimeChanges()
    {
        string path = await WriteSourceAsync("mtime.png", [1]);
        var cache = new MediaFingerprintCache(_cachePath);
        int calls = 0;
        Task<ImageSample> Create(CancellationToken _) => Task.FromResult(Image((byte)++calls));

        _ = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        ImageSample refreshed = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(2, refreshed.Luminance32x32[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImageEntry_MissesAfterDocumentOrSampleSchemaChanges(bool documentSchema)
    {
        string path = await WriteSourceAsync("schema.png", [1]);
        int calls = 0;
        Task<ImageSample> Create(CancellationToken _) => Task.FromResult(Image((byte)++calls));
        _ = await new MediaFingerprintCache(_cachePath).GetOrCreateImageAsync(
            path, Create, CancellationToken.None);
        JsonObject document = await ReadDocumentAsync();
        if (documentSchema)
        {
            document["schemaVersion"] = 999;
        }
        else
        {
            document["imageEntries"]!.AsArray()[0]!["sampleSchemaVersion"] = 999;
        }

        await WriteDocumentAsync(document);
        ImageSample refreshed = await new MediaFingerprintCache(_cachePath).GetOrCreateImageAsync(
            path, Create, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(2, refreshed.Luminance32x32[0]);
    }

    [Fact]
    public async Task Load_PrunesMissingAndChangedEntriesOnNextSuccessfulWrite()
    {
        string missing = await WriteSourceAsync("missing.png", [1]);
        string changed = await WriteSourceAsync("changed.png", [2]);
        string added = await WriteSourceAsync("added.png", [3]);
        var writer = new MediaFingerprintCache(_cachePath);
        _ = await writer.GetOrCreateImageAsync(missing, _ => Task.FromResult(Image(1)), CancellationToken.None);
        _ = await writer.GetOrCreateImageAsync(changed, _ => Task.FromResult(Image(2)), CancellationToken.None);
        File.Delete(missing);
        await File.WriteAllBytesAsync(changed, [2, 2]);

        _ = await new MediaFingerprintCache(_cachePath).GetOrCreateImageAsync(
            added, _ => Task.FromResult(Image(3)), CancellationToken.None);

        string[] paths = GetImagePaths(await ReadDocumentAsync());
        Assert.DoesNotContain(paths, path => string.Equals(
            path,
            Path.GetFullPath(missing),
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, path => string.Equals(
            path,
            Path.GetFullPath(changed),
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path => string.Equals(
            path,
            Path.GetFullPath(added),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Load_MalformedJsonRecoversWithValidDocument()
    {
        string path = await WriteSourceAsync("malformed.png", [1]);
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await File.WriteAllTextAsync(_cachePath, "{not json");

        ImageSample result = await new MediaFingerprintCache(_cachePath).GetOrCreateImageAsync(
            path, _ => Task.FromResult(Image(7)), CancellationToken.None);

        Assert.Equal(7, result.Luminance32x32[0]);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(_cachePath));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Load_InvalidSampleShapeIsMissAndIsReplaced()
    {
        string path = await WriteSourceAsync("invalid-shape.png", [1]);
        int calls = 0;
        Task<ImageSample> Create(CancellationToken _) => Task.FromResult(Image((byte)++calls));
        _ = await new MediaFingerprintCache(_cachePath).GetOrCreateImageAsync(
            path, Create, CancellationToken.None);
        JsonObject document = await ReadDocumentAsync();
        document["imageEntries"]!.AsArray()[0]!["sample"]!["luminance32x32"] = "AQ==";
        await WriteDocumentAsync(document);

        ImageSample refreshed = await new MediaFingerprintCache(_cachePath).GetOrCreateImageAsync(
            path, Create, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(2, refreshed.Luminance32x32[0]);
    }

    [Fact]
    public async Task ChangedDuringFactory_ReturnsWithoutPersisting()
    {
        string path = await WriteSourceAsync("changing.png", [1]);
        var cache = new MediaFingerprintCache(_cachePath);
        ImageSample result = await cache.GetOrCreateImageAsync(path, async _ =>
        {
            await File.WriteAllBytesAsync(path, [1, 2]);
            return Image(4);
        }, CancellationToken.None);

        Assert.Equal(4, result.Luminance32x32[0]);
        Assert.False(File.Exists(_cachePath));
        int calls = 0;
        _ = await cache.GetOrCreateImageAsync(path, _ =>
        {
            calls++;
            return Task.FromResult(Image(5));
        }, CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvalidFactorySamples_AreRejectedWithoutSaving()
    {
        string imagePath = await WriteSourceAsync("invalid-image.png", [1]);
        string videoPath = await WriteSourceAsync("invalid-video.mp4", [2]);
        var cache = new MediaFingerprintCache(_cachePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetOrCreateImageAsync(
            imagePath, _ => Task.FromResult(new ImageSample(1, 1, [1], "PNG")), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetOrCreateVideoAsync(
            videoPath,
            _ => Task.FromResult(Video(3) with { LuminanceFrames32x32 = [new byte[1024]] }),
            CancellationToken.None));
        Assert.False(File.Exists(_cachePath));
    }

    [Fact]
    public async Task VideoFactorySample_RejectsWhitespaceCodecWithoutSaving()
    {
        string path = await WriteSourceAsync("missing-codec.mp4", [1]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MediaFingerprintCache(_cachePath).GetOrCreateVideoAsync(
                path,
                _ => Task.FromResult(Video(3) with { Codec = " " }),
                CancellationToken.None));

        Assert.False(File.Exists(_cachePath));
    }

    [Fact]
    public async Task CancellationAfterFactory_PreservesPriorFinalAndLeavesNoTemporaryFile()
    {
        string oldPath = await WriteSourceAsync("old.png", [1]);
        string cancelledPath = await WriteSourceAsync("cancelled.png", [2]);
        var cache = new MediaFingerprintCache(_cachePath);
        _ = await cache.GetOrCreateImageAsync(oldPath, _ => Task.FromResult(Image(1)), CancellationToken.None);
        byte[] original = await File.ReadAllBytesAsync(_cachePath);
        using var cancellationSource = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateImageAsync(
            cancelledPath,
            _ =>
            {
                cancellationSource.Cancel();
                return Task.FromResult(Image(2));
            },
            cancellationSource.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(_cachePath));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task CancellationBeforeCommit_PreservesFinalAndMemoryAndDeletesOwnedTemporaryFile()
    {
        string oldPath = await WriteSourceAsync("old-before-commit.png", [1]);
        string cancelledPath = await WriteSourceAsync("cancel-before-commit.png", [2]);
        using var cancellationSource = new CancellationTokenSource();
        bool flushedTemporaryObserved = false;
        var cache = new MediaFingerprintCache(_cachePath, (temporaryPath, token) =>
        {
            if (token.CanBeCanceled)
            {
                Assert.True(File.Exists(temporaryPath));
                using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(temporaryPath));
                flushedTemporaryObserved = true;
                cancellationSource.Cancel();
            }

            return Task.CompletedTask;
        });
        _ = await cache.GetOrCreateImageAsync(
            oldPath,
            _ => Task.FromResult(Image(1)),
            CancellationToken.None);
        byte[] oldFinal = await File.ReadAllBytesAsync(_cachePath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateImageAsync(
            cancelledPath,
            _ => Task.FromResult(Image(2)),
            cancellationSource.Token));

        Assert.True(flushedTemporaryObserved);
        Assert.Equal(oldFinal, await File.ReadAllBytesAsync(_cachePath));
        Assert.Empty(TemporaryFiles());
        int oldFactoryCalls = 0;
        ImageSample oldHit = await cache.GetOrCreateImageAsync(oldPath, _ =>
        {
            oldFactoryCalls++;
            return Task.FromResult(Image(9));
        }, CancellationToken.None);
        Assert.Equal(0, oldFactoryCalls);
        Assert.Equal(1, oldHit.Luminance32x32[0]);
    }

    [Fact]
    public async Task WriteFailure_ReturnsSampleAndCleansOwnedTemporaryFile()
    {
        string path = await WriteSourceAsync("write-failure.png", [1]);
        Directory.CreateDirectory(_cachePath);
        var cache = new MediaFingerprintCache(_cachePath);
        int calls = 0;
        Task<ImageSample> Create(CancellationToken _) => Task.FromResult(Image((byte)++calls));

        ImageSample result = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);
        _ = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);

        Assert.Equal(1, result.Luminance32x32[0]);
        Assert.Equal(2, calls);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task FactoriesForDifferentPaths_RunOutsideStateLock()
    {
        string firstPath = await WriteSourceAsync("first.png", [1]);
        string secondPath = await WriteSourceAsync("second.png", [2]);
        var cache = new MediaFingerprintCache(_cachePath);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        async Task<ImageSample> Create(byte value)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return Image(value);
        }

        await Task.WhenAll(
            cache.GetOrCreateImageAsync(firstPath, _ => Create(1), CancellationToken.None),
            cache.GetOrCreateImageAsync(secondPath, _ => Create(2), CancellationToken.None));

        Assert.Equal(2, started);
    }

    [Fact]
    public async Task ConcurrentInstances_LeaveValidOldOrNewFinalAndNoTemporaryFiles()
    {
        string firstPath = await WriteSourceAsync("instance-one.png", [1]);
        string secondPath = await WriteSourceAsync("instance-two.png", [2]);
        var firstCache = new MediaFingerprintCache(_cachePath);
        var secondCache = new MediaFingerprintCache(_cachePath);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        async Task<ImageSample> Create(byte value)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return Image(value);
        }

        await Task.WhenAll(
            firstCache.GetOrCreateImageAsync(firstPath, _ => Create(1), CancellationToken.None),
            secondCache.GetOrCreateImageAsync(secondPath, _ => Create(2), CancellationToken.None));

        string[] paths = GetImagePaths(await ReadDocumentAsync());
        Assert.True(
            paths.Contains(Path.GetFullPath(firstPath), StringComparer.OrdinalIgnoreCase) ||
            paths.Contains(Path.GetFullPath(secondPath), StringComparer.OrdinalIgnoreCase));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task ClearDuringFactory_PreventsOldGenerationFromRepopulatingCache()
    {
        string path = await WriteSourceAsync("in-flight.png", [1]);
        var cache = new MediaFingerprintCache(_cachePath);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<ImageSample> Create(CancellationToken _)
        {
            calls++;
            started.TrySetResult();
            await release.Task;
            return Image((byte)calls);
        }

        Task<ImageSample> inFlight = cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.ClearAsync(CancellationToken.None);
        release.TrySetResult();
        _ = await inFlight;

        Assert.False(File.Exists(_cachePath));
        _ = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Clear_RemovesFinalAndMemoryButPreservesForeignTemporaryFile()
    {
        string path = await WriteSourceAsync("clear.png", [1]);
        var cache = new MediaFingerprintCache(_cachePath);
        int calls = 0;
        Task<ImageSample> Create(CancellationToken _) => Task.FromResult(Image((byte)++calls));
        _ = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);
        string foreign = _cachePath + ".foreign.tmp";
        await File.WriteAllTextAsync(foreign, "foreign");

        await cache.ClearAsync(CancellationToken.None);
        _ = await cache.GetOrCreateImageAsync(path, Create, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Clear_ExistingDirectoryAtCachePathSurfacesDeletionFailure()
    {
        Directory.CreateDirectory(_cachePath);
        var cache = new MediaFingerprintCache(_cachePath);

        Exception? exception = await Record.ExceptionAsync(() =>
            cache.ClearAsync(CancellationToken.None));

        Assert.IsType<UnauthorizedAccessException>(exception);
    }

    private async Task<string> WriteSourceAsync(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private async Task<JsonObject> ReadDocumentAsync() =>
        JsonNode.Parse(await File.ReadAllTextAsync(_cachePath))!.AsObject();

    private Task WriteDocumentAsync(JsonObject document) =>
        File.WriteAllTextAsync(_cachePath, document.ToJsonString());

    private static string[] GetImagePaths(JsonObject document) => document["imageEntries"]!
        .AsArray()
        .Select(entry => entry!["path"]!.GetValue<string>())
        .ToArray();

    private string[] TemporaryFiles()
    {
        string? directory = Path.GetDirectoryName(_cachePath);
        return directory is null || !Directory.Exists(directory)
            ? []
            : Directory.GetFiles(directory, Path.GetFileName(_cachePath) + ".*.tmp");
    }

    private static ImageSample Image(byte value) =>
        new(64, 32, Enumerable.Repeat(value, 1024).ToArray(), "PNG");

    private static VideoSample Video(byte value) => new(
        1920,
        1080,
        16d / 9d,
        TimeSpan.FromSeconds(10),
        1_000_000,
        30,
        "H264",
        Enumerable.Range(0, 5)
            .Select(index => Enumerable.Repeat((byte)(value + index), 1024).ToArray())
            .ToArray());
}
