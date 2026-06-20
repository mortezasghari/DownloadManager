using System.Text.Json;
using DownloadManager.Core.Routing;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Core.Configuration;

/// <summary>
/// Loads the user-editable <c>settings.json</c> once at startup (ADR-0016) and turns it into the
/// engine-ready <see cref="ResolvedSettings"/>. Behaviour is failure-proof by contract:
/// <list type="bullet">
/// <item><b>Missing file</b> → defaults are applied <i>and</i> a documented default file is written
/// (first-run discoverability).</item>
/// <item><b>Malformed JSON</b> → defaults are applied, a warning is logged, and the user's file is
/// <i>left untouched</i> (never destroy a broken hand-edit).</item>
/// <item><b>Out-of-range values</b> → clamped to the nearest legal value with a warning, reusing the
/// engine's own invariants (segment count [1..16], etc.).</item>
/// </list>
/// Binding is done exclusively through the <see cref="AppSettingsJsonContext"/> source generator —
/// the reflection-based configuration binder throws under Native AOT (spec §1).
/// </summary>
public static partial class SettingsStore
{
    // Legal ranges — kept here so an out-of-range edit can never reach an engine invariant.
    private const int MinConcurrent = 1, MaxConcurrent = 64;
    private const int MinQueueCapacity = 1, MaxQueueCapacity = 65536;
    private const int MinSegments = 1, MaxSegments = 16;
    private const int MinCopyBuffer = 4 * 1024, MaxCopyBuffer = 8 * 1024 * 1024;
    private const long MinCheckpoint = 64 * 1024, MaxCheckpoint = 1L * 1024 * 1024 * 1024;
    private const int MinAttemptTimeout = 1, MaxAttemptTimeout = 3600;
    private const int MinAttempts = 1, MaxAttempts = 100;
    private const double MinDelaySeconds = 0, MaxDelaySeconds = 3600;
    private const double MinJitter = 0, MaxJitter = 1;

    /// <summary>The per-user config directory: <c>{ApplicationData}/DownloadManager</c> (honors <c>$XDG_CONFIG_HOME</c>).</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DownloadManager");

    /// <summary>The default <c>settings.json</c> path under <see cref="DefaultDirectory"/>.</summary>
    public static string DefaultPath() => Path.Combine(DefaultDirectory(), "settings.json");

    /// <summary>Load from <see cref="DefaultPath"/> (first-run writes defaults). Routing resolves against the user profile.</summary>
    public static ResolvedSettings LoadOrCreate(ILogger logger) => LoadOrCreate(DefaultPath(), logger);

    /// <summary>
    /// Load from <paramref name="settingsPath"/>. <paramref name="userProfile"/> (default: the OS user
    /// profile) is the base directory that relative routing folders resolve against — injectable so tests
    /// land files under a temp home rather than the real one.
    /// </summary>
    public static ResolvedSettings LoadOrCreate(string settingsPath, ILogger logger, string? userProfile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingsPath);
        ArgumentNullException.ThrowIfNull(logger);
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AppSettings settings;
        if (!File.Exists(settingsPath))
        {
            // First run: defaults + write a discoverable, documented file.
            settings = AppSettings.CreateDefault();
            TryWriteDefault(settingsPath, settings, logger);
        }
        else
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)
                    ?? AppSettings.CreateDefault();
            }
            catch (JsonException ex)
            {
                // Broken hand-edit: fall back to defaults, but DO NOT overwrite their file.
                LogMalformed(logger, settingsPath, ex.Message);
                settings = AppSettings.CreateDefault();
            }
            catch (IOException ex)
            {
                LogMalformed(logger, settingsPath, ex.Message);
                settings = AppSettings.CreateDefault();
            }
        }

        return Validate(settings, userProfile, logger);
    }

    /// <summary>
    /// Read the persisted settings into the raw <see cref="AppSettings"/> shape <b>without</b> writing,
    /// clamping, or projecting — for the settings UI to load current values, overlay edits, and hand back
    /// to <see cref="Save"/>. Missing or malformed files yield defaults (no write).
    /// </summary>
    public static AppSettings ReadRaw(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingsPath);
        if (!File.Exists(settingsPath))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)
                ?? AppSettings.CreateDefault();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return AppSettings.CreateDefault();
        }
    }

    /// <summary>
    /// Persist edited settings (ADR-0018). The whole set is clamped through the same path config-load
    /// uses, so the UI can never persist a value the loader would reject; the <b>clamped</b> form is what
    /// is written and what the returned <see cref="ResolvedSettings"/> reflects. Routing and file-only
    /// advanced knobs in <paramref name="settings"/> pass through untouched.
    /// </summary>
    public static ResolvedSettings Save(string settingsPath, AppSettings settings, ILogger logger, string? userProfile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingsPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var clamped = Clamp(settings, logger);
        WriteFile(settingsPath, clamped, logger);
        return Project(clamped, userProfile);
    }

    private static ResolvedSettings Validate(AppSettings settings, string userProfile, ILogger logger) =>
        Project(Clamp(settings, logger), userProfile);

    /// <summary>Clamp every numeric knob to its legal range (warning on change); routing/comment pass through.</summary>
    private static AppSettings Clamp(AppSettings settings, ILogger logger)
    {
        var scheduler = settings.Scheduler ?? new SchedulerSettings();
        var engine = settings.Engine ?? new EngineSettings();
        var retry = settings.Retry ?? new RetrySettings();

        var maxSegments = ClampInt(logger, "engine.maxSegmentsPerDownload", engine.MaxSegmentsPerDownload, MinSegments, MaxSegments);
        var baseDelay = ClampDouble(logger, "retry.baseDelaySeconds", retry.BaseDelaySeconds, MinDelaySeconds, MaxDelaySeconds);
        // Max backoff must be at least the base delay, otherwise the bound is incoherent.
        var maxDelay = ClampDouble(logger, "retry.maxDelaySeconds", retry.MaxDelaySeconds, baseDelay, MaxDelaySeconds);

        return new AppSettings
        {
            Comment = settings.Comment,
            Scheduler = new SchedulerSettings
            {
                MaxConcurrentDownloads = ClampInt(logger, "scheduler.maxConcurrentDownloads", scheduler.MaxConcurrentDownloads, MinConcurrent, MaxConcurrent),
                QueueCapacity = ClampInt(logger, "scheduler.queueCapacity", scheduler.QueueCapacity, MinQueueCapacity, MaxQueueCapacity),
            },
            Engine = new EngineSettings
            {
                // Requested default is also bounded by the (clamped) hard segment cap.
                SegmentsPerDownload = ClampInt(logger, "engine.segmentsPerDownload", engine.SegmentsPerDownload, MinSegments, maxSegments),
                MaxSegmentsPerDownload = maxSegments,
                SmallFileThresholdBytes = ClampLong(logger, "engine.smallFileThresholdBytes", engine.SmallFileThresholdBytes, 0, long.MaxValue),
                CopyBufferBytes = ClampInt(logger, "engine.copyBufferBytes", engine.CopyBufferBytes, MinCopyBuffer, MaxCopyBuffer),
                CheckpointIntervalBytes = ClampLong(logger, "engine.checkpointIntervalBytes", engine.CheckpointIntervalBytes, MinCheckpoint, MaxCheckpoint),
                PerAttemptTimeoutSeconds = ClampInt(logger, "engine.perAttemptTimeoutSeconds", engine.PerAttemptTimeoutSeconds, MinAttemptTimeout, MaxAttemptTimeout),
                MaxFullPreallocationBytes = ClampLong(logger, "engine.maxFullPreallocationBytes", engine.MaxFullPreallocationBytes, 0, long.MaxValue),
            },
            Retry = new RetrySettings
            {
                MaxAttempts = ClampInt(logger, "retry.maxAttempts", retry.MaxAttempts, MinAttempts, MaxAttempts),
                BaseDelaySeconds = baseDelay,
                MaxDelaySeconds = maxDelay,
                JitterFactor = ClampDouble(logger, "retry.jitterFactor", retry.JitterFactor, MinJitter, MaxJitter),
            },
            Routing = settings.Routing ?? RoutingSettings.CreateDefault(),
        };
    }

    /// <summary>Project an already-clamped raw settings into the engine-ready option records.</summary>
    private static ResolvedSettings Project(AppSettings clamped, string userProfile) => new()
    {
        Scheduler = new SchedulerOptions
        {
            MaxConcurrentDownloads = clamped.Scheduler.MaxConcurrentDownloads,
            QueueCapacity = clamped.Scheduler.QueueCapacity,
        },
        Engine = new EngineOptions
        {
            CopyBufferSize = clamped.Engine.CopyBufferBytes,
            CheckpointIntervalBytes = clamped.Engine.CheckpointIntervalBytes,
            PerAttemptTimeout = TimeSpan.FromSeconds(clamped.Engine.PerAttemptTimeoutSeconds),
            MaxSegmentsPerDownload = clamped.Engine.MaxSegmentsPerDownload,
            SmallFileThresholdBytes = clamped.Engine.SmallFileThresholdBytes,
            MaxFullPreallocationBytes = clamped.Engine.MaxFullPreallocationBytes,
        },
        Retry = new RetryOptions
        {
            MaxAttempts = clamped.Retry.MaxAttempts,
            BaseDelay = TimeSpan.FromSeconds(clamped.Retry.BaseDelaySeconds),
            MaxDelay = TimeSpan.FromSeconds(clamped.Retry.MaxDelaySeconds),
            JitterFactor = clamped.Retry.JitterFactor,
        },
        Routing = RoutingOptions.FromSettings(clamped.Routing, userProfile),
        Defaults = new DownloadDefaults { SegmentCount = clamped.Engine.SegmentsPerDownload },
    };

    private static void TryWriteDefault(string settingsPath, AppSettings settings, ILogger logger)
    {
        if (WriteFile(settingsPath, settings, logger))
        {
            // README sibling is best-effort and only seeded on first run.
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(settingsPath)!, "settings.README.md"), ReadmeText);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWriteFailed(logger, settingsPath, ex.Message);
            }

            LogCreatedDefault(logger, settingsPath);
        }
    }

    /// <summary>Write settings.json, creating the directory. Returns false (with a warning) if it cannot.</summary>
    private static bool WriteFile(string settingsPath, AppSettings settings, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't persist (read-only home, etc.) — in-memory values still apply.
            LogWriteFailed(logger, settingsPath, ex.Message);
            return false;
        }
    }

    private static int ClampInt(ILogger logger, string key, int value, int min, int max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            LogClamped(logger, key, value, min, max, clamped);
        }

        return clamped;
    }

    private static long ClampLong(ILogger logger, string key, long value, long min, long max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            LogClamped(logger, key, value, min, max, clamped);
        }

        return clamped;
    }

    private static double ClampDouble(ILogger logger, string key, double value, double min, double max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            LogClamped(logger, key, value, min, max, clamped);
        }

        return clamped;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "settings.json: {Key} = {Value} is out of range [{Min}, {Max}]; clamped to {Clamped}.")]
    private static partial void LogClamped(ILogger logger, string key, object value, object min, object max, object clamped);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "settings.json at {Path} is malformed ({Error}); using defaults and leaving the file untouched.")]
    private static partial void LogMalformed(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "No settings.json at {Path}; wrote a default file.")]
    private static partial void LogCreatedDefault(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not write default settings.json at {Path}: {Error}. Using in-memory defaults.")]
    private static partial void LogWriteFailed(ILogger logger, string path, string error);

    private const string ReadmeText = """
        # DownloadManager settings.json

        This file is read once at startup. Edit it with any text editor. Invalid values do not crash the
        app: out-of-range numbers are clamped to the nearest legal value (a warning is logged), and a
        file that fails to parse is ignored (defaults are used) and left untouched.

        ## scheduler
        - `maxConcurrentDownloads` — downloads running at once. Range [1, 64]. Default 3.
        - `queueCapacity` — bounded queue size; enqueue applies backpressure when full. Range [1, 65536].

        ## engine
        - `segmentsPerDownload` — desired segments for a new download. Range [1, 16]. Default 8.
        - `maxSegmentsPerDownload` — hard cap on segments. Range [1, 16]. Default 16.
        - `smallFileThresholdBytes` — files below this download as a single stream. Default 8 MiB.
        - `copyBufferBytes` — network→disk copy buffer. Range [4 KiB, 8 MiB]. Default 128 KiB.
        - `checkpointIntervalBytes` — bytes between durable fsync checkpoints. Range [64 KiB, 1 GiB].
        - `perAttemptTimeoutSeconds` — max time for one attempt. Range [1, 3600]. Default 100.
        - `maxFullPreallocationBytes` — cap on bytes reserved up front by Full preallocation; a larger
          server-advertised size (or one above most of the free disk) falls back to sparse. Default 16 GiB.

        ## retry
        - `maxAttempts` — total attempts including the first. Range [1, 100]. Default 5.
        - `baseDelaySeconds` — first backoff delay. Range [0, 3600]. Default 1.
        - `maxDelaySeconds` — backoff ceiling (raised to at least baseDelaySeconds). Range [0, 3600].
        - `jitterFactor` — fraction of the delay added as random jitter. Range [0, 1]. Default 0.2.

        ## routing (IDM-style, by file extension, resolved at download start)
        Each category lists its `extensions` and a destination `folder`. Relative folders resolve against
        your user profile; absolute folders are used as-is. `unknownFolder` is the catch-all for unknown
        or extensionless downloads. Target folders are created automatically.

        Terminal content types (video/audio/documents/pictures) route to the matching user folder.
        Containers (archives/executables) route to neutral subfolders of Downloads, because the
        extension does not reveal the contained content. Add your own extensions or categories freely.
        """;
}