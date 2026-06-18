using System.Windows.Input;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// A pure-BCL <see cref="ICommand"/> over an async action (no MVVM toolkit). It is disabled while
/// running (so a slow control op can't be re-invoked) and never blocks the UI thread; faults are
/// swallowed because failure is reflected in the download's observable state, not thrown at the click.
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Func<Task> _execute = execute;
    private readonly Func<bool>? _canExecute = canExecute;
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true); // stay on the UI sync-context for VM/collection updates
        }
        catch
        {
            // The outcome surfaces through the download's status/reason; a command click never throws.
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}