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

    public FileOperationSummary NextMoveSummary { get; set; } = new([], 0);

    public FileOperationResult? NextRenameResult { get; set; }

    public Action<IReadOnlyList<FileActionTarget>, IProgress<DeleteProgress>?>? OnDelete { get; set; }

    public Action<IReadOnlyList<FileActionTarget>, string, MoveCollisionBehavior, IProgress<FileOperationProgress>?>? OnMove { get; set; }

    public Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<FileActionTarget> targets,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        OnDelete?.Invoke(targets, progress);
        return Task.FromResult(NextSummary);
    }

    public Task<FileOperationSummary> MoveAsync(
        IReadOnlyList<FileActionTarget> targets,
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        OnMove?.Invoke(targets, destinationFolder, collisionBehavior, progress);
        return Task.FromResult(NextMoveSummary);
    }

    public Task<FileOperationResult> RenameAsync(
        FileActionTarget target,
        string newName,
        CancellationToken cancellationToken)
    {
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
