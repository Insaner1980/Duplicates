namespace Duplicates.Models;

public sealed record DeleteSummary(int DeletedCount, long DeletedBytes, IReadOnlyList<FileActionFailure> Failures);
