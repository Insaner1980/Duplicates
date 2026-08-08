using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed record SimilarImageOptions(int MaximumHammingDistance);

public sealed class SimilarImageAnalyzer
{
    private const double MaximumAspectRatioDifference = 0.05d;
    private static readonly FileTypeFilter ImageFilter = FileTypeFilter.ForCategories([FileTypeCategory.Images]);
    private readonly IImageSampleProvider _provider;

    public SimilarImageAnalyzer(IImageSampleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        SimilarImageOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var candidates = new List<Candidate>();
        var analyzerSkips = new List<SkippedPath>();
        IEnumerable<InventoryFile> files = inventory.Files
            .Where(static file =>
                !file.Attributes.HasFlag(FileAttributes.Directory) &&
                !file.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                ImageFilter.Matches(file.Extension))
            .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.FullPath, StringComparer.Ordinal);

        foreach (InventoryFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadSnapshot(file.FullPath, out FileSnapshot before) || !MatchesInventory(before, file))
            {
                analyzerSkips.Add(ChangedSkip(file.FullPath));
                continue;
            }

            ImageSample? sample = null;
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
                analyzerSkips.Add(ChangedSkip(file.FullPath));
                continue;
            }

            if (providerFailure is not null)
            {
                analyzerSkips.Add(DecodeSkip(file.FullPath));
                continue;
            }

            try
            {
                candidates.Add(new Candidate(
                    file.FullPath,
                    file.SizeBytes,
                    file.ModifiedUtc,
                    BuildEvidence(sample)));
            }
            catch (InvalidDataException)
            {
                analyzerSkips.Add(DecodeSkip(file.FullPath));
            }
        }

        IReadOnlyList<SimilarityGroup> groups = BuildGroups(candidates, options);
        stopwatch.Stop();
        return new AnalysisResult
        {
            Findings = [],
            Groups = groups,
            SkippedPaths = analyzerSkips.Count == 0
                ? inventory.SkippedPaths
                : [.. inventory.SkippedPaths, .. analyzerSkips],
            Elapsed = stopwatch.Elapsed,
        };
    }

    public async Task<bool> RevalidateAsync(
        SimilarityItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Evidence is not ImageSimilarityEvidence expected)
        {
            throw new InvalidDataException("The similarity item does not contain image evidence.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ImageSample sample = await _provider.GetFreshSampleAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return BuildEvidence(sample) == expected;
    }

    public IReadOnlyList<SimilarityGroup> Regroup(
        IReadOnlyList<SimilarityItem> items,
        SimilarImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateOptions(options);
        var candidates = new List<Candidate>(items.Count);
        foreach (SimilarityItem item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Evidence is not ImageSimilarityEvidence evidence || !IsValid(evidence))
            {
                throw new InvalidDataException("The similarity item does not contain valid image evidence.");
            }

            candidates.Add(new Candidate(item.FullPath, item.SizeBytes, item.ModifiedUtc, evidence));
        }

        return BuildGroups(candidates, options);
    }

    private static IReadOnlyList<SimilarityGroup> BuildGroups(
        IReadOnlyList<Candidate> source,
        SimilarImageOptions options)
    {
        Candidate[] candidates = source
            .OrderBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.FullPath, StringComparer.Ordinal)
            .ToArray();
        var disjointSet = new DisjointSet(candidates.Length);
        var bands = new Dictionary<BandKey, List<int>>();

        for (int index = 0; index < candidates.Length; index++)
        {
            Candidate current = candidates[index];
            var priorCandidates = new SortedSet<int>();
            for (int band = 0; band < 13; band++)
            {
                var key = new BandKey(band, ReadBand(current.Evidence.PerceptualHash, band));
                if (bands.TryGetValue(key, out List<int>? matching))
                {
                    priorCandidates.UnionWith(matching);
                }
            }

            foreach (int otherIndex in priorCandidates)
            {
                Candidate other = candidates[otherIndex];
                if (AspectRatiosMatch(current.Evidence, other.Evidence) &&
                    PerceptualHash.Distance(current.Evidence.PerceptualHash, other.Evidence.PerceptualHash) <=
                        options.MaximumHammingDistance)
                {
                    disjointSet.Union(index, otherIndex);
                }
            }

            for (int band = 0; band < 13; band++)
            {
                var key = new BandKey(band, ReadBand(current.Evidence.PerceptualHash, band));
                if (!bands.TryGetValue(key, out List<int>? matching))
                {
                    matching = [];
                    bands.Add(key, matching);
                }

                matching.Add(index);
            }
        }

        Candidate[][] components = Enumerable.Range(0, candidates.Length)
            .GroupBy(disjointSet.Find)
            .Select(group => group.Select(index => candidates[index]).ToArray())
            .Where(static component => component.Length >= 2)
            .ToArray();
        var orderedComponents = components
            .Select(component => (Component: component, Reference: ChooseReference(component)))
            .OrderBy(static group => group.Reference.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Reference.FullPath, StringComparer.Ordinal)
            .ToArray();

        var groups = new List<SimilarityGroup>(orderedComponents.Length);
        for (int groupIndex = 0; groupIndex < orderedComponents.Length; groupIndex++)
        {
            (Candidate[] component, Candidate reference) = orderedComponents[groupIndex];
            SimilarityItem[] items = component
                .Select(candidate => (Candidate: candidate, Distance: PerceptualHash.Distance(
                    reference.Evidence.PerceptualHash,
                    candidate.Evidence.PerceptualHash)))
                .OrderBy(item => item.Candidate == reference ? 0 : 1)
                .ThenBy(static item => item.Distance)
                .ThenBy(static item => item.Candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Candidate.FullPath, StringComparer.Ordinal)
                .Select(static item => BuildItem(item.Candidate, item.Distance))
                .ToArray();
            groups.Add(new SimilarityGroup
            {
                Id = $"image-{groupIndex + 1:0000}",
                ReferenceItem = items[0],
                Items = items,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Type"] = "Image",
                    ["MaximumHammingDistance"] = options.MaximumHammingDistance.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        return groups;
    }

    private static SimilarityItem BuildItem(Candidate candidate, int distance) => new()
    {
        FullPath = candidate.FullPath,
        SizeBytes = candidate.SizeBytes,
        ModifiedUtc = candidate.ModifiedUtc,
        SimilarityPercent = 100d * (64 - distance) / 64,
        Evidence = candidate.Evidence,
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Width"] = candidate.Evidence.Width.ToString(CultureInfo.InvariantCulture),
            ["Height"] = candidate.Evidence.Height.ToString(CultureInfo.InvariantCulture),
            ["Format"] = candidate.Evidence.Format,
            ["HammingDistance"] = distance.ToString(CultureInfo.InvariantCulture),
        },
    };

    private static Candidate ChooseReference(IEnumerable<Candidate> component) => component
        .OrderByDescending(static candidate => checked((long)candidate.Evidence.Width * candidate.Evidence.Height))
        .ThenByDescending(static candidate => candidate.SizeBytes)
        .ThenBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static candidate => candidate.FullPath, StringComparer.Ordinal)
        .First();

    private static ImageSimilarityEvidence BuildEvidence(ImageSample? sample)
    {
        if (sample is null || sample.Width <= 0 || sample.Height <= 0 || sample.Luminance32x32?.Length != 1024)
        {
            throw new InvalidDataException("The image sample is structurally invalid.");
        }

        return new ImageSimilarityEvidence(
            PerceptualHash.Compute(sample.Luminance32x32),
            sample.Width,
            sample.Height,
            sample.Format);
    }

    private static bool IsValid(ImageSimilarityEvidence evidence) =>
        evidence.Width > 0 && evidence.Height > 0;

    private static bool AspectRatiosMatch(ImageSimilarityEvidence left, ImageSimilarityEvidence right)
    {
        double leftRatio = (double)left.Width / left.Height;
        double rightRatio = (double)right.Width / right.Height;
        return Math.Abs(leftRatio - rightRatio) / Math.Max(leftRatio, rightRatio) <= MaximumAspectRatioDifference;
    }

    private static int ReadBand(ulong hash, int band)
    {
        int offset = band * 5;
        int width = band == 12 ? 4 : 5;
        return (int)((hash >> offset) & ((1UL << width) - 1));
    }

    private static void ValidateOptions(SimilarImageOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumHammingDistance is < 0 or > 12)
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
        Reason = "Could not decode image.",
    };

    private sealed record Candidate(
        string FullPath,
        long SizeBytes,
        DateTime ModifiedUtc,
        ImageSimilarityEvidence Evidence);

    private readonly record struct BandKey(int Band, int Value);

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
