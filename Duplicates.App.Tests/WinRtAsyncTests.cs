using Duplicates.Services;
using Windows.Foundation;

namespace Duplicates.App.Tests;

public sealed class WinRtAsyncTests
{
    [Fact]
    public async Task AwaitAndCloseAsync_DisposesLateResultAfterCancellation()
    {
        using var result = new DisposableResult();
        var operation = new ControlledOperation<DisposableResult>(result);
        using var cancellation = new CancellationTokenSource();
        Task<DisposableResult> task = WinRtAsync.AwaitAndCloseAsync(operation, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, result.DisposeCount);
        Assert.Equal(1, operation.CloseCount);
    }

    [Fact]
    public async Task AwaitAndCloseAsync_LeavesSuccessfulResultOwnedByCaller()
    {
        using var result = new DisposableResult();
        var operation = new ControlledOperation<DisposableResult>(result);
        Task<DisposableResult> task = WinRtAsync.AwaitAndCloseAsync(operation, CancellationToken.None);

        operation.Complete();

        Assert.Same(result, await task);
        Assert.Equal(0, result.DisposeCount);
        Assert.Equal(1, operation.CloseCount);
    }

    [Theory]
    [InlineData(AsyncStatus.Completed)]
    [InlineData(AsyncStatus.Canceled)]
    public async Task AwaitAndCloseAsync_ProgressActionWaitsForNativeCompletionAfterCancellation(
        AsyncStatus terminalStatus)
    {
        var operation = new ControlledProgressAction();
        using var cancellation = new CancellationTokenSource();
        var progress = new List<double>();
        Task task = WinRtAsync.AwaitAndCloseAsync(
            operation,
            new InlineProgress(progress.Add),
            cancellation.Token);

        operation.Report(25);
        cancellation.Cancel();
        try
        {
            Assert.Equal(1, operation.CancelCount);
            Assert.False(task.IsCompleted);
            Assert.Equal(0, operation.CloseCount);
            Assert.Equal([25d], progress);
        }
        finally
        {
            operation.Complete(terminalStatus);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, operation.CloseCount);
    }

    [Fact]
    public async Task AwaitAndCloseAsync_ProgressActionPreservesNativeFailureAndCloses()
    {
        var operation = new ControlledProgressAction();
        Task task = WinRtAsync.AwaitAndCloseAsync(
            operation,
            progress: null,
            CancellationToken.None);

        operation.Complete(AsyncStatus.Error);

        Assert.Same(operation.ErrorCode, await Assert.ThrowsAsync<IOException>(() => task));
        Assert.Equal(1, operation.CloseCount);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class ControlledProgressAction : IAsyncActionWithProgress<double>
    {
        public AsyncActionWithProgressCompletedHandler<double>? Completed { get; set; }

        public AsyncActionProgressHandler<double>? Progress { get; set; }

        public Exception ErrorCode { get; } = new IOException("Controlled native failure.");

        public uint Id => 1;

        public AsyncStatus Status { get; private set; } = AsyncStatus.Started;

        public int CancelCount { get; private set; }

        public int CloseCount { get; private set; }

        public void GetResults() { }

        public void Cancel() => CancelCount++;

        public void Close() => CloseCount++;

        public void Report(double value) => Progress?.Invoke(this, value);

        public void Complete(AsyncStatus status)
        {
            Status = status;
            Completed?.Invoke(this, Status);
        }
    }

    private sealed class DisposableResult : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ControlledOperation<T>(T result) : IAsyncOperation<T>
    {
        public AsyncOperationCompletedHandler<T>? Completed { get; set; }

        public Exception ErrorCode => null!;

        public uint Id => 1;

        public AsyncStatus Status { get; private set; } = AsyncStatus.Started;

        public int CloseCount { get; private set; }

        public T GetResults() => result;

        public void Cancel() => Complete();

        public void Close() => CloseCount++;

        public void Complete()
        {
            Status = AsyncStatus.Completed;
            Completed?.Invoke(this, Status);
        }
    }
}
