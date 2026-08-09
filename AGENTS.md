# Duplicates agent instructions

## Projektin tila

- Duplicates on natiivi Windows 11 WinUI 3 -sovellus, joka etsii byte-identtiset duplikaattitiedostot ja poistaa valitut kopiot oletuksena Recycle Biniin.
- Ratkaisu on jaettu kahteen tuotantovastuuseen:
  - `Duplicates.Engine`: `net10.0`-kirjasto ilman UI-riippuvuuksia. Sisaltaa mallit, tiedostoenumeraation, XxHash3-pohjaisen skannausfunnelin ja byte-by-byte-varmistuksen.
  - `Duplicates`: WinUI 3 -app, joka omistaa shellin, sivut, asetukset, tiedostotoiminnot ja teemat.
- Testiprojekti `Duplicates.Engine.Tests` verifioi engine-kirjastoa erillaan UI:sta.
- Testiprojekti `Duplicates.App.Tests` verifioi appin ViewModel-kayttaytymista. Se rakentaa WinUI-appin testikonfiguraatiossa ilman Windows App SDK auto-initializeria ja viittaa appin Debug x64 -DLL:aan, jotta ViewModel-testit eivat vaadi rekisteroitya Windows App Runtimea.

## Lukitut versiot

- .NET SDK: `10.0.301` (`global.json`)
- TFM appille: `net10.0-windows10.0.22621.0`
- Minimi Windows: `10.0.22000.0`
- RuntimeIdentifier: `win-x64`
- Windows App SDK: `Microsoft.WindowsAppSDK` `2.3.1`
- MVVM Toolkit: `CommunityToolkit.Mvvm` `8.4.2`
- Settings controls: `CommunityToolkit.WinUI.Controls.SettingsControls` `8.2.251219`
- Hashing: `System.IO.Hashing` `10.0.10`
- Pakettiversiot ovat keskitettyna `Directory.Packages.props`-tiedostossa. Ala lisaa versioita suoraan yksittaisiin `.csproj`-tiedostoihin.

## Arkkitehtuurirajat

- `Duplicates.Engine` ei saa viitata WinUI-, Windows App SDK- tai app-projektin tyyppeihin.
- UI kutsuu enginea vain `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)` -rajapinnan kautta.
- Scan-sivun valitut kansiot ovat `ScanFolderViewModel`-olioita: `FullPath` on kanoninen engineen välitettävä arvo, kun taas `DisplayName` ja `ParentPath` ovat vain UI-esitystä. Rinnakkaista string-kokoelmaa ei ylläpidetä.
- Results-nakyma sailyttaa kanonisen duplicate-ryhmalistan erillaan nakyvasta search/sort-listasta. Valinnat, delete guard, delete-paivitykset ja totals lasketaan kanonisesta listasta.
- Tiedostopoistot kulkevat `IFileActionService.DeleteAsync(files, IProgress<DeleteProgress>?, CancellationToken)` -rajapinnan kautta; progress ja failure-detailit ovat appin ViewModel-tilaa.
- Skannaustuloksia, valintoja ja poistovirtaa ei tallenneta pysyvasti v1:ssa. Vain asetukset tallennetaan `%LOCALAPPDATA%\Duplicates\settings.json`.
- Poistoturvan invariantti kuuluu ViewModel-tasolle ja tarkistetaan uudelleen ennen poistoa: yhdesta duplicate-ryhmasta ei saa valita poistoon kaikkia tiedostoja.

## UI- ja teemasaannot

- UI-tekstit ovat englanniksi.
- Valttamattomat koodikommentit pidetaan lyhyina; kayta suomea vain jos kommentti on agenttityon tai projektiohjeen kontekstia, ei kayttajalle nakyvaa UI-tekstia.
- Kaikki varit ovat `Duplicates/Themes/Colors.xaml`-tokeneita. Ala kirjoita inline-hex-arvoja XAML-nakymiin tai ViewModel-koodiin.
- `#D72323` on sovelluksen yleinen accent-vari kaikissa interaktiivisissa kontrolleissa. Delete/destructive-toiminnot kayttavat samaa punaista danger-tokenin kautta.
- Kayta WinUI:n natiiveja kontrolleja ja Community Toolkitin `SettingsCard`/`SettingsExpander`-kontrolleja, kun se sopii speksiin.
- Interaktiiviset pinnat kayttavat WinUI 3:n tai Microsoftin WinUI Community Toolkitin tarkoitukseen suunniteltuja kontrolleja ja niiden oletustemplaatteja.
- Ala lisaa omaa `ControlTemplate`a tai yleisia Primary/Secondary/Card-jaljitelmatyyleja. Kayta WinUI:n `AccentButtonStyle`a ensisijaisiin toimintoihin ja oletustyylia muihin painikkeisiin.
- Lukittu varipaletti on `Colors.xaml`-tiedoston viisi `Palette*`-resurssia. Kontrollien hover-, pressed-, disabled-, focus- ja high-contrast-tilat kuuluvat WinUI:lle.
- Results kayttaa grouped `ListView` -valintaa preview-kohteelle; poistovalinta sailyy erillisena `DuplicateFileViewModel.IsSelected`-tilana ja kanoninen ryhmalista pysyy `ResultsViewModel`issa.
- Results-lista ei lyhennä hakemistopolkuja ViewModelissa. Lista saa rivittää polun enintään kahdelle riville, tooltip säilyttää koko tiedostopolun ja Preview näyttää valitun tiedoston koko polun ilman ellipsiä.

## Verifiointi

- Kayta TDD:ta tuotantokoodin kayttaytymismuutoksiin.
- Engine-muutosten peruskomento: `dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore`.
- App ViewModel -muutosten peruskomento: `dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore`.
- App-buildin peruskomento: `dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore`.
- Koko restore: `dotnet restore Duplicates.slnx`.
- Lopuksi aja `dotnet format Duplicates.slnx --verify-no-changes`, ellei se esty tunnettuun toolchain-ongelmaan.
- Paikalliset Windows-check-wrapperit ovat `tools/lc.ps1`, `tools/sc.ps1`, `tools/bc.ps1`, `tools/tc.ps1`, `tools/dc.ps1`, `tools/ss.ps1`, `tools/ql.ps1` ja `tools/db.ps1`.
- Wrapperit delegoivat yhteiseen `C:\Dev\Windows-check`-runkoon ja lukevat projektikohtaiset polut `tools/windows-check.config.psd1`-tiedostosta.
- Windows-check-runko on vain WinUI/.NET-projekteille; ala kayta Android-checkin Gradle-, ktlint-, detekt-, Compose- tai MobSF-polkuja tassa projektissa.
- Wrapperiraportit kirjoitetaan `reports/`-kansioon, joka pysyy gitignoressa.
- Packaged Debug -launch edellyttaa Windows App Runtime 2.3 -paketteja nykyiselle kayttajalle. Asennusapu on `tools\Install-WindowsAppRuntime2.3.ps1`.
