namespace Duplicates.Models;

public readonly record struct FileSystemIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);
