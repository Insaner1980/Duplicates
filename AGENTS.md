# Duplicates agent instructions

## Projektin tila

- Duplicates on natiivi Windows 11 WinUI 3 -sovellus, jossa on 14 paikallista tiedostoanalyysi- ja siivoustyokalua. Exact-duplikaatit ovat byte-varmennettuja; media-similarity on aina kayttajan arvioitava ehdokas, ei automaattinen duplikaattitotuus.
- Ratkaisu on jaettu kahteen tuotantovastuuseen:
  - `Duplicates.Engine`: `net10.0`-kirjasto ilman UI-riippuvuuksia. Sisaltaa exact-skannerin seka AnalysisScope/inventory/analyzer/media-algoritmit.
  - `Duplicates`: WinUI 3 -app, joka omistaa shellin, Windows-providerit, session storet, operation coordinatorin, tiedosto- ja transform-transaktiot, sivut, asetukset, cachen ja teemat.
- Testiprojekti `Duplicates.Engine.Tests` verifioi engine-kirjastoa erillaan UI:sta.
- Testiprojekti `Duplicates.App.Tests` verifioi appin service-, ViewModel-, native-XAML- ja Windows-provider-seameja. Se rakentaa WinUI-appin testikonfiguraatiossa ilman Windows App SDK auto-initializeria ja viittaa appin Debug x64 -DLL:aan, jotta logiikka- ja source-contract-testit eivat vaadi rekisteroitya Windows App Runtimea.

## Lukitut versiot

- .NET SDK: `10.0.301` (`global.json`)
- TFM appille: `net10.0-windows10.0.22621.0`
- Minimi Windows: `10.0.22000.0`
- RuntimeIdentifier: `win-x64`
- Windows App SDK: `Microsoft.WindowsAppSDK` `1.8.260529003`
- MVVM Toolkit: `CommunityToolkit.Mvvm` `8.4.2`
- Settings controls: `CommunityToolkit.WinUI.Controls.SettingsControls` `8.2.251219`
- Hashing: `System.IO.Hashing` `10.0.9`
- Pakettiversiot ovat keskitettyna `Directory.Packages.props`-tiedostossa. Ala lisaa versioita suoraan yksittaisiin `.csproj`-tiedostoihin.

## Arkkitehtuurirajat

- `Duplicates.Engine` ei saa viitata WinUI-, Windows App SDK- tai app-projektin tyyppeihin.
- Exact-skannaus kutsuu enginea `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)` -rajapinnan kautta.
- Byte-verification vuokraa yhden kahden 1 MiB bufferin parin per hash-ryhma, kayttaa sita kaikissa ryhman candidate-vertailuissa ja palauttaa bufferit `finally`-lohkossa. Vertailut avaavat tiedostostreamit tuoreina ja sailyttavat exactness-, cancellation- ja skip-semanticsin.
- Yleiset analyysityokalut kulkevat `IAnalysisService.RunAsync(...)`-rajapinnan kautta. Windows-kuva/video/musiikki/probe-toteutukset ja fingerprint-cache pysyvat app-kerroksessa engine-interfacejen takana.
- `PathScopeViewModel` on Scan/Analysis/EXIF/Video-sivujen yhteinen kanoninen include/exclude-scope. Engine saa `AnalysisScope`-arvot, ei UI-esitysteksteja.
- `PathScopeViewModel.IncludedPaths` ja `ExcludedPaths` sisaltavat `ScopePathViewModel`-oliot: `FullPath` on kanoninen engineen valitettava arvo, kun taas `DisplayName` ja `ParentPath` ovat vain UI-esitysta. Rinnakkaista string-kokoelmaa ei yllapideta.
- Results-nakyma sailyttaa kanonisen duplicate-ryhmalistan erillaan nakyvasta search/sort-listasta. Valinnat, delete guard, delete-paivitykset ja totals lasketaan kanonisesta listasta.
- Analysis Results sailyttaa flat findingit ja similarity-ryhmat kanonisina erillaan nakyvista search/sort-projektioista. Partial action paivittaa vain onnistuneet polut; similarity survivors revalidoidaan ja ryhmitellaan globaalisti uudelleen.
- Move/Delete-toiminnot ja resetit captureoivat initiating sessionin. Move/Delete-service-summaryjen kanoninen reconciliation ja completion-status paivitetaan vain, kun initiating session on yha current; reset callback tyhjentaa vain capturetun current-sessionin. `IAppOperationCoordinator` sallii yhden foreground-operaation kerrallaan.
- Tiedostopoistot kulkevat `IFileActionService.DeleteAsync(files, IProgress<DeleteProgress>?, CancellationToken)` -rajapinnan kautta; progress ja failure-detailit ovat appin ViewModel-tilaa.
- Result-sessioita, valintoja ja action-historiaa ei tallenneta pysyvasti. Asetukset tallennetaan `%LOCALAPPDATA%\Duplicates\settings.json`; valinnainen media-fingerprint-cache on `%LOCALAPPDATA%\Duplicates\cache\media-fingerprints-v1.json`.
- Poistoturvan invariantti kuuluu ViewModel-tasolle ja tarkistetaan uudelleen ennen poistoa: yhdesta duplicate-ryhmasta ei saa valita poistoon kaikkia tiedostoja.
- Ryhman ainoan KEEP-rivin native poistovalinta on disabled `CanToggleDeletionSelection`-tilalla. Bulk-valintasaannot ja Undo eivat refreshaa total-arvoja per tiedosto, vaan julkaisevat yhden lopullisen totals-refreshin.
- Bad extensions/Bad names ovat rename-only. Similarity-ryhmien Move/Delete on aina manuaalinen. Exact-link replacement kuuluu vain exact-resultteihin. EXIF/video-output-riveilla on vain Open/Reveal; video optimizer luo uuden sisar-`.mp4`:n ja sailyttaa lahteen.
- Native `FileSavePicker` on exportin create/overwrite-authority: vain onnistunut picker-handleri valittaa `overwriteExisting=true`; service- ja ei-picker-kutsujen oletus pysyy `false`, ja julkaisu tapahtuu sibling-tempista ilman destinationin pre-deletea.

## UI- ja teemasaannot

- UI-tekstit ovat englanniksi.
- Valttamattomat koodikommentit pidetaan lyhyina; kayta suomea vain jos kommentti on agenttityon tai projektiohjeen kontekstia, ei kayttajalle nakyvaa UI-tekstia.
- Kaikki varit ovat `Duplicates/Themes/Colors.xaml`-tokeneita. Ala kirjoita inline-hex-arvoja XAML-nakymiin tai ViewModel-koodiin.
- `#D72323` on sovelluksen yleinen accent-vari kaikissa interaktiivisissa kontrolleissa. Delete/destructive-toiminnot kayttavat samaa punaista danger-tokenin kautta.
- Kayta WinUI:n natiiveja kontrolleja ja Community Toolkitin `SettingsCard`/`SettingsExpander`-kontrolleja, kun se sopii speksiin.
- Interaktiiviset pinnat kayttavat WinUI 3:n tai Microsoftin WinUI Community Toolkitin tarkoitukseen suunniteltuja kontrolleja ja niiden oletustemplaatteja.
- Ala lisaa omaa `ControlTemplate`a tai yleisia Primary/Secondary/Card-jaljitelmatyyleja. Kayta WinUI:n `AccentButtonStyle`a ensisijaisiin toimintoihin ja oletustyylia muihin painikkeisiin.
- Lukittu varipaletti on `Colors.xaml`-tiedoston viisi `Palette*`-resurssia. Kontrollien hover-, pressed-, disabled-, focus- ja high-contrast-tilat kuuluvat WinUI:lle.
- `AccentButtonStyle` kuuluu vain toimintoihin Start scan, Start analysis, Clean images ja Optimize videos. New scan/analysis, destructive-toiminnot ja Clear cache ovat tavallisia native-painikkeita.
- Icon-only-kontrollilla on accessible name ja tooltip. Run/status-pinnat ovat polite live regioneita. Layout-elementteihin ei lisata tap-handleria; result-valinta kayttaa native `ListView`-semantiikkaa.
- `WrapGrid` kuuluu vain `ItemsPanelTemplate`-kayttoon. Kayta yleisessa layoutissa `VariableSizedWrapGrid`ia tai muuta tarkoitukseen sopivaa native-paneelia.
- Results kayttaa grouped `ListView` -valintaa preview-kohteelle; poistovalinta sailyy erillisena `DuplicateFileViewModel.IsSelected`-tilana ja kanoninen ryhmalista pysyy `ResultsViewModel`issa.
- Results-lista ei lyhennä hakemistopolkuja ViewModelissa. Lista saa rivittää polun enintään kahdelle riville, tooltip säilyttää koko tiedostopolun ja Preview näyttää valitun tiedoston koko polun ilman ellipsiä.

## Verifiointi

- Kayta TDD:ta tuotantokoodin kayttaytymismuutoksiin.
- Engine-muutosten peruskomento: `dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore`.
- App service/ViewModel/native-contract -muutosten peruskomento: `dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore`.
- App-buildin peruskomento: `dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore`.
- Koko restore: `dotnet restore Duplicates.slnx`.
- Lopuksi aja `dotnet format Duplicates.slnx --verify-no-changes`, ellei se esty tunnettuun toolchain-ongelmaan.
- Paikalliset Windows-check-wrapperit ovat `tools/lc.ps1`, `tools/sc.ps1`, `tools/bc.ps1`, `tools/tc.ps1`, `tools/dc.ps1`, `tools/ss.ps1`, `tools/ql.ps1` ja `tools/db.ps1`.
- Wrapperit delegoivat yhteiseen `C:\Dev\Windows-check`-runkoon ja lukevat projektikohtaiset polut `tools/windows-check.config.psd1`-tiedostosta.
- Windows-check-runko on vain WinUI/.NET-projekteille; ala kayta Android-checkin Gradle-, ktlint-, detekt-, Compose- tai MobSF-polkuja tassa projektissa.
- Wrapperiraportit kirjoitetaan `reports/`-kansioon, joka pysyy gitignoressa.
- Packaged Debug -launch edellyttaa Windows App Runtime 1.8 -paketteja nykyiselle kayttajalle. Asennusapu on `tools\Install-WindowsAppRuntime1.8.ps1`.
