using System.Diagnostics;
using Duplicates.Engine.Models;

namespace Duplicates.Engine;

internal sealed class ScanProgressReporter
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProgress<ScanProgress>? _inner;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _gate = new();
    private TimeSpan _lastReport = TimeSpan.MinValue;

    public ScanProgressReporter(IProgress<ScanProgress>? inner)
    {
        _inner = inner;
    }

    public void Report(ScanProgress progress, bool force = false)
    {
        if (_inner is null)
        {
            return;
        }

        lock (_gate)
        {
            TimeSpan elapsed = _stopwatch.Elapsed;
            if (!force && _lastReport != TimeSpan.MinValue && elapsed - _lastReport < MinimumInterval)
            {
                return;
            }

            _lastReport = elapsed;
            _inner.Report(progress);
        }
    }
}
