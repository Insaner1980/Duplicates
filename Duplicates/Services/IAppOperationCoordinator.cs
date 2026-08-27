using Duplicates.Models;

namespace Duplicates.Services;

public interface IAppOperationLease : IDisposable
{
}

public interface IAppOperationCoordinator
{
    AppOperationDescriptor? ActiveOperation { get; }

    event EventHandler? ActiveOperationChanged;

    bool TryAcquire(
        AppOperationDescriptor descriptor,
        Action requestCancellation,
        out IAppOperationLease? lease);

    void RequestCancellation();

    Task WaitForIdleAsync();
}
