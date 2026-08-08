using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class RecycleBinServiceTests
{
    [Fact]
    public async Task RecycleFileAsync_RecyclesExpectedRegularFileAndProvesPathVacant()
    {
        var platform = new FakeFileLinkPlatform();
        string path = FullPath("expected.bin");
        platform.AddOrdinaryFile(path, 42, [1]);
        FileSystemIdentity identity = platform.GetInfo(platform.OpenNoFollow(path, false)).Identity;
        var service = new RecycleBinService(platform);

        await service.RecycleFileAsync(path, identity, CancellationToken.None);

        Assert.Equal(1, platform.RecycleDispatchCount);
        Assert.False(platform.ContainsPath(path));
    }

    [Fact]
    public async Task RecycleFileAsync_IdentityMismatchNeverDispatchesShellOperation()
    {
        var platform = new FakeFileLinkPlatform();
        string path = FullPath("foreign.bin");
        platform.AddOrdinaryFile(path, 43, [1]);
        var service = new RecycleBinService(platform);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecycleFileAsync(
            path,
            new FileSystemIdentity(7, 999, 0),
            CancellationToken.None));

        Assert.Equal(0, platform.RecycleDispatchCount);
        Assert.True(platform.ContainsPath(path));
    }

    [Theory]
    [InlineData(FileAttributes.Directory)]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint)]
    public async Task RecycleFileAsync_NeverRecyclesDirectoryOrLink(FileAttributes attributes)
    {
        var platform = new FakeFileLinkPlatform();
        string path = FullPath("unsafe-entry");
        platform.AddOrdinaryFile(path, 44, [1]);
        platform.SetAttributes(path, attributes);
        FileSystemIdentity identity = platform.GetInfo(platform.OpenNoFollow(path, false)).Identity;
        var service = new RecycleBinService(platform);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecycleFileAsync(path, identity, CancellationToken.None));

        Assert.Equal(0, platform.RecycleDispatchCount);
    }

    [Fact]
    public async Task RecycleFileAsync_ShellErrorAfterExpectedIdentityDisappearsIsCommitted()
    {
        var platform = new FakeFileLinkPlatform
        {
            RecycleException = new IOException("Shell result failed."),
            RemoveBeforeRecycleException = true,
        };
        string path = FullPath("committed.bin");
        platform.AddOrdinaryFile(path, 45, [1]);
        FileSystemIdentity identity = platform.GetInfo(platform.OpenNoFollow(path, false)).Identity;
        var service = new RecycleBinService(platform);

        await service.RecycleFileAsync(path, identity, CancellationToken.None);

        Assert.False(platform.ContainsPath(path));
    }

    [Fact]
    public async Task RecycleFileAsync_ShellErrorWithExpectedIdentityStillAtPathThrowsForRollback()
    {
        var platform = new FakeFileLinkPlatform
        {
            RecycleException = new IOException("Shell result failed."),
        };
        string path = FullPath("rollback.bin");
        platform.AddOrdinaryFile(path, 46, [1]);
        FileSystemIdentity identity = platform.GetInfo(platform.OpenNoFollow(path, false)).Identity;
        var service = new RecycleBinService(platform);

        await Assert.ThrowsAsync<IOException>(() =>
            service.RecycleFileAsync(path, identity, CancellationToken.None));

        Assert.True(platform.ContainsPath(path));
    }

    [Fact]
    public async Task RecycleFileAsync_ForeignOccupantAfterShellErrorFailsClosed()
    {
        var platform = new FakeFileLinkPlatform
        {
            RecycleException = new IOException("Shell result failed."),
            ReplaceWithForeignBeforeRecycleException = true,
        };
        string path = FullPath("race.bin");
        platform.AddOrdinaryFile(path, 47, [1]);
        FileSystemIdentity identity = platform.GetInfo(platform.OpenNoFollow(path, false)).Identity;
        var service = new RecycleBinService(platform);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecycleFileAsync(path, identity, CancellationToken.None));

        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(platform.ContainsPath(path));
    }

    [Fact]
    public async Task RecycleFileAsync_ForeignOccupantDuringShellItemHandoffIsNeverDispatched()
    {
        var platform = new FakeFileLinkPlatform
        {
            ReplaceWithForeignBeforeRecycleException = true,
        };
        string path = FullPath("handoff-race.bin");
        platform.AddOrdinaryFile(path, 48, [1]);
        FileSystemIdentity identity = platform.GetInfo(platform.OpenNoFollow(path, false)).Identity;
        var service = new RecycleBinService(platform);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecycleFileAsync(path, identity, CancellationToken.None));

        Assert.Equal(0, platform.RecycleDispatchCount);
        Assert.True(platform.ContainsPath(path));
    }

    private static string FullPath(string name) => Path.Combine("C:\\Task16RecycleTests", name);
}
