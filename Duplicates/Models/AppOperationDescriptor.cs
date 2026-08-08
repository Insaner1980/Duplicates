namespace Duplicates.Models;

public sealed record AppOperationDescriptor(
    AppOperationKind Kind,
    bool UsesMediaFingerprintCache = false);
