using Duplicates.Engine;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class ScanViewModelTests
{
    [Fact]
    public void AddFolderCreatesReadablePresentationWithoutChangingCanonicalPath()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"Duplicates-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "Documents", "ObsidianVault");
        Directory.CreateDirectory(folder);

        try
        {
            var scope = new PathScopeViewModel();
            var viewModel = new ScanViewModel(
                new DuplicateScanner(),
                new FakeSettingsService(),
                new ResultsStore(),
                scope);
            Assert.Same(scope, viewModel.PathScope);

            scope.AddFolder(folder);

            ScopePathViewModel item = Assert.Single(scope.IncludedPaths);
            Assert.Equal(Path.GetFullPath(folder), item.FullPath);
            Assert.Equal("ObsidianVault", item.DisplayName);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(folder)), item.ParentPath);
            Assert.True(scope.HasIncludedPaths);
            Assert.Equal(Visibility.Collapsed, scope.EmptyIncludedPathsVisibility);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FolderCommandsUseCanonicalFullPathAndKeepOneSelection()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"Duplicates-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "ObsidianVault");
        Directory.CreateDirectory(folder);

        try
        {
            var scope = new PathScopeViewModel();
            var viewModel = new ScanViewModel(
                new DuplicateScanner(),
                new FakeSettingsService(),
                new ResultsStore(),
                scope);
            Assert.Same(scope, viewModel.PathScope);

            scope.AddFolder(folder);
            scope.AddFolder(folder + Path.DirectorySeparatorChar);

            ScopePathViewModel item = Assert.Single(scope.IncludedPaths);
            scope.RemoveIncludedPathCommand.Execute(item);

            Assert.Empty(scope.IncludedPaths);
            Assert.False(scope.HasIncludedPaths);
            Assert.Equal(Visibility.Visible, scope.EmptyIncludedPathsVisibility);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SettingsChanged_UpdatesScanDefaultsWhenNotScanning()
    {
        var settings = new FakeSettingsService();
        var viewModel = new ScanViewModel(new DuplicateScanner(), settings, new ResultsStore(), new PathScopeViewModel());

        settings.SetCurrent(new AppSettings
        {
            DefaultMinSizeBytes = 2048,
            IgnoreHiddenFiles = false,
            IgnoreSystemFiles = false,
            VerifyByteByByte = false,
        });

        Assert.Equal(2048d, viewModel.MinSizeValue);
        Assert.False(viewModel.IgnoreHiddenFiles);
        Assert.False(viewModel.IgnoreSystemFiles);
        Assert.False(viewModel.VerifyByteByByte);
    }

    [Fact]
    public void SettingsChanged_DoesNotOverwriteActiveScanOptions()
    {
        var settings = new FakeSettingsService();
        var viewModel = new ScanViewModel(new DuplicateScanner(), settings, new ResultsStore(), new PathScopeViewModel())
        {
            IsScanning = true,
            MinSizeValue = 99d,
        };

        settings.SetCurrent(new AppSettings { DefaultMinSizeBytes = 2048 });

        Assert.Equal(99d, viewModel.MinSizeValue);
    }

    [Fact]
    public void NewestPendingSettingsWaitForCoordinatorIdleAndScanTerminalState()
    {
        var settings = new FakeSettingsService();
        var coordinator = new AppOperationCoordinator();
        var scope = new PathScopeViewModel();
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            settings,
            new ResultsStore(),
            scope,
            coordinator)
        {
            IsScanning = true,
            MinSizeValue = 99,
        };
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.ExactScan),
            static () => { },
            out IAppOperationLease? lease));
        settings.SetCurrent(new AppSettings { DefaultMinSizeBytes = 100, DefaultIncludeSubfolders = false });
        settings.SetCurrent(new AppSettings { DefaultMinSizeBytes = 200, DefaultIncludeSubfolders = true });

        lease!.Dispose();

        Assert.Equal(99, viewModel.MinSizeValue);
        Assert.True(scope.IncludeSubfolders);

        viewModel.IsScanning = false;

        Assert.Equal(200, viewModel.MinSizeValue);
        Assert.True(scope.IncludeSubfolders);
    }

    [Fact]
    public void SizePresets_SetNumberBoxValuesAndNoMaximum()
    {
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore(),
            new PathScopeViewModel());

        viewModel.UseOneKilobyteMinimumCommand.Execute(null);
        Assert.Equal(1024d, viewModel.MinSizeValue);

        viewModel.UseOneMegabyteMinimumCommand.Execute(null);
        Assert.Equal(1_048_576d, viewModel.MinSizeValue);

        viewModel.UseAnySizeCommand.Execute(null);
        Assert.Equal(0d, viewModel.MinSizeValue);
        Assert.True(double.IsNaN(viewModel.MaxSizeValue));
    }

    [Theory]
    [InlineData(-1d, 0L)]
    [InlineData(123.9d, 123L)]
    [InlineData(double.PositiveInfinity, 7L)]
    public void ByteSizeInput_NormalizesNumberBoxValues(double value, long expected)
    {
        Assert.Equal(expected, ByteSizeInput.ToBytes(value, fallback: 7L));
    }

    [Fact]
    public void ByteSizeInput_TreatsEmptyMaximumAsUnbounded()
    {
        Assert.Equal(
            long.MaxValue,
            ByteSizeInput.ToBytes(
                ByteSizeInput.NoMaximum,
                fallback: 7L,
                noValueMeansMaximum: true));
        Assert.True(double.IsNaN(ByteSizeInput.FromBytes(long.MaxValue)));
    }

    [Fact]
    public void FilterOptionVisibilityTracksSelectedMode()
    {
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore(),
            new PathScopeViewModel());

        Assert.Equal(Visibility.Collapsed, viewModel.CategoryFiltersVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.CustomExtensionsVisibility);

        viewModel.SelectedFileFilterIndex = 1;

        Assert.Equal(Visibility.Visible, viewModel.CategoryFiltersVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.CustomExtensionsVisibility);

        viewModel.SelectedFileFilterIndex = 2;

        Assert.Equal(Visibility.Collapsed, viewModel.CategoryFiltersVisibility);
        Assert.Equal(Visibility.Visible, viewModel.CustomExtensionsVisibility);
    }
}
