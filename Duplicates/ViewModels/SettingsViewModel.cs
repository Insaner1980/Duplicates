using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IMediaFingerprintCacheControl? _cacheControl;
    private readonly IAppOperationCoordinator? _operationCoordinator;
    private bool _isLoading;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings(settingsService.Current);
        AboutText = BuildAboutText();
        _settingsService.SettingsChanged += (_, settings) => LoadFromSettings(settings);
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        IMediaFingerprintCacheControl cacheControl,
        IAppOperationCoordinator operationCoordinator)
        : this(settingsService)
    {
        _cacheControl = cacheControl ?? throw new ArgumentNullException(nameof(cacheControl));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
        _operationCoordinator.ActiveOperationChanged += (_, _) => ClearCacheCommand.NotifyCanExecuteChanged();
    }

    public IReadOnlyList<AppThemeMode> ThemeModes { get; } = Enum.GetValues<AppThemeMode>();

    public IReadOnlyList<BackdropMode> BackdropModes { get; } = Enum.GetValues<BackdropMode>();

    public IReadOnlyList<DeletionMode> DeletionModes { get; } = Enum.GetValues<DeletionMode>();

    public IReadOnlyList<string> ConcurrencyOptions { get; } = ["Auto", "1", "2", "4", "8"];

    public IReadOnlyList<SimilarityPreset> SimilarityPresets { get; } = Enum.GetValues<SimilarityPreset>();

    public IReadOnlyList<string> MediaConcurrencyOptions { get; } = ["Auto", "1", "2", "4"];

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
    public partial bool DefaultIncludeSubfolders { get; set; } = true;

    [ObservableProperty]
    public partial double DefaultLargeFileMinimumValue { get; set; } = 1_073_741_824d;

    [ObservableProperty]
    public partial double DefaultTemporaryFileMinimumAgeDays { get; set; } = 7d;

    [ObservableProperty]
    public partial SimilarityPreset DefaultImageSimilarity { get; set; } = SimilarityPreset.Balanced;

    [ObservableProperty]
    public partial SimilarityPreset DefaultVideoSimilarity { get; set; } = SimilarityPreset.Balanced;

    [ObservableProperty]
    public partial int SelectedMediaConcurrencyIndex { get; set; }

    [ObservableProperty]
    public partial bool UseMediaFingerprintCache { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedConcurrencyIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermanentDeleteWarningVisibility))]
    public partial DeletionMode SelectedDeletionMode { get; set; }

    [ObservableProperty]
    public partial bool ConfirmBeforeDelete { get; set; }

    [ObservableProperty]
    public partial string AboutText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsInfoOpen))]
    public partial string SettingsInfoMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Controls.InfoBarSeverity SettingsInfoSeverity { get; set; } =
        Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

    public bool IsSettingsInfoOpen => !string.IsNullOrWhiteSpace(SettingsInfoMessage);

    [ObservableProperty]
    public partial string CacheStatusText { get; set; } = "Cache status not loaded.";

    [ObservableProperty]
    public partial string CachePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsClearingCache { get; set; }

    public Visibility PermanentDeleteWarningVisibility => SelectedDeletionMode == DeletionMode.Permanent ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSelectedThemeModeChanged(AppThemeMode value) => QueueSave();

    partial void OnSelectedBackdropModeChanged(BackdropMode value) => QueueSave();

    partial void OnDefaultMinSizeValueChanged(double value) => QueueSave();

    partial void OnVerifyByteByByteChanged(bool value) => QueueSave();

    partial void OnIgnoreHiddenFilesChanged(bool value) => QueueSave();

    partial void OnIgnoreSystemFilesChanged(bool value) => QueueSave();

    partial void OnDefaultIncludeSubfoldersChanged(bool value) => QueueSave();

    partial void OnDefaultLargeFileMinimumValueChanged(double value) => QueueSave();

    partial void OnDefaultTemporaryFileMinimumAgeDaysChanged(double value) => QueueSave();

    partial void OnDefaultImageSimilarityChanged(SimilarityPreset value) => QueueSave();

    partial void OnDefaultVideoSimilarityChanged(SimilarityPreset value) => QueueSave();

    partial void OnSelectedMediaConcurrencyIndexChanged(int value) => QueueSave();

    partial void OnUseMediaFingerprintCacheChanged(bool value) => QueueSave();

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

    public async Task RefreshCacheStatusAsync()
    {
        if (_cacheControl is null)
        {
            return;
        }

        try
        {
            MediaFingerprintCacheStatus status = await _cacheControl.GetStatusAsync(CancellationToken.None);
            CacheStatusText = $"{status.EntryCount:N0} cached items, {ByteFormatter.Format(status.SizeBytes)}";
            CachePath = status.Path;
        }
        catch (Exception)
        {
            SettingsInfoSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
            SettingsInfoMessage = "Could not read media cache status.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearCache))]
    private async Task ClearCacheAsync()
    {
        if (!CanClearCache() || _cacheControl is null)
        {
            return;
        }

        IsClearingCache = true;
        ClearCacheCommand.NotifyCanExecuteChanged();
        try
        {
            await _cacheControl.ClearAsync(CancellationToken.None);
            await RefreshCacheStatusAsync();
        }
        catch (Exception)
        {
            SettingsInfoSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
            SettingsInfoMessage = "Could not clear media cache.";
        }
        finally
        {
            IsClearingCache = false;
            ClearCacheCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanClearCache() =>
        !IsClearingCache &&
        _operationCoordinator?.ActiveOperation is not { UsesMediaFingerprintCache: true };

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
            DefaultIncludeSubfolders = settings.DefaultIncludeSubfolders;
            DefaultLargeFileMinimumValue = settings.DefaultLargeFileMinimumBytes;
            DefaultTemporaryFileMinimumAgeDays = settings.DefaultTemporaryFileMinimumAgeDays;
            DefaultImageSimilarity = settings.DefaultImageSimilarity;
            DefaultVideoSimilarity = settings.DefaultVideoSimilarity;
            SelectedMediaConcurrencyIndex = settings.MaxMediaConcurrency switch
            {
                1 => 1,
                2 => 2,
                4 => 3,
                _ => 0,
            };
            UseMediaFingerprintCache = settings.UseMediaFingerprintCache;
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

        AppSettings snapshot = CaptureSettingsSnapshot();
        _ = SaveAndObserveAsync(snapshot);
    }

    private AppSettings CaptureSettingsSnapshot()
    {
        long minSize = ByteSizeInput.ToBytes(
            DefaultMinSizeValue,
            _settingsService.Current.DefaultMinSizeBytes);

        return new AppSettings
        {
            ThemeMode = SelectedThemeMode,
            BackdropMode = SelectedBackdropMode,
            DefaultMinSizeBytes = minSize,
            VerifyByteByByte = VerifyByteByByte,
            IgnoreHiddenFiles = IgnoreHiddenFiles,
            IgnoreSystemFiles = IgnoreSystemFiles,
            DefaultIncludeSubfolders = DefaultIncludeSubfolders,
            DefaultLargeFileMinimumBytes = ByteSizeInput.ToBytes(
                DefaultLargeFileMinimumValue,
                _settingsService.Current.DefaultLargeFileMinimumBytes),
            DefaultTemporaryFileMinimumAgeDays = double.IsFinite(DefaultTemporaryFileMinimumAgeDays)
                ? (int)Math.Clamp(Math.Round(DefaultTemporaryFileMinimumAgeDays), 1, 365)
                : _settingsService.Current.DefaultTemporaryFileMinimumAgeDays,
            DefaultImageSimilarity = DefaultImageSimilarity,
            DefaultVideoSimilarity = DefaultVideoSimilarity,
            MaxMediaConcurrency = SelectedMediaConcurrencyIndex switch
            {
                1 => 1,
                2 => 2,
                3 => 4,
                _ => null,
            },
            UseMediaFingerprintCache = UseMediaFingerprintCache,
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
    }

    private async Task SaveAndObserveAsync(AppSettings snapshot)
    {
        try
        {
            await _settingsService.SaveAsync(snapshot);
        }
        catch (Exception)
        {
            SettingsInfoSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
            SettingsInfoMessage = "Could not save settings.";
        }
    }

    private static string BuildAboutText()
    {
        string version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
        return $"Duplicates {version} by Finnvek - .NET 10 - Windows App SDK 1.8";
    }
}
