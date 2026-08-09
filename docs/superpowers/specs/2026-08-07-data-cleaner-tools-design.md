# Data Cleaner Tools Design

**Date:** 2026-08-07

**Status:** Approved direction; implementation is split into independently testable milestones.

**Source material:** 19 screenshots under `C:\Users\EmmaH\Downloads\duplicates-sreenshots` and the current `C:\Dev\Duplicates` source tree.

## Goal

Expand Duplicates from an exact-duplicate finder into a native Windows 11 file-cleaning toolkit while preserving the current product identity, visual system, safety guarantees, and two-production-project architecture.

The left navigation will expose every tool visible in the screenshots:

1. Duplicate files
2. Empty folders
3. Big files
4. Empty files
5. Temporary files
6. Similar images
7. Similar videos
8. Music duplicates
9. Invalid links
10. Broken files
11. Bad extensions
12. Bad names
13. EXIF remover
14. Video optimizer

The screenshots are a feature inventory, not a visual template. No Crococrypt/Czkawka branding, icons, layout, colors, or custom widget styling will be copied.

## Design Approaches Considered

### A. One large parameterized page and scanner

One page, one ViewModel, and one engine entrypoint switch behavior for all fourteen tools.

- Benefit: few files.
- Cost: large switch statements, many nullable properties, fragile XAML visibility rules, and difficult isolated testing.
- Verdict: rejected. It minimizes file count but not complexity.

### B. Fourteen independent pages and ViewModels

Every navigation item owns a complete page, ViewModel, scanner, progress flow, result list, and action bar.

- Benefit: each tool is locally understandable.
- Cost: duplicates folder selection, progress, cancellation, result selection, deletion, export, accessibility, and responsive layout fourteen times.
- Verdict: rejected. The repeated behavior would drift and make the app visually inconsistent.

### C. Shared native workbench with focused analyzers

Keep tool algorithms isolated, but share only the behavior that is genuinely common: path scope, run state, flat/grouped result presentation, file actions, progress, and navigation.

- Benefit: algorithms remain independently testable, common UX stays consistent, and the current exact-duplicate scanner remains intact.
- Cost: requires a small tool catalog and result-adapter layer.
- Verdict: selected. It is the smallest structure that supports all fourteen tools without a monolith or copied pages.

## Product Structure

### Navigation

The existing native `TitleBar + NavigationView + Frame` shell remains. The separate `Results` navigation item is removed; results belong to the currently selected tool, so the selected navigation item does not jump away when a scan completes.

The open pane contains three labeled groups:

- **Find duplicates**
  - Duplicate files
  - Similar images
  - Similar videos
  - Music duplicates
- **Clean storage**
  - Empty folders
  - Big files
  - Empty files
  - Temporary files
- **Inspect and repair**
  - Invalid links
  - Broken files
  - Bad extensions
  - Bad names
  - EXIF remover
  - Video optimizer

Settings stays in the native bottom Settings slot. Every item uses a native `SymbolIcon` or `FontIcon` from Segoe Fluent Icons, an English accessible name, and the existing red selection indicator.

At narrow widths, `NavigationView` keeps its existing compact behavior. Tool content must remain usable at 640 epx without horizontal scrolling.

### Pages

- `ScanPage` remains the setup/progress page for exact duplicate files.
- `ResultsPage` remains the exact-duplicate grouped result page.
- `AnalysisPage` is the shared setup/progress page for the eleven read-only analyzers.
- `AnalysisResultsPage` presents either flat findings or review-only similarity groups.
- `ExifRemoverPage` owns EXIF inspection and cleaning options.
- `VideoOptimizerPage` owns video queueing and transcode options.

The shell passes a `ToolKind` navigation parameter. The active navigation item remains selected when routing from setup to results. A `New scan` command returns to that tool's setup state.

### Shared path scope

All scan-based tools use one in-memory `PathScopeViewModel` for the current app session:

- Included folders
- Included individual files
- Excluded folders
- Excluded individual files
- Include subfolders
- Ignore hidden files
- Ignore system files
- Optional preferred folder used only by exact-duplicate survivor rules

Paths are not persisted. The canonical full path remains the engine value; display name and parent path remain presentation-only. Switching tools reuses the current scope, matching the reference application's useful behavior without storing scan history.

An exclusion applies to the exact path and, for a directory, every descendant. Overlapping included paths are canonicalized and de-duplicated case-insensitively. Explicitly included files outside included folders are allowed.

### Shared run state

Only one analysis or transform runs at a time. The shared state exposes:

- Current phase and path
- Discovered and processed counts
- Processed and total bytes where known
- Percentage or indeterminate progress
- Elapsed time and ETA where meaningful
- Cancellation
- Skipped paths and per-item failures

Navigation away from an active run requires confirmation. Cancellation stops after the current atomic file operation and never leaves a partial destination presented as successful.

## Architecture

### Existing boundary remains

`Duplicates.Engine` remains `net10.0` and contains no WinUI, Windows App SDK, WinRT, Windows Storage, or UI ViewModel references. `Duplicates` remains the Windows-specific app and owns WinUI, WinRT media APIs, settings, file mutation, and theme resources.

The existing public exact-duplicate entrypoint remains:

```csharp
DuplicateScanner.ScanAsync(
    ScanOptions options,
    IProgress<ScanProgress>? progress,
    CancellationToken cancellationToken)
```

New analyzers have focused public entrypoints rather than expanding `DuplicateScanner` into a general switchboard.

### Common engine inventory

A new `FileInventoryBuilder` enumerates files, directories, and reparse points for the new analyzers. It accepts a pure `AnalysisScope`, reports `AnalysisProgress`, and returns skipped-path records instead of failing the entire run.

The existing `FileWalker` is not replaced until inventory behavior is proven by regression tests. The duplicate scanner can adopt explicit files and exclusions through small extensions to `ScanOptions`, but its size/hash/byte-verification funnel remains unchanged.

### Analysis service

The app-level `AnalysisService` selects one focused analyzer from `ToolKind`. It contains orchestration only; detection lives in engine classes:

- `LargeFileAnalyzer`
- `EmptyFileAnalyzer`
- `EmptyFolderAnalyzer`
- `TemporaryFileAnalyzer`
- `InvalidLinkAnalyzer`
- `BrokenFileAnalyzer`
- `BadExtensionAnalyzer`
- `BadNameAnalyzer`
- `SimilarImageAnalyzer`
- `SimilarVideoAnalyzer`
- `MusicDuplicateAnalyzer`

Platform-dependent probing is injected through engine-defined interfaces and implemented in the app:

- `IImageSampleProvider`
- `IVideoSampleProvider`
- `IMusicMetadataProvider`
- `IFileFormatProbe`

Tests replace these providers with deterministic fakes. This preserves the engine boundary while allowing Windows Imaging Component and Windows Media APIs in production.

### Result models

The engine returns strongly typed results. The app maps them into two presentation shapes:

- `PathFindingViewModel` for a file, folder, or link with reason, metadata, suggestion, and selection state.
- `SimilarityGroupViewModel` containing media candidates, a reference item, similarity scores, metadata, and manual selection state.

Canonical result collections remain separate from visible search/sort collections. Selection, actions, totals, and post-action mutation always use canonical state, preserving the existing Results invariant.

### Media fingerprint cache

Image and video fingerprinting is expensive enough to justify a cache. It stores only derived fingerprints and media metadata, never scan results or selections.

Cache key:

```text
schema version + canonical path + file length + last-write UTC ticks
```

Location:

```text
%LOCALAPPDATA%\Duplicates\cache\media-fingerprints-v1.json
```

Writes use a temporary file and atomic replace. Entries with missing or changed source files are pruned during load. A schema mismatch discards the old cache safely. Settings provides one enable toggle, size/status text, and a `Clear cache` command. No cache JSON export, manual cache editing, or general enumeration cache is added.

## Tool Definitions

### 1. Duplicate files

Current byte-identical detection remains authoritative:

1. Size bucket
2. 64 KiB XxHash3
3. Full XxHash3
4. Same-physical-file elimination
5. Optional byte-for-byte verification, on by default

Enhancements:

- Shared included files/folders and exclusions
- Preferred folder from path scope
- Move selected copies
- CSV/JSON export
- Safe hard-link replacement
- Safe symbolic-link replacement

The invariant remains: at least one physical file survives every group. Search and sorting do not reset selection.

Hard-link and symbolic-link replacement are offered only for byte-identical duplicate groups. They are not offered for visually or logically similar media.

### 2. Empty folders

A result is a physically empty directory, not a directory that merely becomes empty after filters hide its contents. Included root folders are never results.

Batch removal:

1. Sort deepest path first.
2. Recheck immediately before action that the directory still exists and contains no entries.
3. Send it to the Recycle Bin by default.
4. Continue after per-folder failures and show details.

Parent directories that become empty after a child removal are not silently added to the same operation. A rescan finds them.

### 3. Big files

Return every file at or above the selected threshold, default 1 GiB. Results sort by size descending and show file name, complete path, size, modified date, and file type. There is no automatic selection.

### 4. Empty files

Return every regular file whose current length is exactly zero. Recheck the length immediately before deletion. Empty directories are not mixed into this tool.

### 5. Temporary files

Flag conservatively named temporary artifacts older than the selected age, default seven days:

- `.tmp`, `.temp`, `.partial`, `.part`, `.crdownload`, `.download`, `.dmp`, `.chk`
- Microsoft Office lock names beginning with `~$`
- names ending with `~`

`.bak` and `.old` are intentionally excluded from the default rule because they are commonly intentional backups. The tool scans only user-selected scope and does not silently add browser profiles or system folders.

Before deletion, recheck path, age, and that the file can be opened without conflicting write access. Findings are never preselected.

### 6. Similar images

Windows `BitmapDecoder` decodes supported images, applies EXIF orientation, and produces a normalized 32 x 32 luminance sample. The engine computes a 64-bit perceptual hash from the low-frequency DCT coefficients. The median is calculated from the 63 non-DC coefficients in the top-left 8 x 8 block, then all 64 coefficients are packed against that median.

Candidate generation first gates by aspect-ratio difference, then compares perceptual hashes by Hamming distance. Presets:

- Strict: distance at most 4
- Balanced: distance at most 8, default
- Broad: distance at most 12

Results show dimensions, format, file size, modified date, reference-relative similarity, and image preview. Connected matches form review groups, but no member is preselected and no automatic delete/survivor rule is run. This avoids treating cropped or edited images as interchangeable.

Supported detection is limited to formats that the installed Windows imaging codecs can decode. Decode failures appear in skipped details and can also be found by Broken files.

### 7. Similar videos

The Windows media stack reads duration, dimensions, bitrate, frame rate, and codec. Five thumbnails are sampled at 10%, 30%, 50%, 70%, and 90% of duration using `MediaClip` and `MediaComposition.GetThumbnailsAsync` or its per-frame equivalent.

Each frame receives the same perceptual hash as an image. Candidate videos must have:

- Duration difference at most 2% or two seconds, whichever is larger
- Aspect-ratio difference at most 5%
- Mean aligned-frame Hamming distance within the selected preset

Presets use mean distances 5, 9, and 13 for Strict, Balanced, and Broad. A failed or missing frame makes the item skipped, not falsely unique. Results show dimensions, duration, bitrate, frame rate, codec, size, path, and a generated preview.

No video is preselected for deletion or link replacement.

### 8. Music duplicates

Windows `MusicProperties` supplies title, contributing artist, album artist, album, track number, year, genre, bitrate, and duration.

A high-confidence group requires:

- Non-empty normalized title
- Non-empty normalized artist or album artist
- Equal normalized title and artist
- Duration difference at most two seconds

Normalization uses Unicode Form KC, invariant lowercase, punctuation-to-space, and collapsed whitespace. It does not remove words such as `live`, `remaster`, or `mix`, because those may identify genuinely different recordings.

When title or artist metadata is missing, the item is not grouped by filename alone. Exact byte duplicates remain available through Duplicate files. Results show confidence, title, artist, album, year, bitrate, duration, genre, size, and full path. No automatic deletion or link replacement is offered.

This design deliberately avoids bundling FFmpeg, Chromaprint, or a native acoustic-fingerprint executable. A later acoustic-fingerprint phase can be added only with an explicit dependency and licensing review.

### 9. Invalid links

Enumerate file symbolic links, directory symbolic links, and junctions. `FileSystemInfo.LinkTarget` identifies links; `ResolveLinkTarget(returnFinalTarget: true)` validates the destination.

A finding is produced when:

- The target does not exist
- The chain contains too many levels
- Resolution fails due to an invalid path or I/O error

Deleting a finding deletes only the link or junction entry, never its target. Hard links are not included because they do not have an independently breakable target path.

### 10. Broken files

The tool reports files that are unreadable or malformed for a supported validator:

- All files: opening and reading the header fails
- Images: Windows bitmap decoder cannot parse the container
- Audio/video: Windows media properties/probe cannot open the media
- ZIP: `ZipArchive` cannot read the central directory

Unknown formats that can be read are not labeled broken. Zero-byte files belong to Empty files. Password-protected or codec-unsupported files are reported with a precise `Unsupported or protected` reason rather than a generic corruption claim.

The scan is read-only and never attempts repair.

### 11. Bad extensions

Inspect content signatures without trusting the current extension. The initial signature table covers:

- JPEG, PNG, GIF, BMP, TIFF, WebP
- PDF
- ZIP, RAR, 7z
- MP3 with ID3 or MPEG frame sync, FLAC, WAV, Ogg
- MP4/QuickTime, Matroska/WebM, AVI

Each signature maps to an allowed extension set and one recommended extension. A finding appears only when the signature is recognized and the current extension is absent or outside the allowed set. Ambiguous ZIP-based Office/OpenDocument formats are not renamed based on ZIP alone.

Rename uses the recommended extension, preserves the stem, checks collisions case-insensitively, and asks for confirmation. It does not rewrite file contents.

### 12. Bad names

Flag only objectively risky names:

- ASCII control characters or Unicode bidirectional override/isolate characters
- Leading or trailing whitespace
- Trailing dot
- Reserved DOS device stem such as `CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, or `LPT1`-`LPT9`
- Empty or whitespace-only stem

The suggested name removes control/bidirectional characters, trims unsafe suffixes, replaces invalid Windows characters with `_`, and appends `_file` to reserved stems. The user may edit the suggestion before rename. Ordinary long, punctuated, Unicode, or multi-dot names are not labeled bad.

### 13. EXIF remover

Supported input is a Windows-decodable image with writable metadata. The default privacy-clean action removes:

- GPS block
- Camera make/model/serial and lens identifiers
- Date/time fields
- Author, copyright, comments, title, subject, and keywords
- Embedded EXIF thumbnail
- XMP/IPTC personal metadata blocks

It preserves pixel data, image format, dimensions, EXIF orientation needed for correct display, ICC color profile, DPI, and animation/frame structure where the codec supports it.

Every file is written to a sibling temporary file first. The output is decoded and dimensions are verified. Default behavior creates `<name>.clean<extension>`. `Replace original after verification` is an explicit per-run toggle, off by default. Replacement renames the original to a rollback file, moves the verified output into place, sends the rollback file to the Recycle Bin, and restores it on failure.

Unsupported metadata layouts are reported per file; the original is never modified on failure.

### 14. Video optimizer

Use the built-in `Windows.Media.Transcoding.MediaTranscoder`. Default output is a new sibling `<name>.optimized.mp4`; source files are never overwritten automatically.

Presets:

- Smaller: H.264/AAC MP4, cap at 720p, target bitrate derived from resolution
- Balanced: H.264/AAC MP4, cap at 1080p, default
- High quality: H.264/AAC MP4, preserve source resolution up to 2160p

The page shows source codec, dimensions, duration, bitrate, size, target profile, output path, per-file progress, and final size saving. Hardware acceleration is attempted by default and can be disabled per run. `PrepareFileTranscodeAsync` failures such as missing codec or invalid profile are shown without leaving a partial output. A completed output must be playable through the media probe and smaller than the source; an output that is not smaller is retained only after the user explicitly chooses to keep it.

## Result Actions

### Common safe actions

- Open
- Reveal in File Explorer
- Copy full path
- Search and sort
- Select or clear selection
- Move selected files to a chosen folder
- Delete selected items using the configured Recycle Bin/permanent mode
- Export canonical results to CSV or JSON

Move handles collisions with a dialog offering Skip, Keep both with a generated suffix, or Cancel operation. It never silently overwrites.

Export records the tool name, scan time, scope, path, reason/group, media fields, and skipped paths. Exporting does not persist live result state inside the app.

### Exact-duplicate link replacement

Hard-link and symbolic-link replacement uses an explicit survivor. For every selected duplicate:

1. Verify both files still exist and remain byte-identical.
2. For hard links, verify same volume and supported filesystem.
3. Rename the duplicate to a unique rollback path in the same directory.
4. Create the link at the original duplicate path.
5. Verify the created link points to the survivor or shares the same physical file identity.
6. Send the rollback file to the Recycle Bin.
7. Restore the rollback path if any step before verification fails.

Symbolic-link creation explains that Windows Developer Mode or elevation may be required. No automatic elevation is requested.

## Settings

Only settings necessary for these tools are added.

### Appearance

Existing Theme and Window background settings remain unchanged.

### Scanning defaults

- Default minimum duplicate-file size: existing value
- Include subfolders: default on
- Verify byte-by-byte: existing, default on
- Ignore hidden files: existing, default on
- Ignore system files: existing, default on
- File/hash concurrency: existing Auto/1/2/4/8
- Big-file threshold: 1 GiB
- Temporary-file minimum age: 7 days

### Similarity and media

- Image similarity: Strict/Balanced/Broad, default Balanced
- Video similarity: Strict/Balanced/Broad, default Balanced
- Media processing concurrency: Auto/1/2/4, default Auto capped at 2
- Use media fingerprint cache: on
- Cache status and Clear cache command

### File actions

- Default deletion action: existing Recycle Bin/Permanent
- Confirm before deleting: existing, default on
- Confirm before move, rename, metadata replacement, or link replacement: always on and not disableable

EXIF category choices, video output profile, hardware acceleration, and replace-original behavior are per-run options rather than global settings.

### Explicitly not added

- Language selector; UI remains English
- Manual application scale; WinUI and Per-Monitor V2 DPI remain authoritative
- Icon-only navigation mode
- Persisted window or column sizes
- Log-line limits
- Linux-only filesystem controls
- Cache export/editing
- General presets with Save/Load/Reset
- Completion sound or notification

These controls are visible in the reference screenshots but are not required to implement the requested tools and would add clutter or duplicate platform behavior.

## Visual Contract

- Keep the exact five `Palette*` colors in `Themes/Colors.xaml`.
- Keep `#D72323` as the single accent and danger token.
- Add no inline hex values.
- Keep native control templates, focus visuals, hover/pressed/disabled states, and high-contrast behavior.
- Use `AccentButtonStyle` only for the page's primary Start/Clean/Optimize command.
- Use default buttons for secondary actions and `CommandBar`/`MenuFlyout` for result actions.
- Use `SettingsCard` and `SettingsExpander` on Settings.
- Use grouped `ListView` for exact/similar groups and standard `ListView` for flat findings.
- Keep preview selection separate from action selection.
- Paths may wrap to two lines in lists; tooltips and preview show the full path without ellipsis.
- Preserve responsive breakpoints at 640 and approximately 1008 epx.
- Every icon-only command has an accessible name and tooltip.
- Every run status uses polite live-region announcements; destructive confirmation receives focus.

## Safety and Failure Handling

- Read-only analysis never mutates files.
- No finding is automatically selected except the existing exact-duplicate survivor rule.
- Similar media is always manual review.
- All mutations revalidate the path and the finding-specific predicate immediately before action.
- Batch operations continue after per-item failures and retain failed items in canonical results.
- Successful moves, deletes, renames, cleans, or optimizations update canonical and visible results together.
- A changed file is skipped with `File changed since scan`.
- Output uses temporary sibling files and rollback paths; partial outputs are removed after cancellation/failure.
- App shutdown during a transform prompts the user; it does not abandon an in-progress atomic replacement.
- No secrets, telemetry, analytics, network service, or cloud dependency is introduced.

## Delivery Order

1. Navigation, tool catalog, shared path scope, common run/results infrastructure
2. Big files and Empty files
3. Empty folders and Temporary files
4. Invalid links, Bad extensions, Bad names, and Broken files
5. Shared move/export/rename actions and exact-duplicate link replacement
6. Media providers and fingerprint cache
7. Similar images
8. Similar videos
9. Music duplicates
10. EXIF remover
11. Video optimizer
12. Settings completion, accessibility, performance, packaged/unpackaged UAT, and documentation

Each numbered stage must build, pass relevant tests, and be usable before the next begins. Media and transform work does not block shipping the completed filesystem tools.

## Acceptance Criteria

- All fourteen tools are reachable from the native left navigation.
- Current exact duplicate detection and deletion invariants still pass unchanged.
- Every scan supports cancellation, skipped paths, included/excluded paths, and canonical result state.
- Every tool implements its precise detection rule above and does not claim unsupported formats are broken or duplicated.
- Similar image/video/music results are review-only by default.
- Empty-folder, empty-file, temporary-file, rename, link, EXIF, and optimization actions revalidate before mutation.
- Recycle Bin remains the default deletion target.
- The five-color palette and native WinUI control contract tests pass.
- Engine tests, App ViewModel tests, Debug x64 build, format verification, and manual packaged/unpackaged acceptance pass.

## Authoritative API Notes

- Windows Graphics Imaging exposes `BitmapDecoder`, `BitmapEncoder`, `BitmapProperties`, and `SoftwareBitmap` for image decoding and metadata work: <https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging>
- `BitmapEncoder.CreateForTranscodingAsync` preserves image data while allowing metadata changes: <https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.createfortranscodingasync>
- Windows Media `MediaComposition.GetThumbnailAsync` renders a frame at a requested timeline position: <https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.mediacomposition.getthumbnailasync>
- `MediaTranscoder.PrepareFileTranscodeAsync` prepares a source/destination/profile operation and exposes codec/profile failure reasons: <https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/transcode-media-files>
- `MusicProperties` exposes title, artist, bitrate, duration, genre, and year through Windows file properties: <https://learn.microsoft.com/en-us/uwp/api/windows.storage.fileproperties.musicproperties>
- `.NET FileSystemInfo.ResolveLinkTarget` resolves symbolic links and junctions and reports broken chains: <https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.resolvelinktarget>
- Win32 hard links are same-volume filesystem references; symbolic-link creation can require Developer Mode when unelevated: <https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions> and <https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createsymboliclinkw>
