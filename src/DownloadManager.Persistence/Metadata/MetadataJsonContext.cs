using System.Text.Json.Serialization;
using DownloadManager.Core.Domain;

namespace DownloadManager.Persistence.Metadata;

/// <summary>
/// System.Text.Json <b>source-generation</b> context for the metadata sidecar (spec §6a). Using a
/// generated context means no reflection-based serialization at runtime, which is mandatory for
/// Native AOT (spec §1/§6).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DownloadMetadata))]
internal sealed partial class MetadataJsonContext : JsonSerializerContext;