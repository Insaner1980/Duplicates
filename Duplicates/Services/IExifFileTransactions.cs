using Duplicates.Models;

namespace Duplicates.Services;

internal sealed record ExifTrackedFile(
    string Path,
    FileSystemIdentity Identity,
    long Length,
    DateTime ModifiedUtc,
    FileAttributes Attributes);

internal enum ExifPathState
{
    Missing,
    Present,
    Indeterminate,
}

internal readonly record struct ExifPathProbe(
    ExifPathState State,
    FileSystemIdentity? Identity);

internal enum ExifMoveCommitState
{
    NotCommitted,
    Committed,
    Indeterminate,
}

internal sealed record ExifMoveResult(
    ExifMoveCommitState CommitState,
    ExifTrackedFile File,
    bool DestinationOccupied = false);

internal sealed class ExifMoveException : IOException
{
    public ExifMoveException(
        string message,
        ExifMoveCommitState commitState,
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

    public ExifMoveCommitState CommitState { get; }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public FileSystemIdentity ExpectedIdentity { get; }
}

internal class ExifTransactionException : IOException
{
    public ExifTransactionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class ExifOwnedCreationRecoveryException : ExifTransactionException
{
    public ExifOwnedCreationRecoveryException(
        string recoveryPath,
        Exception creationFailure,
        Exception cleanupFailure)
        : base(
            "A newly created EXIF artifact could not be validated or removed safely.",
            new AggregateException(creationFailure, cleanupFailure))
    {
        RecoveryPath = recoveryPath;
    }

    public string RecoveryPath { get; }
}

internal sealed class ExifSourceChangedException : ExifTransactionException
{
    public ExifSourceChangedException()
        : base("The EXIF source snapshot changed before publication.")
    {
    }
}

internal interface IExifFileTransactions
{
    ExifTrackedFile Capture(string path);

    ExifTrackedFile CreateOwnedNew(string destinationPath);

    Task<ExifTrackedFile> CopyAndFlushAsync(
        ExifTrackedFile source,
        ExifTrackedFile destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    IDisposable GuardOwnedPath(ExifTrackedFile file);

    IDisposable GuardSourceSnapshot(ExifTrackedFile source);

    bool EntryExistsCaseInsensitive(string path);

    ExifMoveResult MoveNoOverwrite(ExifTrackedFile source, string destinationPath);

    ExifMoveResult MoveSourceNoOverwrite(ExifTrackedFile source, string destinationPath);

    void DeleteOwned(ExifTrackedFile file);

    ExifPathProbe Probe(string path);
}
