using System.Text.Json.Serialization;
using DownloadManager.Core.History;

namespace DownloadManager.Persistence.History;

/// <summary>
/// System.Text.Json <b>source-generation</b> context for <c>history.json</c> (ADR-0019), mirroring the
/// metadata and settings contexts. Generated (reflection-free) binding is mandatory under Native AOT
/// (spec §1). <see cref="HistoryState"/> is written as a readable string via the source-gen string-enum
/// converter, and the file is indented so it is hand-inspectable.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HistoryFile))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;