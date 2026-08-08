using System.Collections;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Tests;

public sealed class BadNameAnalyzerTests
{
    [Theory]
    [InlineData("bad\u0001name.txt")]
    [InlineData("bad\u007Fname.txt")]
    public void Detect_FlagsAndRemovesControlCharacters(string currentName)
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect(currentName));

        Assert.Equal(["Contains control characters"], finding.Reasons);
        Assert.Equal("badname.txt", finding.SuggestedName);
    }

    [Theory]
    [InlineData('\u202A')]
    [InlineData('\u202B')]
    [InlineData('\u202C')]
    [InlineData('\u202D')]
    [InlineData('\u202E')]
    [InlineData('\u2066')]
    [InlineData('\u2067')]
    [InlineData('\u2068')]
    [InlineData('\u2069')]
    public void Detect_FlagsAndRemovesEveryBidirectionalControlClass(char control)
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect($"safe{control}name.txt"));

        Assert.Equal(["Contains bidirectional control characters"], finding.Reasons);
        Assert.Equal("safename.txt", finding.SuggestedName);
    }

    [Fact]
    public void Detect_ReplacesOtherInvalidWindowsCharacters()
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect("bad:name.txt"));

        Assert.Equal(["Contains invalid Windows characters"], finding.Reasons);
        Assert.Equal("bad_name.txt", finding.SuggestedName);
    }

    [Theory]
    [InlineData(" report.txt", "report.txt")]
    [InlineData("report.txt ", "report.txt")]
    [InlineData("\u2003report.txt\u2002", "report.txt")]
    public void Detect_TrimsLeadingAndTrailingUnicodeWhitespace(string currentName, string suggestion)
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect(currentName));

        Assert.Equal(["Has leading or trailing whitespace"], finding.Reasons);
        Assert.Equal(suggestion, finding.SuggestedName);
    }

    [Fact]
    public void Detect_TrimsTrailingDotsAfterUnsafeWhitespace()
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect("report.txt. "));

        Assert.Equal(["Has leading or trailing whitespace", "Ends with a dot"], finding.Reasons);
        Assert.Equal("report.txt", finding.SuggestedName);
    }

    [Theory]
    [InlineData("CON", "CON_file")]
    [InlineData("con.txt", "con_file.txt")]
    [InlineData("PRN.log", "PRN_file.log")]
    [InlineData("AUX", "AUX_file")]
    [InlineData("NUL.bin", "NUL_file.bin")]
    [InlineData("COM1.txt", "COM1_file.txt")]
    [InlineData("COM9.backup.txt", "COM9_file.backup.txt")]
    [InlineData("LPT1", "LPT1_file")]
    [InlineData("lpt9.txt", "lpt9_file.txt")]
    public void Detect_AppendsFileToReservedDosStem(string currentName, string suggestion)
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect(currentName));

        Assert.Equal(["Uses a reserved DOS device name"], finding.Reasons);
        Assert.Equal(suggestion, finding.SuggestedName);
    }

    [Fact]
    public void Detect_UsesTheTrimmedFirstComponentForReservedNames()
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect("CON .backup.txt"));

        Assert.Equal(["Uses a reserved DOS device name"], finding.Reasons);
        Assert.Equal("CON _file.backup.txt", finding.SuggestedName);
    }

    [Fact]
    public void Detect_DoesNotTreatLeadingWhitespaceAsPartOfAReservedStem()
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect(" CON.txt"));

        Assert.Equal(["Has leading or trailing whitespace"], finding.Reasons);
        Assert.Equal("CON_file.txt", finding.SuggestedName);
    }

    [Fact]
    public void Detect_ReplacesAnEmptyStemAndPreservesSafeExtension()
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect(".txt"));

        Assert.Equal(["Has an empty file name stem"], finding.Reasons);
        Assert.Equal("file.txt", finding.SuggestedName);
    }

    [Theory]
    [InlineData("café-😀.txt")]
    [InlineData("archive.tar.gz")]
    [InlineData("report!#$%&'()+,-;=@[]^_`{}.txt")]
    [InlineData("CONTEXT.txt")]
    [InlineData("COM10.txt")]
    [InlineData("my CON notes.txt")]
    [InlineData("spaces inside.txt")]
    public void Detect_DoesNotFlagOrdinaryValidNames(string currentName)
    {
        Assert.Null(Detect(currentName));
    }

    [Fact]
    public void Detect_DoesNotFlagALongValidName()
    {
        Assert.Null(Detect(new string('a', 240) + ".txt"));
    }

    [Fact]
    public void Detect_UsesExactReasonOrderAndDeterministicSuggestion()
    {
        const string currentName = " \u0001\u202Ebad:. ";

        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect(currentName));

        Assert.Equal(
            [
                "Contains control characters",
                "Contains bidirectional control characters",
                "Contains invalid Windows characters",
                "Has leading or trailing whitespace",
                "Ends with a dot",
            ],
            finding.Reasons);
        Assert.Equal("bad_", finding.SuggestedName);
        Assert.Equal(currentName, finding.CurrentName);
        Assert.Equal(Path.Combine(@"C:\scan", currentName), finding.FullPath);
    }

    [Fact]
    public void Detect_PreservesOriginalUnicodeWhileRemovingUnsafeCharacters()
    {
        BadNameFinding finding = Assert.IsType<BadNameFinding>(Detect("cafe\u0301\u202E-😀.txt"));

        Assert.Equal("cafe\u0301-😀.txt", finding.SuggestedName);
    }

    [Fact]
    public async Task AnalyzeAsync_MapsInventorySnapshotAndForwardsSkippedPaths()
    {
        var created = new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc);
        var modified = created.AddMinutes(5);
        InventoryFile file = NewFile(@"C:\scan\ bad.txt", " bad.txt", 42, created, modified);
        var skipped = new SkippedPath { Path = @"C:\scan\locked", Reason = "Access denied" };
        FileInventory inventory = NewInventory(files: [file], skippedPaths: [skipped]);

        AnalysisResult result = await new BadNameAnalyzer().AnalyzeAsync(inventory, CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(file.FullPath, finding.FullPath);
        Assert.Equal(PathFindingKind.File, finding.Kind);
        Assert.Equal("Has leading or trailing whitespace", finding.Reason);
        Assert.Equal("bad.txt", finding.Suggestion);
        Assert.Equal(file.SizeBytes, finding.SizeBytes);
        Assert.Equal(created, finding.CreatedUtc);
        Assert.Equal(modified, finding.ModifiedUtc);
        Assert.Equal(" bad.txt", finding.Metadata["CurrentName"]);
        Assert.Single(finding.Metadata);
        Assert.Empty(result.Groups);
        Assert.Same(inventory.SkippedPaths, result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task AnalyzeAsync_OnlyAnalyzesOrdinaryInventoryFiles()
    {
        FileInventory inventory = NewInventory(
            files:
            [
                NewFile(@"C:\scan\ ordinary.txt", " ordinary.txt"),
                NewFile(@"C:\scan\ reparse.txt", " reparse.txt", attributes: FileAttributes.ReparsePoint),
                NewFile(@"C:\scan\ directory.txt", " directory.txt", attributes: FileAttributes.Directory),
            ],
            directories:
            [
                new InventoryDirectory(
                    @"C:\scan\ bad folder ",
                    " bad folder ",
                    @"C:\scan",
                    1,
                    0,
                    FileAttributes.Directory),
            ]);

        AnalysisResult result = await new BadNameAnalyzer().AnalyzeAsync(inventory, CancellationToken.None);

        Assert.Equal(@"C:\scan\ ordinary.txt", Assert.Single(result.Findings).FullPath);
    }

    [Fact]
    public async Task AnalyzeAsync_SortsByFullPathIgnoreCaseThenOrdinal()
    {
        FileInventory inventory = NewInventory(files:
        [
            NewFile(@"C:\scan\ z.txt", " z.txt"),
            NewFile(@"C:\scan\ a.txt", " a.txt"),
            NewFile(@"C:\scan\ A.txt", " A.txt"),
        ]);

        AnalysisResult result = await new BadNameAnalyzer().AnalyzeAsync(inventory, CancellationToken.None);

        Assert.Equal(
            [@"C:\scan\ A.txt", @"C:\scan\ a.txt", @"C:\scan\ z.txt"],
            result.Findings.Select(static finding => finding.FullPath));
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellationBeforeEveryItem()
    {
        using var cancellationSource = new CancellationTokenSource();
        FileInventory inventory = NewInventory(files: new CancelBeforeSecondItemList<InventoryFile>(
            [
                NewFile(@"C:\scan\ first.txt", " first.txt"),
                NewFile(@"C:\scan\ second.txt", " second.txt"),
            ],
            cancellationSource));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BadNameAnalyzer().AnalyzeAsync(inventory, cancellationSource.Token));
    }

    private static BadNameFinding? Detect(string currentName) => BadNameAnalyzer.Detect(
        Path.Combine(@"C:\scan", currentName),
        currentName);

    private static InventoryFile NewFile(
        string fullPath,
        string fileName,
        long sizeBytes = 1,
        DateTime? createdUtc = null,
        DateTime? modifiedUtc = null,
        FileAttributes attributes = FileAttributes.Normal) => new(
            fullPath,
            fileName,
            Path.GetExtension(fileName).ToLowerInvariant(),
            Path.GetDirectoryName(fullPath) ?? string.Empty,
            sizeBytes,
            createdUtc ?? new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc),
            modifiedUtc ?? new DateTime(2026, 8, 7, 11, 0, 0, DateTimeKind.Utc),
            attributes);

    private static FileInventory NewInventory(
        IReadOnlyList<InventoryFile>? files = null,
        IReadOnlyList<InventoryDirectory>? directories = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new(
            files ?? [],
            directories ?? [],
            [],
            [],
            skippedPaths ?? []);

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
