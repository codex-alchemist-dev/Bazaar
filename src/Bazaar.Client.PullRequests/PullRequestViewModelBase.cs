using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Bazaar.Client.PullRequests;

/// <summary>Shared change notification for the PR view models. Computed properties are re-announced by <see cref="Touch"/>.</summary>
public abstract class PullRequestViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-announces every property. Used after a state change that touches several computed values at once.</summary>
    protected void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>Sets a backing field and raises the change when the value differs.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
