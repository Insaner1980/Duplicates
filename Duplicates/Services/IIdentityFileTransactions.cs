using Duplicates.Models;

namespace Duplicates.Services;

internal sealed record IdentityTrackedFile(
    string Path,
    FileSystemIdentity Identity,
    long Length,
    DateTime ModifiedUtc,
    FileAttributes Attributes);

internal enum IdentityPathState
{
    Missing,
    Present,
    Indeterminate,
}

internal readonly record struct IdentityPathProbe(
    IdentityPathState State,
    FileSystemIdentity? Identity);

internal enum IdentityMoveCommitState
{
    NotCommitted,
    Committed,
    Indeterminate,
}

internal sealed record IdentityMoveResult(
    IdentityMoveCommitState CommitState,
    IdentityTrackedFile File,
    bool DestinationOccupied = false);

internal sealed class IdentityMoveException : IOException
{
    public IdentityMoveException(
        string message,
        IdentityMoveCommitState commitState,
        string sourcePath,
        string destinationPath,
        FileSystemIdentity expectedIdentity,
        Exception? innerException = null)
        : base(message, innerException)
    {
        CommitState = commitState;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        ExpectedIdentity = expectedIdentity;
    }

    public IdentityMoveCommitState CommitState { get; }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public FileSystemIdentity ExpectedIdentity { get; }
}

internal class IdentityTransactionException : IOException
{
    public IdentityTransactionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class IdentityOwnedCreationRecoveryException : IdentityTransactionException
{
    public IdentityOwnedCreationRecoveryException(
        string recoveryPath,
        Exception creationFailure,
        Exception cleanupFailure)
        : base(
            "A newly created identity-owned artifact could not be validated or removed safely.",
            new AggregateException(creationFailure, cleanupFailure))
    {
        RecoveryPath = recoveryPath;
    }

    public string RecoveryPath { get; }
}

internal sealed class IdentitySourceChangedException : IdentityTransactionException
{
    public IdentitySourceChangedException()
        : base("The source identity snapshot changed before publication.")
    {
    }
}

internal interface IIdentityFileTransactions
{
    IdentityTrackedFile Capture(string path);

    IdentityTrackedFile CreateOwnedNew(string destinationPath);

    Task<IdentityTrackedFile> CopyAndFlushAsync(
        IdentityTrackedFile source,
        IdentityTrackedFile destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    IDisposable GuardOwnedPath(IdentityTrackedFile file);

    IDisposable GuardSourceSnapshot(IdentityTrackedFile source);

    bool EntryExistsCaseInsensitive(string path);

    IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath);

    IdentityMoveResult MoveSourceNoOverwrite(IdentityTrackedFile source, string destinationPath);

    void DeleteOwned(IdentityTrackedFile file);

    IdentityPathProbe Probe(string path);
}
