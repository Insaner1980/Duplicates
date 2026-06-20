# Duplicates v1 Toteutussuunnitelma

**Summary**
Toteutetaan [Duplicates_BuildSpec.md](C:/Dev/Duplicates/Duplicates_BuildSpec.md:1) mukaan natiivi Windows 11 WinUI 3 -sovellus, jossa UI ja poistovirta ovat turvallisia ja skannausmoottori on erillinen testattava kirjasto. Nykyinen työtila sisältää vain speksin, ei git-repoa eikä paikallista `AGENTS.md`:tä.

**Lukitut versiot ja päätökset**
- Käytä paikallista .NET SDK:ta `10.0.301`; Microsoftin .NET 10 metadata näyttää sen uusimmaksi SDK:ksi.
- Käytä speksin mukaisesti Windows App SDK 1.8 -haaraa, ei uusinta 2.x-haaraa: `Microsoft.WindowsAppSDK` `1.8.260529003`.
- Käytä `CommunityToolkit.Mvvm` `8.4.2`, `CommunityToolkit.WinUI.Controls.SettingsControls` `8.2.251219`, `System.IO.Hashing` `10.0.9`.
- Käytä WinUI 3:n uusia `Microsoft.Windows.Storage.Pickers`-pickereitä `AppWindow.Id`:llä, koska 1.8+ ei tarvitse vanhaa HWND-initialisointia.
- Tee manual-wiring `AppServices`-luokalla, ei erillistä DI-pakettia. UI-tekstit englanniksi; välttämättömät koodikommentit ja commit-viestit suomeksi.
- Release-polku: packaged MSIX oletuksena, unpackaged x64 publish dokumentoituna fallbackina. Git-commitit vain jos repo alustetaan erikseen.

**Toteutusvaiheet**
1. Scaffold ja projektikonfiguraatio:
   - Luo `Duplicates.sln`, WinUI app `Duplicates/`, engine-kirjasto `Duplicates.Engine/` ja testiprojekti `Duplicates.Engine.Tests/`.
   - Lisää `Directory.Packages.props` yhdeksi pakettiversioiden lähteeksi.
   - Aseta appiin `net10.0-windows10.0.22621.0`, `TargetPlatformMinVersion=10.0.22000.0`, `RuntimeIdentifiers=win-x64`, `UseWinUI=true`, `Nullable=enable`, `LangVersion=14.0`, `EnableMsixTooling=true`.
   - Engine pysyy `net10.0`-kirjastona ilman WinUI-viitteitä.
2. Engine API ja mallit:
   - Luo `FileEntry`, `DuplicateGroup`, `ScanOptions`, `ScanProgress`, `ScanResult`, `SkippedPath`, `FileTypeFilter`, `ScanPhase` ja kategoriat.
   - Lisää `ScanOptions.MaxHashingConcurrency` nullable-inttinä: `null` = Auto / `Environment.ProcessorCount`, muuten vain `1`, `2`, `4`, `8`.
   - `ScanResult.SkippedPaths` on `IReadOnlyList<SkippedPath>` eikä pelkkä string-lista, jotta syy säilyy ilman rinnakkaislistoja.
3. Skannausmoottori:
   - `FileWalker` enumeroi `Directory.EnumerateFiles` + `EnumerationOptions`, suodattaa koon, tyypit, hidden/system ja reparse pointit.
   - Dedupoi kanonisen polun perusteella ja lisää pieni Windows file-id helper hardlinkien poisrajaamiseen; jos file-id ei onnistu, fallback on kanoninen polku.
   - `DuplicateScanner.ScanAsync(...)` toteuttaa size bucket -> 64 KB partial hash -> full `XxHash3` -> byte-by-byte verify -> sorted result.
   - Hashaus ja verify striimaavat tiedostot, käyttävät noin 1 MB bufferia, kunnioittavat cancellationia ja throttlaavat progressin noin 10 Hz tasolle.
4. WinUI shell, teema ja asetukset:
   - Luo custom title bar, Mica/Mica Alt/Acrylic/Solid backdrop, `NavigationView` ja kolme sivua: Scan, Results, Settings.
   - Toteuta `Themes/Colors.xaml` ja `Themes/Styles.xaml`; kaikki värit tokenien kautta, ei inline-hexejä muualla.
   - `SettingsService` lukee/kirjoittaa `%LOCALAPPDATA%\Duplicates\settings.json`; tallennetaan vain asetukset, ei skannauskansioita tai tuloksia.
5. Scan flow:
   - `ScanViewModel` hallitsee kansiolistaa, type filteriä, kokorajaa, hidden/system/verify-toggleja ja cancellationia.
   - Folder picker + drag/drop lisäävät vain kansioita; Start scan on pois käytöstä ilman kansioita.
   - Progress-näkymä näyttää vaiheen, tiedostomäärät, prosessoidun datan, elapsed/ETA:n, current pathin ja Cancel-napin; valmis skannaus navigoi Results-sivulle.
6. Results ja poistoturva:
   - `ResultsViewModel`, `DuplicateGroupViewModel` ja `DuplicateFileViewModel` hallitsevat hakua, lajittelua, auto-select-sääntöjä ja valintaa.
   - Valintainvariantti toteutetaan ViewModelissa: ryhmässä jää aina vähintään yksi unchecked survivor; sama tarkistetaan uudelleen ennen deleteä.
   - `FileActionService` avaa tiedoston, paljastaa kansion, kopioi pathin ja poistaa oletuksena Recycle Biniin `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile`-kutsulla.
   - Poisto toimii taustalla, raportoi osittaiset epäonnistumiset `InfoBar`illa ja päivittää ryhmät/totalsit ilman sovelluksen uudelleenkäynnistystä.
7. Dokumentaatio ja projektimuisti:
   - Luo `README.md`: restore/build/run, packaged MSIX, cert-trust step ja unpackaged fallback.
   - Luo root `AGENTS.md`: arkkitehtuuri, sallitut paketit, engine/UI-raja, teematokenisääntö, verifiointikomennot.
   - Luo `memory/MEMORY.md` projektin sisälle ja kirjaa v1-arkkitehtuuri, koska toteutus luo uuden vastuurakenteen.

**Test Plan**
- Engine unit tests: size-bucket elimination, partial-hash elimination, full-hash grouping, byte-verify split, zero-byte default exclusion, min-size 0 inclusion, locked/permission failure skip, cancellation, changed-file skip, category/custom extension filtering, symlink default skip, canonical duplicate path dedupe.
- ViewModel tests or focused manual checks: auto-select keep newest/oldest/shortest/preferred-folder, never-select-all invariant, delete guard, search/sort preserving selection.
- Build checks: `dotnet restore`, `dotnet build -c Debug`, `dotnet test`, `dotnet format --verify-no-changes`.
- UI acceptance: launch Scan page, theme switch System/Light/Dark, Mica fallback, folder picker, drag/drop, progress cancel, no-duplicates empty state, before-first-scan state, Results delete confirmation, Recycle Bin default.
- Packaging checks: packaged MSIX build, cert instructions verified, and unpackaged `dotnet publish -c Release -r win-x64 --self-contained` fallback documented.

**Assumptions**
- Toteutus ei aja käyttäjän `lc`/`sc`-skriptejä; niitä ei ole tässä repossa ja ohje sanoo, että käyttäjä ajaa ne itse.
- Testiprojekti lisätään vain verifiointia varten; tuotantoarkkitehtuuri pysyy speksin mukaisena: yksi WinUI app + yksi engine-kirjasto.
- Windows App SDK 2.2 on nykyinen stable release, mutta speksi vaatii 1.8-haaran uusimman patchin, joten 2.x:ään ei hypätä.

**Tarkistetut lähteet**
- [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [Windows App SDK 1.8.9 discussion](https://github.com/microsoft/WindowsAppSDK/discussions/6552)
- [CommunityToolkit.Mvvm NuGet](https://www.nuget.org/packages/CommunityToolkit.Mvvm)
- [SettingsControls NuGet](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.SettingsControls/)
- [System.IO.Hashing NuGet](https://www.nuget.org/packages/System.IO.Hashing/)
- [WinUI folder pickers](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers)
- [WinUI TitleBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/title-bar)
- [WinUI system backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)
