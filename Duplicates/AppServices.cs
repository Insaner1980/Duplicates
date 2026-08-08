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
        MediaFingerprintCache = new MediaFingerprintCache();
        ImageSampleProvider = new WindowsImageSampleProvider(MediaFingerprintCache);
        VideoSampleProvider = new WindowsVideoSampleProvider(MediaFingerprintCache);
        MusicMetadataProvider = new WindowsMusicMetadataProvider();
        MediaPreviewLoader = new WindowsMediaPreviewLoader();
        FileFormatProbe = new WindowsFileFormatProbe();
        AnalysisService = new AnalysisService(
            fileFormatProbe: FileFormatProbe,
            imageSampleProvider: ImageSampleProvider,
            videoSampleProvider: VideoSampleProvider,
            musicMetadataProvider: MusicMetadataProvider);
        FileActionService = new FileActionService(SettingsService);
        ResultExportService = new ResultExportService();
        OperationCoordinator = new AppOperationCoordinator();
        var fileLinkPlatform = new FileLinkNative();
        RecycleBinService = new RecycleBinService(fileLinkPlatform);
        FileLinkService = new FileLinkService(fileLinkPlatform, RecycleBinService);
        IdentityFileTransactions = new IdentityFileTransactions();
        ExifCleanerService = new ExifCleanerService(
            new WicMetadataBackend(),
            IdentityFileTransactions,
            RecycleBinService);
        VideoOptimizerService = new VideoOptimizerService(
            new WindowsVideoMediaProbe(),
            new WindowsVideoTranscodeBackend(),
            IdentityFileTransactions);
        PathScopeViewModel = new PathScopeViewModel();

        ScanViewModel = new ScanViewModel(
            new DuplicateScanner(),
            SettingsService,
            ResultsStore,
            PathScopeViewModel,
            OperationCoordinator);
        ResultsViewModel = new ResultsViewModel(
            ResultsStore,
            FileActionService,
            SettingsService,
            ResultExportService,
            FileLinkService,
            OperationCoordinator);
        AnalysisViewModel = new AnalysisViewModel(
            AnalysisService,
            AnalysisSessionStore,
            PathScopeViewModel,
            OperationCoordinator);
        AnalysisResultsViewModel = new AnalysisResultsViewModel(
            AnalysisSessionStore,
            FileActionService,
            ResultExportService,
            FileSignatureDetector.DetectFileAsync,
            FileFormatProbe,
            AnalysisService,
            MediaPreviewLoader,
            OperationCoordinator);
        ExifRemoverViewModel = new ExifRemoverViewModel(
            ExifCleanerService,
            PathScopeViewModel,
            FileActionService,
            OperationCoordinator);
        VideoOptimizerViewModel = new VideoOptimizerViewModel(
            VideoOptimizerService,
            PathScopeViewModel,
            FileActionService,
            OperationCoordinator);
        SettingsViewModel = new SettingsViewModel(SettingsService);
    }

    public ISettingsService SettingsService { get; }

    public ThemeService ThemeService { get; }

    public ResultsStore ResultsStore { get; }

    public AnalysisSessionStore AnalysisSessionStore { get; }

    public MediaFingerprintCache MediaFingerprintCache { get; }

    public IImageSampleProvider ImageSampleProvider { get; }

    public IVideoSampleProvider VideoSampleProvider { get; }

    public IMusicMetadataProvider MusicMetadataProvider { get; }

    public IMediaPreviewLoader MediaPreviewLoader { get; }

    public IFileFormatProbe FileFormatProbe { get; }

    public IAnalysisService AnalysisService { get; }

    public IFileActionService FileActionService { get; }

    public IResultExportService ResultExportService { get; }

    public IAppOperationCoordinator OperationCoordinator { get; }

    public IRecycleBinService RecycleBinService { get; }

    public IFileLinkService FileLinkService { get; }

    internal IIdentityFileTransactions IdentityFileTransactions { get; }

    public IExifCleanerService ExifCleanerService { get; }

    public IVideoOptimizerService VideoOptimizerService { get; }

    public PathScopeViewModel PathScopeViewModel { get; }

    public ScanViewModel ScanViewModel { get; }

    public ResultsViewModel ResultsViewModel { get; }

    public AnalysisViewModel AnalysisViewModel { get; }

    public AnalysisResultsViewModel AnalysisResultsViewModel { get; }

    public ExifRemoverViewModel ExifRemoverViewModel { get; }

    public VideoOptimizerViewModel VideoOptimizerViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }
}
