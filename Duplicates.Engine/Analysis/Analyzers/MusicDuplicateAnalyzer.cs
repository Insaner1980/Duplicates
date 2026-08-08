using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed record MusicDuplicateOptions(TimeSpan MaximumDurationDifference);

public sealed class MusicDuplicateAnalyzer
{
    private static readonly FileTypeFilter AudioFilter = FileTypeFilter.ForCategories([FileTypeCategory.Audio]);
    private readonly IMusicMetadataProvider _provider;

    public MusicDuplicateAnalyzer(IMusicMetadataProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        MusicDuplicateOptions options,
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
                AudioFilter.Matches(file.Extension))
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

            MusicMetadata? metadata = null;
            Exception? providerFailure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                metadata = await _provider.GetMetadataAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
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
                analyzerSkips.Add(UnreadableSkip(file.FullPath));
                continue;
            }

            try
            {
                candidates.Add(new Candidate(
                    file.FullPath,
                    file.SizeBytes,
                    file.ModifiedUtc,
                    BuildEvidence(metadata)));
            }
            catch (MissingRequiredMusicMetadataException)
            {
                analyzerSkips.Add(MissingMetadataSkip(file.FullPath));
            }
            catch (Exception ex) when (IsExpectedProviderFailure(ex))
            {
                analyzerSkips.Add(UnreadableSkip(file.FullPath));
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
        if (item.Evidence is not MusicSimilarityEvidence expected || !IsValid(expected))
        {
            throw new InvalidDataException("The similarity item does not contain valid music evidence.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        MusicMetadata metadata = await _provider.GetMetadataAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        MusicSimilarityEvidence actual = BuildEvidence(metadata);
        return EvidenceEquals(expected, actual);
    }

    public IReadOnlyList<SimilarityGroup> Regroup(
        IReadOnlyList<SimilarityItem> items,
        MusicDuplicateOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateOptions(options);
        var candidates = new List<Candidate>(items.Count);
        foreach (SimilarityItem item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Evidence is not MusicSimilarityEvidence evidence || !IsValid(evidence))
            {
                throw new InvalidDataException("The similarity item does not contain valid music evidence.");
            }

            candidates.Add(new Candidate(item.FullPath, item.SizeBytes, item.ModifiedUtc, evidence));
        }

        return BuildGroups(candidates, options);
    }

    private static IReadOnlyList<SimilarityGroup> BuildGroups(
        IReadOnlyList<Candidate> source,
        MusicDuplicateOptions options)
    {
        var clusters = new List<Candidate[]>();
        foreach (IGrouping<MusicKey, Candidate> bucket in source.GroupBy(static candidate => new MusicKey(
                     candidate.Evidence.NormalizedTitle,
                     candidate.Evidence.NormalizedArtist)))
        {
            Candidate[] ordered = bucket
                .OrderBy(static candidate => candidate.Evidence.Duration.Ticks)
                .ThenBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.FullPath, StringComparer.Ordinal)
                .ToArray();
            var cluster = new List<Candidate>();
            foreach (Candidate candidate in ordered)
            {
                if (cluster.Count > 0 &&
                    candidate.Evidence.Duration - cluster[0].Evidence.Duration > options.MaximumDurationDifference)
                {
                    AddCluster(clusters, cluster);
                    cluster.Clear();
                }

                cluster.Add(candidate);
            }

            AddCluster(clusters, cluster);
        }

        var orderedClusters = clusters
            .Select(static cluster => (Cluster: cluster, Reference: ChooseReference(cluster)))
            .OrderBy(static group => group.Reference.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Reference.FullPath, StringComparer.Ordinal)
            .ToArray();
        var groups = new List<SimilarityGroup>(orderedClusters.Length);
        for (int groupIndex = 0; groupIndex < orderedClusters.Length; groupIndex++)
        {
            (Candidate[] cluster, Candidate reference) = orderedClusters[groupIndex];
            SimilarityItem[] items = cluster
                .Select(candidate => (Candidate: candidate, Difference: DurationDifference(
                    candidate.Evidence.Duration,
                    reference.Evidence.Duration)))
                .OrderBy(item => ReferenceEquals(item.Candidate, reference) ? 0 : 1)
                .ThenBy(static item => item.Difference.Ticks)
                .ThenBy(static item => item.Candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Candidate.FullPath, StringComparer.Ordinal)
                .Select(static item => BuildItem(item.Candidate, item.Difference))
                .ToArray();
            groups.Add(new SimilarityGroup
            {
                Id = $"music-{groupIndex + 1:0000}",
                ReferenceItem = items[0],
                Items = items,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Type"] = "Music metadata",
                    ["Confidence"] = "High",
                    ["MaximumDurationDifference"] = options.MaximumDurationDifference.ToString(
                        "c",
                        CultureInfo.InvariantCulture),
                },
            });
        }

        return groups;
    }

    private static void AddCluster(ICollection<Candidate[]> clusters, IReadOnlyCollection<Candidate> cluster)
    {
        if (cluster.Count >= 2)
        {
            clusters.Add(cluster.ToArray());
        }
    }

    private static SimilarityItem BuildItem(Candidate candidate, TimeSpan difference) => new()
    {
        FullPath = candidate.FullPath,
        SizeBytes = candidate.SizeBytes,
        ModifiedUtc = candidate.ModifiedUtc,
        SimilarityPercent = 100,
        Evidence = candidate.Evidence,
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Confidence"] = "High",
            ["Title"] = candidate.Evidence.Title,
            ["Artist"] = candidate.Evidence.Artist,
            ["AlbumArtist"] = candidate.Evidence.AlbumArtist,
            ["Album"] = candidate.Evidence.Album,
            ["TrackNumber"] = candidate.Evidence.TrackNumber.ToString(CultureInfo.InvariantCulture),
            ["Year"] = candidate.Evidence.Year.ToString(CultureInfo.InvariantCulture),
            ["Genres"] = string.Join(", ", candidate.Evidence.Genres),
            ["Bitrate"] = candidate.Evidence.Bitrate.ToString(CultureInfo.InvariantCulture),
            ["Duration"] = candidate.Evidence.Duration.ToString("c", CultureInfo.InvariantCulture),
            ["DurationDifference"] = difference.ToString("c", CultureInfo.InvariantCulture),
        },
    };

    private static Candidate ChooseReference(IEnumerable<Candidate> cluster) => cluster
        .OrderByDescending(static candidate => candidate.Evidence.Bitrate)
        .ThenByDescending(static candidate => candidate.SizeBytes)
        .ThenBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static candidate => candidate.FullPath, StringComparer.Ordinal)
        .First();

    private static MusicSimilarityEvidence BuildEvidence(MusicMetadata? metadata)
    {
        if (metadata is null || metadata.Duration <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The music metadata is structurally invalid.");
        }

        string normalizedTitle = NormalizeField(metadata.Title);
        string normalizedArtist = NormalizeField(metadata.Artist);
        string normalizedAlbumArtist = NormalizeField(metadata.AlbumArtist);
        string normalizedChosenArtist = normalizedArtist.Length > 0 ? normalizedArtist : normalizedAlbumArtist;
        if (normalizedTitle.Length == 0 || normalizedChosenArtist.Length == 0)
        {
            throw new MissingRequiredMusicMetadataException();
        }

        bool contributingArtistChosen = normalizedArtist.Length > 0;
        return new MusicSimilarityEvidence(
            normalizedTitle,
            normalizedChosenArtist,
            metadata.Title ?? string.Empty,
            contributingArtistChosen ? metadata.Artist ?? string.Empty : metadata.AlbumArtist ?? string.Empty,
            metadata.AlbumArtist ?? string.Empty,
            metadata.Album ?? string.Empty,
            metadata.TrackNumber,
            metadata.Year,
            metadata.Genres ?? [],
            metadata.Bitrate,
            metadata.Duration);
    }

    private static string NormalizeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Normalize(value);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string Normalize(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var result = new StringBuilder(normalized.Length);
        bool pendingSpace = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || IsPunctuation(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(rune);
        }

        return result.ToString();
    }

    private static bool IsPunctuation(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.ConnectorPunctuation or
        UnicodeCategory.DashPunctuation or
        UnicodeCategory.OpenPunctuation or
        UnicodeCategory.ClosePunctuation or
        UnicodeCategory.InitialQuotePunctuation or
        UnicodeCategory.FinalQuotePunctuation or
        UnicodeCategory.OtherPunctuation;

    private static bool IsValid(MusicSimilarityEvidence evidence) =>
        !string.IsNullOrEmpty(evidence.NormalizedTitle) &&
        !string.IsNullOrEmpty(evidence.NormalizedArtist) &&
        evidence.Title is not null &&
        evidence.Artist is not null &&
        evidence.AlbumArtist is not null &&
        evidence.Album is not null &&
        evidence.Genres is not null &&
        evidence.Duration > TimeSpan.Zero;

    private static bool EvidenceEquals(MusicSimilarityEvidence left, MusicSimilarityEvidence right) =>
        string.Equals(left.NormalizedTitle, right.NormalizedTitle, StringComparison.Ordinal) &&
        string.Equals(left.NormalizedArtist, right.NormalizedArtist, StringComparison.Ordinal) &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        string.Equals(left.Artist, right.Artist, StringComparison.Ordinal) &&
        string.Equals(left.AlbumArtist, right.AlbumArtist, StringComparison.Ordinal) &&
        string.Equals(left.Album, right.Album, StringComparison.Ordinal) &&
        left.TrackNumber == right.TrackNumber &&
        left.Year == right.Year &&
        left.Genres.SequenceEqual(right.Genres, StringComparer.Ordinal) &&
        left.Bitrate == right.Bitrate &&
        left.Duration == right.Duration;

    private static TimeSpan DurationDifference(TimeSpan left, TimeSpan right) =>
        left >= right ? left - right : right - left;

    private static void ValidateOptions(MusicDuplicateOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumDurationDifference < TimeSpan.Zero)
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

    private static SkippedPath UnreadableSkip(string path) => new()
    {
        Path = path,
        Reason = "Could not read music metadata.",
    };

    private static SkippedPath MissingMetadataSkip(string path) => new()
    {
        Path = path,
        Reason = "Required music metadata is missing.",
    };

    private sealed record Candidate(
        string FullPath,
        long SizeBytes,
        DateTime ModifiedUtc,
        MusicSimilarityEvidence Evidence);

    private readonly record struct MusicKey(string NormalizedTitle, string NormalizedArtist);

    private readonly record struct FileSnapshot(long SizeBytes, DateTime ModifiedUtc);
}
