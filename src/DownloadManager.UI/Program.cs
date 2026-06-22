using Avalonia;
using DownloadManager.Core;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Engine;
using DownloadManager.Core.Http;
using DownloadManager.Core.Recovery;
using DownloadManager.Core.Routing;
using DownloadManager.Core.Scheduler;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Persistence.History;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Lifecycle;
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
        // Calculated versioning (ADR-0025): `--next-version <current-tag> <label>` prints the next version
        // for CI to inject. Pure arithmetic on the args (the running binary's own version is irrelevant), so
        // there is no chicken-and-egg with version injection. Reuses the tested VersionBump logic.
        if (args is ["--next-version", var current, var label, ..])
        {
            Console.WriteLine($"NEXT_VERSION={Versioning.VersionBump.Next(current, label)}");
            return 0;
        }

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
        services.AddSingleton<IClipboardTextSource, AvaloniaClipboardTextSource>();
        services.AddSingleton<IImportDialog, AvaloniaImportDialog>();

        // Read-only download history (Phase 9 / ADR-0019): source-gen JSON at {ApplicationData}/
        // DownloadManager/history.json (same OS-config dir as settings.json), atomically written. The
        // launcher shells out per-platform for the open / reveal actions.
        services.AddSingleton<IFileLauncher, ProcessFileLauncher>();

        // Notify-only update check (ADR-0025): compares the running version to the latest GitHub release.
        // Reuses the shared HttpClient; never downloads/installs anything (the secured updater is parked).
        services.AddSingleton<IUpdateChecker>(sp => new GithubUpdateChecker(
            sp.GetRequiredService<SharedHttpClient>().Client,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GithubUpdateChecker>()));
        services.AddSingleton<IHistoryStore>(sp => new JsonHistoryStore(
            Path.Combine(SettingsStore.DefaultDirectory(), "history.json"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonHistoryStore>()));

        // Lifecycle-event log = source of truth (Phase: queue rebuild / ADR-0021). The queue (channel) and
        // history.json are projections; recovery replays the log on startup to rebuild both.
        services.AddSingleton<ILifecycleLog>(sp => new JsonLifecycleLog(
            Path.Combine(SettingsStore.DefaultDirectory(), "queue.log"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonLifecycleLog>()));
        services.AddSingleton<QueueRecoveryService>();

        // Inline queue-settings panel (Phase 8). It mutates the same shared option singletons the engine
        // and scheduler read, and persists through the Phase-7 store at the default settings.json path.
        services.AddSingleton(sp => new QueueSettingsViewModel(
            sp.GetRequiredService<IDownloadScheduler>(),
            sp.GetRequiredService<EngineOptions>(),
            sp.GetRequiredService<RetryOptions>(),
            sp.GetRequiredService<DownloadDefaults>(),
            sp.GetRequiredService<ScheduleOptions>(),
            SettingsStore.DefaultPath(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("QueueSettings"),
            updateChecker: sp.GetRequiredService<IUpdateChecker>(),
            launcher: sp.GetRequiredService<IFileLauncher>()));
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
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedSettings>().Schedule);
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
        _ = provider.GetRequiredService<QueueRecoveryService>();

        return provider;
    }
}