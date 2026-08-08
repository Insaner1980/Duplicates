using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Models;
using Duplicates.Services;
using Xunit.Sdk;

namespace Duplicates.App.Tests;

public sealed class AnalysisServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.App.Tests",
        Guid.NewGuid().ToString("N"));

    public AnalysisServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLinksWithNoOptions_UsesInventoryAndIncludesTotalElapsed()
    {
        string missingTarget = Path.Combine(_root, "missing-target");
        string link = Path.Combine(_root, "broken-link");
        CreateFileSymbolicLinkOrSkip(link, missingTarget);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);

        AnalysisResult result = await new AnalysisService().RunAsync(
            ToolKind.InvalidLinks,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(link, finding.FullPath);
        Assert.Equal("Link target is missing.", finding.Reason);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task BadExtensionsWithNoOptions_UsesInventoryAndIncludesTotalElapsed()
    {
        string path = Path.Combine(_root, "photo.txt");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);

        AnalysisResult result = await new AnalysisService().RunAsync(
            ToolKind.BadExtensions,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(path, finding.FullPath);
        Assert.Equal("photo.png", finding.Suggestion);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task BadNamesWithNoOptions_UsesInventoryAndIncludesTotalElapsed()
    {
        string path = Path.Combine(_root, " report.txt");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);

        AnalysisResult result = await new AnalysisService().RunAsync(
            ToolKind.BadNames,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(path, finding.FullPath);
        Assert.Equal("report.txt", finding.Suggestion);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task BrokenFilesWithInjectedProbe_UsesInventoryAndIncludesTotalElapsed()
    {
        string path = Path.Combine(_root, "broken.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);
        var probe = new FakeFileFormatProbe
        {
            Handler = (_, detected, _) => Task.FromResult(
                detected?.Name == "PNG"
                    ? new FileProbeResult(FileProbeStatus.Invalid, "ImageDecodeFailure", null)
                    : new FileProbeResult(FileProbeStatus.Valid, null, null)),
        };

        AnalysisResult result = await new AnalysisService(probe).RunAsync(
            ToolKind.BrokenFiles,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(path, finding.FullPath);
        Assert.Equal("Image", finding.Metadata["Validator"]);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task InvalidLinksRejectsToolOptionsThatDoNotMatch()
    {
        var service = new AnalysisService();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(
            ToolKind.InvalidLinks,
            new AnalysisScope { IncludedFolders = [_root] },
            new LargeFileToolOptions(1),
            progress: null,
            CancellationToken.None));

        Assert.Equal("toolOptions", exception.ParamName);
    }

    [Fact]
    public async Task BrokenFilesWithoutProbe_FailsBeforeInventory()
    {
        var service = new AnalysisService();
        var reports = new List<AnalysisProgress>();

        await Assert.ThrowsAsync<NotSupportedException>(() => service.RunAsync(
            ToolKind.BrokenFiles,
            new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
            new NoToolOptions(),
            new RecordingProgress(reports),
            CancellationToken.None));

        Assert.Empty(reports);
    }

    [Fact]
    public async Task BrokenFilesRejectsMismatchedOptionsBeforeInventory()
    {
        var reports = new List<AnalysisProgress>();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new AnalysisService(new FakeFileFormatProbe()).RunAsync(
                ToolKind.BrokenFiles,
                new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
                new LargeFileToolOptions(1),
                new RecordingProgress(reports),
                CancellationToken.None));

        Assert.Equal("toolOptions", exception.ParamName);
        Assert.Empty(reports);
    }

    [Fact]
    public void AppServices_ComposesOneProbeIntoAnalysisAndActionRevalidation()
    {
        var services = new AppServices();

        Assert.IsType<WindowsFileFormatProbe>(services.FileFormatProbe);
        Assert.Same(
            services.FileFormatProbe,
            typeof(AnalysisService)
                .GetField("_fileFormatProbe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisService));
        Assert.Same(
            services.FileFormatProbe,
            typeof(Duplicates.ViewModels.AnalysisResultsViewModel)
                .GetField("_fileFormatProbe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisResultsViewModel));
    }

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (IsLinkCapabilityFailure(ex))
        {
            throw SkipException.ForSkip($"A file symbolic-link fixture cannot be created: {ex.Message}");
        }
    }

    private static bool IsLinkCapabilityFailure(Exception exception)
    {
        if (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return true;
        }

        int nativeError = exception.HResult & 0xFFFF;
        return exception is IOException && nativeError is 5 or 1314;
    }

    private sealed class DelayingProgress(TimeSpan delay) : IProgress<AnalysisProgress>
    {
        public bool Delayed { get; private set; }

        public void Report(AnalysisProgress value)
        {
            if (Delayed)
            {
                return;
            }

            Delayed = true;
            Thread.Sleep(delay);
        }
    }

    private sealed class RecordingProgress(List<AnalysisProgress> reports) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => reports.Add(value);
    }
}
