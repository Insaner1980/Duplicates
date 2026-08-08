using Duplicates.Engine;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
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
        AnalysisSessionStore = new AnalysisSessionStore();
        FileFormatProbe = new WindowsFileFormatProbe();
        AnalysisService = new AnalysisService(FileFormatProbe);
        FileActionService = new FileActionService(SettingsService);
        ResultExportService = new ResultExportService();
        PathScopeViewModel = new PathScopeViewModel();

        ScanViewModel = new ScanViewModel(new DuplicateScanner(), SettingsService, ResultsStore, PathScopeViewModel);
        ResultsViewModel = new ResultsViewModel(ResultsStore, FileActionService, SettingsService, ResultExportService);
        AnalysisViewModel = new AnalysisViewModel(AnalysisService, AnalysisSessionStore, PathScopeViewModel);
        AnalysisResultsViewModel = new AnalysisResultsViewModel(
            AnalysisSessionStore,
            FileActionService,
            ResultExportService,
            FileSignatureDetector.DetectFileAsync,
            FileFormatProbe);
        SettingsViewModel = new SettingsViewModel(SettingsService);
    }

    public ISettingsService SettingsService { get; }

    public ThemeService ThemeService { get; }

    public ResultsStore ResultsStore { get; }

    public AnalysisSessionStore AnalysisSessionStore { get; }

    public IFileFormatProbe FileFormatProbe { get; }

    public IAnalysisService AnalysisService { get; }

    public IFileActionService FileActionService { get; }

    public IResultExportService ResultExportService { get; }

    public PathScopeViewModel PathScopeViewModel { get; }

    public ScanViewModel ScanViewModel { get; }

    public ResultsViewModel ResultsViewModel { get; }

    public AnalysisViewModel AnalysisViewModel { get; }

    public AnalysisResultsViewModel AnalysisResultsViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }
}
