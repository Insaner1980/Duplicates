# Duplicates Project Reference

Last full source review: 2026-08-08. Merge integration review: 2026-08-27.

This is the comprehensive, source-backed reference for the current Duplicates implementation after the data-cleaner tool expansion. It is intended for code review, UI work, testing, security review, maintenance, onboarding, and implementation planning. Claims below are grounded in current source, project files, manifests, resources, tests, and recorded verification; dated runtime evidence is kept separate from source contracts.

The implementation is authoritative. Historical specifications and implementation plans explain intent, but they do not override current source or tests.

The data-cleaner implementation is integrated with the newer package versions from `main`. The August 8–9 runtime and scanner results below are historical branch evidence, not verification of the Windows App SDK 2.5 build. Their ignored artifacts belonged to the removed worktree and are not part of this checkout.

## Product Summary

Duplicates is a native Windows 11 WinUI 3 desktop app with 14 local file-analysis and cleanup tools. Exact duplicates remain content-verified and survivor-protected. Similar-media results are review candidates rather than proof of byte identity. Destructive work is explicit, scoped to the initiating in-memory session, coordinated across the app, and uses the Windows Recycle Bin by default where deletion is supported.

### Implemented scope

- Duplicate files: byte-identical grouping with optional category/extension filters, XxHash3 funnel, byte verification, hardlink identity handling, survivor rules, move/delete/export, and exact-link replacement.
- Similar images: perceptual-hash candidates with preset thresholds, review grouping, cached fingerprints, preview, manual move/delete, and export.
- Similar videos: sampled-frame candidates with duration/codec evidence, preview, manual move/delete, cache, and explicit decode skips.
- Music duplicates: Windows metadata and duration matching; this is metadata matching, not acoustic fingerprinting.
- Empty folders, big files, empty files, and temporary files: flat storage findings with tool-specific validation immediately before supported actions.
- Invalid links: symbolic-link/junction inspection with entry-only removal semantics.
- Broken files: readability plus Windows-supported image/audio/video and ZIP validation; findings support revalidated move/delete but not rename.
- Bad extensions and bad names: review plus rename-only repair; these tools do not enable or authorize delete/move actions.
- EXIF remover: JPEG/TIFF copy or replace workflow with metadata policy, identity-aware replacement, rollback, and source preservation on failure.
- Video optimizer: H.264/AAC transcode profiles, rotation/PAR preservation, size gate, sibling `.mp4` publication with source preservation, cancellation, and transaction cleanup.
- Shared path scope with include roots, exclusions, picker/drop ingestion, and root containment checks.
- One app-wide operation coordinator, transient exact/analysis sessions, canonical result state, native WinUI controls, settings/cache management, and packaged plus unpackaged build paths.

### Explicit non-features and boundaries

- No semantic image search, acoustic fingerprinting, cloud comparison, or claim that similarity candidates are byte-identical.
- No database, cloud backend, network synchronization, account, telemetry, analytics, or web view.
- No scan/analysis history, result persistence, selection persistence, or delete history. Media fingerprints alone may be cached according to Settings.
- No scheduled scan, background service, shell extension, or filesystem watcher.
- No localization layer; user-visible UI strings are English.
- No automatic mutation for similar media, EXIF/video inspection findings, broken-file findings, or bad-name/extension findings.
- No stale canonical Move/Delete reconciliation: canonical updates and Move/Delete status require the initiating session to remain current, while reset callbacks clear only the captured still-current session.
- No in-repository CI workflow. There is no `.github` directory in the reviewed checkout.

## Source-of-Truth Layout

| Path | Responsibility |
| --- | --- |
| `Duplicates.Engine/` | UI-free exact scanner plus analysis models, path inventory, storage/name/link/broken-file analyzers, perceptual hashing, and similarity/music analyzers. |
| `Duplicates/` | WinUI app, composition, navigation, Windows providers, cache, session stores, operation coordinator, file actions/transactions, pages, ViewModels, settings, themes, manifests, and assets. |
| `Duplicates.Engine.Tests/` | xUnit tests for exact scanning, inventory, analyzers, similarity/media algorithms, identity, and cancellation. |
| `Duplicates.App.Tests/` | xUnit tests for app services/ViewModels, Windows-provider seams, native XAML contracts, canonical cross-tool state, and transaction behavior. |
| `Directory.Packages.props` | Central NuGet package versions. |
| `global.json` | Requested .NET SDK and roll-forward policy. |
| `Duplicates.slnx` | Four-project solution and Any CPU/x64 solution mappings. |
| `tools/` | Thin project wrappers, checker configuration, runtime installer, and signing-certificate helper. |
| `memory/MEMORY.md` | Project-maintenance memory; useful context, not runtime code. |
| `reports/` | Gitignored output location for shared Windows-check wrappers. |
| `docs/superpowers/` | Historical UI plans and data-cleaner specifications; implementation and current tests take precedence. |

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

This is not a strict guarantee that commands execute on exactly `10.0.301`: `latestFeature` permits a later feature band. During the 2026-08-27 merge verification, `dotnet --version` resolved to stable SDK `10.0.400`. Treat `10.0.301` as the repository request and the actual `dotnet --version` output as the execution-environment fact.

Project targets:

| Project | Target framework | Platforms/runtime |
| --- | --- | --- |
| `Duplicates.Engine` | `net10.0` | `AnyCPU;x64` |
| `Duplicates.Engine.Tests` | `net10.0` | `AnyCPU;x64` |
| `Duplicates` | `net10.0-windows10.0.22621.0` | `x64`, runtime identifiers `win-x64` |
| `Duplicates.App.Tests` | `net10.0-windows10.0.22621.0` | `x64`, runtime identifier `win-x64` |

All projects enable nullable reference types, implicit usings, and C# language version `14.0`. The app and app tests set `TargetPlatformMinVersion` to `10.0.22000.0`. Neither project hardcodes `SelfContained`; their evaluated .NET deployment mode is framework-dependent. The app tests target `win-x64` and must match the app's deployment mode for executable-reference validation.

### Central package versions

`Directory.Packages.props` enables `ManagePackageVersionsCentrally=true`. Do not add versions to individual `.csproj` files.

| Package | Repository pin |
| --- | --- |
| `Microsoft.WindowsAppSDK` | `2.5.1` |
| `Microsoft.Windows.SDK.BuildTools` | `10.0.28000.2705` |
| `CommunityToolkit.Mvvm` | `8.4.2` |
| `CommunityToolkit.WinUI.Controls.SettingsControls` | `8.2.251219` |
| `System.IO.Hashing` | `10.0.12` |
| `coverlet.collector` | `10.1.0` |
| `Microsoft.NET.Test.Sdk` | `18.10.1` |
| `xunit.v3.mtp-off` | `4.0.1` |
| `xunit.runner.visualstudio` | `4.0.0` |

The test projects use `xunit.v3.mtp-off` to retain VSTest execution and `coverlet.collector` coverage on .NET 10.

### Package update policy

Repository pins are source facts; whether they are still the latest upstream releases is an external, time-sensitive question and must be rechecked separately when dependency work is requested.

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

`tools/Install-WindowsAppRuntime2.5.ps1` is pinned to `2.5.1`. It downloads Microsoft's x64 runtime installer to `%TEMP%`, runs it with `--quiet`, and rejects a nonzero exit code.

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

The design is a layered, local desktop application with manual singleton-style composition:

```text
App.OnLaunched
    |
    v
AppServices
    +--> SettingsService ----------> %LOCALAPPDATA%\Duplicates\settings.json
    +--> ThemeService -------------> MainWindow/root theme and backdrop
    +--> PathScopeViewModel -------> shared include/exclude scope editor
    +--> AppOperationCoordinator --> one foreground scan/analysis/action/transform
    +--> ResultsStore -------------> transient ExactResultsSession
    +--> AnalysisSessionStore -----> transient AnalysisSession
    +--> MediaFingerprintCache ----> versioned local image/video fingerprint cache
    +--> FileAction/FileLink ------> shell, Recycle Bin, filesystem, links
    +--> IdentityFileTransactions -> staged replace/rollback for transforms
    +--> ScanViewModel ------------> DuplicateScanner
    +--> AnalysisViewModel --------> AnalysisService
    +--> Results ViewModels -------> canonical exact/flat/similarity state
    +--> EXIF/Video ViewModels ----> dedicated transactional services

DuplicateScanner
    -> FileWalker
    -> size buckets
    -> 64 KiB XxHash3
    -> full XxHash3
    -> physical-file identity de-duplication
    -> optional byte-for-byte verification
    -> ScanResult

AnalysisService
    -> FileInventoryBuilder(AnalysisScope)
    -> storage/name/link/broken analyzers
    -> Windows image/video/music providers where required
    -> flat PathFinding[] or grouped SimilarityGroup[]
    -> AnalysisResult
```

There is no general-purpose dependency-injection container. `AppServices` constructs one instance of every service and ViewModel, and pages reuse those instances through `App.Current.Services`.

### Architectural boundaries

- `Duplicates.Engine` must remain free of WinUI, Windows App SDK, app service, ViewModel, and deletion dependencies.
- Exact scanning enters the engine through `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)`.
- General tools enter through `IAnalysisService.RunAsync(ToolKind, AnalysisScope, ToolOptions, AnalysisRunOptions, IProgress<AnalysisProgress>?, CancellationToken)`. Engine analyzers consume UI-free scope, inventory, provider interfaces, and immutable result records.
- `PathScopeViewModel` is shared by setup/transform pages. Canonical include/exclude roots use `Path.GetFullPath`, path-boundary containment, and reparse-aware inventory rules; UI display strings are not engine authority.
- `ResultsStore` and `AnalysisSessionStore` are transient handoff boundaries. Move/Delete actions capture the initiating session; service-summary canonical reconciliation and completion status are ignored when that session is no longer current.
- Exact results own canonical `_allGroups`; flat analysis owns canonical path findings; similarity analysis owns canonical similarity groups/items. Search and sort rebuild visible projections without changing selection authority, totals, or preview identity.
- `IAppOperationCoordinator` permits one foreground operation. Main-window departure/close/reset dialogs share suppression and generation checks; reset callbacks clear only the captured still-current idle session.
- Deletion/move goes through `IFileActionService`; link replacement/removal goes through `IFileLinkService`; EXIF copy/replacement and video sibling publication use their dedicated services plus `IIdentityFileTransactions` commit/rollback seams.
- Only `AppSettings` and the optional media fingerprint cache are persisted. Result sessions and selections remain transient.

An architectural change includes moving responsibilities across these boundaries, adding a persistent store, changing the scanner entry point, or changing the canonical/visible result split. Repository instructions require updating `AGENTS.md` and `memory/MEMORY.md` when such a change is made.

## Startup, Window, and Navigation

Startup sequence:

1. `App` constructs `AppServices`.
2. `App.OnLaunched` awaits `SettingsService.LoadAsync()`.
3. It creates `MainWindow`, stores it in `App.MainWindow`, and activates it.
4. `MainWindow` extends content into a custom `TitleBar`.
5. It sets the icon, resizes to 1200 x 800, applies theme/backdrop, and subscribes to settings and window lifecycle changes. Long-lived result ViewModels observe the result stores.
6. It selects Duplicate files and navigates to `ScanPage`.

`MainWindow.xaml` sets a 640 x 480 minimum root size. Its `NavigationView`:

- Uses `PaneDisplayMode="Auto"`.
- Uses a 260-pixel open pane.
- Hides the NavigationView's own pane toggle because the custom `TitleBar` owns the toggle.
- Uses the native settings item (`IsSettingsVisible="True"`).
- Contains all 14 tools under Find duplicates, Clean storage, and Inspect and repair, plus the native Settings item.

Duplicate files, EXIF remover, and Video optimizer use dedicated pages. Selecting Duplicate files reopens `ResultsPage` while an exact session exists and otherwise opens `ScanPage`; the ordinary New scan button remains reachable in every exact-result state and uses the same guarded reset. The eleven shared-analysis tools use `AnalysisPage` and `AnalysisResultsPage`, while `ToolKind` and the captured `AnalysisSession` select the actual behavior. `ShowResultsPage()` routes completed exact scans. `ShowAnalysisResultsPage()` routes completed analysis. A running operation can continue after a confirmed page departure, but app close requests coordinator cancellation and waits for a safe transaction boundary. New scan/analysis confirmation preserves state on Cancel and clears only the captured still-current session on Confirm.

## Engine Models

### `ScanOptions`

Defaults:

| Property | Default |
| --- | --- |
| `Folders` | Empty |
| `Files` | Empty |
| `ExcludedPaths` | Empty |
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

`FileWalker.Walk` first normalizes the exclusion list. An exact excluded path is always rejected; an excluded directory also rejects descendants using a separator-aware path-boundary check rather than a raw prefix.

It enumerates every nonblank included folder with recursion following `IncludeSubfolders`, `IgnoreInaccessible=true`, and Hidden/System/ReparsePoint attribute skipping according to options. It then processes explicit `ScanOptions.Files`. Both sources pass through the same `TryAddFile` path: canonicalize, apply exclusions, require an existing file, de-duplicate through one case-insensitive seen-path set, recheck attributes, apply size/type filters, and capture `FileEntry` metadata. Overlapping folders and an explicit file therefore cannot create duplicate candidates.

Invalid included folder paths are recorded as skipped, and a missing directory is reported as `Folder does not exist.` Explicit missing files are ignored by the common existence check.

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

Byte verification rents one pair of 1 MiB arrays from `ArrayPool<byte>.Shared` for each hash group, reuses that pair across every candidate comparison, and returns both arrays in `finally`. Every comparison still opens fresh reference/candidate streams, rechecks both lengths, and compares equal-size chunks until EOF. Zero-byte hash groups skip byte verification but can be returned when the minimum size is explicitly set to zero.

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

- The shared `PathScopeViewModel` include folders/files, exclusions, recursion, and hidden/system flags.
- Double-valued minimum/maximum byte inputs.
- File-type mode and category toggles.
- Custom extension text.
- Byte-verification flag.
- Running state and cancellation source.
- Phase, counts, bytes, elapsed, ETA, current path, and progress-bar state.

`ResetFromSettings()` applies default minimum size, include-subfolders, hidden/system flags, and byte verification. Maximum size, selected filter, categories, custom extensions, and included/excluded paths are not persisted scan defaults.

Settings changes reset scan defaults only while no scan is active. This prevents a live scan's visible options from being overwritten.

The shared `PathScopeEditor` adds included/excluded folders and individual files through the Windows App SDK pickers or drag/drop. `PathScopeViewModel` canonicalizes full paths, rejects nonexistent or wrong-kind inputs, and de-duplicates case-insensitively; `FileTypeFilter` constrains file picker/drop input but does not remove folder selection.

`BuildScanOptions`:

- Splits shared included paths by `ScopePathKind` into `Folders` and explicit `Files`, and copies `ExcludedPaths` plus scope flags.
- Converts the two `NumberBox` double values through `ByteSizeInput.ToBytes`: NaN minimum falls back to the saved default, NaN maximum means `long.MaxValue`, infinity uses the supplied fallback, values at/above `long.MaxValue` saturate, and finite values truncate after clamping at zero.
- Builds the selected file-type filter, reads hashing concurrency from current settings, and leaves `FollowSymlinks` at its engine default.

If a parsed maximum is below the minimum, engine validation produces the user-visible argument error.

`StartScanAsync` clears status, creates a cancellation source, builds options, reports progress, stores success in `ResultsStore`, and raises `ScanCompleted`. It displays cancellation or caught I/O/access/argument errors. In `finally` it disposes the token source and clears running state.

Progress UI:

- Enumeration and size grouping are indeterminate.
- Hash phases become determinate when total bytes are known.
- ETA is elapsed time multiplied by remaining/processed byte ratio.
- Current path is displayed with the path text style.
- Scan cancellation is exposed as a button and calls the current token source.

### Scan layout

- Header maximum width: 1040; scan work-area maximum width: 680.
- Header padding: `32,24,32,20`.
- Content padding: `32,0,32,32`.
- Header contains title/subtitle and a right-aligned Start scan button.
- Shared `PathScopeEditor` accepts canonical folders/files plus exclusions and has named icon actions.
- At 1008+ epx the option areas use two columns; narrower states stack the work area without horizontal scrolling.
- Advanced options are inside an Expander.
- The active progress card replaces setup content.

Adaptive states start at 0, 641, and 1008 epx. The shell minimum is 640 x 480.

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

The current implementation therefore starts with every file except the selected survivor marked for deletion. The automatic operation captures the prior empty selection, so Undo is immediately available and restores an empty selection.

### Survivor rules

`DuplicateGroupViewModel` implements:

- Newest: descending `ModifiedUtc`, then shortest full path, then case-insensitive path.
- Oldest: ascending `ModifiedUtc`, then shortest full path, then case-insensitive path.
- Shortest path: path length, then case-insensitive path.
- Preferred folder: newest file under the normalized preferred folder; otherwise newest overall.

The preferred-folder containment check requires a directory boundary, not a raw prefix match.

Each rule clears the group first, chooses one survivor, and selects every other member. Batch rules save one path-keyed snapshot before applying. Undo restores that one snapshot and then clears it; there is no multi-step history.

Bulk rules and Undo suppress per-file totals refreshes while applying their selection loop, then publish one final totals refresh. This keeps 100,000-member groups linear instead of repeatedly rescanning the full canonical group for every changed file.

### Selection invariant

The central safety invariant is:

> At least one file must remain in every duplicate group.

Enforcement points:

- `DuplicateFileViewModel.IsSelected` asks its parent group before accepting a transition to selected.
- `DuplicateGroupViewModel.CanSelectForDeletion` requires another currently unselected file.
- `DuplicateFileViewModel.CanToggleDeletionSelection` disables the native checkbox for the sole remaining KEEP row while leaving selected rows and groups with multiple survivor candidates editable.
- Automatic rules choose a survivor before selecting others.
- `ResultsViewModel.CanDelete` requires selected files and `SelectedCount < Files.Count` for every canonical group.
- `DeleteSelectedAsync` repeats the invariant immediately before calling the file service.

The checkbox uses a normal TwoWay `IsSelected` binding. Illegal survivor input is prevented by native disabled state rather than by toggling the control and trying to coerce its local dependency-property value afterward.

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
- File rows show filename, the full directory path, modified/created dates, and size.
- Directory text may wrap to two lines without ViewModel shortening; full paths also remain available as row tooltips and untrimmed preview text.
- Dates convert UTC to local time and format with current culture using `g`.
- `ByteFormatter` uses binary 1024 divisions but labels units `KB`, `MB`, `GB`, and `TB`.

Exclude removes a file only from in-memory results. It does not delete the file. A group is removed when fewer than two members remain. If the excluded row was previewed, preview selection is cleared.

### Preview

- The preview pane starts closed after construction and after each new result.
- Selecting a file row sets `SelectedFile` and automatically opens the pane.
- The toolbar can open/close the pane; opening without a selected file shows placeholder metadata.
- The responsive `SplitView` uses 320 epx at small/medium widths and 360 epx at 1008+ epx; the pane overlays at 640 epx.
- Recognized image extensions create a `BitmapImage` with a 512-pixel width decode hint. The exact rendered preview surface is bounded to 512 pixels wide and 220 pixels high, so its rendered long side remains within 512 without setting both decode dimensions and distorting aspect ratio.
- Other files show a file glyph.
- Details include size, created/modified date, and the source group's reclaimable size.
- Preview is metadata/image-only; there is no video, audio, document, or text content renderer.

### Results layout

- Header contains title, summary, delete status/progress/failures, skipped-path details, search/sort, and a native `CommandBar`.
- Results use one grouped native `ListView` outside any outer page `ScrollViewer`, preserving item-container virtualization and keyboard selection.
- Preview selection is the native single-selection `ListView` state; delete selection remains the separate checkbox-backed `DuplicateFileViewModel.IsSelected` state.
- Each row exposes full path text/tooltip, KEEP or DELETE text in addition to color, and named/tooled icon actions.
- Visual states switch the preview `SplitView` among overlay, compact overlay, and inline layouts at 640/641/1008 epx thresholds.

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

Caught per-file exception types are `IOException` (including `FileNotFoundException`), `UnauthorizedAccessException`, `SecurityException`, and `NotSupportedException`. The loop-level token check occurs before the per-file `try`, so token cancellation there aborts the operation rather than creating a failure entry.

After service completion, `ResultsViewModel`:

- Uses `DeleteSummary.DeletedPaths` as the authoritative successful-path set when supplied, otherwise falls back to requested files without a matching failure.
- Clears preview when its row no longer belongs to a canonical duplicate group, including an untouched survivor after a two-member group collapses.
- Removes successful files from all canonical groups.
- Keeps failed files and their existing selection.
- Removes groups with fewer than two members.
- Updates status, progress, failure details, visible results, and totals.

Delete and move capture the initiating `ExactResultsSession`; both pre-dispatch validation and post-await reconciliation require that exact session to remain current. A successful action therefore cannot mutate a replacement scan session. When a two-file group collapses, preview is cleared unless the selected preview row still belongs to some canonical group. Export snapshots canonical rows. A successful native `FileSavePicker` result is the create/overwrite authority boundary: the page passes `overwriteExisting=true`, while non-picker `IResultExportService` callers retain the safe `false` default. Publication remains a flushed sibling-temp move, with no pre-delete race. Hard/symbolic-link replacement uses its own immutable exact-session snapshot and is unavailable outside exact results.

Open uses shell execution on the path. Reveal starts `explorer.exe` with `/select,"path"`. Copy path uses the Windows clipboard.

## Shared Scope and Analysis Pipeline

`PathScopeViewModel` is the canonical UI scope shared by exact scanning, general analysis, EXIF removal, and video optimization. It owns included folders/files, excluded paths, include-subfolder and hidden/system defaults, picker/drop normalization, duplicate suppression, and conversion to engine `AnalysisScope`. `PathScopeEditor` accepts both folders and individual files; the selected tool's `FileTypeFilter` constrains file picker/drop input.

`FileInventoryBuilder` runs in `Duplicates.Engine` and:

- canonicalizes roots with `Path.GetFullPath` and directory-separator-aware exclusion checks;
- records included roots, files, directories, reparse points, and skipped paths separately;
- does not traverse reparse points;
- honors include-subfolders plus hidden/system exclusions;
- reports throttled enumeration progress and checks cancellation throughout traversal.

`AnalysisService.RunAsync` validates each `ToolKind`/`ToolOptions` pair, builds one inventory on a background task, then dispatches to `LargeFileAnalyzer`, `EmptyFileAnalyzer`, `EmptyFolderAnalyzer`, `TemporaryFileAnalyzer`, `InvalidLinkAnalyzer`, `BrokenFileAnalyzer`, `BadExtensionAnalyzer`, `BadNameAnalyzer`, `SimilarImageAnalyzer`, `SimilarVideoAnalyzer`, or `MusicDuplicateAnalyzer`. Windows-specific decoding and metadata stay behind engine interfaces: `WindowsFileFormatProbe`, `WindowsImageSampleProvider`, `WindowsVideoSampleProvider`, and `WindowsMusicMetadataProvider`. Unsupported or unreadable items are returned as explicit skips rather than silently promoted to findings.

Image and video providers use `MediaFingerprintCache` only when the captured run options enable it. Entries are keyed by canonical path and validated with length, last-write UTC ticks, and sample schema. Media processing concurrency is restricted to Auto/1/2/4; similarity presets resolve to bounded algorithm thresholds. `AnalysisService` can revalidate and globally regroup surviving similarity items after a partial action.

## Analysis Result State and Actions

`AnalysisResultsViewModel` maintains separate canonical and visible state:

- flat tools keep canonical `PathFindingViewModel` instances apart from the filtered/sorted `Findings` projection;
- similarity tools keep canonical `SimilarityGroupViewModel`/`SimilarityItemViewModel` instances apart from visible groups;
- totals, selection, action validation, export, and preview authority come from canonical state;
- search/sort never discard hidden selection;
- successful flat actions remove only successful canonical paths;
- successful similarity actions revalidate candidates, globally regroup all survivors, drop groups smaller than two, and rebind preview by full path;
- failed or undecodable items remain available when they still satisfy the canonical group contract.

Bulk Move/Delete captures the initiating `AnalysisSession`; canonical reconciliation and Move/Delete status are ignored when that session is no longer current. Immediately before mutation, the ViewModel rechecks path kind, size/time/tool predicate or provider evidence as appropriate. Rename/export retain their narrower tool-specific session and validation checks. Tool authority is deliberate:

| Result tool | Mutation authority |
| --- | --- |
| Empty folders, big files, empty files, temporary files | Validated move/delete. |
| Invalid links | Link-entry move/delete; target content is not followed or deleted. |
| Broken files | Validated move/delete; no rename. |
| Bad extensions, bad names | Rename only. |
| Similar images, similar videos, music duplicates | Manual move/delete after revalidation; no automatic cleanup. |
| EXIF remover, video optimizer output rows | Open and Reveal only. |

Open, Reveal, Copy path, export, collision handling, progress, cancellation, and per-item failures use shared app services. Exact link replacement is intentionally limited to exact duplicate results; it is not exposed on similarity or flat-analysis rows.

## Operation Coordination and Reset Semantics

`AppOperationCoordinator` grants one lease for exact scan, analysis run, exact result action, analysis result action, EXIF cleaning, or video optimization. Commands observe the shared active operation, and cancellation is requested through the captured lease. File transformations own cleanup/rollback before coordinator completion.

`MainWindowOperationGuard` serializes departure, close, and result-reset dialogs. A reset captures the coordinator generation and exact/analysis session before showing the dialog. Cancel changes nothing. Confirm proceeds only if no operation started meanwhile, the generation is unchanged, the coordinator is idle, and the ViewModel can reference-match and clear the captured still-current session. Concurrent dialogs are suppressed. Exact and analysis reset clear canonical/visible results, selection, search, sort, status, and preview together, then navigate to the same tool's setup page.

## Link and Transform Transaction Boundaries

- Exact-link replacement snapshots path/size/modified time, verifies full byte identity against the kept file, renames the duplicate to a same-directory rollback sibling, creates and validates the link at the original path, then sends the rollback entry to the Recycle Bin. Failure restores the original entry and cleans owned artifacts. Hardlink and symbolic-link capability is explicit; cross-volume behavior is not assumed.
- `ExifCleanerService` supports JPEG/TIFF copy or replace. WIC metadata removal follows `ExifMetadataPolicy`; replace mode uses identity-aware staging and Recycle Bin/rollback boundaries so a failed output never silently destroys the source.
- `VideoOptimizerService` probes the source, applies validated H.264/AAC profiles, preserves rotation and pixel-aspect-ratio metadata, rejects outputs that do not satisfy the size gate, and commits a new sibling `.mp4` only after successful transcode validation. The source is preserved. Cancellation removes temporary/partial output and respects the transaction boundary.
- Transform result rows expose only Open/Reveal. The ViewModels never broaden completed outputs into generic move/delete actions.

## Settings and Persistence

Only `AppSettings` is persisted, at:

```text
%LOCALAPPDATA%\Duplicates\settings.json
```

Defaults:

| Setting | Default |
| --- | --- |
| Theme / backdrop | `System` / `MicaAlt` |
| Exact minimum bytes / byte verification | `1` / `true` |
| Ignore hidden / system files | `true` / `true` |
| Include subfolders | `true` |
| Big-file threshold | `1,073,741,824` bytes |
| Temporary-file age | `7` days |
| Image / video similarity | `Balanced` / `Balanced` |
| Media concurrency / fingerprint cache | `null` (Auto) / `true` |
| Hashing concurrency | `null` / Auto |
| Deletion mode / confirmation | `RecycleBin` / `true` |

`SettingsService` uses `JsonSerializerDefaults.Web`, indented output, a serialized write gate, monotonically registered revisions, same-directory temporary files, flush-to-disk, and atomic overwrite. Only the latest registered revision may commit, and owned temporary files are removed in `finally`.

Load behavior:

- Missing files preserve defaults.
- Malformed, unsupported, inaccessible, or otherwise recognized settings failures fall back to normalized defaults instead of aborting startup.
- Enums, sizes, ages, concurrency values, and deletion mode are normalized before `Current` changes.
- Successful load raises `SettingsChanged`.

Save behavior:

- `SettingsViewModel` captures and normalizes a complete immutable snapshot for every change outside guarded load.
- `SettingsService` serializes writes and commits only the newest registered revision.
- The flushed temporary document moves over the target atomically.
- `Current` and `SettingsChanged` update only for the committed revision; observed failures surface in the Settings InfoBar.

Hashing concurrency offers Auto/1/2/4/8. Media concurrency offers Auto/1/2/4. Invalid size input preserves the current saved value, non-negative size values are clamped, and temporary age is clamped to 1-365 days.

The Settings page has exactly four native Community Toolkit expanders: Appearance, Scanning defaults, Similarity and media, and File actions. Its cards contain exactly three `NumberBox` controls for default minimum bytes, big-file bytes, and temporary-file days. Cache status shows entry count, size, and path; Clear cache is an ordinary button and is disabled only while clearing or while an active operation uses the cache. Permanent deletion keeps a visible warning. About text is derived from the app assembly version plus the locked .NET/Windows App SDK families.

The media fingerprint document is separate from settings at `%LOCALAPPDATA%\Duplicates\cache\media-fingerprints-v1.json`. It has its own schema version and atomic write/cleanup behavior. Entries are canonical-path keyed and include file length plus last-write ticks so changed files miss the cache. Clearing increments the cache generation so an in-flight producer cannot repopulate a cleared generation.

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
| `ScanWorkAreaMaxWidth` | 680 |
| `SettingsContentMaxWidth` | 1000 |
| `FolderListMaxHeight` | 220 |
| `PageHeaderPadding` | `32,24,32,20` |
| `PageContentPadding` | `32,0,32,32` |

`Styles.xaml` intentionally contains only metric resources plus three text styles: section header, muted text, and path text. Production XAML defines no custom `ControlTemplate` and no `Setter Property="Template"`. Buttons use WinUI defaults; `AccentButtonStyle` is reserved for Start scan, Start analysis, Clean images, and Optimize videos. New scan/analysis, destructive actions, and Clear cache remain ordinary native buttons so WinUI owns hover, pressed, disabled, focus, and High Contrast behavior.

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

The application uses native WinUI semantics first:

- result and scope selection use `ListView`/`ListViewItem`, checkboxes, toggles, `CommandBar`, `SplitView`, `NumberBox`, `ContentDialog`, and Community Toolkit settings controls;
- production XAML has no tap handlers on `Border`, `Grid`, `StackPanel`, `Canvas`, or `RelativePanel`;
- icon-only controls have explicit accessible names and tooltips;
- all setup pages disable horizontal scrolling, and responsive states target 640 and 1008 epx layouts;
- top result lists are virtualizing native `ListView` controls outside an outer page scroller;
- long paths remain available in full through wrapping, preview text, or tooltip rather than ViewModel truncation;
- exact preview rendering is bounded to 512 x 220, while analysis media previews use their separate aspect-preserving 512-pixel-long-side loader;
- active run/status surfaces expose polite live regions;
- KEEP/DELETE, warnings, failures, progress, selection, and skipped states use text in addition to color.

Dialog defaults are conservative: destructive/reset/departure dialogs default to Close/Cancel/Stay. Declined navigation restores the prior native navigation selection. Reset confirmation does not mutate focusable result state while open, suppresses concurrent dialogs, and returns to the same tool setup only after a successful captured-session reset.

`NativeWinUiContractTests` enforce the five-color-only palette, no custom templates, layout-tap ban, exact four accent actions, setup scrolling, result-list virtualization, live regions, preview bounds, settings shape, and icon names/tooltips. These source-contract tests do not substitute for rendered Narrator, High Contrast, focus-order, touch, DPI, or text-scale UAT. The UI has no localization resource layer; visible strings remain English.

## Tests

### Engine test surface

`Duplicates.Engine.Tests` covers the exact scanner funnel, forced hash collisions, hardlinks, zero-byte policy, filters, overlapping roots, changing/inaccessible paths, progress, cancellation, and pooled-buffer ownership. Analysis coverage includes `FileInventoryBuilder`, all storage/name/link/broken analyzers, file signatures, perceptual hashing, image/video grouping and revalidation, music metadata grouping, provider failures, concurrency, skips, and cancellation. `Duplicates.Engine/Properties/AssemblyInfo.cs` exposes engine internals only to the engine test assembly so deterministic seams do not widen the production API.

### App test surface

`Duplicates.App.Tests` covers:

- shared path scope, scan/analysis setup, operation coordination, departure/close/reset dialogs, and replacement-session guards;
- canonical exact, flat, and similarity state under search/sort, hidden selection/totals, partial success/failure, preview collapse/rebind, and global regrouping;
- move/delete/export/rename, collision policy, Recycle Bin targeting, exact link replacement, and native Windows capability skips;
- Windows WIC/media/music/file-format providers and the media fingerprint cache;
- EXIF and video transaction commit, rollback, cancellation, cleanup, profile/metadata/size gates, and UI action boundaries;
- settings normalization, malformed JSON fallback, rapid-save last-write-wins behavior, cache status/clear guards, and theme/backdrop state;
- native XAML accessibility/layout/virtualization contracts.

### App-test assembly arrangement

`Duplicates.App.Tests.csproj`:

- Uses a non-output project reference to the app with `DisableWindowsAppSdkAutoInitialize=true`.
- Directly references the built app Debug x64 `Duplicates.dll`.
- Directly references `Microsoft.WinUI.dll` and `WinRT.Runtime.dll` from the same app output folder.
- References `Duplicates.Engine` normally.

Build the app first if those Debug x64 files are absent or stale. This arrangement tests app logic and source XAML contracts without initializing a registered Windows App Runtime; it does not constitute packaged/unpackaged launch or rendered accessibility UAT.

## Acceptance Evidence Matrix

| Area | Automated evidence | UNP named UAT | PKG named UAT |
| --- | --- | --- | --- |
| Duplicate files | PASS - scanner/results/actions/link tests | PASS - picker/drop/exclusion, scan/progress/skips, search/sorts/rules, selection guard, reset and actions | PASS - same matrix in a fresh registered DevelopmentMode package |
| Empty folders | PASS - inventory/storage/mutation tests | PASS - exact two-folder fixture | PASS - exact two-folder fixture |
| Big files | PASS - storage threshold/revalidation tests | PASS - exact three-file fixture | PASS - exact three-file fixture |
| Empty files | PASS - zero-byte/revalidation tests | PASS - exact two-file fixture | PASS - exact two-file fixture |
| Temporary files | PASS - age/freshness/revalidation tests | PASS - exact two-file fixture | PASS - exact two-file fixture |
| Similar images | PASS - pHash/provider/cache/regroup tests | PASS - similarity group plus malformed-image skip | PASS - similarity group plus malformed-image skip |
| Similar videos | PASS - sample/provider/cache/regroup tests | PASS - similarity group plus malformed-video skip | PASS - similarity group plus malformed-video skip |
| Music duplicates | PASS - metadata/provider/regroup tests | PASS - exact three-track metadata group | PASS - exact three-track metadata group |
| Invalid links | PASS - analyzer and entry-only action tests | PASS - exact broken-link fixture | PASS - exact broken-link fixture |
| Broken files | PASS - probe/analyzer/revalidated action tests | PASS - exact malformed-file fixture | PASS - exact malformed-file fixture |
| Bad extensions | PASS - signature/analyzer/rename tests | PASS - mismatch plus safe rename | PASS - mismatch plus safe rename |
| Bad names | PASS - analyzer/rename safety tests | PASS - exact U+202E fixture | PASS - exact U+202E fixture |
| EXIF remover | PASS - service/transaction/provider tests; some synthetic metadata-layout capabilities remain unavailable | PASS - supported TIFF cleaned, removable metadata absent, orientation/source preserved, unsupported layouts explicit | PASS - same verified TIFF outcome |
| Video optimizer | PASS - service/transaction/real-UAT tests with explicit codec capability limits | PASS - verified smaller sibling outputs, source preservation and cancellation cleanup | PASS - verified transform and cancellation cleanup |
| Result actions | PASS - export/move/delete/rename/link/collision/session tests | PASS - SavePicker fresh/Yes/No, Move, Delete/Recycle restore, links, rename, Reveal and Copy; `.txt` Open and cross-volume unavailable | PASS - same applicable action matrix; `.txt` Open and cross-volume unavailable |
| Settings/cache | PASS - normalization/latest-wins/cache/guard/native tests | PASS - Light persistence, dynamic cache names and clear | PASS - Light persistence, dynamic cache names and clear |
| Visual/accessibility | PASS - native source contracts | PASS - 640 epx, no horizontal setup scroll, names/access keys, keyboard focus, High Contrast and observable Narrator boundary | PASS - same plus all 14 headings; spoken Narrator audio and legacy UIA `LiveSetting` query unavailable |
| Performance | PASS - inventory/cancellation and pooled-verifier/bulk-refresh regressions | PASS - responsive rendered 100,000-file Results in 94.035 s, virtualization and End/Home | PASS - responsive rendered 100,000-file Results in 63.567 s, virtualization and End/Home |
| Debug x64 launch | PASS - direct executable/window/process | PASS - fresh exact worktree executable, responsive process, clean close | PASS - fresh exact PFN/layout process, responsive window, clean close |

The historical Gate D verdict is PASS for the feature branch's unpackaged and packaged Debug x64 builds. Evidence is layered: the broad dual-mode run covers all 14 tool flows, layout, settings, High Contrast and observable Narrator keyboard use; the targeted correction run covers PathScope, selection, SavePicker, file actions, contextual cancellation and cache accessibility; the final isolated run covers responsive dual-mode 100,000-file Results. Remaining bounded capabilities are objective Narrator speech capture, the legacy client's `LiveSetting` property query, deterministic `.txt` Open behavior without controlling the external default association, and cross-volume actions without a second owned volume.

## Merge Verification (2026-08-27)

The data-cleaner branch was verified with the package versions retained from `main` and .NET SDK `10.0.400`:

| Check | Result |
| --- | --- |
| Solution restore | Passed |
| Engine tests | 331 passed, 0 failed, 0 skipped |
| App tests | 738 passed, 0 failed, 2 cross-volume capability skips |
| Debug x64 app build | Passed; 0 warnings, 0 errors |
| `dotnet format Duplicates.slnx --verify-no-changes --no-restore` | Passed; exit 0, no workspace warnings |
| `git diff --check` | Passed |

The xUnit v3 integration aligns the test project's .NET deployment mode with the app, removes obsolete xUnit v2 imports, supplies source locations for the custom fact attribute, and passes the test cancellation token to cancellable test operations. No analyzer suppressions were added. Packaged/unpackaged GUI acceptance was not repeated in this merge verification.

## Historical Verification Snapshot

Verification recorded for the feature branch on 2026-08-08–09:

| Check | Result |
| --- | --- |
| Focused `CrossToolStateTests` + `NativeWinUiContractTests` | PASS: 82 passed, 0 skipped/failed |
| Focused Results state/link suite | PASS: 115 passed, 0 skipped/failed after the final bulk-selection change |
| Engine scanner suite | PASS: 23 passed, 0 skipped/failed after pooled-verifier change |
| Engine suite | PASS: 331 passed, 0 skipped/failed |
| App suite | PASS on direct and wrapper runs: 737 passed, 2 explicit capability skips, 0 failed |
| Debug x64 app build | PASS: 0 warnings, 0 errors |
| `dotnet format Duplicates.slnx --verify-no-changes` | PASS; 0 of 221 files formatted |
| 100,000-file `FileInventoryBuilder` fixture | PASS: 100,000 files/101 directories, 7,562.8 ms inventory, 14.2 ms cancellation response after request; one NTFS/NVMe host only |
| Unpackaged rendered 100,000-file Results | PASS: 94.035 s, all samples responsive, peak working set 765,538,304 B, exact selection/virtualization/End/Home, EventLog 0/0/0 |
| Packaged rendered 100,000-file Results | PASS: 63.567 s, all samples responsive, peak working set 1,017,565,184 B, exact selection/virtualization/End/Home, EventLog 0/0/0 |
| Unpackaged Debug x64 launch/flow | PASS: fresh exact worktree executable; combined broad/targeted/performance matrix and clean teardown |
| Packaged Debug x64 registration/launch/flow | PASS: fresh MSIX to unique loose layout, Status `Ok`/DevelopmentMode, exact PFN/layout process; combined matrix and clean teardown |

Execution-environment qualification:

- `global.json` requests `10.0.301` with feature-band roll-forward; the actual SDK was `10.0.302` and runtime `10.0.10`.
- Two cross-volume native tests remained explicit capability skips: real cross-volume link replacement and cross-volume empty-directory move.
- Full App.Tests previously exhibited a nondeterministic testhost inactivity hang in accumulated Windows-media UAT runs. Dumps identified synchronous lifetime move/delete/cleanup boundaries after completed WinRT media work. Test-only operations are now bounded and WinRT operations are explicitly closed; three fresh full App runs, the final blame-hang run, and both final wrappers completed. The historical native-lifetime boundary remains a harness risk to monitor, not a diagnosed product failure.
- Three identical unpackaged `Microsoft.UI.Xaml.dll` `0xc000027b` crashes occurred as the Scan progress subtree became visible. A picker-only forced Gen2 GC stayed responsive, and deferring result navigation did not help. Replacing the progress subtree's unsupported general-layout `WrapGrid` with `VariableSizedWrapGrid` made the same fresh UIA flow pass with visible progress and Results; source contracts now reject direct `WrapGrid` outside `ItemsPanelTemplate` across production XAML.
- Two successive long-lived 100,000-file runs exposed independent scaling defects. The pre-pool unpackaged run exposed per-comparison 1 MiB verification-array churn; reusing one pooled pair per hash group removed it. With pooling already present, the later retained packaged Results run exposed per-file totals refreshes during bulk selection; batching selection-rule/Undo notifications removed that O(N^2) getter fanout. Fresh dual-mode reruns then remained responsive and reached Results within the bounds above.

Standard commands:

```powershell
dotnet restore Duplicates.slnx
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet format Duplicates.slnx --verify-no-changes
```

## Local Check Wrappers and CI

Project wrappers delegate to:

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

`tools/lc.ps1` first builds the engine in Debug so the formatter can load the WinUI project; the other wrappers delegate directly.

Historical Task 20 wrapper evidence:

| Wrapper/capability | Status |
| --- | --- |
| `lc.ps1` | PASS; format/analyzers completed, 0 files changed |
| `sc.ps1` dependency audit | PASS; no vulnerable direct/transitive packages reported from configured sources |
| Semgrep C# scan | FAIL; Semgrep 1.172.0 scanned 183 tracked C# targets with 27 rules and reported 3 blocking `unsafe-path-combine` findings in test helpers |
| `bc.ps1` | PASS on the final single run; restore, Engine 331/331, App 737 passed plus 2 explicit capability skips, and Debug x64 build with 0 warnings/errors |
| `tc.ps1` | PASS on the final single run; Engine 331/331 and App 737 passed plus 2 explicit capability skips |
| Gitleaks secret scan | PASS; Gitleaks 8.30.1 scanned 55 commits / about 2.50 MB and reported no leaks |
| TruffleHog installation probe | PASS; TruffleHog 3.96.0 completed a no-verification filesystem scan of a benign temporary probe with 0 findings; the project wrapper prefers Gitleaks when both are installed |
| Scoped regex secret review | PASS; 31 intended Task 20 content-diff/new files, no matching private-key, provider-token, or assigned-secret patterns |

Wrapper exit code alone is not capability evidence: a `SKIPPED` scanner remains `UNAVAILABLE`. Reports go to gitignored `reports/`. `dc/ql/db` were outside Task 20 because there was no dependency or remote-CI change. Do not route this WinUI/.NET project through Android tooling.

There is no checked-in GitHub Actions, Azure Pipelines, or other CI configuration. Local passes are not evidence of a remote quality gate.

## Security and Data-Safety Properties

Positive controls:

- Fully local data path; no application network client is present.
- Exact byte verification is enabled by default after non-cryptographic hashing.
- Recycle Bin is the default deletion target.
- Permanent deletion is opt-in and produces a warning in Settings.
- Confirmation is enabled by default.
- Selection and delete guards keep a survivor in each group.
- Per-file failures do not remove failed rows from canonical Results.
- Tool-specific action authority prevents Bad names/extensions and transform outputs from acquiring broader mutation controls.
- Move/Delete actions capture their initiating session, revalidate tool predicates where required, and ignore service-summary canonical reconciliation and completion status against replacement sessions.
- EXIF, video, and exact-link transformations stage owned artifacts and clean up or roll back before coordinator completion.
- Settings and cache documents use serialized, flushed, same-directory temporary writes and atomic replacement.
- Scanner shares files for read/write/delete to reduce lock interference and checks expected length.
- Package has only the `runFullTrust` restricted capability, with no broad library capability declarations in the package manifest.

Review cautions:

- XxHash3 is non-cryptographic. If byte verification is disabled, equal size and XxHash3 are accepted as duplicate identity.
- The app is full-trust and can permanently delete any selected path accessible to the user.
- Exact move/delete maps path, size, and target kind but does not rehash or check modified time immediately before the action. A same-size file can change after scanning; this remains a time-of-check/time-of-use boundary. Exact link replacement is narrower and does check modified time plus full byte identity.
- Length checks do not detect same-length content changes during hashing. Media cache validation similarly cannot detect a same-length, same-mtime content rewrite.
- Results and selection describe a scan snapshot, not a live filesystem truth.
- Similarity thresholds and platform media decoders can produce false positives, skips, or environment-specific codec limitations. Similarity findings require human review.
- Link creation, Recycle Bin, protected paths, and cross-volume moves depend on Windows privileges/filesystem capability; tests keep explicit capability skips distinct from passes.
- ViewModel saves are intentionally fire-and-forget and not debounced, although the service serializes them and enforces latest-registered-wins.
- The fingerprint cache stores canonical local paths and derived media samples unencrypted under the current user's local app data.
- Shell Open/Reveal and clipboard behavior remain platform operations; runtime failure handling must remain user-visible and session-safe.
- The runtime installer downloads and executes package installation from a hardcoded external URL; validate source/version before maintenance changes.

## Known UI and Maintainability Risks

- Similarity groups use nested item presentation inside the top-level native results list; very large realized similarity groups remain the main UI-density/virtualization risk.
- Dark/light semantic roles collapse to five palette colors. Light persistence, High Contrast substitution, and observable keyboard/Narrator focus were exercised in both modes; text scale and objective spoken Narrator output still require separate runtime evidence.
- `SuccessBrush` is red, so "success" naming does not describe its visual semantics.
- ViewModels subscribe to long-lived service events and are themselves long-lived singletons; this currently matches `AppServices`, but a move to transient pages/ViewModels would require unsubscription/lifetime work.
- About text hardcodes `.NET 10` and `Windows App SDK 2.5`; it is not derived from package metadata.
- The inventory measurement and fresh unpackaged/packaged rendered 100,000-file runs are one local NTFS/NVMe host's evidence, not a cross-machine performance guarantee.
- Semgrep, gitleaks, and trufflehog availability is external to the repository; missing scanners must remain `UNAVAILABLE`, never reported as a clean scan.

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
- Does the layout work at 640 and 1008 epx, high DPI, high text scale, keyboard-only, dark, and light?
- Are long paths both trimmed and discoverable in full?
- Does image preview retain a decode-size constraint?
- Does a new list avoid eager creation of unbounded child controls?

### Settings and persistence

- Is the default conservative?
- Are model, service, ViewModel, XAML, and tests updated together?
- Does the setting apply at the intended time, especially during an active scan?
- Are parsing, corrupt JSON, overlapping saves, and migration/default behavior considered?
- Does no result/session/action state accidentally become persistent, and is any cache persistence explicit?

### Build, packages, and tests

- Is every package version centralized?
- Was app Debug x64 built before App.Tests?
- Were focused tests added before/with behavior changes?
- Did the current Engine and App suites complete with capability skips classified separately?
- Did `dotnet format Duplicates.slnx --verify-no-changes` pass?
- Was the actual resolved SDK recorded when it differs from the requested SDK?
- For Windows App SDK changes, were packaged and unpackaged runtime paths tested?
- Were local-wrapper results distinguished from direct commands and from absent remote CI?

## Remaining Runtime Acceptance

The historical Task 20 Gate D passed for the feature branch on Windows App SDK 1.8; the Windows App SDK 2.5 build requires its own runtime acceptance. The following still need explicit manual or environment-backed evidence before claiming distribution-wide release readiness:

- Unsigned Debug MSIX generation, MakeAppx unpack, DevelopmentMode registration, AUMID launch, removal, and exact InstallLocation verification passed. Release signing, trusted sideload installation, update, and uninstall remain unverified as a distribution flow.
- Runtime installer behavior with missing, same, newer, and corrupted runtime packages.
- Cross-volume real link replacement and empty-directory move remain explicit capability skips on this host.
- Files modified, renamed, or deleted between scan and delete.
- High hashing concurrency under memory/I/O pressure.
- Objective Narrator spoken-audio capture, the legacy UIA client's unsupported `LiveSetting` property query, and deterministic `.txt` Open behavior without controlling the external default association remain unavailable. Dynamic accessible cache names and source live-region contracts passed.
- The historical accumulated-process Windows-media lifetime hang remains worth monitoring even though bounded cleanup, three fresh full runs, the final blame-hang run, and both wrappers passed.
- Semgrep, Gitleaks, and TruffleHog are installed on this host. Gitleaks reported no leaks, while Semgrep reported three blocking `unsafe-path-combine` findings in test helpers that remain to be adjudicated. TruffleHog passed an installation probe but was not used for the repository scan because the wrapper prefers Gitleaks when both are available. The dependency audit and the scoped regex review also passed.
- External Windows-check implementations and any remote CI/security service.
- Packaged and unpackaged GUI acceptance of the combined tool implementation on Windows App SDK 2.5.1; historical 1.8 runtime results do not verify that combination.
