using System.Globalization;
using System.Text.Json.Serialization;

namespace DownloadManager.Core.Domain;

/// <summary>
/// HTTP cache validators used as resume preconditions (spec §7). Storing them is not enough — they
/// must be sent as <c>If-Range</c> so the server proves the resource is unchanged before honouring
/// a ranged resume.
/// </summary>
public readonly record struct ResourceValidators(string? ETag, DateTimeOffset? LastModified)
{
    public static readonly ResourceValidators None = new(null, null);

    [JsonIgnore]
    public bool HasUsableValidator => !string.IsNullOrEmpty(ETag) || LastModified is not null;

    /// <summary>
    /// A strong ETag is one that is not weak-prefixed (<c>W/</c>). Only strong validators are valid
    /// in <c>If-Range</c>; a weak ETag must not be used there (RFC 9110 §13.1.3).
    /// </summary>
    [JsonIgnore]
    public bool HasStrongETag =>
        !string.IsNullOrEmpty(ETag) && !ETag.StartsWith("W/", StringComparison.Ordinal);

    /// <summary>
    /// The value to send in <c>If-Range</c>: prefer a strong ETag, otherwise fall back to the
    /// <c>Last-Modified</c> HTTP-date. Returns <c>null</c> when no usable validator exists, in which
    /// case resume is unsafe and the policy in §7 applies.
    /// </summary>
    public string? ToIfRangeHeaderValue()
    {
        if (HasStrongETag)
        {
            return ETag;
        }

        return LastModified is { } lastModified
            ? lastModified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)
            : null;
    }
}