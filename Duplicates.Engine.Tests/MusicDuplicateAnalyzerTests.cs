using System.Runtime.InteropServices;
using System.Security;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Tests;

public sealed class MusicDuplicateAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.Engine.Tests",
        Guid.NewGuid().ToString("N"));

    public MusicDuplicateAnalyzerTests()
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
    public void Options_RejectNegativeAndAcceptZeroAndTimeSpanMaxValue()
    {
        var analyzer = new MusicDuplicateAnalyzer(new FakeMusicMetadataProvider());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            analyzer.Regroup([], new MusicDuplicateOptions(TimeSpan.FromTicks(-1))));
        Assert.Empty(analyzer.Regroup([], new MusicDuplicateOptions(TimeSpan.Zero)));
        Assert.Empty(analyzer.Regroup([], new MusicDuplicateOptions(TimeSpan.MaxValue)));
    }

    [Fact]
    public async Task AnalyzeAsync_NormalizesFormKCCaseEveryPunctuationClassWhitespaceAndSupplementaryScalars()
    {
        InventoryFile first = WriteFile("first.mp3", size: 3);
        InventoryFile second = WriteFile("second.wma", size: 4);
        string decoratedTitle = " \tＦＯＯ\u203Fbar\u2014baz(qux)«zap»!zip  😀é♥ ";
        var provider = new FakeMusicMetadataProvider();
        provider.Results[first.FullPath] = Music(
            decoratedTitle,
            artist: "  THE\tARTIST  ",
            albumArtist: "Ignored Album Artist",
            duration: TimeSpan.FromSeconds(100));
        provider.Results[second.FullPath] = Music(
            "foo bar baz qux zap zip 😀é♥",
            artist: "the artist",
            albumArtist: "Different Album Artist",
            duration: TimeSpan.FromSeconds(101));

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(first, second),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Items.Count);
        var evidence = Assert.IsType<MusicSimilarityEvidence>(
            group.Items.Single(item => item.FullPath == first.FullPath).Evidence);
        Assert.Equal("foo bar baz qux zap zip 😀é♥", evidence.NormalizedTitle);
        Assert.Equal("the artist", evidence.NormalizedArtist);
        Assert.Equal(decoratedTitle, evidence.Title);
        Assert.Equal("  THE\tARTIST  ", evidence.Artist);
        Assert.Empty(result.SkippedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_InvalidFieldsAreLocalAndArtistFallbackIsPerItem()
    {
        InventoryFile invalidTitle = WriteFile("invalid-title.mp3");
        InventoryFile fallbackOne = WriteFile("fallback-one.mp3");
        InventoryFile fallbackTwo = WriteFile("fallback-two.mp3");
        InventoryFile invalidUnusedAlbumArtist = WriteFile("valid-contributing.mp3");
        InventoryFile matchingContributing = WriteFile("matching-contributing.mp3");
        var provider = new FakeMusicMetadataProvider();
        provider.Results[invalidTitle.FullPath] = Music("\uD800", artist: "Artist");
        provider.Results[fallbackOne.FullPath] = Music("Song", artist: "\uD800", albumArtist: "Album Artist");
        provider.Results[fallbackTwo.FullPath] = Music("song", artist: string.Empty, albumArtist: "album artist");
        provider.Results[invalidUnusedAlbumArtist.FullPath] = Music(
            "Other",
            artist: "Contributing",
            albumArtist: "\uD800");
        provider.Results[matchingContributing.FullPath] = Music(
            "other",
            artist: "contributing",
            albumArtist: "Different");

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(invalidTitle, fallbackOne, fallbackTwo, invalidUnusedAlbumArtist, matchingContributing),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.Equal(2, result.Groups.Count);
        Assert.Contains(result.Groups, group => group.Items.Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals([fallbackOne.FullPath, fallbackTwo.FullPath]));
        SimilarityItem fallbackItem = result.Groups.SelectMany(static group => group.Items)
            .Single(item => item.FullPath == fallbackOne.FullPath);
        var fallbackEvidence = Assert.IsType<MusicSimilarityEvidence>(fallbackItem.Evidence);
        Assert.Equal("Album Artist", fallbackEvidence.Artist);
        Assert.Equal("Album Artist", fallbackItem.Metadata["Artist"]);
        Assert.Contains(
            result.SkippedPaths,
            skipped => skipped.Path == invalidTitle.FullPath &&
                skipped.Reason == "Required music metadata is missing.");
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotUseFilenameAlbumArtistOrArtistListAlternatives()
    {
        InventoryFile missingTitle = WriteFile("Same Title - Artist one.mp3");
        InventoryFile missingArtist = WriteFile("Same Title - Artist two.mp3");
        InventoryFile artistOne = WriteFile("artist-one.mp3");
        InventoryFile artistTwo = WriteFile("artist-two.mp3");
        InventoryFile orderedArtists = WriteFile("ordered.mp3");
        InventoryFile reversedArtists = WriteFile("reversed.mp3");
        InventoryFile live = WriteFile("live.mp3");
        InventoryFile plain = WriteFile("plain.mp3");
        InventoryFile remaster = WriteFile("remaster.mp3");
        InventoryFile mix = WriteFile("mix.mp3");
        var provider = new FakeMusicMetadataProvider();
        provider.Results[missingTitle.FullPath] = Music(string.Empty, artist: "Artist one");
        provider.Results[missingArtist.FullPath] = Music("Same Title", artist: string.Empty, albumArtist: string.Empty);
        provider.Results[artistOne.FullPath] = Music("Shared", artist: "Artist one", albumArtist: "Compilation");
        provider.Results[artistTwo.FullPath] = Music("Shared", artist: "Artist two", albumArtist: "Compilation");
        provider.Results[orderedArtists.FullPath] = Music("Duet", artist: "Alice; Bob");
        provider.Results[reversedArtists.FullPath] = Music("Duet", artist: "Bob; Alice");
        provider.Results[live.FullPath] = Music("Song Live", artist: "Artist");
        provider.Results[plain.FullPath] = Music("Song", artist: "Artist");
        provider.Results[remaster.FullPath] = Music("Song Remaster", artist: "Artist");
        provider.Results[mix.FullPath] = Music("Song Mix", artist: "Artist");

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(
                missingTitle,
                missingArtist,
                artistOne,
                artistTwo,
                orderedArtists,
                reversedArtists,
                live,
                plain,
                remaster,
                mix),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(
            [missingTitle.FullPath, missingArtist.FullPath],
            result.SkippedPaths.Select(static skipped => skipped.Path));
        Assert.All(
            result.SkippedPaths,
            static skipped => Assert.Equal("Required music metadata is missing.", skipped.Reason));
    }

    [Fact]
    public async Task AnalyzeAsync_UsesInclusiveCompleteLinkDurationClustersWithoutChaining()
    {
        InventoryFile oneHundred = WriteFile("100.mp3");
        InventoryFile oneHundredTwo = WriteFile("102.mp3");
        InventoryFile oneHundredFour = WriteFile("104.mp3");
        InventoryFile laterOne = WriteFile("later-one.mp3");
        InventoryFile laterTwo = WriteFile("later-two.mp3");
        var provider = new FakeMusicMetadataProvider();
        provider.Results[oneHundred.FullPath] = Music(duration: TimeSpan.FromSeconds(100));
        provider.Results[oneHundredTwo.FullPath] = Music(duration: TimeSpan.FromSeconds(102));
        provider.Results[oneHundredFour.FullPath] = Music(duration: TimeSpan.FromSeconds(104));
        provider.Results[laterOne.FullPath] = Music("Later", duration: TimeSpan.FromSeconds(200));
        provider.Results[laterTwo.FullPath] = Music(
            "Later",
            duration: TimeSpan.FromSeconds(202) + TimeSpan.FromTicks(1));

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(oneHundredFour, oneHundredTwo, oneHundred, laterTwo, laterOne),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Equal(
            [oneHundred.FullPath, oneHundredTwo.FullPath],
            group.Items.Select(static item => item.FullPath).OrderBy(static path => path, StringComparer.Ordinal));
        Assert.DoesNotContain(group.Items, item => item.FullPath == oneHundredFour.FullPath);
    }

    [Fact]
    public async Task AnalyzeAsync_BuildsExactDeterministicReferenceMetadataEvidenceAndOrdering()
    {
        InventoryFile low = WriteFile("x-low.mp3", size: 9);
        InventoryFile reference = WriteFile("y-reference.mp3", size: 5);
        InventoryFile sameBitrateLarger = WriteFile("z-larger.mp3", size: 6);
        InventoryFile earlierGroupOne = WriteFile("b-second-group.mp3", size: 2);
        InventoryFile earlierGroupTwo = WriteFile("c-second-group.mp3", size: 3);
        var mutableGenres = new List<string> { "Rock", "Pop" };
        var provider = new FakeMusicMetadataProvider();
        provider.Results[low.FullPath] = Music(
            title: "Song",
            artist: "Artist",
            albumArtist: "Album Artist",
            album: "Album",
            trackNumber: 7,
            year: 2026,
            genres: mutableGenres,
            bitrate: 128_000,
            duration: TimeSpan.FromSeconds(101));
        provider.Results[reference.FullPath] = Music(
            title: "song",
            artist: "artist",
            bitrate: 320_000,
            duration: TimeSpan.FromSeconds(100));
        provider.Results[sameBitrateLarger.FullPath] = Music(
            title: "song",
            artist: "artist",
            bitrate: 320_000,
            duration: TimeSpan.FromSeconds(102));
        provider.Results[earlierGroupOne.FullPath] = Music("Another", bitrate: 10, duration: TimeSpan.FromSeconds(30));
        provider.Results[earlierGroupTwo.FullPath] = Music("another", bitrate: 20, duration: TimeSpan.FromSeconds(31));
        var inventorySkip = new SkippedPath { Path = "inventory-first", Reason = "Inventory skip" };

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory([low, reference, sameBitrateLarger, earlierGroupOne, earlierGroupTwo], [inventorySkip]),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.Equal(["music-0001", "music-0002"], result.Groups.Select(static group => group.Id));
        Assert.Equal(earlierGroupTwo.FullPath, result.Groups[0].ReferenceItem.FullPath);
        SimilarityGroup songGroup = result.Groups[1];
        Assert.Equal(sameBitrateLarger.FullPath, songGroup.ReferenceItem.FullPath);
        Assert.Same(songGroup.Items[0], songGroup.ReferenceItem);
        Assert.Equal(
            [sameBitrateLarger.FullPath, low.FullPath, reference.FullPath],
            songGroup.Items.Select(static item => item.FullPath));
        Assert.Equal(
            new[] { "Type", "Confidence", "MaximumDurationDifference" },
            songGroup.Metadata.Keys);
        Assert.Equal("Music metadata", songGroup.Metadata["Type"]);
        Assert.Equal("High", songGroup.Metadata["Confidence"]);
        Assert.Equal("00:00:02", songGroup.Metadata["MaximumDurationDifference"]);

        SimilarityItem lowItem = songGroup.Items.Single(item => item.FullPath == low.FullPath);
        Assert.Equal(100, lowItem.SimilarityPercent);
        Assert.Equal(
            new[]
            {
                "Confidence", "Title", "Artist", "AlbumArtist", "Album", "TrackNumber", "Year", "Genres",
                "Bitrate", "Duration", "DurationDifference",
            },
            lowItem.Metadata.Keys);
        Assert.Equal("High", lowItem.Metadata["Confidence"]);
        Assert.Equal("Song", lowItem.Metadata["Title"]);
        Assert.Equal("Artist", lowItem.Metadata["Artist"]);
        Assert.Equal("Album Artist", lowItem.Metadata["AlbumArtist"]);
        Assert.Equal("Album", lowItem.Metadata["Album"]);
        Assert.Equal("7", lowItem.Metadata["TrackNumber"]);
        Assert.Equal("2026", lowItem.Metadata["Year"]);
        Assert.Equal("Rock, Pop", lowItem.Metadata["Genres"]);
        Assert.Equal("128000", lowItem.Metadata["Bitrate"]);
        Assert.Equal("00:01:41", lowItem.Metadata["Duration"]);
        Assert.Equal("00:00:01", lowItem.Metadata["DurationDifference"]);
        var evidence = Assert.IsType<MusicSimilarityEvidence>(lowItem.Evidence);
        mutableGenres[0] = "Mutated";
        Assert.Equal(["Rock", "Pop"], evidence.Genres);
        Assert.Equal("song", evidence.NormalizedTitle);
        Assert.Equal("artist", evidence.NormalizedArtist);
        Assert.Same(inventorySkip, result.SkippedPaths[0]);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task AnalyzeAsync_PreSnapshotMismatchSkipsProviderAndKeepsStableSkipPrecedence()
    {
        InventoryFile stale = WriteFile("stale.mp3");
        InventoryFile later = WriteFile("later.mp3");
        File.AppendAllBytes(stale.FullPath, [9]);
        var provider = new FakeMusicMetadataProvider();
        provider.Results[later.FullPath] = Music(title: string.Empty);
        var inventorySkip = new SkippedPath { Path = "inventory", Reason = "First" };

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory([later, stale], [inventorySkip]),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.Equal([later.FullPath], provider.Paths);
        Assert.Equal(
            ["inventory", later.FullPath, stale.FullPath],
            result.SkippedPaths.Select(static skipped => skipped.Path));
        Assert.Equal("Required music metadata is missing.", result.SkippedPaths[1].Reason);
        Assert.Equal("File changed since scan.", result.SkippedPaths[2].Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task AnalyzeAsync_ExpectedProviderFailuresContinueWithExactBoundary(int failureKind)
    {
        InventoryFile failed = WriteFile("a-failed.mp3");
        InventoryFile first = WriteFile("b-first.mp3");
        InventoryFile second = WriteFile("c-second.mp3");
        var provider = new FakeMusicMetadataProvider();
        provider.Handlers[failed.FullPath] = (_, _) => Task.FromException<MusicMetadata>(ExpectedFailure(failureKind));
        provider.Results[first.FullPath] = Music();
        provider.Results[second.FullPath] = Music();

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(failed, first, second),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.Single(result.Groups);
        SkippedPath skipped = Assert.Single(result.SkippedPaths);
        Assert.Equal(failed.FullPath, skipped.Path);
        Assert.Equal("Could not read music metadata.", skipped.Reason);
    }

    [Theory]
    [InlineData(false, "Could not read music metadata.")]
    [InlineData(true, "File changed since scan.")]
    public async Task AnalyzeAsync_PostProviderMutationWinsOverFailure(bool mutate, string expectedReason)
    {
        InventoryFile file = WriteFile("changing.mp3");
        var provider = new FakeMusicMetadataProvider();
        provider.Handlers[file.FullPath] = (path, _) =>
        {
            if (mutate)
            {
                File.AppendAllBytes(path, [7]);
            }

            return Task.FromException<MusicMetadata>(new IOException("metadata"));
        };

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(file),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.Equal(expectedReason, Assert.Single(result.SkippedPaths).Reason);
    }

    [Fact]
    public async Task AnalyzeAsync_NonpositiveDurationIsProviderFailureAndUnexpectedFaultPropagates()
    {
        InventoryFile invalidDuration = WriteFile("invalid-duration.mp3");
        var provider = new FakeMusicMetadataProvider();
        provider.Results[invalidDuration.FullPath] = Music(duration: TimeSpan.Zero);

        AnalysisResult result = await new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(invalidDuration),
            new MusicDuplicateOptions(TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal("Could not read music metadata.", Assert.Single(result.SkippedPaths).Reason);

        provider.Handlers[invalidDuration.FullPath] = (_, _) =>
            Task.FromException<MusicMetadata>(new InvalidOperationException("unexpected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
            Inventory(invalidDuration),
            new MusicDuplicateOptions(TimeSpan.Zero),
            CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellation()
    {
        InventoryFile file = WriteFile("cancelled.mp3");
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeMusicMetadataProvider();
        provider.Handlers[file.FullPath] = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<MusicMetadata>(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MusicDuplicateAnalyzer(provider).AnalyzeAsync(
                Inventory(file),
                new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
                cancellation.Token));
    }

    [Fact]
    public async Task RevalidateAsync_UsesFreshStructuralOrdinalEvidenceAndDefensiveGenreCopy()
    {
        InventoryFile first = WriteFile("first.mp3");
        InventoryFile second = WriteFile("second.mp3");
        var originalGenres = new List<string> { "Rock", "pop" };
        var provider = new FakeMusicMetadataProvider();
        provider.Results[first.FullPath] = Music(genres: originalGenres);
        provider.Results[second.FullPath] = Music(genres: ["Rock", "pop"]);
        var analyzer = new MusicDuplicateAnalyzer(provider);
        SimilarityItem item = Assert.Single((await analyzer.AnalyzeAsync(
            Inventory(first, second),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None)).Groups).Items[0];
        originalGenres[0] = "Mutated";

        provider.Results[item.FullPath] = Music(genres: new List<string> { "Rock", "pop" });
        Assert.True(await analyzer.RevalidateAsync(item, CancellationToken.None));
        provider.Results[item.FullPath] = Music(genres: ["pop", "Rock"]);
        Assert.False(await analyzer.RevalidateAsync(item, CancellationToken.None));
        provider.Results[item.FullPath] = Music(genres: ["Rock", "Pop"]);
        Assert.False(await analyzer.RevalidateAsync(item, CancellationToken.None));
        Assert.Equal(4, provider.Paths.Count(path => path == item.FullPath));
    }

    [Fact]
    public async Task RevalidateAsync_UsesThreeWayValidationAndPropagatesProviderFailureAndCancellation()
    {
        SimilarityItem item = MusicItem(Path.Combine(_root, "item.mp3"));
        var provider = new FakeMusicMetadataProvider();
        var analyzer = new MusicDuplicateAnalyzer(provider);
        provider.Results[item.FullPath] = Music(title: string.Empty);
        await Assert.ThrowsAsync<MissingRequiredMusicMetadataException>(() =>
            analyzer.RevalidateAsync(item, CancellationToken.None));
        provider.Results[item.FullPath] = Music(duration: TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            analyzer.RevalidateAsync(item, CancellationToken.None));
        provider.Handlers[item.FullPath] = (_, _) => Task.FromException<MusicMetadata>(new IOException("provider"));
        await Assert.ThrowsAsync<IOException>(() => analyzer.RevalidateAsync(item, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analyzer.RevalidateAsync(item, cancellation.Token));

        SimilarityItem wrongEvidence = item with
        {
            Evidence = new ImageSimilarityEvidence(0, 1, 1, "JPEG"),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            analyzer.RevalidateAsync(wrongEvidence, CancellationToken.None));
    }

    [Fact]
    public void Regroup_IsPureAndRebuildsCompleteLinkClustersReferencesIdsAndIdentity()
    {
        SimilarityItem hundred = MusicItem(Path.Combine(_root, "100.mp3"), 100, bitrate: 100, size: 10);
        SimilarityItem hundredTwo = MusicItem(Path.Combine(_root, "102.mp3"), 102, bitrate: 200, size: 5);
        SimilarityItem hundredFour = MusicItem(Path.Combine(_root, "104.mp3"), 104, bitrate: 300, size: 5);
        var analyzer = new MusicDuplicateAnalyzer(new FakeMusicMetadataProvider());

        SimilarityGroup group = Assert.Single(analyzer.Regroup(
            [hundredFour, hundred, hundredTwo],
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2))));

        Assert.Equal("music-0001", group.Id);
        Assert.Equal(hundredTwo.FullPath, group.ReferenceItem.FullPath);
        Assert.Same(group.Items[0], group.ReferenceItem);
        Assert.Equal([hundredTwo.FullPath, hundred.FullPath], group.Items.Select(static item => item.FullPath));
        Assert.DoesNotContain(group.Items, item => item.FullPath == hundredFour.FullPath);
    }

    private InventoryFile WriteFile(string name, int size = 1)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(static value => (byte)value).ToArray());
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc));
        var file = new FileInfo(path);
        file.Refresh();
        return new InventoryFile(
            path,
            file.Name,
            file.Extension.ToLowerInvariant(),
            file.DirectoryName!,
            file.Length,
            file.CreationTimeUtc,
            file.LastWriteTimeUtc,
            FileAttributes.Normal);
    }

    private static FileInventory Inventory(params InventoryFile[] files) => Inventory(files, []);

    private static FileInventory Inventory(
        IReadOnlyList<InventoryFile> files,
        IReadOnlyList<SkippedPath> skipped) => new(files, [], [], [], skipped);

    private static MusicMetadata Music(
        string title = "Song",
        string artist = "Artist",
        string albumArtist = "Album Artist",
        string album = "Album",
        uint trackNumber = 1,
        uint year = 2025,
        IReadOnlyList<string>? genres = null,
        uint bitrate = 192_000,
        TimeSpan? duration = null) => new(
            title,
            artist,
            albumArtist,
            album,
            trackNumber,
            year,
            genres ?? ["Rock"],
            bitrate,
            duration ?? TimeSpan.FromSeconds(100));

    private static SimilarityItem MusicItem(
        string path,
        int seconds = 100,
        uint bitrate = 192_000,
        long size = 1) => new()
        {
            FullPath = path,
            SizeBytes = size,
            ModifiedUtc = new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc),
            SimilarityPercent = 100,
            Evidence = new MusicSimilarityEvidence(
                "song",
                "artist",
                "Song",
                "Artist",
                "Album Artist",
                "Album",
                1,
                2025,
                ["Rock"],
                bitrate,
                TimeSpan.FromSeconds(seconds)),
        };

    private static Exception ExpectedFailure(int failureKind) => failureKind switch
    {
        0 => new IOException("io"),
        1 => new UnauthorizedAccessException("unauthorized"),
        2 => new SecurityException("security"),
        3 => new ArgumentException("argument"),
        4 => new NotSupportedException("not supported"),
        5 => new InvalidDataException("invalid"),
        6 => new OverflowException("overflow"),
        7 => new COMException("com"),
        _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
    };

    private sealed class FakeMusicMetadataProvider : IMusicMetadataProvider
    {
        public Dictionary<string, MusicMetadata> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Func<string, CancellationToken, Task<MusicMetadata>>> Handlers { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Paths { get; } = [];

        public Task<MusicMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            if (Handlers.TryGetValue(path, out Func<string, CancellationToken, Task<MusicMetadata>>? handler))
            {
                return handler(path, cancellationToken);
            }

            return Task.FromResult(Results[path]);
        }
    }
}
