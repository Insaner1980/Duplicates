using System.Diagnostics;
using System.Globalization;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;
using static Duplicates.Engine.Analysis.Analyzers.SimilarityAnalyzerSupport;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed record SimilarImageOptions(int MaximumHammingDistance);

public sealed class SimilarImageAnalyzer
{
    private const double MaximumAspectRatioDifference = 0.05d;
    private static readonly FileTypeFilter ImageFilter = FileTypeFilter.ForCategories([FileTypeCategory.Images]);
    private readonly IImageSampleProvider _provider;
    private readonly int _maximumConcurrency;

    public SimilarImageAnalyzer(IImageSampleProvider provider)
        : this(provider, 1)
    {
    }

    public SimilarImageAnalyzer(IImageSampleProvider provider, int maximumConcurrency)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        _provider = provider;
        _maximumConcurrency = maximumConcurrency;
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
        (Candidate[] candidates, IReadOnlyList<SkippedPath> skippedPaths) = await AnalyzeFilesAsync<Candidate>(
            inventory,
            ImageFilter,
            _maximumConcurrency,
            AnalyzeFileAsync,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<SimilarityGroup> groups = BuildGroups(candidates, options, cancellationToken);
        stopwatch.Stop();
        return new AnalysisResult
        {
            Findings = [],
            Groups = groups,
            SkippedPaths = skippedPaths,
            Elapsed = stopwatch.Elapsed,
        };
    }

    private async ValueTask<FileAnalysisOutcome<Candidate>> AnalyzeFileAsync(
        InventoryFile file,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSnapshot(file.FullPath, out FileSnapshot before) || !MatchesInventory(before, file))
        {
            return new(null, ChangedSkip(file.FullPath));
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
        if (item.Evidence is not ImageSimilarityEvidence expected)
        {
            throw new InvalidDataException("The similarity item does not contain image evidence.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ImageSample sample = await _provider.GetFreshSampleAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return BuildEvidence(sample) == expected;
    }

    public static IReadOnlyList<SimilarityGroup> Regroup(
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

        return BuildGroups(candidates, options, CancellationToken.None);
    }

    private static List<SimilarityGroup> BuildGroups(
        IReadOnlyList<Candidate> source,
        SimilarImageOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Candidate[] candidates = source
            .OrderBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.FullPath, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var disjointSet = new DisjointSet(candidates.Length);
        var bands = new Dictionary<BandKey, List<int>>();

        for (int index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate current = candidates[index];
            SortedSet<int> priorCandidates = GetPriorCandidates(bands, current);

            foreach (int otherIndex in priorCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Candidate other = candidates[otherIndex];
                if (AspectRatiosMatch(current.Evidence, other.Evidence) &&
                    PerceptualHash.Distance(current.Evidence.PerceptualHash, other.Evidence.PerceptualHash) <=
                        options.MaximumHammingDistance)
                {
                    disjointSet.Union(index, otherIndex);
                }
            }

            AddToBands(bands, current, index);
        }

        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();

        var groups = new List<SimilarityGroup>(orderedComponents.Length);
        for (int groupIndex = 0; groupIndex < orderedComponents.Length; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        cancellationToken.ThrowIfCancellationRequested();
        return groups;
    }

    private static SortedSet<int> GetPriorCandidates(Dictionary<BandKey, List<int>> bands, Candidate current)
    {
        var priorCandidates = new SortedSet<int>();
        for (int band = 0; band < 13; band++)
        {
            var key = new BandKey(band, ReadBand(current.Evidence.PerceptualHash, band));
            if (bands.TryGetValue(key, out List<int>? matching))
            {
                priorCandidates.UnionWith(matching);
            }
        }

        return priorCandidates;
    }

    private static void AddToBands(Dictionary<BandKey, List<int>> bands, Candidate current, int index)
    {
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
}
