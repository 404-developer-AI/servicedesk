namespace Servicedesk.Infrastructure.Mail.Graph;

/// Microsoft Graph protocol constants for outbound attachments. These are
/// API contracts, not tunables — the admin-facing knob is the total-payload
/// cap (Mail.MaxOutboundTotalBytes), which stays a setting.
public static class GraphMailLimits
{
    /// Graph rejects a single-request fileAttachment POST (bytes base64-encoded
    /// in the JSON body) above ~3 MB. Parts above this threshold must go
    /// through an upload session on the draft instead (Graph allows up to
    /// ~150 MB per part that way, subject to the tenant's Exchange Online
    /// message-size limit).
    public const long SimpleAttachmentMaxBytes = 3 * 1024 * 1024;

    /// Upload-session chunk size. Graph requires byte ranges in multiples of
    /// 320 KiB and recommends staying under 4 MB per PUT; 10 × 320 KiB keeps
    /// a large part down to a handful of round-trips.
    public const int UploadChunkBytes = 10 * 320 * 1024;

    public static bool RequiresUploadSession(long sizeBytes) => sizeBytes > SimpleAttachmentMaxBytes;
}
