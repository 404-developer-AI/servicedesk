using System.Security.Claims;
using System.Text.Encodings.Web;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Signatures;

/// Self-service signature profile for the signed-in agent: the local override
/// fields that feed the `{{agent.*}}` tokens, the profile photo, and a resolved
/// preview of the variables (so the builder can show real values). Each agent
/// manages only their own row; all endpoints are agent-scoped.
public static class AgentProfileEndpoints
{
    private const int MaxFieldLength = 200;

    public static IEndpointRouteBuilder MapAgentProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me")
            .WithTags("AgentProfile")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/signature-profile", async (
            HttpContext http, IAgentProfileRepository repo, CancellationToken ct) =>
        {
            var userId = CurrentUser(http);
            var p = await repo.GetAsync(userId, ct);
            // Raw values — these populate editable form inputs; React escapes on
            // render, so HTML-encoding here would double-escape on round-trip.
            return Results.Ok(new
            {
                displayName = p?.DisplayName,
                jobTitle = p?.JobTitle,
                workPhone = p?.WorkPhone,
                mobilePhone = p?.MobilePhone,
                hasPhoto = !string.IsNullOrWhiteSpace(p?.PhotoBlobHash),
                photoUrl = string.IsNullOrWhiteSpace(p?.PhotoBlobHash) ? null : "/api/me/signature-photo",
                entraSyncedUtc = p?.EntraSyncedUtc,
            });
        }).WithName("GetMySignatureProfile").WithOpenApi();

        group.MapPut("/signature-profile", async (
            UpdateProfileRequest req, HttpContext http,
            IAgentProfileRepository repo, ISignatureVariableResolver resolver,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var userId = CurrentUser(http);
            await repo.UpsertOverrideAsync(
                userId,
                Clean(req.DisplayName), Clean(req.JobTitle),
                Clean(req.WorkPhone), Clean(req.MobilePhone), ct);
            resolver.Invalidate(userId);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "signature.profile.update", Actor: actor, ActorRole: role,
                Target: userId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()));

            return Results.NoContent();
        }).WithName("UpdateMySignatureProfile").WithOpenApi();

        group.MapPost("/signature-photo", async (
            HttpContext http, IAgentProfileRepository repo, IBlobStore blobs,
            ISettingsService settings, ISignatureVariableResolver resolver,
            CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required." });

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Upload a non-empty image in the 'file' field." });

            var maxBytes = await settings.GetAsync<long>(SettingKeys.Storage.InlineImageMaxBytes, ct);
            if (maxBytes <= 0) maxBytes = 2_097_152;
            if (file.Length > maxBytes)
                return Results.Json(new { error = $"Image exceeds the {Math.Max(1, maxBytes / 1_048_576)} MB limit." },
                    statusCode: 413);

            byte[] bytes;
            await using (var s = file.OpenReadStream())
            using (var ms = new MemoryStream())
            {
                await s.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }

            var sniffed = MimeSniffer.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, MimeSniffer.SniffWindowBytes)),
                file.ContentType, file.FileName);
            if (!sniffed.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only image files are allowed." });

            var userId = CurrentUser(http);
            BlobWriteResult written;
            await using (var blobStream = new MemoryStream(bytes, writable: false))
            {
                written = await blobs.WriteAsync(blobStream, ct);
            }
            await repo.SetPhotoAsync(userId, written.ContentHash, sniffed, ct);
            resolver.Invalidate(userId);

            return Results.Ok(new { photoUrl = "/api/me/signature-photo", mimeType = sniffed });
        }).WithName("UploadMySignaturePhoto").WithOpenApi().DisableRequestTimeout();

        group.MapDelete("/signature-photo", async (
            HttpContext http, IAgentProfileRepository repo,
            ISignatureVariableResolver resolver, CancellationToken ct) =>
        {
            var userId = CurrentUser(http);
            await repo.SetPhotoAsync(userId, null, null, ct);
            resolver.Invalidate(userId);
            return Results.NoContent();
        }).WithName("DeleteMySignaturePhoto").WithOpenApi();

        group.MapGet("/signature-photo", async (
            HttpContext http, IAgentProfileRepository repo, IBlobStore blobs, CancellationToken ct) =>
        {
            var userId = CurrentUser(http);
            var p = await repo.GetAsync(userId, ct);
            if (p is null || string.IsNullOrWhiteSpace(p.PhotoBlobHash)) return Results.NotFound();

            var etag = $"\"{p.PhotoBlobHash}\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, max-age=86400, must-revalidate";
            var inm = http.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(inm) && (inm == "*" || inm.Contains(etag)))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var stream = await blobs.OpenReadAsync(p.PhotoBlobHash, ct);
            if (stream is null) return Results.NotFound();
            return Results.File(stream, string.IsNullOrWhiteSpace(p.PhotoMime) ? "image/jpeg" : p.PhotoMime,
                fileDownloadName: null, enableRangeProcessing: true);
        }).WithName("GetMySignaturePhoto").WithOpenApi();

        // Resolved `{{agent.*}}` values for the current user — drives the
        // builder's live preview so it shows real data, not placeholders.
        group.MapGet("/signature-variables", async (
            HttpContext http, ISignatureVariableResolver resolver, CancellationToken ct) =>
        {
            var userId = CurrentUser(http);
            var v = await resolver.ResolveForUserAsync(userId, ct);
            return Results.Ok(new
            {
                fullName = Enc(v.FullName),
                firstName = Enc(v.FirstName),
                lastName = Enc(v.LastName),
                jobTitle = Enc(v.JobTitle),
                email = Enc(v.Email),
                phone = Enc(v.Phone),
                mobile = Enc(v.Mobile),
                photoUrl = string.IsNullOrWhiteSpace(v.PhotoBlobHash) ? null : "/api/me/signature-photo",
            });
        }).WithName("GetMySignatureVariables").WithOpenApi();

        return app;
    }

    public sealed record UpdateProfileRequest(
        string? DisplayName, string? JobTitle, string? WorkPhone, string? MobilePhone);

    private static string? Validate(UpdateProfileRequest req)
    {
        if (req is null) return "Body is required.";
        foreach (var v in new[] { req.DisplayName, req.JobTitle, req.WorkPhone, req.MobilePhone })
            if (v is not null && v.Length > MaxFieldLength)
                return $"Each field must be ≤{MaxFieldLength} characters.";
        return null;
    }

    private static Guid CurrentUser(HttpContext http)
        => Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Enc(string? s) => string.IsNullOrEmpty(s) ? s : HtmlEncoder.Default.Encode(s);
}
