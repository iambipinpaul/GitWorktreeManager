using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.UI;

namespace GitWorktreeManager.Dialogs;

/// <summary>
/// Manages the Delete Confirmation dialog.
/// </summary>
public class DeleteConfirmationDialog
{
    private readonly VisualStudioExtensibility _extensibility;

    public DeleteConfirmationDialog(VisualStudioExtensibility extensibility)
    {
        _extensibility = extensibility ?? throw new ArgumentNullException(nameof(extensibility));
    }

    /// <summary>
    /// Shows the Delete Confirmation dialog.
    /// Returns true if the user confirmed deletion, false otherwise.
    /// </summary>
    public async Task<bool> ShowAsync(
        string worktreeName,
        string worktreePath,
        bool isLocked = false,
        CancellationToken cancellationToken = default)
    {
        var dialogData = new DeleteConfirmationDialogData
        {
            WorktreeName = worktreeName,
            WorktreePath = worktreePath,
            ConfirmationText = isLocked
                ? "I understand this will override the lock and the worktree will be PERMANENTLY deleted."
                : "I understand that uncommitted changes will be PERMANENTLY lost."
        };

        // Create completion source for result
        var resultTcs = new TaskCompletionSource<bool>();
        var dialogCts = new CancellationTokenSource();

        dialogData.OkCommand = new AsyncCommand(async (_, ct) =>
        {
            if (dialogData.IsValid)
            {
                resultTcs.TrySetResult(true);
                await dialogCts.CancelAsync();
            }
        });

        dialogData.CancelCommand = new AsyncCommand(async (_, ct) =>
        {
            resultTcs.TrySetResult(false);
            await dialogCts.CancelAsync();
        });

        dialogData.CopyNameCommand = new AsyncCommand((_, _) =>
        {
            CopyToClipboard(worktreeName);
            return Task.CompletedTask;
        });

        dialogData.CopyPathCommand = new AsyncCommand((_, _) =>
        {
            CopyToClipboard(worktreePath);
            return Task.CompletedTask;
        });

        // Trigger initial validation
        dialogData.Validate();

        var dialogControl = new DeleteConfirmationDialogControl(dialogData);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dialogCts.Token);

        try
        {
            await _extensibility.Shell().ShowDialogAsync(
                dialogControl,
                "Force Remove Worktree",
                new Microsoft.VisualStudio.RpcContracts.Notifications.DialogOption(
                    Microsoft.VisualStudio.RpcContracts.Notifications.DialogButton.None,
                    Microsoft.VisualStudio.RpcContracts.Notifications.DialogResult.None),
                linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            dialogCts.Dispose();
        }

        if (resultTcs.Task.IsCompleted)
        {
            return await resultTcs.Task;
        }

        return false;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var thread = new Thread(() => { System.Windows.Clipboard.SetDataObject(text, true); });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch
        {
            // Best effort — clipboard may be unavailable.
        }
    }
}
