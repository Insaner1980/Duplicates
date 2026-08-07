using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Duplicates;
using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.Views;

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
    public void CompletedAnalysisDestination_ReopensResultsOnlyForTheSameTool()
    {
        MethodInfo? resolver = typeof(MainWindow).GetMethod(
            "ResolveAnalysisDestination",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(resolver);
        var session = new AnalysisSession(
            ToolKind.BigFiles,
            new AnalysisScope(),
            new LargeFileToolOptions(1_073_741_824),
            new AnalysisResult
            {
                Findings = [],
                Groups = [],
                SkippedPaths = [],
                Elapsed = TimeSpan.FromSeconds(1),
            },
            DateTimeOffset.UtcNow);

        Assert.Equal(
            typeof(AnalysisResultsPage),
            resolver.Invoke(null, [ToolKind.BigFiles, session]));
        Assert.Equal(
            typeof(AnalysisPage),
            resolver.Invoke(null, [ToolKind.EmptyFiles, session]));
        Assert.Equal(
            typeof(AnalysisPage),
            resolver.Invoke(null, [ToolKind.BigFiles, null]));
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
    public void AnalysisPage_ReusesNativeSetupAndProgressControls()
    {
        XDocument page = LoadXaml(@"Views\AnalysisPage.xaml");

        Assert.Single(page.Descendants(), element => element.Name.LocalName == "PathScopeEditor");
        Assert.Single(page.Descendants(Presentation + "Expander"));
        Assert.Single(page.Descendants(Presentation + "InfoBar"));
        Assert.Single(page.Descendants(Presentation + "ProgressBar"));
        Assert.Single(
            page.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("Style") == "{StaticResource AccentButtonStyle}");

        XElement scrollViewer = Assert.Single(page.Descendants(Presentation + "ScrollViewer"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollMode"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void AnalysisPage_UsesNativeBigFileInputAndExactPresets()
    {
        XDocument page = LoadXaml(@"Views\AnalysisPage.xaml");
        XElement options = page
            .Descendants(Presentation + "Expander")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "AnalysisOptions");
        XElement numberBox = Assert.Single(options.Descendants(Presentation + "NumberBox"));

        Assert.Equal("{Binding OptionsVisibility}", (string?)options.Attribute("Visibility"));
        Assert.Equal("Minimum size, bytes", (string?)numberBox.Attribute("Header"));
        Assert.Equal("0", (string?)numberBox.Attribute("Minimum"));
        Assert.Equal("InvalidInputOverwritten", (string?)numberBox.Attribute("ValidationMode"));
        Assert.Equal(
            "{Binding LargeFileMinimumSizeValue, Mode=TwoWay}",
            (string?)numberBox.Attribute("Value"));
        Assert.Equal(
            new[] { "Any", "100 MB", "1 GB", "10 GB" },
            options.Descendants(Presentation + "Button")
                .Select(button => (string?)button.Attribute("Content"))
                .Where(static content => content is not null));
        Assert.Equal(
            new[]
            {
                "{Binding SetLargeFileMinimumSizeToAnyCommand}",
                "{Binding SetLargeFileMinimumSizeTo100MbCommand}",
                "{Binding SetLargeFileMinimumSizeTo1GbCommand}",
                "{Binding SetLargeFileMinimumSizeTo10GbCommand}",
            },
            options.Descendants(Presentation + "Button")
                .Select(button => (string?)button.Attribute("Command")));
    }

    [Fact]
    public void AnalysisResultsPage_UsesOneNativeListCommandBarAndResponsivePreview()
    {
        XDocument page = LoadXaml(@"Views\AnalysisResultsPage.xaml");

        Assert.Single(page.Descendants(Presentation + "CommandBar"));
        Assert.Single(page.Descendants(Presentation + "ListView"));
        Assert.Single(page.Descendants(Presentation + "SplitView"));
        Assert.NotEmpty(page.Descendants(Presentation + "VisualState"));

        XElement list = Assert.Single(page.Descendants(Presentation + "ListView"));
        Assert.Equal("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollMode"));
        Assert.Equal("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("{Binding SelectedResult, Mode=TwoWay}", (string?)list.Attribute("SelectedItem"));

        XElement skipped = Assert.Single(page.Descendants(Presentation + "InfoBar"));
        Assert.Equal("{Binding HasSkippedPaths}", (string?)skipped.Attribute("IsOpen"));
        Assert.DoesNotContain(
            skipped.Ancestors(),
            ancestor => ancestor.Name == Presentation + "SplitView");
    }

    [Fact]
    public void AnalysisResultsPage_SeparatesPreviewFromPathAndSimilarityActionSelection()
    {
        XDocument page = LoadXaml(@"Views\AnalysisResultsPage.xaml");
        XElement pathTemplate = page
            .Descendants(Presentation + "DataTemplate")
            .Single(template => (string?)template.Attribute(Xaml + "Key") == "PathFindingTemplate");
        XElement groupTemplate = page
            .Descendants(Presentation + "DataTemplate")
            .Single(template => (string?)template.Attribute(Xaml + "Key") == "SimilarityGroupTemplate");

        Assert.Contains(
            pathTemplate.Descendants(Presentation + "CheckBox"),
            checkBox => (string?)checkBox.Attribute("IsChecked") == "{Binding IsSelected, Mode=TwoWay}");
        XElement similarityItems = Assert.Single(groupTemplate.Descendants(Presentation + "ItemsControl"));
        Assert.Equal("{Binding Items}", (string?)similarityItems.Attribute("ItemsSource"));
        Assert.Contains(
            similarityItems.Descendants(Presentation + "CheckBox"),
            checkBox => (string?)checkBox.Attribute("IsChecked") == "{Binding IsSelected, Mode=TwoWay}");

        XElement list = Assert.Single(page.Descendants(Presentation + "ListView"));
        Assert.Equal("Single", (string?)list.Attribute("SelectionMode"));
        Assert.Equal("{Binding SelectedResult, Mode=TwoWay}", (string?)list.Attribute("SelectedItem"));
    }

    [Fact]
    public void AnalysisResultsPage_KeepsCompletePathsAvailableAndIconCommandsAccessible()
    {
        XDocument page = LoadXaml(@"Views\AnalysisResultsPage.xaml");

        Assert.Contains(
            page.Descendants(Presentation + "TextBlock"),
            text =>
                (string?)text.Attribute("Text") == "{Binding FullPath}" &&
                (string?)text.Attribute("TextWrapping") == "Wrap" &&
                (string?)text.Attribute("MaxLines") == "2");
        XElement previewPath = page
            .Descendants(Presentation + "TextBlock")
            .Single(text => (string?)text.Attribute("Text") == "{Binding PreviewPath}");
        Assert.Equal("Wrap", (string?)previewPath.Attribute("TextWrapping"));
        Assert.Equal("None", (string?)previewPath.Attribute("TextTrimming"));
        Assert.Equal("True", (string?)previewPath.Attribute("IsTextSelectionEnabled"));

        foreach (XElement button in page.Descendants(Presentation + "AppBarButton"))
        {
            Assert.True(
                button.Attribute("Label") is not null ||
                button.Attribute("AutomationProperties.Name") is not null,
                Location(@"Views\AnalysisResultsPage.xaml", button));
        }
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
    public void ResultPages_ExposeNativeMoveAndExportCommands()
    {
        XDocument exactResults = LoadXaml(@"Views\ResultsPage.xaml");
        Assert.Contains(
            exactResults.Descendants(Presentation + "AppBarButton"),
            button =>
                (string?)button.Attribute("Label") == "Move selected" &&
                (string?)button.Attribute("Click") == "MoveSelected_Click");
        Assert.Contains(
            exactResults.Descendants(Presentation + "AppBarButton"),
            button =>
                (string?)button.Attribute("Label") == "Export" &&
                (string?)button.Attribute("Click") == "Export_Click");

        XDocument analysisResults = LoadXaml(@"Views\AnalysisResultsPage.xaml");
        Assert.Contains(
            analysisResults.Descendants(Presentation + "AppBarButton"),
            button =>
                (string?)button.Attribute("Label") == "Delete selected" &&
                (string?)button.Attribute("Click") == "DeleteSelected_Click");
        Assert.Contains(
            analysisResults.Descendants(Presentation + "AppBarButton"),
            button =>
                (string?)button.Attribute("Label") == "Move selected" &&
                (string?)button.Attribute("Click") == "MoveSelected_Click");
        Assert.Contains(
            analysisResults.Descendants(Presentation + "AppBarButton"),
            button =>
                (string?)button.Attribute("Label") == "Export" &&
                (string?)button.Attribute("Click") == "Export_Click");
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
    public void PathScopeEditor_PickerButtonsHaveUniqueAccessKeys()
    {
        XDocument editor = LoadXaml(@"Views\Controls\PathScopeEditor.xaml");
        XDocument scan = LoadXaml(@"Views\ScanPage.xaml");
        string[] pickerHandlers =
        [
            "AddIncludedFolder_Click",
            "AddIncludedFile_Click",
            "AddExcludedFolder_Click",
            "AddExcludedFile_Click",
        ];

        XElement[] pickerButtons = editor
            .Descendants(Presentation + "Button")
            .Where(button => pickerHandlers.Contains((string?)button.Attribute("Click"), StringComparer.Ordinal))
            .ToArray();
        string[] accessKeys = pickerButtons
            .Select(button => (string?)button.Attribute("AccessKey"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToArray();

        Assert.Equal(pickerHandlers.Length, pickerButtons.Length);
        Assert.Equal(pickerButtons.Length, accessKeys.Length);
        Assert.Equal(accessKeys.Length, accessKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        XElement startScan = Assert.Single(
            scan.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("AccessKey") == "S");
        string startScanAccessKey = Assert.IsType<string>(startScan.Attribute("AccessKey")?.Value);
        Assert.Equal(
            accessKeys.Length + 1,
            accessKeys.Append(startScanAccessKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("F", AccessKeyFor(pickerButtons, "AddIncludedFolder_Click"));
        Assert.Equal("I", AccessKeyFor(pickerButtons, "AddIncludedFile_Click"));
        Assert.Equal("X", AccessKeyFor(pickerButtons, "AddExcludedFolder_Click"));
        Assert.Equal("E", AccessKeyFor(pickerButtons, "AddExcludedFile_Click"));
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

    private static string? AccessKeyFor(IEnumerable<XElement> buttons, string clickHandler) =>
        buttons
            .Single(button => (string?)button.Attribute("Click") == clickHandler)
            .Attribute("AccessKey")?.Value;

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
