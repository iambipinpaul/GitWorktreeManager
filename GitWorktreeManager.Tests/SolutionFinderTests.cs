using FluentAssertions;
using GitWorktreeManager.Services;
using Xunit;

namespace GitWorktreeManager.Tests;

public class SolutionFinderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "SolutionFinderTests_" + Guid.NewGuid().ToString("N"));

    public SolutionFinderTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void FindBestSolutionFile_WithSingleRootSolution_ReturnsIt()
    {
        string sln = Path.Combine(_tempRoot, "app.sln");
        File.WriteAllText(sln, "");

        SolutionFinder.FindBestSolutionFile(_tempRoot).Should().Be(sln);
    }

    [Fact]
    public void FindBestSolutionFile_WithAmbiguousRoot_ReturnsNull_DoesNotFallThroughToSubdir()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "a.sln"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "b.sln"), "");
        string sub = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sub);
        string nested = Path.Combine(sub, "nested.sln");
        File.WriteAllText(nested, "");

        // Root wins: ambiguous root means open folder, not a nested guess.
        SolutionFinder.FindBestSolutionFile(_tempRoot).Should().BeNull();
    }

    [Fact]
    public void FindBestSolutionFile_WithNoRootSolution_FindsNestedSolution()
    {
        string sub = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sub);
        string nested = Path.Combine(sub, "nested.sln");
        File.WriteAllText(nested, "");

        SolutionFinder.FindBestSolutionFile(_tempRoot).Should().Be(nested);
    }

    [Fact]
    public void FindBestSolutionFile_WithNoSolutions_ReturnsNull()
    {
        SolutionFinder.FindBestSolutionFile(_tempRoot).Should().BeNull();
    }

    [Fact]
    public void FindBestSolutionFile_WithInvalidPath_ReturnsNull()
    {
        SolutionFinder.FindBestSolutionFile(Path.Combine(_tempRoot, "does-not-exist")).Should().BeNull();
        SolutionFinder.FindBestSolutionFile("").Should().BeNull();
    }

    [Fact]
    public void FindBestSolutionFile_PrefersSlnxOverSln()
    {
        string sln = Path.Combine(_tempRoot, "app.sln");
        string slnx = Path.Combine(_tempRoot, "app.slnx");
        File.WriteAllText(sln, "");
        File.WriteAllText(slnx, "");

        SolutionFinder.FindBestSolutionFile(_tempRoot).Should().Be(slnx);
    }

    [Fact]
    public void FindBestSolutionFile_SkipsExcludedDirectories()
    {
        string bin = Path.Combine(_tempRoot, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ignored.sln"), "");

        SolutionFinder.FindBestSolutionFile(_tempRoot).Should().BeNull();
    }

    [Fact]
    public void PickSolution_WithMultipleSlnx_ReturnsNull()
    {
        var files = new List<string> { "a.slnx", "b.slnx" };

        SolutionFinder.PickSolution(files).Should().BeNull();
    }
}
