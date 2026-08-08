using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.Win32.SafeHandles;
using Xunit.Sdk;

namespace Duplicates.App.Tests;

public sealed class FileLinkNativeTests
{
    [Fact]
    [Trait("Category", "Task16RealUat")]
    public void RetainedHardLinkHandle_DisposesOnlyCreatedPathAndDecrementsLinkCount()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "survivor.bin");
            string link = Path.Combine(root, "created-link.bin");
            File.WriteAllBytes(survivor, [1, 2, 3, 4]);
            var platform = new FileLinkNative();
            using IFileLinkHandle survivorHandle = platform.OpenNoFollow(survivor, false);
            uint before = platform.GetInfo(survivorHandle).LinkCount;
            IFileLinkHandle createdHandle = platform.CreateHardLinkAndOpen(link, survivor);
            uint afterCreate = platform.GetInfo(survivorHandle).LinkCount;

            platform.DeleteByHandle(createdHandle);
            createdHandle.Dispose();

            Assert.True(File.Exists(survivor));
            Assert.False(File.Exists(link));
            Assert.Equal(before + 1, afterCreate);
            Assert.Equal(before, platform.GetInfo(survivorHandle).LinkCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public void RetainedCreatedLinkHandle_PreventsPathRenameUntilTransactionEnds()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "pinned-survivor.bin");
            string link = Path.Combine(root, "pinned-link.bin");
            string moved = Path.Combine(root, "foreign-location.bin");
            File.WriteAllBytes(survivor, [1, 3, 3, 7]);
            var platform = new FileLinkNative();
            using IFileLinkHandle createdHandle = platform.CreateHardLinkAndOpen(link, survivor);

            Assert.Throws<IOException>(() => File.Move(link, moved));
            Assert.True(File.Exists(link));
            Assert.False(File.Exists(moved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public void HardLinkIdentity_UsesVolumeAndFullFileId()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "identity-survivor.bin");
            string link = Path.Combine(root, "identity-link.bin");
            File.WriteAllBytes(survivor, [5, 6, 7]);
            var platform = new FileLinkNative();
            using IFileLinkHandle survivorHandle = platform.OpenNoFollow(survivor, false);
            using IFileLinkHandle linkHandle = platform.CreateHardLinkAndOpen(link, survivor);

            Assert.Equal(platform.GetInfo(survivorHandle).Identity, platform.GetInfo(linkHandle).Identity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public void SymbolicLinkEntry_RecordsAbsoluteTargetAndFollowedIdentity()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "symbolic-survivor.bin");
            string link = Path.Combine(root, "symbolic-link.bin");
            File.WriteAllBytes(survivor, [8, 9]);
            var platform = new FileLinkNative();
            using IFileLinkHandle survivorHandle = platform.OpenNoFollow(survivor, false);
            IFileLinkHandle? linkHandle = null;
            try
            {
                linkHandle = platform.CreateSymbolicLinkAndOpen(link, survivor);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1314)
            {
                throw SkipException.ForSkip($"Symbolic links require Developer Mode or existing link privilege: {ex.Message}");
            }

            using (linkHandle)
            using (IFileLinkHandle followedHandle = platform.OpenFollow(link))
            {
                Assert.Equal(Path.GetFullPath(survivor), platform.GetSymbolicLinkTarget(link, linkHandle));
                Assert.NotEqual(platform.GetInfo(survivorHandle).Identity, platform.GetInfo(linkHandle).Identity);
                Assert.Equal(platform.GetInfo(survivorHandle).Identity, platform.GetInfo(followedHandle).Identity);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealRecycleBinUat")]
    public async Task RecycleBinService_RealNtfsRemovesExpectedRollbackPath()
    {
        string root = CreateTempRoot();
        string rollback = Path.Combine(root, ".duplicates-rollback-real-uat.bin");
        try
        {
            File.WriteAllBytes(rollback, [10, 11, 12]);
            var platform = new FileLinkNative();
            using IFileLinkHandle anchor = platform.OpenNoFollow(rollback, false);
            var service = new RecycleBinService(platform);

            await service.RecycleFileAsync(
                rollback,
                platform.GetInfo(anchor).Identity,
                CancellationToken.None);

            Assert.False(File.Exists(rollback));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealNtfsHardLinkTransactionLeavesNoRollbackResidue()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "transaction-survivor.bin");
            string duplicate = Path.Combine(root, "transaction-duplicate.bin");
            File.WriteAllBytes(survivor, [1, 3, 3, 7]);
            File.WriteAllBytes(duplicate, [1, 3, 3, 7]);
            var platform = new FileLinkNative();
            using (IFileLinkHandle survivorProbe = platform.OpenNoFollow(survivor, false))
            using (IFileLinkHandle duplicateProbe = platform.OpenNoFollow(duplicate, false))
            {
                Assert.True(platform.ContentEquals(survivorProbe, duplicateProbe, CancellationToken.None));
                Assert.True(platform.StreamsEqual(
                    survivor,
                    survivorProbe,
                    duplicate,
                    duplicateProbe,
                    CancellationToken.None));
            }

            string renameProbePath = Path.Combine(root, "rename-probe.bin");
            using (IFileLinkHandle renameProbe = platform.OpenNoFollow(duplicate, requestDelete: true))
            {
                platform.Rename(renameProbe, renameProbePath);
                Assert.False(File.Exists(duplicate));
                Assert.True(
                    File.Exists(renameProbePath),
                    string.Join(" | ", Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)));
                platform.Rename(renameProbe, duplicate);
            }

            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));

            FileOperationSummary summary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);

            FileOperationResult result = Assert.Single(summary.Results);
            Assert.True(result.Succeeded, result.Failure?.Reason);
            using IFileLinkHandle survivorHandle = platform.OpenNoFollow(survivor, false);
            using IFileLinkHandle duplicateHandle = platform.OpenNoFollow(duplicate, false);
            Assert.Equal(platform.GetInfo(survivorHandle).Identity, platform.GetInfo(duplicateHandle).Identity);
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(root),
                path => Path.GetFileName(path).StartsWith(".duplicates-rollback-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealNtfsSymbolicTransactionUsesAbsoluteSurvivor()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "transaction-symbolic-survivor.bin");
            string duplicate = Path.Combine(root, "transaction-symbolic-duplicate.bin");
            File.WriteAllBytes(survivor, [2, 4, 6, 8]);
            File.WriteAllBytes(duplicate, [2, 4, 6, 8]);
            var platform = new FileLinkNative();
            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));

            FileOperationSummary summary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.SymbolicLink,
                null,
                CancellationToken.None);

            FileOperationResult result = Assert.Single(summary.Results);
            if (!result.Succeeded && result.Failure?.Reason.Contains("Developer Mode", StringComparison.Ordinal) == true)
            {
                throw SkipException.ForSkip(result.Failure.Reason);
            }

            Assert.True(result.Succeeded, result.Failure?.Reason);
            using IFileLinkHandle linkHandle = platform.OpenNoFollow(duplicate, false);
            Assert.Equal(Path.GetFullPath(survivor), platform.GetSymbolicLinkTarget(duplicate, linkHandle));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(root),
                path => Path.GetFileName(path).StartsWith(".duplicates-rollback-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealNtfsAlternateDataStreamsMustMatchExactly()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "ads-survivor.bin");
            string duplicate = Path.Combine(root, "ads-duplicate.bin");
            File.WriteAllBytes(survivor, [5, 5, 5]);
            File.WriteAllBytes(duplicate, [5, 5, 5]);
            try
            {
                File.WriteAllText(survivor + ":task16", "survivor-stream");
                File.WriteAllText(duplicate + ":task16", "different-stream");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw SkipException.ForSkip($"NTFS ADS fixture unavailable: {ex.Message}");
            }

            var platform = new FileLinkNative();
            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));
            FileOperationSummary mismatch = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);
            Assert.False(Assert.Single(mismatch.Results).Succeeded);
            Assert.True(File.Exists(duplicate));

            File.WriteAllText(duplicate + ":task16", "survivor-stream");
            FileOperationSummary match = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);
            FileOperationResult matchedResult = Assert.Single(match.Results);
            Assert.True(matchedResult.Succeeded, matchedResult.Failure?.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealLockedAndAlreadyHardLinkedDuplicatesFailBeforeRename()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "guard-survivor.bin");
            string locked = Path.Combine(root, "locked-duplicate.bin");
            string linked = Path.Combine(root, "linked-duplicate.bin");
            string alias = Path.Combine(root, "linked-alias.bin");
            File.WriteAllBytes(survivor, [7, 7, 7]);
            File.WriteAllBytes(locked, [7, 7, 7]);
            File.WriteAllBytes(linked, [7, 7, 7]);
            var platform = new FileLinkNative();
            using (IFileLinkHandle aliasHandle = platform.CreateHardLinkAndOpen(alias, linked))
            {
            }

            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));
            using (FileStream lockHandle = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                FileOperationSummary lockedSummary = await service.ReplaceWithLinksAsync(
                    [new(Snapshot(survivor), [Snapshot(locked)])],
                    LinkReplacementMode.HardLink,
                    null,
                    CancellationToken.None);
                Assert.False(Assert.Single(lockedSummary.Results).Succeeded);
            }

            FileOperationSummary linkedSummary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(linked)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);
            Assert.False(Assert.Single(linkedSummary.Results).Succeeded);
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(root),
                path => Path.GetFileName(path).StartsWith(".duplicates-rollback-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public void OpenNoFollow_RejectsAWriterEvenWhenTheWriterAllowsDeleteSharing()
    {
        string root = CreateTempRoot();
        try
        {
            string path = Path.Combine(root, "cooperative-writer.bin");
            File.WriteAllBytes(path, [3, 1, 4]);
            using var writer = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            var platform = new FileLinkNative();

            Exception? failure = Record.Exception(() =>
            {
                using IFileLinkHandle handle = platform.OpenNoFollow(path, requestDelete: false);
            });

            Assert.IsType<Win32Exception>(failure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealMaterialAttributeMismatchFailsBeforeRename()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "attribute-survivor.bin");
            string duplicate = Path.Combine(root, "attribute-duplicate.bin");
            File.WriteAllBytes(survivor, [9, 9]);
            File.WriteAllBytes(duplicate, [9, 9]);
            File.SetAttributes(duplicate, File.GetAttributes(duplicate) | FileAttributes.Hidden);
            var platform = new FileLinkNative();
            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));

            FileOperationSummary summary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);

            Assert.False(Assert.Single(summary.Results).Succeeded);
            Assert.True(File.Exists(duplicate));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(root),
                path => Path.GetFileName(path).StartsWith(".duplicates-rollback-", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(Path.Combine(root, "attribute-duplicate.bin")))
            {
                File.SetAttributes(Path.Combine(root, "attribute-duplicate.bin"), FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(NativeAttributeFixture.Sparse, FileAttributes.SparseFile)]
    [InlineData(NativeAttributeFixture.Compressed, FileAttributes.Compressed)]
    [InlineData(NativeAttributeFixture.Offline, FileAttributes.Offline)]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealUnsafeOrDistinctNativeAttributeFailsBeforeRename(
        NativeAttributeFixture fixture,
        FileAttributes expectedAttribute)
    {
        string root = CreateTempRoot();
        string duplicate = Path.Combine(root, $"{fixture}-duplicate.bin");
        try
        {
            string survivor = Path.Combine(root, $"{fixture}-survivor.bin");
            File.WriteAllBytes(survivor, [6, 2, 6]);
            File.WriteAllBytes(duplicate, [6, 2, 6]);
            ApplyNativeAttribute(duplicate, fixture);
            Assert.True((File.GetAttributes(duplicate) & expectedAttribute) != 0);
            var platform = new FileLinkNative();
            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));

            FileOperationSummary summary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);

            Assert.False(Assert.Single(summary.Results).Succeeded);
            Assert.True(File.Exists(duplicate));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(root),
                path => Path.GetFileName(path).StartsWith(".duplicates-rollback-", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(duplicate))
            {
                File.SetAttributes(duplicate, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealDaclControlMismatchFailsBeforeRename()
    {
        string root = CreateTempRoot();
        try
        {
            string survivor = Path.Combine(root, "acl-survivor.bin");
            string duplicate = Path.Combine(root, "acl-duplicate.bin");
            File.WriteAllBytes(survivor, [2, 7, 1, 8]);
            File.WriteAllBytes(duplicate, [2, 7, 1, 8]);
            var duplicateInfo = new FileInfo(duplicate);
            FileSecurity security = duplicateInfo.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(!security.AreAccessRulesProtected, preserveInheritance: true);
            duplicateInfo.SetAccessControl(security);
            var platform = new FileLinkNative();
            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));

            FileOperationSummary summary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);

            Assert.False(Assert.Single(summary.Results).Succeeded);
            Assert.True(File.Exists(duplicate));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(root),
                path => Path.GetFileName(path).StartsWith(".duplicates-rollback-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [CrossVolumeFact]
    [Trait("Category", "Task16RealUat")]
    public async Task FileLinkService_RealCrossVolumeFixtureOrExplicitCapabilitySkip()
    {
        DriveInfo[] writableCandidates = DriveInfo.GetDrives()
            .Where(static drive => drive.IsReady)
            .ToArray();

        string leftRoot = Path.Combine(writableCandidates[0].RootDirectory.FullName, $"Duplicates-Task16-{Guid.NewGuid():N}");
        string rightRoot = Path.Combine(writableCandidates[1].RootDirectory.FullName, $"Duplicates-Task16-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(leftRoot);
            Directory.CreateDirectory(rightRoot);
            string survivor = Path.Combine(leftRoot, "cross-volume-survivor.bin");
            string duplicate = Path.Combine(rightRoot, "cross-volume-duplicate.bin");
            File.WriteAllBytes(survivor, [4, 2]);
            File.WriteAllBytes(duplicate, [4, 2]);
            var platform = new FileLinkNative();
            var service = new FileLinkService(platform, new DispositionRecycleBinService(platform));

            FileOperationSummary summary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor), [Snapshot(duplicate)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);

            Assert.False(Assert.Single(summary.Results).Succeeded);
            Assert.True(File.Exists(duplicate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SkipException.ForSkip($"Cross-volume fixture unavailable: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(leftRoot))
            {
                Directory.Delete(leftRoot, recursive: true);
            }

            if (Directory.Exists(rightRoot))
            {
                Directory.Delete(rightRoot, recursive: true);
            }
        }
    }

    private static LinkReplacementFile Snapshot(string path) => new(
        Path.GetFullPath(path),
        new FileInfo(path).Length,
        File.GetLastWriteTimeUtc(path));

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-Task16-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void ApplyNativeAttribute(string path, NativeAttributeFixture fixture)
    {
        if (fixture == NativeAttributeFixture.Offline)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
            return;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete);
        nint input = 0;
        uint inputSize = 0;
        uint controlCode = 0x000900C4; // FSCTL_SET_SPARSE
        try
        {
            if (fixture == NativeAttributeFixture.Compressed)
            {
                controlCode = 0x0009C040; // FSCTL_SET_COMPRESSION
                input = Marshal.AllocHGlobal(sizeof(short));
                Marshal.WriteInt16(input, 1); // COMPRESSION_FORMAT_DEFAULT
                inputSize = sizeof(short);
            }

            if (!DeviceIoControl(
                    stream.SafeFileHandle,
                    controlCode,
                    input,
                    inputSize,
                    0,
                    0,
                    out _,
                    0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (input != 0)
            {
                Marshal.FreeHGlobal(input);
            }
        }
    }

    public enum NativeAttributeFixture
    {
        Sparse,
        Compressed,
        Offline,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);

    private sealed class DispositionRecycleBinService(IFileLinkPlatform platform) : IRecycleBinService
    {
        public Task RecycleFileAsync(
            string path,
            FileSystemIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IFileLinkHandle handle = platform.OpenNoFollow(path, requestDelete: true);
            if (platform.GetInfo(handle).Identity != expectedIdentity)
            {
                throw new InvalidOperationException("The real UAT rollback identity changed.");
            }

            platform.DeleteByHandle(handle);
            return Task.CompletedTask;
        }
    }
}
