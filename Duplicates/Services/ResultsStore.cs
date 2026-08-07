using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;

namespace Duplicates.Services;

public sealed record ExactResultsSession(
    ScanResult Result,
    AnalysisScope Scope,
    DateTimeOffset CompletedAtUtc);

public sealed class ResultsStore
{
    public ExactResultsSession? CurrentSession { get; private set; }

    public ScanResult? CurrentResult => CurrentSession?.Result;

    public event EventHandler<ScanResult?>? ResultChanged;

    public void SetResult(ScanResult result)
    {
        SetResult(result, new AnalysisScope(), DateTimeOffset.UtcNow);
    }

    public void SetResult(ScanResult result, AnalysisScope scope, DateTimeOffset completedAtUtc)
    {
        CurrentSession = new ExactResultsSession(result, CopyScope(scope), completedAtUtc.ToUniversalTime());
        ResultChanged?.Invoke(this, CurrentResult);
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
