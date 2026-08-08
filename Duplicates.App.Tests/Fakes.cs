using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task LoadAsync()
    {
        SettingsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task SaveAsync(AppSettings settings)
    {
        SetCurrent(settings);
        return Task.CompletedTask;
    }

    public void SetCurrent(AppSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(this, Current);
    }
}

internal sealed class FakeFileActionService : IFileActionService
{
    public DeleteSummary NextSummary { get; set; } = new(0, 0, []);

    public DeleteOperationCanceledException? NextDeleteCancellation { get; set; }

    public FileOperationSummary NextMoveSummary { get; set; } = new([], 0);

    public FileOperationCanceledException? NextMoveCancellation { get; set; }

    public FileOperationResult? NextRenameResult { get; set; }

    public int DeleteCallCount { get; private set; }

    public int MoveCallCount { get; private set; }

    public int RenameCallCount { get; private set; }

    public FileActionTarget? LastRenameTarget { get; private set; }

    public string? LastRenameName { get; private set; }

    public Action<IReadOnlyList<FileActionTarget>, IProgress<DeleteProgress>?>? OnDelete { get; set; }

    public Action<IReadOnlyList<FileActionTarget>, string, MoveCollisionBehavior, IProgress<FileOperationProgress>?>? OnMove { get; set; }

    public Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<FileActionTarget> targets,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        DeleteCallCount++;
        OnDelete?.Invoke(targets, progress);
        if (NextDeleteCancellation is not null)
        {
            throw NextDeleteCancellation;
        }

        return Task.FromResult(NextSummary);
    }

    public Task<FileOperationSummary> MoveAsync(
        IReadOnlyList<FileActionTarget> targets,
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        MoveCallCount++;
        OnMove?.Invoke(targets, destinationFolder, collisionBehavior, progress);
        if (NextMoveCancellation is not null)
        {
            throw NextMoveCancellation;
        }

        return Task.FromResult(NextMoveSummary);
    }

    public Task<FileOperationResult> RenameAsync(
        FileActionTarget target,
        string newName,
        CancellationToken cancellationToken)
    {
        RenameCallCount++;
        LastRenameTarget = target;
        LastRenameName = newName;
        return Task.FromResult(NextRenameResult ?? new FileOperationResult(
            target.FullPath,
            Path.Combine(Path.GetDirectoryName(target.FullPath)!, newName),
            null));
    }

    public void OpenFile(string path)
    {
    }

    public void RevealInExplorer(string path)
    {
    }
}

internal sealed class FakeResultExportService : IResultExportService
{
    public ResultExportSnapshot? Snapshot { get; private set; }

    public ResultExportFormat? Format { get; private set; }

    public string? DestinationPath { get; private set; }

    public Task ExportAsync(
        ResultExportSnapshot snapshot,
        ResultExportFormat format,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Snapshot = snapshot;
        Format = format;
        DestinationPath = destinationPath;
        return Task.CompletedTask;
    }
}
