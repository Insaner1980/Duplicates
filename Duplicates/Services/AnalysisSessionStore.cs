using Duplicates.Engine.Analysis;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed record AnalysisSession(
    ToolKind Tool,
    AnalysisScope Scope,
    ToolOptions ToolOptions,
    AnalysisResult Result,
    DateTimeOffset CompletedAtUtc);

public sealed class AnalysisSessionStore
{
    public AnalysisSession? CurrentSession { get; private set; }

    public event EventHandler<AnalysisSession?>? ResultChanged;

    public void SetCompleted(ToolKind tool, AnalysisScope scope, ToolOptions toolOptions, AnalysisResult result)
    {
        CurrentSession = new AnalysisSession(tool, CopyScope(scope), toolOptions, result, DateTimeOffset.UtcNow);
        ResultChanged?.Invoke(this, CurrentSession);
    }

    public void SetCompleted(ToolKind tool, AnalysisScope scope, AnalysisResult result)
    {
        SetCompleted(tool, scope, DefaultOptions(tool), result);
    }

    public void Clear()
    {
        CurrentSession = null;
        ResultChanged?.Invoke(this, null);
    }

    private static AnalysisScope CopyScope(AnalysisScope scope) => new()
    {
        IncludedFolders = scope.IncludedFolders.ToArray(),
        IncludedFiles = scope.IncludedFiles.ToArray(),
        ExcludedPaths = scope.ExcludedPaths.ToArray(),
        IncludeSubfolders = scope.IncludeSubfolders,
        IgnoreHiddenFiles = scope.IgnoreHiddenFiles,
        IgnoreSystemFiles = scope.IgnoreSystemFiles,
    };

    private static ToolOptions DefaultOptions(ToolKind tool) => tool switch
    {
        ToolKind.BigFiles => new LargeFileToolOptions(1_073_741_824),
        ToolKind.TemporaryFiles => new TemporaryFileToolOptions(TimeSpan.FromDays(7), DateTime.UtcNow),
        ToolKind.SimilarImages => new SimilarImageToolOptions(10),
        ToolKind.SimilarVideos => new SimilarVideoToolOptions(10),
        ToolKind.MusicDuplicates => new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
        _ => new NoToolOptions(),
    };
}
