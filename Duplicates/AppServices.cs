using Duplicates.Engine;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates;

public sealed class AppServices
{
    public AppServices()
    {
        SettingsService = new SettingsService();
        ThemeService = new ThemeService();
        ResultsStore = new ResultsStore();
        FileActionService = new FileActionService(SettingsService);

        ScanViewModel = new ScanViewModel(new DuplicateScanner(), SettingsService, ResultsStore);
        ResultsViewModel = new ResultsViewModel(ResultsStore, FileActionService, SettingsService);
        SettingsViewModel = new SettingsViewModel(SettingsService);
    }

    public ISettingsService SettingsService { get; }

    public ThemeService ThemeService { get; }

    public ResultsStore ResultsStore { get; }

    public IFileActionService FileActionService { get; }

    public ScanViewModel ScanViewModel { get; }

    public ResultsViewModel ResultsViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }
}
