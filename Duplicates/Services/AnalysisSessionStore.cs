using Duplicates.Engine.Analysis;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed record AnalysisSession(
    ToolKind Tool,
    AnalysisScope Scope,
    IToolOptions ToolOptions,
    AnalysisResult Result,
    DateTimeOffset CompletedAtUtc);

public sealed class AnalysisSessionStore
{
    public AnalysisSession? CurrentSession { get; private set; }

    public event EventHandler<AnalysisSession?>? ResultChanged;

    public void SetCompleted(ToolKind tool, AnalysisScope scope, IToolOptions toolOptions, AnalysisResult result)
    {
        CurrentSession = new AnalysisSession(tool, CopyScope(scope), toolOptions, result, DateTimeOffset.UtcNow);
        ResultChanged?.Invoke(this, CurrentSession);
    }

    public void SetCompleted(ToolKind tool, AnalysisScope scope, AnalysisResult result)
    {
        SetCompleted(tool, scope, DefaultOptions(tool), result);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S4220", Justification = "The nullable session payload signals that the current session has been cleared.")]
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

    private static IToolOptions DefaultOptions(ToolKind tool) => tool switch
    {
        ToolKind.BigFiles => new LargeFileToolOptions(1_073_741_824),
        ToolKind.SimilarImages => new SimilarImageToolOptions(8),
        ToolKind.SimilarVideos => new SimilarVideoToolOptions(9),
        ToolKind.MusicDuplicates => new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
        _ => new NoToolOptions(),
    };
}
