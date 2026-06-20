namespace Duplicates.Models;

public sealed record DeleteProgress(int ProcessedCount, int TotalCount, string CurrentPath, long DeletedBytes);
