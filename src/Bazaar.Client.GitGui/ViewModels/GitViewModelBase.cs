using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Bazaar.Client.GitGui.ViewModels;

/// <summary>Plain <see cref="INotifyPropertyChanged"/> support for the Git view models. No UI types.</summary>
public abstract class GitViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

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

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
