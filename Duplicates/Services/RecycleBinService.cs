using Duplicates.Models;

namespace Duplicates.Services;

public sealed class RecycleBinService : IRecycleBinService
{
    private readonly IFileLinkPlatform _platform;

    public RecycleBinService()
        : this(new FileLinkNative())
    {
    }

    internal RecycleBinService(IFileLinkPlatform platform)
    {
        _platform = platform;
    }

    public Task RecycleFileAsync(
        string path,
        FileSystemIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        string canonicalPath = ValidatePath(path);
        return Task.Run(
            () => RecycleFile(canonicalPath, expectedIdentity, cancellationToken),
            cancellationToken);
    }

    private void RecycleFile(
        string path,
        FileSystemIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IFileLinkHandle anchor = _platform.OpenNoFollow(path, requestDelete: false);
        EnsureAnchorAtPath(path, anchor, expectedIdentity);

        cancellationToken.ThrowIfCancellationRequested();
        Exception? shellFailure = null;
        try
        {
            _platform.SendToRecycleBin(
                path,
                () => EnsureAnchorAtPath(path, anchor, expectedIdentity));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            shellFailure = ex;
        }

        Postcondition postcondition = InspectPostcondition(path, expectedIdentity);
        if (postcondition == Postcondition.Committed)
        {
            return;
        }

        if (postcondition == Postcondition.OwnedRollback)
        {
            if (shellFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(shellFailure).Throw();
            }

            throw new IOException("The Recycle Bin operation did not remove the expected rollback file.");
        }

        throw new InvalidOperationException(
            "The rollback path identity is foreign or indeterminate after the Recycle Bin handoff.",
            shellFailure);
    }

    private Postcondition InspectPostcondition(string path, FileSystemIdentity expectedIdentity)
    {
        FileLinkPathProbe probe = _platform.ProbeNoFollow(path);
        if (probe.State == FileLinkPathState.Missing)
        {
            return Postcondition.Committed;
        }

        if (probe.State != FileLinkPathState.Present || probe.Identity != expectedIdentity)
        {
            return Postcondition.Fatal;
        }

        try
        {
            using IFileLinkHandle current = _platform.OpenNoFollow(path, requestDelete: false);
            FileLinkFileInfo info = _platform.GetInfo(current);
            EnsureOrdinaryFile(info);
            return info.Identity == expectedIdentity
                ? Postcondition.OwnedRollback
                : Postcondition.Fatal;
        }
        catch
        {
            return Postcondition.Fatal;
        }
    }

    private static void EnsureOrdinaryFile(FileLinkFileInfo info)
    {
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidOperationException("Only an ordinary non-link file can be sent to the Recycle Bin.");
        }
    }

    private void EnsureAnchorAtPath(
        string path,
        IFileLinkHandle anchor,
        FileSystemIdentity expectedIdentity)
    {
        FileLinkFileInfo info = _platform.GetInfo(anchor);
        EnsureOrdinaryFile(info);
        if (info.Identity != expectedIdentity ||
            !string.Equals(_platform.GetFinalPath(anchor), path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The rollback path identity changed before the Recycle Bin handoff.");
        }
    }

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The Recycle Bin path must be fully qualified.", nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private enum Postcondition
    {
        Committed,
        OwnedRollback,
        Fatal,
    }
}
