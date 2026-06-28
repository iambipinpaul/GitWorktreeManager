using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Linq;
using GitWorktreeManager.Dialogs;
using GitWorktreeManager.Models;
using GitWorktreeManager.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

namespace GitWorktreeManager.ViewModels;

/// <summary>
/// ViewModel for the Worktree Manager tool window.
/// Manages the collection of worktrees and exposes commands for worktree operations.
/// Uses DataContract attributes for Remote UI serialization.
/// </summary>
[DataContract]
public class WorktreeViewModel : INotifyPropertyChanged
{
    private readonly IGitService _gitService;
    private readonly VisualStudioExtensibility? _extensibility;
    private readonly INotificationService? _notificationService;
    private bool _isLoading;
    private bool _hasRepository;
    private string? _errorMessage;
    private string? _repositoryPath;
    private string? _successMessage;
    private string? _searchFilter;
    private ObservableCollection<WorktreeItemViewModel> _filteredWorktrees;
    private CancellationTokenSource? _enrichmentCts;

    /// <summary>
    /// Initializes a new instance of the WorktreeViewModel.
    /// </summary>
    /// <param name="gitService">The Git service for executing commands.</param>
    /// <param name="extensibility">The VS extensibility object for showing dialogs.</param>
    /// <param name="notificationService">The notification service for displaying messages.</param>
    public WorktreeViewModel(
        IGitService gitService,
        VisualStudioExtensibility? extensibility = null,
        INotificationService? notificationService = null)
    {
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
        _extensibility = extensibility;
        _notificationService = notificationService;
        Worktrees = new ObservableCollection<WorktreeItemViewModel>();
        _filteredWorktrees = new ObservableCollection<WorktreeItemViewModel>();

        // Initialize commands
        RefreshCommand = new AsyncCommand(async (_, ct) => await RefreshAsync(ct));
        AddWorktreeCommand = new AsyncCommand(async (_, ct) => await OnAddWorktreeCommandAsync(ct));
        RemoveWorktreeCommand = new AsyncCommand(async (param, ct) =>
        {
            // Debug: Log what we received
            System.Diagnostics.Debug.WriteLine(
                $"RemoveWorktreeCommand received param type: {param?.GetType().Name ?? "null"}");

            if (param is WorktreeItemViewModel item)
            {
                await RemoveWorktreeAsync(item, false, ct);
            }
            else
            {
                // Parameter wasn't the expected type - show error
                string errorMsg = $"Remove command received unexpected parameter: {param?.GetType().Name ?? "null"}";
                System.Diagnostics.Debug.WriteLine(errorMsg);
                await ShowErrorNotificationAsync("Failed to remove worktree - invalid selection", ct);
            }
        });
        OpenInNewWindowCommand = new AsyncCommand(async (param, ct) =>
        {
            if (param is WorktreeItemViewModel item)
            {
                await OpenInNewWindowAsync(item, ct);
            }
        });
        OpenInExplorerCommand = new AsyncCommand(async (param, ct) =>
        {
            System.Diagnostics.Debug.WriteLine($"OpenInExplorerCommand param: {param?.GetType().Name ?? "null"}");
            if (param is WorktreeItemViewModel item)
            {
                System.Diagnostics.Debug.WriteLine($"OpenInExplorerCommand path: {item.Path}");
                await OpenInExplorerAsync(item, ct);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"OpenInExplorerCommand: param is not WorktreeItemViewModel");
            }
        });
        CopyPathCommand = new AsyncCommand(async (param, ct) =>
        {
            if (param is WorktreeItemViewModel item)
            {
                await CopyPathToClipboardAsync(item, ct);
            }
        });
        DismissErrorCommand = new AsyncCommand((_, ct) =>
        {
            ErrorMessage = null;
            return Task.CompletedTask;
        });
        DismissSuccessCommand = new AsyncCommand((_, ct) =>
        {
            SuccessMessage = null;
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Command to refresh the worktree list.
    /// </summary>
    [DataMember]
    public IAsyncCommand RefreshCommand { get; }

    /// <summary>
    /// Command to add a new worktree.
    /// </summary>
    [DataMember]
    public IAsyncCommand AddWorktreeCommand { get; }

    /// <summary>
    /// Command to remove a worktree.
    /// </summary>
    [DataMember]
    public IAsyncCommand RemoveWorktreeCommand { get; }

    /// <summary>
    /// Command to open a worktree in a new Visual Studio window.
    /// </summary>
    [DataMember]
    public IAsyncCommand OpenInNewWindowCommand { get; }

    /// <summary>
    /// Command to open a worktree folder in Windows Explorer.
    /// </summary>
    [DataMember]
    public IAsyncCommand OpenInExplorerCommand { get; }

    /// <summary>
    /// Command to copy a worktree path to clipboard.
    /// </summary>
    [DataMember]
    public IAsyncCommand CopyPathCommand { get; }

    /// <summary>
    /// Command to dismiss the error message.
    /// </summary>
    [DataMember]
    public IAsyncCommand DismissErrorCommand { get; }

    /// <summary>
    /// Command to dismiss the success message.
    /// </summary>
    [DataMember]
    public IAsyncCommand DismissSuccessCommand { get; }

    /// <summary>
    /// Handles the Add Worktree command - shows dialog and creates worktree.
    /// </summary>
    private async Task OnAddWorktreeCommandAsync(CancellationToken cancellationToken)
    {
        if (_extensibility == null)
        {
            ErrorMessage = "Cannot show dialog: extensibility not available.";
            return;
        }

        if (string.IsNullOrEmpty(RepositoryPath))
        {
            ErrorMessage = "No repository is currently open.";
            await ShowErrorNotificationAsync("No repository is currently open.", cancellationToken);
            return;
        }

        // Clear any previous messages
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            // Show the Add Worktree dialog with GitService for branch loading
            var dialog = new AddWorktreeDialog(_extensibility, _gitService);
            AddWorktreeDialogResult? result = await dialog.ShowAsync(RepositoryPath, cancellationToken);

            if (result == null)
            {
                // User cancelled
                return;
            }

            // Validate inputs
            if (string.IsNullOrWhiteSpace(result.BranchName))
            {
                ErrorMessage = "Branch name cannot be empty.";
                await ShowErrorNotificationAsync("Branch name cannot be empty.", cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.WorktreePath))
            {
                ErrorMessage = "Worktree path cannot be empty.";
                await ShowErrorNotificationAsync("Worktree path cannot be empty.", cancellationToken);
                return;
            }

            // Add the worktree
            (bool Success, string? ErrorMessage) addResult = await AddWorktreeAsync(
                result.WorktreePath,
                result.BranchName,
                result.CreateNewBranch,
                result.BaseBranch,
                cancellationToken);

            if (addResult.Success)
            {
                SuccessMessage = $"Worktree '{result.BranchName}' created successfully!";

                // Open in new VS window if requested
                if (result.OpenAfterCreation)
                {
                    var worktreeItem = new WorktreeItemViewModel
                    {
                        Path = result.WorktreePath,
                        DisplayPath = Path.GetFileName(result.WorktreePath),
                        BranchName = result.BranchName,
                        HeadCommit = string.Empty,
                        IsMainWorktree = false,
                        IsLocked = false,
                        IsPrunable = false
                    };
                    await OpenInNewWindowAsync(worktreeItem, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            string errorMsg = $"Error adding worktree: {ex.Message}";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
        }
    }

    /// <summary>
    /// Event raised when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The collection of worktrees to display in the UI.
    /// </summary>
    [DataMember]
    public ObservableCollection<WorktreeItemViewModel> Worktrees { get; }

    /// <summary>
    /// Indicates whether a loading operation is in progress.
    /// </summary>
    [DataMember]
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowNoRepositoryMessage));
            }
        }
    }

    /// <summary>
    /// Indicates whether a Git repository is detected.
    /// </summary>
    [DataMember]
    public bool HasRepository
    {
        get => _hasRepository;
        private set
        {
            if (SetProperty(ref _hasRepository, value))
            {
                OnPropertyChanged(nameof(ShowNoRepositoryMessage));
            }
        }
    }

    /// <summary>
    /// The current error message, if any.
    /// </summary>
    [DataMember]
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>
    /// The current success message, if any.
    /// </summary>
    [DataMember]
    public string? SuccessMessage
    {
        get => _successMessage;
        private set
        {
            if (SetProperty(ref _successMessage, value))
            {
                OnPropertyChanged(nameof(HasSuccess));
            }
        }
    }

    /// <summary>
    /// The path to the current Git repository.
    /// </summary>
    [DataMember]
    public string? RepositoryPath
    {
        get => _repositoryPath;
        set => SetProperty(ref _repositoryPath, value);
    }

    /// <summary>
    /// The search filter text for filtering worktrees.
    /// </summary>
    [DataMember]
    public string? SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
            {
                UpdateFilteredWorktrees();
            }
        }
    }

    /// <summary>
    /// The filtered collection of worktrees based on search filter.
    /// </summary>
    [DataMember]
    public ObservableCollection<WorktreeItemViewModel> FilteredWorktrees
    {
        get => _filteredWorktrees;
        private set => SetProperty(ref _filteredWorktrees, value);
    }

    /// <summary>
    /// Indicates whether the "No Repository" message should be shown.
    /// True when not loading and no repository is detected.
    /// </summary>
    [DataMember]
    public bool ShowNoRepositoryMessage => !IsLoading && !HasRepository;

    /// <summary>
    /// Indicates whether the "No Worktrees" message should be shown.
    /// True when has repository, not loading, and no filtered worktrees.
    /// </summary>
    [DataMember]
    public bool ShowNoWorktreesMessage => HasRepository && !IsLoading && FilteredWorktrees.Count == 0;

    /// <summary>
    /// Indicates whether the worktree list should be shown.
    /// True when has repository and has filtered worktrees.
    /// </summary>
    [DataMember]
    public bool ShowWorktreeList => HasRepository && FilteredWorktrees.Count > 0;

    /// <summary>
    /// Indicates whether there is an error message to display.
    /// </summary>
    [DataMember]
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Indicates whether there is a success message to display.
    /// </summary>
    [DataMember]
    public bool HasSuccess => !string.IsNullOrEmpty(SuccessMessage);

    /// <summary>
    /// Indicates whether Git is not installed.
    /// </summary>
    [DataMember]
    public bool IsGitNotInstalled { get; private set; }

    /// <summary>
    /// Sets the error state when Git is not installed.
    /// </summary>
    public void SetGitNotInstalledError()
    {
        IsGitNotInstalled = true;
        HasRepository = false;
        ErrorMessage =
            "Git is not installed or not found in PATH. Please install Git and ensure it is added to your system PATH.";
        OnPropertyChanged(nameof(IsGitNotInstalled));
    }


    /// <summary>
    /// Refreshes the worktree list from the Git repository.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <summary>
    /// Refreshes the worktree list from the Git repository.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Cancel any previous enrichment
        if (_enrichmentCts != null)
        {
            await _enrichmentCts.CancelAsync();
        }

        _enrichmentCts = new CancellationTokenSource();
        // Link with the passed token if needed, but usually enrichment can run independently until refresh
        CancellationToken linkedToken = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, _enrichmentCts.Token).Token;

        // Don't refresh if Git is not installed
        if (IsGitNotInstalled)
        {
            return;
        }

        if (string.IsNullOrEmpty(RepositoryPath))
        {
            HasRepository = false;
            Worktrees.Clear();
            FilteredWorktrees.Clear();
            ErrorMessage = null;
            OnPropertyChanged(nameof(ShowNoWorktreesMessage));
            OnPropertyChanged(nameof(ShowWorktreeList));
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // PHASE 1: Fast load
            GitCommandResult<IReadOnlyList<Worktree>> result =
                await _gitService.GetWorktreesAsync(RepositoryPath, cancellationToken);

            if (result.Success && result.Data != null)
            {
                HasRepository = true;
                Worktrees.Clear();

                // Compute shortest unique paths for all worktrees
                Dictionary<string, string> displayPaths = ComputeShortestUniquePaths(
                    result.Data.Select(w => w.Path).ToList());

                foreach (Worktree worktree in result.Data)
                {
                    WorktreeItemViewModel viewModel = MapToViewModel(worktree, displayPaths);
                    // Initialize new properties
                    viewModel.IsLoadingStatus = true;
                    viewModel.StatusSummary = "Loading status...";
                    Worktrees.Add(viewModel);
                }

                UpdateFilteredWorktrees();

                // PHASE 2: Background Enrichment
                // Fire and forget (monitored by _enrichmentCts)
                _ = EnrichWorktreesAsync(Worktrees.ToList(), _enrichmentCts.Token);
            }
            else
            {
                HasRepository = false;
                string errorMsg = result.ErrorMessage ?? "Failed to retrieve worktrees";
                ErrorMessage = errorMsg;
                Worktrees.Clear();
                FilteredWorktrees.Clear();
                OnPropertyChanged(nameof(ShowNoWorktreesMessage));
                OnPropertyChanged(nameof(ShowWorktreeList));

                // Show notification for git errors (includes stderr)
                await ShowErrorNotificationAsync("Failed to retrieve worktrees", result.ErrorMessage,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            HasRepository = false;
            string errorMsg = $"Error refreshing worktrees: {ex.Message}";
            ErrorMessage = errorMsg;
            Worktrees.Clear();
            FilteredWorktrees.Clear();
            OnPropertyChanged(nameof(ShowNoWorktreesMessage));
            OnPropertyChanged(nameof(ShowWorktreeList));
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }


    private async Task EnrichWorktreesAsync(List<WorktreeItemViewModel> items, CancellationToken ct)
    {
        try
        {
            // Use Parallel.ForEachAsync to limit concurrency to 4
            var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };

            await Parallel.ForEachAsync(items, options, async (item, token) =>
            {
                // Skip if path is invalid
                if (string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
                {
                    item.IsLoadingStatus = false;
                    item.StatusSummary = "Invalid path";
                    return;
                }

                try
                {
                    WorktreeStatus status = await _gitService.GetWorktreeStatusAsync(item.Path, item.BranchName, token);

                    // Update properties on the view model
                    item.UncommittedChangesCount = status.ModifiedCount;
                    item.UntrackedChangesCount = status.UntrackedCount;
                    item.HasUncommittedChanges = status.ModifiedCount > 0;

                    item.IncomingCommits = status.Incoming;
                    item.OutgoingCommits = status.Outgoing;
                    item.StatusSummary = FormatStatusSummary(status);
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                }
                catch
                {
                    item.StatusSummary = "Failed to load status";
                }
                finally
                {
                    item.IsLoadingStatus = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Enrichment failed: {ex}");
        }
    }

    private string FormatStatusSummary(WorktreeStatus status)
    {
        // If no upstream tracking branch, show "Local only"
        if (!status.HasUpstream)
        {
            return "Local only";
        }

        var parts = new List<string>();
        if (status.Incoming > 0)
        {
            parts.Add($"{status.Incoming} behind");
        }

        if (status.Outgoing > 0)
        {
            parts.Add($"{status.Outgoing} ahead");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "Synced";
    }

    /// <summary>
    /// Opens a worktree in a new Visual Studio instance.
    /// </summary>
    /// <param name="worktreeItem">The worktree item to open.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A result indicating success or failure with an error message.</returns>
    public async Task<(bool Success, string? ErrorMessage)> OpenInNewWindowAsync(
        WorktreeItemViewModel worktreeItem,
        CancellationToken cancellationToken = default)
    {
        if (worktreeItem == null)
        {
            string errorMsg = "No worktree selected";
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        if (!Directory.Exists(worktreeItem.Path))
        {
            string errorMsg = $"Worktree path does not exist: {worktreeItem.Path}";
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        try
        {
            // Find the best solution file, searching subdirectories when none is
            // present at the worktree root. When a single solution is found it is
            // opened directly; otherwise the folder is opened so the user can choose.
            string? solutionFile = FindBestSolutionFile(worktreeItem.Path);
            string argument = solutionFile != null
                ? $"\"{solutionFile}\""
                : $"\"{worktreeItem.Path}\"";

            // Launch devenv.exe with the appropriate argument
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "devenv.exe", Arguments = argument, UseShellExecute = true
            };

            await Task.Run(() => { System.Diagnostics.Process.Start(startInfo); }, cancellationToken);

            return (true, null);
        }
        catch (Exception ex)
        {
            string errorMsg = $"Error opening worktree in new window: {ex.Message}";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }
    }

    /// <summary>
    /// Maximum directory depth to search when no solution is found at the worktree root.
    /// </summary>
    private const int MaxSolutionSearchDepth = 4;

    /// <summary>
    /// Directories that are skipped while searching for solution files because they
    /// never contain a user-authored solution and can be expensive to traverse.
    /// </summary>
    private static readonly string[] ExcludedSearchDirectories =
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".vscode", "packages", "TestResults"
    };

    /// <summary>
    /// Finds the most appropriate solution file to open for a worktree.
    /// Searches the root directory first and falls back to a bounded recursive
    /// search so solutions located in a subdirectory are still detected.
    /// </summary>
    /// <param name="rootPath">The worktree root directory.</param>
    /// <returns>The full path of the solution to open, or <c>null</c> when none is unambiguous.</returns>
    private static string? FindBestSolutionFile(string rootPath)
    {
        // Prefer a solution at the root so the common case stays fast.
        string? rootSolution = PickSolution(EnumerateSolutionFiles(rootPath, maxDepth: 0));
        if (rootSolution != null)
        {
            return rootSolution;
        }

        // Nothing at the root - search subdirectories (e.g. solution under src/).
        return PickSolution(EnumerateSolutionFiles(rootPath, MaxSolutionSearchDepth));
    }

    /// <summary>
    /// Chooses a single solution file from the candidates, preferring <c>.slnx</c>
    /// over <c>.sln</c> when both are present. Returns <c>null</c> when the choice is
    /// ambiguous (no candidates, or multiple of the preferred kind).
    /// </summary>
    private static string? PickSolution(List<string> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        // Prefer .slnx over .sln when both exist (see .github/copilot-instructions.md).
        var slnx = files.Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = slnx.Count > 0 ? slnx : files;

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Recursively collects <c>.sln</c>/<c>.slnx</c> files up to <paramref name="maxDepth"/>
    /// levels below <paramref name="rootPath"/>, skipping build and tooling directories.
    /// </summary>
    private static List<string> EnumerateSolutionFiles(string rootPath, int maxDepth)
    {
        var results = new List<string>();
        CollectSolutionFiles(rootPath, maxDepth, results);
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
                if (name.StartsWith('.') ||
                    ExcludedSearchDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                CollectSolutionFiles(subDirectory, remainingDepth - 1, results);
            }
        }
        catch (Exception)
        {
            // Ignore directories we cannot enumerate (e.g. access denied).
        }
    }

    /// <summary>
    /// Removes a worktree from the repository.
    /// </summary>
    /// <param name="worktreeItem">The worktree item to remove.</param>
    /// <param name="force">If true, forces removal even with uncommitted changes.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A result indicating success or failure with an error message.</returns>
    public async Task<(bool Success, string? ErrorMessage)> RemoveWorktreeAsync(
        WorktreeItemViewModel worktreeItem,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Debug.WriteLine($"RemoveWorktreeAsync called for: {worktreeItem?.Path ?? "null"}");

        if (string.IsNullOrEmpty(RepositoryPath))
        {
            string errorMsg = "No repository is currently open";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        if (worktreeItem == null)
        {
            string errorMsg = "No worktree selected";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        // Validate not main worktree
        if (worktreeItem.IsMainWorktree)
        {
            string errorMsg = "Cannot remove the main worktree";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        if (!worktreeItem.CanRemove)
        {
            string errorMsg = "This worktree cannot be removed";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        // Check if this is the currently open worktree
        if (IsCurrentWorktree(worktreeItem.Path))
        {
            string errorMsg =
                "Cannot remove the currently open worktree. Please close this solution first or switch to a different worktree.";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        if (_extensibility == null)
        {
            string errorMsg = "Cannot remove worktree: extensibility service not available for confirmation.";
            ErrorMessage = errorMsg;
            return (false, errorMsg);
        }

        // Show confirmation dialog (skip if forcing, as user just confirmed via the Force dialog)
        if (_extensibility != null && !force)
        {
            string confirmMessage =
                $"Are you sure you want to remove the worktree '{worktreeItem.DisplayPath}'?\n\nBranch: {worktreeItem.BranchName}\nPath: {worktreeItem.Path}\n\nThis will delete the worktree folder and its contents.";

            bool result = await _extensibility.Shell().ShowPromptAsync(
                confirmMessage,
                Microsoft.VisualStudio.Extensibility.Shell.PromptOptions.OKCancel,
                cancellationToken);

            if (result != true)
            {
                // User cancelled
                return (false, null);
            }
        }

        IsLoading = true;
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            System.Diagnostics.Debug.WriteLine($"Calling GitService.RemoveWorktreeAsync for: {worktreeItem.Path}");

            GitCommandResult result = await _gitService.RemoveWorktreeAsync(
                RepositoryPath,
                worktreeItem.Path,
                force,
                cancellationToken);

            System.Diagnostics.Debug.WriteLine(
                $"RemoveWorktreeAsync result: Success={result.Success}, Error={result.ErrorMessage}");

            if (result.Success)
            {
                SuccessMessage = $"Worktree '{worktreeItem.DisplayPath}' removed successfully!";
                // Refresh the list to reflect the removal
                await RefreshAsync(cancellationToken);
                return (true, null);
            }
            else
            {
                // Check for common error patterns and provide friendly messages
                string errorMessage = result.ErrorMessage ?? "Unknown error";
                bool folderStillExists = Directory.Exists(worktreeItem.Path);

                if (errorMessage.Contains("failed to delete") || errorMessage.Contains("Invalid argument"))
                {
                    if (folderStillExists)
                    {
                        // Git removed the reference but couldn't delete the folder
                        errorMessage =
                            $"Git worktree reference removed, but the folder could not be deleted (files are in use).\n\nPlease manually delete: {worktreeItem.Path}";

                        // Still refresh the list since the git reference is gone
                        await RefreshAsync(cancellationToken);
                    }
                    else
                    {
                        errorMessage =
                            "Cannot remove worktree - files may be in use. Please close any open files or applications using this worktree.";
                    }
                }
                else if (errorMessage.Contains("modified or untracked files") ||
                         errorMessage.Contains("contains modified or untracked files") ||
                         errorMessage.Contains("forcing it"))
                {
                    // If we haven't already tried to force remove, ask the user if they want to
                    if (!force && _extensibility != null)
                    {
                        var dialog = new DeleteConfirmationDialog(_extensibility);
                        bool confirmed = await dialog.ShowAsync(
                            worktreeItem.DisplayPath, // User must type this name
                            worktreeItem.Path,
                            cancellationToken);

                        if (confirmed)
                        {
                            // Retry with force = true
                            (bool Success, string? ErrorMessage) forceRemoveResult =
                                await RemoveWorktreeAsync(worktreeItem, true, cancellationToken);

                            // If successful, return true (recursive call invalidates this frame's return but we return its result)
                            if (forceRemoveResult.Success)
                            {
                                return (true, null);
                            }
                            else
                            {
                                // If force remove failed, return that error
                                return forceRemoveResult;
                            }
                        }
                        else
                        {
                            // User cancelled the force remove dialog - do nothing, just return false
                            // Don't show an error message since this was a user choice
                            return (false, null);
                        }
                    }

                    errorMessage =
                        "Worktree has uncommitted changes. Commit or stash your changes, or force remove using the dialog.";
                }

                ErrorMessage = errorMessage;

                // Show notification with details
                await ShowErrorNotificationAsync(
                    "Failed to remove worktree",
                    errorMessage,
                    cancellationToken);

                return (false, errorMessage);
            }
        }
        catch (Exception ex)
        {
            string errorMsg = $"Error removing worktree: {ex.Message}";
            ErrorMessage = errorMsg;
            System.Diagnostics.Debug.WriteLine($"Exception in RemoveWorktreeAsync: {ex}");
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Adds a new worktree to the repository.
    /// </summary>
    /// <param name="worktreePath">The path where the new worktree will be created.</param>
    /// <param name="branchName">The branch name to checkout in the worktree.</param>
    /// <param name="createBranch">If true, creates a new branch.</param>
    /// <param name="baseBranch">The base branch to create the new branch from.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A result indicating success or failure with an error message.</returns>
    public async Task<(bool Success, string? ErrorMessage)> AddWorktreeAsync(
        string worktreePath,
        string branchName,
        bool createBranch = false,
        string? baseBranch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(RepositoryPath))
        {
            string errorMsg = "No repository is currently open";
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            string errorMsg = "Worktree path cannot be empty";
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        if (string.IsNullOrWhiteSpace(branchName))
        {
            string errorMsg = "Branch name cannot be empty";
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            GitCommandResult result = await _gitService.AddWorktreeAsync(
                RepositoryPath,
                worktreePath,
                branchName,
                createBranch,
                baseBranch,
                cancellationToken);

            if (result.Success)
            {
                // Refresh the list to show the new worktree
                await RefreshAsync(cancellationToken);
                return (true, null);
            }
            else
            {
                ErrorMessage = result.ErrorMessage;

                // Show notification with git stderr details
                await ShowErrorNotificationAsync(
                    "Failed to add worktree",
                    result.ErrorMessage,
                    cancellationToken);

                return (false, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            string errorMsg = $"Error adding worktree: {ex.Message}";
            ErrorMessage = errorMsg;
            await ShowErrorNotificationAsync(errorMsg, cancellationToken);
            return (false, errorMsg);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Opens a worktree folder in Windows Explorer.
    /// </summary>
    private async Task OpenInExplorerAsync(WorktreeItemViewModel worktreeItem, CancellationToken cancellationToken)
    {
        // Validate input
        if (worktreeItem == null)
        {
            ErrorMessage = "No worktree selected";
            return;
        }

        if (string.IsNullOrEmpty(worktreeItem.Path))
        {
            ErrorMessage = "Worktree path is empty";
            return;
        }

        string path = worktreeItem.Path;

        // Ensure the path exists
        if (!Directory.Exists(path))
        {
            ErrorMessage = $"Directory does not exist: {path}";
            return;
        }

        try
        {
            // Use ProcessStartInfo with the folder path directly
            // Setting UseShellExecute = true and FileName = path opens the folder
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path, UseShellExecute = true, Verb = "open"
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to open Explorer: {ex.Message}";
        }
    }

    /// <summary>
    /// Copies a worktree path to the clipboard.
    /// </summary>
    private Task CopyPathToClipboardAsync(WorktreeItemViewModel worktreeItem, CancellationToken cancellationToken)
    {
        if (worktreeItem == null || string.IsNullOrEmpty(worktreeItem.Path))
        {
            return Task.CompletedTask;
        }

        try
        {
            string path = worktreeItem.Path;

            // Use a thread with STA apartment state for clipboard access
            var thread = new Thread(() => { System.Windows.Clipboard.SetDataObject(path, true); });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            SuccessMessage = "Path copied to clipboard!";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to copy path: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the filtered worktrees collection based on the search filter.
    /// </summary>
    private void UpdateFilteredWorktrees()
    {
        FilteredWorktrees.Clear();

        string filter = SearchFilter?.Trim() ?? string.Empty;

        foreach (WorktreeItemViewModel worktree in Worktrees)
        {
            if (string.IsNullOrEmpty(filter) ||
                worktree.DisplayPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                worktree.BranchName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                worktree.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredWorktrees.Add(worktree);
            }
        }

        OnPropertyChanged(nameof(ShowNoWorktreesMessage));
        OnPropertyChanged(nameof(ShowWorktreeList));
    }

    /// <summary>
    /// Computes the shortest unique path for each worktree by removing common prefixes.
    /// </summary>
    /// <param name="paths">List of all worktree paths.</param>
    /// <returns>Dictionary mapping full path to shortest unique display path.</returns>
    private Dictionary<string, string> ComputeShortestUniquePaths(List<string> paths)
    {
        var result = new Dictionary<string, string>();

        if (paths.Count == 0)
        {
            return result;
        }

        if (paths.Count == 1)
        {
            // Single worktree - just show the folder name
            string path = paths[0];
            string displayPath = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            result[path] = string.IsNullOrEmpty(displayPath) ? path : displayPath;
            return result;
        }

        // Normalize all paths and split into segments
        var pathSegments = new List<(string OriginalPath, string NormalizedPath, string[] Segments)>();
        foreach (string path in paths)
        {
            string normalized = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string[] segments = normalized.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            pathSegments.Add((path, normalized, segments));
        }

        // Find the common prefix length
        int commonPrefixLength = 0;
        if (pathSegments.Count > 1)
        {
            int minSegments = pathSegments.Min(p => p.Segments.Length);
            for (int i = 0; i < minSegments; i++)
            {
                string segment = pathSegments[0].Segments[i];
                if (pathSegments.All(p => string.Equals(p.Segments[i], segment, StringComparison.OrdinalIgnoreCase)))
                {
                    commonPrefixLength = i + 1;
                }
                else
                {
                    break;
                }
            }
        }

        // Generate display paths by removing common prefix
        foreach (var (originalPath, normalizedPath, segments) in pathSegments)
        {
            string displayPath;
            
            if (commonPrefixLength >= segments.Length)
            {
                // All segments are common - just show the last segment
                displayPath = segments[^1];
            }
            else if (commonPrefixLength == segments.Length - 1)
            {
                // Only the last segment is unique - show just that
                displayPath = segments[^1];
            }
            else
            {
                // Show segments after the common prefix
                string[] uniqueSegments = segments[commonPrefixLength..];
                displayPath = string.Join(Path.DirectorySeparatorChar.ToString(), uniqueSegments);
            }

            result[originalPath] = displayPath;
        }

        return result;
    }

    /// <summary>
    /// Maps a Worktree model to a WorktreeItemViewModel.
    /// </summary>
    /// <param name="worktree">The worktree model to map.</param>
    /// <param name="displayPaths">Dictionary of precomputed display paths.</param>
    /// <returns>The mapped view model.</returns>
    private WorktreeItemViewModel MapToViewModel(Worktree worktree, Dictionary<string, string> displayPaths)
    {
        // Use precomputed display path if available, otherwise fall back to folder name
        string displayPath;
        if (displayPaths.TryGetValue(worktree.Path, out string? precomputedPath))
        {
            displayPath = precomputedPath;
        }
        else
        {
            displayPath = Path.GetFileName(worktree.Path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

            if (string.IsNullOrEmpty(displayPath))
            {
                displayPath = worktree.Path;
            }
        }

        // Abbreviate the commit SHA for display (first 7 characters)
        string shortCommit = worktree.HeadCommit.Length > 7
            ? worktree.HeadCommit[..7]
            : worktree.HeadCommit;

        return new WorktreeItemViewModel
        {
            Path = worktree.Path,
            DisplayPath = displayPath,
            BranchName = worktree.Branch ?? "detached",
            HeadCommit = shortCommit,
            IsMainWorktree = worktree.IsMainWorktree,
            IsLocked = worktree.IsLocked,
            IsPrunable = worktree.IsPrunable,
            IsCurrentWorktree = IsCurrentWorktree(worktree.Path)
        };
    }

    /// <summary>
    /// Sets a property value and raises PropertyChanged if the value changed.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="field">Reference to the backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="propertyName">The name of the property (auto-filled by compiler).</param>
    /// <returns>True if the value changed, false otherwise.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    /// <param name="propertyName">The name of the property that changed.</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Shows an error notification to the user via the notification service.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    private async Task ShowErrorNotificationAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_notificationService != null)
        {
            await _notificationService.ShowErrorAsync(message, null, cancellationToken);
        }
    }

    /// <summary>
    /// Shows an error notification with details (e.g., git stderr) to the user.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    /// <param name="details">Additional details such as git stderr output.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    private async Task ShowErrorNotificationAsync(string message, string? details,
        CancellationToken cancellationToken = default)
    {
        if (_notificationService != null)
        {
            await _notificationService.ShowErrorAsync(message, details, cancellationToken);
        }
    }

    /// <summary>
    /// Checks if the given path is the currently open worktree/solution.
    /// </summary>
    private bool IsCurrentWorktree(string worktreePath)
    {
        if (string.IsNullOrEmpty(RepositoryPath) || string.IsNullOrEmpty(worktreePath))
        {
            return false;
        }

        // Normalize paths for comparison
        string normalizedRepo = Path.GetFullPath(RepositoryPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedWorktree = Path.GetFullPath(worktreePath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedRepo, normalizedWorktree, StringComparison.OrdinalIgnoreCase);
    }
}
