# Data Cleaner Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add all fourteen file-cleaning tools identified in the reference screenshots while preserving Duplicates' native WinUI 3 style, current exact-duplicate safety guarantees, and UI-free engine boundary.

**Architecture:** Keep `Duplicates.Engine` platform-neutral and add focused analyzers plus injected media-probe interfaces. Keep Windows imaging, media transcoding, settings, navigation, file mutation, caching, and WinUI presentation in `Duplicates`. Share only path scope, run state, result presentation, and file-action infrastructure; do not combine the fourteen algorithms into one scanner.

**Tech Stack:** .NET SDK 10.0.301, C# 14, WinUI 3, Windows App SDK 1.8.260529003, CommunityToolkit.Mvvm 8.4.2, CommunityToolkit WinUI SettingsControls 8.2.251219, System.IO.Hashing 10.0.9, Windows.Graphics.Imaging, Windows.Media.Editing, Windows.Media.Transcoding, xUnit.

## Global Constraints

- `Duplicates.Engine` stays `net10.0` and must not reference WinUI, Windows App SDK, WinRT, app services, or app ViewModels.
- Preserve `DuplicateScanner.ScanAsync(ScanOptions, IProgress<ScanProgress>?, CancellationToken)` as the exact-duplicate public entrypoint.
- Preserve the exact-duplicate invariant: at least one physical file remains in every group, including immediately before mutation.
- Keep canonical result collections separate from search/sort-visible collections for every tool.
- Store only settings and derived media fingerprints; do not persist scanned paths, findings, selections, results, or action history.
- Keep the five existing `Palette*` colors unchanged; add no inline XAML hex colors and no custom `ControlTemplate`.
- Use native WinUI controls, `AccentButtonStyle` only for primary actions, and Community Toolkit `SettingsCard`/`SettingsExpander` controls in Settings.
- User-visible UI text remains English; code identifiers and comments remain English.
- Default deletion remains Recycle Bin; transformations write and verify a sibling temporary output before any optional replacement.
- Similar images, similar videos, and music duplicates are manual-review results and are never automatically selected for deletion or link replacement.
- New dependency versions, if any become unavoidable, belong only in `Directory.Packages.props` and require a separate license and package-freshness check. This plan requires no new NuGet package.
- Preserve all unrelated and uncommitted user work. Never stage `PROJECT.md` or existing untracked plans unless the user separately requests it.
- Commit messages are concise Finnish imperative subjects. Never create a pull request.

---

## Planned File Structure

### Existing files to modify

- `Duplicates/MainWindow.xaml` — grouped tool navigation using the current native shell.
- `Duplicates/MainWindow.xaml.cs` — route `ToolKind` to setup, results, and transform pages while retaining selected navigation item.
- `Duplicates/AppServices.cs` — compose shared scope, analyzers, stores, platform providers, caches, and transform services.
- `Duplicates/Views/ScanPage.xaml(.cs)` — consume shared in-memory scope and route exact duplicate results without a separate Results navigation item.
- `Duplicates/Views/ResultsPage.xaml(.cs)` — add move/export/link commands without changing grouped selection behavior.
- `Duplicates/ViewModels/ScanViewModel.cs` — build `ScanOptions` from shared scope.
- `Duplicates/ViewModels/ResultsViewModel.cs` — map generic file-action targets and expose move/export/link operations.
- `Duplicates/Services/IFileActionService.cs` and `FileActionService.cs` — decouple filesystem actions from `DuplicateFileViewModel` and support files, folders, and links.
- `Duplicates/Models/AppSettings.cs`, `SettingsViewModel.cs`, and `SettingsPage.xaml` — add only the documented defaults and media-cache controls.
- `Duplicates.Engine/Models/ScanOptions.cs` and `FileEnumeration/FileWalker.cs` — add explicit included files and excluded paths without changing the duplicate funnel.
- Existing test files — protect current behavior and add native UI contracts.
- `AGENTS.md`, `memory/MEMORY.md`, `README.md`, and `PROJECT.md` — update architecture and supported-feature documentation only after implementation is verified.

### New engine files

- `Duplicates.Engine/Analysis/AnalysisScope.cs`
- `Duplicates.Engine/Analysis/AnalysisPhase.cs`
- `Duplicates.Engine/Analysis/AnalysisProgress.cs`
- `Duplicates.Engine/Analysis/AnalysisResult.cs`
- `Duplicates.Engine/Analysis/PathFinding.cs`
- `Duplicates.Engine/Analysis/SimilarityGroup.cs`
- `Duplicates.Engine/Analysis/SimilarityItem.cs`
- `Duplicates.Engine/Analysis/FileInventory.cs`
- `Duplicates.Engine/Analysis/FileInventoryBuilder.cs`
- `Duplicates.Engine/Analysis/Analyzers/LargeFileAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/EmptyFileAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/EmptyFolderAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/TemporaryFileAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/InvalidLinkAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/FileSignatureDetector.cs`
- `Duplicates.Engine/Analysis/Analyzers/BadExtensionAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/BadNameAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/BrokenFileAnalyzer.cs`
- `Duplicates.Engine/Analysis/Media/IImageSampleProvider.cs`
- `Duplicates.Engine/Analysis/Media/IVideoSampleProvider.cs`
- `Duplicates.Engine/Analysis/Media/IMusicMetadataProvider.cs`
- `Duplicates.Engine/Analysis/Media/IFileFormatProbe.cs`
- `Duplicates.Engine/Analysis/Media/PerceptualHash.cs`
- `Duplicates.Engine/Analysis/Analyzers/SimilarImageAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/SimilarVideoAnalyzer.cs`
- `Duplicates.Engine/Analysis/Analyzers/MusicDuplicateAnalyzer.cs`

### New app files

- `Duplicates/Models/ToolKind.cs`
- `Duplicates/Models/ToolDescriptor.cs`
- `Duplicates/Models/ToolOptions.cs`
- `Duplicates/Models/ScopePathKind.cs`
- `Duplicates/Models/FileActionTarget.cs`
- `Duplicates/Models/FileActionTargetKind.cs`
- `Duplicates/Models/MoveCollisionBehavior.cs`
- `Duplicates/Models/SimilarityPreset.cs`
- `Duplicates/Views/Controls/PathScopeEditor.xaml(.cs)`
- `Duplicates/ViewModels/ScopePathViewModel.cs`
- `Duplicates/ViewModels/PathScopeViewModel.cs`
- `Duplicates/ViewModels/AnalysisViewModel.cs`
- `Duplicates/ViewModels/AnalysisResultsViewModel.cs`
- `Duplicates/ViewModels/PathFindingViewModel.cs`
- `Duplicates/ViewModels/SimilarityGroupViewModel.cs`
- `Duplicates/ViewModels/SimilarityItemViewModel.cs`
- `Duplicates/ViewModels/ExifRemoverViewModel.cs`
- `Duplicates/ViewModels/VideoOptimizerViewModel.cs`
- `Duplicates/Views/AnalysisPage.xaml(.cs)`
- `Duplicates/Views/AnalysisResultsPage.xaml(.cs)`
- `Duplicates/Views/ExifRemoverPage.xaml(.cs)`
- `Duplicates/Views/VideoOptimizerPage.xaml(.cs)`
- `Duplicates/Services/AnalysisService.cs`
- `Duplicates/Services/AnalysisSessionStore.cs`
- `Duplicates/Services/IResultExportService.cs`
- `Duplicates/Services/ResultExportService.cs`
- `Duplicates/Services/IFileLinkService.cs`
- `Duplicates/Services/FileLinkService.cs`
- `Duplicates/Services/MediaFingerprintCache.cs`
- `Duplicates/Services/WindowsImageSampleProvider.cs`
- `Duplicates/Services/WindowsVideoSampleProvider.cs`
- `Duplicates/Services/WindowsMusicMetadataProvider.cs`
- `Duplicates/Services/WindowsFileFormatProbe.cs`
- `Duplicates/Services/IExifCleanerService.cs`
- `Duplicates/Services/ExifCleanerService.cs`
- `Duplicates/Services/IVideoOptimizerService.cs`
- `Duplicates/Services/VideoOptimizerService.cs`
- `Duplicates/Interop/WicMetadataInterop.cs`

### New test files

- `Duplicates.Engine.Tests/FileInventoryBuilderTests.cs`
- `Duplicates.Engine.Tests/StorageAnalyzerTests.cs`
- `Duplicates.Engine.Tests/InvalidLinkAnalyzerTests.cs`
- `Duplicates.Engine.Tests/FileSignatureDetectorTests.cs`
- `Duplicates.Engine.Tests/BadNameAnalyzerTests.cs`
- `Duplicates.Engine.Tests/BrokenFileAnalyzerTests.cs`
- `Duplicates.Engine.Tests/PerceptualHashTests.cs`
- `Duplicates.Engine.Tests/SimilarImageAnalyzerTests.cs`
- `Duplicates.Engine.Tests/SimilarVideoAnalyzerTests.cs`
- `Duplicates.Engine.Tests/MusicDuplicateAnalyzerTests.cs`
- `Duplicates.App.Tests/PathScopeViewModelTests.cs`
- `Duplicates.App.Tests/AnalysisViewModelTests.cs`
- `Duplicates.App.Tests/AnalysisResultsViewModelTests.cs`
- `Duplicates.App.Tests/FileActionServiceContractTests.cs`
- `Duplicates.App.Tests/MediaFingerprintCacheTests.cs`
- `Duplicates.App.Tests/ExifRemoverViewModelTests.cs`
- `Duplicates.App.Tests/VideoOptimizerViewModelTests.cs`
- `Duplicates.App.Tests/FileLinkServiceTests.cs`
- `Duplicates.App.Tests/CrossToolStateTests.cs`

---

### Task 1: Add the native tool catalog and shell navigation

**Files:**
- Create: `Duplicates/Models/ToolKind.cs`
- Create: `Duplicates/Models/ToolDescriptor.cs`
- Modify: `Duplicates/MainWindow.xaml`
- Modify: `Duplicates/MainWindow.xaml.cs`
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`

**Interfaces:**
- Produces: `ToolKind` with all fourteen values.
- Produces: `ToolDescriptor.For(ToolKind)` returning English title, subtitle, navigation group, and glyph.
- Preserves: one native `TitleBar`, one `NavigationView`, and one root `Frame`.

- [ ] **Step 1: Write failing navigation contract tests**

Add tests that load `MainWindow.xaml` and assert:

```csharp
string[] expectedTags =
[
    "DuplicateFiles", "SimilarImages", "SimilarVideos", "MusicDuplicates",
    "EmptyFolders", "BigFiles", "EmptyFiles", "TemporaryFiles",
    "InvalidLinks", "BrokenFiles", "BadExtensions", "BadNames",
    "ExifRemover", "VideoOptimizer",
];

Assert.Equal(expectedTags, NavigationTags(main));
Assert.Equal(3, main.Descendants(Presentation + "NavigationViewItemHeader").Count());
Assert.DoesNotContain(NavigationTags(main), tag => tag == "Results");
```

Also retain the existing one-title-bar/one-navigation-view/one-frame assertion and assert every navigation item has an icon and non-empty `Content`.

- [ ] **Step 2: Run the focused test and confirm RED**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~NativeWinUiContractTests"
```

Expected: failure because only `Scan` and `Results` tags exist.

- [ ] **Step 3: Add the catalog and grouped native navigation**

Define:

```csharp
public enum ToolKind
{
    DuplicateFiles,
    SimilarImages,
    SimilarVideos,
    MusicDuplicates,
    EmptyFolders,
    BigFiles,
    EmptyFiles,
    TemporaryFiles,
    InvalidLinks,
    BrokenFiles,
    BadExtensions,
    BadNames,
    ExifRemover,
    VideoOptimizer,
}

public sealed record ToolDescriptor(
    ToolKind Kind,
    string Title,
    string Subtitle,
    string NavigationGroup,
    string Glyph);
```

Use `NavigationViewItemHeader` labels `Find duplicates`, `Clean storage`, and `Inspect and repair`. Use only Segoe Fluent Icons glyphs or native symbols. Keep `IsSettingsVisible="True"`, pane width 260, and compact behavior.

Update selection routing so unknown tags return without navigation. Initially route only `DuplicateFiles` to `ScanPage`. For every other catalog item, show a non-destructive `ContentDialog` saying the tool is not installed in the current build. Task 4 replaces that temporary branch for analyzer tools, and Tasks 17-18 replace it for the transform tools.

- [ ] **Step 4: Run contracts and build**

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~NativeWinUiContractTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: PASS and successful Debug x64 build.

- [ ] **Step 5: Commit the shell milestone**

```powershell
git add Duplicates/Models/ToolKind.cs Duplicates/Models/ToolDescriptor.cs Duplicates/MainWindow.xaml Duplicates/MainWindow.xaml.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Laajenna työkalunavigaatio"
```

---

### Task 2: Add canonical included/excluded path scope

**Files:**
- Create: `Duplicates.Engine/Analysis/AnalysisScope.cs`
- Create: `Duplicates/Models/ScopePathKind.cs`
- Create: `Duplicates/ViewModels/ScopePathViewModel.cs`
- Create: `Duplicates/ViewModels/PathScopeViewModel.cs`
- Create: `Duplicates/Views/Controls/PathScopeEditor.xaml(.cs)`
- Create: `Duplicates.App.Tests/PathScopeViewModelTests.cs`
- Modify: `Duplicates.Engine/Models/ScanOptions.cs`
- Modify: `Duplicates.Engine/FileEnumeration/FileWalker.cs`
- Modify: `Duplicates.Engine.Tests/DuplicateScannerTests.cs`
- Modify: `Duplicates/ViewModels/ScanViewModel.cs`
- Modify: `Duplicates/Views/ScanPage.xaml(.cs)`
- Modify: `Duplicates/AppServices.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record AnalysisScope
{
    public IReadOnlyList<string> IncludedFolders { get; init; } = [];
    public IReadOnlyList<string> IncludedFiles { get; init; } = [];
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public bool IncludeSubfolders { get; init; } = true;
    public bool IgnoreHiddenFiles { get; init; } = true;
    public bool IgnoreSystemFiles { get; init; } = true;
}
```

- Extends `ScanOptions` with `Files` and `ExcludedPaths` while leaving `Folders` intact.
- Produces one `PathScopeViewModel` singleton shared by setup pages during the process lifetime.

- [ ] **Step 1: Write scope and duplicate-scanner exclusion tests**

Cover these exact cases:

```csharp
[Fact] public void AddPath_CanonicalizesAndDeduplicatesCaseInsensitively();
[Fact] public void ExcludePath_RemovesAnEquivalentIncludedFile();
[Fact] public void PreferredFolder_AllowsOnlyOneIncludedFolderToBePreferred();
[Fact] public async Task ScanAsync_ExplicitIncludedFiles_AreCompared();
[Fact] public async Task ScanAsync_ExcludedDirectory_RemovesAllDescendants();
[Fact] public async Task ScanAsync_ExcludedFile_DoesNotRemoveItsSibling();
```

Assert that presentation properties never replace canonical full paths.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "ExplicitIncludedFiles|ExcludedDirectory|ExcludedFile"
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~PathScopeViewModelTests"
```

- [ ] **Step 3: Implement path normalization and exclusion semantics**

`ScopePathViewModel` exposes `FullPath`, `DisplayName`, `ParentPath`, `Kind`, and `IsPreferred`. `PathScopeViewModel` owns included and excluded `ObservableCollection<ScopePathViewModel>` collections and commands to add/remove/prefer paths.

`FileWalker` must test exclusion after `Path.GetFullPath` and before reading metadata. Directory exclusion uses a separator-aware ancestor test so excluding `C:\Data\One` does not exclude `C:\Data\OneMore`.

An explicitly included file is accepted only when it exists, passes hidden/system, size, extension, and exclusion filters, and has not already been reached through an included folder.

Update `ScanViewModel` to consume the shared scope without creating a second string collection. Keep current settings refresh behavior while idle. Put the native included/excluded path editor in `Views/Controls/PathScopeEditor.xaml`; `ScanPage` uses it in this task and `AnalysisPage`, `ExifRemoverPage`, and `VideoOptimizerPage` reuse it later.

- [ ] **Step 4: Update Scan XAML with native include/exclude disclosure**

Keep the existing centered work area. The default visible list is Included paths. Put Excluded paths in a native collapsed `Expander`. Add folder and file picker buttons with text, access keys, and tooltips. A preferred folder uses a native `RadioButton` or menu command labeled `Prefer copies in this folder`; do not add a custom checkbox template.

- [ ] **Step 5: Run all existing scanner and Scan ViewModel tests**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ScanViewModelTests|FullyQualifiedName~PathScopeViewModelTests|FullyQualifiedName~NativeWinUiContractTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Expected: all current duplicate tests remain green; path scope tests pass.

- [ ] **Step 6: Commit canonical scope**

```powershell
git add Duplicates.Engine/Analysis/AnalysisScope.cs Duplicates.Engine/Models/ScanOptions.cs Duplicates.Engine/FileEnumeration/FileWalker.cs Duplicates.Engine.Tests/DuplicateScannerTests.cs Duplicates/Models/ScopePathKind.cs Duplicates/ViewModels/ScopePathViewModel.cs Duplicates/ViewModels/PathScopeViewModel.cs Duplicates/ViewModels/ScanViewModel.cs Duplicates/Views/Controls/PathScopeEditor.xaml Duplicates/Views/Controls/PathScopeEditor.xaml.cs Duplicates/Views/ScanPage.xaml Duplicates/Views/ScanPage.xaml.cs Duplicates/AppServices.cs Duplicates.App.Tests/PathScopeViewModelTests.cs Duplicates.App.Tests/ScanViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Yhtenäistä skannauspolut"
```

---

### Task 3: Build the shared file inventory and progress contract

**Files:**
- Create: `Duplicates.Engine/Analysis/AnalysisPhase.cs`
- Create: `Duplicates.Engine/Analysis/AnalysisProgress.cs`
- Create: `Duplicates.Engine/Analysis/FileInventory.cs`
- Create: `Duplicates.Engine/Analysis/FileInventoryBuilder.cs`
- Create: `Duplicates.Engine.Tests/FileInventoryBuilderTests.cs`

**Interfaces:**

```csharp
public enum AnalysisPhase { Enumerating, Inspecting, Comparing, Done }

public sealed record AnalysisProgress(
    AnalysisPhase Phase,
    long ItemsDiscovered,
    long ItemsProcessed,
    long BytesProcessed,
    long TotalBytes,
    string? CurrentPath);

public sealed class FileInventoryBuilder
{
    public FileInventory Build(
        AnalysisScope scope,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record InventoryFile(
    string FullPath,
    string FileName,
    string Extension,
    string DirectoryPath,
    long SizeBytes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    FileAttributes Attributes);

public sealed record InventoryDirectory(
    string FullPath,
    string Name,
    string ParentPath,
    int Depth,
    FileAttributes Attributes);
```

`FileInventory` contains files, directories, reparse-point paths, and skipped paths.

- [ ] **Step 1: Write inventory tests**

Test recursive/non-recursive enumeration, overlapping roots, explicit files, exact/ancestor exclusions, hidden/system flags, reparse-point capture without traversal, inaccessible-path skip reporting, cancellation, and included-root marking.

- [ ] **Step 2: Run inventory tests and confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FileInventoryBuilderTests"
```

- [ ] **Step 3: Implement iterative enumeration**

Use an explicit directory stack rather than `Directory.EnumerateFiles(... RecurseSubdirectories=true)` so the builder can record directories, capture reparse points, avoid cycles, and report failures per directory. Do not follow reparse points. Add a root-aware, case-insensitive seen-path set.

Progress reports at most every 100 ms except phase changes and Done, matching the existing duplicate scanner's throttling intent.

- [ ] **Step 4: Run inventory and duplicate engine tests**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
```

- [ ] **Step 5: Commit inventory foundation**

```powershell
git add Duplicates.Engine/Analysis Duplicates.Engine.Tests/FileInventoryBuilderTests.cs
git commit -m "Lisää yhteinen tiedostoinventaario"
```

---

### Task 4: Add analysis sessions and native setup/results pages

**Files:**
- Create: `Duplicates.Engine/Analysis/AnalysisResult.cs`
- Create: `Duplicates.Engine/Analysis/PathFinding.cs`
- Create: `Duplicates.Engine/Analysis/SimilarityItem.cs`
- Create: `Duplicates.Engine/Analysis/SimilarityGroup.cs`
- Create: `Duplicates/Models/ToolOptions.cs`
- Create: `Duplicates/Services/AnalysisSessionStore.cs`
- Create: `Duplicates/Services/AnalysisService.cs`
- Create: `Duplicates/ViewModels/AnalysisViewModel.cs`
- Create: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`
- Create: `Duplicates/ViewModels/PathFindingViewModel.cs`
- Create: `Duplicates/ViewModels/SimilarityItemViewModel.cs`
- Create: `Duplicates/ViewModels/SimilarityGroupViewModel.cs`
- Create: `Duplicates/Views/AnalysisPage.xaml(.cs)`
- Create: `Duplicates/Views/AnalysisResultsPage.xaml(.cs)`
- Create: `Duplicates.App.Tests/AnalysisViewModelTests.cs`
- Create: `Duplicates.App.Tests/AnalysisResultsViewModelTests.cs`
- Modify: `Duplicates/AppServices.cs`
- Modify: `Duplicates/MainWindow.xaml.cs`
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`

**Interfaces:**

```csharp
public sealed record AnalysisResult
{
    public required IReadOnlyList<PathFinding> Findings { get; init; }
    public required IReadOnlyList<SimilarityGroup> Groups { get; init; }
    public required IReadOnlyList<SkippedPath> SkippedPaths { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

public sealed record AnalysisSession(
    ToolKind Tool,
    AnalysisScope Scope,
    AnalysisResult Result,
    DateTimeOffset CompletedAt);

public interface IAnalysisService
{
    Task<AnalysisResult> RunAsync(
        ToolKind tool,
        AnalysisScope scope,
        ToolOptions toolOptions,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken);
}
```

`ToolOptions` is an app-owned closed record hierarchy containing `NoToolOptions`, `LargeFileToolOptions`, `TemporaryFileToolOptions`, `SimilarImageToolOptions`, `SimilarVideoToolOptions`, and `MusicDuplicateToolOptions`. `AnalysisService` pattern-matches the exact `(ToolKind, ToolOptions)` pair and rejects mismatches with `ArgumentException`. It may know app-owned `ToolKind`; the engine-owned `AnalysisResult` does not. `AnalysisSessionStore` wraps the result in app-owned `AnalysisSession`. Individual engine analyzers remain focused and typed.

- [ ] **Step 1: Write ViewModel state tests**

Assert setup → progress → results, cancellation, skipped paths, exception InfoBar, single-active-run guard, current path, canonical-versus-visible collections, search preservation, sorting, selection totals, and New scan reset.

- [ ] **Step 2: Confirm tests fail**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~AnalysisViewModelTests|FullyQualifiedName~AnalysisResultsViewModelTests"
```

- [ ] **Step 3: Implement store, ViewModels, and fake analyzer seam**

`AnalysisSessionStore` stores one current result and active tool in memory. It raises `ResultChanged`. New runs replace old results only after successful completion; cancellation leaves the previous completed result untouched.

`AnalysisResultsViewModel` owns canonical `_allFindings` and `_allGroups`, visible collections, preview selection, action selection, search, sort, and totals. Similarity groups start with no action selection.

- [ ] **Step 4: Build native pages**

`AnalysisPage` reuses the current Scan page hierarchy: title/subtitle, accent Start button, scope editor, native option `Expander`, `InfoBar`, `ProgressBar`, and Cancel. `AnalysisResultsPage` uses one native `CommandBar`, a flat or grouped `ListView`, a responsive `SplitView` preview, and a clean no-findings state.

Add contract tests for no layout tap handlers, accessible icon-only commands, native ListView/CommandBar/SplitView, full path availability, and disabled horizontal scrolling.

- [ ] **Step 5: Run App tests and build**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 6: Commit shared analysis UX**

```powershell
git add Duplicates.Engine/Analysis/AnalysisResult.cs Duplicates.Engine/Analysis/PathFinding.cs Duplicates.Engine/Analysis/SimilarityItem.cs Duplicates.Engine/Analysis/SimilarityGroup.cs Duplicates/Models/ToolOptions.cs Duplicates/Services/AnalysisSessionStore.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisViewModel.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs Duplicates/ViewModels/PathFindingViewModel.cs Duplicates/ViewModels/SimilarityItemViewModel.cs Duplicates/ViewModels/SimilarityGroupViewModel.cs Duplicates/Views/AnalysisPage.xaml Duplicates/Views/AnalysisPage.xaml.cs Duplicates/Views/AnalysisResultsPage.xaml Duplicates/Views/AnalysisResultsPage.xaml.cs Duplicates/AppServices.cs Duplicates/MainWindow.xaml.cs Duplicates.App.Tests/AnalysisViewModelTests.cs Duplicates.App.Tests/AnalysisResultsViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Lisää analyysityötilan perusta"
```

---

### Task 5: Generalize safe file actions and add move/export

**Files:**
- Create: `Duplicates/Models/FileActionTarget.cs`
- Create: `Duplicates/Models/FileActionTargetKind.cs`
- Create: `Duplicates/Models/MoveCollisionBehavior.cs`
- Create: `Duplicates/Services/IResultExportService.cs`
- Create: `Duplicates/Services/ResultExportService.cs`
- Create: `Duplicates.App.Tests/FileActionServiceContractTests.cs`
- Modify: `Duplicates/Services/IFileActionService.cs`
- Modify: `Duplicates/Services/FileActionService.cs`
- Modify: `Duplicates/ViewModels/ResultsViewModel.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`
- Modify: `Duplicates.App.Tests/Fakes.cs`
- Modify: `Duplicates.App.Tests/ResultsViewModelTests.cs`

**Interfaces:**

```csharp
public sealed record FileActionTarget(
    string FullPath,
    long SizeBytes,
    FileActionTargetKind Kind);

public interface IFileActionService
{
    Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<FileActionTarget> targets,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken);

    Task<FileOperationSummary> MoveAsync(
        IReadOnlyList<FileActionTarget> targets,
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<FileOperationResult> RenameAsync(
        FileActionTarget target,
        string newName,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Change fakes and tests first**

Update existing duplicate delete tests to assert the mapped `FileActionTarget`. Add filesystem-backed service tests for file/folder/link recycle dispatch, permanent dispatch, move skip, move keep-both suffix `(2)`, collision cancellation, invalid destination, rename collision, and per-item continuation.

- [ ] **Step 2: Run and confirm compile/test failure**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ResultsViewModelTests|FullyQualifiedName~FileActionServiceContractTests"
```

- [ ] **Step 3: Implement target-based actions**

For directories use `FileSystem.DeleteDirectory`; for regular files and file links use `FileSystem.DeleteFile`; for directory links use directory deletion without following the target. Recheck finding-specific predicates in the caller immediately before invoking the generic service.

Move creates the destination directory only after user confirmation, preserves file timestamps, and never overwrites. Keep-both generates `name (2).ext`, `name (3).ext`, and so on using case-insensitive existence checks.

- [ ] **Step 4: Implement deterministic CSV/JSON export**

`IResultExportService.ExportAsync` accepts an immutable snapshot of canonical results and a selected format. CSV uses RFC 4180 quoting and UTF-8 with BOM; JSON uses indented `System.Text.Json`. Include tool, generated UTC, scope summary, path, kind, reason, suggestion, group id, similarity, size, timestamps, media fields, and skipped paths.

- [ ] **Step 5: Run full App tests**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 6: Commit file actions**

```powershell
git add Duplicates/Models/FileActionTarget.cs Duplicates/Models/FileActionTargetKind.cs Duplicates/Models/MoveCollisionBehavior.cs Duplicates/Services/IFileActionService.cs Duplicates/Services/FileActionService.cs Duplicates/Services/IResultExportService.cs Duplicates/Services/ResultExportService.cs Duplicates/ViewModels/ResultsViewModel.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs Duplicates.App.Tests/Fakes.cs Duplicates.App.Tests/ResultsViewModelTests.cs Duplicates.App.Tests/FileActionServiceContractTests.cs
git commit -m "Yhtenäistä tiedostotoiminnot"
```

---

### Task 6: Implement Big files and Empty files

**Files:**
- Create: `Duplicates.Engine/Analysis/Analyzers/LargeFileAnalyzer.cs`
- Create: `Duplicates.Engine/Analysis/Analyzers/EmptyFileAnalyzer.cs`
- Create: `Duplicates.Engine.Tests/StorageAnalyzerTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisViewModel.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public Task<AnalysisResult> AnalyzeAsync(
    FileInventory inventory,
    long minimumSizeBytes,
    CancellationToken cancellationToken);
```

`EmptyFileAnalyzer` has no tool option and returns only current length zero.

- [ ] **Step 1: Write analyzer tests**

Large-file cases: exact threshold included, one byte below excluded, descending size order, explicit file, excluded file, cancellation. Empty-file cases: zero included, nonzero excluded, directory excluded, later size change captured by action revalidation.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~StorageAnalyzerTests"
```

- [ ] **Step 3: Implement analyzers and app option mapping**

Large files default to 1,073,741,824 bytes. Results expose size, modified UTC, extension, and reason `At least {threshold}`. Empty-file result reason is `File is empty`.

Add a `NumberBox` and Any/100 MB/1 GB/10 GB presets only for Big files. Empty files shows no unnecessary option control.

- [ ] **Step 4: Add action revalidation**

Before deleting an empty file, verify `FileInfo.Length == 0`. Before deleting a big-file finding, verify current length remains at or above the run threshold. Changed files remain in results with failure `File changed since scan.`

- [ ] **Step 5: Run engine, app, and build checks**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 6: Commit storage analyzers**

```powershell
git add Duplicates.Engine/Analysis/Analyzers/LargeFileAnalyzer.cs Duplicates.Engine/Analysis/Analyzers/EmptyFileAnalyzer.cs Duplicates.Engine.Tests/StorageAnalyzerTests.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisViewModel.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs
git commit -m "Lisää suuret ja tyhjät tiedostot"
```

---

### Task 7: Implement Empty folders and Temporary files

**Files:**
- Create: `Duplicates.Engine/Analysis/Analyzers/EmptyFolderAnalyzer.cs`
- Create: `Duplicates.Engine/Analysis/Analyzers/TemporaryFileAnalyzer.cs`
- Modify: `Duplicates.Engine.Tests/StorageAnalyzerTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisViewModel.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public sealed record TemporaryFileOptions(TimeSpan MinimumAge, DateTime UtcNow);
```

- [ ] **Step 1: Add failing test cases**

Empty folders: physically empty included, root excluded, directory containing an excluded file not considered empty, directory containing an empty child not considered empty, deepest-first sort, link directory excluded. Temporary files: every approved suffix/prefix, case-insensitivity, exact age boundary, fresh file excluded, `.bak`/`.old` excluded, ordinary tilde in middle excluded.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "EmptyFolder|TemporaryFile"
```

- [ ] **Step 3: Implement physical-empty and conservative-temp rules**

Determine physical emptiness from actual child counts recorded by inventory before analysis filters. Return directory targets with depth. Temporary matching uses the exact design-spec pattern list and `ModifiedUtc <= UtcNow - MinimumAge`.

- [ ] **Step 4: Revalidate before deletion**

Empty directory: enumerate one entry immediately before delete; if one exists, fail without mutation. Temporary file: verify name still matches, age still meets threshold, and an exclusive write-open succeeds; otherwise fail with `File is active or changed.`

- [ ] **Step 5: Run relevant tests and build**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 6: Commit cleanup analyzers**

```powershell
git add Duplicates.Engine/Analysis/Analyzers/EmptyFolderAnalyzer.cs Duplicates.Engine/Analysis/Analyzers/TemporaryFileAnalyzer.cs Duplicates.Engine.Tests/StorageAnalyzerTests.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisViewModel.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs
git commit -m "Lisää kansio- ja väliaikaissiivous"
```

---

### Task 8: Implement Invalid links

**Files:**
- Create: `Duplicates.Engine/Analysis/Analyzers/InvalidLinkAnalyzer.cs`
- Create: `Duplicates.Engine.Tests/InvalidLinkAnalyzerTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public sealed record LinkFinding(
    string FullPath,
    string? ImmediateTarget,
    string Reason,
    bool IsDirectoryLink);
```

- [ ] **Step 1: Write Windows link tests**

Create temporary file symlink, directory symlink, and junction fixtures when the OS permits. Cover valid link excluded, missing target included, relative target resolution, multi-hop valid chain, loop/too-many-levels failure, inaccessible target failure, and cancellation. Mark only fixture-creation permission failures as skipped with an explicit reason.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~InvalidLinkAnalyzerTests"
```

- [ ] **Step 3: Implement link analysis**

Use `FileSystemInfo.LinkTarget` to distinguish link entries and `ResolveLinkTarget(true)` for validation. Do not follow link targets during inventory. Map `IOException`, missing target, invalid path, and excessive levels to stable English reasons.

- [ ] **Step 4: Implement link-only delete revalidation**

Immediately before delete, assert `LinkTarget` is still non-null and final resolution still fails. Delete the link entry based on file/directory kind. Never call delete on the resolved target path.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
git add Duplicates.Engine/Analysis/Analyzers/InvalidLinkAnalyzer.cs Duplicates.Engine.Tests/InvalidLinkAnalyzerTests.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs
git commit -m "Lisää virheellisten linkkien tarkistus"
```

---

### Task 9: Implement content signatures and Bad extensions

**Files:**
- Create: `Duplicates.Engine/Analysis/Analyzers/FileSignatureDetector.cs`
- Create: `Duplicates.Engine/Analysis/Analyzers/BadExtensionAnalyzer.cs`
- Create: `Duplicates.Engine.Tests/FileSignatureDetectorTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public sealed record DetectedFileType(
    string Name,
    IReadOnlySet<string> AllowedExtensions,
    string RecommendedExtension);

public static DetectedFileType? Detect(ReadOnlySpan<byte> header, string path);
```

- [ ] **Step 1: Write one positive and one negative fixture per signature**

Cover JPEG, PNG, GIF87a/GIF89a, BMP, little/big-endian TIFF, WebP RIFF, PDF, ZIP, RAR4/RAR5, 7z, ID3 MP3, MPEG-frame MP3, FLAC, WAV, Ogg, MP4/QuickTime `ftyp`, Matroska/WebM EBML, and AVI. Add truncated-header and random-byte tests. Add ZIP-based `.docx` ambiguity test that produces no rename suggestion.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FileSignatureDetectorTests"
```

- [ ] **Step 3: Implement bounded header detection**

Read at most 64 KiB. Never trust the extension while detecting. Normalize allowed extensions with a leading dot and invariant lowercase. For ISO BMFF, inspect compatible brands before recommending `.mp4` or `.mov`; return a generic allowed set if the brand is ambiguous.

- [ ] **Step 4: Implement bad-extension findings and rename revalidation**

Finding fields: current extension, proper extension, detected type, full path. Before rename, re-read the signature, confirm the recommendation is unchanged, validate the new file name, and reject case-insensitive collisions.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
git add Duplicates.Engine/Analysis/Analyzers/FileSignatureDetector.cs Duplicates.Engine/Analysis/Analyzers/BadExtensionAnalyzer.cs Duplicates.Engine.Tests/FileSignatureDetectorTests.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs
git commit -m "Lisää tiedostopäätteen tarkistus"
```

---

### Task 10: Implement Bad names

**Files:**
- Create: `Duplicates.Engine/Analysis/Analyzers/BadNameAnalyzer.cs`
- Create: `Duplicates.Engine.Tests/BadNameAnalyzerTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public sealed record BadNameFinding(
    string FullPath,
    string CurrentName,
    string SuggestedName,
    IReadOnlyList<string> Reasons);
```

- [ ] **Step 1: Write rule and non-rule tests**

Positive cases: control character, each bidi override/isolate class, leading/trailing whitespace, trailing dot, reserved DOS stems with and without extension, empty stem. Negative cases: accented/emoji names, multiple dots, long valid name, punctuation, `CONTEXT.txt`, `COM10.txt`, and spaces inside a name.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~BadNameAnalyzerTests"
```

- [ ] **Step 3: Implement deterministic sanitization**

Normalize only for inspection; do not rewrite ordinary Unicode. Remove control/bidi characters, trim unsafe ends, replace `Path.GetInvalidFileNameChars()` with `_`, use `file` for an empty stem, and append `_file` to reserved stems. Preserve the original extension when safe.

- [ ] **Step 4: Add editable rename confirmation**

The dialog contains current full path, reason list, editable `TextBox` initialized to the suggestion, destination preview, collision message, Rename primary button, and Cancel. Disable Rename until the name is valid and distinct.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
git add Duplicates.Engine/Analysis/Analyzers/BadNameAnalyzer.cs Duplicates.Engine.Tests/BadNameAnalyzerTests.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs Duplicates/Views/AnalysisResultsPage.xaml Duplicates/Views/AnalysisResultsPage.xaml.cs
git commit -m "Lisää tiedostonimien tarkistus"
```

---

### Task 11: Implement Broken files with explicit validator coverage

**Files:**
- Create: `Duplicates.Engine/Analysis/Media/IFileFormatProbe.cs`
- Create: `Duplicates.Engine/Analysis/Analyzers/BrokenFileAnalyzer.cs`
- Create: `Duplicates/Services/WindowsFileFormatProbe.cs`
- Create: `Duplicates.Engine.Tests/BrokenFileAnalyzerTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/AppServices.cs`

**Interfaces:**

```csharp
public enum FileProbeStatus { Valid, Invalid, UnsupportedOrProtected }

public sealed record FileProbeResult(
    FileProbeStatus Status,
    string? ErrorType,
    string? Message);

public interface IFileFormatProbe
{
    Task<FileProbeResult> ProbeAsync(
        string path,
        DetectedFileType? detectedType,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write analyzer tests with a fake probe**

Cover readable unknown ignored, unreadable generic reported, invalid image/media/ZIP reported, unsupported codec reported separately, zero-byte ignored, changed file skipped, cancellation, and stable reason mapping.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~BrokenFileAnalyzerTests"
```

- [ ] **Step 3: Implement Windows probes**

Generic: open and read one byte. Image: create `BitmapDecoder`. Audio/video: obtain `StorageFile` and read the matching Windows media properties; attempt media clip/source open when properties are empty. ZIP: open `ZipArchive` in read mode and enumerate entries without extracting.

Map missing codec, password/protection, and unsupported container to `UnsupportedOrProtected`; do not claim corruption. Unknown readable formats return Valid.

- [ ] **Step 4: Add coverage disclosure to the UI**

AnalysisPage description must say: `Checks readability and validates Windows-supported images, audio, video, and ZIP containers.` Result reason and error type are visible; skipped details remain separate.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
git add Duplicates.Engine/Analysis/Media/IFileFormatProbe.cs Duplicates.Engine/Analysis/Analyzers/BrokenFileAnalyzer.cs Duplicates/Services/WindowsFileFormatProbe.cs Duplicates.Engine.Tests/BrokenFileAnalyzerTests.cs Duplicates/Services/AnalysisService.cs Duplicates/AppServices.cs Duplicates/Views/AnalysisPage.xaml
git commit -m "Lisää rikkoutuneiden tiedostojen tarkistus"
```

---

### Task 12: Add media provider contracts and atomic fingerprint cache

**Files:**
- Create: `Duplicates.Engine/Analysis/Media/IImageSampleProvider.cs`
- Create: `Duplicates.Engine/Analysis/Media/IVideoSampleProvider.cs`
- Create: `Duplicates.Engine/Analysis/Media/IMusicMetadataProvider.cs`
- Create: `Duplicates/Services/MediaFingerprintCache.cs`
- Create: `Duplicates/Services/WindowsImageSampleProvider.cs`
- Create: `Duplicates/Services/WindowsVideoSampleProvider.cs`
- Create: `Duplicates/Services/WindowsMusicMetadataProvider.cs`
- Create: `Duplicates.App.Tests/MediaFingerprintCacheTests.cs`
- Modify: `Duplicates/AppServices.cs`

**Interfaces:**

```csharp
public sealed record ImageSample(
    int Width,
    int Height,
    byte[] Luminance32x32,
    string Format);

public sealed record VideoSample(
    int Width,
    int Height,
    TimeSpan Duration,
    uint Bitrate,
    double FramesPerSecond,
    string Codec,
    IReadOnlyList<byte[]> LuminanceFrames32x32);

public sealed record MusicMetadata(
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    uint TrackNumber,
    uint Year,
    IReadOnlyList<string> Genres,
    uint Bitrate,
    TimeSpan Duration);
```

- [ ] **Step 1: Write cache tests**

Cover hit for identical path/length/mtime/schema, miss after length change, miss after mtime change, schema invalidation, missing-file pruning, malformed JSON recovery, cancellation, temp-file atomic save, and Clear.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~MediaFingerprintCacheTests"
```

- [ ] **Step 3: Implement cache without result persistence**

Use `%LOCALAPPDATA%\Duplicates\cache\media-fingerprints-v1.json`. Serialize a schema version and path-keyed records. Save to `.tmp`, flush, then `File.Move(temp, final, true)`. Never include selected state, group ids, included paths, or result ordering.

- [ ] **Step 4: Implement Windows providers**

Image provider: `StorageFile` → stream → `BitmapDecoder` → orientation-aware BGRA8 `SoftwareBitmap` scaled to 32 x 32 → integer luminance `(54R + 183G + 19B) >> 8`.

Video provider: `StorageFile` → `MediaClip.CreateFromFileAsync` → `MediaComposition` → thumbnails at 10/30/50/70/90 percent → same luminance conversion. Read video metadata through `VideoProperties` and encoding properties.

Music provider: `StorageFile.Properties.GetMusicPropertiesAsync` and map every declared field. Provider exceptions are returned as skipped paths by analyzers.

- [ ] **Step 5: Run App tests and packaged smoke build**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Manually probe one JPEG, one PNG, one MP4, and one MP3/WMA file. Confirm dimensions/duration are nonzero and a second probe is served from cache.

- [ ] **Step 6: Commit media foundation**

```powershell
git add Duplicates.Engine/Analysis/Media Duplicates/Services/MediaFingerprintCache.cs Duplicates/Services/WindowsImageSampleProvider.cs Duplicates/Services/WindowsVideoSampleProvider.cs Duplicates/Services/WindowsMusicMetadataProvider.cs Duplicates.App.Tests/MediaFingerprintCacheTests.cs Duplicates/AppServices.cs
git commit -m "Lisää median analyysipalvelut"
```

---

### Task 13: Implement Similar images perceptual matching

**Files:**
- Create: `Duplicates.Engine/Analysis/Media/PerceptualHash.cs`
- Create: `Duplicates.Engine/Analysis/Analyzers/SimilarImageAnalyzer.cs`
- Create: `Duplicates.Engine.Tests/PerceptualHashTests.cs`
- Create: `Duplicates.Engine.Tests/SimilarImageAnalyzerTests.cs`
- Create: `Duplicates/Models/SimilarityPreset.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisViewModel.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public static ulong Compute(ReadOnlySpan<byte> luminance32x32);
public static int Distance(ulong left, ulong right);

public sealed record SimilarImageOptions(int MaximumHammingDistance);
```

- [ ] **Step 1: Write deterministic hash tests**

Use generated 32 x 32 luminance arrays. Assert identical hash distance 0, brightness offset remains near, horizontal edge differs from vertical edge, one-bit distance calculation, input length validation, and deterministic output across repeated calls.

- [ ] **Step 2: Write analyzer grouping tests**

Use a fake provider for same image/different encoding sample, resized sample, unrelated sample, aspect-ratio gate, strict/balanced/broad thresholds, provider failure skip, cancellation, connected grouping, stable reference choice, and zero automatic selection at the ViewModel layer.

- [ ] **Step 3: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~PerceptualHashTests|FullyQualifiedName~SimilarImageAnalyzerTests"
```

- [ ] **Step 4: Implement pHash and bounded candidate comparison**

Compute a separable 32 x 32 DCT. Take the top-left 8 x 8 block, calculate the median from its 63 non-DC coefficients, compare all 64 coefficients against that median, and pack 64 bits. Filter by image extensions and aspect-ratio difference before Hamming comparisons. Use deterministic path ordering and connected components for groups of two or more.

Do not add automatic survivor rules. Expose reference-relative similarity as `100 * (64 - distance) / 64`.

- [ ] **Step 5: Add UI preset and image preview**

Strict maps to 4, Balanced to 8, Broad to 12. Results show similarity, dimensions, format, size, modified date, full path, and capped 512-pixel preview. Decode failure stays in skipped details.

- [ ] **Step 6: Run and commit**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
git add Duplicates.Engine/Analysis/Media/PerceptualHash.cs Duplicates.Engine/Analysis/Analyzers/SimilarImageAnalyzer.cs Duplicates.Engine.Tests/PerceptualHashTests.cs Duplicates.Engine.Tests/SimilarImageAnalyzerTests.cs Duplicates/Models/SimilarityPreset.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisViewModel.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs Duplicates/Views/AnalysisPage.xaml Duplicates/Views/AnalysisResultsPage.xaml
git commit -m "Lisää samankaltaisten kuvien haku"
```

---

### Task 14: Implement Similar videos frame matching

**Files:**
- Create: `Duplicates.Engine/Analysis/Analyzers/SimilarVideoAnalyzer.cs`
- Create: `Duplicates.Engine.Tests/SimilarVideoAnalyzerTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisViewModel.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public sealed record SimilarVideoOptions(int MaximumMeanFrameDistance);
```

- [ ] **Step 1: Write fake-provider tests**

Cover same content/re-encode, duration gate, two-second minimum tolerance, aspect gate, mean aligned-frame distance, strict/balanced/broad boundary, missing frame skip, codec failure skip, cancellation, deterministic grouping, and no automatic action selection.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SimilarVideoAnalyzerTests"
```

- [ ] **Step 3: Implement candidate gates and five-frame score**

Require five frames. Duration tolerance is `max(2 seconds, longer duration * 0.02)`. Aspect-ratio difference is at most 5%. Compare frame index to same index, average five Hamming distances, and group qualifying edges deterministically.

- [ ] **Step 4: Add result metadata and preview**

Strict/Balanced/Broad map to mean distances 5/9/13. Show dimensions, duration, bitrate, FPS, codec, size, modified date, path, and cached 50% thumbnail. Preview failure must not crash list selection.

- [ ] **Step 5: Validate with real media fixtures**

Create one short source video and one lower-bitrate transcode through the Windows transcoder, plus an unrelated video. Expected: source/transcode group together under Balanced; unrelated video does not.

Run:

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 6: Commit video similarity**

```powershell
git add Duplicates.Engine/Analysis/Analyzers/SimilarVideoAnalyzer.cs Duplicates.Engine.Tests/SimilarVideoAnalyzerTests.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisViewModel.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs Duplicates/Views/AnalysisPage.xaml Duplicates/Views/AnalysisResultsPage.xaml
git commit -m "Lisää samankaltaisten videoiden haku"
```

---

### Task 15: Implement Music duplicates metadata matching

**Files:**
- Create: `Duplicates.Engine/Analysis/Analyzers/MusicDuplicateAnalyzer.cs`
- Create: `Duplicates.Engine.Tests/MusicDuplicateAnalyzerTests.cs`
- Modify: `Duplicates/Services/AnalysisService.cs`
- Modify: `Duplicates/ViewModels/AnalysisResultsViewModel.cs`

**Interfaces:**

```csharp
public sealed record MusicDuplicateOptions(TimeSpan MaximumDurationDifference);
```

- [ ] **Step 1: Write normalization and grouping tests**

Cover Unicode Form KC, case, punctuation-to-space, repeated whitespace, contributing artist fallback to album artist, exact two-second boundary, greater duration exclusion, missing title exclusion, missing artist exclusion, same title/different artist exclusion, `Live` versus non-Live title separation, stable group order, metadata-provider failure skip, and no automatic selection.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~MusicDuplicateAnalyzerTests"
```

- [ ] **Step 3: Implement conservative metadata match**

Normalize with Form KC, invariant lowercase, Unicode punctuation to a single space, and whitespace collapse. Do not strip bracketed text or edition words. Require normalized title and artist plus duration within two seconds.

- [ ] **Step 4: Add metadata-rich grouped result**

Show confidence `High`, title, artist, album, year, track, bitrate, duration, genre, size, and full path. Explain in the page subtitle that matching uses Windows music metadata and duration, not acoustic fingerprinting.

- [ ] **Step 5: Run and commit**

```powershell
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
git add Duplicates.Engine/Analysis/Analyzers/MusicDuplicateAnalyzer.cs Duplicates.Engine.Tests/MusicDuplicateAnalyzerTests.cs Duplicates/Services/AnalysisService.cs Duplicates/ViewModels/AnalysisResultsViewModel.cs Duplicates/Views/AnalysisPage.xaml Duplicates/Views/AnalysisResultsPage.xaml
git commit -m "Lisää musiikkiduplikaattien haku"
```

---

### Task 16: Add safe exact-duplicate hard-link and symbolic-link replacement

**Files:**
- Create: `Duplicates/Services/IFileLinkService.cs`
- Create: `Duplicates/Services/FileLinkService.cs`
- Create: `Duplicates.App.Tests/FileLinkServiceTests.cs`
- Modify: `Duplicates/ViewModels/ResultsViewModel.cs`
- Modify: `Duplicates/Views/ResultsPage.xaml(.cs)`
- Modify: `Duplicates/AppServices.cs`

**Interfaces:**

```csharp
public enum LinkReplacementMode { HardLink, SymbolicLink }

public interface IFileLinkService
{
    Task<FileOperationSummary> ReplaceWithLinksAsync(
        string survivorPath,
        IReadOnlyList<string> duplicatePaths,
        LinkReplacementMode mode,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write service tests around real temporary files**

Cover byte-identical requirement, different-content rejection, survivor-not-selected requirement, same-volume hard-link requirement, physical-id verification, rollback after create failure, duplicate restoration, successful backup recycle dispatch, symbolic-link permission failure, cancellation between items, and per-item continuation.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~FileLinkServiceTests"
```

- [ ] **Step 3: Implement rollback-first replacement**

Re-run full byte comparison before each replacement. Rename duplicate to `.duplicates-rollback-<guid>` in the same directory. Create link at original path. Verify target or physical identity. Recycle rollback only after verification. On any failure, remove an unverified link if present and rename rollback back.

Use `CreateHardLinkW` and `CreateSymbolicLinkW` with `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE`. Translate `ERROR_PRIVILEGE_NOT_HELD` into an explanation about Developer Mode/elevation; do not request elevation.

- [ ] **Step 4: Add exact-results commands only**

Add Hard link and Symbolic link commands to the exact duplicate Results command overflow. Require one explicit survivor and at least one selected duplicate. Show a confirmation listing survivor, count, mode, rollback behavior, and Developer Mode caveat. Do not expose either command on `AnalysisResultsPage`.

- [ ] **Step 5: Run all Results tests and build**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ResultsViewModelTests|FullyQualifiedName~FileLinkServiceTests|FullyQualifiedName~NativeWinUiContractTests"
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 6: Commit link replacement**

```powershell
git add Duplicates/Services/IFileLinkService.cs Duplicates/Services/FileLinkService.cs Duplicates/ViewModels/ResultsViewModel.cs Duplicates/Views/ResultsPage.xaml Duplicates/Views/ResultsPage.xaml.cs Duplicates/AppServices.cs Duplicates.App.Tests/FileLinkServiceTests.cs Duplicates.App.Tests/ResultsViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Lisää turvallinen linkkikorvaus"
```

---

### Task 17: Implement EXIF Remover with verified outputs

**Files:**
- Create: `Duplicates/Services/IExifCleanerService.cs`
- Create: `Duplicates/Services/ExifCleanerService.cs`
- Create: `Duplicates/Interop/WicMetadataInterop.cs`
- Create: `Duplicates/ViewModels/ExifRemoverViewModel.cs`
- Create: `Duplicates/Views/ExifRemoverPage.xaml(.cs)`
- Create: `Duplicates.App.Tests/ExifRemoverViewModelTests.cs`
- Modify: `Duplicates/AppServices.cs`
- Modify: `Duplicates/MainWindow.xaml.cs`
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`

**Interfaces:**

```csharp
public sealed record ExifCleanOptions(
    bool RemoveGps,
    bool RemoveDeviceIdentifiers,
    bool RemoveDates,
    bool RemoveAuthorAndDescription,
    bool RemoveEmbeddedThumbnail,
    bool ReplaceOriginal);

public interface IExifCleanerService
{
    Task<ExifCleanResult> CleanAsync(
        string sourcePath,
        ExifCleanOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write ViewModel tests with a fake cleaner**

Cover supported-image queue filtering, default options all privacy categories on and ReplaceOriginal off, no source overwrite by default, progress aggregation, cancellation, partial failures, output path display, ReplaceOriginal warning visibility, and completed-output result update.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ExifRemoverViewModelTests"
```

- [ ] **Step 3: Implement metadata removal through WIC/Windows imaging**

Copy the source to `<name>.clean<extension>.tmp`, then use dependency-free COM interop declarations in `WicMetadataInterop.cs` for `IWICImagingFactory`, `IWICBitmapDecoder`, `IWICFastMetadataEncoder`, and `IWICMetadataQueryWriter`. Obtain the query writer from the copied file, call `RemoveMetadataByName` for the selected EXIF/GPS/XMP/IPTC blocks, and commit the fast metadata encoder. This edits only the copy's metadata and preserves the encoded pixel payload. Preserve orientation, ICC profile, DPI, dimensions, and frame count. Flush, close, reopen with `BitmapDecoder`, and verify format/dimensions/frame count.

If the codec cannot remove the requested blocks without changing render-critical data, return `Unsupported metadata layout` and do not modify the source.

- [ ] **Step 4: Implement optional rollback replacement**

When ReplaceOriginal is true, rename source to a GUID rollback path, rename verified output to the original name, reopen and verify again, then recycle rollback. Restore source if final verification or recycle preparation fails.

- [ ] **Step 5: Build the native page**

Use the shared path scope file/folder picker, image-only filtering, a native option Expander with privacy-category ToggleSwitch controls, ReplaceOriginal ToggleSwitch plus warning InfoBar, an accent `Clean images` button, progress, result list, and per-file failure details.

- [ ] **Step 6: Validate fixtures**

Use JPEG and TIFF fixtures containing GPS, make/model, dates, author, comment, orientation, and ICC profile. Verify removed metadata is absent, orientation renders the same, pixel dimensions/frame count match, and original bytes remain when ReplaceOriginal is off.

Run:

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 7: Commit EXIF remover**

```powershell
git add Duplicates/Services/IExifCleanerService.cs Duplicates/Services/ExifCleanerService.cs Duplicates/Interop/WicMetadataInterop.cs Duplicates/ViewModels/ExifRemoverViewModel.cs Duplicates/Views/ExifRemoverPage.xaml Duplicates/Views/ExifRemoverPage.xaml.cs Duplicates/AppServices.cs Duplicates/MainWindow.xaml.cs Duplicates.App.Tests/ExifRemoverViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Lisää EXIF-tietojen poisto"
```

---

### Task 18: Implement Video Optimizer with Windows MediaTranscoder

**Files:**
- Create: `Duplicates/Services/IVideoOptimizerService.cs`
- Create: `Duplicates/Services/VideoOptimizerService.cs`
- Create: `Duplicates/ViewModels/VideoOptimizerViewModel.cs`
- Create: `Duplicates/Views/VideoOptimizerPage.xaml(.cs)`
- Create: `Duplicates.App.Tests/VideoOptimizerViewModelTests.cs`
- Modify: `Duplicates/AppServices.cs`
- Modify: `Duplicates/MainWindow.xaml.cs`
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`

**Interfaces:**

```csharp
public enum VideoOptimizationPreset { Smaller, Balanced, HighQuality }

public sealed record VideoOptimizationOptions(
    VideoOptimizationPreset Preset,
    bool HardwareAccelerationEnabled,
    bool KeepOutputWhenNotSmaller);

public interface IVideoOptimizerService
{
    Task<VideoOptimizationResult> OptimizeAsync(
        string sourcePath,
        string destinationPath,
        VideoOptimizationOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write ViewModel tests with a fake optimizer**

Cover video-only queue, Balanced default, hardware acceleration on, `.optimized.mp4` collision suffixing, sequential queue processing, progress, cancellation cleanup, codec-not-found error, invalid-profile error, output-smaller success, output-not-smaller deletion by default, KeepOutput override, and no source mutation.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~VideoOptimizerViewModelTests"
```

- [ ] **Step 3: Implement profile construction and transcoding**

Build H.264/AAC MP4 profiles:

```text
Smaller      max 1280x720, 30 fps, bitrate bounded by source and 3.5 Mbps
Balanced     max 1920x1080, source fps up to 60, bitrate bounded by source and 8 Mbps
High quality max 3840x2160, source fps up to 60, bitrate bounded by source and 20 Mbps
```

Never upscale. Preserve source aspect ratio. Set `MediaTranscoder.HardwareAccelerationEnabled` from options. Call `PrepareFileTranscodeAsync`; map every `TranscodeFailureReason`. Track `TranscodeAsync` progress and cancellation.

- [ ] **Step 4: Verify output and clean partials**

Write to `<destination>.tmp`. On completion, probe duration and dimensions, requiring duration difference at most one second and nonzero dimensions. Rename temp to final only after verification. If final size is not smaller and KeepOutput is false, delete it and return `No space saving`. On cancellation/failure, delete temp and leave source untouched.

- [ ] **Step 5: Build native optimizer page**

Show input queue; source codec, dimensions, duration, bitrate, and size; preset ComboBox; hardware acceleration ToggleSwitch; keep-larger-output ToggleSwitch with warning text; target summary; accent `Optimize videos` button; per-file progress; output size and saving; open/reveal actions.

- [ ] **Step 6: Validate with real files**

Run one 1080p H.264 source, one already-small MP4, and one unsupported/codec-missing fixture. Confirm smaller output, no-upscale behavior, no-source mutation, correct failure reason, and partial cleanup after cancel.

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

- [ ] **Step 7: Commit video optimization**

```powershell
git add Duplicates/Services/IVideoOptimizerService.cs Duplicates/Services/VideoOptimizerService.cs Duplicates/ViewModels/VideoOptimizerViewModel.cs Duplicates/Views/VideoOptimizerPage.xaml Duplicates/Views/VideoOptimizerPage.xaml.cs Duplicates/AppServices.cs Duplicates/MainWindow.xaml.cs Duplicates.App.Tests/VideoOptimizerViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Lisää videoiden optimointi"
```

---

### Task 19: Add only necessary settings and cache controls

**Files:**
- Modify: `Duplicates/Models/SimilarityPreset.cs`
- Modify: `Duplicates/Models/AppSettings.cs`
- Modify: `Duplicates/ViewModels/SettingsViewModel.cs`
- Modify: `Duplicates/Views/SettingsPage.xaml`
- Modify: `Duplicates/Services/SettingsService.cs`
- Modify: `Duplicates.App.Tests/SettingsViewModelTests.cs`
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`

**Interfaces:**

Add these exact defaults:

```csharp
public bool DefaultIncludeSubfolders { get; init; } = true;
public long DefaultLargeFileMinimumBytes { get; init; } = 1_073_741_824;
public int DefaultTemporaryFileMinimumAgeDays { get; init; } = 7;
public SimilarityPreset DefaultImageSimilarity { get; init; } = SimilarityPreset.Balanced;
public SimilarityPreset DefaultVideoSimilarity { get; init; } = SimilarityPreset.Balanced;
public int? MaxMediaConcurrency { get; init; }
public bool UseMediaFingerprintCache { get; init; } = true;
```

- [ ] **Step 1: Write settings round-trip and normalization tests**

Cover missing-old-JSON defaults, unknown enum fallback, large threshold negative clamp to zero, temp age clamp 1-365, media concurrency allowed null/1/2/4 and invalid fallback null, cache toggle save, Clear cache command, and settings-changed propagation only to idle pages.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~SettingsViewModelTests"
```

- [ ] **Step 3: Implement serialized saves without stale overwrite**

Keep backward-compatible property defaults. Serialize concurrent ViewModel saves through a `SemaphoreSlim` in `SettingsService` and write settings through a temporary file plus atomic replace so rapid setting changes cannot interleave or truncate JSON.

- [ ] **Step 4: Update Settings UI**

Keep Appearance. Expand Scanning defaults with include-subfolders, large-file threshold, and temporary age. Add `Similarity and media` for image/video presets, media concurrency, cache toggle, status, and Clear cache. Rename Deletion to `File actions` while preserving the permanent-delete warning.

Do not add language, DPI scale, icon-only mode, window/tab size persistence, log limits, Linux options, completion notification, preset files, or cache JSON editing.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
git add Duplicates/Models/SimilarityPreset.cs Duplicates/Models/AppSettings.cs Duplicates/ViewModels/SettingsViewModel.cs Duplicates/Views/SettingsPage.xaml Duplicates/Services/SettingsService.cs Duplicates.App.Tests/SettingsViewModelTests.cs Duplicates.App.Tests/NativeWinUiContractTests.cs
git commit -m "Täydennä työkalujen asetukset"
```

---

### Task 20: Complete cross-tool safety, accessibility, performance, and release verification

**Files:**
- Modify: `Duplicates.App.Tests/NativeWinUiContractTests.cs`
- Create: `Duplicates.App.Tests/CrossToolStateTests.cs`
- Modify: `README.md`
- Modify: `PROJECT.md`
- Modify: `AGENTS.md`
- Modify: `memory/MEMORY.md`

**Interfaces:**
- No new production interface. This task proves the integrated product and updates source-backed documentation.

- [ ] **Step 1: Add final cross-tool contract tests**

Assert all production XAML has no inline hex colors, custom `ControlTemplate`, layout-element tap handlers, or icon-only commands without accessible names. Assert all setup pages disable horizontal scrolling, all result pages expose full paths, only primary Start/Clean/Optimize buttons use `AccentButtonStyle`, and Settings uses native Settings controls.

- [ ] **Step 2: Add canonical-state regression tests**

For every result shape, verify search/sort preserves selection, hidden selected findings remain in action totals, successful actions remove canonical items, failed actions remain, preview clears only when its item is removed, and New scan clears the prior session only after confirmation.

- [ ] **Step 3: Run automated verification from a restored tree**

```powershell
dotnet restore Duplicates.slnx
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
dotnet format Duplicates.slnx --verify-no-changes
```

Expected: every command exits 0. If `dotnet format` hits a known preview-toolchain error, record the exact error and run `dotnet format --include` on every changed `.cs` file as a bounded fallback; do not label formatting verified without evidence.

- [ ] **Step 4: Run checker wrappers**

```powershell
.\tools\lc.ps1
.\tools\sc.ps1
.\tools\bc.ps1
.\tools\tc.ps1
```

Inspect reports under `reports/`. Fix only failures caused by this feature set; identify unrelated pre-existing failures explicitly.

- [ ] **Step 5: Perform packaged and unpackaged UAT**

Use a temporary fixture tree containing:

```text
exact duplicate pair
zero-byte file
empty directory and non-empty parent
1 GiB sparse/fixture large file or a reduced threshold
old .tmp and fresh .tmp
valid and broken symlink/junction
signature/extension mismatch
bidi/control/reserved-name fixture where Windows permits creation
valid and truncated JPEG/ZIP/media files
same/resized/unrelated images
same-transcoded/unrelated videos
same-title/artist/duration and differing music metadata
JPEG/TIFF with privacy metadata
optimizable and already-efficient video
```

For both packaged and unpackaged Debug x64 launches verify navigation, narrow 640 epx layout, keyboard-only use, screen-reader names, high contrast, System/Light/Dark, folder/file picker, drag/drop, exclusions, cancellation, skipped details, result search/sort, Recycle Bin delete, move collision, export, rename, link rollback, EXIF copy/replace, video cancel/output validation, and cache clear.

- [ ] **Step 6: Update source-backed documentation**

Document the fourteen tools, exact algorithms and limits, media codec dependence, cache path/schema, new settings, no-result-persistence boundary, mutation safeguards, test commands, and UAT result. Update AGENTS architecture boundaries to permit focused analyzers and injected app media providers while keeping the engine UI-free.

- [ ] **Step 7: Review for plan/design acceptance coverage**

Create a checklist mapping every section under `Tool Definitions`, `Result Actions`, `Settings`, `Visual Contract`, and `Acceptance Criteria` in the design spec to a passing automated test or named UAT step. Resolve every unmapped item before commit.

- [ ] **Step 8: Commit final verification and documentation**

```powershell
git add Duplicates.App.Tests/NativeWinUiContractTests.cs Duplicates.App.Tests/CrossToolStateTests.cs README.md PROJECT.md AGENTS.md memory/MEMORY.md
git commit -m "Viimeistele tiedostotyökalujen varmennus"
```

Do not push unless the user separately asks. Do not create a pull request.

---

## Milestone Gates

### Gate A — Filesystem tools ready

Tasks 1-11 complete. Duplicate files, Empty folders, Big files, Empty files, Temporary files, Invalid links, Broken files, Bad extensions, and Bad names are usable and tested. Media work may remain absent without weakening this gate.

### Gate B — Similarity tools ready

Tasks 12-15 complete. Cache invalidation, image pHash, five-frame video comparison, and conservative music metadata matching pass both fake-provider tests and real Windows media smoke tests.

### Gate C — Transform tools ready

Tasks 16-18 complete. Link replacement, EXIF removal, and video optimization prove rollback/temporary-output behavior and never mutate sources on failed verification.

### Gate D — Release candidate

Tasks 19-20 complete. Settings are backward compatible, all checks pass, UAT covers packaged and unpackaged runs, documentation matches live behavior, and no requested feature lacks evidence.

## Stop Conditions During Implementation

Stop and report instead of guessing if any of these occur:

- Windows imaging cannot remove a requested metadata block without re-encoding render-critical data.
- Windows MediaTranscoder cannot produce the documented H.264/AAC profiles on the target machine.
- Symbolic-link creation requires authority not already available and rollback cannot be proven.
- A new dependency appears necessary for a format or algorithm; provide dependency, license, size, and alternative analysis first.
- A remote branch is unexpectedly ahead before a requested push.
- Secret-like files or credentials enter commit scope.
- Existing uncommitted user changes overlap a required production file in a way that cannot be preserved.
