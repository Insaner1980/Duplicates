using System.Reflection;
using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class AppServicesCompositionTests
{
    [Fact]
    public void ExifRemoverUsesTheSharedScopeCoordinatorAndProductionCleaner()
    {
        var services = new AppServices();

        Assert.IsType<ExifCleanerService>(services.ExifCleanerService);
        Assert.Same(services.PathScopeViewModel, services.ExifRemoverViewModel.PathScope);
        services.PathScopeViewModel.IncludedPaths.Add(new Duplicates.ViewModels.ScopePathViewModel("C:\\ExifComposition", Duplicates.Models.ScopePathKind.Folder));
        Assert.True(services.ExifRemoverViewModel.CleanImagesCommand.CanExecute(false));

        Assert.True(services.OperationCoordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun),
            static () => { },
            out IAppOperationLease? lease));
        Assert.False(services.ExifRemoverViewModel.CleanImagesCommand.CanExecute(false));

        lease!.Dispose();
        Assert.True(services.ExifRemoverViewModel.CleanImagesCommand.CanExecute(false));
    }

    [Fact]
    public void VideoOptimizerUsesSharedScopeCoordinatorAndIdentityTransactions()
    {
        var services = new AppServices();

        Assert.IsType<VideoOptimizerService>(services.VideoOptimizerService);
        var transactions = Assert.IsType<IdentityFileTransactions>(services.IdentityFileTransactions);
        Assert.Same(services.PathScopeViewModel, services.VideoOptimizerViewModel.PathScope);
        Assert.Same(
            transactions,
            GetPrivateField(Assert.IsType<ExifCleanerService>(services.ExifCleanerService), "_transactions"));
        Assert.Same(
            transactions,
            GetPrivateField(Assert.IsType<VideoOptimizerService>(services.VideoOptimizerService), "_transactions"));

        services.PathScopeViewModel.IncludedPaths.Add(new Duplicates.ViewModels.ScopePathViewModel("C:\\VideoOptimizerComposition", Duplicates.Models.ScopePathKind.Folder));
        Assert.True(services.VideoOptimizerViewModel.OptimizeVideosCommand.CanExecute(null));

        Assert.True(services.OperationCoordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.ExifCleaning),
            static () => { },
            out IAppOperationLease? lease));
        Assert.False(services.VideoOptimizerViewModel.OptimizeVideosCommand.CanExecute(null));

        lease!.Dispose();
        Assert.True(services.VideoOptimizerViewModel.OptimizeVideosCommand.CanExecute(null));
    }

    [Fact]
    public void SettingsUsesTheSharedMediaCacheAndOperationCoordinator()
    {
        var services = new AppServices();

        Assert.Same(
            services.MediaFingerprintCache,
            GetPrivateField(services.SettingsViewModel, "_cacheControl"));
        Assert.Same(
            services.OperationCoordinator,
            GetPrivateField(services.SettingsViewModel, "_operationCoordinator"));
        Assert.Same(
            services.SettingsService,
            GetPrivateField(services.AnalysisViewModel, "_settingsService"));
    }

    private static object? GetPrivateField(object instance, string name) =>
        instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);
}
