using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private bool _isLoading;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings(settingsService.Current);
        AboutText = BuildAboutText();
        _settingsService.SettingsChanged += (_, settings) => LoadFromSettings(settings);
    }

    public IReadOnlyList<AppThemeMode> ThemeModes { get; } = Enum.GetValues<AppThemeMode>();

    public IReadOnlyList<BackdropMode> BackdropModes { get; } = Enum.GetValues<BackdropMode>();

    public IReadOnlyList<DeletionMode> DeletionModes { get; } = Enum.GetValues<DeletionMode>();

    public IReadOnlyList<string> ConcurrencyOptions { get; } = ["Auto", "1", "2", "4", "8"];

    [ObservableProperty]
    public partial AppThemeMode SelectedThemeMode { get; set; }

    [ObservableProperty]
    public partial BackdropMode SelectedBackdropMode { get; set; }

    [ObservableProperty]
    public partial double DefaultMinSizeValue { get; set; } = 1d;

    [ObservableProperty]
    public partial bool VerifyByteByByte { get; set; }

    [ObservableProperty]
    public partial bool IgnoreHiddenFiles { get; set; }

    [ObservableProperty]
    public partial bool IgnoreSystemFiles { get; set; }

    [ObservableProperty]
    public partial int SelectedConcurrencyIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermanentDeleteWarningVisibility))]
    public partial DeletionMode SelectedDeletionMode { get; set; }

    [ObservableProperty]
    public partial bool ConfirmBeforeDelete { get; set; }

    [ObservableProperty]
    public partial string AboutText { get; set; } = string.Empty;

    public Visibility PermanentDeleteWarningVisibility => SelectedDeletionMode == DeletionMode.Permanent ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSelectedThemeModeChanged(AppThemeMode value) => QueueSave();

    partial void OnSelectedBackdropModeChanged(BackdropMode value) => QueueSave();

    partial void OnDefaultMinSizeValueChanged(double value) => QueueSave();

    partial void OnVerifyByteByByteChanged(bool value) => QueueSave();

    partial void OnIgnoreHiddenFilesChanged(bool value) => QueueSave();

    partial void OnIgnoreSystemFilesChanged(bool value) => QueueSave();

    partial void OnSelectedConcurrencyIndexChanged(int value) => QueueSave();

    partial void OnSelectedDeletionModeChanged(DeletionMode value) => QueueSave();

    partial void OnConfirmBeforeDeleteChanged(bool value) => QueueSave();

    [RelayCommand]
    private void UseAnySizeDefault()
    {
        DefaultMinSizeValue = 0d;
    }

    [RelayCommand]
    private void UseOneKilobyteDefault()
    {
        DefaultMinSizeValue = 1024d;
    }

    [RelayCommand]
    private void UseOneMegabyteDefault()
    {
        DefaultMinSizeValue = 1_048_576d;
    }

    private void LoadFromSettings(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            SelectedThemeMode = settings.ThemeMode;
            SelectedBackdropMode = settings.BackdropMode;
            DefaultMinSizeValue = ByteSizeInput.FromBytes(settings.DefaultMinSizeBytes);
            VerifyByteByByte = settings.VerifyByteByByte;
            IgnoreHiddenFiles = settings.IgnoreHiddenFiles;
            IgnoreSystemFiles = settings.IgnoreSystemFiles;
            SelectedConcurrencyIndex = settings.MaxHashingConcurrency switch
            {
                1 => 1,
                2 => 2,
                4 => 3,
                8 => 4,
                _ => 0,
            };
            SelectedDeletionMode = settings.DeletionMode;
            ConfirmBeforeDelete = settings.ConfirmBeforeDelete;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void QueueSave()
    {
        if (_isLoading)
        {
            return;
        }

        _ = SaveAsync();
    }

    private Task SaveAsync()
    {
        long minSize = ByteSizeInput.ToBytes(
            DefaultMinSizeValue,
            _settingsService.Current.DefaultMinSizeBytes);

        var settings = new AppSettings
        {
            ThemeMode = SelectedThemeMode,
            BackdropMode = SelectedBackdropMode,
            DefaultMinSizeBytes = minSize,
            VerifyByteByByte = VerifyByteByByte,
            IgnoreHiddenFiles = IgnoreHiddenFiles,
            IgnoreSystemFiles = IgnoreSystemFiles,
            MaxHashingConcurrency = SelectedConcurrencyIndex switch
            {
                1 => 1,
                2 => 2,
                3 => 4,
                4 => 8,
                _ => null,
            },
            DeletionMode = SelectedDeletionMode,
            ConfirmBeforeDelete = ConfirmBeforeDelete,
        };

        return _settingsService.SaveAsync(settings);
    }

    private static string BuildAboutText()
    {
        string version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
        return $"Duplicates {version} by Finnvek - .NET 10 - Windows App SDK 1.8";
    }
}
