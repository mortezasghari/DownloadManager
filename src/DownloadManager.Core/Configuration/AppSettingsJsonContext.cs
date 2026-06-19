using System.Text.Json.Serialization;

namespace DownloadManager.Core.Configuration;

/// <summary>
/// System.Text.Json <b>source-generation</b> context for <see cref="AppSettings"/> (ADR-0016), mirroring
/// the persistence sidecar's <c>MetadataJsonContext</c>. A generated context means config is bound with
/// zero reflection — mandatory under Native AOT, where the reflection-based configuration binder throws
/// (spec §1). <see cref="System.Text.Json.JsonSerializerOptions.WriteIndented"/> is on so the first-run
/// default file is human-editable.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
