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
- Windows App SDK: `Microsoft.WindowsAppSDK` `1.8.260529003`
- MVVM Toolkit: `CommunityToolkit.Mvvm` `8.4.2`
- Settings controls: `CommunityToolkit.WinUI.Controls.SettingsControls` `8.2.251219`
- Hashing: `System.IO.Hashing` `10.0.9`
- Pakettiversiot ovat keskitettyna `Directory.Packages.props`-tiedostossa. Ala lisaa versioita suoraan yksittaisiin `.csproj`-tiedostoihin.

## Arkkitehtuurirajat

- `Duplicates.Engine` ei saa viitata WinUI-, Windows App SDK- tai app-projektin tyyppeihin.
- UI kutsuu enginea vain `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)` -rajapinnan kautta.
- Results-nakyma sailyttaa kanonisen duplicate-ryhmalistan erillaan nakyvasta search/sort-listasta. Valinnat, delete guard, delete-paivitykset ja totals lasketaan kanonisesta listasta.
- Tiedostopoistot kulkevat `IFileActionService.DeleteAsync(files, IProgress<DeleteProgress>?, CancellationToken)` -rajapinnan kautta; progress ja failure-detailit ovat appin ViewModel-tilaa.
- Skannaustuloksia, valintoja ja poistovirtaa ei tallenneta pysyvasti v1:ssa. Vain asetukset tallennetaan `%LOCALAPPDATA%\Duplicates\settings.json`.
- Poistoturvan invariantti kuuluu ViewModel-tasolle ja tarkistetaan uudelleen ennen poistoa: yhdesta duplicate-ryhmasta ei saa valita poistoon kaikkia tiedostoja.

## UI- ja teemasaannot

- UI-tekstit ovat englanniksi.
- Valttamattomat koodikommentit pidetaan lyhyina; kayta suomea vain jos kommentti on agenttityon tai projektiohjeen kontekstia, ei kayttajalle nakyvaa UI-tekstia.
- Kaikki varit ovat `Duplicates/Themes/Colors.xaml`-tokeneita. Ala kirjoita inline-hex-arvoja XAML-nakymiin tai ViewModel-koodiin.
- Mint on ainoa turvallisen toiminnan accent-varina. Delete/destructive-toiminnot kayttavat danger-tokenia.
- Kayta WinUI:n natiiveja kontrolleja ja Community Toolkitin `SettingsCard`/`SettingsExpander`-kontrolleja, kun se sopii speksiin.

## Verifiointi

- Kayta TDD:ta tuotantokoodin kayttaytymismuutoksiin.
- Engine-muutosten peruskomento: `dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore`.
- App ViewModel -muutosten peruskomento: `dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore`.
- App-buildin peruskomento: `dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore`.
- Koko restore: `dotnet restore Duplicates.slnx`.
- Lopuksi aja `dotnet format Duplicates.slnx --verify-no-changes`, ellei se esty tunnettuun toolchain-ongelmaan.
- Ala aja kayttajan `lc` / `sc` -skripteja. Tassa projektissa niita ei ole; jos ne lisataan myohemmin, kayttaja ajaa ne itse.
- Packaged Debug -launch edellyttaa Windows App Runtime 1.8 -paketteja nykyiselle kayttajalle. Asennusapu on `tools\Install-WindowsAppRuntime1.8.ps1`.
