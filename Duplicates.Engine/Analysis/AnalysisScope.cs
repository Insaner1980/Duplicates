namespace Duplicates.Engine.Analysis;

public sealed record AnalysisScope
{
    public IReadOnlyList<string> IncludedFolders { get; init; } = [];

    public IReadOnlyList<string> IncludedFiles { get; init; } = [];

    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];

    public bool IncludeSubfolders { get; init; } = true;

    public bool IgnoreHiddenFiles { get; init; } = true;

    public bool IgnoreSystemFiles { get; init; } = true;
}
