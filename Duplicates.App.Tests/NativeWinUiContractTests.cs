using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Duplicates;
using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.Views;
using Duplicates.Views.Controls;

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
    public void AnalysisPage_UsesNativeStorageToolInputsAndExactBigFilePresets()
    {
        XDocument page = LoadXaml(@"Views\AnalysisPage.xaml");
        XElement options = page
            .Descendants(Presentation + "Expander")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "AnalysisOptions");
        XElement[] numberBoxes = options.Descendants(Presentation + "NumberBox").ToArray();
        Assert.Equal(2, numberBoxes.Length);
        XElement largeFileInput = numberBoxes.Single(
            numberBox => (string?)numberBox.Attribute("Header") == "Minimum size, bytes");
        XElement temporaryFileInput = numberBoxes.Single(
            numberBox => (string?)numberBox.Attribute("Header") == "Minimum age, days");

        Assert.Equal("{Binding OptionsVisibility}", (string?)options.Attribute("Visibility"));
        Assert.Equal("0", (string?)largeFileInput.Attribute("Minimum"));
        Assert.Equal("InvalidInputOverwritten", (string?)largeFileInput.Attribute("ValidationMode"));
        Assert.Equal(
            "{Binding LargeFileMinimumSizeValue, Mode=TwoWay}",
            (string?)largeFileInput.Attribute("Value"));
        Assert.Equal(
            "{Binding LargeFileOptionsVisibility}",
            (string?)largeFileInput.Parent?.Attribute("Visibility"));
        Assert.Equal("0", (string?)temporaryFileInput.Attribute("Minimum"));
        Assert.Equal("InvalidInputOverwritten", (string?)temporaryFileInput.Attribute("ValidationMode"));
        Assert.Equal(
            "{Binding TemporaryFileMinimumAgeDays, Mode=TwoWay}",
            (string?)temporaryFileInput.Attribute("Value"));
        Assert.Equal(
            "{Binding TemporaryFileOptionsVisibility}",
            (string?)temporaryFileInput.Parent?.Attribute("Visibility"));
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
    public void AnalysisPage_UsesSeparateNativeImageAndVideoPresetComboBoxes()
    {
        XDocument page = LoadXaml(@"Views\AnalysisPage.xaml");
        XElement imageCombo = page.Descendants(Presentation + "ComboBox")
            .Single(element => (string?)element.Attribute("Header") == "Image similarity");
        XElement videoCombo = page.Descendants(Presentation + "ComboBox")
            .Single(element => (string?)element.Attribute("Header") == "Video similarity");

        Assert.Equal(
            "{Binding ImageSimilarityPreset, Mode=TwoWay}",
            (string?)imageCombo.Attribute("SelectedIndex"));
        Assert.Equal(
            "{Binding SimilarImageOptionsVisibility}",
            (string?)imageCombo.Parent?.Attribute("Visibility"));
        Assert.Equal(
            new[] { "Strict", "Balanced", "Broad" },
            imageCombo.Elements(Presentation + "ComboBoxItem").Select(item => (string?)item.Attribute("Content")));
        Assert.Equal(
            "{Binding VideoSimilarityPreset, Mode=TwoWay}",
            (string?)videoCombo.Attribute("SelectedIndex"));
        Assert.Equal(
            "{Binding SimilarVideoOptionsVisibility}",
            (string?)videoCombo.Parent?.Attribute("Visibility"));
        Assert.Equal(
            new[] { "Strict", "Balanced", "Broad" },
            videoCombo.Elements(Presentation + "ComboBoxItem").Select(item => (string?)item.Attribute("Content")));
    }

    [Fact]
    public void MusicDuplicates_UsesExactDisclosureWithoutThresholdControlOrAcousticSimilarityCopy()
    {
        ToolDescriptor descriptor = ToolDescriptor.For(ToolKind.MusicDuplicates);
        XDocument setup = LoadXaml(@"Views\AnalysisPage.xaml");
        XDocument results = LoadXaml(@"Views\AnalysisResultsPage.xaml");

        Assert.Equal(
            "Match tracks using Windows music metadata and duration, not acoustic fingerprinting.",
            descriptor.Subtitle);
        Assert.DoesNotContain(
            setup.Descendants(Presentation + "NumberBox"),
            input => ((string?)input.Attribute("Header"))?.Contains("music", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            setup.Descendants(Presentation + "ComboBox"),
            input => ((string?)input.Attribute("Header"))?.Contains("music", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            setup.DescendantNodes().OfType<XText>().Concat(results.DescendantNodes().OfType<XText>()),
            text => text.Value.Contains("100% similar", StringComparison.OrdinalIgnoreCase));

        XElement groupTemplate = results
            .Descendants(Presentation + "DataTemplate")
            .Single(template => (string?)template.Attribute(Xaml + "Key") == "SimilarityGroupTemplate");
        Assert.Contains(
            groupTemplate.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding SummaryText}");
        Assert.Contains(
            groupTemplate.Descendants(Presentation + "TextBlock"),
            text =>
                (string?)text.Attribute("Text") == "{Binding FullPath}" &&
                (string?)text.Attribute("TextWrapping") == "Wrap" &&
                (string?)text.Attribute("ToolTipService.ToolTip") == "{Binding FullPath}");
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
        XElement previewCommand = Assert.Single(similarityItems.Descendants(Presentation + "HyperlinkButton"));
        Assert.Equal("{Binding DisplayName}", (string?)previewCommand.Attribute("Content"));
        Assert.Equal(
            "{Binding DataContext.SelectSimilarityPreviewItemCommand, ElementName=PageRoot}",
            (string?)previewCommand.Attribute("Command"));
        Assert.Equal("{Binding}", (string?)previewCommand.Attribute("CommandParameter"));
        Assert.Empty(similarityItems.Descendants(Presentation + "ListView"));
        Assert.Contains(
            similarityItems.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding MediaDetailsText}");
        Assert.Contains(
            similarityItems.Descendants(Presentation + "TextBlock"),
            text =>
                (string?)text.Attribute("Text") == "{Binding FullPath}" &&
                (string?)text.Attribute("ToolTipService.ToolTip") == "{Binding FullPath}");

        XElement list = Assert.Single(page.Descendants(Presentation + "ListView"));
        Assert.Equal("Single", (string?)list.Attribute("SelectionMode"));
        Assert.Equal("{Binding SelectedResult, Mode=TwoWay}", (string?)list.Attribute("SelectedItem"));
    }

    [Fact]
    public void AnalysisResultsPage_DisablesSelectionMutationDuringSharedOperation()
    {
        XDocument page = LoadXaml(@"Views\AnalysisResultsPage.xaml");
        XElement[] actionSelections = page
            .Descendants(Presentation + "CheckBox")
            .Where(element => ((string?)element.Attribute("AutomationProperties.Name"))?.Contains(
                "for action",
                StringComparison.Ordinal) == true)
            .ToArray();
        XElement clearSelection = page
            .Descendants(Presentation + "AppBarButton")
            .Single(element => (string?)element.Attribute("Label") == "Clear selection");

        Assert.Equal(2, actionSelections.Length);
        Assert.All(
            actionSelections,
            selection => Assert.Equal(
                "{Binding DataContext.CanMutateSelection, ElementName=PageRoot}",
                (string?)selection.Attribute("IsEnabled")));
        Assert.Equal("{Binding CanMutateSelection}", (string?)clearSelection.Attribute("IsEnabled"));
    }

    [Fact]
    public void AnalysisResultsPage_UsesCappedNativeImagePreviewAndNonfatalFallback()
    {
        XDocument page = LoadXaml(@"Views\AnalysisResultsPage.xaml");
        XElement preview = page.Descendants(Presentation + "Image")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "SimilarityPreviewImage");

        Assert.Equal("512", (string?)preview.Attribute("MaxWidth"));
        Assert.Equal("512", (string?)preview.Attribute("MaxHeight"));
        Assert.Equal("Uniform", (string?)preview.Attribute("Stretch"));
        Assert.Equal(
            "{Binding SimilarityPreviewVisibility}",
            (string?)preview.Attribute("Visibility"));
        Assert.Contains(
            page.Descendants(Presentation + "TextBlock"),
            text =>
                (string?)text.Attribute("Text") == "{Binding SimilarityPreviewStatusText}" &&
                (string?)text.Attribute("Visibility") == "{Binding SimilarityPreviewStatusVisibility}");
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
    public void AnalysisResultsPage_UsesAccessibleNativeRenameDialog()
    {
        XDocument page = LoadXaml(@"Views\AnalysisResultsPage.xaml");
        XElement dialog = page
            .Descendants(Presentation + "ContentDialog")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "RenameDialog");

        Assert.Equal("Rename file", (string?)dialog.Attribute("Title"));
        Assert.Equal("Rename", (string?)dialog.Attribute("PrimaryButtonText"));
        Assert.Equal("Cancel", (string?)dialog.Attribute("CloseButtonText"));
        Assert.Equal("False", (string?)dialog.Attribute("IsPrimaryButtonEnabled"));

        XElement nameInput = Assert.Single(dialog.Descendants(Presentation + "TextBox"));
        Assert.Equal("RenameNameTextBox", (string?)nameInput.Attribute(Xaml + "Name"));
        Assert.Equal("New file name", (string?)nameInput.Attribute("AutomationProperties.Name"));
        Assert.Equal("RenameNameTextBox_TextChanged", (string?)nameInput.Attribute("TextChanged"));

        string[] accessibleDetails = dialog
            .Descendants(Presentation + "TextBlock")
            .Select(text => (string?)text.Attribute("AutomationProperties.Name"))
            .Where(static name => name is not null)
            .Cast<string>()
            .ToArray();
        Assert.Contains("Current full path", accessibleDetails);
        Assert.Contains("Reasons", accessibleDetails);
        Assert.Contains("Destination preview", accessibleDetails);
        Assert.Contains("Rename validity", accessibleDetails);

        Assert.Contains(
            page.Descendants(Presentation + "AppBarButton"),
            button =>
                (string?)button.Attribute("Label") == "Rename selected" &&
                (string?)button.Attribute("AutomationProperties.Name") == "Rename selected analysis result" &&
                (string?)button.Attribute("IsEnabled") == "{Binding CanRenameSelection}" &&
                (string?)button.Attribute("Click") == "RenameSelected_Click");
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

    [Fact]
    public void ResultsPage_ExposesLinkReplacementOnlyAsAccessibleSecondaryCommands()
    {
        XDocument exact = LoadXaml(@"Views\ResultsPage.xaml");
        XElement commandBar = exact
            .Descendants(Presentation + "CommandBar")
            .Single();
        XElement secondaryCommands = commandBar
            .Elements(Presentation + "CommandBar.SecondaryCommands")
            .Single();
        XElement[] linkCommands = secondaryCommands
            .Elements(Presentation + "AppBarButton")
            .Where(element => ((string?)element.Attribute("Click")) is "ReplaceWithHardLinks_Click" or "ReplaceWithSymbolicLinks_Click")
            .ToArray();

        Assert.Equal(2, linkCommands.Length);
        Assert.Contains(linkCommands, command => (string?)command.Attribute("Label") == "Replace with hard links");
        Assert.Contains(linkCommands, command => (string?)command.Attribute("Label") == "Replace with symbolic links");
        Assert.All(linkCommands, command => Assert.Equal("{Binding CanReplaceWithLinks}", (string?)command.Attribute("IsEnabled")));

        XDocument analysis = LoadXaml(@"Views\AnalysisResultsPage.xaml");
        Assert.DoesNotContain(
            analysis.Descendants(Presentation + "AppBarButton"),
            command => ((string?)command.Attribute("Label"))?.Contains("link", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void ResultsPage_UsesDedicatedAccessibleSurvivorSelectionSeparateFromDeleteSelection()
    {
        XDocument page = LoadXaml(@"Views\ResultsPage.xaml");
        XElement deleteSelection = page
            .Descendants(Presentation + "CheckBox")
            .Single(element => (string?)element.Attribute("IsChecked") == "{Binding IsSelected, Mode=TwoWay}");
        XElement survivorSelection = page
            .Descendants(Presentation + "RadioButton")
            .Single(element => (string?)element.Attribute("IsChecked") == "{Binding IsLinkSurvivor, Mode=TwoWay}");

        Assert.Equal("Select file for deletion", (string?)deleteSelection.Attribute("AutomationProperties.Name"));
        Assert.Equal("Use as link survivor", (string?)survivorSelection.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding CanBeLinkSurvivor}", (string?)survivorSelection.Attribute("IsEnabled"));
    }

    [Fact]
    public void ResultsPage_DisablesSelectionMutationsDuringSharedOperation()
    {
        XDocument page = LoadXaml(@"Views\ResultsPage.xaml");
        const string pageOperationBinding =
            "{Binding ViewModel.CanMutateSelection, ElementName=PageRoot}";

        XElement selectionRule = page
            .Descendants(Presentation + "AppBarButton")
            .Single(element => (string?)element.Attribute("Label") == "Selection rule");
        XElement clearSelection = page
            .Descendants(Presentation + "AppBarButton")
            .Single(element => (string?)element.Attribute("Label") == "Clear selection");
        XElement deleteSelection = page
            .Descendants(Presentation + "CheckBox")
            .Single(element => (string?)element.Attribute("IsChecked") == "{Binding IsSelected, Mode=TwoWay}");
        XElement survivorSelection = page
            .Descendants(Presentation + "RadioButton")
            .Single(element => (string?)element.Attribute("IsChecked") == "{Binding IsLinkSurvivor, Mode=TwoWay}");
        XElement[] deleteFileCommands = page
            .Descendants(Presentation + "MenuFlyoutItem")
            .Where(element => (string?)element.Attribute("Text") == "Delete file")
            .ToArray();

        Assert.Equal("{Binding CanMutateSelection}", (string?)selectionRule.Attribute("IsEnabled"));
        Assert.Equal("{Binding CanMutateSelection}", (string?)clearSelection.Attribute("IsEnabled"));
        Assert.Equal("{Binding CanMutateSelection}", (string?)deleteSelection.Attribute("IsEnabled"));
        Assert.Equal("{Binding CanBeLinkSurvivor}", (string?)survivorSelection.Attribute("IsEnabled"));
        Assert.NotEmpty(deleteFileCommands);
        Assert.All(
            deleteFileCommands,
            command => Assert.Equal(pageOperationBinding, (string?)command.Attribute("IsEnabled")));
    }

    [Fact]
    public void PathScopeEditor_FileFilterDefaultsToWildcardAndNormalizesExtensions()
    {
        Assert.Equal(["*"], PathScopeEditor.ParseFileTypeFilter(null));
        Assert.Equal(["*"], PathScopeEditor.ParseFileTypeFilter("  "));
        Assert.Equal(
            [".jpg", ".jpeg", ".tif", ".tiff"],
            PathScopeEditor.ParseFileTypeFilter("jpg, .JPEG; tif .tiff, .jpg"));
    }

    [Theory]
    [InlineData("C:\\Images\\photo.JPG", ".jpg,.jpeg,.tif,.tiff", true)]
    [InlineData("C:\\Images\\scan.tIfF", ".jpg,.jpeg,.tif,.tiff", true)]
    [InlineData("C:\\Images\\graphic.png", ".jpg,.jpeg,.tif,.tiff", false)]
    [InlineData("C:\\Images\\anything.bin", "*", true)]
    public void PathScopeEditor_FileFilterPredicateIsSharedByPickerAndDrop(
        string path,
        string filter,
        bool expected)
    {
        Assert.Equal(expected, PathScopeEditor.IsFileTypeAllowed(path, filter));
    }

    [Fact]
    public void ExifRemoverPage_UsesNativeResponsiveCleaningControls()
    {
        XDocument page = LoadXaml(@"Views\ExifRemoverPage.xaml");
        XElement scopeEditor = Assert.Single(
            page.Descendants(),
            element => element.Name.LocalName == "PathScopeEditor");
        Assert.Equal(".jpg,.jpeg,.tif,.tiff", (string?)scopeEditor.Attribute("FileTypeFilter"));

        Assert.Single(page.Descendants(Presentation + "Expander"));
        XElement[] toggles = page.Descendants(Presentation + "ToggleSwitch").ToArray();
        Assert.Equal(7, toggles.Length);
        string[] optionBindings =
        [
            "{Binding RemoveGps, Mode=TwoWay}",
            "{Binding RemoveDeviceIdentifiers, Mode=TwoWay}",
            "{Binding RemoveDates, Mode=TwoWay}",
            "{Binding RemoveAuthorAndDescription, Mode=TwoWay}",
            "{Binding RemoveEmbeddedThumbnail, Mode=TwoWay}",
            "{Binding RemoveXmpAndIptc, Mode=TwoWay}",
            "{Binding ReplaceOriginal, Mode=TwoWay}",
        ];
        Assert.Equal(
            optionBindings,
            toggles.Select(toggle => (string?)toggle.Attribute("IsOn")));

        XElement replacementWarning = page
            .Descendants(Presentation + "InfoBar")
            .Single(infoBar => (string?)infoBar.Attribute("Severity") == "Warning");
        Assert.Equal("{Binding ReplaceOriginal}", (string?)replacementWarning.Attribute("IsOpen"));
        Assert.Equal("False", (string?)replacementWarning.Attribute("IsClosable"));

        XElement[] accentButtons = page
            .Descendants(Presentation + "Button")
            .Where(button => (string?)button.Attribute("Style") == "{StaticResource AccentButtonStyle}")
            .ToArray();
        XElement cleanButton = Assert.Single(accentButtons);
        Assert.Equal("C", (string?)cleanButton.Attribute("AccessKey"));
        Assert.Contains(
            cleanButton.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "Clean images");
        XElement cancelButton = page
            .Descendants(Presentation + "Button")
            .Single(button => button.Descendants(Presentation + "TextBlock")
                .Any(text => (string?)text.Attribute("Text") == "Cancel"));
        Assert.Null(cancelButton.Attribute("Style"));

        Assert.Equal(2, page.Descendants(Presentation + "ProgressBar").Count());
        Assert.Equal(
            ["{Binding ProgressValue}", "{Binding CurrentFileProgress}"],
            page.Descendants(Presentation + "ProgressBar")
                .Select(progress => (string?)progress.Attribute("Value")));
        Assert.Single(page.Descendants(Presentation + "ListView"));
        XElement resultTemplate = page
            .Descendants(Presentation + "DataTemplate")
            .Single(template => (string?)template.Attribute(Xaml + "Key") == "ExifResultTemplate");
        foreach (string binding in new[] { "{Binding SourcePath}", "{Binding OutputPath}" })
        {
            XElement path = resultTemplate
                .Descendants(Presentation + "TextBlock")
                .Single(text => (string?)text.Attribute("Text") == binding);
            Assert.Equal("Wrap", (string?)path.Attribute("TextWrapping"));
            Assert.Null(path.Attribute("TextTrimming"));
        }
        Assert.Contains(
            resultTemplate.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding Detail}");
        Assert.Contains(
            resultTemplate.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding RecoveryPathsText}");

        XElement[] resultActions = resultTemplate.Descendants(Presentation + "Button").ToArray();
        Assert.Equal(2, resultActions.Length);
        Assert.Equal(
            ["Open cleaned image", "Reveal cleaned image in Explorer"],
            resultActions.Select(button => (string?)button.Attribute("AutomationProperties.Name")));
        Assert.Equal(
            [
                "{Binding DataContext.OpenOutputCommand, ElementName=PageRoot}",
                "{Binding DataContext.RevealOutputCommand, ElementName=PageRoot}",
            ],
            resultActions.Select(button => (string?)button.Attribute("Command")));

        XElement scrollViewer = Assert.Single(page.Descendants(Presentation + "ScrollViewer"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollMode"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Contains(
            page.Descendants(Presentation + "AdaptiveTrigger"),
            trigger => (string?)trigger.Attribute("MinWindowWidth") == "641");
        Assert.True(
            page.Descendants().Count(element =>
                (string?)element.Attribute("AutomationProperties.LiveSetting") == "Polite") >= 2);
    }

    [Fact]
    public void MainWindow_RoutesExifRemoverDirectlyAndUsesTheTypedPageAsOperationOwner()
    {
        Type? pageType = typeof(MainWindow).Assembly.GetType("Duplicates.Views.ExifRemoverPage");
        Assert.NotNull(pageType);
        MethodInfo? resolver = typeof(MainWindow).GetMethod(
            "ResolveDirectToolPage",
            BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? owner = typeof(MainWindow).GetMethod(
            "IsOwningPage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(resolver);
        Assert.NotNull(owner);

        Assert.Equal(pageType, resolver.Invoke(null, [ToolKind.ExifRemover]));
        Assert.True(Assert.IsType<bool>(owner.Invoke(
            null,
            [AppOperationKind.ExifCleaning, pageType])));
        Assert.False(Assert.IsType<bool>(owner.Invoke(
            null,
            [AppOperationKind.ExifCleaning, typeof(ExifRemoverPage)])));
    }

    [Fact]
    public void VideoOptimizerPage_UsesNativeResponsiveOptimizationControls()
    {
        XDocument page = LoadXaml(@"Views\VideoOptimizerPage.xaml");
        XElement scopeEditor = Assert.Single(
            page.Descendants(),
            element => element.Name.LocalName == "PathScopeEditor");
        Assert.Equal(
            ".mp4,.mkv,.mov,.avi,.wmv,.flv,.webm,.m4v,.mpg,.mpeg,.3gp,.ts",
            (string?)scopeEditor.Attribute("FileTypeFilter"));
        Assert.Equal(
            "{Binding DataContext.CanEditQueue, ElementName=PageRoot}",
            (string?)scopeEditor.Attribute("IsEnabled"));

        XElement preset = Assert.Single(page.Descendants(Presentation + "ComboBox"));
        Assert.Equal("{Binding Preset, Mode=TwoWay}", (string?)preset.Attribute("SelectedIndex"));
        Assert.Equal(
            ["Smaller", "Balanced", "High quality"],
            preset.Elements(Presentation + "ComboBoxItem")
                .Select(item => (string?)item.Attribute("Content")));
        Assert.Equal("{Binding CanEditQueue}", (string?)preset.Attribute("IsEnabled"));

        XElement[] toggles = page.Descendants(Presentation + "ToggleSwitch").ToArray();
        Assert.Equal(2, toggles.Length);
        Assert.Equal(
            [
                "{Binding HardwareAccelerationEnabled, Mode=TwoWay}",
                "{Binding KeepOutputWhenNotSmaller, Mode=TwoWay}",
            ],
            toggles.Select(toggle => (string?)toggle.Attribute("IsOn")));
        Assert.All(
            toggles,
            toggle => Assert.Equal("{Binding CanEditQueue}", (string?)toggle.Attribute("IsEnabled")));
        XElement keepWarning = page
            .Descendants(Presentation + "InfoBar")
            .Single(infoBar => (string?)infoBar.Attribute("Severity") == "Warning");
        Assert.Equal("{Binding KeepOutputWhenNotSmaller}", (string?)keepWarning.Attribute("IsOpen"));
        Assert.Equal("False", (string?)keepWarning.Attribute("IsClosable"));

        XElement optimizeButton = Assert.Single(
            page.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("Style") == "{StaticResource AccentButtonStyle}");
        Assert.Equal("O", (string?)optimizeButton.Attribute("AccessKey"));
        Assert.Contains(
            optimizeButton.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "Optimize videos");
        XElement cancelButton = page
            .Descendants(Presentation + "Button")
            .Single(button => button.Descendants(Presentation + "TextBlock")
                .Any(text => (string?)text.Attribute("Text") == "Cancel"));
        Assert.Null(cancelButton.Attribute("Style"));

        Assert.Equal(
            ["{Binding Progress}", "{Binding ProgressValue}", "{Binding CurrentFileProgress}"],
            page.Descendants(Presentation + "ProgressBar")
                .Select(progress => (string?)progress.Attribute("Value")));
        XElement queue = Assert.Single(page.Descendants(Presentation + "ListView"));
        Assert.Equal("{Binding Queue}", (string?)queue.Attribute("ItemsSource"));
        Assert.Equal("Disabled", (string?)queue.Attribute("ScrollViewer.HorizontalScrollMode"));
        Assert.Equal("Disabled", (string?)queue.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        XElement itemTemplate = page
            .Descendants(Presentation + "DataTemplate")
            .Single(template => (string?)template.Attribute(Xaml + "Key") == "VideoOptimizationQueueTemplate");
        foreach (string binding in new[]
                 {
                     "{Binding SourcePath}",
                     "{Binding DestinationPath}",
                     "{Binding OutputPath}",
                 })
        {
            XElement path = itemTemplate
                .Descendants(Presentation + "TextBlock")
                .Single(text => (string?)text.Attribute("Text") == binding);
            Assert.Equal("Wrap", (string?)path.Attribute("TextWrapping"));
            Assert.Null(path.Attribute("TextTrimming"));
        }

        foreach (string binding in new[]
                 {
                     "{Binding SnapshotText}",
                     "{Binding SourceSummary}",
                     "{Binding TargetSummary}",
                     "{Binding OutputSummary}",
                     "{Binding Detail}",
                     "{Binding RecoveryPathsText}",
                 })
        {
            Assert.Contains(
                itemTemplate.Descendants(Presentation + "TextBlock"),
                text => (string?)text.Attribute("Text") == binding);
        }

        XElement[] outputActions = itemTemplate.Descendants(Presentation + "Button").ToArray();
        Assert.Equal(2, outputActions.Length);
        Assert.Equal(
            ["Open optimized video", "Reveal optimized video in Explorer"],
            outputActions.Select(button => (string?)button.Attribute("AutomationProperties.Name")));
        Assert.Equal(
            [
                "{Binding DataContext.OpenOutputCommand, ElementName=PageRoot}",
                "{Binding DataContext.RevealOutputCommand, ElementName=PageRoot}",
            ],
            outputActions.Select(button => (string?)button.Attribute("Command")));

        XElement scrollViewer = Assert.Single(page.Descendants(Presentation + "ScrollViewer"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollMode"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Contains(
            page.Descendants(Presentation + "AdaptiveTrigger"),
            trigger => (string?)trigger.Attribute("MinWindowWidth") == "641");
    }

    [Fact]
    public void MainWindow_RoutesVideoOptimizerDirectlyAndUsesTheTypedPageAsOperationOwner()
    {
        Type? pageType = typeof(MainWindow).Assembly.GetType("Duplicates.Views.VideoOptimizerPage");
        Assert.NotNull(pageType);
        MethodInfo? resolver = typeof(MainWindow).GetMethod(
            "ResolveDirectToolPage",
            BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? owner = typeof(MainWindow).GetMethod(
            "IsOwningPage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(resolver);
        Assert.NotNull(owner);

        Assert.Equal(pageType, resolver.Invoke(null, [ToolKind.VideoOptimizer]));
        Assert.True(Assert.IsType<bool>(owner.Invoke(
            null,
            [AppOperationKind.VideoOptimization, pageType])));
        Assert.False(Assert.IsType<bool>(owner.Invoke(
            null,
            [AppOperationKind.VideoOptimization, typeof(VideoOptimizerPage)])));
    }

    [Fact]
    public void Manifest_RemainsNonElevatedForSymbolicLinkCreation()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "UiSource", "app.manifest");
        XDocument manifest = XDocument.Load(path);
        XElement requestedExecutionLevel = manifest
            .Descendants()
            .Single(element => element.Name.LocalName == "requestedExecutionLevel");

        Assert.Equal("asInvoker", (string?)requestedExecutionLevel.Attribute("level"));
        Assert.Equal("false", (string?)requestedExecutionLevel.Attribute("uiAccess"));
    }

    [Fact]
    public void MainWindow_InterceptsAppWindowClosingBeforeAwaitingOperationCleanup()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "UiSource", "MainWindow.xaml.cs");
        string source = File.ReadAllText(path);

        Assert.Contains("AppWindow.Closing += AppWindow_Closing", source, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("ConfirmCloseAsync", source, StringComparison.Ordinal);
        Assert.Contains("Close();", source, StringComparison.Ordinal);
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

    private sealed class ExifRemoverPage
    {
    }

    private sealed class VideoOptimizerPage
    {
    }
}
