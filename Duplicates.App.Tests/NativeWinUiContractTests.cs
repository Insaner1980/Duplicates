using System.Xml;
using System.Xml.Linq;

namespace Duplicates.App.Tests;

public sealed class NativeWinUiContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Colors_KeepTheLockedFiveColorPalette()
    {
        XDocument colors = LoadXaml(@"Themes\Colors.xaml");
        Dictionary<string, string> palette = colors
            .Descendants(Presentation + "Color")
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Key")!,
                element => element.Value.Trim(),
                StringComparer.Ordinal);

        Assert.Equal("#000000", palette["PaletteBlack"]);
        Assert.Equal("#181818", palette["PaletteBackground"]);
        Assert.Equal("#242424", palette["PaletteSurface"]);
        Assert.Equal("#D72323", palette["PaletteAccentRed"]);
        Assert.Equal("#F5EDED", palette["PaletteWarmWhite"]);
        Assert.Equal(5, palette.Count);
    }

    [Fact]
    public void Resources_DoNotDefineLegacyButtonOrCardEmulationStyles()
    {
        XDocument styles = LoadXaml(@"Themes\Styles.xaml");
        HashSet<string> keys = styles
            .Descendants()
            .Attributes(Xaml + "Key")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);

        string[] forbidden =
        [
            "PrimaryButtonStyle",
            "SecondaryButtonStyle",
            "DangerButtonStyle",
            "WhiteOutlineButtonStyle",
            "RemoveFolderButtonStyle",
        ];

        Assert.DoesNotContain(forbidden, keys.Contains);
    }

    [Fact]
    public void Colors_DoNotReplacePerControlDefaultStateResources()
    {
        XDocument colors = LoadXaml(@"Themes\Colors.xaml");
        HashSet<string> keys = colors
            .Descendants()
            .Attributes(Xaml + "Key")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(
            keys,
            key =>
                key.StartsWith("Button", StringComparison.Ordinal) ||
                key.StartsWith("CheckBox", StringComparison.Ordinal) ||
                key.StartsWith("ToggleSwitch", StringComparison.Ordinal) ||
                key.StartsWith("RadioButton", StringComparison.Ordinal));
    }

    [Fact]
    public void MainWindow_UsesOneNativeTitleBarNavigationViewAndFrame()
    {
        XDocument main = LoadXaml("MainWindow.xaml");

        Assert.Single(main.Descendants(Presentation + "TitleBar"));
        Assert.Single(main.Descendants(Presentation + "NavigationView"));
        Assert.Single(main.Descendants(Presentation + "Frame"));

        XElement root = main
            .Descendants(Presentation + "Grid")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "Root");
        Assert.Equal("640", (string?)root.Attribute("MinWidth"));
    }

    [Fact]
    public void ScanPage_UsesNativeControlsWithoutLegacyCards()
    {
        XDocument page = LoadXaml(@"Views\ScanPage.xaml");

        Assert.Equal(2, page.Descendants(Presentation + "NumberBox").Count());
        Assert.NotEmpty(page.Descendants(Presentation + "ListView"));
        Assert.NotEmpty(page.Descendants(Presentation + "Expander"));
        Assert.NotEmpty(page.Descendants(Presentation + "InfoBar"));
        Assert.NotEmpty(page.Descendants(Presentation + "ProgressBar"));
        Assert.DoesNotContain(
            page.Descendants(),
            element =>
                (string?)element.Attribute("Style") ==
                "{StaticResource CardBorderStyle}");
    }

    [Fact]
    public void ScanPage_UsesWinUiAccentStyleOnlyForPrimaryActions()
    {
        XDocument page = LoadXaml(@"Views\ScanPage.xaml");
        XElement[] accentButtons = page
            .Descendants(Presentation + "Button")
            .Where(
                element =>
                    (string?)element.Attribute("Style") ==
                    "{StaticResource AccentButtonStyle}")
            .ToArray();

        Assert.Equal(2, accentButtons.Length);
    }

    [Fact]
    public void SettingsPage_UsesRecommendedSettingsControlsAndNumberBox()
    {
        XDocument page = LoadXaml(@"Views\SettingsPage.xaml");
        XNamespace toolkit = "using:CommunityToolkit.WinUI.Controls";

        Assert.Equal(3, page.Descendants(toolkit + "SettingsExpander").Count());
        Assert.NotEmpty(page.Descendants(toolkit + "SettingsCard"));
        Assert.Single(page.Descendants(Presentation + "NumberBox"));
        Assert.Empty(page.Descendants(Presentation + "TextBox"));
    }

    [Fact]
    public void ResultsPage_UsesGroupedListViewInsteadOfTappedBorders()
    {
        XDocument page = LoadXaml(@"Views\ResultsPage.xaml");

        XElement results = page
            .Descendants(Presentation + "ListView")
            .Single(
                element =>
                    (string?)element.Attribute(Xaml + "Name") == "ResultsList");

        Assert.Equal("Single", (string?)results.Attribute("SelectionMode"));
        Assert.Equal(
            "{Binding SelectedFile, Mode=TwoWay}",
            (string?)results.Attribute("SelectedItem"));
        Assert.DoesNotContain(
            page.Descendants(Presentation + "Border"),
            element => element.Attribute("Tapped") is not null);
    }

    [Fact]
    public void ResultsPage_UsesNativeCommandBarAndSplitView()
    {
        XDocument page = LoadXaml(@"Views\ResultsPage.xaml");

        Assert.Single(page.Descendants(Presentation + "CommandBar"));
        Assert.NotEmpty(page.Descendants(Presentation + "AppBarButton"));
        Assert.Single(page.Descendants(Presentation + "SplitView"));
        Assert.NotEmpty(page.Descendants(Presentation + "MenuFlyout"));

        XDocument styles = LoadXaml(@"Themes\Styles.xaml");
        Assert.DoesNotContain(
            styles.Descendants().Attributes(Xaml + "Key"),
            attribute => attribute.Value == "CardBorderStyle");
    }

    [Fact]
    public void InteractiveImagesAndIconOnlyButtonsHaveAccessibleNames()
    {
        foreach ((string path, XDocument document) in AllProductionXaml())
        {
            foreach (XElement button in document.Descendants(Presentation + "Button"))
            {
                bool hasText = button
                    .Descendants(Presentation + "TextBlock")
                    .Any(text => text.Attribute("Text") is not null);
                bool hasContent = button.Attribute("Content") is not null;
                bool hasAccessibleName = button
                    .Attributes()
                    .Any(
                        attribute =>
                            attribute.Name.LocalName ==
                            "AutomationProperties.Name");

                Assert.True(
                    hasText || hasContent || hasAccessibleName,
                    Location(path, button));
            }
        }
    }

    [Fact]
    public void ProductionXaml_DoesNotHandleTapOnLayoutElements()
    {
        foreach ((string path, XDocument document) in AllProductionXaml())
        {
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName is "Border" or "Grid" or "StackPanel")
                {
                    Assert.False(
                        element.Attribute("Tapped") is not null,
                        Location(path, element));
                }
            }
        }
    }

    [Fact]
    public void PrimaryCommandsExposeAccessKeysAndResultsAccelerators()
    {
        XDocument scan = LoadXaml(@"Views\ScanPage.xaml");
        Assert.Contains(
            scan.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("AccessKey") == "A");
        Assert.Contains(
            scan.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("AccessKey") == "S");

        XDocument results = LoadXaml(@"Views\ResultsPage.xaml");
        string[] accessKeys = results
            .Descendants()
            .Attributes("AccessKey")
            .Select(attribute => attribute.Value)
            .ToArray();
        Assert.Contains("R", accessKeys);
        Assert.Contains("D", accessKeys);
        Assert.Contains("P", accessKeys);

        string[] accelerators = results
            .Descendants(Presentation + "KeyboardAccelerator")
            .Select(
                accelerator =>
                    $"{(string?)accelerator.Attribute("Modifiers")}+" +
                    $"{(string?)accelerator.Attribute("Key")}")
            .ToArray();
        Assert.Contains("Control+F", accelerators);
        Assert.Contains("Control+Z", accelerators);
    }

    [Fact]
    public void Resources_DoNotOverrideNativeFocusVisuals()
    {
        string[] forbidden =
        [
            "FocusVisualPrimaryBrush",
            "FocusVisualSecondaryBrush",
            "SystemControlFocusVisualPrimaryBrush",
            "FocusStrokeColorOuterBrush",
        ];

        foreach ((string path, XDocument document) in AllProductionXaml())
        {
            Assert.DoesNotContain(
                document.Descendants().Attributes(Xaml + "Key"),
                attribute => forbidden.Contains(attribute.Value, StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData(@"Views\ScanPage.xaml")]
    [InlineData(@"Views\SettingsPage.xaml")]
    public void ResponsivePages_DoNotAllowHorizontalContentClipping(string relativePath)
    {
        XDocument page = LoadXaml(relativePath);
        XElement scrollViewer = Assert.Single(
            page.Descendants(Presentation + "ScrollViewer"));

        Assert.Equal(
            "Disabled",
            (string?)scrollViewer.Attribute("HorizontalScrollMode"));
        Assert.Equal(
            "Disabled",
            (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void ScanPage_CentersActionsFoldersAndOptionsOnOneWorkArea()
    {
        XDocument styles = LoadXaml(@"Themes\Styles.xaml");
        XElement workAreaWidth = styles
            .Descendants(Xaml + "Double")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Key") == "ScanWorkAreaMaxWidth");
        Assert.Equal("680", workAreaWidth.Value.Trim());

        XDocument page = LoadXaml(@"Views\ScanPage.xaml");
        string[] centeredNames =
        [
            "HeaderActionRail",
            "FoldersActionRail",
            "FoldersList",
            "EmptyFoldersState",
            "ScanOptions",
        ];

        foreach (string name in centeredNames)
        {
            XElement element = page
                .Descendants()
                .Single(candidate =>
                    (string?)candidate.Attribute(Xaml + "Name") == name);
            Assert.Equal(
                "{StaticResource ScanWorkAreaMaxWidth}",
                (string?)element.Attribute("MaxWidth"));
            Assert.Equal("Stretch", (string?)element.Attribute("HorizontalAlignment"));
        }

        XElement scanOptions = page
            .Descendants(Presentation + "Expander")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Name") == "ScanOptions");
        Assert.Null(scanOptions.Attribute("Width"));
    }

    [Fact]
    public void ScanPage_FolderRowsExposeNameParentPathAndNearbyRemoveAction()
    {
        XDocument page = LoadXaml(@"Views\ScanPage.xaml");
        XElement foldersList = page
            .Descendants(Presentation + "ListView")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Name") == "FoldersList");
        XElement template = Assert.Single(
            foldersList.Descendants(Presentation + "DataTemplate"));

        Assert.Contains(
            template.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding DisplayName}");
        Assert.Contains(
            template.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding ParentPath}");

        XElement removeButton = template
            .Descendants(Presentation + "Button")
            .Single(element =>
                (string?)element.Attribute("AutomationProperties.Name") == "Remove folder");
        Assert.Equal("{Binding}", (string?)removeButton.Attribute("CommandParameter"));
        Assert.Equal(
            "{Binding FullPath}",
            (string?)template.Descendants(Presentation + "Grid").First()
                .Attribute("ToolTipService.ToolTip"));
    }

    private static XDocument LoadXaml(string relativePath)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "UiSource", relativePath);
        return XDocument.Load(path, LoadOptions.SetLineInfo);
    }

    private static IEnumerable<(string Path, XDocument Document)> AllProductionXaml()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "UiSource");
        return Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Select(path =>
                (
                    Path.GetRelativePath(root, path),
                    XDocument.Load(path, LoadOptions.SetLineInfo)));
    }

    private static string Location(string path, XElement element)
    {
        var lineInfo = (IXmlLineInfo)element;
        return $"{path}:{lineInfo.LineNumber}";
    }
}
