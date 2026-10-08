using Duplicates.Engine;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

public sealed class ByteSizeEditorViewModelTests
{
    [Theory]
    [InlineData(1_073_741_824d, 1d, 3)]
    [InlineData(104_857_600d, 100d, 2)]
    [InlineData(1024d, 1d, 1)]
    [InlineData(1536d, 1536d, 0)]
    [InlineData(1d, 1d, 0)]
    public void ExternalByteValuesUseTheLargestExactUnit(double bytes, double amount, int unitIndex)
    {
        double stored = bytes;
        var editor = new ByteSizeEditorViewModel(() => stored, value => stored = value);

        Assert.Equal(amount, editor.Amount);
        Assert.Equal(unitIndex, editor.UnitIndex);
        Assert.Equal(["B", "KB", "MB", "GB"], editor.Units);
    }

    [Fact]
    public void EmptyAndZeroValuesKeepTheCurrentUnit()
    {
        double stored = 1_048_576d;
        var editor = new ByteSizeEditorViewModel(() => stored, value => stored = value);

        stored = 0d;
        editor.Refresh();
        Assert.Equal(0d, editor.Amount);
        Assert.Equal(2, editor.UnitIndex);

        stored = double.NaN;
        editor.Refresh();
        Assert.True(double.IsNaN(editor.Amount));
        Assert.Equal(2, editor.UnitIndex);
    }

    [Fact]
    public void UserEditsWriteBytesWithoutReformattingTheVisibleUnit()
    {
        double stored = 1024d;
        ByteSizeEditorViewModel? editor = null;
        editor = new ByteSizeEditorViewModel(
            () => stored,
            value =>
            {
                stored = value;
                editor!.Refresh();
            });

        editor.Amount = 1.5;
        Assert.Equal(1536d, stored);
        Assert.Equal(1.5, editor.Amount);
        Assert.Equal(1, editor.UnitIndex);

        editor.UnitIndex = 2;
        Assert.Equal(1_572_864d, stored);
        Assert.Equal(1.5, editor.Amount);
        Assert.Equal(2, editor.UnitIndex);

        editor.Amount = double.NaN;
        Assert.True(double.IsNaN(stored));
    }

    [Fact]
    public void InvalidUnitSelectionsAreIgnored()
    {
        double stored = 2048d;
        var editor = new ByteSizeEditorViewModel(() => stored, value => stored = value);

        editor.UnitIndex = -1;

        Assert.Equal(1, editor.UnitIndex);
        Assert.Equal(2048d, stored);
    }

    [Fact]
    public void ViewModelEditorsFollowPresetsAndDefaults()
    {
        var scan = new ScanViewModel(
            new DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore(),
            new PathScopeViewModel());
        scan.UseOneMegabyteMinimumCommand.Execute(null);
        Assert.Equal((1d, 2), (scan.MinSizeEditor.Amount, scan.MinSizeEditor.UnitIndex));
        scan.MaxSizeEditor.Amount = 10;
        Assert.Equal(10d, scan.MaxSizeValue);

        var settings = new SettingsViewModel(new FakeSettingsService());
        settings.DefaultLargeFileMinimumEditor.UnitIndex = 2;
        Assert.Equal(1_048_576d, settings.DefaultLargeFileMinimumValue);
        settings.UseOneKilobyteDefaultCommand.Execute(null);
        Assert.Equal((1d, 1), (settings.DefaultMinSizeEditor.Amount, settings.DefaultMinSizeEditor.UnitIndex));
    }
}
