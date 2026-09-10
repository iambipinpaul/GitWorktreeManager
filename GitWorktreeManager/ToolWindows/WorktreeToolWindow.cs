using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using GitWorktreeManager.ViewModels;
using GitWorktreeManager.Services;

namespace GitWorktreeManager.ToolWindows;

/// <summary>
/// Tool window for managing Git worktrees.
/// Provides a dockable UI for listing, adding, removing, and opening worktrees.
/// Automatically refreshes when the solution changes.
/// Checks for Git installation on load and displays appropriate error messages.
/// </summary>
[VisualStudioContribution]
public class WorktreeToolWindow : ToolWindow
{
    private readonly WorktreeToolWindowControl _control;
    private readonly WorktreeViewModel _viewModel;
    private readonly SolutionService? _solutionService;
    private readonly ILoggerService _loggerService;
    private readonly IGitService? _gitService;
    private readonly INotificationService? _notificationService;
    private bool _isDisposed;
    private bool _gitInstalled = true;
    private bool _initializationFailed;

    /// <summary>
    /// Initializes a new instance of the WorktreeToolWindow.
    /// </summary>
    /// <param name="extensibility">The extensibility object for VS services.</param>
    public WorktreeToolWindow(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
        Title = "Worktree Manager";

        // Create logger first - this should never fail
        _loggerService = new ActivityLogService();

        try
        {
            // Create services with proper error handling
            _gitService = new GitService(_loggerService);
            _solutionService = new SolutionService(extensibility, _loggerService);
            _notificationService = new NotificationService(extensibility, _loggerService);

            // Create ViewModel with notification service
            _viewModel = new WorktreeViewModel(_gitService, extensibility, _notificationService);

            // Subscribe to solution change events
            _solutionService.SolutionChanged += OnSolutionChanged;
        }
        catch (Exception ex)
        {
            _loggerService.LogException(ex, "Failed to initialize WorktreeToolWindow services");
            _initializationFailed = true;

            // Create a minimal ViewModel that shows the error state
            _gitService ??= new GitService(_loggerService);
            _viewModel = new WorktreeViewModel(_gitService, extensibility, null);
            _viewModel.SetGitNotInstalledError();
        }

        // Always create the control - this must never be null
        _control = new WorktreeToolWindowControl(_viewModel);

        // Initialize solution service asynchronously (fire and forget with error handling)
        if (!_initializationFailed)
        {
            _ = InitializeSolutionServiceAsync();
        }
    }

    /// <summary>
    /// Initializes the solution service and performs initial refresh.
    /// Also checks if Git is installed and shows an error if not found.
    /// </summary>
    private async Task InitializeSolutionServiceAsync()
    {
        try
        {
            _loggerService.LogInformation("Initializing WorktreeToolWindow solution monitoring");

            // Check if Git is installed first
            await CheckGitInstallationAsync();

            if (!_gitInstalled)
            {
                // Git not installed - don't proceed with solution monitoring
                return;
            }

            if (_solutionService == null || _gitService == null)
            {
                _loggerService.LogError("Solution or Git service unavailable - skipping solution monitoring");
                return;
            }

            await _solutionService.InitializeAsync();

            // Set initial repository path and refresh
            string? solutionDirectory = _solutionService.CurrentSolutionDirectory;
            if (!string.IsNullOrEmpty(solutionDirectory))
            {
                await UpdateRepositoryPathAsync(solutionDirectory);
            }

            _loggerService.LogInformation("WorktreeToolWindow initialization complete");
        }
        catch (Exception ex)
        {
            _loggerService.LogException(ex, "Failed to initialize solution service");
        }
    }

    /// <summary>
    /// Checks if Git is installed and available in the system PATH.
    /// Shows an error notification if Git is not found.
    /// </summary>
    private async Task CheckGitInstallationAsync()
    {
        try
        {
            _loggerService.LogInformation("Checking Git installation");

            if (_gitService == null)
            {
                _loggerService.LogError("Git service unavailable");
                _gitInstalled = false;
                _viewModel.SetGitNotInstalledError();
                return;
            }

            _gitInstalled = await _gitService.IsGitInstalledAsync();

            if (!_gitInstalled)
            {
                _loggerService.LogError("Git is not installed or not in PATH");

                // Update ViewModel to show error state
                _viewModel.SetGitNotInstalledError();

                // Show notification to user
                if (_notificationService != null)
                {
                    await _notificationService.ShowErrorAsync(
                        "Git is not installed or not found in PATH",
                        "Please install Git and ensure it is added to your system PATH. " +
                        "You can download Git from https://git-scm.com/downloads");
                }
            }
            else
            {
                _loggerService.LogInformation("Git installation verified");
            }
        }
        catch (Exception ex)
        {
            _loggerService.LogException(ex, "Error checking Git installation");
            _gitInstalled = false;
            _viewModel.SetGitNotInstalledError();
        }
    }

    /// <summary>
    /// Handles solution change events with debouncing already applied by SolutionService.
    /// </summary>
    private void OnSolutionChanged(object? sender, SolutionChangedEventArgs e)
    {
        // Fire and forget with proper exception handling
        _ = HandleSolutionChangedAsync(e);
    }

    /// <summary>
    /// Async handler for solution change events.
    /// </summary>
    private async Task HandleSolutionChangedAsync(SolutionChangedEventArgs e)
    {
        try
        {
            _loggerService.LogInformation($"Solution changed event received: {e.SolutionDirectory ?? "(closed)"}");

            if (e.IsSolutionOpen)
            {
                await UpdateRepositoryPathAsync(e.SolutionDirectory!);
            }
            else
            {
                // Solution closed - clear the worktree list
                _viewModel.RepositoryPath = null;
                await _viewModel.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            _loggerService.LogException(ex, "Error handling solution change");
        }
    }

    /// <summary>
    /// Updates the repository path and refreshes the worktree list.
    /// </summary>
    /// <param name="solutionDirectory">The solution directory to check for a Git repository.</param>
    private async Task UpdateRepositoryPathAsync(string solutionDirectory)
    {
        try
        {
            if (_gitService == null)
            {
                _loggerService.LogError("Git service unavailable - cannot resolve repository path");
                return;
            }

            // Find the Git repository root from the solution directory
            string? repositoryRoot = await _gitService.GetRepositoryRootAsync(solutionDirectory);

            if (!string.IsNullOrEmpty(repositoryRoot))
            {
                _loggerService.LogInformation($"Found Git repository at: {repositoryRoot}");
                _viewModel.RepositoryPath = repositoryRoot;
                await _viewModel.RefreshAsync();
            }
            else
            {
                _loggerService.LogInformation($"No Git repository found in: {solutionDirectory}");
                _viewModel.RepositoryPath = null;
                await _viewModel.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            _loggerService.LogException(ex, $"Error updating repository path for: {solutionDirectory}");
            _viewModel.RepositoryPath = null;
            await _viewModel.RefreshAsync();
        }
    }

    /// <summary>
    /// Configuration for the tool window placement and behavior.
    /// Docks to the right side panel by default (like GitHub Copilot Chat, Properties).
    /// </summary>
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        // Dock to right side panel by default (like GitHub Copilot, Properties)
        Placement = ToolWindowPlacement.Floating, DockDirection = Dock.Right, AllowAutoCreation = true
    };

    /// <summary>
    /// Gets the content control for the tool window.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Remote UI control for the tool window.</returns>
    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        // Defensive null check - _control should never be null after constructor completes
        if (_control == null)
        {
            _loggerService.LogError("GetContentAsync called but _control is null - this should never happen");
            throw new InvalidOperationException("Tool window control was not properly initialized");
        }

        return Task.FromResult<IRemoteUserControl>(_control);
    }

    /// <summary>
    /// Disposes of the tool window resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (disposing)
        {
            _loggerService.LogInformation("Disposing WorktreeToolWindow");

            if (_solutionService != null)
            {
                _solutionService.SolutionChanged -= OnSolutionChanged;
                _solutionService.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
