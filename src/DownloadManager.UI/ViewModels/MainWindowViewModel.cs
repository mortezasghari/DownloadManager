using DownloadManager.Core.Abstractions;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Phase 0 view model. Static, one-way bound values are enough to prove the
/// compiled-binding + Core-interface path works under AOT. Real download state
/// (with INotifyPropertyChanged) arrives in later phases.
/// </summary>
public sealed class MainWindowViewModel
{
    public MainWindowViewModel(IAppInfoService appInfo)
    {
        Title = "Download Manager";
        Greeting = "Phase 0 — Native AOT Avalonia shell is alive.";
        RuntimeInfo = appInfo.Describe();
    }

    public string Title { get; }

    public string Greeting { get; }

    public string RuntimeInfo { get; }
}