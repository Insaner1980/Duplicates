using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Models;
using Xunit.Sdk;

namespace Duplicates.Engine.Tests;

[SupportedOSPlatform("windows")]
public sealed class InvalidLinkAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.Engine.Tests",
        Guid.NewGuid().ToString("N"));

    public InvalidLinkAnalyzerTests()
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
    public async Task ValidFileAndDirectoryLinks_AreExcluded()
    {
        string fileTarget = WriteFile("targets/file.txt", "target");
        string directoryTarget = Path.Combine(_root, "targets", "folder");
        Directory.CreateDirectory(directoryTarget);
        string fileLink = Path.Combine(_root, "file-link");
        string directoryLink = Path.Combine(_root, "directory-link");
        CreateFileSymbolicLinkOrSkip(fileLink, fileTarget);
        CreateDirectorySymbolicLinkOrSkip(directoryLink, directoryTarget);

        AnalysisResult result = await AnalyzeAsync([fileLink, directoryLink]);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ValidJunction_IsExcluded()
    {
        string target = Path.Combine(_root, "junction-target");
        string junction = Path.Combine(_root, "junction");
        Directory.CreateDirectory(target);
        CreateJunctionOrSkip(junction, target);

        try
        {
            AnalysisResult result = await AnalyzeAsync([junction]);

            Assert.Empty(result.Findings);
            Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint));
            Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.Directory));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task MissingFileAndDirectoryTargets_ProduceStableReasonsAndMetadata()
    {
        string fileLink = Path.Combine(_root, "missing-file-link");
        string directoryLink = Path.Combine(_root, "missing-directory-link");
        string missingFileTarget = Path.Combine(_root, "missing-file.txt");
        string missingDirectoryTarget = Path.Combine(_root, "missing-directory");
        CreateFileSymbolicLinkOrSkip(fileLink, missingFileTarget);
        CreateDirectorySymbolicLinkOrSkip(directoryLink, missingDirectoryTarget);

        AnalysisResult result = await AnalyzeAsync([fileLink, directoryLink]);

        Assert.Equal(2, result.Findings.Count);
        PathFinding directoryFinding = result.Findings.Single(finding => finding.FullPath == directoryLink);
        Assert.Equal(PathFindingKind.Link, directoryFinding.Kind);
        Assert.Equal("Link target is missing.", directoryFinding.Reason);
        Assert.Equal(0, directoryFinding.SizeBytes);
        Assert.Equal("Directory", directoryFinding.Metadata["LinkKind"]);
        Assert.Equal(missingDirectoryTarget, directoryFinding.Metadata["ImmediateTarget"]);
        Assert.Equal(2, directoryFinding.Metadata.Count);

        PathFinding fileFinding = result.Findings.Single(finding => finding.FullPath == fileLink);
        Assert.Equal(PathFindingKind.Link, fileFinding.Kind);
        Assert.Equal("Link target is missing.", fileFinding.Reason);
        Assert.Equal(0, fileFinding.SizeBytes);
        Assert.Equal("File", fileFinding.Metadata["LinkKind"]);
        Assert.Equal(missingFileTarget, fileFinding.Metadata["ImmediateTarget"]);
        Assert.Equal(2, fileFinding.Metadata.Count);
    }

    [Fact]
    public async Task RelativeTarget_IsResolvedFromLinkDirectoryAndRawTextIsPreserved()
    {
        string folder = Path.Combine(_root, "relative");
        Directory.CreateDirectory(folder);
        WriteFile("relative/target.txt", "target");
        string validLink = Path.Combine(folder, "valid-link");
        string missingLink = Path.Combine(folder, "missing-link");
        CreateFileSymbolicLinkOrSkip(validLink, "target.txt");
        CreateFileSymbolicLinkOrSkip(missingLink, "missing.txt");

        AnalysisResult result = await AnalyzeAsync([validLink, missingLink]);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(missingLink, finding.FullPath);
        Assert.Equal("missing.txt", finding.Metadata["ImmediateTarget"]);
        Assert.Equal("Link target is missing.", finding.Reason);
    }

    [Fact]
    public async Task ValidMultiHopChain_IsExcluded()
    {
        string folder = Path.Combine(_root, "chain");
        Directory.CreateDirectory(folder);
        WriteFile("chain/target.txt", "target");
        string secondLink = Path.Combine(folder, "second-link");
        string firstLink = Path.Combine(folder, "first-link");
        CreateFileSymbolicLinkOrSkip(secondLink, "target.txt");
        CreateFileSymbolicLinkOrSkip(firstLink, "second-link");

        AnalysisResult result = await AnalyzeAsync([firstLink]);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task LoopAndExcessiveChain_UseUnresolvedReason()
    {
        string loopA = Path.Combine(_root, "loop-a");
        string loopB = Path.Combine(_root, "loop-b");
        CreateFileSymbolicLinkOrSkip(loopA, "loop-b");
        CreateFileSymbolicLinkOrSkip(loopB, "loop-a");

        string chainFolder = Path.Combine(_root, "excessive");
        Directory.CreateDirectory(chainFolder);
        WriteFile("excessive/target.txt", "target");
        const int linkCount = 65;
        for (int index = linkCount - 1; index >= 0; index--)
        {
            string link = Path.Combine(chainFolder, $"link-{index:D2}");
            string target = index == linkCount - 1 ? "target.txt" : $"link-{index + 1:D2}";
            CreateFileSymbolicLinkOrSkip(link, target);
        }

        string excessive = Path.Combine(chainFolder, "link-00");
        AnalysisResult result = await AnalyzeAsync([loopA, excessive]);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(
            result.Findings,
            static finding => Assert.Equal("Link target cannot be resolved.", finding.Reason));
    }

    [Fact]
    public async Task InaccessibleTarget_UsesStableReason()
    {
        string restrictedFolder = Path.Combine(_root, "restricted");
        string target = WriteFile("restricted/target.txt", "target");
        string link = Path.Combine(_root, "restricted-link");
        CreateFileSymbolicLinkOrSkip(link, target);
        string sid = WindowsIdentity.GetCurrent().User?.Value ??
            throw SkipException.ForSkip("An ACL-denied fixture cannot be created because the current user SID is unavailable.");

        if (!TryDenyAccess(restrictedFolder, target, sid, out string failureReason))
        {
            throw SkipException.ForSkip($"An ACL-denied fixture cannot be created: {failureReason}");
        }

        try
        {
            AnalysisResult result = await AnalyzeAsync([link]);

            PathFinding finding = Assert.Single(result.Findings);
            Assert.Equal("Link target is inaccessible.", finding.Reason);
        }
        finally
        {
            RestoreAccess(restrictedFolder, sid);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_HonorsCancellationBeforeEveryEntry()
    {
        string first = Path.Combine(_root, "first-link");
        string second = Path.Combine(_root, "second-link");
        CreateFileSymbolicLinkOrSkip(first, Path.Combine(_root, "missing-first"));
        CreateFileSymbolicLinkOrSkip(second, Path.Combine(_root, "missing-second"));
        using var cancellationSource = new CancellationTokenSource();
        FileInventory inventory = NewInventory(new CancelBeforeSecondItemList<string>(
            [first, second],
            cancellationSource));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new InvalidLinkAnalyzer().AnalyzeAsync(inventory, cancellationSource.Token));
    }

    [Fact]
    public async Task AnalyzeAsync_SortsDeterministicallyForwardsSkippedPathsAndReportsElapsed()
    {
        string physicalLink = Path.Combine(_root, "order-link");
        string lastLink = Path.Combine(_root, "z-link");
        string regularFile = WriteFile("regular.txt", "not a link");
        CreateFileSymbolicLinkOrSkip(physicalLink, "missing");
        CreateFileSymbolicLinkOrSkip(lastLink, "missing");
        string upperCasePath = physicalLink.ToUpperInvariant();
        string lowerCasePath = physicalLink.ToLowerInvariant();
        var skippedPaths = new[]
        {
            new SkippedPath { Path = Path.Combine(_root, "skipped"), Reason = "Access denied" },
        };
        FileInventory inventory = NewInventory(
            [lowerCasePath, lastLink, regularFile, upperCasePath],
            skippedPaths);

        AnalysisResult result = await new InvalidLinkAnalyzer().AnalyzeAsync(inventory, CancellationToken.None);

        Assert.Equal(
            [upperCasePath, lowerCasePath, lastLink],
            result.Findings.Select(static finding => finding.FullPath));
        Assert.Empty(result.Groups);
        Assert.Same(skippedPaths, result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    private Task<AnalysisResult> AnalyzeAsync(IReadOnlyList<string> reparsePointPaths) =>
        new InvalidLinkAnalyzer().AnalyzeAsync(NewInventory(reparsePointPaths), CancellationToken.None);

    private static FileInventory NewInventory(
        IReadOnlyList<string> reparsePointPaths,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new(
            [],
            [],
            [],
            reparsePointPaths,
            skippedPaths ?? []);

    private string WriteFile(string relativePath, string contents)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (IsLinkCapabilityFailure(ex))
        {
            throw SkipException.ForSkip($"A file symbolic-link fixture cannot be created: {ex.Message}");
        }
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (IsLinkCapabilityFailure(ex))
        {
            throw SkipException.ForSkip($"A directory symbolic-link fixture cannot be created: {ex.Message}");
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

    private static void CreateJunctionOrSkip(string junctionPath, string targetPath)
    {
        ProcessResult result = RunProcess("cmd.exe", "/d", "/c", "mklink", "/J", junctionPath, targetPath);
        if (result.ExitCode == 0)
        {
            return;
        }

        string output = $"{result.StandardOutput} {result.StandardError}".Trim();
        if (output.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("privilege", StringComparison.OrdinalIgnoreCase))
        {
            throw SkipException.ForSkip($"A junction fixture cannot be created: {output}");
        }

        throw new XunitException($"Junction creation failed with exit code {result.ExitCode}: {output}");
    }

    private static bool TryDenyAccess(string aclPath, string probePath, string sid, out string failureReason)
    {
        ProcessResult result = RunProcess("icacls.exe", aclPath, "/inheritance:r", "/deny", $"*{sid}:(F)");
        if (result.ExitCode != 0)
        {
            failureReason = $"icacls exited with {result.ExitCode}: {result.StandardOutput} {result.StandardError}".Trim();
            return false;
        }

        try
        {
            _ = File.GetAttributes(probePath);
            RestoreAccess(aclPath, sid);
            failureReason = "the target remains readable after applying a deny rule";
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            failureReason = string.Empty;
            return true;
        }
    }

    private static void RestoreAccess(string path, string sid)
    {
        _ = RunProcess("icacls.exe", path, "/remove:d", $"*{sid}");
        _ = RunProcess("icacls.exe", path, "/inheritance:e");
    }

    private static ProcessResult RunProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class CancelBeforeSecondItemList<T>(
        IReadOnlyList<T> items,
        CancellationTokenSource cancellationSource) : IReadOnlyList<T>
    {
        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator() => Enumerate().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<T> Enumerate()
        {
            yield return items[0];
            cancellationSource.Cancel();
            for (int index = 1; index < items.Count; index++)
            {
                yield return items[index];
            }
        }
    }
}
