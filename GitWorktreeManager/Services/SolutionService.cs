namespace GitWorktreeManager.Services;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;

/// <summary>
/// Service for monitoring Visual Studio solution events.
/// Implements debouncing to prevent excessive Git command execution during rapid solution changes.
/// </summary>
public class SolutionService : ISolutionService
{
    private const int DebounceDelayMs = 500;
    private static readonly TimeSpan PollDueTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(5);

    private readonly VisualStudioExtensibility _extensibility;
    private readonly ILoggerService? _logger;
    private readonly object _debounceLock = new();

    private CancellationTokenSource? _debounceCts;
    private Timer? _pollingTimer;
    private string? _currentSolutionDirectory;
    private string? _pendingSolutionDirectory;
    private bool _isDisposed;
    private int _checkInFlight;

    /// <inheritdoc />
    public event EventHandler<SolutionChangedEventArgs>? SolutionChanged;

    /// <inheritdoc />
    public string? CurrentSolutionDirectory => _currentSolutionDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SolutionService"/> class.
    /// </summary>
    /// <param name="extensibility">The VS extensibility object for accessing solution services.</param>
    /// <param name="logger">Optional logger service for recording operations.</param>
    public SolutionService(VisualStudioExtensibility extensibility, ILoggerService? logger = null)
    {
        _extensibility = extensibility ?? throw new ArgumentNullException(nameof(extensibility));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Initializing SolutionService");

        try
        {
            // Get the initial solution state
            await RefreshCurrentSolutionAsync(cancellationToken);

            // Start polling for solution changes
            // Note: VisualStudio.Extensibility SDK doesn't have direct solution events in the out-of-process model,
            // so we use a polling approach with debouncing
            StartSolutionPolling();

            _logger?.LogInformation(
                $"SolutionService initialized. Current solution: {_currentSolutionDirectory ?? "(none)"}");
        }
        catch (Exception ex)
        {
            _logger?.LogException(ex, "Failed to initialize SolutionService");
            throw;
        }
    }

    /// <summary>
    /// Starts polling for solution changes.
    /// Polls every 5s (after a 2s initial delay) — a slower cadence than before
    /// to avoid hammering the out-of-proc workspace query. Overlapping ticks are
    /// skipped via an interlocked flag.
    /// </summary>
    private void StartSolutionPolling()
    {
        // Use a timer to periodically check for solution changes
        // This is a workaround for the lack of direct solution events in the out-of-process model
        _pollingTimer = new Timer(
            OnPollingTimerCallback,
            null,
            PollDueTime,
            PollPeriod);
    }

    /// <summary>
    /// Timer callback for polling solution changes.
    /// </summary>
    private void OnPollingTimerCallback(object? state)
    {
        // Fire and forget with proper exception handling inside CheckForSolutionChangeAsync
        _ = CheckForSolutionChangeAsync();
    }

    /// <summary>
    /// Checks if the solution has changed and raises the event if needed.
    /// Skips overlapping ticks so slow workspace queries never pile up.
    /// </summary>
    private async Task CheckForSolutionChangeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _checkInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            string? previousDirectory = _currentSolutionDirectory;
            await RefreshCurrentSolutionAsync(CancellationToken.None);

            if (_isDisposed)
            {
                return;
            }

            if (!string.Equals(previousDirectory, _currentSolutionDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation(
                    $"Solution changed from '{previousDirectory ?? "(none)"}' to '{_currentSolutionDirectory ?? "(none)"}'");
                RaiseSolutionChangedDebounced(_currentSolutionDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogException(ex, "Error checking for solution change");
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    /// <summary>
    /// Refreshes the current solution directory from Visual Studio.
    /// </summary>
    private async Task RefreshCurrentSolutionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Query the workspace for the current solution
            WorkspacesExtensibility workspaceQuery = _extensibility.Workspaces();
            IQueryResults<ISolutionSnapshot> result = await workspaceQuery.QuerySolutionAsync(
                solution => solution.With(s => s.Path),
                cancellationToken);

            string? solutionPath = null;

            // IQueryResults implements IEnumerable, not IAsyncEnumerable
            foreach (ISolutionSnapshot solution in result)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                solutionPath = solution.Path;
                break; // We only need the first (and only) solution
            }

            if (!string.IsNullOrEmpty(solutionPath))
            {
                _currentSolutionDirectory = Path.GetDirectoryName(solutionPath);
            }
            else
            {
                _currentSolutionDirectory = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Could not query solution: {ex.Message}");
            _currentSolutionDirectory = null;
        }
    }

    /// <summary>
    /// Raises the SolutionChanged event with debouncing to prevent rapid-fire events.
    /// </summary>
    /// <param name="solutionDirectory">The new solution directory.</param>
    private void RaiseSolutionChangedDebounced(string? solutionDirectory)
    {
        CancellationTokenSource newCts = new();
        CancellationTokenSource? oldCts;
        lock (_debounceLock)
        {
            oldCts = _debounceCts;
            _debounceCts = newCts;
            _pendingSolutionDirectory = solutionDirectory;
        }

        // Cancel and dispose the previous debounce outside the lock.
        if (oldCts != null)
        {
            try
            {
                oldCts.Cancel();
            }
            catch
            {
                // Ignore cancellation errors from a stale debounce.
            }
            finally
            {
                oldCts.Dispose();
            }
        }

        // Schedule the debounced event
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelayMs, newCts.Token);

                string? directoryToRaise;
                lock (_debounceLock)
                {
                    directoryToRaise = _pendingSolutionDirectory;
                }

                if (_isDisposed)
                {
                    return;
                }

                _logger?.LogInformation(
                    $"Raising debounced SolutionChanged event for: {directoryToRaise ?? "(none)"}");
                SolutionChanged?.Invoke(this, new SolutionChangedEventArgs(directoryToRaise));
            }
            catch (OperationCanceledException)
            {
                // Debounce was cancelled by a newer event, which is expected
                _logger?.LogInformation("Solution change event debounced (cancelled by newer event)");
            }
            catch (Exception ex)
            {
                _logger?.LogException(ex, "Error raising SolutionChanged event");
            }
        });
    }

    /// <summary>
    /// Manually triggers a solution change check and event.
    /// Useful for forcing a refresh when the solution state may have changed.
    /// </summary>
    public void TriggerSolutionCheck() => _ = CheckForSolutionChangeAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _logger?.LogInformation("Disposing SolutionService");

        _pollingTimer?.Dispose();
        _pollingTimer = null;

        CancellationTokenSource? debounceCts;
        lock (_debounceLock)
        {
            debounceCts = _debounceCts;
            _debounceCts = null;
        }

        if (debounceCts != null)
        {
            try
            {
                debounceCts.Cancel();
            }
            catch
            {
                // Ignore errors during dispose.
            }
            finally
            {
                debounceCts.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }
}
