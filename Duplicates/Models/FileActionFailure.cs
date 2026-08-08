namespace Duplicates.Models;

public sealed record FileActionFailure(
    string Path,
    string Reason,
    string? RecoveryPath = null);
