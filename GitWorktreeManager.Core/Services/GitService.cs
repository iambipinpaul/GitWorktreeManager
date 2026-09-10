namespace GitWorktreeManager.Services;

using System.Diagnostics;
using System.Text;
using Models;

/// <summary>
/// Service for executing Git worktree commands using the Git CLI.
/// </summary>
public class GitService : IGitService
{
    private const int DefaultTimeoutMs = 30000; // 30 seconds
    private const string GitExecutable = "git";
    private static readonly string[] LongPathErrorPatterns =
    {
        "filename too long",
        "file name too long",
        "path too long",
        "path length",
        "path, file name, or both are too long"
    };

    private readonly ILoggerService? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitService"/> class.
    /// </summary>
    public GitService() : this(null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GitService"/> class with logging support.
    /// </summary>
    /// <param name="logger">The logger service for recording operations.</param>
    public GitService(ILoggerService? logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GitCommandResult<IReadOnlyList<Worktree>>> GetWorktreesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation($"Getting worktrees for repository: {repositoryPath}");

        GitProcessResult result = await ExecuteGitCommandAsync(
            repositoryPath,
            new[] { "worktree", "list", "--porcelain" },
            cancellationToken);

        if (!result.Success)
        {
            _logger?.LogError($"Failed to list worktrees: {result.ErrorMessage}");
            return GitCommandResult<IReadOnlyList<Worktree>>.Fail(
                result.ErrorMessage ?? "Failed to list worktrees",
                result.ExitCode);
        }

        IReadOnlyList<Worktree> worktrees = WorktreeParser.ParsePorcelainOutput(result.Output ?? string.Empty);
        _logger?.LogInformation($"Found {worktrees.Count} worktree(s)");
        return GitCommandResult<IReadOnlyList<Worktree>>.Ok(worktrees);
    }

    /// <inheritdoc />
    public async Task<GitCommandResult> AddWorktreeAsync(
        string repositoryPath,
        string worktreePath,
        string branchName,
        bool createBranch = false,
        string? baseBranch = null,
        CancellationToken cancellationToken = default)
    {
        // Build command: git worktree add [-b <branch>] <path> [<commit-ish>]
        // With -b: git worktree add -b <new-branch> <path> [<base-branch>]
        // Without -b: git worktree add <path> <branch>
        // Passed as an argument list (no shell quoting) so paths/branches with
        // spaces or quotes are handled safely.
        List<string> arguments;

        if (createBranch)
        {
            // Create new branch based on another branch
            if (!string.IsNullOrEmpty(baseBranch))
            {
                arguments = new List<string> { "worktree", "add", "-b", branchName, worktreePath, baseBranch };
            }
            else
            {
                arguments = new List<string> { "worktree", "add", "-b", branchName, worktreePath };
            }
        }
        else
        {
            // Checkout existing branch
            arguments = new List<string> { "worktree", "add", worktreePath, branchName };
        }

        _logger?.LogInformation(
            $"Adding worktree: path='{worktreePath}', branch='{branchName}', createBranch={createBranch}, baseBranch='{baseBranch}'");

        GitProcessResult result = await ExecuteGitCommandAsync(
            repositoryPath,
            arguments,
            cancellationToken);

        if (result.Success)
        {
            _logger?.LogInformation($"Successfully added worktree at '{worktreePath}'");
            return GitCommandResult.Ok();
        }
        else
        {
            _logger?.LogError($"Failed to add worktree: {result.ErrorMessage}");
            return GitCommandResult.Fail(result.ErrorMessage ?? "Failed to add worktree", result.ExitCode);
        }
    }

    /// <inheritdoc />
    public async Task<GitCommandResult> RemoveWorktreeAsync(
        string repositoryPath,
        string worktreePath,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        // Build command: git worktree remove [--force] <path>
        // A single --force removes dirty worktrees, but locked worktrees require
        // -f -f (e.g. "cannot remove a locked working tree ... use 'remove -f -f'").
        // Send --force twice whenever force is requested so one flag covers both.
        List<string> arguments = force
            ? new List<string> { "worktree", "remove", "--force", "--force", worktreePath }
            : new List<string> { "worktree", "remove", worktreePath };

        _logger?.LogInformation($"Removing worktree: path='{worktreePath}', force={force}");

        GitProcessResult result = await ExecuteGitCommandAsync(
            repositoryPath,
            arguments,
            cancellationToken);

        if (result.Success)
        {
            _logger?.LogInformation($"Successfully removed worktree at '{worktreePath}'");
            return GitCommandResult.Ok();
        }
        else
        {
            _logger?.LogError($"Failed to remove worktree: {result.ErrorMessage}");
            return GitCommandResult.Fail(result.ErrorMessage ?? "Failed to remove worktree", result.ExitCode);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetRepositoryRootAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation($"Getting repository root for path: {path}");

        GitProcessResult result = await ExecuteGitCommandAsync(
            path,
            new[] { "rev-parse", "--show-toplevel" },
            cancellationToken);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            _logger?.LogWarning($"Path '{path}' is not within a Git repository");
            return null;
        }

        string root = result.Output.Trim();
        _logger?.LogInformation($"Repository root: {root}");
        return root;
    }

    /// <inheritdoc />
    public async Task<bool> IsGitInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Checking if Git is installed");

        try
        {
            GitProcessResult result = await ExecuteGitCommandAsync(
                Directory.GetCurrentDirectory(),
                new[] { "--version" },
                cancellationToken);

            if (result.Success)
            {
                _logger?.LogInformation($"Git is installed: {result.Output?.Trim()}");
            }
            else
            {
                _logger?.LogWarning("Git is not installed or not in PATH");
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogException(ex, "Error checking Git installation");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<GitCommandResult<IReadOnlyList<string>>> GetBranchesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation($"Getting branches for repository: {repositoryPath}");

        // Get local branches
        GitProcessResult localResult = await ExecuteGitCommandAsync(
            repositoryPath,
            new[] { "branch", "--format=%(refname:short)" },
            cancellationToken);

        // Get remote branches
        GitProcessResult remoteResult = await ExecuteGitCommandAsync(
            repositoryPath,
            new[] { "branch", "-r", "--format=%(refname:short)" },
            cancellationToken);

        var branches = new List<string>();

        if (localResult.Success && !string.IsNullOrWhiteSpace(localResult.Output))
        {
            IEnumerable<string> localBranches = localResult.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim())
                .Where(b => !string.IsNullOrEmpty(b));
            branches.AddRange(localBranches);
        }

        if (remoteResult.Success && !string.IsNullOrWhiteSpace(remoteResult.Output))
        {
            IEnumerable<string> remoteBranches = remoteResult.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim())
                .Where(b => !string.IsNullOrEmpty(b) && !b.Contains("HEAD"))
                // Remove origin/ prefix for cleaner display but keep track it's remote
                .Select(b => b.StartsWith("origin/") ? b.Substring(7) : b)
                .Where(b => !branches.Contains(b)); // Don't duplicate local branches
            branches.AddRange(remoteBranches);
        }

        _logger?.LogInformation($"Found {branches.Count} branch(es)");
        return GitCommandResult<IReadOnlyList<string>>.Ok(branches.Distinct().OrderBy(b => b).ToList());
    }


    /// <summary>
    /// Executes a Git command and returns the result.
    /// Arguments are passed via <see cref="ProcessStartInfo.ArgumentList"/> (no shell),
    /// so paths/branches with spaces or quotes are handled safely.
    /// </summary>
    /// <param name="workingDirectory">The working directory for the command.</param>
    /// <param name="arguments">The Git command arguments (already split).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The result of the command execution.</returns>
    private async Task<GitProcessResult> ExecuteGitCommandAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        bool enableLongPathSupport = OperatingSystem.IsWindows();
        List<string> fullArgs = BuildGitArgumentList(arguments, enableLongPathSupport);
        string displayArgs = string.Join(" ", fullArgs.Select(QuoteForDisplay));

        _logger?.LogInformation($"Executing: git {displayArgs} (in {workingDirectory})");

        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in fullArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for process with timeout and cancellation support
            using var timeoutCts = new CancellationTokenSource(DefaultTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                TryKillProcess(process);
                _logger?.LogError($"Git command timed out after {DefaultTimeoutMs}ms: git {displayArgs}");
                return new GitProcessResult { Success = false, ExitCode = -1, ErrorMessage = "Git command timed out" };
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                _logger?.LogWarning($"Git command was cancelled: git {displayArgs}");
                return new GitProcessResult
                {
                    Success = false, ExitCode = -1, ErrorMessage = "Git command was cancelled"
                };
            }

            int exitCode = process.ExitCode;
            string output = stdout.ToString();
            string error = stderr.ToString();

            // Log stdout if present
            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger?.LogInformation($"Git stdout:\n{output.Trim()}");
            }

            // Log stderr if present (as warning for exit code 0, error otherwise)
            if (!string.IsNullOrWhiteSpace(error))
            {
                if (exitCode == 0)
                {
                    _logger?.LogWarning($"Git stderr (exit code 0):\n{error.Trim()}");
                }
                else
                {
                    _logger?.LogError($"Git stderr (exit code {exitCode}):\n{error.Trim()}");
                }
            }

            _logger?.LogInformation($"Git command completed with exit code: {exitCode}");

            string rawError = error.Trim();
            string? errorMessage = exitCode != 0
                ? CreateUserFacingErrorMessage(
                    string.IsNullOrWhiteSpace(rawError)
                        ? $"Git command failed with exit code {exitCode}."
                        : rawError,
                    enableLongPathSupport)
                : null;

            return new GitProcessResult
            {
                Success = exitCode == 0,
                ExitCode = exitCode,
                Output = output,
                ErrorMessage = errorMessage
            };
        }
        catch (Exception ex)
        {
            _logger?.LogException(ex, $"Failed to execute Git command: git {displayArgs}");
            string errorMessage = CreateUserFacingErrorMessage(
                $"Failed to execute Git command: {ex.Message}",
                enableLongPathSupport);

            return new GitProcessResult
            {
                Success = false, ExitCode = -1, ErrorMessage = errorMessage
            };
        }
    }

    internal static string BuildGitArguments(string arguments, bool enableLongPathSupport)
    {
        return enableLongPathSupport
            ? $"-c core.longpaths=true {arguments}"
            : arguments;
    }

    internal static List<string> BuildGitArgumentList(IEnumerable<string> arguments, bool enableLongPathSupport)
    {
        var args = arguments.ToList();
        if (enableLongPathSupport)
        {
            args.Insert(0, "core.longpaths=true");
            args.Insert(0, "-c");
        }

        return args;
    }

    internal static string QuoteForDisplay(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        return arg.Any(char.IsWhiteSpace) || arg.Contains('"')
            ? $"\"{arg.Replace("\"", "\\\"")}\""
            : arg;
    }

    /// <summary>
    /// Indicates whether a remove failure is caused by uncommitted changes,
    /// in which case the user can be offered a force remove.
    /// </summary>
    public static bool IsDirtyWorktreeError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("modified or untracked files", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("contains modified or untracked files", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("forcing it", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Indicates whether a remove failure is caused by a worktree lock,
    /// in which case the user can be offered a force remove (sent as -f -f).
    /// </summary>
    public static bool IsLockedWorktreeError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("locked working tree", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("remove -f -f", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("unlock first", StringComparison.OrdinalIgnoreCase) ||
            (errorMessage.Contains("locked", StringComparison.OrdinalIgnoreCase) &&
                errorMessage.Contains("worktree", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Indicates whether a remove failure should offer the user a force remove dialog
    /// (dirty worktree or locked worktree — force covers both).
    /// </summary>
    public static bool ShouldOfferForceRemove(string? errorMessage) =>
        IsDirtyWorktreeError(errorMessage) || IsLockedWorktreeError(errorMessage);

    internal static bool IsLongPathError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return LongPathErrorPatterns.Any(
            pattern => errorMessage.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    internal static string CreateUserFacingErrorMessage(string errorMessage, bool longPathSupportWasEnabled)
    {
        if (!IsLongPathError(errorMessage))
        {
            return errorMessage;
        }

        string supportStatus = longPathSupportWasEnabled
            ? "Git Worktree Manager already ran this Git command with process-scoped core.longpaths=true."
            : "This looks like a Git for Windows long path error.";

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            errorMessage,
            supportStatus +
            " If the same repository also fails from a terminal or another Git tool, enable Git for Windows long paths globally with:",
            "git config --global core.longpaths true",
            "This is a Git for Windows setting, not a Visual Studio setting.");
    }

    /// <inheritdoc />
    public async Task<WorktreeStatus> GetWorktreeStatusAsync(string path, string branch, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return new WorktreeStatus(0, 0, 0, 0, false);
            }

            // 1. Check modifications and untracked files
            // Use a shorter timeout for status (e.g., 10s) as we don't want to hang the UI enrichment
            using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            statusCts.CancelAfter(10000);

            // --porcelain=v1 is stable. 
            // -uno (no untracked) is fast, but user specifically wants untracked.
            // Using -unormal is usually faster than -uall.
            GitProcessResult statusRes =
                await ExecuteGitCommandAsync(path, new[] { "status", "--porcelain=v1", "-unormal" }, statusCts.Token);

            int modified = 0;
            int untracked = 0;

            if (statusRes.Success && !string.IsNullOrWhiteSpace(statusRes.Output))
            {
                string[] lines = statusRes.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.Length < 3)
                    {
                        continue;
                    }

                    string status = line.Substring(0, 2);
                    if (status == "??")
                    {
                        untracked++;
                    }
                    else
                    {
                        modified++;
                    }
                }
            }

            // 2. Check Ahead/Behind (Incoming/Outgoing)
            int ahead = 0;
            int behind = 0;
            bool hasUpstream = false;

            try
            {
                // Check if we have an upstream. Use a very short timeout.
                using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                upstreamCts.CancelAfter(5000);

                GitProcessResult upstreamRes = await ExecuteGitCommandAsync(path,
                    new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" }, upstreamCts.Token);

                if (upstreamRes.Success && !string.IsNullOrWhiteSpace(upstreamRes.Output))
                {
                    // We have an upstream tracking branch
                    hasUpstream = true;
                    
                    // Get ahead/behind counts
                    GitProcessResult countRes = await ExecuteGitCommandAsync(path,
                        new[] { "rev-list", "--left-right", "--count", "HEAD...@{u}" }, upstreamCts.Token);
                    if (countRes.Success && !string.IsNullOrWhiteSpace(countRes.Output))
                    {
                        string[] parts = countRes.Output.Trim()
                            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            if (int.TryParse(parts[0], out int canForward))
                            {
                                ahead = canForward;
                            }

                            if (int.TryParse(parts[1], out int canPull))
                            {
                                behind = canPull;
                            }
                        }
                    }
                }
                // If upstreamRes failed or output is empty, hasUpstream remains false (local-only branch)
            }
            catch (Exception)
            {
                // If upstream check fails, it's a local-only branch
                _logger?.LogWarning($"No upstream found for {path}. Branch is local-only.");
                hasUpstream = false;
            }

            return new WorktreeStatus(modified, untracked, behind, ahead, hasUpstream);
        }
        catch (OperationCanceledException)
        {
            return new WorktreeStatus(0, 0, 0, 0, false);
        }
        catch (Exception ex)
        {
            _logger?.LogException(ex, $"Failed to get status for {path}");
            return new WorktreeStatus(0, 0, 0, 0, false);
        }
    }

    /// <summary>
    /// Attempts to kill a process safely.
    /// </summary>
    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Ignore errors when killing process
        }
    }

    /// <summary>
    /// Internal result type for Git process execution.
    /// </summary>
    private record GitProcessResult
    {
        public bool Success { get; init; }
        public int ExitCode { get; init; }
        public string? Output { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
