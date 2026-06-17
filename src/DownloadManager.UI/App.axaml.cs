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
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}