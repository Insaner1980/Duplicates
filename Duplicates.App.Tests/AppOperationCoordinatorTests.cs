using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class AppOperationCoordinatorTests
{
    [Fact]
    public void TryAcquire_IsAtomicAndDoesNotQueueSecondOperation()
    {
        var coordinator = new AppOperationCoordinator();
        var descriptor = new AppOperationDescriptor(AppOperationKind.ExactScan);

        bool acquired = coordinator.TryAcquire(descriptor, static () => { }, out IAppOperationLease? lease);
        bool secondAcquired = coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun),
            static () => { },
            out IAppOperationLease? secondLease);

        Assert.True(acquired);
        Assert.NotNull(lease);
        Assert.Equal(descriptor, coordinator.ActiveOperation);
        Assert.False(secondAcquired);
        Assert.Null(secondLease);
        lease.Dispose();
    }

    [Fact]
    public async Task TryAcquire_ParallelCallersProduceOneOwner()
    {
        var coordinator = new AppOperationCoordinator();
        var start = new ManualResetEventSlim();
        var leases = new IAppOperationLease?[32];

        Task<bool>[] attempts = Enumerable.Range(0, leases.Length)
            .Select(index => Task.Run(() =>
            {
                start.Wait();
                return coordinator.TryAcquire(
                    new AppOperationDescriptor(AppOperationKind.ExactResultsAction),
                    static () => { },
                    out leases[index]);
            }))
            .ToArray();

        start.Set();
        bool[] results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(static result => result));
        Assert.Single(leases, static lease => lease is not null);
        leases.Single(static lease => lease is not null)!.Dispose();
    }

    [Fact]
    public async Task RequestCancellation_ParallelCallsInvokeCallbackOnceOutsideLock()
    {
        var coordinator = new AppOperationCoordinator();
        int callbackCount = 0;
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun),
            () =>
            {
                Assert.NotNull(coordinator.ActiveOperation);
                Interlocked.Increment(ref callbackCount);
            },
            out IAppOperationLease? lease));

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(coordinator.RequestCancellation)));

        Assert.Equal(1, callbackCount);
        lease!.Dispose();
    }

    [Fact]
    public async Task WaitForIdleAsync_CompletesOnlyAfterOwningLeaseIsDisposed()
    {
        var coordinator = new AppOperationCoordinator();
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.VideoOptimization),
            static () => { },
            out IAppOperationLease? lease));
        Task wait = coordinator.WaitForIdleAsync();

        Assert.False(wait.IsCompleted);
        lease!.Dispose();
        await wait;

        Assert.Null(coordinator.ActiveOperation);
        Assert.True(coordinator.WaitForIdleAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void LeaseDisposal_IsIdempotentAndOldLeaseCannotReleaseNewOwner()
    {
        var coordinator = new AppOperationCoordinator();
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.ExactScan),
            static () => { },
            out IAppOperationLease? first));

        first!.Dispose();
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun),
            static () => { },
            out IAppOperationLease? second));

        first.Dispose();

        Assert.Equal(AppOperationKind.AnalysisRun, coordinator.ActiveOperation?.Kind);
        second!.Dispose();
    }

    [Fact]
    public void ActiveOperationChanged_ReportsAcquireAndRelease()
    {
        var coordinator = new AppOperationCoordinator();
        var observed = new List<AppOperationDescriptor?>();
        coordinator.ActiveOperationChanged += (_, _) => observed.Add(coordinator.ActiveOperation);

        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.ExifCleaning),
            static () => { },
            out IAppOperationLease? lease));
        lease!.Dispose();

        Assert.Equal(2, observed.Count);
        Assert.Equal(AppOperationKind.ExifCleaning, observed[0]?.Kind);
        Assert.Null(observed[1]);
    }
}
