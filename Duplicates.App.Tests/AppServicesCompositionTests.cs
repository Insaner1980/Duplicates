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
        services.PathScopeViewModel.AddFolder("C:\\ExifComposition");
        Assert.True(services.ExifRemoverViewModel.CleanImagesCommand.CanExecute(false));

        Assert.True(services.OperationCoordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun),
            static () => { },
            out IAppOperationLease? lease));
        Assert.False(services.ExifRemoverViewModel.CleanImagesCommand.CanExecute(false));

        lease!.Dispose();
        Assert.True(services.ExifRemoverViewModel.CleanImagesCommand.CanExecute(false));
    }
}
