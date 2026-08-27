using Duplicates.Models;

namespace Duplicates.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task LoadAsync();

    Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default);
}
