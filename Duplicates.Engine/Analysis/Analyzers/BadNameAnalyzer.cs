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

        string inspectedName = currentName.Normalize(NormalizationForm.FormKC);
        string sanitizedName = SanitizeOriginalName(currentName);
        string trimmedInspectedName = TrimUnsafeEnds(inspectedName);
        string trimmedSanitizedName = TrimUnsafeEnds(sanitizedName);
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

        bool hasEmptyStem = HasEmptyStem(trimmedInspectedName) || HasEmptyStem(trimmedSanitizedName);
        if (hasEmptyStem)
        {
            reasons.Add("Has an empty file name stem");
        }

        if (reasons.Count == 0)
        {
            return null;
        }

        string suggestion = trimmedSanitizedName;
        if (HasEmptyStem(suggestion))
        {
            int extensionStart = suggestion.LastIndexOf('.');
            string extension = extensionStart >= 0 ? suggestion[extensionStart..] : string.Empty;
            suggestion = "file" + extension;
        }

        if (UsesReservedDosName(suggestion))
        {
            int firstDot = suggestion.IndexOf('.');
            suggestion = firstDot < 0
                ? suggestion + "_file"
                : suggestion.Insert(firstDot, "_file");
        }

        if (string.IsNullOrEmpty(suggestion))
        {
            suggestion = "file";
        }

        return new BadNameFinding(fullPath, currentName, suggestion, reasons);
    }

    private static string SanitizeOriginalName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (Rune rune in name.EnumerateRunes())
        {
            string original = rune.ToString();
            string inspected = original.Normalize(NormalizationForm.FormKC);
            if (inspected.Any(IsControlCharacter) || inspected.Any(IsBidirectionalControlCharacter))
            {
                continue;
            }

            builder.Append(inspected.Any(IsOtherInvalidFileNameCharacter) ? "_" : original);
        }

        return builder.ToString();
    }

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
}
