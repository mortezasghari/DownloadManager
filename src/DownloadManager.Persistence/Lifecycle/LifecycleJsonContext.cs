using System.Text.Json.Serialization;
using DownloadManager.Core.Lifecycle;

namespace DownloadManager.Persistence.Lifecycle;

/// <summary>
/// System.Text.Json <b>source-generation</b> context for the lifecycle-event log (ADR-0021), mirroring the
/// metadata/settings/history contexts. Compact (one line per event), enum-as-string, reflection-free —
/// mandatory under Native AOT (spec §1).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LifecycleEvent))]
internal sealed partial class LifecycleJsonContext : JsonSerializerContext;