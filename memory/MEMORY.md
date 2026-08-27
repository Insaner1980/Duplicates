# Duplicates project memory

## Current architecture

- Project root: `C:\Dev\Duplicates`.
- Current implementation and tests are authoritative; historical specifications and plans are context only.
- The solution uses `.slnx` and four projects: WinUI app `Duplicates`, UI-free engine library `Duplicates.Engine`, engine xUnit tests `Duplicates.Engine.Tests`, and app service/ViewModel/native-XAML/Windows-provider xUnit tests `Duplicates.App.Tests`.
- Package versions are centralized in `Directory.Packages.props`; do not add package versions to individual project files.
- `global.json` requests SDK `10.0.301` with `latestFeature` roll-forward.
- The app is pinned to Windows App SDK `2.3.1`; the data-cleaner integration retains the newer dependency versions from `main`.
- `ToolKind` has exactly 14 tools: exact duplicates, three similarity/music tools, four storage tools, invalid links, broken files, bad extensions/names, EXIF remover, and video optimizer.

## Engine boundary

- `Duplicates.Engine` targets `net10.0` and must not reference WinUI or Windows App SDK types.
- Public engine entrypoint: `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)`.
- The engine models live in `Duplicates.Engine.Models`.
- The scanner uses a size bucket -> 64 KB partial XxHash3 -> full XxHash3 -> optional byte-by-byte verify funnel.
- The scanner has an internal `IFileHasher` seam for tests only. `InternalsVisibleTo("Duplicates.Engine.Tests")` allows forced hash-collision tests without changing the public `DuplicateScanner.ScanAsync(...)` API.
- Progress reporting is throttled through `ScanProgressReporter` to avoid per-file UI spam.
- Byte verification rents one pair of 1 MiB buffers per hash group, reuses it across candidate comparisons, and returns both buffers in `finally`; comparisons still open fresh streams.
- General tools use UI-free `AnalysisScope`, `FileInventoryBuilder`, analyzer/media interfaces, and `AnalysisResult` records in Engine. Windows WIC/video/music/probe providers remain in the app behind those interfaces.

## App boundary

- `PathScopeViewModel.IncludedPaths` and `ExcludedPaths` contain `ScopePathViewModel` items. `FullPath` is canonical engine input; `DisplayName` and `ParentPath` are presentation only, with no parallel string collection.
- Results state has one canonical `_allGroups` list and a filtered/sorted visible `Groups` collection. Selections, totals, delete guard, undo snapshots, exclude-from-group, and post-delete updates use the canonical list so search/sort does not reset state.
- The sole KEEP row exposes a disabled native deletion checkbox through `CanToggleDeletionSelection`. Bulk rules and Undo suppress per-file totals refreshes and publish one final refresh, avoiding quadratic work on very large groups.
- `IFileActionService.DeleteAsync` accepts `IProgress<DeleteProgress>?` and reports per-file delete progress. The Results ViewModel owns delete progress text/value and failure-detail display.
- `PathScopeViewModel` is shared by Scan, Analysis, EXIF, and Video. Exact, flat, and similarity result ViewModels keep canonical state apart from visible search/sort projections.
- `AppOperationCoordinator` permits one foreground scan/analysis/action/transform. Move/Delete service-summary canonical reconciliation and completion status require the initiating session to remain current; result resets clear only the captured still-current session.
- Similarity move/delete always requires manual selection and provider revalidation followed by global regrouping. Bad extensions/names are rename-only. Exact-link replacement is exact-only. EXIF/video output rows expose only Open/Reveal; video optimization creates a sibling `.mp4` and preserves the source.
- Native FileSavePicker success is export overwrite authority; picker pages pass `overwriteExisting=true`, while the service default remains false and publishes through a flushed sibling temp without pre-deleting the destination.
- Settings use normalized, revisioned latest-wins atomic saves. Optional image/video fingerprints use `%LOCALAPPDATA%\Duplicates\cache\media-fingerprints-v1.json`; cache reuse validates canonical path, length, last-write UTC ticks, and sample schema.
- Scan defaults listen to `ISettingsService.SettingsChanged` when no scan is active. Skipped paths from `ScanResult.SkippedPaths` are surfaced on Results.
- `Duplicates.App.Tests` uses a test ProjectReference with `DisableWindowsAppSdkAutoInitialize=true` and a direct reference to the app Debug x64 DLL. This avoids Windows App Runtime registration requirements for service/ViewModel/source-contract tests.

## Verification notes

- Current engine test command: `dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore`.
- Current app ViewModel test command: `dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore`.
- Current app build command: `dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore`.
- Packaged debug launch needs Windows App Runtime 2.3 installed for the current user. Use `tools\Install-WindowsAppRuntime2.3.ps1` if `REGDB_E_CLASSNOTREG` appears before app code starts.
- For verified packaged Debug x64 UAT, rebuild and generate a fresh MSIX, unpack it with x64 MakeAppx to a unique ignored layout, delete only that layout's root `AppxBlockMap.xml`, register its manifest, verify Status/DevelopmentMode and exact InstallLocation, then launch the package AUMID. Direct registration from the normal build-output root is not a valid substitute.
- Unpackaged Debug x64 must be built explicitly with `-p:WindowsPackageType=None` and launched from `Duplicates\bin\x64\Debug\net10.0-windows10.0.22621.0\Duplicates.exe`; rebuild default MSIX afterward before packaged registration.

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
- Results-lista ei lyhennä hakemistopolkuja ViewModelissa. Lista saa rivittää polun enintään kahdelle riville, tooltip säilyttää koko tiedostopolun ja Preview näyttää valitun tiedoston koko polun ilman ellipsiä.

## 2026-08-08 - Data-cleaner completion contracts

- Main navigation exposes all 14 tools. Eleven share Analysis/Analysis Results; Duplicate files, EXIF remover, and Video optimizer have dedicated pages.
- Production XAML has exactly five palette colors, no custom control templates, no layout-element tap handlers, native virtualizing result ListViews, polite live status, and icon-only accessible names/tooltips.
- Direct `WrapGrid` is forbidden as general layout; it is valid only inside `ItemsPanelTemplate`, while general wrapping layout uses `VariableSizedWrapGrid`.
- `AccentButtonStyle` is reserved for Start scan, Start analysis, Clean images, and Optimize videos.
- Results reset is confirm/cancel safe, coordinator-generation safe, replacement-session safe, and dialog-concurrency safe.
- Historical Task 20 Gate D passed in fresh UNP and PKG modes on Windows App SDK 1.8. Final 100,000-file Results completed responsively in 94.035 s UNP and 63.567 s PKG on one NTFS/NVMe host; these results do not verify the merged Windows App SDK 2.3 build or other machines.
