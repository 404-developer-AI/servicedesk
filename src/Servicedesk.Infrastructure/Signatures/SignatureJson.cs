using System.Text.Json;
using System.Text.Json.Serialization;
using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

/// Single source of truth for (de)serializing a <see cref="SignatureDesign"/>
/// to/from the <c>mail_signatures.design</c> JSONB column and the API. Frontend
/// emits camelCase; we read case-insensitively and skip nulls so the stored
/// JSON stays compact.
public static class SignatureJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Parses stored/posted JSON into a design. Tolerant: blank or malformed
    /// JSON yields an empty design rather than throwing, so a render never
    /// crashes a send over a bad row.
    public static SignatureDesign Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SignatureDesign();
        try
        {
            return JsonSerializer.Deserialize<SignatureDesign>(json, Options) ?? new SignatureDesign();
        }
        catch (JsonException)
        {
            return new SignatureDesign();
        }
    }

    public static string Serialize(SignatureDesign design)
        => JsonSerializer.Serialize(design, Options);
}
