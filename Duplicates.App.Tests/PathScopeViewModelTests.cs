using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

public sealed partial class PathScopeViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"Duplicates.Scope.{Guid.NewGuid():N}");

    public PathScopeViewModelTests()
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
    public void InvalidPathsPreserveExistingScope()
    {
        string file = Path.Combine(_root, "existing.txt");
        File.WriteAllText(file, "data");
        var scope = new PathScopeViewModel();
        Assert.True(scope.AddFile(file));
        ScopePathViewModel original = Assert.Single(scope.IncludedPaths);

        Assert.False(scope.AddFolder(file));
        Assert.False(scope.AddFile(_root));
        Assert.False(scope.AddFolder(Path.Combine(_root, "missing")));
        Assert.False(scope.AddFile(Path.Combine(_root, "missing.txt")));
        Assert.False(scope.ExcludePath(Path.Combine(_root, "missing.txt")));
        Assert.False(scope.AddFile(" "));
        Assert.False(scope.AddFile("invalid\0path"));
        Assert.Same(original, Assert.Single(scope.IncludedPaths));
        Assert.Empty(scope.ExcludedPaths);
    }

    [Fact]
    public void AddPath_CanonicalizesAndDeduplicatesCaseInsensitively()
    {
        string folder = Path.Combine(_root, "ScopeFolder");
        Directory.CreateDirectory(folder);
        var scope = new PathScopeViewModel();

        scope.AddFolder(Path.Combine(folder, "."));
        scope.AddFolder(folder.ToUpperInvariant());

        ScopePathViewModel item = Assert.Single(scope.IncludedPaths);
        Assert.Equal(folder, item.FullPath);
        Assert.Equal("ScopeFolder", item.DisplayName);
        Assert.Equal(_root, item.ParentPath);
    }

    [Fact]
    public void ExcludePath_RemovesAnEquivalentIncludedFile()
    {
        string file = Path.Combine(_root, "report.txt");
        File.WriteAllText(file, "report");
        var scope = new PathScopeViewModel();

        scope.AddFile(file);
        scope.ExcludePath(file.ToUpperInvariant());

        Assert.Empty(scope.IncludedPaths);
        ScopePathViewModel excluded = Assert.Single(scope.ExcludedPaths);
        Assert.Equal(Path.GetFullPath(file.ToUpperInvariant()), excluded.FullPath);
        Assert.EndsWith("report.txt", excluded.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreferredFolder_AllowsOnlyOneIncludedFolderToBePreferred()
    {
        string first = Path.Combine(_root, "First");
        string second = Path.Combine(_root, "Second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var scope = new PathScopeViewModel();

        scope.AddFolder(first);
        scope.AddFolder(second);
        ScopePathViewModel[] folders = scope.IncludedPaths.ToArray();

        scope.PreferFolder(folders[1]);

        Assert.False(folders[0].IsPreferred);
        Assert.True(folders[1].IsPreferred);

        scope.PreferFolder(folders[0]);

        Assert.True(folders[0].IsPreferred);
        Assert.False(folders[1].IsPreferred);
    }
}
