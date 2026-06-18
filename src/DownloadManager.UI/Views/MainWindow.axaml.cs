using Avalonia.Controls;
using Avalonia.Threading;
using DownloadManager.UI.ViewModels;

namespace DownloadManager.UI.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;

    // Public parameterless constructor calling InitializeComponent (spec §10).
    public MainWindow()
    {
        InitializeComponent();

        // Throttled UI-thread refresh: poll the lock-free handle snapshots ~3x/sec. No engine work runs
        // here, and nothing blocks the UI thread (ADR-0013 threading model).
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(333) };
        _refreshTimer.Tick += (_, _) => (DataContext as MainWindowViewModel)?.Tick();

        Opened += (_, _) => _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();
    }
}