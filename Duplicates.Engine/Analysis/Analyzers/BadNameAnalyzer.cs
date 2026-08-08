using System.Diagnostics;
using System.Text;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed record BadNameFinding(
    string FullPath,
    string CurrentName,
    string SuggestedName,
    IReadOnlyList<string> Reasons);

public sealed class BadNameAnalyzer
{
    private static readonly HashSet<char> InvalidFileNameCharacters =
        Path.GetInvalidFileNameChars().ToHashSet();

    private static readonly HashSet<string> ReservedDosNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PathFinding>();
        foreach (InventoryFile file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Attributes.HasFlag(FileAttributes.Directory) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            BadNameFinding? detected = Detect(file.FullPath, file.FileName);
            if (detected is null)
            {
                continue;
            }

            findings.Add(new PathFinding
            {
                FullPath = detected.FullPath,
                Kind = PathFindingKind.File,
                Reason = string.Join("; ", detected.Reasons),
                Suggestion = detected.SuggestedName,
                SizeBytes = file.SizeBytes,
                CreatedUtc = file.CreatedUtc,
                ModifiedUtc = file.ModifiedUtc,
                Metadata = new Dictionary<string, string>
                {
                    ["CurrentName"] = detected.CurrentName,
                },
            });
        }

        stopwatch.Stop();
        return Task.FromResult(new AnalysisResult
        {
            Findings = findings
                .OrderBy(static finding => finding.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static finding => finding.FullPath, StringComparer.Ordinal)
                .ToArray(),
            Groups = [],
            SkippedPaths = inventory.SkippedPaths,
            Elapsed = stopwatch.Elapsed,
        });
    }

    public static BadNameFinding? Detect(string fullPath, string currentName)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(currentName);

        string inspectedName = NormalizeForInspection(currentName);
        string trimmedInspectedName = TrimUnsafeEnds(inspectedName);
        List<NameSegment> safeSegments = CreateSafeSegments(currentName);
        string sanitizedInspectedName = JoinInspected(safeSegments);
        List<string> reasons = GetReasons(
            inspectedName,
            trimmedInspectedName,
            sanitizedInspectedName);

        if (reasons.Count == 0)
        {
            return null;
        }

        string suggestion = EnsureSafeSuggestion(BuildSuggestion(safeSegments));
        return new BadNameFinding(fullPath, currentName, suggestion, reasons);
    }

    private static List<string> GetReasons(
        string inspectedName,
        string trimmedInspectedName,
        string sanitizedInspectedName)
    {
        var reasons = new List<string>(7);

        if (inspectedName.Any(IsControlCharacter))
        {
            reasons.Add("Contains control characters");
        }

        if (inspectedName.Any(IsBidirectionalControlCharacter))
        {
            reasons.Add("Contains bidirectional control characters");
        }

        if (inspectedName.Any(IsOtherInvalidFileNameCharacter))
        {
            reasons.Add("Contains invalid Windows characters");
        }

        if (inspectedName.Length > 0 &&
            (char.IsWhiteSpace(inspectedName[0]) || char.IsWhiteSpace(inspectedName[^1])))
        {
            reasons.Add("Has leading or trailing whitespace");
        }

        if (TrimEndWhitespace(inspectedName).EndsWith('.'))
        {
            reasons.Add("Ends with a dot");
        }

        bool usesReservedName = UsesReservedDosName(inspectedName);
        if (usesReservedName)
        {
            reasons.Add("Uses a reserved DOS device name");
        }

        bool hasEmptyStem = HasEmptyStem(trimmedInspectedName) || HasEmptyStem(sanitizedInspectedName);
        if (hasEmptyStem)
        {
            reasons.Add("Has an empty file name stem");
        }

        return reasons;
    }

    private static string EnsureSafeSuggestion(string suggestion)
    {
        if (!HasDefinedRisk(suggestion))
        {
            return suggestion;
        }

        string inspectedSuggestion = NormalizeForInspection(suggestion);
        string fallback = BuildSuggestion(CreateSafeSegments(inspectedSuggestion));
        return HasDefinedRisk(fallback) ? "file" : fallback;
    }

    private static bool HasDefinedRisk(string name)
    {
        string inspectedName = NormalizeForInspection(name);
        List<NameSegment> safeSegments = CreateSafeSegments(name);
        return GetReasons(
            inspectedName,
            TrimUnsafeEnds(inspectedName),
            JoinInspected(safeSegments)).Count > 0;
    }

    private static string NormalizeForInspection(string name)
    {
        var builder = new StringBuilder(name.Length);
        int runStart = 0;
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (char.IsHighSurrogate(current) &&
                index + 1 < name.Length &&
                char.IsLowSurrogate(name[index + 1]))
            {
                index++;
                continue;
            }

            if (!char.IsSurrogate(current))
            {
                continue;
            }

            AppendNormalizedRun(builder, name, runStart, index - runStart);
            builder.Append(current);
            runStart = index + 1;
        }

        if (runStart == 0)
        {
            return name.Normalize(NormalizationForm.FormKC);
        }

        AppendNormalizedRun(builder, name, runStart, name.Length - runStart);
        return builder.ToString();
    }

    private static void AppendNormalizedRun(
        StringBuilder builder,
        string name,
        int start,
        int length)
    {
        if (length > 0)
        {
            builder.Append(name.Substring(start, length).Normalize(NormalizationForm.FormKC));
        }
    }

    private static List<NameSegment> CreateSafeSegments(string name)
    {
        var segments = new List<NameSegment>(name.Length);
        for (int index = 0; index < name.Length; index++)
        {
            int length = char.IsHighSurrogate(name[index]) &&
                index + 1 < name.Length &&
                char.IsLowSurrogate(name[index + 1])
                    ? 2
                    : 1;
            string original = name.Substring(index, length);
            string inspected = length == 1 && char.IsSurrogate(original[0])
                ? original
                : original.Normalize(NormalizationForm.FormKC);
            index += length - 1;

            if (inspected.Any(IsControlCharacter) || inspected.Any(IsBidirectionalControlCharacter))
            {
                continue;
            }

            segments.Add(inspected.Any(IsOtherInvalidFileNameCharacter)
                ? new NameSegment("_", "_")
                : new NameSegment(original, inspected));
        }

        while (segments.Count > 0 && IsWhitespaceSegment(segments[0]))
        {
            segments.RemoveAt(0);
        }

        while (segments.Count > 0 && IsUnsafeTrailingSegment(segments[^1]))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return segments;
    }

    private static string BuildSuggestion(List<NameSegment> safeSegments)
    {
        var segments = new List<NameSegment>(safeSegments);
        string inspected = JoinInspected(segments);
        if (HasEmptyStem(inspected))
        {
            segments.Insert(0, new NameSegment("file", "file"));
            inspected = "file" + inspected;
        }

        if (UsesReservedDosName(inspected))
        {
            int extensionSegment = segments.FindIndex(static segment => segment.Inspected.Contains('.'));
            int insertionIndex = extensionSegment < 0 ? segments.Count : extensionSegment;
            segments.Insert(insertionIndex, new NameSegment("_file", "_file"));
        }

        return string.Concat(segments.Select(static segment => segment.Original));
    }

    private static string JoinInspected(IEnumerable<NameSegment> segments) =>
        string.Concat(segments.Select(static segment => segment.Inspected));

    private static bool IsWhitespaceSegment(NameSegment segment) =>
        segment.Inspected.Length > 0 && segment.Inspected.All(char.IsWhiteSpace);

    private static bool IsUnsafeTrailingSegment(NameSegment segment) =>
        segment.Inspected.Length > 0 &&
        segment.Inspected.All(static character => char.IsWhiteSpace(character) || character == '.');

    private static string TrimUnsafeEnds(string name)
    {
        int start = 0;
        while (start < name.Length && char.IsWhiteSpace(name[start]))
        {
            start++;
        }

        int end = name.Length;
        while (end > start && (char.IsWhiteSpace(name[end - 1]) || name[end - 1] == '.'))
        {
            end--;
        }

        return name[start..end];
    }

    private static string TrimEndWhitespace(string name)
    {
        int end = name.Length;
        while (end > 0 && char.IsWhiteSpace(name[end - 1]))
        {
            end--;
        }

        return name[..end];
    }

    private static bool HasEmptyStem(string name)
    {
        int extensionStart = name.LastIndexOf('.');
        string stem = extensionStart >= 0 ? name[..extensionStart] : name;
        return stem.Length == 0;
    }

    private static bool UsesReservedDosName(string name)
    {
        string withoutUnsafeEnd = name.TrimEnd(' ', '.');
        int firstDot = withoutUnsafeEnd.IndexOf('.');
        string firstComponent = firstDot < 0
            ? withoutUnsafeEnd
            : withoutUnsafeEnd[..firstDot];
        firstComponent = firstComponent.TrimEnd(' ', '.');
        return ReservedDosNames.Contains(firstComponent);
    }

    private static bool IsControlCharacter(char character) =>
        character is >= '\u0000' and <= '\u001F' or '\u007F';

    private static bool IsBidirectionalControlCharacter(char character) =>
        character is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

    private static bool IsOtherInvalidFileNameCharacter(char character) =>
        InvalidFileNameCharacters.Contains(character) &&
        !IsControlCharacter(character) &&
        !IsBidirectionalControlCharacter(character);

    private sealed record NameSegment(string Original, string Inspected);
}
