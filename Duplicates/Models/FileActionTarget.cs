namespace Duplicates.Models;

public sealed record BadExtensionContentConstraint(
    DateTime ModifiedUtc,
    string DetectedType,
    string RecommendedExtension);

public sealed record FileActionTarget(
    string FullPath,
    long SizeBytes,
    FileActionTargetKind Kind,
    string? ExpectedInvalidLinkReason = null,
    BadExtensionContentConstraint? ExpectedBadExtensionContent = null,
    DateTime? ExpectedModifiedUtc = null);

public sealed record FileOperationResult(
    string SourcePath,
    string? DestinationPath,
    FileActionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record FileOperationSummary(
    IReadOnlyList<FileOperationResult> Results,
    long SucceededBytes);

public sealed record FileOperationProgress(
    int ProcessedCount,
    int TotalCount,
    string CurrentPath,
    long SucceededBytes);

public sealed class FileOperationCanceledException : OperationCanceledException
{
    public FileOperationCanceledException(
        FileOperationSummary summary,
        CancellationToken cancellationToken)
        : base("The file operation was cancelled.", null, cancellationToken)
    {
        Summary = summary;
    }

    public FileOperationSummary Summary { get; }
}
