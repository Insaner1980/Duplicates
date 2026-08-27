using Duplicates.Models;

namespace Duplicates.Services;

public sealed class AppOperationCoordinator : IAppOperationCoordinator
{
    private readonly object _gate = new();
    private OperationLease? _activeLease;
    private AppOperationDescriptor? _activeOperation;
    private Action? _requestCancellation;
    private bool _cancellationRequested;
    private TaskCompletionSource? _idleCompletion;

    public AppOperationDescriptor? ActiveOperation
    {
        get
        {
            lock (_gate)
            {
                return _activeOperation;
            }
        }
    }

    public event EventHandler? ActiveOperationChanged;

    public bool TryAcquire(
        AppOperationDescriptor descriptor,
        Action requestCancellation,
        out IAppOperationLease? lease)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(requestCancellation);

        OperationLease? owner;
        lock (_gate)
        {
            if (_activeLease is not null)
            {
                lease = null;
                return false;
            }

            owner = new OperationLease(this);
            _activeLease = owner;
            _activeOperation = descriptor;
            _requestCancellation = requestCancellation;
            _cancellationRequested = false;
            _idleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lease = owner;
        }

        ActiveOperationChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RequestCancellation()
    {
        Action? callback;
        lock (_gate)
        {
            if (_activeLease is null || _cancellationRequested)
            {
                return;
            }

            _cancellationRequested = true;
            callback = _requestCancellation;
        }

        callback?.Invoke();
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _activeLease is null
                ? Task.CompletedTask
                : _idleCompletion!.Task;
        }
    }

    private void Release(OperationLease owner)
    {
        TaskCompletionSource? idleCompletion;
        lock (_gate)
        {
            if (!ReferenceEquals(_activeLease, owner))
            {
                return;
            }

            _activeLease = null;
            _activeOperation = null;
            _requestCancellation = null;
            idleCompletion = _idleCompletion;
            _idleCompletion = null;
        }

        ActiveOperationChanged?.Invoke(this, EventArgs.Empty);
        idleCompletion?.TrySetResult();
    }

    private sealed class OperationLease(AppOperationCoordinator owner) : IAppOperationLease
    {
        private AppOperationCoordinator? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(this);
        }
    }
}
