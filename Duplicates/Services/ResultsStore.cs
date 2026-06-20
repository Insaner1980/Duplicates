using Duplicates.Engine.Models;

namespace Duplicates.Services;

public sealed class ResultsStore
{
    public ScanResult? CurrentResult { get; private set; }

    public event EventHandler<ScanResult?>? ResultChanged;

    public void SetResult(ScanResult result)
    {
        CurrentResult = result;
        ResultChanged?.Invoke(this, CurrentResult);
    }

    public void Clear()
    {
        CurrentResult = null;
        ResultChanged?.Invoke(this, CurrentResult);
    }
}
