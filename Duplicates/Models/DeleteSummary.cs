namespace Duplicates.Models;

public sealed record DeleteSummary(
    int DeletedCount,
    long DeletedBytes,
    IReadOnlyList<FileActionFailure> Failures,
    IReadOnlyList<string>? DeletedPaths = null);

public sealed class DeleteOperationCanceledException : OperationCanceledException
{
    public DeleteOperationCanceledException(
        DeleteSummary summary,
        CancellationToken cancellationToken)
        : base("The delete operation was cancelled.", null, cancellationToken)
    {
        Summary = summary;
    }

    public DeleteSummary Summary { get; }
}
