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

    public Action<IReadOnlyList<DuplicateFileViewModel>, IProgress<DeleteProgress>?>? OnDelete { get; set; }

    public Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<DuplicateFileViewModel> files,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        OnDelete?.Invoke(files, progress);
        return Task.FromResult(NextSummary);
    }

    public void OpenFile(string path)
    {
    }

    public void RevealInExplorer(string path)
    {
    }
}
