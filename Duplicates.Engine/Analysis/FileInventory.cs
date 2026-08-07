using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis;

public sealed record FileInventory(
    IReadOnlyList<InventoryFile> Files,
    IReadOnlyList<InventoryDirectory> Directories,
    IReadOnlyList<string> IncludedRootPaths,
    IReadOnlyList<string> ReparsePointPaths,
    IReadOnlyList<SkippedPath> SkippedPaths);

public sealed record InventoryFile(
    string FullPath,
    string FileName,
    string Extension,
    string DirectoryPath,
    long SizeBytes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    FileAttributes Attributes);

public sealed record InventoryDirectory(
    string FullPath,
    string Name,
    string ParentPath,
    int Depth,
    FileAttributes Attributes);
