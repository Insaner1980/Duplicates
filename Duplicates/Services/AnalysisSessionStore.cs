using Duplicates.Engine.Analysis;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed record AnalysisSession(
    ToolKind Tool,
    AnalysisScope Scope,
    AnalysisResult Result,
    DateTimeOffset CompletedAt);

public sealed class AnalysisSessionStore
{
    public AnalysisSession? CurrentSession { get; private set; }

    public event EventHandler<AnalysisSession?>? ResultChanged;

    public void SetCompleted(ToolKind tool, AnalysisScope scope, AnalysisResult result)
    {
        CurrentSession = new AnalysisSession(tool, scope, result, DateTimeOffset.Now);
        ResultChanged?.Invoke(this, CurrentSession);
    }

    public void Clear()
    {
        CurrentSession = null;
        ResultChanged?.Invoke(this, null);
    }
}
