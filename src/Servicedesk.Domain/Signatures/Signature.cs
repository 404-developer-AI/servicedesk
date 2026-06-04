namespace Servicedesk.Domain.Signatures;

/// An admin-managed email signature. The visual layout lives in
/// <see cref="Design"/> (a block-tree) and is rendered to email-safe HTML at
/// send-time; image bytes referenced by the design are stored as
/// <see cref="SignatureAsset"/> rows and embedded inline (cid) on each send.
public sealed record Signature(
    Guid Id,
    string Name,
    SignatureDesign Design,
    bool IsSystem,
    bool Enabled,
    int SortOrder,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedBy);

/// Block-tree design. Deserialized from <c>mail_signatures.design</c> (JSONB,
/// camelCase). Kept deliberately tolerant: unknown block types and missing
/// fields render to nothing rather than throwing, so a newer frontend can add
/// a block kind without breaking an older renderer mid-deploy.
public sealed record SignatureDesign
{
    public int Version { get; init; } = 1;

    /// Outer background colour (hex). Null = transparent.
    public string? Background { get; init; }

    /// CSS font-family stack applied to the whole signature. Null = inherit a
    /// safe default (Inter / Arial / sans-serif) chosen by the renderer.
    public string? FontFamily { get; init; }

    /// Hard max width (px) of the signature table. Null = renderer default.
    public int? MaxWidthPx { get; init; }

    public IReadOnlyList<SignatureRow> Rows { get; init; } = Array.Empty<SignatureRow>();
}

public sealed record SignatureRow
{
    public IReadOnlyList<SignatureColumn> Columns { get; init; } = Array.Empty<SignatureColumn>();
}

public sealed record SignatureColumn
{
    /// Column width as a percentage of the row. Null = auto / equal split.
    public int? WidthPct { get; init; }

    /// Vertical alignment: "top" | "middle" | "bottom". Null = top.
    public string? VAlign { get; init; }

    public IReadOnlyList<SignatureBlock> Blocks { get; init; } = Array.Empty<SignatureBlock>();
}

/// A single content block. A flat property-bag with a <see cref="Type"/>
/// discriminator (rather than a polymorphic hierarchy) so System.Text.Json
/// round-trips it without custom converters and an unknown type degrades
/// gracefully. Only the fields relevant to <see cref="Type"/> are populated.
public sealed record SignatureBlock
{
    /// "text" | "image" | "divider" | "spacer" | "social" | "disclaimer".
    public string Type { get; init; } = "text";

    // ---- text / disclaimer ----
    /// Constrained rich-text HTML (may contain `{{agent.*}}` tokens).
    /// Sanitized server-side before render and before storage.
    public string? Html { get; init; }

    // ---- image ----
    /// Static image: the id of a SignatureAsset belonging to this signature.
    public string? AssetId { get; init; }

    /// Dynamic image source, e.g. "agent.photo" — resolved per-sender at
    /// send-time. Takes precedence over <see cref="AssetId"/> when set.
    public string? Variable { get; init; }

    public int? WidthPx { get; init; }

    /// For image: render height. For spacer: the vertical gap height.
    public int? HeightPx { get; init; }

    /// Optional link wrapping the image.
    public string? Href { get; init; }

    public string? Alt { get; init; }

    /// "0" / "9999" etc. — border-radius in px for image blocks (round avatar).
    public int? RadiusPx { get; init; }

    // ---- divider ----
    public string? Color { get; init; }
    public int? ThicknessPx { get; init; }
    public int? MarginPx { get; init; }

    // ---- social ----
    public IReadOnlyList<SignatureSocialItem>? Social { get; init; }
}

public sealed record SignatureSocialItem
{
    /// "facebook" | "instagram" | "linkedin" | … — drives a default icon when
    /// no custom <see cref="AssetId"/> is supplied.
    public string Network { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? AssetId { get; init; }
}

/// Content-addressed image asset belonging to a signature. Bytes live on disk
/// via IBlobStore keyed by <see cref="ContentHash"/>.
public sealed record SignatureAsset(
    Guid Id,
    Guid SignatureId,
    string ContentHash,
    string MimeType,
    string OriginalFilename,
    long SizeBytes,
    DateTime CreatedUtc);

/// A queue→signature binding ("this signature is active on this mailbox").
public sealed record SignatureMailbox(Guid QueueId, Guid SignatureId);

/// Per-user signature profile fields. Each is a local override of the Entra ID
/// value: null means "use the Entra value (or collapse if absent)".
public sealed record AgentProfile(
    Guid UserId,
    string? DisplayName,
    string? JobTitle,
    string? WorkPhone,
    string? MobilePhone,
    string? PhotoBlobHash,
    string? PhotoMime,
    DateTime? EntraSyncedUtc);

/// The fully-resolved variable values for one sender, ready for substitution.
/// Empty strings mean "no value" — the renderer collapses the line.
public sealed record SignatureVariables(
    string FullName,
    string FirstName,
    string LastName,
    string JobTitle,
    string Email,
    string Phone,
    string Mobile,
    string? PhotoBlobHash,
    string? PhotoMime)
{
    public static readonly SignatureVariables Empty =
        new(string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, null, null);
}
