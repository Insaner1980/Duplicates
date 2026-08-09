# Native WinUI 3 UI Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Muunna koko Duplicates-sovelluksen käyttöliittymä käyttämään WinUI 3:n omia kontrolleja, oletustemplaatteja, vuorovaikutusmalleja, saavutettavuustukea ja responsiivista käyttäytymistä säilyttäen nykyinen viiden värin paletti täsmälleen ennallaan.

**Architecture:** Sovellus pysyy nykyisenä Windows App SDK / WinUI 3 -sovelluksena ja nykyiset engine-, palvelu- ja ViewModel-vastuut säilyvät. Migraatio poistaa kontrollikohtaiset ulkoasujäljitelmät, palauttaa WinUI:n omat templaatit ja visual state -käyttäytymisen, käyttää kuhunkin tehtävään tarkoitettua WinUI-kontrollia sekä muuttaa Results-näkymän käsin rakennetut rivit ja komentopalkin natiiviksi valinta- ja komentokokemukseksi. Väripaletti jää yhdeksi lähteeksi, mutta hover-, pressed-, disabled-, focus-, high-contrast- ja keyboard-käyttäytyminen annetaan jälleen WinUI:lle.

**Tech Stack:** .NET SDK 10.0.301, C# 14, `net10.0-windows10.0.22621.0`, WinUI 3, Windows App SDK 1.8.9 (`1.8.260529003`), CommunityToolkit.Mvvm 8.4.2, CommunityToolkit WinUI SettingsControls 8.2.251219, xUnit 2.9.3.

## Global Constraints

- Säilytä nämä viisi väriarvoa muuttamattomina: `#000000`, `#181818`, `#242424`, `#D72323`, `#F5EDED`.
- Älä lisää uutta RGB- tai hex-väriä. Natiivien tilojen läpinäkyvyys saa käyttää samaa palettiväriä eri `Opacity`-arvolla.
- `#D72323` pysyy sovelluksen kiinteänä accent- ja danger-värinä.
- Säilytä sekä light-, dark- että system-teema ja nykyiset Mica-, Mica Alt-, Acrylic- ja Solid-asetukset.
- Mica ja Acrylic sävyttävät taustaa Windowsin toimesta; paletin täsmällinen pikseliväri taataan vain solid-pinnoille ja varsinaisille paletteista johdetuille kontrolleille.
- Älä muuta skannaus-, duplikaattivalinta-, poistoturva-, tiedostotoiminto- tai asetusten tallennussemantiikkaa.
- Älä muuta `Duplicates.Engine`-projektia; UI käyttää engineä edelleen vain `DuplicateScanner.ScanAsync(...)`-rajapinnan kautta.
- Säilytä poistoturvan invariantti: yhdestä duplikaattiryhmästä ei saa valita kaikkia tiedostoja poistoon.
- Käytä WinUI:n oletustemplaatteja. Älä lisää omaa `ControlTemplate`a millekään WinUI-kontrollille.
- Käytä kontrollikohtaista tyyliä vain, jos se perustuu WinUI:n omaan oletustyyliin tai valmiiseen `AccentButtonStyle`en.
- Settings-sivu saa käyttää Microsoftin dokumentaatiossa suositeltuja Community Toolkitin `SettingsCard`- ja `SettingsExpander`-kontrolleja.
- UI-tekstit pysyvät englanniksi.
- Välttämättömät koodikommentit ovat lyhyitä ja projektiohjeen mukaisia.
- Käytä TDD:tä kaikkiin ViewModel- ja käyttäytymismuutoksiin.
- Älä aja `lc`- tai `sc`-wrappereita; käyttäjä ajaa ne pyytäessään.
- Säilytä työpuun ennestään olemassa olevat muutokset. Toteutus aloitetaan eristetyssä worktreessä vasta, kun nykyinen likainen työpuu on checkpointattu käyttäjän valitsemalla tavalla.
- Windows App SDK -version päivitys ei kuulu tähän muutokseen. Vaikka 1.8-sarjan uusin vakaa korjausjulkaisu on `1.8.260710003`, projektin lukittu `1.8.260529003` säilyy.

## Authoritative Design References

- Windows App SDK 1.8 release notes: <https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8>
- WinUI controls overview: <https://learn.microsoft.com/en-us/windows/apps/design/controls/>
- XAML styles and default styles: <https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/xaml-styles>
- TitleBar and NavigationView integration: <https://learn.microsoft.com/en-us/windows/apps/design/controls/title-bar>
- NavigationView: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview>
- Buttons: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons>
- NumberBox: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/number-box>
- ListView and GridView: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/listview-and-gridview>
- CommandBar: <https://learn.microsoft.com/en-us/windows/apps/design/controls/command-bar>
- Commands and shared command surfaces: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/commanding>
- ContentDialog: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs>
- Responsive breakpoints: <https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design>
- Accessibility overview and checklist: <https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview> and <https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist>
- Keyboard interactions: <https://learn.microsoft.com/en-us/windows/apps/design/input/keyboard-interactions>
- Settings guidance: <https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings>

## Current-State Findings

1. Projekti on jo aidosti WinUI 3: `Duplicates.csproj` sisältää `UseWinUI=true`, viittaa Windows App SDK:hon ja `App.xaml` lataa `XamlControlsResources`-resurssit.
2. `MainWindow` käyttää jo oikeaa WinUI `TitleBar` + `NavigationView` -integraatiota. Tätä ei korvata omalla shellillä.
3. Nykyinen `Colors.xaml` ylikirjoittaa suuren joukon kontrollien sisäisiä resursseja, esimerkiksi kaikki Button-, CheckBox- ja ToggleSwitch-tilat. Tilat käyttävät usein samaa väriä, joten WinUI:n oma hover/pressed/disabled-hierarkia litistyy.
4. `Styles.xaml` määrittelee itse Primary-, Secondary-, Danger-, WhiteOutline- ja RemoveFolder-painiketyylit sekä korttimaisen `Border`-tyylin. Kontrollit ovat WinUI-kontrolleja, mutta niiden oletusulkoasua ei tällä hetkellä käytetä.
5. Scan-sivun `Folders`-, `Scan options`- ja progress-alueet ovat itse koottuja Border-kortteja. Tavukentät ovat tavallisia `TextBox`-kontrolleja, vaikka WinUI tarjoaa numeeriseen syötteeseen `NumberBox`in.
6. Results-sivun yläkomennot ovat käsin koottu Button-rivi. Tiedostorivit ovat `Border` + `Tapped` -elementtejä, joten rivi ei itsessään saa `ListViewItem`in natiivia valinta-, focus-, nuolinäppäin- tai UI Automation -käyttäytymistä.
7. Results-sivun tiedostotoiminnot ovat viisi vierekkäistä ikonipainiketta. Natiivi Windows-komentomalli on CommandBar/CommandBarFlyout/MenuFlyout ja jaetut komennot.
8. Settings-sivu käyttää jo Microsoftin suosittelemaa SettingsCard/SettingsExpander-rakennetta. Sen pääasiallinen migraatiotarve on omien painiketyylien poistaminen ja numeerisen syötteen vaihtaminen NumberBoxiin.
9. Poistovahvistus käyttää jo oikeaa `ContentDialog`ia ja turvallista Close-oletuspainiketta. Tätä semantiikkaa ei korvata.
10. Root-elementin `MinWidth=960` estää sovellusta saavuttamasta Microsoftin small-breakpointia. Results- ja Scan-layoutit eivät vielä vaihda rakennetta standardien 640/1008 epx rajojen mukaan.

## Target File Structure

### Created files

- `Duplicates.App.Tests/NativeWinUiContractTests.cs` — staattiset XAML-sopimustestit paletille, oletustyyleille, kontrollivalinnoille ja saavutettavuusmerkinnöille.
- `Duplicates/ViewModels/ByteSizeInput.cs` — yksi yhteinen muunnos NumberBoxin `double`-arvojen ja domainin `long`-tavumäärien välillä.

### Modified files

- `Duplicates.App.Tests/Duplicates.App.Tests.csproj` — kopioi tuotannon XAML-tiedostot testitulokseen sopimustestejä varten.
- `Duplicates.App.Tests/ScanViewModelTests.cs` — NumberBox-arvojen, presetien ja rajojen testit.
- `Duplicates.App.Tests/SettingsViewModelTests.cs` — oletuskoon numeerisen tallennuksen testit.
- `Duplicates.App.Tests/ResultsViewModelTests.cs` — natiivin ListView-valinnan ja preview-tilan käyttäytymissopimus.
- `Duplicates/App.xaml` — säilyttää `XamlControlsResources` ensimmäisenä ja lataa vain paletti- ja kevyet layout-resurssit.
- `Duplicates/Themes/Colors.xaml` — lukittu paletti sekä mahdollisimman suppea semanttinen WinUI-värikytkentä.
- `Duplicates/Themes/Styles.xaml` — layout- ja tekstiresurssit; ei omia kontrollitemplaatteja tai painikejäljitelmiä.
- `Duplicates/MainWindow.xaml` — natiivi shell, breakpoint-kelpoinen minimikoko ja semanttiset automation landmarkit.
- `Duplicates/MainWindow.xaml.cs` — shellin focus- ja elinkaarikäyttäytyminen, ei muutosta navigaatiovastuuseen.
- `Duplicates/ViewModels/ScanViewModel.cs` — numeeriset min/max-arvot Text-arvojen sijaan.
- `Duplicates/Views/ScanPage.xaml` — WinUI:n omat Button-, ListView-, ComboBox-, NumberBox-, Expander-, InfoBar- ja ProgressBar-kokemukset.
- `Duplicates/Views/ScanPage.xaml.cs` — drag/drop säilyy ja kohdistuu natiiviin layout-elementtiin; focus palautetaan lisäyksen jälkeen.
- `Duplicates/ViewModels/SettingsViewModel.cs` — numeerinen oletuskoko ja yhteinen ByteSizeInput-muunnos.
- `Duplicates/Views/SettingsPage.xaml` — suositeltu SettingsCard/SettingsExpander + WinUI NumberBox/default Button.
- `Duplicates/ViewModels/ResultsViewModel.cs` — valittu ListView-rivi on previewn yksi lähde; nykyinen kanoninen ryhmälista säilyy.
- `Duplicates/Views/ResultsPage.xaml` — CommandBar, grouped ListView, SplitView-preview, MenuFlyout/CommandBarFlyout ja responsiiviset visual statet.
- `Duplicates/Views/ResultsPage.xaml.cs` — poistaa Border.Tapped-käsittelyn, säilyttää picker-, clipboard-, launcher- ja ContentDialog-siltakoodin.
- `AGENTS.md` — dokumentoi natiivien kontrollien/oletustemplaattien rajan ja lukitun paletin.
- `memory/MEMORY.md` — dokumentoi uusi UI-rakenne ja Results-valinnan data flow projektin arkkitehtuurimuutoksena.

---

### Task 1: Add an executable native-UI contract harness

**Files:**
- Create: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates.App.Tests/Duplicates.App.Tests.csproj`

**Interfaces:**
- Consumes: tuotannon `Duplicates/**/*.xaml`-tiedostot.
- Produces: `NativeWinUiContractTests.LoadXaml(string relativePath)` ja `AllProductionXaml()` myöhempien tehtävien regressiotesteille.

- [ ] **Step 1: Copy production XAML into the test output**

Lisää testiprojektin loppuun:

```xml
<ItemGroup>
  <None
    Include="..\Duplicates\**\*.xaml"
    Link="UiSource\%(RecursiveDir)%(Filename)%(Extension)"
    CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Add the XAML loader and locked-palette test**

```csharp
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

    private static XDocument LoadXaml(string relativePath)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "UiSource", relativePath);
        return XDocument.Load(path, LoadOptions.SetLineInfo);
    }

    private static IEnumerable<(string Path, XDocument Document)> AllProductionXaml()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "UiSource");
        return Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(root, path), XDocument.Load(path, LoadOptions.SetLineInfo)));
    }
}
```

- [ ] **Step 3: Run the contract test**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~NativeWinUiContractTests.Colors_KeepTheLockedFiveColorPalette
```

Expected: PASS and exactly five palette colors.

- [ ] **Step 4: Commit the harness**

```powershell
git add Duplicates.App.Tests/Duplicates.App.Tests.csproj Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "testit: lisää natiivin WinUI-käyttöliittymän sopimustestit"
```

---

### Task 2: Restore WinUI default templates while preserving the palette

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates/Themes/Colors.xaml`
- Modify: `Duplicates/Themes/Styles.xaml`
- Modify: `Duplicates/App.xaml`
- Modify: `Duplicates/Views/ScanPage.xaml`
- Modify: `Duplicates/Views/ResultsPage.xaml`
- Modify: `Duplicates/Views/SettingsPage.xaml`

**Interfaces:**
- Consumes: lukitut `Palette*`-värit ja WinUI:n `XamlControlsResources`.
- Produces: `AccentRedBrush`, `DangerBrush`, `OnAccentBrush`, appin pinta- ja tekstibrushit sekä WinUI:n oma `AccentButtonStyle`.

- [ ] **Step 1: Add failing tests for legacy visual emulation**

Lisää testiluokkaan:

```csharp
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

    Assert.Empty(forbidden.Where(keys.Contains));
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

    Assert.DoesNotContain(keys, key =>
        key.StartsWith("Button", StringComparison.Ordinal) ||
        key.StartsWith("CheckBox", StringComparison.Ordinal) ||
        key.StartsWith("ToggleSwitch", StringComparison.Ordinal) ||
        key.StartsWith("RadioButton", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Verify the tests fail against the current styles**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~NativeWinUiContractTests
```

Expected: FAIL listing the five legacy button styles and per-control state resources.

- [ ] **Step 3: Reduce Colors.xaml to palette and semantic resources**

Pidä viisi `Color`-resurssia täsmälleen ennallaan. Säilytä sovelluskohtaiset brushit:

```xml
<SolidColorBrush x:Key="AccentRedBrush" Color="{StaticResource PaletteAccentRed}" />
<SolidColorBrush x:Key="DangerBrush" Color="{StaticResource PaletteAccentRed}" />
<SolidColorBrush x:Key="OnAccentBrush" Color="{StaticResource PaletteWarmWhite}" />
```

Kytke accent WinUI:n kevyisiin semanttisiin resursseihin samalla RGB-arvolla ja natiivien tilojen opacity-hierarkialla:

```xml
<SolidColorBrush x:Key="AccentFillColorDefaultBrush" Color="{StaticResource PaletteAccentRed}" />
<SolidColorBrush x:Key="AccentFillColorSecondaryBrush" Color="{StaticResource PaletteAccentRed}" Opacity="0.90" />
<SolidColorBrush x:Key="AccentFillColorTertiaryBrush" Color="{StaticResource PaletteAccentRed}" Opacity="0.80" />
<SolidColorBrush x:Key="AccentFillColorDisabledBrush" Color="{StaticResource PaletteAccentRed}" Opacity="0.40" />
<SolidColorBrush x:Key="TextOnAccentFillColorPrimaryBrush" Color="{StaticResource PaletteWarmWhite}" />
<SolidColorBrush x:Key="TextOnAccentFillColorSecondaryBrush" Color="{StaticResource PaletteWarmWhite}" Opacity="0.70" />
```

Poista kaikki `Button*`, `CheckBox*`, `ToggleSwitch*`, `RadioButton*`, `ComboBox*`, `TextControl*`, `Focus*` ja `Progress*`-kontrollikohtaiset override-resurssit. Säilytä `ThemeDictionaries`-osassa nykyiset appin pinta- ja tekstibrushit, mutta anna high-contrast-tilassa WinUI:n järjestelmäresurssien voittaa jättämällä `HighContrast`-sanakirja määrittelemättä.

- [ ] **Step 4: Move every page back to WinUI button styles**

Päivitä kaikki kolme page-XAML-tiedostoa samassa atomisessa muutoksessa:

- `PrimaryButtonStyle` → `{StaticResource AccentButtonStyle}`;
- `DangerButtonStyle` → `{StaticResource AccentButtonStyle}` ja säilytä danger-semanttiikka tekstissä/ikonissa;
- poista `Style` kokonaan Secondary-, WhiteOutline- ja RemoveFolder-painikkeista.

Älä vielä poista `CardBorderStyle`ä, koska Scan- ja Results-rakenteet puretaan Tasks 5, 7 ja 8 aikana. Näin jokainen välivaihe rakentuu eikä yksikään StaticResource-viittaus jää rikkinäiseksi.

- [ ] **Step 5: Remove the five legacy button styles**

Poista `Styles.xaml`-tiedostosta testissä kielletyt viisi painiketyyliä. Säilytä tässä vaiheessa `CardBorderStyle`, spacing-, max-width-, padding- ja tekstityylit. Älä lisää `Template`-setteriä.

- [ ] **Step 6: Keep XamlControlsResources first**

Varmista `App.xaml`-järjestys:

```xml
<XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
<ResourceDictionary Source="Themes/Colors.xaml" />
<ResourceDictionary Source="Themes/Styles.xaml" />
```

- [ ] **Step 7: Run tests and build**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~NativeWinUiContractTests
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: contract tests PASS; XAML resources resolve; app build PASS.

- [ ] **Step 8: Commit the native theme baseline**

```powershell
git add Duplicates/App.xaml Duplicates/Themes/Colors.xaml Duplicates/Themes/Styles.xaml Duplicates/Views/ScanPage.xaml Duplicates/Views/ResultsPage.xaml Duplicates/Views/SettingsPage.xaml Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "refaktorointi: palauta WinUI-kontrollien oletustemplaatit"
```

---

### Task 3: Normalize the native shell and responsive window contract

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates/MainWindow.xaml`
- Modify: `Duplicates/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: nykyiset `ShowScanPage()` ja `ShowResultsPage()` -navigointipisteet.
- Produces: yksi WinUI `TitleBar`, yksi `NavigationView`, yksi `Frame` ja vähintään 640 epx leveydessä toimiva shell.

- [ ] **Step 1: Add the shell structure contract**

```csharp
[Fact]
public void MainWindow_UsesOneNativeTitleBarNavigationViewAndFrame()
{
    XDocument main = LoadXaml("MainWindow.xaml");

    Assert.Single(main.Descendants(Presentation + "TitleBar"));
    Assert.Single(main.Descendants(Presentation + "NavigationView"));
    Assert.Single(main.Descendants(Presentation + "Frame"));

    XElement root = main.Descendants(Presentation + "Grid")
        .Single(element => (string?)element.Attribute(Xaml + "Name") == "Root");
    Assert.Equal("640", (string?)root.Attribute("MinWidth"));
}
```

- [ ] **Step 2: Verify the minimum-width assertion fails**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~MainWindow_UsesOneNativeTitleBarNavigationViewAndFrame
```

Expected: FAIL because current `MinWidth` is `960`.

- [ ] **Step 3: Keep the documented TitleBar/NavigationView integration**

Säilytä `TitleBar` omassa ensimmäisessä Grid-rivissään, `NavigationView` toisessa rivissä, `IsPaneToggleButtonVisible="False"` NavigationViewssa ja `IsPaneToggleButtonVisible="True"` TitleBarissa. Muuta root:

```xml
<Grid
    x:Name="Root"
    MinWidth="640"
    MinHeight="480"
    Background="Transparent">
```

Pidä `NavigationView`n pane-taustat paletin semanttisissa resursseissa. Sivujen solid-pinnat käyttävät edelleen `BgBaseBrush`ia. Tämä tekee backdrop-asetuksesta aidosti näkyvän shellin läpinäkyvissä osissa muuttamatta varsinaisten sisältöpintojen palettia.

- [ ] **Step 4: Add semantic landmarks**

Lisää:

```xml
AutomationProperties.LandmarkType="Navigation"
AutomationProperties.Name="Main navigation"
```

`NavigationView`lle ja:

```xml
AutomationProperties.LandmarkType="Main"
```

`RootFrame`lle.

- [ ] **Step 5: Keep native window activation and detach event handlers**

Lisää `MainWindow`-konstruktoriin `Closed += MainWindow_Closed;` ja toteuta:

```csharp
private void MainWindow_Closed(object sender, WindowEventArgs args)
{
    _services.SettingsService.SettingsChanged -= SettingsChanged;
    _services.ResultsStore.ResultChanged -= ResultsChanged;
}
```

Älä muuta `ExtendsContentIntoTitleBar`, `SetTitleBar(AppTitleBar)`, `AppWindow.SetIcon`, NavigationView-valintaa tai navigointimetodeja.

- [ ] **Step 6: Verify the shell**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~MainWindow_UsesOneNativeTitleBarNavigationViewAndFrame
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add Duplicates/MainWindow.xaml Duplicates/MainWindow.xaml.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "refaktorointi: yhdenmukaista natiivi WinUI-sovelluskehys"
```

---

### Task 4: Replace text-based byte inputs with one shared NumberBox model

**Files:**
- Create: `Duplicates/ViewModels/ByteSizeInput.cs`
- Modify: `Duplicates/ViewModels/ScanViewModel.cs`
- Modify: `Duplicates/Views/ScanPage.xaml`
- Modify: `Duplicates.App.Tests/ScanViewModelTests.cs`

**Interfaces:**
- Produces: `ByteSizeInput.NoMaximum`, `ByteSizeInput.FromBytes(long)`, `ByteSizeInput.ToBytes(double, long, bool)`.
- Produces: `ScanViewModel.MinSizeValue` and `ScanViewModel.MaxSizeValue`.
- Consumes: domainin `long`-tavumäärät ja NumberBoxin `double`/`NaN`-arvot.

- [ ] **Step 1: Add failing conversion and preset tests**

Lisää `ScanViewModelTests.cs`-tiedostoon:

```csharp
[Fact]
public void SizePresets_SetNumberBoxValuesAndNoMaximum()
{
    var viewModel = new ScanViewModel(
        new DuplicateScanner(),
        new FakeSettingsService(),
        new ResultsStore());

    viewModel.UseOneKilobyteMinimumCommand.Execute(null);
    Assert.Equal(1024d, viewModel.MinSizeValue);

    viewModel.UseOneMegabyteMinimumCommand.Execute(null);
    Assert.Equal(1_048_576d, viewModel.MinSizeValue);

    viewModel.UseAnySizeCommand.Execute(null);
    Assert.Equal(0d, viewModel.MinSizeValue);
    Assert.True(double.IsNaN(viewModel.MaxSizeValue));
}
```

- [ ] **Step 2: Verify compile failure**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~SizePresets
```

Expected: FAIL because the numeric properties do not exist.

- [ ] **Step 3: Add the single conversion source**

```csharp
namespace Duplicates.ViewModels;

public static class ByteSizeInput
{
    public static double NoMaximum => double.NaN;

    public static double FromBytes(long value)
    {
        return value == long.MaxValue ? NoMaximum : value;
    }

    public static long ToBytes(double value, long fallback, bool noValueMeansMaximum = false)
    {
        if (double.IsNaN(value))
        {
            return noValueMeansMaximum ? long.MaxValue : fallback;
        }

        if (double.IsInfinity(value))
        {
            return fallback;
        }

        if (value >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return checked((long)Math.Max(0d, Math.Truncate(value)));
    }
}
```

NumberBox käyttää `double`a. Dokumentoi testillä, että käyttöliittymä hyväksyy vain kokonaiset ei-negatiiviset tavumäärät; engine saa edelleen `long`in eikä engine-rajapinta muutu.

- [ ] **Step 4: Convert ScanViewModel**

Korvaa `MinSizeText` ja `MaxSizeText`:

```csharp
[ObservableProperty]
public partial double MinSizeValue { get; set; } = 1d;

[ObservableProperty]
public partial double MaxSizeValue { get; set; } = ByteSizeInput.NoMaximum;
```

Presetit asettavat `0d`, `1024d`, `1_048_576d` ja `ByteSizeInput.NoMaximum`. `BuildScanOptions()` käyttää:

```csharp
long minSize = ByteSizeInput.ToBytes(
    MinSizeValue,
    _settingsService.Current.DefaultMinSizeBytes);
long maxSize = ByteSizeInput.ToBytes(
    MaxSizeValue,
    long.MaxValue,
    noValueMeansMaximum: true);
```

- [ ] **Step 5: Bind ScanPage to native NumberBoxes in the same change**

Korvaa min/max TextBoxit:

```xml
<NumberBox
    Header="Minimum size, bytes"
    Minimum="0"
    SpinButtonPlacementMode="Compact"
    ValidationMode="InvalidInputOverwritten"
    Value="{Binding MinSizeValue, Mode=TwoWay}" />
<NumberBox
    Header="Maximum size, bytes"
    Minimum="0"
    PlaceholderText="No cap"
    SpinButtonPlacementMode="Compact"
    ValidationMode="InvalidInputOverwritten"
    Value="{Binding MaxSizeValue, Mode=TwoWay}" />
```

- [ ] **Step 6: Run focused ViewModel tests and build**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~ScanViewModelTests
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS; no references to `MinSizeText` or `MaxSizeText`.

- [ ] **Step 7: Verify all callers**

Run:

```powershell
rg -n "MinSizeText|MaxSizeText|ParseSize" Duplicates Duplicates.App.Tests
```

Expected: no matches.

- [ ] **Step 8: Commit the ViewModel behavior**

```powershell
git add Duplicates/ViewModels/ByteSizeInput.cs Duplicates/ViewModels/ScanViewModel.cs Duplicates/Views/ScanPage.xaml Duplicates.App.Tests/ScanViewModelTests.cs
git commit -m "refaktorointi: käytä yhteistä numeerista tavusyötettä"
```

---

### Task 5: Rebuild ScanPage from native workflow controls

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates/Views/ScanPage.xaml`
- Modify: `Duplicates/Views/ScanPage.xaml.cs`

**Interfaces:**
- Consumes: nykyinen `ScanViewModel`, `FolderPicker`, drag/drop ja `StartScanCommand`.
- Produces: native ListView folder list, NumberBox size inputs, standard Expander, InfoBar, ProgressBar, default Buttons and responsive layout.

- [ ] **Step 1: Add ScanPage control-choice contracts**

```csharp
[Fact]
public void ScanPage_UsesNativeControlsWithoutLegacyCards()
{
    XDocument page = LoadXaml(@"Views\ScanPage.xaml");

    Assert.Equal(2, page.Descendants(Presentation + "NumberBox").Count());
    Assert.NotEmpty(page.Descendants(Presentation + "ListView"));
    Assert.NotEmpty(page.Descendants(Presentation + "Expander"));
    Assert.NotEmpty(page.Descendants(Presentation + "InfoBar"));
    Assert.NotEmpty(page.Descendants(Presentation + "ProgressBar"));
    Assert.DoesNotContain(page.Descendants(), element =>
        (string?)element.Attribute("Style") == "{StaticResource CardBorderStyle}");
}

[Fact]
public void ScanPage_UsesWinUiAccentStyleOnlyForPrimaryActions()
{
    XDocument page = LoadXaml(@"Views\ScanPage.xaml");
    XElement[] accentButtons = page.Descendants(Presentation + "Button")
        .Where(element =>
            (string?)element.Attribute("Style") == "{StaticResource AccentButtonStyle}")
        .ToArray();

    Assert.Equal(2, accentButtons.Length);
}
```

- [ ] **Step 2: Verify the tests fail**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ScanPage_"
```

Expected: FAIL because CardBorderStyle is still used and the native section structure is not complete.

- [ ] **Step 3: Replace the page-header primary action**

Use the native accent style:

```xml
<Button
    AccessKey="S"
    Command="{Binding StartScanCommand}"
    Style="{StaticResource AccentButtonStyle}"
    Visibility="{Binding SetupVisibility}">
    <StackPanel Orientation="Horizontal" Spacing="8">
        <SymbolIcon Symbol="Play" />
        <TextBlock Text="Start scan" />
    </StackPanel>
</Button>
```

Do the same for `Add folder` with `AccessKey="A"`. Do not set local `Background`, `BorderBrush`, `CornerRadius`, `MinHeight` or `Padding`.

- [ ] **Step 4: Replace the Folders card with a semantic section and native ListView**

Use a named `Grid` as the drop target, a section heading, the accent Add button, and:

```xml
<ListView
    x:Name="FoldersList"
    ItemsSource="{Binding Folders}"
    SelectionMode="None"
    AutomationProperties.Name="Folders to scan">
```

Use the default Button for Remove, preserve `AutomationProperties.Name` and tooltip, and remove `RemoveFolderButtonStyle`. The empty state remains ordinary TextBlocks/FontIcon inside the Grid; it must not pretend to be a control.

- [ ] **Step 5: Replace the scan-options card**

Use a standard expanded `Expander Header="Scan options" IsExpanded="True"` and keep existing CheckBox, ComboBox and advanced-option bindings. Säilytä Task 4:ssä lisätyt kaksi NumberBoxia:

```xml
<NumberBox
    Header="Minimum size, bytes"
    Minimum="0"
    SpinButtonPlacementMode="Compact"
    ValidationMode="InvalidInputOverwritten"
    Value="{Binding MinSizeValue, Mode=TwoWay}" />

<NumberBox
    Header="Maximum size, bytes"
    Minimum="0"
    PlaceholderText="No cap"
    SpinButtonPlacementMode="Compact"
    ValidationMode="InvalidInputOverwritten"
    Value="{Binding MaxSizeValue, Mode=TwoWay}" />
```

Preset-painikkeet ovat tavallisia default `Button`eita. Poista `WhiteOutlineButtonStyle`.

- [ ] **Step 6: Replace the progress card**

Käytä section-heading + `ProgressBar` + nykyiset viisi mittaria + default `Cancel` Button. Älä ympäröi aluetta CardBorderStylellä. Lisää progress-alueelle:

```xml
AutomationProperties.LiveSetting="Polite"
```

ja status-InfoBarille `AutomationProperties.LiveSetting="Polite"`. Nykyinen ViewModel ei erottele status-severityä, joten tähän migraatioon ei lisätä uutta virhetilamallia.

- [ ] **Step 7: Add responsive states**

Nimeä kaksipalstainen options-grid `OptionsGrid` ja sen sarakkeet `OptionsLeftColumn`/`OptionsRightColumn`. Lisää page rootiin VisualStateGroup, joka käyttää Microsoftin breakpointteja:

```xml
<VisualState x:Name="Medium">
    <VisualState.StateTriggers>
        <AdaptiveTrigger MinWindowWidth="641" />
    </VisualState.StateTriggers>
</VisualState>
<VisualState x:Name="Large">
    <VisualState.StateTriggers>
        <AdaptiveTrigger MinWindowWidth="1008" />
    </VisualState.StateTriggers>
    <VisualState.Setters>
        <Setter Target="OptionsRightColumn.Width" Value="*" />
        <Setter Target="SizeInputsGrid.(Grid.Column)" Value="1" />
    </VisualState.Setters>
</VisualState>
```

Small/Medium-tilassa sarakkeet pinotaan yhteen sarakkeeseen ja komentopainikkeet saavat wrapata. Large-tilassa nykyinen kaksipalstainen rakenne palautuu. Älä lukitse yksittäistä kontrollia 640 epx:ää leveämmäksi.

- [ ] **Step 8: Retarget drag/drop and focus**

Nimeä uusi Grid `FoldersRegion`, siirrä `AllowDrop`, `DragOver` ja `Drop` siihen. Kun picker tai drop lisää vähintään yhden kansion, kutsu:

```csharp
FoldersList.Focus(FocusState.Programmatic);
```

Älä muuta `ViewModel.AddFolder`-semantiikkaa.

- [ ] **Step 9: Run contracts, app tests and build**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ScanPage_|FullyQualifiedName~ScanViewModelTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS.

- [ ] **Step 10: Commit**

```powershell
git add Duplicates/Views/ScanPage.xaml Duplicates/Views/ScanPage.xaml.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "refaktorointi: rakenna skannausnäkymä natiiveista WinUI-kontrolleista"
```

---

### Task 6: Finish the Microsoft-recommended Settings experience

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates.App.Tests/SettingsViewModelTests.cs`
- Modify: `Duplicates/ViewModels/SettingsViewModel.cs`
- Modify: `Duplicates/Views/SettingsPage.xaml`

**Interfaces:**
- Consumes: `ByteSizeInput` and existing settings collections.
- Produces: SettingsCard/SettingsExpander-based settings page with native NumberBox, ComboBox, ToggleSwitch, InfoBar and default buttons.

- [ ] **Step 1: Add failing SettingsViewModel numeric tests**

Lisää `SettingsViewModelTests.cs`-tiedostoon testi, joka asettaa `DefaultMinSizeValue = 2048d`, odottaa fake-palvelun `Current.DefaultMinSizeBytes == 2048`, sekä testi, jossa negatiivinen arvo normalisoituu nollaksi.

- [ ] **Step 2: Add a settings control contract**

```csharp
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
```

- [ ] **Step 3: Verify failure**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~SettingsPage_
```

Expected: FAIL because `DefaultMinSizeValue` does not exist and default minimum size is a TextBox.

- [ ] **Step 4: Convert SettingsViewModel**

Korvaa `DefaultMinSizeText`:

```csharp
[ObservableProperty]
public partial double DefaultMinSizeValue { get; set; } = 1d;
```

`LoadFromSettings` käyttää `ByteSizeInput.FromBytes(settings.DefaultMinSizeBytes)`. `SaveAsync` käyttää:

```csharp
long minSize = ByteSizeInput.ToBytes(
    DefaultMinSizeValue,
    _settingsService.Current.DefaultMinSizeBytes);
```

Nimeä partial change handler `OnDefaultMinSizeValueChanged`.

- [ ] **Step 5: Replace the default size control**

```xml
<NumberBox
    MinWidth="220"
    Minimum="0"
    SpinButtonPlacementMode="Compact"
    ValidationMode="InvalidInputOverwritten"
    Value="{Binding DefaultMinSizeValue, Mode=TwoWay}" />
```

Poista kolmesta preset-painikkeesta `SecondaryButtonStyle`; ne käyttävät WinUI:n oletuspainiketta.

- [ ] **Step 6: Preserve the recommended settings hierarchy**

Pidä `Appearance`, `Scanning defaults` ja `Deletion` omina SettingsExpandereina. Pidä jokainen asetus omana SettingsCardina ja käytä binary-asetuksiin ToggleSwitchiä, yksittäisiin valintoihin ComboBoxia sekä permanent delete -varoitukseen InfoBaria. Älä siirrä Scan-sivun usein käytettäviä workflow-komentoja Settings-sivulle.

- [ ] **Step 7: Verify settings**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~SettingsPage_|FullyQualifiedName~SettingsViewModelTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS.

- [ ] **Step 8: Verify no legacy binding remains**

Run:

```powershell
rg -n "DefaultMinSizeText" Duplicates Duplicates.App.Tests
```

Expected: no matches.

- [ ] **Step 9: Commit**

```powershell
git add Duplicates/ViewModels/SettingsViewModel.cs Duplicates/Views/SettingsPage.xaml Duplicates.App.Tests/SettingsViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "refaktorointi: viimeistele asetukset WinUI-suositusten mukaisiksi"
```

---

### Task 7: Make Results selection a real grouped ListView experience

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates.App.Tests/ResultsViewModelTests.cs`
- Modify: `Duplicates/ViewModels/ResultsViewModel.cs`
- Modify: `Duplicates/Views/ResultsPage.xaml`
- Modify: `Duplicates/Views/ResultsPage.xaml.cs`

**Interfaces:**
- Consumes: canonical `ResultsViewModel.Groups`, each `DuplicateGroupViewModel.Files`, and `ResultsViewModel.SelectedFile`.
- Produces: grouped `CollectionViewSource`, native `ListView` single-selection for preview, checkbox-based independent delete selection and built-in keyboard/UI Automation behavior.

- [ ] **Step 1: Add the Results XAML contract**

```csharp
[Fact]
public void ResultsPage_UsesGroupedListViewInsteadOfTappedBorders()
{
    XDocument page = LoadXaml(@"Views\ResultsPage.xaml");

    XElement results = page.Descendants(Presentation + "ListView")
        .Single(element => (string?)element.Attribute(Xaml + "Name") == "ResultsList");

    Assert.Equal("Single", (string?)results.Attribute("SelectionMode"));
    Assert.Equal(
        "{Binding SelectedFile, Mode=TwoWay}",
        (string?)results.Attribute("SelectedItem"));
    Assert.DoesNotContain(page.Descendants(Presentation + "Border"), element =>
        element.Attribute("Tapped") is not null);
}
```

- [ ] **Step 2: Add the preview-selection behavior test**

Lisää `ResultsViewModelTests.cs`-tiedostoon:

```csharp
[Fact]
public void SelectingResultRow_OpensPreviewWithoutChangingDeleteSelection()
{
    var store = new ResultsStore();
    var viewModel = NewViewModel(store);
    store.SetResult(NewResult(NewGroup(1, "photos", "one.png", "two.png")));
    DuplicateFileViewModel file = viewModel.Groups[0].Files[0];
    bool deleteSelectionBefore = file.IsSelected;

    viewModel.SelectedFile = file;

    Assert.True(viewModel.IsPreviewPaneOpen);
    Assert.Same(file, viewModel.SelectedFile);
    Assert.Equal(deleteSelectionBefore, file.IsSelected);
}
```

- [ ] **Step 3: Verify the XAML contract fails and behavior passes**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ResultsPage_|FullyQualifiedName~SelectingResultRow"
```

Expected: XAML contract FAIL because current row uses `Border.Tapped`; ViewModel behavior PASS and locks the existing delete-selection separation.

- [ ] **Step 4: Define a grouped collection source**

Lisää ResultsPage resources:

```xml
<Page.Resources>
    <CollectionViewSource
        x:Name="GroupedResults"
        IsSourceGrouped="True"
        ItemsPath="Files"
        Source="{Binding Groups}" />
</Page.Resources>
```

Käytä yhtä `ListView`ta:

```xml
<ListView
    x:Name="ResultsList"
    ItemsSource="{Binding Source={StaticResource GroupedResults}}"
    SelectedItem="{Binding SelectedFile, Mode=TwoWay}"
    SelectionMode="Single"
    IsItemClickEnabled="False"
    AutomationProperties.Name="Duplicate files">
```

Määritä `GroupStyle.HeaderTemplate`, joka näyttää nykyiset `DisplayName`, `FilesSummaryText` ja `SelectedSummaryText` -arvot. File ItemTemplate näyttää checkboxin, tiedostonimen, hakemiston, metadatan ja Keep/Delete-tilan. Poista ulompi ryhmä-ListView, sisempi ItemsControl ja `FileRow_Tapped`.

- [ ] **Step 5: Preserve canonical data semantics**

Älä sido ListViewn `SelectedItems`-kokoelmaa poistovalintaan. `SelectedItem` ohjaa vain previewtä. Poistovalinta jatkaa `DuplicateFileViewModel.IsSelected` + CheckBox -sidonnan kautta, jotta kanoninen `_allGroups` ja delete guard säilyvät.

- [ ] **Step 6: Remove the code-behind tap handler**

Poista kokonaan:

```csharp
private void FileRow_Tapped(...)
```

Älä korvaa sitä pointer/tap-handlerilla. Native ListView hoitaa hiiren, kosketuksen, Space-valinnan, nuolinäppäimet, focus-visualin ja automation peerin.

- [ ] **Step 7: Run behavior and build verification**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ResultsPage_|FullyQualifiedName~ResultsViewModelTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS; grouped ListView renders; delete-selection tests remain green.

- [ ] **Step 8: Commit**

```powershell
git add Duplicates/Views/ResultsPage.xaml Duplicates/Views/ResultsPage.xaml.cs Duplicates/ViewModels/ResultsViewModel.cs Duplicates.App.Tests/ResultsViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "refaktorointi: käytä tuloksissa natiivia ryhmiteltyä ListViewta"
```

---

### Task 8: Replace handcrafted Results commands and preview layout

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates/Themes/Styles.xaml`
- Modify: `Duplicates/Views/ResultsPage.xaml`
- Modify: `Duplicates/Views/ResultsPage.xaml.cs`
- Modify: `Duplicates/ViewModels/ResultsViewModel.cs`

**Interfaces:**
- Consumes: existing selection commands, `Delete_Click`, file actions and `IsPreviewPaneOpen`.
- Produces: CommandBar/AppBarButton command surface, MenuFlyout row actions and SplitView preview.

- [ ] **Step 1: Add command-surface and preview contracts**

```csharp
[Fact]
public void ResultsPage_UsesNativeCommandBarAndSplitView()
{
    XDocument page = LoadXaml(@"Views\ResultsPage.xaml");

    Assert.Single(page.Descendants(Presentation + "CommandBar"));
    Assert.NotEmpty(page.Descendants(Presentation + "AppBarButton"));
    Assert.Single(page.Descendants(Presentation + "SplitView"));
    Assert.NotEmpty(page.Descendants(Presentation + "MenuFlyout"));

    XDocument styles = LoadXaml(@"Themes\Styles.xaml");
    Assert.DoesNotContain(styles.Descendants().Attributes(Xaml + "Key"),
        attribute => attribute.Value == "CardBorderStyle");
}
```

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter FullyQualifiedName~ResultsPage_UsesNativeCommandBarAndSplitView
```

Expected: FAIL because the current toolbar and preview are Grid/StackPanel/Border composites.

- [ ] **Step 3: Build the page CommandBar**

Korvaa käsin koottu selection/action Button-rivi:

```xml
<CommandBar
    x:Name="ResultsCommandBar"
    DefaultLabelPosition="Right"
    IsDynamicOverflowEnabled="True">
    <AppBarButton Icon="Filter" Label="Selection rule">
        <AppBarButton.Flyout>
            <MenuFlyout>
                <MenuFlyoutItem Text="Keep newest" Command="{Binding AutoSelectKeepNewestCommand}" />
                <MenuFlyoutItem Text="Keep oldest" Command="{Binding AutoSelectKeepOldestCommand}" />
                <MenuFlyoutItem Text="Keep shortest path" Command="{Binding AutoSelectKeepShortestPathCommand}" />
                <MenuFlyoutItem Text="Keep preferred folder" Click="KeepPreferredFolder_Click" />
            </MenuFlyout>
        </AppBarButton.Flyout>
    </AppBarButton>
    <AppBarButton Icon="Clear" Label="Clear selection" Command="{Binding ClearSelectionCommand}" />
    <AppBarButton Icon="Undo" Label="Undo" Command="{Binding UndoSelectionCommand}" />
    <AppBarToggleButton
        Icon="View"
        Label="Preview"
        IsChecked="{Binding IsPreviewPaneOpen, Mode=TwoWay}" />
    <AppBarSeparator />
    <AppBarButton
        Icon="Delete"
        Label="{Binding DeleteButtonText}"
        Foreground="{ThemeResource DangerBrush}"
        IsEnabled="{Binding CanDelete}"
        Click="Delete_Click" />
</CommandBar>
```

Jos `Symbol.Clear` ei ole käytettävissä lukitussa SDK:ssa, käytä `FontIcon`ia Segoe Fluent Icons -glyphistä, joka vastaa WinUI Galleryn Clear-komentoa. Varmista buildillä symbolin saatavuus ennen commitia.

- [ ] **Step 4: Use the right search control**

Korvaa hakukentän TextBox `AutoSuggestBox`illa:

```xml
<AutoSuggestBox
    x:Name="SearchBox"
    Header="Search"
    PlaceholderText="Filename or path"
    QueryIcon="Find"
    Text="{Binding SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
```

Älä lisää tekaistuja ehdotuksia. Kontrollia käytetään sen natiivin search entry-, clear- ja keyboard-käyttäytymisen vuoksi; nykyinen välitön suodatus säilyy.

- [ ] **Step 5: Move row commands to a native overflow menu**

Korvaa viisi vierekkäistä ikonipainiketta yhdellä default More-painikkeella ja MenuFlyoutilla:

```xml
<Button
    AutomationProperties.Name="File actions"
    ToolTipService.ToolTip="File actions">
    <SymbolIcon Symbol="More" />
    <Button.Flyout>
        <MenuFlyout>
            <MenuFlyoutItem Text="Open" Click="OpenFile_Click" />
            <MenuFlyoutItem Text="Reveal in folder" Click="RevealFile_Click" />
            <MenuFlyoutItem Text="Copy path" Click="CopyPath_Click" />
            <MenuFlyoutSeparator />
            <MenuFlyoutItem Text="Exclude from group"
                Command="{Binding ViewModel.ExcludeFileCommand, ElementName=PageRoot}"
                CommandParameter="{Binding}" />
            <MenuFlyoutItem Text="Delete file"
                Foreground="{ThemeResource DangerBrush}"
                Click="DeleteFile_Click" />
        </MenuFlyout>
    </Button.Flyout>
</Button>
```

Kiinnitä sama MenuFlyout myös `ListViewItem.ContextFlyout`iksi, jotta hiiren oikea painike avaa saman komentopinnan eikä syntyisi kahta komentototeutusta.

- [ ] **Step 6: Replace the preview Border with SplitView**

Käytä:

```xml
<SplitView
    x:Name="ResultsSplitView"
    DisplayMode="Inline"
    IsPaneOpen="{Binding IsPreviewPaneOpen, Mode=TwoWay}"
    OpenPaneLength="360"
    PanePlacement="Right">
```

`SplitView.Pane` sisältää nykyisen preview-sisällön default Buttonilla sulkemista varten. `SplitView.Content` sisältää grouped ResultsListin. Poista `CardBorderStyle`.

- [ ] **Step 7: Remove the remaining card emulation**

Korvaa “Run a scan to see results” ja “No duplicates found” -CardBorderStyle-pinnat tavallisilla semanttisilla StackPanel-tyhjätiloilla. Säilytä “New scan” `AccentButtonStyle`lla. Kun Scan- ja Results-sivuilla ei ole enää yhtään `CardBorderStyle`-viittausta, poista tyyli `Styles.xaml`-tiedostosta.

Run:

```powershell
rg -n "CardBorderStyle" Duplicates -g "*.xaml"
```

Expected: no matches.

- [ ] **Step 8: Add responsive preview behavior**

Käytä VisualStateja:

- Small `0–640`: `DisplayMode="Overlay"`, `OpenPaneLength="320"`, CommandBar overflowaa automaattisesti.
- Medium `641–1007`: `DisplayMode="CompactOverlay"`, preview aukeaa komennosta.
- Large `1008+`: `DisplayMode="Inline"`, `OpenPaneLength="360"`.

Kun käyttäjä valitsee rivin Small/Medium-tilassa, paneeli saa avautua overlayna. Esc sulkee natiivin overlay-paneelin. Large-tilassa preview ei peitä listaa.

- [ ] **Step 9: Preserve native ContentDialog deletion safety**

Pidä nykyinen `ContentDialog`, `XamlRoot`, `PrimaryButtonText="Delete"`, `CloseButtonText="Cancel"` ja `DefaultButton=Close`. Älä vaihda MessageDialogiin tai omaan dialogi-ikkunaan. Varmista, ettei useita dialogeja voi avata, koska `CanDelete`/`IsDeleting` estää toistuvan komennon.

- [ ] **Step 10: Verify**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ResultsPage_|FullyQualifiedName~ResultsViewModelTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS.

- [ ] **Step 11: Commit**

```powershell
git add Duplicates/Themes/Styles.xaml Duplicates/Views/ResultsPage.xaml Duplicates/Views/ResultsPage.xaml.cs Duplicates/ViewModels/ResultsViewModel.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "refaktorointi: siirrä tuloskomennot natiiviin WinUI-komentopalkkiin"
```

---

### Task 9: Complete native keyboard, focus and UI Automation behavior

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Modify: `Duplicates/MainWindow.xaml`
- Modify: `Duplicates/Views/ScanPage.xaml`
- Modify: `Duplicates/Views/ScanPage.xaml.cs`
- Modify: `Duplicates/Views/ResultsPage.xaml`
- Modify: `Duplicates/Views/ResultsPage.xaml.cs`
- Modify: `Duplicates/Views/SettingsPage.xaml`

**Interfaces:**
- Produces: complete Tab/arrow/Enter/Space/Escape path, access keys, accelerators, visible default focus rings and screen-reader names.

- [ ] **Step 1: Add a source-level accessibility contract**

```csharp
[Fact]
public void InteractiveImagesAndIconOnlyButtonsHaveAccessibleNames()
{
    foreach ((string path, XDocument document) in AllProductionXaml())
    {
        foreach (XElement button in document.Descendants(Presentation + "Button"))
        {
            bool hasText = button.Descendants(Presentation + "TextBlock")
                .Any(text => text.Attribute("Text") is not null);
            bool hasContent = button.Attribute("Content") is not null;
            bool hasAccessibleName = button.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name");

            Assert.True(hasText || hasContent || hasAccessibleName, path);
        }
    }
}

[Fact]
public void ProductionXaml_DoesNotHandleTapOnLayoutElements()
{
    foreach ((string path, XDocument document) in AllProductionXaml())
    {
        Assert.DoesNotContain(document.Descendants(), element =>
            (element.Name.LocalName is "Border" or "Grid" or "StackPanel") &&
            element.Attribute("Tapped") is not null);
    }
}
```

Pidä assertion-viestissä tiedostopolku ja elementin source line, jotta puuttuva nimi löytyy suoraan.

- [ ] **Step 2: Add access keys**

Lisää näkyville, keskeisille komennoille:

- Scan: `Alt+A` Add folder, `Alt+S` Start scan.
- Results: `Alt+R` Selection rule, `Alt+D` Delete, `Alt+P` Preview.
- Dialogin natiivit painikkeet säilyvät ContentDialogin keyboard-käyttäytymisessä.

Varmista, ettei sama access key toistu yhtä aikaa näkyvällä sivulla.

- [ ] **Step 3: Add keyboard accelerators**

Lisää Results-sivulle:

```xml
<Page.KeyboardAccelerators>
    <KeyboardAccelerator Key="F" Modifiers="Control" Invoked="FocusSearch_Invoked" />
    <KeyboardAccelerator Key="Z" Modifiers="Control" Invoked="UndoSelection_Invoked" />
</Page.KeyboardAccelerators>
```

Toteuta:

```csharp
private void FocusSearch_Invoked(
    KeyboardAccelerator sender,
    KeyboardAcceleratorInvokedEventArgs args)
{
    args.Handled = SearchBox.Focus(FocusState.Keyboard);
}

private void UndoSelection_Invoked(
    KeyboardAccelerator sender,
    KeyboardAcceleratorInvokedEventArgs args)
{
    if (ViewModel.UndoSelectionCommand.CanExecute(null))
    {
        ViewModel.UndoSelectionCommand.Execute(null);
        args.Handled = true;
    }
}
```

- [ ] **Step 4: Verify logical focus order**

Pidä XAML-elementtien järjestys samana kuin visuaalinen järjestys. Älä lisää `TabIndex`-arvoja, ellei Accessibility Insights osoita poikkeamaa. Default-kontrollit ovat tab stoppeja, layout-elementit eivät ole.

- [ ] **Step 5: Add screen-reader semantics**

- Lisää icon-only Buttoneille eksplisiittinen `AutomationProperties.Name`.
- Lisää dynaamisille scan/delete-statuksille `AutomationProperties.LiveSetting="Polite"`.
- Lisää virhe-InfoBarille nimi, joka sisältää virheen luonteen mutta ei toista pitkää failure-listaa.
- Merkitse page-title TextBlockit `AutomationProperties.HeadingLevel="Level1"` ja section-headingit `Level2`.
- Pidä tooltip täydentävänä; älä käytä tooltippiä ainoana nimenä.

- [ ] **Step 6: Preserve default focus visuals**

Varmista lähdehaulla, ettei XAML tai Colors.xaml ylikirjoita:

```text
FocusVisualPrimaryBrush
FocusVisualSecondaryBrush
SystemControlFocusVisualPrimaryBrush
FocusStrokeColorOuterBrush
```

Expected: no matches. WinUI piirtää keyboard focus -renkaat itse ja high contrast voi vaihtaa ne.

- [ ] **Step 7: Run automated verification**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS.

- [ ] **Step 8: Perform manual keyboard and Narrator acceptance**

Packaged Debug -sovelluksessa:

1. Tab kulkee shellistä sivun tärkeimpiin komentoihin loogisessa järjestyksessä.
2. NavigationView toimii nuolinäppäimillä ja Enterillä.
3. Scan-kansiolistassa Remove toimii Tab + Enter/Space.
4. ResultsList toimii nuolinäppäimillä; preview-rivin valinta ei muuta delete-checkboxia.
5. Context menu aukeaa Shift+F10:llä.
6. Ctrl+F kohdistaa haun, Ctrl+Z suorittaa valinnan undon vain kun komento on käytettävissä.
7. Esc sulkee overlay-previewn ja ContentDialogin.
8. Narrator ilmoittaa page headingit, kenttien labelit, icon-only-painikkeet, valintatilat ja progress-päivitykset.

- [ ] **Step 9: Commit**

```powershell
git add Duplicates/MainWindow.xaml Duplicates/Views/ScanPage.xaml Duplicates/Views/ScanPage.xaml.cs Duplicates/Views/ResultsPage.xaml Duplicates/Views/ResultsPage.xaml.cs Duplicates/Views/SettingsPage.xaml Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "saavutettavuus: viimeistele natiivi näppäimistö- ja automaatiotuki"
```

---

### Task 10: Verify visual states, scaling, themes and native interaction states

**Files:**
- Modify when a confirmed defect is found: `Duplicates/Themes/Colors.xaml`
- Modify when a confirmed defect is found: `Duplicates/Themes/Styles.xaml`
- Modify when a confirmed defect is found: affected page XAML

**Interfaces:**
- Consumes: completed UI.
- Produces: recorded acceptance evidence; no speculative visual changes.

- [ ] **Step 1: Run the complete automated gate**

Run in this order:

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
dotnet format Duplicates.slnx --verify-no-changes
```

Expected: all tests/build PASS and format reports no changes. If restore inputs changed unexpectedly, stop and run `dotnet restore Duplicates.slnx` only after confirming no package version was changed.

- [ ] **Step 2: Run a literal/token audit**

Run:

```powershell
rg -n "#[0-9A-Fa-f]{6,8}|PrimaryButtonStyle|SecondaryButtonStyle|DangerButtonStyle|WhiteOutlineButtonStyle|RemoveFolderButtonStyle|CardBorderStyle|ControlTemplate|Tapped=" Duplicates -g "*.xaml"
```

Expected:

- Hex matches occur only in `Themes/Colors.xaml`.
- Exactly the five locked RGB values occur.
- No legacy style, custom ControlTemplate or layout-element Tapped match occurs.

- [ ] **Step 3: Validate breakpoints**

Test app content widths:

- 640 epx: one-column Scan options, overflowing Results commands in CommandBar overflow, overlay preview, no horizontal clipping.
- 641–1007 epx: medium layout, compact/overlay preview, readable SettingsCards.
- 1008 epx and larger: two-column Scan options and inline Results preview.

Resize continuously across 640/641 and 1007/1008 boundaries. Acceptance: no element jumps outside the viewport, loses focus, or becomes unreachable.

- [ ] **Step 4: Validate themes and materials**

For System, Light and Dark theme:

1. Solid background uses the locked palette.
2. Mica, Mica Alt and Acrylic render through `Window.SystemBackdrop`.
3. Buttons, ComboBoxes, CheckBoxes, ToggleSwitches, NumberBoxes, Expander, ListView, CommandBar, MenuFlyout and ContentDialog use their WinUI 3 default geometry and animations.
4. Accent controls use `#D72323`; content uses `#F5EDED` or the matching locked light-theme foreground.
5. Hover, pressed, disabled and focus states are visibly distinct because WinUI owns their template and state transitions.

- [ ] **Step 5: Validate display and text scaling**

Test Windows display scaling at 100%, 150% and 200%, plus Windows “Make text bigger” at 100% and 150%. Acceptance:

- no clipped labels;
- command labels move to overflow instead of truncating critical actions;
- SettingsCard content stacks when needed;
- NumberBox header/value remains readable;
- delete confirmation buttons remain visible.

- [ ] **Step 6: Validate input modes**

Test mouse, touchpad and keyboard. If a touch-capable device is available, test touch scrolling, ListView selection, Button hit targets, Expander, ToggleSwitch, MenuFlyout light-dismiss and preview overlay. Record touch as “not verified” if no touch hardware exists; do not infer it from mouse behavior.

- [ ] **Step 7: Run Accessibility Insights**

Run FastPass on Scan, Results, Settings and delete ContentDialog. Acceptance:

- no missing accessible name;
- no keyboard trap;
- no invalid tab stop;
- no color-only state indication;
- no contrast failure for palette-defined text/background pairs;
- no clipped text at tested scale.

Do not suppress findings. Fix only confirmed findings in the owning page/resource and rerun the affected automated and manual gate.

- [ ] **Step 8: Commit confirmed acceptance fixes**

Stage only files changed for confirmed acceptance defects:

```powershell
git add Duplicates/Themes/Colors.xaml Duplicates/Themes/Styles.xaml Duplicates/Views
git commit -m "korjaus: viimeistele WinUI-käyttöliittymän hyväksyntähavainnot"
```

If no defect is found, do not create an empty commit.

---

### Task 11: Update architecture guidance and final proof

**Files:**
- Modify: `AGENTS.md`
- Modify: `memory/MEMORY.md`
- Modify: `README.md` only if it currently describes the old card/button UI

**Interfaces:**
- Produces: durable project rules preventing a return to handcrafted control emulation.

- [ ] **Step 1: Update AGENTS.md**

Lisää UI- ja teemasaantoihin:

```markdown
- Interaktiiviset pinnat käyttävät WinUI 3:n tai Microsoftin WinUI Community Toolkitin tarkoitukseen suunniteltuja kontrolleja ja niiden oletustemplaatteja.
- Älä lisää omaa ControlTemplatea tai yleisiä Primary/Secondary/Card-jäljitelmätyylejä. Käytä WinUI:n AccentButtonStylea ensisijaisiin toimintoihin ja oletustyyliä muihin painikkeisiin.
- Lukittu väripaletti on Colors.xaml-tiedoston viisi Palette*-resurssia. Kontrollien hover-, pressed-, disabled-, focus- ja high-contrast-tilat kuuluvat WinUI:lle.
- Results käyttää grouped ListView -valintaa preview-kohteelle; poistovalinta säilyy erillisenä DuplicateFileViewModel.IsSelected-tilana ja kanoninen ryhmälista pysyy ResultsViewModelissa.
```

- [ ] **Step 2: Update project memory**

Lisää `memory/MEMORY.md`-tiedostoon päivätty merkintä, joka kertoo:

- shell: TitleBar + NavigationView;
- Scan: ListView/NumberBox/Expander/InfoBar/ProgressBar;
- Results: CommandBar + grouped ListView + SplitView;
- Settings: SettingsCard/SettingsExpander;
- palette is locked, control templates are native;
- preview selection and deletion selection are separate data flows.

- [ ] **Step 3: Re-run the final source and build proof**

Run:

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
dotnet format Duplicates.slnx --verify-no-changes
rg -n "PrimaryButtonStyle|SecondaryButtonStyle|DangerButtonStyle|WhiteOutlineButtonStyle|RemoveFolderButtonStyle|CardBorderStyle|ControlTemplate|Tapped=" Duplicates -g "*.xaml"
```

Expected: all automated gates PASS and final `rg` produces no matches.

- [ ] **Step 4: Record honest manual evidence**

Final implementation report must list:

- exact commands and exit codes;
- tested Windows theme/material combinations;
- tested widths and scaling;
- keyboard/Narrator/Accessibility Insights results;
- whether touch was verified on hardware;
- whether packaged launch required `tools\Install-WindowsAppRuntime1.8.ps1`;
- any unverified device-specific behavior.

- [ ] **Step 5: Commit documentation**

```powershell
git add AGENTS.md memory/MEMORY.md README.md
git commit -m "dokumentaatio: kirjaa natiivin WinUI-käyttöliittymän rajat"
```

Omit `README.md` from `git add` if it did not require a factual update.

## Completion Criteria

Migraatio on valmis vasta, kun kaikki seuraavat toteutuvat:

1. Sovellus rakentuu WinUI 3 / Windows App SDK 1.8.9 -pinolla ilman uutta UI-frameworkia.
2. `XamlControlsResources` on ladattu ja tuotanto-XAML ei sisällä omaa `ControlTemplate`a.
3. Nykyiset viisi palettiväriä ovat täsmälleen ennallaan eikä kuudetta hex-väriä ole lisätty.
4. Primary-toiminnot käyttävät `AccentButtonStyle`a; muut Buttonit käyttävät WinUI-oletustyyliä.
5. Scan käyttää ListView-, NumberBox-, ComboBox-, CheckBox-, Expander-, InfoBar- ja ProgressBar-kontrolleja ilman CardBorderStyleä.
6. Settings käyttää SettingsCard/SettingsExpanderia ja NumberBoxia.
7. Results käyttää CommandBaria, AppBarButtoneita, grouped ListViewta, MenuFlyoutia ja SplitViewta.
8. Yksikään Border/Grid/StackPanel ei toimi `Tapped`-pohjaisena kontrollijäljitelmänä.
9. Results-rivin valinta ohjaa previewtä, mutta ei muuta poistovalintaa.
10. Poistoturvan invariantti ja ContentDialog-vahvistus säilyvät.
11. 640, 641–1007 ja 1008+ epx layoutit toimivat ilman vaakasuuntaista leikkaantumista.
12. Hover-, pressed-, disabled-, focus- ja high-contrast-tilat tulevat WinUI:n omista templateista.
13. Kaikki icon-only-komennot ovat nimetty UI Automationille.
14. Keyboard-, Narrator- ja Accessibility Insights -hyväksyntä on dokumentoitu.
15. Engine-testit, App-testit, app build ja `dotnet format --verify-no-changes` ovat vihreitä.
16. `AGENTS.md` ja `memory/MEMORY.md` kuvaavat uuden UI-arkkitehtuurin.

## Explicit Non-Goals

- Ei Windows App SDK-, Toolkit-, .NET- tai testipakettien versiopäivitystä.
- Ei engine-algoritmien, hashauksen, file walkerin tai byte-by-byte-varmistuksen muutosta.
- Ei skannaustulosten pysyvää tallennusta.
- Ei uutta design systemiä tai uutta väripalettia.
- Ei omaa custom control -kirjastoa.
- Ei WinUI Galleryn ulkoasun kopiointia pikseli pikseliltä; käytetään samoja WinUI-kontrolleja ja niiden lukitun SDK-version omia templateja.
- Ei `lc`- tai `sc`-wrapperien ajoa ilman käyttäjän erillistä pyyntöä.
