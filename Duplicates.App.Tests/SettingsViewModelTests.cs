using Duplicates.Models;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void PermanentDeleteWarningVisibilityTracksDeletionMode()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());

        Assert.Equal(Visibility.Collapsed, viewModel.PermanentDeleteWarningVisibility);

        viewModel.SelectedDeletionMode = DeletionMode.Permanent;

        Assert.Equal(Visibility.Visible, viewModel.PermanentDeleteWarningVisibility);
    }

    [Fact]
    public void MinimumSizePresetCommandsUpdateDefaultMinSizeValue()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());

        viewModel.UseAnySizeDefaultCommand.Execute(null);
        Assert.Equal(0d, viewModel.DefaultMinSizeValue);

        viewModel.UseOneKilobyteDefaultCommand.Execute(null);
        Assert.Equal(1024d, viewModel.DefaultMinSizeValue);

        viewModel.UseOneMegabyteDefaultCommand.Execute(null);
        Assert.Equal(1_048_576d, viewModel.DefaultMinSizeValue);
    }

    [Theory]
    [InlineData(2048d, 2048L)]
    [InlineData(-1d, 0L)]
    public void DefaultMinimumSizeValue_IsNormalizedBeforeSaving(
        double input,
        long expected)
    {
        var settings = new FakeSettingsService();
        var viewModel = new SettingsViewModel(settings);

        viewModel.DefaultMinSizeValue = input;

        Assert.Equal(expected, settings.Current.DefaultMinSizeBytes);
    }

    [Fact]
    public void AboutTextIncludesAppAssemblyVersion()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());
        string version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

        Assert.Contains(version, viewModel.AboutText);
    }
}
