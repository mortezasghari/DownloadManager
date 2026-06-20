using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DownloadManager.UI.ViewModels;
using DownloadManager.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DownloadManager.UI;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services) => _services = services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Rebuild the active queue from the lifecycle log (ADR-0021): re-enqueue recovered downloads.
            // Fire-and-forget on the UI context; failures surface through each download's own state.
            _ = viewModel.RestoreRecoveredAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}