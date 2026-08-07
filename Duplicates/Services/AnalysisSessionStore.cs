using Duplicates.Engine.Analysis;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed record AnalysisSession(
    ToolKind Tool,
    AnalysisScope Scope,
    AnalysisResult Result,
    DateTimeOffset CompletedAtUtc);

public sealed class AnalysisSessionStore
{
    public AnalysisSession? CurrentSession { get; private set; }

    public event EventHandler<AnalysisSession?>? ResultChanged;

    public void SetCompleted(ToolKind tool, AnalysisScope scope, AnalysisResult result)
    {
        CurrentSession = new AnalysisSession(tool, CopyScope(scope), result, DateTimeOffset.UtcNow);
        ResultChanged?.Invoke(this, CurrentSession);
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
}
