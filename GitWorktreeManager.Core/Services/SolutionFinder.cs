namespace GitWorktreeManager.Services;

/// <summary>
/// Finds the most appropriate solution file to open for a worktree.
/// Searches the root directory first and falls back to a bounded recursive
/// search so solutions located in a subdirectory are still detected.
/// </summary>
public static class SolutionFinder
{
    /// <summary>
    /// Maximum directory depth to search when no solution is found at the worktree root.
    /// </summary>
    public const int MaxSolutionSearchDepth = 4;

    /// <summary>
    /// Directories that are skipped while searching for solution files because they
    /// never contain a user-authored solution and can be expensive to traverse.
    /// </summary>
    public static readonly string[] ExcludedSearchDirectories =
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".vscode", "packages", "TestResults"
    };

    /// <summary>
    /// Finds the most appropriate solution file to open for a worktree.
    /// Returns <c>null</c> when none exists or the choice is ambiguous (open the folder instead).
    /// </summary>
    public static string? FindBestSolutionFile(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return null;
        }

        List<string> rootFiles = EnumerateSolutionFiles(rootPath, maxDepth: 0);

        // If the root contains any solution files, the root wins:
        // return the unambiguous pick, or null (open folder) when ambiguous.
        // Do NOT fall through to subdirectories — root files would poison the result.
        if (rootFiles.Count != 0)
        {
            return PickSolution(rootFiles);
        }

        // Nothing at the root — search subdirectories only (skip root files, there are none).
        return PickSolution(EnumerateSubdirectorySolutionFiles(rootPath, MaxSolutionSearchDepth));
    }

    /// <summary>
    /// Chooses a single solution file from the candidates, preferring <c>.slnx</c>
    /// over <c>.sln</c> when both are present. Returns <c>null</c> when the choice is
    /// ambiguous (no candidates, or multiple of the preferred kind).
    /// </summary>
    public static string? PickSolution(List<string> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        List<string> slnx = files.Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> candidates = slnx.Count > 0 ? slnx : files;

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Recursively collects <c>.sln</c>/<c>.slnx</c> files up to <paramref name="maxDepth"/>
    /// levels below <paramref name="rootPath"/>, skipping build and tooling directories.
    /// </summary>
    public static List<string> EnumerateSolutionFiles(string rootPath, int maxDepth)
    {
        var results = new List<string>();
        CollectSolutionFiles(rootPath, maxDepth, results);
        return results;
    }

    /// <summary>
    /// Collects solution files from subdirectories only (excludes root-level files).
    /// Used for the fallback search so an ambiguous root does not poison subdirectory results.
    /// </summary>
    public static List<string> EnumerateSubdirectorySolutionFiles(string rootPath, int maxDepth)
    {
        var results = new List<string>();

        try
        {
            foreach (string subDirectory in Directory.EnumerateDirectories(rootPath))
            {
                string name = Path.GetFileName(subDirectory);
                if (string.IsNullOrEmpty(name) ||
                    name.StartsWith('.') ||
                    ExcludedSearchDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                CollectSolutionFiles(subDirectory, maxDepth - 1, results);
            }
        }
        catch
        {
            // Ignore directories we cannot enumerate (e.g. access denied).
        }

        return results;
    }

    private static void CollectSolutionFiles(string directory, int remainingDepth, List<string> results)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(file);
                }
            }

            if (remainingDepth <= 0)
            {
                return;
            }

            foreach (string subDirectory in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(subDirectory);
                if (string.IsNullOrEmpty(name) ||
                    name.StartsWith('.') ||
                    ExcludedSearchDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                CollectSolutionFiles(subDirectory, remainingDepth - 1, results);
            }
        }
        catch
        {
            // Ignore directories we cannot enumerate (e.g. access denied).
        }
    }
}
