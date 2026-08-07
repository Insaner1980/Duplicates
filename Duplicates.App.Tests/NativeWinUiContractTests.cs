using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Duplicates;

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

        string[] expectedTags =
        [
            "DuplicateFiles", "SimilarImages", "SimilarVideos", "MusicDuplicates",
            "EmptyFolders", "BigFiles", "EmptyFiles", "TemporaryFiles",
            "InvalidLinks", "BrokenFiles", "BadExtensions", "BadNames",
            "ExifRemover", "VideoOptimizer",
        ];

        Assert.Single(main.Descendants(Presentation + "TitleBar"));
        Assert.Single(main.Descendants(Presentation + "NavigationView"));
        Assert.Single(main.Descendants(Presentation + "Frame"));
        Assert.Equal(expectedTags, NavigationTags(main));
        Assert.Equal(3, main.Descendants(Presentation + "NavigationViewItemHeader").Count());
        Assert.DoesNotContain(NavigationTags(main), tag => tag == "Results");

        foreach (XElement item in main.Descendants(Presentation + "NavigationViewItem"))
        {
            Assert.NotEmpty(item.Descendants(Presentation + "FontIcon"));
            Assert.False(string.IsNullOrWhiteSpace((string?)item.Attribute("Content")));
        }

        XElement root = main
            .Descendants(Presentation + "Grid")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "Root");
        Assert.Equal("640", (string?)root.Attribute("MinWidth"));
    }

    [Fact]
    public async Task UnavailableToolDialog_RestoresDuplicateFilesSelectionAfterItCloses()
    {
        MethodInfo? completion = typeof(MainWindow).GetMethod(
            "CompleteUnavailableToolSelectionAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(completion);

        var dialogClosed = new TaskCompletionSource<object?>();
        string? selectedTag = null;
        Task recovery = Assert.IsAssignableFrom<Task>(
            completion.Invoke(
                null,
                [
                    new Func<Task>(() => dialogClosed.Task),
                    new Action(() => selectedTag = "DuplicateFiles"),
                ]));

        Assert.Null(selectedTag);

        dialogClosed.SetResult(null);
        await recovery;

        Assert.Equal("DuplicateFiles", selectedTag);
    }

    [Fact]
    public void ScanPage_UsesNativeControlsWithoutLegacyCards()
    {
        XDocument page = LoadXaml(@"Views\ScanPage.xaml");
        XDocument scopeEditor = LoadXaml(@"Views\Controls\PathScopeEditor.xaml");

        Assert.Equal(2, page.Descendants(Presentation + "NumberBox").Count());
        Assert.Single(page.Descendants(), element => element.Name.LocalName == "PathScopeEditor");
        Assert.NotEmpty(scopeEditor.Descendants(Presentation + "ListView"));
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

        Assert.Single(accentButtons);
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
        XDocument scopeEditor = LoadXaml(@"Views\Controls\PathScopeEditor.xaml");
        Assert.Contains(
            scopeEditor.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("AccessKey") == "F");
        Assert.Contains(
            scopeEditor.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("AccessKey") == "I");
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
    public void ScanPage_CentersActionsScopeAndOptionsOnOneWorkArea()
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
            "PathScopeEditor",
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
    public void PathScopeEditor_ExposesCanonicalPathPresentationAndNativeActions()
    {
        XDocument editor = LoadXaml(@"Views\Controls\PathScopeEditor.xaml");
        XElement pathsList = editor
            .Descendants(Presentation + "ListView")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Name") == "IncludedPathsList");
        Assert.Equal("{Binding IncludedPaths}", (string?)pathsList.Attribute("ItemsSource"));

        XElement template = editor
            .Descendants(Presentation + "DataTemplate")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "PathItemTemplate");

        Assert.Contains(
            template.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding DisplayName}");
        Assert.Contains(
            template.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding ParentPath}");

        XElement removeButton = template
            .Descendants(Presentation + "Button")
            .Single(element =>
                (string?)element.Attribute("AutomationProperties.Name") == "Remove path");
        Assert.Equal("{Binding}", (string?)removeButton.Attribute("CommandParameter"));
        Assert.Equal(
            "{Binding FullPath}",
            (string?)template.Descendants(Presentation + "Grid").First()
                .Attribute("ToolTipService.ToolTip"));
        Assert.Contains(
            template.Descendants(Presentation + "MenuFlyoutItem"),
            item => (string?)item.Attribute("Text") == "Prefer copies in this folder");
    }

    [Fact]
    public void ScanPage_SmallMovesActionRailsBelowHeaderCopy()
    {
        XDocument page = LoadXaml(@"Views\ScanPage.xaml");
        XElement small = page
            .Descendants(Presentation + "VisualState")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Name") == "Small");
        Dictionary<string, string> setters = small
            .Descendants(Presentation + "Setter")
            .ToDictionary(
                element => (string)element.Attribute("Target")!,
                element => (string)element.Attribute("Value")!,
                StringComparer.Ordinal);

        Assert.Equal("1", setters["HeaderActionRail.(Grid.Row)"]);
    }

    [Fact]
    public void ResultsPage_UsesFullWidthAndKeepsCompletePathAvailable()
    {
        XDocument page = LoadXaml(@"Views\ResultsPage.xaml");
        XElement root = page.Root!
            .Elements(Presentation + "Grid")
            .Single();
        Assert.Equal("24", (string?)root.Attribute("Padding"));
        Assert.Null(root.Attribute("MaxWidth"));

        XElement rowPath = page
            .Descendants(Presentation + "TextBlock")
            .Single(element =>
                (string?)element.Attribute("Text") == "{Binding DisplayDirectoryPath}");
        Assert.Equal("Wrap", (string?)rowPath.Attribute("TextWrapping"));
        Assert.Equal("2", (string?)rowPath.Attribute("MaxLines"));

        XElement previewPath = page
            .Descendants(Presentation + "TextBlock")
            .Single(element =>
                (string?)element.Attribute("Text") == "{Binding PreviewPath}");
        Assert.Equal("Wrap", (string?)previewPath.Attribute("TextWrapping"));
        Assert.Equal("None", (string?)previewPath.Attribute("TextTrimming"));
        Assert.Equal("True", (string?)previewPath.Attribute("IsTextSelectionEnabled"));
    }

    private static XDocument LoadXaml(string relativePath)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "UiSource", relativePath);
        return XDocument.Load(path, LoadOptions.SetLineInfo);
    }

    private static string[] NavigationTags(XDocument main) => main
        .Descendants(Presentation + "NavigationViewItem")
        .Select(item => (string?)item.Attribute("Tag"))
        .Where(tag => tag is not null)
        .Cast<string>()
        .ToArray();

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
