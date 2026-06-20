# Duplicates v1 Remaining Work Plan

## Summary
Nykytila: scaffold, engine, perus-UI, asetukset, poistopalvelu, README, AGENTS ja memory ovat olemassa. `dotnet test`, app-build ja `dotnet format --verify-no-changes` menivät läpi. Jäljellä oleva työ liittyy v1-speksin viimeistelyyn, ei build-rikkoihin.

Vielä kesken suhteessa [Duplicates_BuildSpec.md](C:/Dev/Duplicates/Duplicates_BuildSpec.md:502): Results-sivun valinnan säilyminen haussa/lajittelussa, poistovirran progressi ja failure-detailit, skipped-files InfoBar, ViewModel-testit, engine-edge-testit, preview/perf/accessibility-viimeistely, asetusten varoitukset/presetit, dead template -koodin poisto sekä pakkaus-/launch-UAT.

## Key Changes
- Lisää app-testiprojekti `C:\Dev\Duplicates\Duplicates.App.Tests\Duplicates.App.Tests.csproj`, koska nykyiset 9 testiä kattavat vain engineä.
- Korjaa `ResultsViewModel`: pidä kanoninen all-groups-lista erillään näkyvästä `Groups`-listasta, jotta search/sort ei nollaa valintoja.
- Lisää app-sisäinen `DeleteProgress` ja muuta `IFileActionService.DeleteAsync(...)` raportoimaan eteneminen.
- Lisää Results/Scan UI:hin skipped-file summary + details, auto-select undo, exclude-from-group, delete progress, responsive/collapsible preview ja capped image preview.
- Viimeistele Settings: permanent delete -varoitus, min-size presetit, assembly-version About-teksti ja persisted settings -> Scan defaults -synkka.
- Poista käyttämättömät `C:\Dev\Duplicates\Duplicates\MainPage.xaml` ja `.xaml.cs`, joissa on template-TODO.
- Päivitä `C:\Dev\Duplicates\AGENTS.md`, `C:\Dev\Duplicates\memory\MEMORY.md` ja `C:\Dev\Duplicates\README.md`, koska uusi testiprojekti ja muuttunut Results/deletion-dataflow ovat arkkitehtuurimuutos.

## Implementation Plan
1. **Test harness ensin**
   - Luo `Duplicates.App.Tests` xUnit-projekti Windows TFM:llä `net10.0-windows10.0.22621.0`, lisää se `C:\Dev\Duplicates\Duplicates.slnx`:ään ja viittaa `C:\Dev\Duplicates\Duplicates\Duplicates.csproj`:iin.
   - Lisää fake `ISettingsService` ja fake `IFileActionService`.
   - Testaa: manual never-select-all, auto-select newest/oldest/shortest/preferred, search/sort preserve selection, delete guard, partial delete failures, hidden selected files remain selected, settings load updates scan defaults.

2. **Results state correctness**
   - Muuta `C:\Dev\Duplicates\Duplicates\ViewModels\ResultsViewModel.cs`: `_allGroups` säilyttää kaikki group-VM:t, `Groups` näyttää vain filtteröidyn/sortatun näkymän.
   - `SelectedFiles`, totals, delete guard ja delete update käyttävät `_allGroups`-listaa.
   - Lisää selection snapshot + `UndoSelectionCommand` auto-selectille.
   - Lisää `ExcludeFileCommand`, joka poistaa tiedoston ryhmästä ilman levytoimintoa ja pudottaa alle 2 tiedoston ryhmän.
   - Korjaa `DuplicateGroupViewModel`-lazy state: `IsExpanded` ja näkyvä files-kokoelma vain avatuille ryhmille.

3. **Deletion flow**
   - Lisää `C:\Dev\Duplicates\Duplicates\Models\DeleteProgress.cs`.
   - Päivitä `IFileActionService.DeleteAsync(files, progress, cancellationToken)`.
   - Näytä Results-sivulla progress text/bar ja failure details -dialogi/expander.
   - Tee final guard ryhmittäin juuri ennen poistoa: jos selected count >= group file count, abortoi ja näytä warning.
   - Säilytä default Recycle Bin; permanent-mode näkyy sekä Settingsissä että confirm-dialogissa selkeänä irreversible-varoituksena.

4. **Scan + skipped files + settings sync**
   - `ScanViewModel` kuuntelee `SettingsService.SettingsChanged` ja päivittää oletukset, kun skannaus ei ole käynnissä.
   - Lisää skipped paths -summary Results-sivulle `ScanResult.SkippedPaths`-datasta: “N files were skipped” + details.
   - Lisää no-folders empty stateen myös Add folder -button.
   - Piilota category-checkboxit ellei mode ole Categories ja custom-extension input ellei mode ole CustomExtensions.
   - Korjaa `ScanPage` event-subscription niin `ScanCompleted` ei kerry useita kertoja navigoinnissa.

5. **UI/perf/accessibility viimeistely**
   - Muuta page-level XAML-sidonnat `x:Bind`-muotoon; jos folder string -template estää tämän siististi, vaihda folder item pieneksi typed view modeliksi.
   - Lisää outer `ListView` virtualizing panel eksplisiittisesti ja varmista, että member rows realisoituvat vasta group expandissa.
   - Tee preview-pane collapsible ja alle kapean leveyden piilotettava/drop-below.
   - Luo capped image preview: `BitmapImage.DecodePixelWidth = 512`, metadataan size, created, modified, group reclaimable ja kuvien dimensions.
   - Lisää/varmista `AutomationProperties.Name` kaikille icon-only-toiminnoille, keyboard tab order, visible focus ja “Kept” star/text badge.
   - Toteuta middle-path display helper pitkille poluille; full path pysyy tooltipissä.

6. **Engine hardening**
   - Lisää engine-testit: same first 64 KB but different tail, locked file skipped, invalid folder skipped, changed-file/hash failure skipped, invalid concurrency throws, hardlink same-file-id not reported.
   - Lisää sisäinen test seam vain tarvittaessa: `IFileHasher` + `InternalsVisibleTo("Duplicates.Engine.Tests")`, jotta byte-verify split voidaan testata forced hash collision -tilanteella.
   - Pidä public engine API ennallaan: `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)`.

7. **Docs, packaging, acceptance**
   - Päivitä docs uuden testiprojektin, delete progressin, skipped-file surfaced statejen ja Results dataflow’n mukaan.
   - Aja verifiointi:
     - `dotnet restore C:\Dev\Duplicates\Duplicates.slnx`
     - `dotnet test C:\Dev\Duplicates\Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore`
     - `dotnet test C:\Dev\Duplicates\Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore`
     - `dotnet build C:\Dev\Duplicates\Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore`
     - `dotnet publish C:\Dev\Duplicates\Duplicates\Duplicates.csproj -c Release -r win-x64 --self-contained -p:WindowsPackageType=None`
     - `dotnet format C:\Dev\Duplicates\Duplicates.slnx --verify-no-changes --no-restore`
   - Manual UAT: launch Scan page, add folders via picker/drop, cancel progress, no-duplicates state, duplicates state, search/sort preserving selection, auto-select undo, delete confirm, Recycle Bin delete, partial failure display, theme switch, backdrop switch, keyboard navigation, narrow-window Results layout.

## Assumptions
- Ei ajeta `lc`/`sc`-skriptejä.
- Työtila ei ole git-repo, joten suunnitelma ei sisällä commit-askeleita. Jos git alustetaan myöhemmin, commit-viestit suomeksi.
- Lukitut paketit pysyvät AGENTS-ohjeen mukaisina; ennen toteutusta tarkistetaan viralliset docs/NuGet-sivut, mutta ei hypätä Windows App SDK 2.x -haaraan ilman uutta päätöstä.
