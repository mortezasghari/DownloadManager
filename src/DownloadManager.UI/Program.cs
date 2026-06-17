using Avalonia;
using DownloadManager.Core;
using DownloadManager.Core.Abstractions;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DownloadManager.UI;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var services = ConfigureServices();
        return AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    // Explicit, reflection-free DI registration (spec §1: no assembly scanning).
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppInfoService, AppInfoService>();
        services.AddTransient<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
