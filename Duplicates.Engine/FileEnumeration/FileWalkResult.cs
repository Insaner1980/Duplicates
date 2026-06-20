using Duplicates.Engine.Models;

namespace Duplicates.Engine.FileEnumeration;

internal sealed record FileWalkResult(IReadOnlyList<FileEntry> Files, IReadOnlyList<SkippedPath> SkippedPaths);
