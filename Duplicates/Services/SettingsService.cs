using System.Text.Json;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public SettingsService()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Duplicates",
            "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        await using FileStream stream = File.OpenRead(_settingsPath);
        AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions);
        if (settings is not null)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, Current);
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await using FileStream stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions);
        Current = settings;
        SettingsChanged?.Invoke(this, Current);
    }
}
