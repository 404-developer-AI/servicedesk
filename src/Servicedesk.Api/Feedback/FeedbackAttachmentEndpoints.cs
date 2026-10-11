using Servicedesk.Api.Tickets;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Feedback;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Feedback;

/// Inline-image upload + download for Employee Feedback entries. Reuses the
/// existing blob pipeline (content-addressed dedup, MIME-sniff, size cap, HTML
/// rejection) from the ticket/KB endpoints; only the ownership
/// (<c>owner_kind='FeedbackEntry'</c>) and authorization (effective feedback
/// access — restricted users are scoped to their own entries) differ. Mirrors
/// <see cref="Servicedesk.Api.KnowledgeBase.KbAttachmentEndpoints"/>.
public static class FeedbackAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feedback/entries")
            .WithTags("Feedback")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // POST /api/feedback/entries/{id}/attachments — multipart upload.
        group.MapPost("/{id:guid}/attachments", async (
            Guid id, HttpContext http,
            IUserService users, IFeedbackEntryService entries,
            IAttachmentRepository attachments, IBlobStore blobs,
            ISettingsService settings, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;

            var entry = await entries.GetAsync(id, ct);
            if (entry is null) return Results.NotFound();
            // Restricted users may only attach to their own entries.
            if (access == FeedbackAccess.OwnOnly && entry.CreatedByUserId != userId)
                return Results.NotFound();

            var received = await UploadReceiver.ReceiveAsync(http, blobs, settings, ct);
            if (received.Error is not null) return received.Error;
            var (safeFilename, sniffedMime, writeResult) = received.Ok!.Value;

            var attachmentId = await attachments.CreateForFeedbackEntryAsync(new NewFeedbackEntryAttachment(
                EntryId: id,
                ContentHash: writeResult.ContentHash,
                SizeBytes: writeResult.SizeBytes,
                MimeType: sniffedMime,
                OriginalFilename: safeFilename), ct);

            await FeedbackAudit.WriteAsync(audit, http, "feedback.entry.attachment.added", attachmentId.ToString(),
                new { entryId = id, filename = safeFilename, mimeType = sniffedMime, size = writeResult.SizeBytes });

            return Results.Created($"/api/feedback/entries/{id}/attachments/{attachmentId}", new
            {
                id = attachmentId,
                url = $"/api/feedback/entries/{id}/attachments/{attachmentId}",
                mimeType = sniffedMime,
                size = writeResult.SizeBytes,
                filename = safeFilename,
            });
        }).WithName("UploadFeedbackAttachment").WithOpenApi();

        // GET /api/feedback/entries/{id}/attachments/{attachmentId} — download.
        group.MapGet("/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, bool? inline, HttpContext http,
            IUserService users, IFeedbackEntryService entries,
            IAttachmentRepository attachments, IBlobStore blobs,
            CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;

            var entry = await entries.GetAsync(id, ct);
            if (entry is null) return Results.NotFound();
            // Restricted users may only read attachments on their own entries.
            if (access == FeedbackAccess.OwnOnly && entry.CreatedByUserId != userId)
                return Results.NotFound();

            var att = await attachments.GetByIdAsync(attachmentId, ct);
            if (att is null) return Results.NotFound();
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash))
                return Results.NotFound();
            if (att.OwnerKind != "FeedbackEntry" || att.OwnerId != id)
                return Results.NotFound();

            var etag = Servicedesk.Api.Tickets.AttachmentResponse.ETag(att.ContentHash, att.MimeType);
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, max-age=604800, must-revalidate";
            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag)))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var stream = await blobs.OpenReadAsync(att.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            // Inline only for inline-safe types (audit v0.1.1 #2).
            return Servicedesk.Api.Tickets.AttachmentResponse.File(
                stream, att.MimeType, att.OriginalFilename, inline == true);
        }).WithName("GetFeedbackAttachment").WithOpenApi();

        return app;
    }

    private static async Task<(Guid UserId, FeedbackAccess Access, IResult? Fail)> GateAsync(
        HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        if (userId == Guid.Empty) return (Guid.Empty, FeedbackAccess.None, Results.Unauthorized());
        var access = (await users.GetFeedbackAccessAsync(userId, ct)).Access;
        if (access == FeedbackAccess.None) return (Guid.Empty, FeedbackAccess.None, Results.Forbid());
        return (userId, access, null);
    }
}
