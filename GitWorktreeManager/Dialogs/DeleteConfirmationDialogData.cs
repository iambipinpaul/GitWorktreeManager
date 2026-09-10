using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace GitWorktreeManager.Dialogs;

/// <summary>
/// Data context for the Delete Confirmation dialog.
/// Uses a checkbox confirmation pattern.
/// </summary>
[DataContract]
public class DeleteConfirmationDialogData : INotifyPropertyChanged
{
    private bool _hasConfirmedRisk;
    private bool _isValid;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The name of the worktree being deleted.
    /// </summary>
    [DataMember]
    public required string WorktreeName { get; init; }

    /// <summary>
    /// The path of the worktree being deleted.
    /// </summary>
    [DataMember]
    public required string WorktreePath { get; init; }

    /// <summary>
    /// The risk confirmation text. Differs for locked worktrees (override the lock)
    /// versus dirty worktrees (uncommitted changes will be lost).
    /// </summary>
    [DataMember]
    public string ConfirmationText { get; set; } =
        "I understand that uncommitted changes will be PERMANENTLY lost.";

    /// <summary>
    /// Whether the user has checked the confirmation box.
    /// </summary>
    [DataMember]
    public bool HasConfirmedRisk
    {
        get => _hasConfirmedRisk;
        set
        {
            if (SetProperty(ref _hasConfirmedRisk, value))
            {
                Validate();
            }
        }
    }

    /// <summary>
    /// Whether the input is valid (checkbox is checked).
    /// </summary>
    [DataMember]
    public bool IsValid
    {
        get => _isValid;
        private set => SetProperty(ref _isValid, value);
    }

    /// <summary>
    /// Confirm command.
    /// </summary>
    [DataMember]
    public IAsyncCommand? OkCommand { get; set; }

    /// <summary>
    /// Cancel command.
    /// </summary>
    [DataMember]
    public IAsyncCommand? CancelCommand { get; set; }

    public void Validate()
    {
        // Valid only if the risk is accepted
        IsValid = HasConfirmedRisk;
    }

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

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
