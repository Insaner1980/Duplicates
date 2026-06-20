# Duplicates — Build Specification for Codex

A native Windows 11 desktop application that finds and removes duplicate files of any type. This document is the complete, authoritative build spec. Implement exactly what is described here. Where a detail is not specified, prefer the simplest correct WinUI 3 idiom and ask before adding speculative complexity.

App name: **Duplicates**
Author/brand: Finnvek (personal, not a company)
UI language: English only
Target user: single user, the developer herself, on her own Windows 11 machine.

A note on engineering discipline for the agent: do not over-build. Do not invent features that are not in this spec. Do not add abstraction layers "for the future" unless this spec asks for them. Favor readable, idiomatic WinUI 3 + MVVM code over clever code. If something here looks wrong or contradictory, stop and flag it rather than guessing.

---

## 1. Style Direction

Custom premium dark/light theme specified by the owner, not the generic personal-default glassmorphism. The look is: near-black charcoal surfaces, crisp white text, a single mint accent, with a fully working Light theme as well. Think "professional Windows 11 utility, Fluent-native, restrained, premium." No neon, no gradients-as-decoration, no glassmorphism cards. Mica (or Mica Alt) backdrop on the window, solid controlled surfaces on top so the exact palette is preserved.

The app must support System / Light / Dark theme, switchable at runtime from Settings, defaulting to System.

---

## 2. Tech Stack (verified current as of June 2026)

Use these exact frameworks. For NuGet packages, the versions below are the known-good current stable floor; pull the latest stable patch of the same major/minor at restore time, do not downgrade, and do not jump to prerelease.

- Language: C# 14
- Runtime: .NET 10 (LTS). Target Framework Moniker: `net10.0-windows10.0.22621.0`
- Minimum OS target: `10.0.22000.0` (Windows 11). Windows 11 only is acceptable, the owner runs Windows 11.
- UI framework: WinUI 3 via Windows App SDK 1.8 (stable). Package `Microsoft.WindowsAppSDK` 1.8.x (latest 1.8 servicing build, currently 1.8.9-era).
- IDE: Visual Studio 2026 (or 2022 17.14+) with the ".NET Desktop Development" workload and Windows App SDK support. Codex may scaffold and build from the CLI instead, see Section 14.
- Architecture (CPU): `win-x64` (the Acer Aspire 14 AI is x64). Add `win-arm64` to RuntimeIdentifiers only if it is free to do so, but x64 is the only required target.

NuGet packages:
- `Microsoft.WindowsAppSDK` 1.8.x
- `Microsoft.Windows.SDK.BuildTools` (matching, restored automatically with the SDK)
- `CommunityToolkit.Mvvm` 8.4.2 (MVVM source generators: `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`)
- `CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.x (`SettingsCard`, `SettingsExpander` for the Settings and Scan-options screens)
- `System.IO.Hashing` 10.0.x (provides `XxHash3`, used as the content hash, see Section 6)
- `Microsoft.VisualBasic` types for Recycle Bin deletion (`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` with `RecycleOption.SendToRecycleBin`). This assembly ships with .NET, no extra package needed in most setups, add the reference if the type is not resolved.

Do not add: any web view, any Electron-like layer, Entity Framework, a database engine, a logging framework heavier than the built-in `System.Diagnostics`/simple file log, or any cloud SDK. This is a fully local, offline app.

---

## 3. High-Level Behaviour

1. The user picks one or more folders to scan.
2. The user optionally narrows the scan (subfolders, size range, file-type categories, hidden/system files).
3. The app scans and finds files whose **byte content is identical** (true duplicates), grouped together.
4. The app shows results grouped by duplicate set, with sizes and total reclaimable space.
5. The user reviews, auto-selects or hand-picks which copies to remove (always keeping at least one per group), and deletes them (to Recycle Bin by default).

The core matching is content-based, so it is inherently format-agnostic: it works on every file type automatically (images, video, audio, documents, archives, executables, anything). "Support as many formats as possible" is therefore satisfied by design for detection. File type only matters for two secondary things: optional category filters (Section 8.3) and preview rendering (Section 10.7).

---

## 4. Project Structure

Single solution, single app project plus one class library for the engine so the detection logic is testable in isolation.

```
Duplicates.sln
├─ Duplicates/                      (WinUI 3 app, net10.0-windows...)
│  ├─ App.xaml / App.xaml.cs
│  ├─ MainWindow.xaml / .cs         (window shell, custom title bar, NavigationView host)
│  ├─ Views/
│  │  ├─ ScanPage.xaml / .cs
│  │  ├─ ResultsPage.xaml / .cs
│  │  └─ SettingsPage.xaml / .cs
│  ├─ ViewModels/
│  │  ├─ ShellViewModel.cs
│  │  ├─ ScanViewModel.cs
│  │  ├─ ResultsViewModel.cs
│  │  ├─ DuplicateGroupViewModel.cs
│  │  ├─ DuplicateFileViewModel.cs
│  │  └─ SettingsViewModel.cs
│  ├─ Services/
│  │  ├─ ISettingsService.cs / SettingsService.cs
│  │  ├─ IDialogService.cs / DialogService.cs
│  │  ├─ IThemeService.cs / ThemeService.cs
│  │  └─ IFileActionService.cs / FileActionService.cs   (open, reveal, delete to recycle bin)
│  ├─ Converters/                   (value converters for XAML: bytes-to-readable, etc.)
│  ├─ Themes/
│  │  ├─ Colors.xaml                (ThemeDictionaries: Light + Dark token brushes)
│  │  └─ Styles.xaml                (component styles overriding native where needed)
│  └─ Assets/                       (app icon, etc.)
└─ Duplicates.Engine/              (class library, net10.0, NO UI references)
   ├─ Models/
   │  ├─ FileEntry.cs
   │  ├─ DuplicateGroup.cs
   │  ├─ ScanOptions.cs
   │  ├─ ScanProgress.cs
   │  └─ ScanResult.cs
   ├─ DuplicateScanner.cs           (the engine, see Section 6)
   ├─ Hashing/FileHasher.cs
   └─ FileEnumeration/FileWalker.cs
```

Use simple constructor-based dependency injection. A lightweight DI container (`Microsoft.Extensions.DependencyInjection`) is acceptable but not required; manual wiring in `App.xaml.cs` is fine for an app this size. Prefer whichever is simpler to read.

MVVM: use `CommunityToolkit.Mvvm`. ViewModels derive from `ObservableObject`, expose state via `[ObservableProperty]`, and expose actions via `[RelayCommand]`. Views are XAML with `x:Bind` (compiled bindings, `Mode=OneWay` default, `TwoWay` where editing). No code-behind logic beyond view wiring.

---

## 5. Data Models (Duplicates.Engine)

`FileEntry`
- `string FullPath`
- `string FileName`
- `string Extension` (lowercased, including the dot, e.g. ".jpg"; empty string if none)
- `string DirectoryPath`
- `long SizeBytes`
- `DateTime CreatedUtc`
- `DateTime ModifiedUtc`
- `ulong? ContentHash` (XxHash3 of full content, filled in only when computed)

`DuplicateGroup`
- `ulong ContentHash`
- `long SizeBytes` (size of one file in the group; all members share it)
- `IReadOnlyList<FileEntry> Files` (2 or more)
- `long WastedBytes` => `SizeBytes * (Files.Count - 1)` (space reclaimable if all but one removed)

`ScanOptions`
- `IReadOnlyList<string> Folders`
- `bool IncludeSubfolders` (default true)
- `long MinSizeBytes` (default 1; never scan 0-byte files by default, see edge cases)
- `long MaxSizeBytes` (default `long.MaxValue` = no cap)
- `FileTypeFilter TypeFilter` (see Section 8.3: All, or a set of categories, or explicit extension list)
- `bool IgnoreHiddenFiles` (default true)
- `bool IgnoreSystemFiles` (default true)
- `bool FollowSymlinks` (default false)
- `bool VerifyByteByByte` (default true, see Section 6 step 5)

`ScanProgress`
- `ScanPhase Phase` (enum: `Enumerating`, `GroupingBySize`, `PartialHashing`, `FullHashing`, `Verifying`, `Done`)
- `long FilesDiscovered`
- `long FilesProcessed`
- `long BytesProcessed`
- `long TotalBytesToProcess` (best estimate for the current hashing phase; used for the determinate bar)
- `string? CurrentFilePath`

`ScanResult`
- `IReadOnlyList<DuplicateGroup> Groups` (sorted by `WastedBytes` descending by default)
- `long TotalFilesScanned`
- `long TotalDuplicateFiles` (sum over groups of `Files.Count - 1`)
- `long TotalReclaimableBytes` (sum of `WastedBytes`)
- `TimeSpan Elapsed`
- `IReadOnlyList<string> SkippedPaths` (files that could not be read, with reasons available via a parallel list or a small record)

---

## 6. The Detection Engine (most important section)

Goal: find sets of files with byte-identical content, fast, on potentially hundreds of thousands of files, without reading every byte of every file unless necessary. Use the classic three-stage funnel. Each stage cheaply eliminates non-duplicates before the next, more expensive stage.

### Stage 0 — Enumerate (`FileWalker`)
- Walk the chosen folders (recursively if `IncludeSubfolders`). Use `Directory.EnumerateFiles` with `EnumerationOptions { RecurseSubdirectories, IgnoreInaccessible = true, AttributesToSkip = ... }`. Set `AttributesToSkip` from the hidden/system options.
- For each file build a `FileEntry` (path, size, timestamps, extension).
- Apply filters here: size range, type filter (Section 8.3), hidden/system (already handled via `AttributesToSkip`, but double-check), symlink/reparse-point handling (skip reparse points unless `FollowSymlinks`).
- Collect inaccessible files into `SkippedPaths` rather than throwing.
- Report progress with `Phase = Enumerating`, updating `FilesDiscovered`.

### Stage 1 — Group by exact size
- Bucket all `FileEntry` by `SizeBytes` into a `Dictionary<long, List<FileEntry>>`.
- Discard every bucket with only one file: a unique size cannot be a duplicate. This is free (no file I/O) and removes the vast majority of candidates.
- `Phase = GroupingBySize`.

### Stage 2 — Partial hash (head sample)
- For each remaining same-size bucket with 2+ files, compute a partial hash of the first N bytes of each file (suggested N = 65536 = 64 KB; for files smaller than N, the partial hash equals the full file and you can mark it as already fully hashed to skip Stage 3 for that file).
- Use `XxHash3` (`System.IO.Hashing`). Read the head with a buffered `FileStream` (sequential, async).
- Re-bucket within each size group by `(SizeBytes, partialHash)`. Discard buckets that drop to a single file.
- `Phase = PartialHashing`. Update `FilesProcessed`, `BytesProcessed`.
- Rationale: two same-size files that differ usually differ in the first few KB, so this cheaply kills most remaining false candidates without reading whole files.

### Stage 3 — Full content hash
- For each surviving `(size, partialHash)` bucket with 2+ files, compute the full-file `XxHash3` of each file by streaming the entire content (buffer ~1 MB, sequential read, async). Cache it on `FileEntry.ContentHash`.
- Re-bucket by full hash. Surviving buckets with 2+ files are duplicate candidates.
- `Phase = FullHashing`. This is the I/O-heavy phase; this is what the determinate progress bar tracks (`BytesProcessed / TotalBytesToProcess`, where total = sum of sizes of files entering Stage 3).

### Stage 4 — Optional byte-by-byte verification (default ON)
- XxHash3 is a fast non-cryptographic hash. Collisions are astronomically unlikely but theoretically possible, and this app deletes files, so safety matters.
- When `VerifyByteByByte` is true, for each candidate group confirm equality by streaming and comparing bytes pairwise (compare file 1 against each other; any that match form the confirmed group, any that do not are split out). Use buffered sequential reads and `Span<byte>.SequenceEqual` on chunks.
- When false, trust the full hash. Keep the toggle in Settings, defaulting ON, with a one-line explanation in the UI ("Re-read files to guarantee they are identical before deleting. Slightly slower, maximally safe.").
- `Phase = Verifying`.

### Stage 5 — Build `ScanResult`
- Materialize confirmed groups into `DuplicateGroup` objects, compute totals, sort groups by `WastedBytes` descending.

### Concurrency, cancellation, progress
- The whole scan runs off the UI thread. Expose:
  `Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress> progress, CancellationToken ct)`
- Hashing stages are parallel. Degree of parallelism must adapt to storage type: SSD/NVMe benefits from many concurrent readers, a spinning HDD is hurt by them (seek thrashing). Strategy: default `maxConcurrency = Environment.ProcessorCount` for hashing, but expose a Settings override (Auto / 1 / 2 / 4 / 8). If detecting drive type is non-trivial, default to `ProcessorCount` and rely on the manual override. Use `Parallel.ForEachAsync` or a `SemaphoreSlim`-bounded set of tasks.
- Honor `CancellationToken` between and within stages (check periodically during long file reads).
- `IProgress<ScanProgress>` reports are marshalled to the UI via the page's `DispatcherQueue`. Throttle UI updates to ~10 per second (do not raise on every file) to keep the UI smooth on huge scans.

### Edge cases the engine must handle gracefully
- Zero-byte files: by default `MinSizeBytes = 1`, so they are excluded (all empty files are trivially "identical" and grouping thousands of them is noise). If the user lowers min size to 0, group them but do not byte-verify (nothing to read).
- Locked / in-use / permission-denied files: catch `IOException` / `UnauthorizedAccessException` per file, add to `SkippedPaths`, continue. Never abort the whole scan for one bad file.
- Very large files (multi-GB): stream, never load whole file into memory.
- Reparse points / symlinks / junctions: skip by default to avoid double-counting and infinite loops; only traverse when `FollowSymlinks` is on, and even then guard against directory cycles with a visited-set of canonical paths.
- The same physical file reachable via two paths (e.g. two scanned folders overlap, or a hardlink): de-duplicate the candidate list by canonical full path before grouping, so one file is never compared against itself. Hardlinks pointing to the same data are technically not wasted space; treat them as the same underlying file and do not offer to "delete a duplicate" of itself. If detecting hardlinks (same volume + file ID) is cheap via `BY_HANDLE_FILE_INFORMATION`, exclude same-file-ID members from a group; if not, at minimum exclude identical full paths.
- Files that change during the scan: if a read fails mid-hash, skip that file into `SkippedPaths`.

---

## 7. Window Shell & Navigation

`MainWindow`:
- Custom title bar: `ExtendsContentIntoTitleBar = true`, set a draggable region, app title "Duplicates" on the left with the app icon, theme handled by the system caption buttons. Use the Windows App SDK 1.8 `TitleBar` conveniences where they simplify this. Title bar height standard (48px).
- Backdrop: default `MicaBackdrop` with `MicaKind.BaseAlt` (darker, premium). Make backdrop a Setting (Mica / Mica Alt / Acrylic / Solid). When "Solid", use `bg.base` from the active theme. Fall back to Solid if the system does not support the chosen backdrop.
- Host a `NavigationView` (left pane) inside the window.

`NavigationView`:
- Pane width: 260px expanded, auto-collapses to icons on narrow widths (`PaneDisplayMode = Auto`, `CompactModeThresholdWidth` default). Back button hidden (flat nav, no deep stack).
- Menu items (top):
  - **Scan** (icon: a magnifier or radar glyph) -> `ScanPage`
  - **Results** (icon: a list/files glyph) -> `ResultsPage`. Disabled/greyed until a scan has produced results, or shows an empty state if navigated to early.
- Footer item:
  - **Settings** (gear) -> `SettingsPage`
- A `Frame` in the content region hosts the pages. Default page on launch: `ScanPage`.

Window minimum size: 960 x 640. Default size: 1200 x 800.

---

## 8. Screen: Scan (ScanPage)

Purpose: configure and launch a scan. This page has two visual states in one page: the **setup** state and the **scanning/progress** state (swap the content area, do not navigate away, so cancellation returns cleanly).

### 8.1 Layout (setup state)

```
┌───────────────────────────────────────────────────────────┐
│  Title: "Scan for duplicates"        (TitleTextBlockStyle) │
│  Subtitle: short one-line description (BodyStrong, muted)  │
│                                                            │
│  ┌── Folders card ──────────────────────────────────────┐ │
│  │  [ + Add folder ]   (also accepts drag-and-drop)      │ │
│  │  • C:\Users\...\Pictures              [Subfolders] [x]│ │
│  │  • D:\Media                           [Subfolders] [x]│ │
│  │  (empty state when none added, see 8.4)               │ │
│  └───────────────────────────────────────────────────────┘ │
│                                                            │
│  ┌── Options (SettingsExpander) ───────────────────────┐  │
│  │  ▸ File size range      [ min ]  to  [ max ] presets │  │
│  │  ▸ File types           [All] [Images][Video]...     │  │
│  │  ▸ Ignore hidden files            [toggle on]        │  │
│  │  ▸ Ignore system files            [toggle on]        │  │
│  │  ▸ Verify byte-by-byte            [toggle on]        │  │
│  └──────────────────────────────────────────────────────┘  │
│                                                            │
│                         [  Start scan  ]  (mint, primary)  │
└───────────────────────────────────────────────────────────┘
```

Content is centered in a column with `MaxWidth ≈ 900`, generous vertical spacing (24px between cards). Use `SettingsCard` / `SettingsExpander` from the Community Toolkit for the options block to get the native Windows 11 settings aesthetic.

### 8.2 Folders
- "Add folder" opens a folder picker (`FolderPicker`; in WinUI 3 it must be initialized with the window handle, or use the WinAppSDK 1.8 picker that accepts a `WindowId`).
- Support drag-and-drop of folders onto the card (`DragOver`/`Drop`, accept `StorageItems` that are folders).
- Each folder row: full path (ellipsized in the middle if long, full path in a tooltip), a per-folder "Subfolders" toggle is optional; simpler is one global `IncludeSubfolders` toggle in Options. Use the global toggle for v1. A remove (x) button per row.

### 8.3 File type filter (`FileTypeFilter`)
Model:
- Mode `All` (default): no extension filtering, scan everything. This is the recommended default and directly satisfies "as many file formats as possible."
- Mode `Categories`: user selects one or more predefined categories (multi-select chips / toggle buttons). Categories map to extension sets:
  - Images: jpg, jpeg, png, gif, bmp, tiff, tif, webp, heic, heif, raw, cr2, nef, arw, dng, svg, ico, psd
  - Video: mp4, mkv, mov, avi, wmv, flv, webm, m4v, mpg, mpeg, 3gp, ts
  - Audio: mp3, flac, wav, aac, ogg, m4a, wma, opus, aiff, alac
  - Documents: pdf, doc, docx, xls, xlsx, ppt, pptx, txt, rtf, odt, ods, odp, epub, md, csv
  - Archives: zip, rar, 7z, tar, gz, bz2, xz, iso, cab
  - Code: include a sensible set (cs, js, ts, py, java, kt, cpp, h, html, css, json, xml, yml, sql) only if Category mode and "Code" is chosen
- Mode `CustomExtensions`: a free-text input where the user types extensions (e.g. "psd, ai, sketch"), parsed into a normalized lowercase set.
Keep `All` front and center; categories and custom are progressive disclosure. Filtering by extension is purely a convenience to shrink the scan; the engine never needs the extension to detect duplicates.

### 8.4 Empty state (no folders)
Centered illustration-light empty state inside the Folders card: a folder glyph, "No folders added yet", "Add a folder or drag one here to begin." plus the Add button. The Start scan button is disabled until at least one folder is present.

### 8.5 Scanning / progress state
When Start scan is pressed, swap the content area to the progress view:
- Large phase label (e.g. "Hashing files...") mapped from `ScanPhase`.
- A `ProgressBar`: indeterminate during Enumerating and GroupingBySize (count unknown), determinate during FullHashing/Verifying (`BytesProcessed / TotalBytesToProcess`).
- Stat row: files discovered, files processed, data processed (human-readable), elapsed time, and a rough ETA during the determinate phase.
- Current file path, truncated, muted, monospace optional.
- A prominent **Cancel** button. Cancelling returns to the setup state with options preserved; no partial results are shown (or, if a scan completes, auto-navigate to Results).
- On completion, automatically navigate to `ResultsPage`.

---

## 9. Screen: Results (ResultsPage)

Purpose: review duplicate groups, choose what to delete safely, delete.

### 9.1 Layout

```
┌───────────────────────────────────────────────────────────────────────┐
│  Summary bar:  128 groups · 412 duplicate files · 6.4 GB reclaimable   │
│  Toolbar: [search]  [Sort ▾]  [Auto-select ▾]  [Select all][Clear]     │
│                                            [ Delete 0 files (0 B) ]     │
├──────────────────────────────────────────┬────────────────────────────┤
│  Groups list (scroll)                     │  Preview pane (optional)   │
│  ┌── group (Expander) ──────────────────┐ │  ┌──────────────────────┐  │
│  │ [thumb] photo.jpg   2.1 MB  ×3  4.2MB│ │  │   image / metadata    │ │
│  │   ▸ expand to members                │ │  │   of selected file    │ │
│  │   ☑ C:\...\photo.jpg   2025-01-03    │ │  └──────────────────────┘  │
│  │   ☐ D:\...\photo.jpg   2024-11-20 ★  │ │                            │
│  │   ☑ E:\...\photo.jpg   2025-02-10    │ │                            │
│  └──────────────────────────────────────┘ │                            │
│  ... more groups ...                       │                            │
└──────────────────────────────────────────┴────────────────────────────┘
```

The preview pane is collapsible. On narrower windows it can drop below the list or hide entirely.

### 9.2 Summary bar
Three numbers, prominent: group count, total duplicate files (excess copies), total reclaimable bytes. The reclaimable number updates to reflect the **current selection** as well: show both "6.4 GB reclaimable total" and the Delete button reflects "selected" size.

### 9.3 Toolbar controls
- **Search**: filter groups by filename or path substring (case-insensitive). Filters the visible list, does not change selection.
- **Sort**: by Reclaimable size (default, desc), by File size, by Number of copies, by File name, by File type. 
- **Auto-select**: applies a rule across all groups, marking copies for deletion while always leaving exactly one kept per group. Rules:
  - Keep newest (by ModifiedUtc), delete older copies
  - Keep oldest, delete newer copies
  - Keep shortest path (often the "original" location), delete the rest
  - Keep file in a preferred folder: user picks a folder; copies inside it are kept, others selected. If a group has none in the preferred folder, fall back to "keep newest" for that group.
  Auto-select replaces the current selection. Show a tiny confirmation/undo affordance.
- **Select all / Clear**: Select all marks every copy except the one kept-by-current-rule in each group (never all). Clear deselects everything.
- **Delete button**: label shows live count and size, e.g. "Delete 207 files (4.1 GB)". Disabled when nothing is selected. Mint is the brand accent but Delete is a destructive action, so style Delete as a **danger** button (red), not mint. The mint accent is reserved for the primary safe action (Start scan) and selected/active states.

### 9.4 Group item (Expander)
Collapsed header shows: a thumbnail or file-type glyph, the representative file name, the per-file size, a count badge ("×3"), and the group's wasted bytes. Expanded shows each member as a row:
- A checkbox (selected = will be deleted)
- Full path (middle-ellipsized, full in tooltip)
- Modified date
- A "kept" star/badge on the one copy that is currently NOT selected (the survivor). 
- Per-row context menu (right-click) and/or hover actions: Open file, Open containing folder, Copy path, Exclude this file from the group.

### 9.5 Safety invariant (critical)
- It must be impossible to select every copy in a group. The UI must always leave at least one survivor per group. Enforce this in the ViewModel: when the user tries to check the last unchecked item in a group, refuse (and gently indicate why, e.g. the last unchecked checkbox is disabled, or attempting to check it instead unchecks another with a subtle hint). Simplest robust approach: the survivor is whichever copy is currently unchecked; never allow zero unchecked in a group.
- Before any deletion, run a final guard: if any group would lose all copies, abort that group and warn.

### 9.6 Deletion flow
- Clicking Delete opens a confirmation dialog (`ContentDialog`): "Delete N files (X reclaimable)? Files will be sent to the Recycle Bin." with Delete / Cancel. The confirm dialog can be suppressed via a Settings toggle (default: confirm ON).
- Default deletion target: **Recycle Bin**, via `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`. A Settings option allows "Permanent delete" but it must be off by default and clearly labeled as not recoverable.
- Deletion runs off the UI thread with progress (it can be slow for many files). Per-file failures are collected and shown in a summary ("204 deleted, 3 could not be deleted") rather than aborting.
- After deletion, remove deleted files from their groups, drop any group that now has fewer than 2 members, and update the summary totals live.

### 9.7 Empty state (no duplicates)
If a scan finds nothing: a clean centered state, a checkmark glyph, "No duplicates found", "Every file in the scanned folders is unique." plus a "New scan" button returning to ScanPage.

---

## 10. Visual Design System

### 10.1 Theme tokens
Define all colors as theme-aware brushes in `Themes/Colors.xaml` using `ResourceDictionary.ThemeDictionaries` with `Default` (Dark) and `Light` keys, exposed as named `SolidColorBrush` resources. Every surface/text/accent in the app references a token, never a raw hex inline. Token table (Dark / Light):

| Token | Dark | Light | Use |
|---|---|---|---|
| `BgBaseBrush` | `#0E0E11` | `#F6F6F8` | window solid background (when backdrop = Solid), root |
| `BgSurfaceBrush` | `#16161B` | `#FFFFFF` | page content panels |
| `BgCardBrush` | `#1C1C22` | `#FFFFFF` | cards, list rows, expanders |
| `BgCardHoverBrush` | `#232329` | `#F2F2F4` | hover state |
| `BgCardSelectedBrush` | `#22332E` | `#E6F7F1` | selected/active row (mint-tinted) |
| `StrokeSubtleBrush` | `#2A2A31` | `#ECECEF` | hairline dividers |
| `StrokeDefaultBrush` | `#34343C` | `#E0E0E4` | card borders, inputs |
| `StrokeStrongBrush` | `#43434C` | `#CFCFD6` | emphasized borders |
| `TextPrimaryBrush` | `#F4F4F6` | `#16161A` | headings, primary text |
| `TextSecondaryBrush` | `#A6A6B0` | `#55555F` | secondary text, subtitles |
| `TextTertiaryBrush` | `#6E6E78` | `#87878F` | muted, hints, paths |
| `AccentMintBrush` | `#2DD4A8` | `#14C0A0` | primary action fill, active accents |
| `AccentMintHoverBrush` | `#41DBB4` | `#0FA98C` | accent hover |
| `AccentMintPressedBrush` | `#22B591` | `#0C8A71` | accent pressed |
| `AccentMintTextBrush` | `#5EEAD4` | `#0C8E73` | mint used as text/icon (AA on bg) |
| `AccentMintSubtleBrush` | `#1F2DD4A8` (12% mint) | `#1F12B89A` (12% mint) | subtle accent backgrounds |
| `OnAccentBrush` | `#0E0E11` | `#0E0E11` | text/icon on mint fills (near-black) |
| `FocusRingBrush` | `#5EEAD4` | `#0C8E73` | keyboard focus rings |
| `SuccessBrush` | `#34D399` | `#10B981` | success states |
| `WarningBrush` | `#FBBF24` | `#D97706` | warnings |
| `DangerBrush` | `#F87171` | `#DC2626` | delete/destructive |
| `DangerHoverBrush` | `#FCA5A5` | `#B91C1C` | delete hover |

The primary button (Start scan) uses `AccentMintBrush` fill with `OnAccentBrush` text (near-black on mint reads crisp and premium, and passes contrast). The Delete button uses `DangerBrush`.

### 10.2 Typography
Use the Windows 11 system font, Segoe UI Variable, via the built-in WinUI `TextBlock` styles. Do not hand-set font sizes; map roles to native styles:
- Page title -> `TitleTextBlockStyle` (28)
- Section heading -> `SubtitleTextBlockStyle` (20)
- Body -> `BodyTextBlockStyle` (14)
- Emphasis -> `BodyStrongTextBlockStyle` (14 semibold)
- Caption / paths / counts -> `CaptionTextBlockStyle` (12)
Limit weights to Regular, Medium, SemiBold. File paths may use a monospace fallback (`Cascadia Code`/`Consolas`) at caption size if desired, but plain Segoe is acceptable.

### 10.3 Spacing & shape
- 4px spacing grid: 4 / 8 / 12 / 16 / 24 / 32. Card padding 16 to 20. Gap between cards 24.
- Corner radius: controls 4 (WinUI default `ControlCornerRadius`), cards/expanders/dialogs 8 (`OverlayCornerRadius`). Do not exceed 8; this is a utility app, not a rounded toy.
- Use `Border`/`Grid` layouts, never absolute positioning.

### 10.4 Component patterns
- **Primary button**: mint fill, near-black text, 32 to 36px height, 4px radius, hover -> `AccentMintHoverBrush`, pressed -> `AccentMintPressedBrush`, disabled -> 38% opacity. 
- **Danger button** (Delete): `DangerBrush` fill, white text, same metrics, hover `DangerHoverBrush`.
- **Secondary button**: transparent fill, `StrokeDefaultBrush` border, `TextPrimaryBrush` text, hover fills `BgCardHoverBrush`.
- **Cards / list rows**: `BgCardBrush`, 1px `StrokeSubtleBrush` border, 8px radius, hover `BgCardHoverBrush`, selected `BgCardSelectedBrush` + 1px `AccentMintBrush` left accent or border.
- **Checkboxes**: native, accent color set to mint.
- **Inputs**: `BgCardBrush`, border brightens to `AccentMintBrush` on focus with a subtle `FocusRingBrush` ring.
- **NavigationView selected item**: native selection indicator recolored to mint.

### 10.5 Accent application restraint
Mint is the single accent. Use it for: the primary action, selection indicators, active toggles, focus, and small status accents. Do NOT mint-flood the UI. Surfaces stay charcoal/white, text stays primary/secondary, and the eye is drawn to mint only where action lives. Destructive deletion is red, not mint.

### 10.6 States (must all be designed)
- Loading: scanning progress view (Section 8.5); for the preview pane, a small spinner.
- Empty: Scan page no-folders state (8.4), Results no-duplicates state (9.7), Results-before-first-scan placeholder ("Run a scan to see results").
- Error: per-file scan/delete failures surfaced as a non-blocking summary `InfoBar` ("3 files were skipped, view details"). Catastrophic errors (e.g. folder no longer exists) -> an `InfoBar` with a retry.
- Disabled: Start scan disabled until a folder is chosen; Delete disabled until a selection exists; both visibly distinct (reduced opacity + no hover).
- Overflow: long paths middle-ellipsized with full-path tooltips; long lists virtualized (Section 12).

### 10.7 Preview rendering (format-aware, best-effort)
The preview pane shows the currently focused file:
- Images (the categories listed under Images): render a downscaled `BitmapImage` thumbnail plus dimensions and size. Decode at a capped size for memory.
- Other types: show a large file-type glyph, the file name, full path, size, created/modified dates, and the duplicate group's reclaimable size. No attempt to render video/audio/document contents in v1 (out of scope), just rich metadata. This keeps "as many formats as possible" true for detection while keeping preview simple and fast.

---

## 11. Settings (SettingsPage) and persistence

Use `SettingsExpander`/`SettingsCard`. Groups:

Appearance
- Theme: System / Light / Dark (default System). Applied at runtime via the root element `RequestedTheme` / a `ThemeService`.
- Backdrop: Mica / Mica Alt / Acrylic / Solid (default Mica Alt).

Scanning defaults
- Default minimum file size (default 1 byte; offer presets: any, 1 KB, 1 MB).
- Verify byte-by-byte by default (default ON).
- Ignore hidden / system files by default (default ON / ON).
- Hashing concurrency: Auto / 1 / 2 / 4 / 8 (default Auto = ProcessorCount).

Deletion
- Default action: Recycle Bin / Permanent (default Recycle Bin; Permanent shows a warning).
- Confirm before deleting (default ON).

About
- App name "Duplicates", version (read from assembly), "by Finnvek", and the framework versions for reference.

Persistence: a `SettingsService` reads/writes a JSON file at `%LOCALAPPDATA%\Duplicates\settings.json` using `System.Text.Json` (source-generated context for AOT-friendliness, optional). Load on startup, save on change. Do not store scanned folders or results between sessions in v1 (keep it stateless except settings), though remembering the last-used folder list is a nice-to-have if trivial.

---

## 12. Performance

- The UI thread never blocks: all enumeration, hashing, verifying, and deleting run on background threads/tasks.
- Results lists must be UI-virtualized (`ItemsRepeater` or virtualizing `ListView`), since a scan can yield thousands of groups and tens of thousands of rows. Group expanders should lazy-realize member rows.
- Progress updates throttled to ~10/s.
- Thumbnails decoded at a capped resolution and ideally only for on-screen items.
- No heavy animations. Use cheap Fluent transitions only (page transition, expander reveal). No continuous animations that tax the integrated GPU.
- Memory: stream all file reads; never materialize file contents. Buffers reused per worker.
- Target: a scan of 100k files on an SSD should remain responsive (UI smooth, cancellable) throughout, with the size+partial-hash funnel doing most of the elimination before full hashing.

---

## 13. Accessibility

- All interactive elements keyboard-reachable with a logical tab order and a visible `FocusRingBrush` focus indicator.
- `AutomationProperties.Name` on icon-only buttons (add folder, remove, delete, theme toggle, row actions).
- Meaning never conveyed by color alone: the "kept" survivor has a star/badge and text, not just a color; the Delete button has a label, not just red.
- Contrast: all text meets WCAG AA against its background in both themes (the token table is chosen for this; verify mint-text tokens specifically).
- Respect system theme and high-contrast where feasible.
- Touch targets at least 32px on desktop for primary controls.

---

## 14. Build, Run, Packaging

### Project configuration (Duplicates.csproj essentials)
```
<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
<TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
<UseWinUI>true</UseWinUI>
<Nullable>enable</Nullable>
<LangVersion>14.0</LangVersion>
<ApplicationManifest>app.manifest</ApplicationManifest>
<EnableMsixTooling>true</EnableMsixTooling>   <!-- packaged path -->
```
`Duplicates.Engine.csproj`: `<TargetFramework>net10.0</TargetFramework>`, plain class library, no WinUI references, references `System.IO.Hashing`.

### CLI build/run (Codex-friendly)
- Ensure .NET 10 SDK is installed (`dotnet --version` -> 10.0.x).
- `dotnet restore`
- `dotnet build -c Debug`
- Run via Visual Studio F5 (recommended for WinUI debugging), or `dotnet run --project Duplicates` for the unpackaged path. Note: the packaged (MSIX) path is best launched/deployed from Visual Studio or via `MSBuild` MSIX targets; pure `dotnet run` works most smoothly with the unpackaged configuration.

### Packaging / distribution (personal use)
- Preferred: **packaged MSIX** with single-project MSIX tooling, gives a clean install/uninstall and Start-menu entry. For sideloading on her own machine, generate a self-signed certificate, trust it locally, and produce an `.msix`/`.msixbundle`. Codex should script the cert creation and document the one-time "trust this certificate" step.
- Acceptable alternative if MSIX/cert friction is a problem: **unpackaged** (`<WindowsPackageType>None</WindowsPackageType>`) self-contained x64 build (`dotnet publish -c Release -r win-x64 --self-contained`), producing a folder with `Duplicates.exe` she can run/pin directly. No certificate needed. Recycle-bin deletion, pickers (with window-handle init), and Mica all work unpackaged.
- Recommend packaged for the polished experience; fall back to unpackaged if certificate trust is awkward for a single-user tool. State clearly in the README which path was built.

---

## 15. Definition of Done (acceptance criteria)

The build is complete when all of these are true:

1. App launches to the Scan page with the custom title bar, Mica/Mica Alt backdrop, and a working NavigationView (Scan, Results, Settings).
2. The user can add multiple folders (picker and drag-and-drop), remove them, toggle subfolders, set a size range, choose All / category / custom-extension filters, and toggle hidden/system/verify options.
3. Start scan runs fully off the UI thread, shows live phased progress (enumerate, hash, verify) with counts, data processed, elapsed, ETA, and current file, and is cancellable at any time.
4. Detection correctly groups byte-identical files of any type using the size -> partial-hash -> full-hash -> optional byte-verify funnel, with no false positives when verify is ON. Unique-size and unique-partial-hash files are eliminated without full reads.
5. Results show groups sorted by reclaimable space, with summary totals, search, sort, auto-select rules (keep newest/oldest/shortest-path/preferred-folder), manual selection, and a live Delete button.
6. It is impossible to select all copies in a group; at least one survivor always remains, enforced in the ViewModel and re-checked before deletion.
7. Deletion sends files to the Recycle Bin by default (permanent only if explicitly enabled), runs with progress, reports per-file failures without aborting, and updates the results and totals live afterward.
8. Light and Dark themes both look correct and premium, every surface uses theme tokens (no inline hex), mint is the single accent used with restraint, and contrast passes AA in both themes.
9. Empty, loading, error, and disabled states are all implemented (no-folders, scanning, no-duplicates, before-first-scan, skipped-files InfoBar).
10. Settings persist across launches in `%LOCALAPPDATA%\Duplicates\settings.json`.
11. Large scans stay responsive: lists are virtualized, the UI thread never blocks, memory stays bounded (streamed reads).
12. The engine library has no UI dependency and could be unit-tested in isolation.

---

## 16. Out of Scope for v1 (do not build now; note as future)

- Near-duplicate / visually-similar image matching (perceptual hashing such as pHash/dHash). The current spec is exact-content only. Leave a clean seam (the matching method is conceptually pluggable) but do not implement perceptual matching now.
- Scheduled / background / automatic scans.
- Saved scan history and re-scan profiles.
- Cloud storage, network shares optimization, or multi-machine dedup.
- Audio/video/document content preview beyond metadata + image thumbnails.
- Localization (UI is English only for v1).

---

## 17. Guidance to the agent on scope and quality

- Implement Sections 1 to 15 fully. Do not implement Section 16.
- Keep the engine simple and correct first; optimize only the funnel as specified. Do not add caching layers, databases, or speculative parallelism schemes beyond the bounded concurrency described.
- Do not hardcode colors in XAML; everything goes through the theme tokens in Section 10.1.
- Prefer native WinUI controls and Community Toolkit `SettingsCard`/`SettingsExpander` over custom-drawn UI.
- If any package version here fails to restore, use the nearest newer stable release of the same package and note the substitution, rather than dropping to prerelease or an older major version.
- Produce a short README covering: how it was built (packaged vs unpackaged), how to run it, and the one-time certificate step if packaged.
- If a requirement here is ambiguous or appears to conflict with another, stop and ask rather than guessing, especially anything touching the deletion path.
