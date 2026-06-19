using Avalonia;
using DownloadManager.Core;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Engine;
using DownloadManager.Core.Http;
using DownloadManager.Core.Recovery;
using DownloadManager.Core.Routing;
using DownloadManager.Core.Scheduler;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Metadata;
using DownloadManager.Persistence.Progress;
using DownloadManager.UI.Services;
using DownloadManager.UI.ViewModels;
using DownloadManager.UI.Views;
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

        // Headless perf harness (local measurement; not part of the CI gate). See docs/perf.
        if (args.Contains("--bench"))
        {
            return Bench.RunAsync().GetAwaiter().GetResult();
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
        services.AddSingleton<IFilePicker, AvaloniaFilePicker>();
        services.AddSingleton<ICredentialPrompt, AvaloniaCredentialPrompt>();
        services.AddTransient<MainWindowViewModel>();

        // Engine composition root. Tunables come from the user-editable settings.json, loaded once via
        // the source-gen context (ADR-0016) — never the reflection binder, which throws under AOT. The
        // load is a factory so its clamp/first-run warnings flow through the DI logger; the option
        // records and routing map are projected out of the single resolved result.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
            SettingsStore.LoadOrCreate(sp.GetRequiredService<ILoggerFactory>().CreateLogger("SettingsStore")));
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedSettings>().Engine);
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedSettings>().Scheduler);
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedSettings>().Retry);
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedSettings>().Defaults);
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedSettings>().Routing);
        services.AddSingleton<IFileRouter, FileRouter>();
        services.AddSingleton(new HttpOptions());
        services.AddSingleton(new ProgressLogOptions());

        services.AddSingleton<SharedHttpClient>();
        services.AddSingleton(sp => sp.GetRequiredService<SharedHttpClient>().Client);
        services.AddSingleton<RangeProber>();
        services.AddSingleton<ITargetFileFactory, TargetFileFactory>();
        services.AddSingleton<IProgressLogStore, BinaryProgressLogStore>();
        services.AddSingleton<IMetadataStore, JsonMetadataStore>();
        services.AddSingleton<ChecksumVerifier>();
        services.AddSingleton<IDownloadEngine, DownloadEngine>();
        services.AddSingleton<RecoveryService>();
        services.AddSingleton<RetryPolicy>();
        services.AddSingleton<IDownloadScheduler, DownloadScheduler>();

        var provider = services.BuildServiceProvider();

        // Resolve the lifecycle graph once so it is rooted in the Native AOT image (and DI wiring is
        // validated at startup). This keeps the engine, scheduler, and native P/Invokes in the trimmed graph.
        _ = provider.GetRequiredService<IDownloadScheduler>();
        _ = provider.GetRequiredService<RecoveryService>();

        return provider;
    }
}