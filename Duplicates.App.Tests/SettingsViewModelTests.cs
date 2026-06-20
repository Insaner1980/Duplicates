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
    public void MinimumSizePresetCommandsUpdateDefaultMinSizeText()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());

        viewModel.UseAnySizeDefaultCommand.Execute(null);
        Assert.Equal("0", viewModel.DefaultMinSizeText);

        viewModel.UseOneKilobyteDefaultCommand.Execute(null);
        Assert.Equal("1024", viewModel.DefaultMinSizeText);

        viewModel.UseOneMegabyteDefaultCommand.Execute(null);
        Assert.Equal("1048576", viewModel.DefaultMinSizeText);
    }

    [Fact]
    public void AboutTextIncludesAppAssemblyVersion()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());
        string version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

        Assert.Contains(version, viewModel.AboutText);
    }
}
