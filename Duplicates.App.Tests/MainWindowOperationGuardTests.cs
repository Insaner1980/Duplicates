using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class MainWindowOperationGuardTests
{
    [Fact]
    public async Task ConfirmDepartureAsync_DeclineKeepsOperationAndFutureDeparturePromptsAgain()
    {
        var coordinator = new AppOperationCoordinator();
        using IAppOperationLease lease = Acquire(coordinator, AppOperationKind.ExactScan, static () => { });
        using var guard = new MainWindowOperationGuard(coordinator);
        int promptCount = 0;

        bool first = await guard.ConfirmDepartureAsync(() =>
        {
            promptCount++;
            return Task.FromResult(false);
        });
        bool second = await guard.ConfirmDepartureAsync(() =>
        {
            promptCount++;
            return Task.FromResult(false);
        });

        Assert.False(first);
        Assert.False(second);
        Assert.Equal(2, promptCount);
        Assert.NotNull(coordinator.ActiveOperation);
    }

    [Fact]
    public async Task ConfirmDepartureAsync_AcceptRemembersOnlyCurrentLease()
    {
        var coordinator = new AppOperationCoordinator();
        using var guard = new MainWindowOperationGuard(coordinator);
        int promptCount = 0;
        IAppOperationLease firstLease = Acquire(coordinator, AppOperationKind.AnalysisRun, static () => { });

        Assert.True(await guard.ConfirmDepartureAsync(Prompt));
        Assert.True(await guard.ConfirmDepartureAsync(Prompt));
        firstLease.Dispose();
        using IAppOperationLease secondLease = Acquire(coordinator, AppOperationKind.AnalysisRun, static () => { });
        Assert.True(await guard.ConfirmDepartureAsync(Prompt));

        Assert.Equal(2, promptCount);

        Task<bool> Prompt()
        {
            promptCount++;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task ConfirmDepartureAsync_OperationCompletionWhileDialogOpenAllowsNavigation()
    {
        var coordinator = new AppOperationCoordinator();
        IAppOperationLease lease = Acquire(coordinator, AppOperationKind.ExactResultsAction, static () => { });
        using var guard = new MainWindowOperationGuard(coordinator);
        var promptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> confirmation = guard.ConfirmDepartureAsync(async () =>
        {
            promptShown.SetResult();
            return await promptResult.Task;
        });
        await promptShown.Task;
        lease.Dispose();
        promptResult.SetResult(false);

        Assert.True(await confirmation);
    }

    [Fact]
    public async Task SharedDialogGuardPreventsNavigationAndClosePromptsFromStacking()
    {
        var coordinator = new AppOperationCoordinator();
        using IAppOperationLease lease = Acquire(coordinator, AppOperationKind.AnalysisResultsAction, static () => { });
        using var guard = new MainWindowOperationGuard(coordinator);
        var promptShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> navigation = guard.ConfirmDepartureAsync(async () =>
        {
            promptShown.SetResult();
            return await promptResult.Task;
        });
        await promptShown.Task;

        bool close = await guard.ConfirmCloseAsync(static () => Task.FromResult(true));
        promptResult.SetResult(false);

        Assert.False(close);
        Assert.False(await navigation);
    }

    [Fact]
    public async Task ConfirmCloseAsync_DeclineDoesNotRequestCancellation()
    {
        var coordinator = new AppOperationCoordinator();
        int cancellationCount = 0;
        using IAppOperationLease lease = Acquire(
            coordinator,
            AppOperationKind.VideoOptimization,
            () => cancellationCount++);
        using var guard = new MainWindowOperationGuard(coordinator);

        bool close = await guard.ConfirmCloseAsync(static () => Task.FromResult(false));

        Assert.False(close);
        Assert.Equal(0, cancellationCount);
        Assert.NotNull(coordinator.ActiveOperation);
    }

    [Fact]
    public async Task ConfirmCloseAsync_AcceptRequestsCancellationOnceAndWaitsForDeferredCleanup()
    {
        var coordinator = new AppOperationCoordinator();
        IAppOperationLease? lease = null;
        int cancellationCount = 0;
        lease = Acquire(
            coordinator,
            AppOperationKind.ExifCleaning,
            () =>
            {
                cancellationCount++;
                _ = Task.Run(async () =>
                {
                    await Task.Yield();
                    lease!.Dispose();
                });
            });
        using var guard = new MainWindowOperationGuard(coordinator);

        bool close = await guard.ConfirmCloseAsync(static () => Task.FromResult(true));

        Assert.True(close);
        Assert.Equal(1, cancellationCount);
        Assert.Null(coordinator.ActiveOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmCloseAsync_ReplacementOperationRejectsStaleConfirmation(bool accepted)
    {
        var coordinator = new AppOperationCoordinator();
        using var guard = new MainWindowOperationGuard(coordinator);
        using IAppOperationLease firstLease = Acquire(coordinator, AppOperationKind.ExactScan, static () => { });
        var promptResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> close = guard.ConfirmCloseAsync(() => promptResult.Task);
        firstLease.Dispose();
        int cancellationCount = 0;
        using IAppOperationLease replacement = Acquire(coordinator, AppOperationKind.ExifCleaning, () => cancellationCount++);
        promptResult.SetResult(accepted);

        Assert.False(await close);
        Assert.Equal(0, cancellationCount);
        Assert.NotNull(coordinator.ActiveOperation);
    }

    [Fact]
    public async Task NoActiveOperationAllowsDepartureAndCloseWithoutPrompt()
    {
        var coordinator = new AppOperationCoordinator();
        using var guard = new MainWindowOperationGuard(coordinator);
        int prompts = 0;

        bool departure = await guard.ConfirmDepartureAsync(Prompt);
        bool close = await guard.ConfirmCloseAsync(Prompt);

        Assert.True(departure);
        Assert.True(close);
        Assert.Equal(0, prompts);

        Task<bool> Prompt()
        {
            prompts++;
            return Task.FromResult(false);
        }
    }

    private static IAppOperationLease Acquire(
        IAppOperationCoordinator coordinator,
        AppOperationKind kind,
        Action requestCancellation)
    {
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(kind),
            requestCancellation,
            out IAppOperationLease? lease));
        return lease!;
    }
}
