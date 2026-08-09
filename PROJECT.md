# Duplicates Project Reference

Last source review: 2026-07-30.

This is the comprehensive, source-backed reference for the current `C:\Dev\Duplicates` checkout. It is intended for code review, UI work, testing, security review, maintenance, onboarding, and implementation planning. The checkout was dirty during this review, and this document deliberately describes the files as they existed in the working tree, including uncommitted UI and Results-flow changes. It is not a description of commit `fc622ae3137588d814e90870d762d363b95b5a26` alone.

The implementation is authoritative. Historical documents such as `Duplicates_BuildSpec.md`, `Duplicates v1 Toteutussuunnitelma.md`, and `Duplicates v1 Remaining Work Plan.md` can explain intent, but they do not override current source, tests, project files, manifests, resources, or tool configuration.

## Product Summary

Duplicates is a native Windows 11 WinUI 3 desktop app for finding byte-identical duplicate files in user-selected folders, reviewing the resulting groups, and deleting selected copies. The default delete target is the Windows Recycle Bin. The Results layer enforces that at least one file remains in every duplicate group.

### Implemented scope

- Exact-content duplicate detection for arbitrary file types.
- Optional file-type filtering by predefined categories or custom extensions.
- Recursive or non-recursive folder scanning.
- Minimum and maximum file-size filters.
- Hidden- and system-file exclusion.
- Parallel XxHash3 partial and full hashing.
- Byte-for-byte collision verification, enabled by default.
- Same-physical-file de-duplication for Windows hardlinks.
- Search and five sort modes over duplicate groups.
- Automatic survivor rules: newest, oldest, shortest path, or preferred folder.
- Manual selection with one-level selection undo.
- Per-file open, reveal, copy-path, preview, exclude, and delete actions.
- Batch deletion with progress and per-file failure details.
- Local settings for appearance, scan defaults, concurrency, and deletion policy.
- Packaged MSIX and unpackaged launch profiles.

### Explicit non-features and boundaries

- No near-duplicate, perceptual, image-similarity, semantic, filename-only, or path-only matching.
- No database, cloud backend, network synchronization, account, telemetry, analytics, or web view.
- No scan history, previous-folder history, result persistence, selection persistence, or delete history.
- No scheduled scan, background service, shell extension, or filesystem watcher.
- No localization layer; user-visible UI strings are English.
- No UI control for `ScanOptions.FollowSymlinks`; app-created options keep the engine default `false`.
- No per-scan hashing-concurrency control; concurrency comes from saved settings.
- No active-delete cancellation control. The token path exists in services and ViewModels, but the page passes `CancellationToken.None`.
- No in-repository CI workflow. There is no `.github` directory in the reviewed checkout.

## Source-of-Truth Layout

| Path | Responsibility |
| --- | --- |
| `Duplicates.Engine/` | UI-free scanner library, models, file walking, hashing, verification, and file identity. |
| `Duplicates/` | WinUI app, service composition, pages, ViewModels, settings, deletion, themes, manifests, and assets. |
| `Duplicates.Engine.Tests/` | xUnit scanner tests. |
| `Duplicates.App.Tests/` | xUnit app ViewModel tests using the built Debug x64 app assembly. |
| `Directory.Packages.props` | Central NuGet package versions. |
| `global.json` | Requested .NET SDK and roll-forward policy. |
| `Duplicates.slnx` | Four-project solution and Any CPU/x64 solution mappings. |
| `tools/` | Thin project wrappers, checker configuration, runtime installer, and signing-certificate helper. |
| `memory/MEMORY.md` | Project-maintenance memory; useful context, not runtime code. |
| `reports/` | Gitignored output location for shared Windows-check wrappers. |

The solution contains:

- `Duplicates.Engine/Duplicates.Engine.csproj`
- `Duplicates/Duplicates.csproj`
- `Duplicates.Engine.Tests/Duplicates.Engine.Tests.csproj`
- `Duplicates.App.Tests/Duplicates.App.Tests.csproj`

`Duplicates.slnx` defines `Any CPU` and `x64`. Its project mappings use x64 for all four projects, and the app is marked deployable for the x64 solution platform.

## Toolchain, Targets, and Packages

### SDK and frameworks

`global.json` requests:

- SDK `10.0.301`
- `rollForward: latestFeature`

This is not a strict guarantee that commands execute on exactly `10.0.301`. In the 2026-07-30 verification environment, `dotnet --version` resolved to `10.0.400-preview.0.26322.102`, and build/test commands printed `NETSDK1057` because that resolved SDK is a preview. Treat `10.0.301` as the repository request and the actual `dotnet --version` output as the execution-environment fact.

Project targets:

| Project | Target framework | Platforms/runtime |
| --- | --- | --- |
| `Duplicates.Engine` | `net10.0` | `AnyCPU;x64` |
| `Duplicates.Engine.Tests` | `net10.0` | `AnyCPU;x64` |
| `Duplicates` | `net10.0-windows10.0.22621.0` | `x64`, runtime identifiers `win-x64` |
| `Duplicates.App.Tests` | `net10.0-windows10.0.22621.0` | `x64`, runtime identifier `win-x64` |

All projects enable nullable reference types, implicit usings, and C# language version `14.0`. The app and app tests set `TargetPlatformMinVersion` to `10.0.22000.0`.

### Central package versions

`Directory.Packages.props` enables `ManagePackageVersionsCentrally=true`. Do not add versions to individual `.csproj` files.

| Package | Repository pin |
| --- | --- |
| `Microsoft.WindowsAppSDK` | `2.3.1` |
| `Microsoft.Windows.SDK.BuildTools` | `10.0.28000.2526` |
| `CommunityToolkit.Mvvm` | `8.4.2` |
| `CommunityToolkit.WinUI.Controls.SettingsControls` | `8.2.251219` |
| `System.IO.Hashing` | `10.0.10` |
| `coverlet.collector` | `10.0.1` |
| `Microsoft.NET.Test.Sdk` | `18.8.1` |
| `xunit.v3` | `3.2.2` |
| `xunit.runner.visualstudio` | `3.1.5` |

### Version freshness snapshot

Repository pins and upstream availability are separate facts. As checked from official Microsoft/NuGet sources on 2026-08-09:

| Dependency | Repository pin | Upstream available | Interpretation |
| --- | --- | --- | --- |
| Windows App SDK | `2.3.1` | `2.3.1` | Repository pin matched the current stable package checked. |
| `System.IO.Hashing` | `10.0.10` | `10.0.10` | Repository pin matched the current stable package checked. |
| `CommunityToolkit.Mvvm` | `8.4.2` | `8.4.2` | Repository pin matched the current stable package checked. |
| SettingsControls | `8.2.251219` | `8.2.251219` | Repository pin matched the current stable package checked; a newer prerelease does not replace this stable comparison. |

Official references checked:

- [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [Microsoft.WindowsAppSDK on NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/)
- [System.IO.Hashing on NuGet](https://www.nuget.org/packages/System.IO.Hashing/)
- [CommunityToolkit.Mvvm on NuGet](https://www.nuget.org/packages/CommunityToolkit.Mvvm/)
- [SettingsControls on NuGet](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.SettingsControls/)

Do not silently update these packages. A Windows App SDK update requires separate packaged and unpackaged launch tests, runtime installation/deployment tests, theme/control regression checks, and App.Tests compatibility verification. A hashing update requires scanner regression tests, including forced collision, changing-file, zero-byte, cancellation, and hardlink cases.

## Packaging, Runtime, and Assets

### App project behavior

`Duplicates/Duplicates.csproj`:

- Produces a WinExe.
- Enables WinUI with `UseWinUI=true`.
- Disables SDK-injected WinUI references with `WinUISDKReferences=false`.
- Enables MSIX tooling.
- uses `Duplicates/app.manifest`.
- Publishes ReadyToRun outside Debug, disables it in Debug, and never trims.
- Supports a `DisableWindowsAppSdkAutoInitialize=true` build mode used by App.Tests.

That test-only property disables Windows App SDK auto-initialization, bootstrap initialization, deployment-manager initialization, undocked reg-free WinRT initialization, and compatibility initialization.

### Package identity

`Duplicates/Package.appxmanifest` declares:

- Identity: `D8D23102-5301-46E2-A506-C486B1E205FB`
- Publisher: `CN=Finnvek`
- Package version: `1.0.0.0`
- Display name: `Duplicates`
- Publisher display name: `Finnvek`
- Device family: `Windows.Desktop`
- Minimum version: `10.0.22000.0`
- Maximum tested version: `10.0.22621.0`
- Restricted capability: `runFullTrust`

`Duplicates/app.manifest` declares the Windows 10 compatibility GUID and Per-Monitor V2 DPI awareness.

`Duplicates/Properties/launchSettings.json` provides:

- `Duplicates (Package)` using `MsixPackage`
- `Duplicates (Unpackaged)` using `Project`

### Runtime and signing helpers

`tools/Install-WindowsAppRuntime2.3.ps1` is pinned to `2.3.1`. It downloads Microsoft's official x64 runtime installer to `%TEMP%` and runs it quietly for the current user.

`tools/New-SigningCertificate.ps1`:

- Defaults the subject to `CN=Finnvek`, matching the package publisher.
- Creates a current-user code-signing certificate.
- Exports the public certificate to `certs\Duplicates.cer` by default.
- Prints the command for trusting it in `Cert:\CurrentUser\TrustedPeople`.
- Does not create or commit a PFX.

`certs/` is gitignored.

### Visual resources

`Duplicates/Assets/` contains:

| Asset | Reviewed file dimensions |
| --- | --- |
| `SplashScreen.scale-200.png` | 1240 x 600 |
| `Square150x150Logo.scale-200.png` | 300 x 300 |
| `Square44x44Logo.scale-200.png` | 88 x 88 |
| `Square44x44Logo.targetsize-24_altform-unplated.png` | 24 x 24 |
| `Square44x44Logo.targetsize-48_altform-lightunplated.png` | 48 x 48 |
| `Wide310x150Logo.scale-200.png` | 620 x 300 |
| `StoreLogo.png` | 50 x 50 |
| `LockScreenLogo.scale-200.png` | 48 x 48 |
| `AppIcon.ico` | Multi-resolution icon container used by `MainWindow`; do not infer all frames from a single decoded frame. |

The package manifest uses scale-qualified resource resolution, while `MainWindow` sets `Assets/AppIcon.ico` directly through `AppWindow.SetIcon`.

## Architecture

The design is a small, layered desktop application with manual singleton-style composition:

```text
App.OnLaunched
    |
    v
AppServices
    +--> SettingsService ------> %LOCALAPPDATA%\Duplicates\settings.json
    +--> ThemeService ---------> MainWindow/root theme and backdrop
    +--> ResultsStore ---------> in-memory ScanResult handoff
    +--> FileActionService ----> Windows shell / Recycle Bin / filesystem
    +--> ScanViewModel --------> DuplicateScanner
    +--> ResultsViewModel -----> ResultsStore + IFileActionService
    +--> SettingsViewModel ----> ISettingsService

DuplicateScanner
    -> FileWalker
    -> size buckets
    -> 64 KiB XxHash3
    -> full XxHash3
    -> physical-file identity de-duplication
    -> optional byte-for-byte verification
    -> ScanResult
```

There is no general-purpose dependency-injection container. `AppServices` constructs one instance of every service and ViewModel, and pages reuse those instances through `App.Current.Services`.

### Architectural boundaries

- `Duplicates.Engine` must remain free of WinUI, Windows App SDK, app service, ViewModel, and deletion dependencies.
- The app enters the engine through `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)`.
- `ResultsStore` is the in-memory boundary between scanning and result presentation.
- Results owns a canonical `_allGroups` list separately from the filtered/sorted visible `Groups` collection.
- Selection, totals, delete safety, undo, exclude, and post-delete mutation operate on `_allGroups`, not only visible groups.
- File deletion goes through `IFileActionService`.
- Only settings are persisted. Scanner results and result-page state are intentionally transient.

An architectural change includes moving responsibilities across these boundaries, adding a persistent store, changing the scanner entry point, or changing the canonical/visible result split. Repository instructions require updating `AGENTS.md` and `memory/MEMORY.md` when such a change is made.

## Startup, Window, and Navigation

Startup sequence:

1. `App` constructs `AppServices`.
2. `App.OnLaunched` awaits `SettingsService.LoadAsync()`.
3. It creates `MainWindow`, stores it in `App.MainWindow`, and activates it.
4. `MainWindow` extends content into a custom `TitleBar`.
5. It sets the icon, resizes to 1200 x 800, applies theme/backdrop, and subscribes to settings and result changes.
6. It selects and navigates to `ScanPage`.

`MainWindow.xaml` sets a 960 x 640 minimum root size. Its `NavigationView`:

- Uses `PaneDisplayMode="Auto"`.
- Uses a 260-pixel open pane.
- Hides the NavigationView's own pane toggle because the custom `TitleBar` owns the toggle.
- Uses the native settings item (`IsSettingsVisible="True"`).
- Enables Results only after `ResultsStore` contains a result.

`ShowResultsPage()` and `ShowScanPage()` synchronize selection and frame navigation. A completed scan raises `ScanViewModel.ScanCompleted`; `ScanPage` subscribes only while loaded and routes to Results.

## Engine Models

### `ScanOptions`

Defaults:

| Property | Default |
| --- | --- |
| `Folders` | Empty |
| `IncludeSubfolders` | `true` |
| `MinSizeBytes` | `1` |
| `MaxSizeBytes` | `long.MaxValue` |
| `TypeFilter` | `FileTypeFilter.All` |
| `IgnoreHiddenFiles` | `true` |
| `IgnoreSystemFiles` | `true` |
| `FollowSymlinks` | `false` |
| `VerifyByteByByte` | `true` |
| `MaxHashingConcurrency` | `null` |

`null` concurrency resolves to `Math.Max(1, Environment.ProcessorCount)`. Explicit values are restricted to `1`, `2`, `4`, or `8`; any other explicit value throws `ArgumentOutOfRangeException`.

Validation also rejects negative minimum size and a maximum below the minimum.

### File and result records

`FileEntry` carries full path, file name, normalized lowercase extension, directory, size, creation/modified UTC timestamps, and a mutable nullable `ContentHash`.

`DuplicateGroup` carries the confirmed `ContentHash`, one-file `SizeBytes`, and member files. `WastedBytes` is `SizeBytes * (Files.Count - 1)`.

`ScanResult` carries:

- `Groups`
- `TotalFilesScanned`
- `TotalDuplicateFiles`
- `TotalReclaimableBytes`
- `Elapsed`
- `SkippedPaths`

`TotalDuplicateFiles` means removable copies beyond one survivor per group, not total files contained in all duplicate groups.

`SkippedPath` contains `Path` and `Reason`. Skipped paths are returned as data rather than treated as a failed whole scan when the caught exception is considered skippable.

### Progress

Phases are:

1. `Enumerating`
2. `GroupingBySize`
3. `PartialHashing`
4. `FullHashing`
5. `Verifying`
6. `Done`

`ScanProgress` includes discovered/processed file counts, processed/total bytes, and current path. `ScanProgressReporter` serializes reports under a lock and throttles non-forced reports to one per 100 ms. Grouping, verifying initialization, and done reports are forced.

## File-Type Filtering

`FileTypeFilter` supports:

- `All`
- `Categories`
- `CustomExtensions`

Normalization trims whitespace, removes leading `*`, lowercases invariantly, ensures a leading dot, removes empty values, and uses case-insensitive sets. Extensionless files match only `All`.

Current category extensions:

- Images: `.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, `.tiff`, `.tif`, `.webp`, `.heic`, `.heif`, `.raw`, `.cr2`, `.nef`, `.arw`, `.dng`, `.svg`, `.ico`, `.psd`
- Video: `.mp4`, `.mkv`, `.mov`, `.avi`, `.wmv`, `.flv`, `.webm`, `.m4v`, `.mpg`, `.mpeg`, `.3gp`, `.ts`
- Audio: `.mp3`, `.flac`, `.wav`, `.aac`, `.ogg`, `.m4a`, `.wma`, `.opus`, `.aiff`, `.alac`
- Documents: `.pdf`, `.doc`, `.docx`, `.xls`, `.xlsx`, `.ppt`, `.pptx`, `.txt`, `.rtf`, `.odt`, `.ods`, `.odp`, `.epub`, `.md`, `.csv`
- Archives: `.zip`, `.rar`, `.7z`, `.tar`, `.gz`, `.bz2`, `.xz`, `.iso`, `.cab`
- Code: `.cs`, `.js`, `.ts`, `.py`, `.java`, `.kt`, `.cpp`, `.h`, `.html`, `.css`, `.json`, `.xml`, `.yml`, `.sql`

Preview image detection duplicates the Images extension list in `ResultsViewModel.IsImageExtension`. If image categories change, reviewers must decide whether preview classification must change too; these are currently two sources of truth.

## Enumeration and File Identity

`FileWalker.Walk` processes every nonblank folder:

1. Normalize with `Path.GetFullPath`.
2. Report invalid paths as skipped.
3. Report a missing directory as `Folder does not exist.`
4. Enumerate files with recursion following `IncludeSubfolders`.
5. Set `IgnoreInaccessible=true`.
6. Skip Hidden, System, and ReparsePoint attributes according to options.
7. Normalize each path and de-duplicate overlapping-folder results case-insensitively.
8. Recheck file attributes.
9. Apply size and extension filters.
10. Capture metadata in `FileEntry`.

Caught skippable exceptions are `IOException`, `UnauthorizedAccessException`, `ArgumentException`, `NotSupportedException`, and `PathTooLongException`.

`FileIdentityReader.GetBestEffortIdentity` opens a file and calls Win32 `GetFileInformationByHandle`. A successful identity consists of volume serial number plus high and low file index. On caught I/O/access/path failures it falls back to an uppercase full-path identity. This prevents two hardlinks to one physical file from being counted as reclaimable copies in normal Windows operation.

The engine has no UI dependency, but this identity implementation is Windows-specific because it P/Invokes `kernel32.dll`.

## Hashing and Duplicate Scanner Funnel

`FileHasher` uses `System.IO.Hashing.XxHash3`.

Constants and stream behavior:

- Partial prefix: 64 KiB.
- Rented buffer: 1 MiB from `ArrayPool<byte>.Shared`.
- File access: read.
- File sharing: read/write/delete.
- File options: asynchronous and sequential scan.
- Stream length is checked against expected size before and after hashing.
- Rented buffers are returned in `finally`.

`DuplicateScanner.ScanAsync` performs:

1. Validate options and cancellation.
2. Enumerate files.
3. Group by exact size and discard singleton size buckets.
4. Partial-hash each remaining candidate up to 64 KiB.
5. Group by `(size, partial hash)` and discard singleton groups.
6. Treat the partial hash as the content hash for files at or below 64 KiB.
7. Full-hash larger surviving candidates in parallel.
8. Group by `(size, full content hash)`.
9. De-duplicate same physical files.
10. If enabled and size is nonzero, compare files byte-for-byte and split hash-collision groups.
11. Materialize groups with members sorted by path.
12. Sort groups by descending reclaimable bytes, then first filename.
13. Return totals, elapsed time, and skipped paths.

Hash parallelism is controlled through `Parallel.ForEachAsync`. Hash failures considered skippable are added to a thread-safe `ConcurrentBag<SkippedPath>`. Cancellation is not converted into a skipped path by the scanner's skippable predicate.

Byte verification uses two 1 MiB arrays and compares a reference file against remaining candidates. It rechecks both stream lengths before reading and compares equal-size chunks until EOF. Zero-byte hash groups skip byte verification but can be returned when the minimum size is explicitly set to zero.

### Scanner correctness invariants

- Unique-size files must be eliminated before hashing.
- Unique partial-hash candidates must be eliminated before full hashing.
- Files at or below 64 KiB must not be redundantly full-hashed.
- Default results must remain exact byte matches, not merely equal hashes.
- Hardlinks to one physical file must not become reclaimable duplicates.
- A skippable failure for one candidate must not normally discard the whole scan.
- Cancellation must continue to propagate.
- Default minimum size excludes zero-byte files.
- Group reclaimable bytes must remain `(count - 1) * size`.

## Scan ViewModel and Page

`ScanViewModel` owns transient scan setup and progress:

- Folder collection.
- Recursion.
- Min/max byte text.
- File-type mode and category toggles.
- Custom extension text.
- Hidden/system/byte-verification flags.
- Running state and cancellation source.
- Phase, counts, bytes, elapsed, ETA, current path, and progress-bar state.

`ResetFromSettings()` copies only default minimum size, hidden/system flags, and byte verification. Include-subfolders, max size, selected filter, categories, custom extensions, and folders are not persisted defaults.

Settings changes reset scan defaults only while no scan is active. This prevents a live scan's visible options from being overwritten.

`AddFolder`:

- Rejects blank and nonexistent paths.
- normalizes to full path.
- de-duplicates case-insensitively.

The page adds folders with the Windows App SDK `FolderPicker` or drag/drop of `StorageFolder` items. The picker starts at Documents, uses list view, and selects one folder per invocation.

`BuildScanOptions`:

- Parses invariant integer bytes.
- Clamps parsed negatives to zero.
- Falls back to saved minimum for invalid minimum text.
- Treats blank maximum as `long.MaxValue`.
- Falls back to `long.MaxValue` for invalid maximum text.
- Reads concurrency from current settings.
- Leaves `FollowSymlinks` at its engine default.

If a parsed maximum is below the minimum, engine validation produces the user-visible argument error.

`StartScanAsync` clears status, creates a cancellation source, builds options, reports progress, stores success in `ResultsStore`, and raises `ScanCompleted`. It displays cancellation or caught I/O/access/argument errors. In `finally` it disposes the token source and clears running state.

Progress UI:

- Enumeration and size grouping are indeterminate.
- Hash phases become determinate when total bytes are known.
- ETA is elapsed time multiplied by remaining/processed byte ratio.
- Current path is displayed with the path text style.
- Scan cancellation is exposed as a button and calls the current token source.

### Scan layout

- Shared maximum width: 1040.
- Header padding: `32,24,32,20`.
- Content padding: `32,0,32,32`.
- Header contains title/subtitle and a right-aligned Start scan button.
- Folder card accepts drop, lists folders up to 220 pixels tall, and has an accessible icon-only remove button.
- Options card uses two equal columns: type/recursion on the left and byte-size fields/presets on the right.
- Advanced options are inside an Expander.
- The active progress card replaces setup content.

There are no adaptive `VisualState` rules. The two-column option grid depends on the app's 960-pixel minimum root width.

## Results State and ViewModel

### Canonical versus visible collections

`ResultsViewModel._allGroups` is canonical. `Groups` is rebuilt from it after search, sort, exclusion, and deletion.

Canonical calculations include:

- Group and duplicate totals.
- Reclaimable bytes.
- Selected file/group counts.
- Selected bytes.
- Delete button text and `CanDelete`.
- Selected file snapshot.
- Delete result mutation.

Therefore a group hidden by search can still contain selected files, contribute to totals, and be deleted.

### New-result behavior

`ReloadFromCurrentResult()`:

1. Clears visible/canonical groups, undo state, selected preview file, and preview-pane state.
2. Creates `DuplicateGroupViewModel` and `DuplicateFileViewModel` wrappers.
3. Subscribes to each file's selection changes.
4. If groups exist, automatically applies `Keep newest`.
5. Rebuilds search/sort output.
6. Sets result status.

The current dirty checkout therefore starts with every file except the selected survivor marked for deletion. The automatic operation captures the prior empty selection, so Undo is immediately available and restores an empty selection.

### Survivor rules

`DuplicateGroupViewModel` implements:

- Newest: descending `ModifiedUtc`, then shortest full path, then case-insensitive path.
- Oldest: ascending `ModifiedUtc`, then shortest full path, then case-insensitive path.
- Shortest path: path length, then case-insensitive path.
- Preferred folder: newest file under the normalized preferred folder; otherwise newest overall.

The preferred-folder containment check requires a directory boundary, not a raw prefix match.

Each rule clears the group first, chooses one survivor, and selects every other member. Batch rules save one path-keyed snapshot before applying. Undo restores that one snapshot and then clears it; there is no multi-step history.

### Selection invariant

The central safety invariant is:

> At least one file must remain in every duplicate group.

Enforcement points:

- `DuplicateFileViewModel.IsSelected` asks its parent group before accepting a transition to selected.
- `DuplicateGroupViewModel.CanSelectForDeletion` requires another currently unselected file.
- Automatic rules choose a survivor before selecting others.
- `ResultsViewModel.CanDelete` requires selected files and `SelectedCount < Files.Count` for every canonical group.
- `DeleteSelectedAsync` repeats the invariant immediately before calling the file service.

Rejected checkbox selection raises a property notification so the UI returns to the actual value.

`ClearSelection` operates on every canonical group. Manual file selection changes `SelectionRuleText` to `Manual selection`.

### Search and sort

Search is case-insensitive and matches any member's filename or full path. It does not reset selection.

Sort indices:

| Index | Sort |
| --- | --- |
| 0 | Descending reclaimable bytes |
| 1 | Descending per-file size |
| 2 | Descending member count |
| 3 | First member filename |
| 4 | First member extension |

### Group and file display

All member rows are exposed through `DuplicateGroupViewModel.Files`. The prior lazy `VisibleFiles`/expanded-group behavior is not present in the current checkout.

Display details:

- Group display name is the first member filename.
- Group summary shows identical member count and reclaimable bytes.
- Selected summary shows selected count and bytes.
- File row shows filename, shortened directory path, modified/created dates, and size.
- Directory paths longer than 72 characters retain a 28-character head and at least a 36-character tail, separated with `...`.
- Full paths remain available as row tooltips.
- Dates convert UTC to local time and format with current culture using `g`.
- `ByteFormatter` uses binary 1024 divisions but labels units `KB`, `MB`, `GB`, and `TB`.

Exclude removes a file only from in-memory results. It does not delete the file. A group is removed when fewer than two members remain. If the excluded row was previewed, preview selection is cleared.

### Preview

- The preview pane starts closed after construction and after each new result.
- Selecting a file row sets `SelectedFile` and automatically opens the pane.
- The toolbar can open/close the pane; opening without a selected file shows placeholder metadata.
- The pane has fixed width 360.
- Recognized image extensions create a `BitmapImage` with `DecodePixelWidth=512`.
- Other files show a file glyph.
- Details include size, created/modified date, and the source group's reclaimable size.
- Preview is metadata/image-only; there is no video, audio, document, or text content renderer.

### Results layout

- Page padding is a direct `24`, not a shared page token.
- Header contains title, summary, delete status/progress/failures, skipped-path details, search/sort, and the action toolbar.
- The action toolbar contains rule flyout, clear, undo, preview toggle, and batch delete.
- Results use an outer `ListView` with `ItemsStackPanel`.
- Each group renders every file through a nested `ItemsControl`.
- Each row has checkbox, metadata, KEEP/DELETE state, and five icon-only actions.
- Preview is a second fixed-width column.

The outer list can virtualize group containers, but the nested `ItemsControl` eagerly creates all member rows for a realized group. Very large hardlink-independent duplicate groups are a UI performance risk.

## Delete Flow

### Batch delete

`ResultsPage.Delete_Click`:

1. Requires a current window and `CanDelete`.
2. Optionally confirms file count, group count, selected bytes, and Recycle Bin/permanent target.
3. Uses a dialog with Delete, Cancel, and Cancel as the default.
4. Calls `DeleteSelectedAsync(CancellationToken.None)`.

### Single-file delete

Every file row has a Delete icon. `DeleteFile_Click`:

1. Resolves the row's `DuplicateFileViewModel`.
2. Rejects while another delete is active.
3. Optionally confirms filename, full path, size, and deletion mode.
4. Calls `DeleteFileAsync(file, CancellationToken.None)`.

`DeleteFileAsync` permits deletion only when the file belongs to a canonical group with more than one remaining member. It does not require the file to be selected. This is intentionally separate from batch-selection state.

### File service

`FileActionService.DeleteAsync` runs on `Task.Run` and chooses:

- `RecycleOption.SendToRecycleBin` for the default.
- `RecycleOption.DeletePermanently` when explicitly configured.

It calls `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` with `UIOption.OnlyErrorDialogs`, reports `DeleteProgress` after every attempted file, and accumulates caught per-file failures.

Caught per-file exception types are `IOException`, `UnauthorizedAccessException`, `FileNotFoundException`, and `OperationCanceledException`. The loop-level token check occurs before the per-file `try`, so token cancellation there aborts the operation rather than creating a failure entry.

After service completion, `ResultsViewModel`:

- Derives successful paths as requested files without a matching failure.
- Clears preview if the previewed file was deleted.
- Removes successful files from all canonical groups.
- Keeps failed files and their existing selection.
- Removes groups with fewer than two members.
- Updates status, progress, failure details, visible results, and totals.

Open uses shell execution on the path. Reveal starts `explorer.exe` with `/select,"path"`. Copy path uses the Windows clipboard.

## Settings and Persistence

Only `AppSettings` is persisted, at:

```text
%LOCALAPPDATA%\Duplicates\settings.json
```

Defaults:

| Setting | Default |
| --- | --- |
| Theme | `System` |
| Backdrop | `MicaAlt` |
| Default minimum bytes | `1` |
| Verify byte-by-byte | `true` |
| Ignore hidden files | `true` |
| Ignore system files | `true` |
| Hashing concurrency | `null` / Auto |
| Deletion mode | `RecycleBin` |
| Confirm before delete | `true` |

`SettingsService` uses `JsonSerializerDefaults.Web` and indented output.

Load:

- Missing file preserves defaults.
- A non-null deserialized record replaces `Current`.
- Successful replacement raises `SettingsChanged`.

Save:

- Creates the parent directory.
- Recreates the JSON file with `File.Create`.
- Serializes the provided record.
- Replaces `Current`.
- Raises `SettingsChanged`.

`SettingsViewModel` mirrors settings and fire-and-forgets `SaveAsync()` on each property change outside its guarded load operation. Saves are not debounced or serialized by the ViewModel.

Concurrency mapping:

| UI index | Saved value |
| --- | --- |
| 0 | `null` / Auto |
| 1 | `1` |
| 2 | `2` |
| 3 | `4` |
| 4 | `8` |

Invalid default-minimum text preserves the current saved minimum when building the next record. Parsed negatives clamp to zero.

The Settings page uses Community Toolkit `SettingsExpander` and `SettingsCard` controls:

- Appearance expander is open by default.
- Scanning defaults and Deletion are collapsed by default.
- Permanent mode reveals a warning InfoBar.
- About text uses the app assembly version and reports `.NET 10` and `Windows App SDK 2.3`.

## Theme and Visual System

`App.xaml` merges:

1. `XamlControlsResources`
2. `Themes/Colors.xaml`
3. `Themes/Styles.xaml`

All raw hex colors in app source are centralized in `Colors.xaml`. The current palette is deliberately small:

| Palette token | Value |
| --- | --- |
| `PaletteBlack` | `#000000` |
| `PaletteBackground` | `#181818` |
| `PaletteSurface` | `#242424` |
| `PaletteAccentRed` | `#D72323` |
| `PaletteWarmWhite` | `#F5EDED` |

### Semantic color mapping

- `AccentRedBrush`, focus, progress, NavigationView selection, focused text controls, checkbox/toggle/radio checked states, and WinUI system accent resources all resolve to `#D72323`.
- `DangerBrush` also resolves to red.
- `SuccessBrush` currently also resolves to red; success does not have an independent green token.
- Dark base is `#181818`, card is `#242424`, primary/secondary/tertiary text all resolve to `#F5EDED`.
- Light base/card is `#F5EDED`, primary text is black, secondary/tertiary text is `#242424`.
- Dark card hover/selected equal the ordinary surface; light card hover/selected equal the ordinary warm-white card. These names do not currently imply a visible state difference.
- `AccentRedPressedBrush` and `AccentRedSubtleBrush` both resolve to the surface color.

This is a five-color system, not the older mint/green multi-semantic palette. Do not reintroduce `AccentMint*` references or inline view hex values.

### Shared metric and style resources

| Resource | Value |
| --- | --- |
| `CardCornerRadius` | 8 |
| `SpacingSmall` | 8 |
| `SpacingMedium` | 12 |
| `SpacingLarge` | 16 |
| `SpacingExtraLarge` | 24 |
| `ContentMaxWidth` | 1040 |
| `SettingsContentMaxWidth` | 1000 |
| `FolderListMaxHeight` | 220 |
| `PageHeaderPadding` | `32,24,32,20` |
| `PageContentPadding` | `32,0,32,32` |

Button styles use a 36-pixel minimum height, `16,6` padding, and 4-pixel radius:

- `PrimaryButtonStyle`: filled red.
- `DangerButtonStyle`: filled danger red.
- `SecondaryButtonStyle`: transparent with red outline/text.
- `WhiteOutlineButtonStyle`: secondary style with primary-text outline/text.
- `RemoveFolderButtonStyle`: secondary style with primary-text border.

`CardBorderStyle` uses card background, subtle stroke, one-pixel border, 8-pixel radius, and 20-pixel padding.

`PathTextBlockStyle` uses Consolas, tertiary text, and character ellipsis.

Some view metrics remain inline, notably Results page padding, row padding, preview width, preview/image/font sizes, and separator heights. "Centralize design tokens" review should distinguish existing debt from new duplication.

### Theme and backdrop

`ThemeService.Apply` maps:

- System -> `ElementTheme.Default`
- Light -> `ElementTheme.Light`
- Dark -> `ElementTheme.Dark`
- Mica -> `MicaKind.Base`
- MicaAlt -> `MicaKind.BaseAlt`
- Acrylic -> `DesktopAcrylicBackdrop`
- Solid -> no system backdrop

Settings changes immediately reapply both theme and backdrop to the window root.

## Accessibility and Interaction Review

Current explicit accessible names:

- Remove folder.
- Verify byte-by-byte, ignore hidden, ignore system, and confirm-delete toggles.
- Toggle preview and collapse preview.
- Select file for deletion.
- Open file, reveal in folder, copy path, delete file, and exclude from group.

Icon-only result actions also have tooltips. Full folder/file paths are available through tooltips where the displayed path can trim.

Text buttons such as Start scan, Add folder, Cancel, selection actions, and New scan expose their visible labels.

Review-sensitive accessibility boundaries:

- File preview selection is wired to `Border.Tapped`; the row container is not explicitly made keyboard-focusable and has no key activation handler.
- KEEP and DELETE state is communicated by text as well as color.
- The five-color palette should be contrast-tested in both dark and light modes; source review alone does not prove rendered contrast.
- The 960 x 640 minimum and fixed/two-column layouts need manual testing at minimum size, high DPI, text scaling, and keyboard-only navigation.
- No automated UI/accessibility test project exists.
- No localized resource strings exist for screen-reader language switching.

## Tests

### Engine tests: 16

`Duplicates.Engine.Tests/DuplicateScannerTests.cs` covers:

- Exact duplicates and wasted-size sorting.
- Different head bytes.
- Same 64 KiB prefix with different tails.
- Locked candidate skipping.
- Invalid/missing folder skipping.
- Invalid concurrency.
- Injected hash failure.
- Hardlink de-duplication.
- Forced hash collision split by byte verification.
- Default zero-byte exclusion.
- Explicit zero-byte duplicate inclusion.
- Custom extension normalization/filtering.
- Category filter sets.
- Overlapping-folder canonical path de-duplication.
- Already-cancelled operation.
- Progress throttling and final Done report.

`Duplicates.Engine/Properties/AssemblyInfo.cs` exposes internals to `Duplicates.Engine.Tests`, allowing injected `IFileHasher` implementations without widening the production API.

### App tests: 22

`Duplicates.App.Tests` contains:

- 16 Results tests: default keep-newest selection and undo, deterministic tie-break, search/sort selection preservation, hidden totals, batch delete success/failure mutation, single-file delete success/failure, undo, exclude, progress/failure details, skipped paths, metadata/path formatting, preview toggle, and preview auto-open.
- 3 Scan tests: settings refresh while idle, no settings overwrite while scanning, and conditional filter UI visibility.
- 3 Settings tests: permanent-delete warning, size presets, and assembly version in About.

`FakeSettingsService` raises `SettingsChanged`; `FakeFileActionService` exposes configurable summaries and a progress hook.

### App-test assembly arrangement

`Duplicates.App.Tests.csproj`:

- Uses a non-output project reference to the app with `DisableWindowsAppSdkAutoInitialize=true`.
- Directly references the built app Debug x64 `Duplicates.dll`.
- Directly references `Microsoft.WinUI.dll` and `WinRT.Runtime.dll` from the same app output folder.
- References `Duplicates.Engine` normally.

Build the app first if those Debug x64 files are absent or stale. This arrangement tests ViewModels without initializing a registered Windows App Runtime, but it does not constitute packaged app launch or XAML UI testing.

## Current Verification Snapshot

Direct verification reported for this checkout on 2026-07-30:

| Check | Result |
| --- | --- |
| `dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore` | Passed; 0 warnings, 0 errors |
| `dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore` | Passed; 16/16 |
| `dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore` | Passed; 22/22 |
| `dotnet format Duplicates.slnx --verify-no-changes` | Exit 0 |

Execution-environment qualification:

- `dotnet --version` was `10.0.400-preview.0.26322.102`.
- Build/test output included the `NETSDK1057` preview-SDK message.
- These results do not prove execution under exactly SDK `10.0.301`.
- They do not prove packaged launch, unpackaged launch, MSIX signing/install, Recycle Bin behavior, permanent deletion, folder picker, drag/drop, shell open/reveal, clipboard, theme/backdrop rendering, high-DPI layout, screen-reader behavior, or large-data UI performance.

Standard commands:

```powershell
dotnet restore Duplicates.slnx
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet format Duplicates.slnx --verify-no-changes
```

## Local Check Wrappers and CI

Project wrappers are one-line delegates to:

```text
C:\Dev\Windows-check\tools\InvokeWindowsProjectCheck.ps1
```

| Wrapper | Check argument |
| --- | --- |
| `tools/lc.ps1` | `lint` |
| `tools/sc.ps1` | `security` |
| `tools/bc.ps1` | `build` |
| `tools/tc.ps1` | `test` |
| `tools/dc.ps1` | `dependency` |
| `tools/ss.ps1` | `secret` |
| `tools/ql.ps1` | `codeql` |
| `tools/db.ps1` | `dependabot` |

`tools/windows-check.config.psd1` configures:

- Solution `Duplicates.slnx`.
- Engine tests in Debug.
- App tests in Debug with `Platform=x64`.
- App build in Debug with `Platform=x64`.

The shared checker entry point existed at review time, but its implementation is outside this repository. Wrapper behavior beyond the thin local scripts and local configuration depends on that external checkout.

Repository instructions reserve lint/security wrapper execution for the user. Reports go to gitignored `reports/`. Do not route this WinUI/.NET project through Android/Gradle, ktlint, detekt, Compose, or MobSF tooling.

There is no checked-in GitHub Actions, Azure Pipelines, or other CI configuration. Local passes are not evidence of a remote quality gate.

## Security and Data-Safety Properties

Positive controls:

- Fully local data path; no application network client is present.
- Exact byte verification is enabled by default after non-cryptographic hashing.
- Recycle Bin is the default deletion target.
- Permanent deletion is opt-in and produces a warning in Settings.
- Confirmation is enabled by default.
- Selection and delete guards keep a survivor in each group.
- Per-file delete failures do not remove failed rows from Results.
- Scanner shares files for read/write/delete to reduce lock interference and checks expected length.
- Package has only the `runFullTrust` restricted capability, with no broad library capability declarations in the package manifest.

Review cautions:

- XxHash3 is non-cryptographic. If byte verification is disabled, equal size and XxHash3 are accepted as duplicate identity.
- The app is full-trust and can permanently delete any selected path accessible to the user.
- Files are not rehashed or byte-compared immediately before deletion. A file can change after scanning; this is a time-of-check/time-of-use boundary.
- Length checks do not detect same-length content changes during hashing.
- Results and selection describe a scan snapshot, not a live filesystem truth.
- Settings JSON load/deserialization errors are not caught in `App.OnLaunched`; a corrupt file can prevent normal startup.
- Settings saves are fire-and-forget, not debounced, and not serialized explicitly; rapid edits can overlap.
- Page delete handlers are `async void` and do not catch service exceptions.
- `FileWalker` catches errors when creating the enumerable and per-file metadata, but an unexpected exception raised while advancing the lazy directory enumeration may escape.
- Delete cancellation has no UI affordance, and `OperationCanceledException` can be either a per-file failure or an operation abort depending on where it is raised.
- Preview constructs a file URI from the selected path; decode failures are not handled in `PreviewImage`.
- Shell open/reveal and clipboard operations have no user-visible exception handling.
- The runtime installer downloads and executes package installation from a hardcoded external URL; validate source/version before maintenance changes.

## Known UI and Maintainability Risks

- Results eagerly renders all files in each realized group; large groups can create many controls.
- Results header/actions have no responsive visual states and can crowd at minimum width or high text scale.
- Preview is fixed at 360 pixels.
- Scan options are fixed to two columns.
- Dark/light semantic roles collapse to very few colors; hover, selected, success, secondary text, and tertiary text often have no independent visual distinction.
- `SuccessBrush` is red, so "success" naming does not describe its visual semantics.
- Image extension knowledge exists both in Engine categories and Results preview classification.
- `DuplicateGroup.Source.WastedBytes` reflects the immutable scan-time member count, while Results totals correctly recompute from current `Files`. `WastedText` and preview group details still read `Source.WastedBytes`, so exclusion/deletion can leave those per-group strings stale until the group disappears.
- `FileActionService` depends on app ViewModel types in its interface. This is acceptable inside the app layer but makes the service harder to reuse independently.
- ViewModels subscribe to long-lived service events and are themselves long-lived singletons; this currently matches `AppServices`, but a move to transient pages/ViewModels would require unsubscription/lifetime work.
- About text hardcodes `.NET 10` and `Windows App SDK 2.3`; it is not derived from package metadata.

## Change-Impact Map

| Change | Inspect/update together |
| --- | --- |
| Scanner options/defaults | `ScanOptions`, `ScanViewModel.BuildScanOptions`, Scan page controls, `AppSettings`, Settings page/ViewModel if persistent, engine/app tests |
| File categories | `FileTypeFilter.CategoryExtensions`, Scan category UI, engine filter tests, `ResultsViewModel.IsImageExtension` when preview-related |
| Scanner stage/progress | `DuplicateScanner`, `ScanPhase`, `ScanProgress`, `ScanProgressReporter`, `ScanViewModel.UpdateProgress`, progress tests/UI |
| Selection rule | `DuplicateGroupViewModel`, `ResultsViewModel.ApplySelectionRule`, Results flyout, undo/default-selection tests |
| Result group mutation | `_allGroups`, visible `Groups`, totals, selected preview, per-group display strings, deletion/exclude tests |
| Deletion | `IFileActionService`, `FileActionService`, delete models, `ResultsViewModel`, page confirmations, Settings deletion policy, tests |
| Settings | `AppSettings`, `SettingsService`, `SettingsViewModel`, Settings page, `ScanViewModel.SettingsChanged`, tests |
| Theme/color | `Colors.xaml`, `Styles.xaml`, every `ThemeResource` consumer, dark/light/manual accessibility checks |
| Navigation | `MainWindow.xaml`, `MainWindow.xaml.cs`, page lifetime/event subscriptions, Results availability |
| Package/runtime | central package file, app project, manifests, runtime installer, launch profiles, packaged/unpackaged smoke tests |
| App-test output path | app TFM/RID/configuration and the direct references in `Duplicates.App.Tests.csproj` |

## Code Review Checklist

### Architecture

- Does Engine remain free of UI/app dependencies?
- Is scanner behavior still implemented once, behind `DuplicateScanner.ScanAsync`?
- Are canonical Results state and visible filtered/sorted state still separate?
- Does a new persistent concept belong in settings, or does it violate the transient v1 boundary?
- Were all callers found before moving or renaming a symbol?
- If responsibilities/data flow changed, were `AGENTS.md` and `memory/MEMORY.md` updated?

### Scanner

- Are unique candidates removed at size and partial-hash stages?
- Are small files avoiding redundant full hashing?
- Does default exactness still include byte verification?
- Are hash collisions, hardlinks, zero bytes, changed files, inaccessible paths, overlapping folders, and cancellation covered?
- Do progress totals and phases remain meaningful and throttled?
- Is any new shared file buffer returned/disposed on all paths?

### Results and deletion

- Can any path select every member of a group?
- Does delete recheck immediately before service invocation?
- Do hidden selections remain counted after search?
- Do search and sort preserve object/selection identity?
- Does successful deletion remove only successful paths?
- Do failures remain visible and retain selection?
- Does exclusion update canonical groups, preview, totals, and per-group display?
- Does single-file delete preserve at least one physical path?
- Is confirmation text correct for count, bytes, and Recycle Bin/permanent mode?

### UI and accessibility

- Are raw colors confined to `Colors.xaml`?
- Can an existing token/style be reused instead of duplicating three or more properties?
- Does every icon-only action have an automation name and tooltip where useful?
- Are state differences communicated by text/icon, not color alone?
- Does the layout work at 960 x 640, high DPI, high text scale, keyboard-only, dark, and light?
- Are long paths both trimmed and discoverable in full?
- Does image preview retain a decode-size constraint?
- Does a new list avoid eager creation of unbounded child controls?

### Settings and persistence

- Is the default conservative?
- Are model, service, ViewModel, XAML, and tests updated together?
- Does the setting apply at the intended time, especially during an active scan?
- Are parsing, corrupt JSON, overlapping saves, and migration/default behavior considered?
- Does no non-settings state accidentally become persistent?

### Build, packages, and tests

- Is every package version centralized?
- Was app Debug x64 built before App.Tests?
- Were focused tests added before/with behavior changes?
- Did Engine 16/16 and App 22/22 remain green?
- Did `dotnet format Duplicates.slnx --verify-no-changes` pass?
- Was the actual resolved SDK recorded when it differs from the requested SDK?
- For Windows App SDK changes, were packaged and unpackaged runtime paths tested?
- Were local-wrapper results distinguished from direct commands and from absent remote CI?

## Unverified Runtime Acceptance

The following need explicit manual or environment-backed evidence before claiming release readiness:

- Packaged MSIX build, signing, registration, launch, update, and uninstall.
- Unpackaged framework-dependent launch.
- Runtime installer behavior with missing, same, newer, and corrupted runtime packages.
- Folder picker and drag/drop across normal, protected, network, removable, long, and reparse-point paths.
- Recycle Bin recovery and permanent-delete behavior on real files.
- Files modified, renamed, or deleted between scan and delete.
- Very large files, very large folder trees, and very large duplicate groups.
- High hashing concurrency under memory/I/O pressure.
- Theme/backdrop behavior on supported Windows versions and unsupported backdrop conditions.
- Keyboard, Narrator, high contrast, high text scale, touch, and minimum-window layout.
- Preview decode failures and uncommon image codecs.
- External Windows-check wrapper implementations and any remote security/dependency service.
- Windows App SDK 2.3.1 packaged and unpackaged runtime compatibility; restore, tests, and build verify the migration statically, but launch behavior remains unverified.
