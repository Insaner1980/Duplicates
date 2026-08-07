using Duplicates.Engine.Models;
using Duplicates.Models;

namespace Duplicates.Services;

public enum ResultExportFormat
{
    Csv,
    Json,
}

public sealed record ResultExportItem(
    string FullPath,
    FileActionTargetKind Kind,
    string Reason,
    string? Suggestion,
    string? GroupId,
    double? SimilarityPercent,
    long? SizeBytes,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ResultExportSnapshot(
    ToolKind Tool,
    DateTimeOffset GeneratedAtUtc,
    string ScopeSummary,
    IReadOnlyList<ResultExportItem> Items,
    IReadOnlyList<SkippedPath> SkippedPaths);

public interface IResultExportService
{
    Task ExportAsync(
        ResultExportSnapshot snapshot,
        ResultExportFormat format,
        string destinationPath,
        CancellationToken cancellationToken);
}
