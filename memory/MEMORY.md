# Duplicates project memory

## v1 architecture

- Project root: `C:\Dev\Duplicates`.
- Source specification: `Duplicates_BuildSpec.md`.
- Implementation plan: `C:\Users\emmah\Downloads\Duplicates v1 Toteutussuunnitelma.md`.
- The solution uses `.slnx` and four projects: WinUI app `Duplicates`, UI-free engine library `Duplicates.Engine`, engine xUnit tests `Duplicates.Engine.Tests`, and app ViewModel xUnit tests `Duplicates.App.Tests`.
- Package versions are centralized in `Directory.Packages.props`; do not add package versions to individual project files.
- `global.json` locks the SDK to `10.0.301`.
- The app is intentionally pinned to Windows App SDK `1.8.260529003`, even though `2.2.0` exists. The build spec requires the 1.8 servicing branch.

## Engine boundary

- `Duplicates.Engine` targets `net10.0` and must not reference WinUI or Windows App SDK types.
- Public engine entrypoint: `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)`.
- The engine models live in `Duplicates.Engine.Models`.
- The scanner uses a size bucket -> 64 KB partial XxHash3 -> full XxHash3 -> optional byte-by-byte verify funnel.
- The scanner has an internal `IFileHasher` seam for tests only. `InternalsVisibleTo("Duplicates.Engine.Tests")` allows forced hash-collision tests without changing the public `DuplicateScanner.ScanAsync(...)` API.
- Progress reporting is throttled through `ScanProgressReporter` to avoid per-file UI spam.

## App boundary

- Results state has one canonical `_allGroups` list and a filtered/sorted visible `Groups` collection. Selections, totals, delete guard, undo snapshots, exclude-from-group, and post-delete updates use the canonical list so search/sort does not reset state.
- `IFileActionService.DeleteAsync` accepts `IProgress<DeleteProgress>?` and reports per-file delete progress. The Results ViewModel owns delete progress text/value and failure-detail display.
- Scan defaults listen to `ISettingsService.SettingsChanged` when no scan is active. Skipped paths from `ScanResult.SkippedPaths` are surfaced on Results.
- `Duplicates.App.Tests` uses a test ProjectReference with `DisableWindowsAppSdkAutoInitialize=true` and a direct reference to the app Debug x64 DLL. This avoids Windows App Runtime registration requirements for ViewModel unit tests.

## Verification notes

- Current engine test command: `dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore`.
- Current app ViewModel test command: `dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore`.
- Current app build command: `dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore`.
- Packaged debug launch needs Windows App Runtime 1.8 installed for the current user. Use `tools\Install-WindowsAppRuntime1.8.ps1` if `REGDB_E_CLASSNOTREG` appears before app code starts.
- A verified smoke path is: build Debug x64, register `Duplicates\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\AppxManifest.xml`, then launch `shell:AppsFolder\D8D23102-5301-46E2-A506-C486B1E205FB_m8bfy0jvdgykw!App`.

## Tooling notes

- Project-local wrappers `tools/lc.ps1`, `tools/sc.ps1`, `tools/bc.ps1`, `tools/tc.ps1`, `tools/dc.ps1`, `tools/ss.ps1`, `tools/ql.ps1`, and `tools/db.ps1` delegate to the shared Windows/.NET checker at `C:\Dev\Windows-check`.
- `tools/windows-check.config.psd1` records the repo-specific solution, Engine/App test projects, and Debug x64 app build command.
- These wrappers are intentionally separate from Android-check tooling; do not route Duplicates checks through Gradle, ktlint, detekt, Compose, MobSF, or other Android-specific scripts.

## 2026-07-30 - Native WinUI 3 interaction architecture

- The shell remains one native `TitleBar` plus `NavigationView` and `Frame`.
- Scan uses native `ListView`, `NumberBox`, `Expander`, `InfoBar`, and `ProgressBar` controls with responsive 640/1008 epx states.
- Results uses a `CommandBar`, grouped single-selection `ListView`, row `MenuFlyout`, and responsive `SplitView` preview.
- Settings uses Community Toolkit `SettingsCard` and `SettingsExpander` controls.
- `Colors.xaml` owns exactly five locked palette colors; WinUI owns control templates and interaction-state visuals.
- Results preview selection (`SelectedFile`) and delete selection (`DuplicateFileViewModel.IsSelected`) are separate data flows. The canonical duplicate groups remain in `ResultsViewModel`.
