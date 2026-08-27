using Duplicates.Models;

namespace Duplicates.Services;

internal interface IFileLinkHandle : IDisposable
{
    string Path { get; }
}

internal sealed record FileLinkFileInfo(
    FileSystemIdentity Identity,
    long Length,
    DateTime ModifiedUtc,
    FileAttributes Attributes,
    uint LinkCount,
    string FileSystemName,
    bool IsLocal);

internal sealed record FileLinkSecurityInfo(
    byte[] OwnerSid,
    byte[] GroupSid,
    bool DaclPresent,
    byte[] Dacl,
    ushort DaclControl);

internal enum FileLinkPathState
{
    Missing,
    Present,
    Indeterminate,
}

internal readonly record struct FileLinkPathProbe(
    FileLinkPathState State,
    FileSystemIdentity? Identity);

internal interface IFileLinkPlatform
{
    IFileLinkHandle OpenNoFollow(string path, bool requestDelete);

    IFileLinkHandle OpenFollow(string path);

    FileLinkFileInfo GetInfo(IFileLinkHandle handle);

    FileLinkSecurityInfo GetSecurityInfo(IFileLinkHandle handle);

    bool StreamsEqual(
        string leftPath,
        IFileLinkHandle leftHandle,
        string rightPath,
        IFileLinkHandle rightHandle,
        CancellationToken cancellationToken);

    bool ContentEquals(
        IFileLinkHandle leftHandle,
        IFileLinkHandle rightHandle,
        CancellationToken cancellationToken);

    void Rename(IFileLinkHandle handle, string destinationPath);

    IFileLinkHandle CreateHardLinkAndOpen(string linkPath, string existingPath);

    IFileLinkHandle CreateSymbolicLinkAndOpen(string linkPath, string targetPath);

    string? GetSymbolicLinkTarget(string linkPath, IFileLinkHandle linkHandle);

    string GetFinalPath(IFileLinkHandle handle);

    FileLinkPathProbe ProbeNoFollow(string path);

    void DeleteByHandle(IFileLinkHandle handle);

    void SendToRecycleBin(string path, Action verifyOwnershipAfterShellItemCreation);
}
