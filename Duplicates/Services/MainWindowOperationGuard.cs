namespace Duplicates.Services;

internal sealed class MainWindowOperationGuard : IDisposable
{
    private readonly IAppOperationCoordinator _coordinator;
    private readonly object _gate = new();
    private long _operationGeneration;
    private long _approvedDepartureGeneration = -1;
    private bool _dialogOpen;
    private bool _disposed;

    public MainWindowOperationGuard(IAppOperationCoordinator coordinator)
    {
        _coordinator = coordinator;
        _operationGeneration = coordinator.ActiveOperation is null ? 0 : 1;
        _coordinator.ActiveOperationChanged += ActiveOperationChanged;
    }

    public async Task<bool> ConfirmDepartureAsync(Func<Task<bool>> showConfirmationAsync)
    {
        ArgumentNullException.ThrowIfNull(showConfirmationAsync);
        long generation;
        lock (_gate)
        {
            if (_coordinator.ActiveOperation is null ||
                _approvedDepartureGeneration == _operationGeneration)
            {
                return true;
            }

            if (_dialogOpen)
            {
                return false;
            }

            _dialogOpen = true;
            generation = _operationGeneration;
        }

        bool accepted;
        try
        {
            accepted = await showConfirmationAsync().ConfigureAwait(true);
        }
        finally
        {
            lock (_gate)
            {
                _dialogOpen = false;
            }
        }

        lock (_gate)
        {
            if (_coordinator.ActiveOperation is null || generation != _operationGeneration)
            {
                return true;
            }

            if (accepted)
            {
                _approvedDepartureGeneration = generation;
            }

            return accepted;
        }
    }

    public async Task<bool> ConfirmResetAsync(
        Func<Task<bool>> showConfirmationAsync,
        Func<bool> tryResetCapturedSession)
    {
        ArgumentNullException.ThrowIfNull(showConfirmationAsync);
        ArgumentNullException.ThrowIfNull(tryResetCapturedSession);
        long generation;
        lock (_gate)
        {
            if (_coordinator.ActiveOperation is not null || _dialogOpen)
            {
                return false;
            }

            _dialogOpen = true;
            generation = _operationGeneration;
        }

        try
        {
            if (!await showConfirmationAsync().ConfigureAwait(true))
            {
                return false;
            }

            lock (_gate)
            {
                return _coordinator.ActiveOperation is null &&
                    generation == _operationGeneration &&
                    tryResetCapturedSession();
            }
        }
        finally
        {
            lock (_gate)
            {
                _dialogOpen = false;
            }
        }
    }

    public async Task<bool> ConfirmCloseAsync(Func<Task<bool>> showConfirmationAsync)
    {
        ArgumentNullException.ThrowIfNull(showConfirmationAsync);
        long generation;
        lock (_gate)
        {
            if (_coordinator.ActiveOperation is null)
            {
                return true;
            }

            if (_dialogOpen)
            {
                return false;
            }

            _dialogOpen = true;
            generation = _operationGeneration;
        }

        bool accepted;
        try
        {
            accepted = await showConfirmationAsync().ConfigureAwait(true);
            lock (_gate)
            {
                if (_coordinator.ActiveOperation is null)
                {
                    return true;
                }

                if (generation != _operationGeneration)
                {
                    return false;
                }
            }

            if (!accepted)
            {
                return false;
            }

            _coordinator.RequestCancellation();
            await _coordinator.WaitForIdleAsync().ConfigureAwait(true);
            return true;
        }
        finally
        {
            lock (_gate)
            {
                _dialogOpen = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.ActiveOperationChanged -= ActiveOperationChanged;
    }

    private void ActiveOperationChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _operationGeneration++;
            if (_coordinator.ActiveOperation is null)
            {
                _approvedDepartureGeneration = -1;
            }
        }
    }
}
