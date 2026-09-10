using FluentAssertions;
using GitWorktreeManager.Services;
using Xunit;

namespace GitWorktreeManager.Tests;

public class GitServiceTests
{
    [Fact]
    public void BuildGitArguments_WhenLongPathSupportEnabled_AddsScopedConfig()
    {
        string result = GitService.BuildGitArguments("worktree list --porcelain", true);

        result.Should().Be("-c core.longpaths=true worktree list --porcelain");
    }

    [Fact]
    public void BuildGitArguments_WhenLongPathSupportDisabled_LeavesArgumentsUnchanged()
    {
        string result = GitService.BuildGitArguments("worktree list --porcelain", false);

        result.Should().Be("worktree list --porcelain");
    }

    [Theory]
    [InlineData("fatal: cannot create directory at 'src/deep/path': Filename too long")]
    [InlineData("fatal: unable to checkout working tree: file name too long")]
    [InlineData("The specified path, file name, or both are too long.")]
    [InlineData("error: path length is above the supported limit")]
    public void IsLongPathError_WithLongPathMessages_ReturnsTrue(string errorMessage)
    {
        bool result = GitService.IsLongPathError(errorMessage);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsLongPathError_WithUnrelatedGitError_ReturnsFalse()
    {
        bool result = GitService.IsLongPathError("fatal: 'missing-branch' is not a commit");

        result.Should().BeFalse();
    }

    [Fact]
    public void CreateUserFacingErrorMessage_ForLongPathError_AddsGuidance()
    {
        string result = GitService.CreateUserFacingErrorMessage(
            "fatal: Filename too long",
            true);

        result.Should().Contain("process-scoped core.longpaths=true");
        result.Should().Contain("git config --global core.longpaths true");
        result.Should().Contain("Git for Windows setting, not a Visual Studio setting");
    }

    [Fact]
    public void CreateUserFacingErrorMessage_ForUnrelatedError_PreservesOriginalMessage()
    {
        const string errorMessage = "fatal: 'missing-branch' is not a commit";

        string result = GitService.CreateUserFacingErrorMessage(errorMessage, true);

        result.Should().Be(errorMessage);
    }

    [Fact]
    public void BuildGitArgumentList_WhenLongPathSupportEnabled_PrependsConfig()
    {
        List<string> result = GitService.BuildGitArgumentList(
            new[] { "worktree", "list", "--porcelain" }, true);

        result.Should().Equal("-c", "core.longpaths=true", "worktree", "list", "--porcelain");
    }

    [Fact]
    public void BuildGitArgumentList_WhenLongPathSupportDisabled_LeavesArgumentsUnchanged()
    {
        List<string> result = GitService.BuildGitArgumentList(
            new[] { "worktree", "list", "--porcelain" }, false);

        result.Should().Equal("worktree", "list", "--porcelain");
    }

    [Fact]
    public void BuildGitArgumentList_PreservesPathsWithSpacesAsSingleArg()
    {
        string pathWithSpaces = @"C:\repos\my repo\feature branch";

        List<string> result = GitService.BuildGitArgumentList(
            new[] { "worktree", "add", pathWithSpaces, "my-branch" }, false);

        result.Should().Contain(pathWithSpaces);
        result.Should().HaveCount(4);
    }
}
