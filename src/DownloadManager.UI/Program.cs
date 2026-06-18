using Avalonia;
using DownloadManager.Core;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Engine;
using DownloadManager.Core.Http;
using DownloadManager.Core.Recovery;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Metadata;
using DownloadManager.Persistence.Progress;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.UI;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless CI self-test: exercises the native preallocation path without a display (every RID).
        if (args.Contains("--smoke"))
        {
            return SelfTest.Run();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
        services.AddLogging();

        services.AddSingleton<IAppInfoService, AppInfoService>();
        services.AddTransient<MainWindowViewModel>();

        // Engine composition root. Options are plain singletons for now; an Options/IConfiguration
        // binding pass comes with the real UI (Phase 5).
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new EngineOptions());
        services.AddSingleton(new HttpOptions());
        services.AddSingleton(new ProgressLogOptions());

        services.AddSingleton<SharedHttpClient>();
        services.AddSingleton(sp => sp.GetRequiredService<SharedHttpClient>().Client);
        services.AddSingleton<RangeProber>();
        services.AddSingleton<ITargetFileFactory, TargetFileFactory>();
        services.AddSingleton<IProgressLogStore, BinaryProgressLogStore>();
        services.AddSingleton<IMetadataStore, JsonMetadataStore>();
        services.AddSingleton<IDownloadEngine, DownloadEngine>();
        services.AddSingleton<RecoveryService>();

        var provider = services.BuildServiceProvider();

        // Resolve the engine graph once so it is rooted in the Native AOT image (and DI wiring is
        // validated at startup). This keeps the native preallocation P/Invokes in the trimmed graph.
        _ = provider.GetRequiredService<IDownloadEngine>();
        _ = provider.GetRequiredService<RecoveryService>();

        return provider;
    }
}