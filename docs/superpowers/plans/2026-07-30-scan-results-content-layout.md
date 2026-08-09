# Scan and Results Content Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keskitä Scan-sivun rajattu työskentelyalue, esitä valitut kansiot selkeinä kaksirivisinä natiiveina listakohteina ja anna Results-sivun tiedosto- ja polkutiedoille lähes koko käytettävissä oleva leveys ilman tarpeetonta keskeltä lyhentämistä.

**Architecture:** Scan-sivun otsikot säilyvät nykyisellä vasemmalla sisältölinjalla, mutta `Start scan`-, `Add folder`-, tyhjä kansiotila, kansiolista ja `Scan options` jakavat yhden keskitetyn 680 DIP:n työskentelyleveyden. `ScanViewModel` säilyttää valitut kansiot pieninä UI-ViewModel-olioina ja projisoi niiden kanoniset `FullPath`-arvot engine-kirjaston `ScanOptions`-malliin. Results-sivu säilyy koko leveän operatiivisena näkymänä: lista näyttää täyden hakemistopolun enintään kahdella rivillä ja Preview-paneeli näyttää valitun tiedoston koko polun ilman ellipsiä.

**Tech Stack:** .NET SDK 10.0.301, C# 14, `net10.0-windows10.0.22621.0`, WinUI 3, Windows App SDK `1.8.260529003`, CommunityToolkit.Mvvm 8.4.2, xUnit.

## Global Constraints

- Älä muuta `Duplicates.Engine`-projektia tai skannauksen, duplikaattien tunnistuksen, poistovalinnan tai poistoturvan semantiikkaa.
- UI kutsuu engineä edelleen vain `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)` -rajapinnan kautta.
- Scan-sivun työskentelyleveys on yksi `ScanWorkAreaMaxWidth`-token; samaa lukua ei toisteta XAML-elementeissä.
- Scan-sivun pääotsikko ja `Folders`-otsikko säilyvät nykyisellä 32 DIP:n vasemmalla sisältölinjalla.
- Scan-sivun työskentelyalueen tavoiteleveys on 680 DIP leveässä ikkunassa ja käytettävissä oleva leveys sitä pienemmissä ikkunoissa.
- `Start scan` ja `Add folder` kohdistuvat keskitetyn työskentelyalueen oikeaan reunaan.
- `No folders added`, kansiolista ja `Scan options` käyttävät samaa keskilinjaa ja maksimileveyttä.
- Results-sivu käyttää edelleen lähes koko käytettävissä olevaa leveyttä 24 DIP:n ulkomarginaaleilla.
- Results-listan hakemistopolku saa rivittyä enintään kahdelle riville. Täydellinen polku säilyy tooltipissä ja näkyy katkaisemattomana Preview-paneelissa.
- Käytä vain WinUI:n natiiveja kontrolleja ja oletustemplaatteja. Älä lisää omaa `ControlTemplatea` tai korttijäljitelmätyyliä.
- Kaikki värit säilyvät `Duplicates/Themes/Colors.xaml`-tokeneissa; tähän muutokseen ei lisätä värejä.
- UI-tekstit pysyvät englanniksi.
- Säilytä käyttäjän ennestään muuttamat tai uudet tiedostot. Älä koske tehtävän ulkopuolisiin `PROJECT.md`- tai `docs/`-sisältöihin.
- Älä aja `lc`- tai `sc`-wrappereita.
- Käytä TDD:tä ViewModel- ja käyttäytymismuutoksiin.
- Arkkitehtuurin dokumentointi päivitetään samassa muutoksessa, koska `Folders`-kokoelman esitystyyppi ja polun projisointi engine-malliin muuttuvat.

## Authoritative References

- Microsoft ListView and GridView: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/listview-and-gridview>
- Microsoft responsive design techniques: <https://learn.microsoft.com/en-us/windows/apps/design/layout/responsive-design>
- Windows App SDK 1.8 `TextBlock`: <https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textblock?view=windows-app-sdk-1.8>
- Windows App SDK 1.8 `TextBlock.IsTextSelectionEnabled`: <https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textblock.istextselectionenabled?view=windows-app-sdk-1.8>
- Windows App SDK 1.8 `SplitView`: <https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.splitview?view=windows-app-sdk-1.8>

## File Map

- Create `Duplicates/ViewModels/ScanFolderViewModel.cs`: yhden valitun kansion kanoninen polku ja kaksirivisen esityksen tekstit.
- Modify `Duplicates/ViewModels/ScanViewModel.cs`: käytä kansio-ViewModel-kokoelmaa ja välitä engineen vain `FullPath`.
- Modify `Duplicates/Views/ScanPage.xaml`: keskitetty työskentelyleveys, toimintojen kohdistus ja uusi kansiorivi.
- Modify `Duplicates/Themes/Styles.xaml`: lisää vain yhteinen `ScanWorkAreaMaxWidth`-mittatoken.
- Modify `Duplicates/ViewModels/DuplicateFileViewModel.cs`: lopeta hakemistopolun keinotekoinen keskeltä lyhentäminen.
- Modify `Duplicates/Views/ResultsPage.xaml`: kahden rivin listapolku ja kokonainen valittujen tietojen Preview-polku.
- Modify `Duplicates.App.Tests/ScanViewModelTests.cs`: kansioesityksen ja kanonisen polun käyttäytymistestit.
- Modify `Duplicates.App.Tests/ResultsViewModelTests.cs`: täyden hakemistopolun sopimus.
- Modify `Duplicates.App.Tests/NativeWinUiContractTests.cs`: Scan- ja Results-layoutien XAML-sopimukset.
- Modify `AGENTS.md`: dokumentoi Scan-sivun kansioesityksen ja Results-polun uusi vastuunjako.
- Modify `memory/MEMORY.md`: kirjaa sama pysyvä arkkitehtuuripäätös.

---

### Task 1: Canonical Scan Folder Presentation Model

**Files:**
- Create: `Duplicates/ViewModels/ScanFolderViewModel.cs`
- Modify: `Duplicates/ViewModels/ScanViewModel.cs:34,139-159,224-245`
- Modify: `Duplicates.App.Tests/ScanViewModelTests.cs`
- Modify: `AGENTS.md:24-31`
- Modify: `memory/MEMORY.md` section `App boundary`

**Interfaces:**
- Produces: `ScanFolderViewModel(string fullPath)`
- Produces: `string FullPath`, `string DisplayName`, `string ParentPath`
- Changes: `ScanViewModel.Folders` becomes `ObservableCollection<ScanFolderViewModel>`
- Preserves: `ScanOptions.Folders` remains a string collection populated from `Folders.Select(folder => folder.FullPath)`

- [ ] **Step 1: Write the failing folder presentation test**

Add this test to `ScanViewModelTests.cs`:

```csharp
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
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore());

        viewModel.AddFolder(folder);

        ScanFolderViewModel item = Assert.Single(viewModel.Folders);
        Assert.Equal(Path.GetFullPath(folder), item.FullPath);
        Assert.Equal("ObsidianVault", item.DisplayName);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(folder)), item.ParentPath);
        Assert.True(viewModel.HasFolders);
        Assert.Equal(Visibility.Collapsed, viewModel.EmptyFoldersVisibility);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~AddFolderCreatesReadablePresentationWithoutChangingCanonicalPath"
```

Expected: FAIL because `ScanFolderViewModel` does not exist and `Folders` still contains strings.

- [ ] **Step 3: Add the folder presentation model**

Create `Duplicates/ViewModels/ScanFolderViewModel.cs`:

```csharp
namespace Duplicates.ViewModels;

public sealed class ScanFolderViewModel
{
    public ScanFolderViewModel(string fullPath)
    {
        FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        string trimmedPath = Path.TrimEndingDirectorySeparator(FullPath);
        string displayName = Path.GetFileName(trimmedPath);

        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? FullPath
            : displayName;
        ParentPath = string.IsNullOrWhiteSpace(displayName)
            ? "Drive root"
            : Path.GetDirectoryName(trimmedPath) ?? FullPath;
    }

    public string FullPath { get; }

    public string DisplayName { get; }

    public string ParentPath { get; }
}
```

- [ ] **Step 4: Make `ScanViewModel` use the presentation model as its one canonical folder collection**

Apply these exact semantic changes:

```csharp
public ObservableCollection<ScanFolderViewModel> Folders { get; } = [];
```

```csharp
var item = new ScanFolderViewModel(folder);
if (Folders.Any(existing =>
    string.Equals(existing.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
{
    return;
}

Folders.Add(item);
```

```csharp
[RelayCommand]
private void RemoveFolder(ScanFolderViewModel folder)
{
    Folders.Remove(folder);
}
```

Change `BuildScanOptions()` to keep the engine contract unchanged:

```csharp
Folders = Folders.Select(static folder => folder.FullPath).ToArray(),
```

Do not introduce a second string collection or synchronize parallel collections.

- [ ] **Step 5: Add removal and duplicate-path regression coverage**

Add:

```csharp
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
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore());

        viewModel.AddFolder(folder);
        viewModel.AddFolder(folder + Path.DirectorySeparatorChar);

        ScanFolderViewModel item = Assert.Single(viewModel.Folders);
        viewModel.RemoveFolderCommand.Execute(item);

        Assert.Empty(viewModel.Folders);
        Assert.False(viewModel.HasFolders);
        Assert.Equal(Visibility.Visible, viewModel.EmptyFoldersVisibility);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 6: Run Scan ViewModel tests and verify GREEN**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ScanViewModelTests"
```

Expected: all `ScanViewModelTests` pass.

- [ ] **Step 7: Document the changed Scan presentation flow**

Add to `AGENTS.md` under `Arkkitehtuurirajat` and to `memory/MEMORY.md` under `App boundary`:

```markdown
- Scan-sivun valitut kansiot ovat `ScanFolderViewModel`-olioita: `FullPath` on kanoninen engineen välitettävä arvo, kun taas `DisplayName` ja `ParentPath` ovat vain UI-esitystä. Rinnakkaista string-kokoelmaa ei ylläpidetä.
```

- [ ] **Step 8: Commit Task 1**

```powershell
git add Duplicates/ViewModels/ScanFolderViewModel.cs Duplicates/ViewModels/ScanViewModel.cs Duplicates.App.Tests/ScanViewModelTests.cs AGENTS.md memory/MEMORY.md
git commit -m "Paranna valittujen kansioiden esitysmallia"
```

---

### Task 2: Centered Scan Work Area and Structured Folder Rows

**Files:**
- Modify: `Duplicates/Themes/Styles.xaml:5-14`
- Modify: `Duplicates/Views/ScanPage.xaml:39-180`
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs:278-291`

**Interfaces:**
- Consumes: `ScanFolderViewModel.DisplayName`, `ParentPath`, and `FullPath`
- Produces: `ScanWorkAreaMaxWidth` resource with value `680`
- Produces named XAML elements: `HeaderActionRail`, `FoldersActionRail`, `FoldersList`, `EmptyFoldersState`, `ScanOptions`

- [ ] **Step 1: Replace the obsolete width-binding contract with a failing centered-layout contract**

Replace `ScanPage_OptionsUseTheAvailableSectionWidth` with:

```csharp
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
```

- [ ] **Step 2: Add a failing native folder-row contract**

Add:

```csharp
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
```

- [ ] **Step 3: Run the two focused contract tests and verify RED**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ScanPage_CentersActionsFoldersAndOptionsOnOneWorkArea|FullyQualifiedName~ScanPage_FolderRowsExposeNameParentPathAndNearbyRemoveAction"
```

Expected: both tests fail because the resource and named layout rails do not exist and the current template binds directly to a string.

- [ ] **Step 4: Add the shared Scan width token**

Add to `Styles.xaml` next to `ContentMaxWidth`:

```xml
<x:Double x:Key="ScanWorkAreaMaxWidth">680</x:Double>
```

Do not repeat `680` in `ScanPage.xaml`.

- [ ] **Step 5: Align the two action buttons to the centered work-area right edge**

Keep the title and section-description `StackPanel` elements on the existing left content line. Place each action in an overlay rail spanning its containing grid:

```xml
<Grid
    x:Name="HeaderActionRail"
    Grid.ColumnSpan="2"
    MaxWidth="{StaticResource ScanWorkAreaMaxWidth}"
    HorizontalAlignment="Stretch">
    <Button
        HorizontalAlignment="Right"
        VerticalAlignment="Center"
        AccessKey="S"
        Command="{Binding StartScanCommand}"
        Style="{StaticResource AccentButtonStyle}"
        Visibility="{Binding SetupVisibility}">
        <StackPanel Orientation="Horizontal" Spacing="{StaticResource SpacingSmall}">
            <SymbolIcon Symbol="Play" />
            <TextBlock Text="Start scan" />
        </StackPanel>
    </Button>
</Grid>
```

Use the same structure and `x:Name="FoldersActionRail"` for `Add folder`. The surrounding header grids remain full width so their left-aligned copy does not move.

- [ ] **Step 6: Center the empty state, folder list, and Scan options**

Apply these shared attributes:

```xml
MaxWidth="{StaticResource ScanWorkAreaMaxWidth}"
HorizontalAlignment="Stretch"
```

Add `x:Name="EmptyFoldersState"` to the empty-state `StackPanel`, and add `x:Name="ScanOptions"` to the existing `Scan options` `Expander`.

Remove:

```xml
Width="{Binding ActualWidth, ElementName=FoldersRegion}"
```

Keep `FoldersRegion` full width and as the drop target. Only its visible empty/list contents are centered.

- [ ] **Step 7: Replace the orphan path row with a native two-line ListView item**

Keep the existing `ListView`; add a `ListViewItem` style that only requests stretch and does not replace the native template:

```xml
<ListView.ItemContainerStyle>
    <Style TargetType="ListViewItem">
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
    </Style>
</ListView.ItemContainerStyle>
```

Use this item structure:

```xml
<DataTemplate>
    <Grid
        Padding="8,6"
        ColumnSpacing="{StaticResource SpacingMedium}"
        ToolTipService.ToolTip="{Binding FullPath}">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <FontIcon
            VerticalAlignment="Center"
            FontSize="20"
            Foreground="{ThemeResource TextPrimaryBrush}"
            Glyph="&#xE8B7;" />
        <StackPanel
            Grid.Column="1"
            VerticalAlignment="Center"
            Spacing="2">
            <TextBlock
                Style="{ThemeResource BodyStrongTextBlockStyle}"
                Foreground="{ThemeResource TextPrimaryBrush}"
                Text="{Binding DisplayName}" />
            <TextBlock
                Style="{StaticResource PathTextBlockStyle}"
                Text="{Binding ParentPath}" />
        </StackPanel>
        <Button
            Grid.Column="2"
            VerticalAlignment="Center"
            AutomationProperties.Name="Remove folder"
            Command="{Binding ViewModel.RemoveFolderCommand, ElementName=PageRoot}"
            CommandParameter="{Binding}"
            ToolTipService.ToolTip="Remove folder">
            <SymbolIcon Symbol="Cancel" />
        </Button>
    </Grid>
</DataTemplate>
```

- [ ] **Step 8: Run Scan layout contracts and app build**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~NativeWinUiContractTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: all native UI contracts pass and the app builds.

- [ ] **Step 9: Commit Task 2**

```powershell
git add Duplicates/Themes/Styles.xaml Duplicates/Views/ScanPage.xaml Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Keskitä Scan-sivun työskentelyalue"
```

---

### Task 3: Full Results Paths with Dense List Rows and Complete Preview

**Files:**
- Modify: `Duplicates/ViewModels/DuplicateFileViewModel.cs:21-39,90-104`
- Modify: `Duplicates/Views/ResultsPage.xaml:207-260,322-354`
- Modify: `Duplicates.App.Tests/ResultsViewModelTests.cs:280-300`
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `AGENTS.md:33-43`
- Modify: `memory/MEMORY.md` section `2026-07-30 - Native WinUI 3 interaction architecture`

**Interfaces:**
- Preserves: `DuplicateFileViewModel.DisplayDirectoryPath`
- Changes: `DisplayDirectoryPath` returns the complete `DirectoryPath`; it no longer inserts `...`
- Preserves: `ResultsSplitView` and the existing preview toggle behavior
- Produces: result-row path with `TextWrapping="Wrap"` and `MaxLines="2"`
- Produces: Preview path with `TextWrapping="Wrap"`, `TextTrimming="None"`, and `IsTextSelectionEnabled="True"`

- [ ] **Step 1: Replace the path-shortening test with a failing full-path contract**

Replace `DuplicateFileDisplayDirectoryPathShortensLongPathsFromTheMiddle` with:

```csharp
[Fact]
public void DuplicateFileDisplayDirectoryPathPreservesTheCompleteDirectory()
{
    var group = new DuplicateGroupViewModel(
        NewGroup(
            1,
            "a-very-long-folder-name-with-many-segments\\another-long-folder-name\\nested\\deeper\\deepest",
            "long-file-name.txt",
            "copy.txt"));
    DuplicateFileViewModel file = group.Files[0];

    Assert.Equal(file.DirectoryPath, file.DisplayDirectoryPath);
    Assert.DoesNotContain("...", file.DisplayDirectoryPath, StringComparison.Ordinal);
}
```

Reuse the existing `NewGroup` helper; do not create a duplicate fixture helper.

- [ ] **Step 2: Run the focused Results ViewModel test and verify RED**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~DuplicateFileDisplayDirectoryPathPreservesTheCompleteDirectory"
```

Expected: FAIL because `DisplayDirectoryPath` currently calls `ShortenMiddle`.

- [ ] **Step 3: Remove artificial path shortening and dead code**

Change:

```csharp
public string DisplayDirectoryPath => DirectoryPath;
```

Delete `ShortenMiddle` completely. Do not leave the unused constants or helper behind.

- [ ] **Step 4: Add failing XAML contracts for full-width results and path presentation**

Add to `NativeWinUiContractTests.cs`:

```csharp
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
```

- [ ] **Step 5: Run the focused XAML contract and verify RED**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ResultsPage_UsesFullWidthAndKeepsCompletePathAvailable"
```

Expected: FAIL because row and preview wrapping attributes are missing.

- [ ] **Step 6: Make result rows dense but stop pre-shortening their path**

Keep the existing flexible `Grid.Column="1"` file-information column and add these local overrides to its directory-path `TextBlock`:

```xml
<TextBlock
    Style="{StaticResource PathTextBlockStyle}"
    Foreground="{ThemeResource TextSecondaryBrush}"
    MaxLines="2"
    Text="{Binding DisplayDirectoryPath}"
    TextWrapping="Wrap" />
```

Do not move size/date/status controls into the flexible path column. The existing auto-width KEEP/DELETE and actions columns remain on the right.

- [ ] **Step 7: Show the complete selected path in Preview**

Change the Preview path block to:

```xml
<TextBlock
    Style="{StaticResource PathTextBlockStyle}"
    IsTextSelectionEnabled="True"
    Text="{Binding PreviewPath}"
    TextTrimming="None"
    TextWrapping="Wrap" />
```

The inherited monospace font and theme color remain. Row context-menu `Copy path` remains the primary explicit copy command; text selection is an additional native capability.

- [ ] **Step 8: Preserve the existing responsive SplitView behavior**

Do not widen the Preview pane in this task. Preserve:

- small: `Overlay`, 320 DIP;
- medium: `CompactOverlay`, 320 DIP;
- large: `Inline`, 360 DIP;
- closed Preview: result list receives the full content width;
- selected row: existing ViewModel behavior may open Preview.

This avoids reducing list width further while solving the actual information-loss issue.

- [ ] **Step 9: Run Results tests and app build**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ResultsViewModelTests|FullyQualifiedName~NativeWinUiContractTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: all Results ViewModel and native XAML contract tests pass; app build passes.

- [ ] **Step 10: Document the Results path contract**

Add to `AGENTS.md` and `memory/MEMORY.md`:

```markdown
- Results-lista ei lyhennä hakemistopolkuja ViewModelissa. Lista saa rivittää polun enintään kahdelle riville, tooltip säilyttää koko tiedostopolun ja Preview näyttää valitun tiedoston koko polun ilman ellipsiä.
```

- [ ] **Step 11: Commit Task 3**

```powershell
git add Duplicates/ViewModels/DuplicateFileViewModel.cs Duplicates/Views/ResultsPage.xaml Duplicates.App.Tests/ResultsViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs AGENTS.md memory/MEMORY.md
git commit -m "Näytä tulosten polkutiedot kokonaisina"
```

---

### Task 4: Integrated Verification and Visual Acceptance

**Files:**
- Verify only: all files modified in Tasks 1-3

**Interfaces:**
- Consumes: completed Scan and Results layout changes
- Produces: automated and visual evidence that the requested alignment and path behavior hold

- [ ] **Step 1: Run the complete app ViewModel and XAML contract suite**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: all tests pass.

- [ ] **Step 2: Run the engine regression suite**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
```

Expected: all engine tests pass, proving that the UI presentation change did not alter scanning behavior.

- [ ] **Step 3: Build the app**

```powershell
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: build succeeds with no new warnings.

- [ ] **Step 4: Verify formatting**

```powershell
dotnet format Duplicates.slnx --verify-no-changes
```

Expected: exit code 0. If the known toolchain blocks formatting, record the exact error without modifying unrelated files.

- [ ] **Step 5: Perform Scan visual acceptance at 150% Windows scaling**

Launch the packaged Debug x64 app and verify:

1. Empty state:
   - `Scan options` is approximately 680 DIP wide and centered on the same vertical axis as the folder icon and `No folders added`.
   - `Start scan` and `Add folder` right edges align with the right edge of that centered work area.
   - `Scan for duplicates` and `Folders` remain on the original left content line.
2. One folder:
   - row shows folder icon, `ObsidianVault`, parent path, and a nearby remove action within the same 680 DIP row;
   - no lone full-path string spans the full page;
   - `Scan options` does not widen after adding a folder.
3. Multiple folders:
   - rows share the same width and alignment;
   - list remains vertically scrollable after reaching `FolderListMaxHeight`;
   - remove action removes only the selected row.

- [ ] **Step 6: Perform Results visual acceptance**

Use a scan fixture containing a deeply nested directory and verify:

1. With Preview closed, ResultsList uses the full content width except for the existing 24 DIP page margins.
2. File name and path receive the flexible column; KEEP/DELETE and row action remain visible at the right.
3. Long directory path wraps to at most two lines and no manually inserted middle `...` appears.
4. Hover tooltip exposes the complete `FullPath`.
5. Selecting the row opens Preview and the complete path wraps without ellipsis.
6. The Preview path can be selected with the native TextBlock behavior, and the existing `Copy path` menu command still copies the exact full path.

- [ ] **Step 7: Verify responsive widths**

Resize through the existing effective-width states:

- below 641 epx: no horizontal clipping; Results Preview overlays;
- 641-1007 epx: Scan work area shrinks to available width; Results Preview uses compact overlay;
- 1008 epx and above: Scan work area caps at 680 DIP; Results Preview is inline and list uses the remaining width.

Also verify that 200% text scaling does not overlap the two action rails with the left-aligned header text. If overlap occurs, move the action below the copy in the existing Small visual state rather than shrinking text or hardcoding a narrower font.

- [ ] **Step 8: Review the final diff for scope and duplication**

Run:

```powershell
git diff --check
git diff -- Duplicates/ViewModels/ScanFolderViewModel.cs Duplicates/ViewModels/ScanViewModel.cs Duplicates/Views/ScanPage.xaml Duplicates/Themes/Styles.xaml Duplicates/ViewModels/DuplicateFileViewModel.cs Duplicates/Views/ResultsPage.xaml Duplicates.App.Tests/ScanViewModelTests.cs Duplicates.App.Tests/ResultsViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs AGENTS.md memory/MEMORY.md
```

Confirm:

- one `ScanWorkAreaMaxWidth` definition;
- no repeated `680` layout literals;
- no parallel folder-path collection;
- no remaining `ShortenMiddle` helper or callers;
- no custom `ControlTemplate`;
- no inline color literals;
- no unrelated file changes.

- [ ] **Step 9: Commit verification-only adjustments if required**

Only if Steps 5-8 required scoped corrections:

```powershell
git add Duplicates Duplicates.App.Tests AGENTS.md memory/MEMORY.md
git commit -m "Viimeistele Scan- ja Results-näkymien mitoitus"
```

Do not create an empty commit.

## Acceptance Criteria

- Scan-sivun `Scan options` remains 680 DIP or narrower and is centered relative to the empty-state icon and folder list.
- `Start scan` and `Add folder` no longer sit at the far page edge; their right edges align with the centered Scan work area.
- Adding folders does not widen `Scan options`.
- A selected folder is presented as a grouped native list row with folder icon, strong folder name, parent path, and same-row remove action.
- Scan engine receives exactly the same normalized full folder paths as before.
- Results content keeps its current near-full-width 24 DIP page margins.
- Results ViewModel no longer inserts `...` into directory paths.
- Results list may limit the path to two visual lines, while tooltip and Preview preserve the exact complete path.
- Preview path wraps, does not trim, and supports native text selection.
- Existing result selection, preview toggle, delete selection, delete guard and file actions remain unchanged.
- All focused tests, complete app tests, engine tests, app build and formatting gate pass or an exact pre-existing toolchain blocker is reported.
- No `lc` or `sc` wrapper is run.
