using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed record SimilarVideoOptions(int MaximumMeanFrameDistance);

public sealed class SimilarVideoAnalyzer
{
    private const int FrameCount = 5;
    private const int BandCount = 14;
    private const double MaximumAspectRatioDifference = 0.05d;
    private static readonly FileTypeFilter VideoFilter = FileTypeFilter.ForCategories([FileTypeCategory.Video]);
    private readonly IVideoSampleProvider _provider;
    private readonly int _maximumConcurrency;

    public SimilarVideoAnalyzer(IVideoSampleProvider provider)
        : this(provider, 1)
    {
    }

    public SimilarVideoAnalyzer(IVideoSampleProvider provider, int maximumConcurrency)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        _provider = provider;
        _maximumConcurrency = maximumConcurrency;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        SimilarVideoOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        InventoryFile[] files = inventory.Files
            .Where(static file =>
                !file.Attributes.HasFlag(FileAttributes.Directory) &&
                !file.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                VideoFilter.Matches(file.Extension))
            .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.FullPath, StringComparer.Ordinal)
            .ToArray();
        var outcomes = new FileAnalysisOutcome[files.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maximumConcurrency,
                TaskScheduler = TaskScheduler.Default,
            },
            async (index, token) =>
            {
                outcomes[index] = await AnalyzeFileAsync(files[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        Candidate[] candidates = outcomes
            .Where(static outcome => outcome.Candidate is not null)
            .Select(static outcome => outcome.Candidate!)
            .ToArray();
        SkippedPath[] analyzerSkips = outcomes
            .Where(static outcome => outcome.Skip is not null)
            .Select(static outcome => outcome.Skip!)
            .ToArray();

        IReadOnlyList<SimilarityGroup> groups = BuildGroups(
            candidates,
            options,
            pairScored: null,
            cancellationToken);
        stopwatch.Stop();
        return new AnalysisResult
        {
            Findings = [],
            Groups = groups,
            SkippedPaths = analyzerSkips.Length == 0
                ? inventory.SkippedPaths
                : [.. inventory.SkippedPaths, .. analyzerSkips],
            Elapsed = stopwatch.Elapsed,
        };
    }

    private async ValueTask<FileAnalysisOutcome> AnalyzeFileAsync(
        InventoryFile file,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSnapshot(file.FullPath, out FileSnapshot before) || !MatchesInventory(before, file))
        {
            return new(null, ChangedSkip(file.FullPath));
        }

        VideoSample? sample = null;
        Exception? providerFailure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            sample = await _provider.GetSampleAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedProviderFailure(ex))
        {
            providerFailure = ex;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSnapshot(file.FullPath, out FileSnapshot after) ||
            after != before ||
            !MatchesInventory(after, file))
        {
            return new(null, ChangedSkip(file.FullPath));
        }

        if (providerFailure is not null)
        {
            return new(null, DecodeSkip(file.FullPath));
        }

        try
        {
            return new(
                new Candidate(
                    file.FullPath,
                    file.SizeBytes,
                    file.ModifiedUtc,
                    BuildEvidence(sample)),
                null);
        }
        catch (InvalidDataException)
        {
            return new(null, DecodeSkip(file.FullPath));
        }
    }

    public async Task<bool> RevalidateAsync(
        SimilarityItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Evidence is not VideoSimilarityEvidence expected || !IsValid(expected))
        {
            throw new InvalidDataException("The similarity item does not contain valid video evidence.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        VideoSample sample = await _provider.GetFreshSampleAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return BuildEvidence(sample) == expected;
    }

    public IReadOnlyList<SimilarityGroup> Regroup(
        IReadOnlyList<SimilarityItem> items,
        SimilarVideoOptions options) => RegroupPrehashed(items, options, pairScored: null);

    internal static IReadOnlyList<SimilarityGroup> RegroupPrehashed(
        IReadOnlyList<SimilarityItem> items,
        SimilarVideoOptions options,
        Action<string, string>? pairScored,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<Candidate>(items.Count);
        foreach (SimilarityItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (item.Evidence is not VideoSimilarityEvidence evidence || !IsValid(evidence))
            {
                throw new InvalidDataException("The similarity item does not contain valid video evidence.");
            }

            candidates.Add(new Candidate(item.FullPath, item.SizeBytes, item.ModifiedUtc, evidence));
        }

        return BuildGroups(candidates, options, pairScored, cancellationToken);
    }

    private static IReadOnlyList<SimilarityGroup> BuildGroups(
        IReadOnlyList<Candidate> source,
        SimilarVideoOptions options,
        Action<string, string>? pairScored,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Candidate[] candidates = source
            .OrderBy(static item => item.Evidence.Duration.Ticks)
            .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.FullPath, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var disjointSet = new DisjointSet(candidates.Length);
        var bands = new Dictionary<BandKey, HashSet<int>>();
        int activeStart = 0;

        for (int index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate current = candidates[index];
            while (activeStart < index &&
                !DurationMatches(candidates[activeStart].Evidence, current.Evidence))
            {
                RemoveFromBands(bands, candidates[activeStart], activeStart);
                activeStart++;
            }

            var priorCandidates = new SortedSet<int>();
            for (int frameIndex = 0; frameIndex < FrameCount; frameIndex++)
            {
                ulong hash = GetFrameHash(current.Evidence, frameIndex);
                for (int bandIndex = 0; bandIndex < BandCount; bandIndex++)
                {
                    var key = new BandKey(frameIndex, bandIndex, ReadBand(hash, bandIndex));
                    if (bands.TryGetValue(key, out HashSet<int>? matching))
                    {
                        priorCandidates.UnionWith(matching);
                    }
                }
            }

            foreach (int otherIndex in priorCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Candidate other = candidates[otherIndex];
                if (!DurationMatches(other.Evidence, current.Evidence) ||
                    !AspectRatiosMatch(other.Evidence, current.Evidence))
                {
                    continue;
                }

                pairScored?.Invoke(other.FullPath, current.FullPath);
                cancellationToken.ThrowIfCancellationRequested();
                int totalDistance = TotalDistance(other.Evidence, current.Evidence);
                if (totalDistance <= FrameCount * options.MaximumMeanFrameDistance)
                {
                    disjointSet.Union(index, otherIndex);
                }
            }

            AddToBands(bands, current, index);
        }

        var componentsByRoot = new Dictionary<int, List<Candidate>>();
        for (int index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int root = disjointSet.Find(index);
            if (!componentsByRoot.TryGetValue(root, out List<Candidate>? component))
            {
                component = [];
                componentsByRoot.Add(root, component);
            }

            component.Add(candidates[index]);
        }

        Candidate[][] components = componentsByRoot.Values
            .Where(static component => component.Count >= 2)
            .Select(static component => component.ToArray())
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var orderedComponents = components
            .Select(component => (Component: component, Reference: ChooseReference(component)))
            .OrderBy(static group => group.Reference.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Reference.FullPath, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var groups = new List<SimilarityGroup>(orderedComponents.Length);
        for (int groupIndex = 0; groupIndex < orderedComponents.Length; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (Candidate[] component, Candidate reference) = orderedComponents[groupIndex];
            SimilarityItem[] items = component
                .Select(candidate => (Candidate: candidate, Mean: MeanDistance(reference.Evidence, candidate.Evidence)))
                .OrderBy(item => item.Candidate == reference ? 0 : 1)
                .ThenBy(static item => item.Mean)
                .ThenBy(static item => item.Candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Candidate.FullPath, StringComparer.Ordinal)
                .Select(static item => BuildItem(item.Candidate, item.Mean))
                .ToArray();
            groups.Add(new SimilarityGroup
            {
                Id = $"video-{groupIndex + 1:0000}",
                ReferenceItem = items[0],
                Items = items,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Type"] = "Video",
                    ["MaximumMeanFrameDistance"] = options.MaximumMeanFrameDistance.ToString(
                        CultureInfo.InvariantCulture),
                },
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        return groups;
    }

    private static SimilarityItem BuildItem(Candidate candidate, double meanDistance) => new()
    {
        FullPath = candidate.FullPath,
        SizeBytes = candidate.SizeBytes,
        ModifiedUtc = candidate.ModifiedUtc,
        SimilarityPercent = 100d * (64 - meanDistance) / 64,
        Evidence = candidate.Evidence,
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Width"] = candidate.Evidence.Width.ToString(CultureInfo.InvariantCulture),
            ["Height"] = candidate.Evidence.Height.ToString(CultureInfo.InvariantCulture),
            ["Duration"] = candidate.Evidence.Duration.ToString("c", CultureInfo.InvariantCulture),
            ["Bitrate"] = candidate.Evidence.Bitrate.ToString(CultureInfo.InvariantCulture),
            ["FramesPerSecond"] = candidate.Evidence.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            ["Codec"] = candidate.Evidence.Codec,
            ["MeanFrameDistance"] = meanDistance.ToString(CultureInfo.InvariantCulture),
        },
    };

    private static Candidate ChooseReference(IEnumerable<Candidate> component) => component
        .OrderByDescending(static candidate => checked((long)candidate.Evidence.Width * candidate.Evidence.Height))
        .ThenByDescending(static candidate => candidate.Evidence.Bitrate)
        .ThenByDescending(static candidate => candidate.SizeBytes)
        .ThenBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static candidate => candidate.FullPath, StringComparer.Ordinal)
        .First();

    private static VideoSimilarityEvidence BuildEvidence(VideoSample? sample)
    {
        if (!IsValid(sample))
        {
            throw new InvalidDataException("The video sample is structurally invalid.");
        }

        return new VideoSimilarityEvidence(
            PerceptualHash.Compute(sample!.LuminanceFrames32x32[0]),
            PerceptualHash.Compute(sample.LuminanceFrames32x32[1]),
            PerceptualHash.Compute(sample.LuminanceFrames32x32[2]),
            PerceptualHash.Compute(sample.LuminanceFrames32x32[3]),
            PerceptualHash.Compute(sample.LuminanceFrames32x32[4]),
            sample.Width,
            sample.Height,
            sample.DisplayAspectRatio,
            sample.Duration,
            sample.Bitrate,
            sample.FramesPerSecond,
            sample.Codec);
    }

    private static bool IsValid(VideoSample? sample)
    {
        if (sample is null ||
            sample.Width <= 0 ||
            sample.Height <= 0 ||
            !double.IsFinite(sample.DisplayAspectRatio) ||
            sample.DisplayAspectRatio <= 0 ||
            sample.Duration <= TimeSpan.Zero ||
            !double.IsFinite(sample.FramesPerSecond) ||
            sample.FramesPerSecond <= 0 ||
            string.IsNullOrWhiteSpace(sample.Codec) ||
            sample.LuminanceFrames32x32 is not { Count: FrameCount })
        {
            return false;
        }

        var frames = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (byte[]? frame in sample.LuminanceFrames32x32)
        {
            if (frame?.Length != 1024 || !frames.Add(frame))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValid(VideoSimilarityEvidence evidence) =>
        evidence.Width > 0 &&
        evidence.Height > 0 &&
        double.IsFinite(evidence.DisplayAspectRatio) &&
        evidence.DisplayAspectRatio > 0 &&
        evidence.Duration > TimeSpan.Zero &&
        double.IsFinite(evidence.FramesPerSecond) &&
        evidence.FramesPerSecond > 0 &&
        !string.IsNullOrWhiteSpace(evidence.Codec);

    private static bool DurationMatches(VideoSimilarityEvidence left, VideoSimilarityEvidence right)
    {
        TimeSpan longer = left.Duration >= right.Duration ? left.Duration : right.Duration;
        TimeSpan shorter = left.Duration >= right.Duration ? right.Duration : left.Duration;
        long deltaTicks = longer.Ticks - shorter.Ticks;
        return deltaTicks <= 2 * TimeSpan.TicksPerSecond ||
            deltaTicks <= longer.Ticks / 50;
    }

    private static bool AspectRatiosMatch(VideoSimilarityEvidence left, VideoSimilarityEvidence right) =>
        Math.Abs(left.DisplayAspectRatio - right.DisplayAspectRatio) /
            Math.Max(left.DisplayAspectRatio, right.DisplayAspectRatio) <= MaximumAspectRatioDifference;

    private static int TotalDistance(VideoSimilarityEvidence left, VideoSimilarityEvidence right)
    {
        int total = 0;
        for (int frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            total += PerceptualHash.Distance(
                GetFrameHash(left, frameIndex),
                GetFrameHash(right, frameIndex));
        }

        return total;
    }

    private static double MeanDistance(VideoSimilarityEvidence left, VideoSimilarityEvidence right) =>
        TotalDistance(left, right) / (double)FrameCount;

    private static ulong GetFrameHash(VideoSimilarityEvidence evidence, int frameIndex) => frameIndex switch
    {
        0 => evidence.FrameHash10,
        1 => evidence.FrameHash30,
        2 => evidence.FrameHash50,
        3 => evidence.FrameHash70,
        4 => evidence.FrameHash90,
        _ => throw new ArgumentOutOfRangeException(nameof(frameIndex)),
    };

    private static int ReadBand(ulong hash, int bandIndex)
    {
        int offset = bandIndex < 8
            ? 5 * bandIndex
            : 40 + (4 * (bandIndex - 8));
        int width = bandIndex < 8 ? 5 : 4;
        return (int)((hash >> offset) & ((1UL << width) - 1));
    }

    private static void AddToBands(
        IDictionary<BandKey, HashSet<int>> bands,
        Candidate candidate,
        int candidateIndex)
    {
        for (int frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            ulong hash = GetFrameHash(candidate.Evidence, frameIndex);
            for (int bandIndex = 0; bandIndex < BandCount; bandIndex++)
            {
                var key = new BandKey(frameIndex, bandIndex, ReadBand(hash, bandIndex));
                if (!bands.TryGetValue(key, out HashSet<int>? matching))
                {
                    matching = [];
                    bands.Add(key, matching);
                }

                matching.Add(candidateIndex);
            }
        }
    }

    private static void RemoveFromBands(
        IDictionary<BandKey, HashSet<int>> bands,
        Candidate candidate,
        int candidateIndex)
    {
        for (int frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            ulong hash = GetFrameHash(candidate.Evidence, frameIndex);
            for (int bandIndex = 0; bandIndex < BandCount; bandIndex++)
            {
                var key = new BandKey(frameIndex, bandIndex, ReadBand(hash, bandIndex));
                if (bands.TryGetValue(key, out HashSet<int>? matching))
                {
                    matching.Remove(candidateIndex);
                    if (matching.Count == 0)
                    {
                        bands.Remove(key);
                    }
                }
            }
        }
    }

    private static void ValidateOptions(SimilarVideoOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumMeanFrameDistance is < 0 or > 13)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static bool TryReadSnapshot(string path, out FileSnapshot snapshot)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists ||
                attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                snapshot = default;
                return false;
            }

            snapshot = new FileSnapshot(file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (IsExpectedProviderFailure(ex))
        {
            snapshot = default;
            return false;
        }
    }

    private static bool MatchesInventory(FileSnapshot snapshot, InventoryFile file) =>
        snapshot.SizeBytes == file.SizeBytes && snapshot.ModifiedUtc.Ticks == file.ModifiedUtc.Ticks;

    private static bool IsExpectedProviderFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or InvalidDataException or OverflowException or COMException;

    private static SkippedPath ChangedSkip(string path) => new()
    {
        Path = path,
        Reason = "File changed since scan.",
    };

    private static SkippedPath DecodeSkip(string path) => new()
    {
        Path = path,
        Reason = "Could not decode video.",
    };

    private sealed record Candidate(
        string FullPath,
        long SizeBytes,
        DateTime ModifiedUtc,
        VideoSimilarityEvidence Evidence);

    private readonly record struct FileAnalysisOutcome(Candidate? Candidate, SkippedPath? Skip);

    private readonly record struct BandKey(int FrameIndex, int BandIndex, int Value);

    private readonly record struct FileSnapshot(long SizeBytes, DateTime ModifiedUtc);

    private sealed class DisjointSet(int count)
    {
        private readonly int[] _parents = Enumerable.Range(0, count).ToArray();

        public int Find(int item)
        {
            while (_parents[item] != item)
            {
                _parents[item] = _parents[_parents[item]];
                item = _parents[item];
            }

            return item;
        }

        public void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                _parents[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
            }
        }
    }
}
