using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Minimal hand-rolled <see cref="INotifyPropertyChanged"/> base (pure BCL — no MVVM toolkit, AOT/trim
/// safe). <see cref="SetProperty{T}"/> assigns a backing field and raises a change notification only on
/// an actual change.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

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

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}