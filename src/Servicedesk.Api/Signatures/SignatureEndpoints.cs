using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Signatures;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Signatures;

/// Admin HTTP surface for email signatures: CRUD over <c>mail_signatures</c>,
/// mailbox (queue) bindings, and the image-asset upload/serve used by the
/// builder. All endpoints are admin-only — signatures carry company branding
/// and a system-mail identity, both privileged. The live builder preview is
/// rendered client-side; the authoritative render happens server-side at send
/// time (<see cref="ISignatureComposer"/>).
public static class SignatureEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxDesignBytes = 500_000;
    private const int MaxQueueBindings = 512;
    private const int MaxAssetsPerSignature = 64;

    public static IEndpointRouteBuilder MapSignatureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/signatures")
            .WithTags("Signatures")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (ISignatureRepository repo, CancellationToken ct) =>
        {
            var sigs = await repo.ListAsync(ct);
            var bindings = await repo.ListMailboxesAsync(ct);
            var byId = bindings.GroupBy(b => b.SignatureId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.QueueId).ToList());
            return Results.Ok(sigs.Select(s => MapSummary(s, byId.GetValueOrDefault(s.Id) ?? new List<Guid>())));
        }).WithName("ListSignatures").WithOpenApi();

        group.MapGet("/tokens", () => Results.Ok(new
        {
            tokens = SignatureTokens.Supported.Select(t => new { token = t.Token, label = t.Label }),
            photoVariable = SignatureTokens.AgentPhoto,
        })).WithName("ListSignatureTokens").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, ISignatureRepository repo, CancellationToken ct) =>
        {
            var sig = await repo.GetAsync(id, ct);
            if (sig is null) return Results.NotFound();
            var assets = await repo.ListAssetsAsync(id, ct);
            var queueIds = await repo.ListQueuesForSignatureAsync(id, ct);
            return Results.Ok(MapDetail(sig, assets, queueIds));
        }).WithName("GetSignature").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] UpsertSignatureRequest req, HttpContext http,
            ISignatureRepository repo, ISignatureHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var design = SignatureDesignSanitizer.Sanitize(req.Design ?? new SignatureDesign(), sanitizer);

            var id = await repo.CreateAsync(
                req.Name!.Trim(), design, req.IsSystem ?? false, req.Enabled ?? true,
                req.SortOrder ?? 0, userId, ct);

            if (req.QueueIds is { Count: > 0 })
                await repo.SetQueuesForSignatureAsync(id, req.QueueIds, ct);

            await Audit(audit, http, "signature.create", id, new { req.IsSystem, queueCount = req.QueueIds?.Count ?? 0 });

            var created = await repo.GetAsync(id, ct);
            var assets = await repo.ListAssetsAsync(id, ct);
            var queueIds = await repo.ListQueuesForSignatureAsync(id, ct);
            return Results.Created($"/api/settings/signatures/{id}", MapDetail(created!, assets, queueIds));
        }).WithName("CreateSignature").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] UpsertSignatureRequest req, HttpContext http,
            ISignatureRepository repo, ISignatureHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var design = SignatureDesignSanitizer.Sanitize(req.Design ?? new SignatureDesign(), sanitizer);

            await repo.UpdateAsync(
                id, req.Name!.Trim(), design,
                req.IsSystem ?? existing.IsSystem, req.Enabled ?? existing.Enabled,
                req.SortOrder ?? existing.SortOrder, ct);

            await repo.SetQueuesForSignatureAsync(id, req.QueueIds ?? Array.Empty<Guid>(), ct);

            await Audit(audit, http, "signature.update", id, new { req.IsSystem, req.Enabled, queueCount = req.QueueIds?.Count ?? 0 });

            var updated = await repo.GetAsync(id, ct);
            var assets = await repo.ListAssetsAsync(id, ct);
            var queueIds = await repo.ListQueuesForSignatureAsync(id, ct);
            return Results.Ok(MapDetail(updated!, assets, queueIds));
        }).WithName("UpdateSignature").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ISignatureRepository repo,
            ISettingsService settings, IBlobStore blobs, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            // Best-effort blob cleanup for this signature's assets before the
            // cascade drops the rows. A shared content-hash (same image in two
            // signatures) is content-addressed, so a delete here only removes
            // bytes no longer referenced — but to stay safe we leave the blob
            // store's own orphan-sweep to reclaim; we only drop the rows.
            var deleted = await repo.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            // If this was the configured system signature, clear the pointer so
            // trigger mail doesn't reference a dead id.
            var sysId = await settings.GetAsync<string>(SettingKeys.Signatures.DefaultSystemSignatureId, ct);
            if (Guid.TryParse(sysId, out var sg) && sg == id)
            {
                var (actor, role) = ActorContext.Resolve(http);
                await settings.SetAsync<string>(SettingKeys.Signatures.DefaultSystemSignatureId, "", actor, role, ct);
            }

            await Audit(audit, http, "signature.delete", id, null);
            return Results.NoContent();
        }).WithName("DeleteSignature").WithOpenApi();

        MapPortabilityEndpoints(group);
        MapAssetEndpoints(group);
        return app;
    }

    // ---- export / import (v0.0.61) ---------------------------------------
    // A signature is portable as a single self-contained JSON bundle: the
    // block-tree design plus its STATIC image assets (logo, social/contact
    // icons) inlined as base64, so it can be re-created on a fresh install
    // where those images don't exist yet. Per-sender variable images (the
    // {{agent.photo}} block) are NOT assets, so they're never embedded — the
    // imported signature keeps the photo placeholder, resolved per agent on the
    // target install. Mailbox bindings and the system flag are install-specific
    // and deliberately left out.
    private static void MapPortabilityEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/export", async (
            Guid id, HttpContext http, ISignatureRepository repo, IBlobStore blobs,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var sig = await repo.GetAsync(id, ct);
            if (sig is null) return Results.NotFound();

            var assets = await repo.ListAssetsAsync(id, ct);
            var bundleAssets = new List<SignatureBundleAsset>(assets.Count);
            foreach (var a in assets)
            {
                await using var blob = await blobs.OpenReadAsync(a.ContentHash, ct);
                if (blob is null) continue; // bytes gone — skip; design ref collapses on import
                using var ms = new MemoryStream();
                await blob.CopyToAsync(ms, ct);
                bundleAssets.Add(new SignatureBundleAsset(
                    a.Id.ToString(), a.MimeType, a.OriginalFilename, Convert.ToBase64String(ms.ToArray())));
            }

            await Audit(audit, http, "signature.export", id, new { assetCount = bundleAssets.Count });

            var bundle = new SignatureBundle(
                SignatureBundle.KindValue, SignatureBundle.CurrentVersion, sig.Name, sig.Design, bundleAssets);
            return Results.Json(bundle);
        }).WithName("ExportSignature").WithOpenApi();

        group.MapPost("/import", async (
            HttpContext http, ISignatureRepository repo, ISignatureHtmlSanitizer sanitizer,
            IBlobStore blobs, ISettingsService settings, IAuditLogger audit, CancellationToken ct) =>
        {
            // We read the body manually so we can raise the request-size cap
            // (base64 imagery is bulky) before binding — [FromBody] would bind
            // under the default Kestrel limit first.
            var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is not null && !sizeFeature.IsReadOnly)
                sizeFeature.MaxRequestBodySize = MaxImportBodyBytes;

            SignatureBundle? bundle;
            try
            {
                bundle = await http.Request.ReadFromJsonAsync<SignatureBundle>(ct);
            }
            catch
            {
                return Results.BadRequest(new { error = "Body is not a valid signature bundle." });
            }

            var err = ValidateBundle(bundle);
            if (err is not null) return Results.BadRequest(new { error = err });
            bundle = bundle!;

            var maxBytes = await settings.GetAsync<long>(SettingKeys.Storage.InlineImageMaxBytes, ct);
            if (maxBytes <= 0) maxBytes = 2_097_152;

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var name = bundle.Name!.Trim();

            // Create the row first (disabled, non-system) so AddAssetAsync has a
            // parent; then write each asset to learn its new id, remap the design
            // references, sanitize, and store. Roll back the row on any failure.
            var newId = await repo.CreateAsync(name, new SignatureDesign(), isSystem: false, enabled: false, 0, userId, ct);
            try
            {
                var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in bundle.Assets)
                {
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(a.DataBase64 ?? string.Empty); }
                    catch { return await FailImport(repo, newId, ct, "An embedded image is not valid base64."); }

                    if (bytes.Length == 0)
                        return await FailImport(repo, newId, ct, "An embedded image is empty.");
                    if (bytes.Length > maxBytes)
                        return await FailImport(repo, newId, ct,
                            $"An embedded image exceeds the {Math.Max(1, maxBytes / 1_048_576)} MB limit.");

                    var sniffed = MimeSniffer.Sniff(
                        bytes.AsSpan(0, Math.Min(bytes.Length, MimeSniffer.SniffWindowBytes)), a.MimeType, a.Filename);
                    if (!sniffed.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        return await FailImport(repo, newId, ct, "Only image assets are allowed in a signature bundle.");

                    BlobWriteResult written;
                    await using (var bs = new MemoryStream(bytes, writable: false))
                    {
                        written = await blobs.WriteAsync(bs, ct);
                    }
                    var filename = SanitizeFilename(a.Filename ?? "image");
                    var newAssetId = await repo.AddAssetAsync(newId, written.ContentHash, sniffed, filename, written.SizeBytes, ct);
                    if (!string.IsNullOrWhiteSpace(a.Id)) idMap[a.Id!] = newAssetId.ToString();
                }

                var remapped = SignatureAssetRemap.Remap(bundle.Design ?? new SignatureDesign(), idMap);
                var design = SignatureDesignSanitizer.Sanitize(remapped, sanitizer);
                if (SignatureJson.Serialize(design).Length > MaxDesignBytes)
                    return await FailImport(repo, newId, ct, $"Imported design exceeds the {MaxDesignBytes / 1000} KB limit.");

                await repo.UpdateAsync(newId, name, design, isSystem: false, enabled: false, 0, ct);
            }
            catch
            {
                await repo.DeleteAsync(newId, ct);
                throw;
            }

            await Audit(audit, http, "signature.import", newId, new { assetCount = bundle.Assets.Count });

            var created = await repo.GetAsync(newId, ct);
            var createdAssets = await repo.ListAssetsAsync(newId, ct);
            return Results.Created($"/api/settings/signatures/{newId}", MapDetail(created!, createdAssets, Array.Empty<Guid>()));
        }).WithName("ImportSignature").WithOpenApi().DisableRequestTimeout();
    }

    private static async Task<IResult> FailImport(ISignatureRepository repo, Guid id, CancellationToken ct, string message)
    {
        await repo.DeleteAsync(id, ct);
        return Results.BadRequest(new { error = message });
    }

    private static string? ValidateBundle(SignatureBundle? bundle)
    {
        if (bundle is null) return "Body is required.";
        if (!string.Equals(bundle.Kind, SignatureBundle.KindValue, StringComparison.OrdinalIgnoreCase))
            return "This file is not a signature bundle.";
        if (bundle.Version > SignatureBundle.CurrentVersion)
            return "This bundle was exported by a newer version and can't be imported here.";
        if (string.IsNullOrWhiteSpace(bundle.Name) || bundle.Name.Trim().Length > MaxNameLength)
            return $"Name is required and must be ≤{MaxNameLength} characters.";
        if (bundle.Assets is null) return "Bundle assets are missing.";
        if (bundle.Assets.Count > MaxAssetsPerSignature)
            return $"A signature may hold at most {MaxAssetsPerSignature} images.";
        return null;
    }

    private const int MaxImportBodyBytes = 64 * 1024 * 1024;

    /// Portable signature bundle: design + STATIC image assets (base64). Shared
    /// shape between export and import.
    public sealed record SignatureBundle(
        string Kind,
        int Version,
        string Name,
        SignatureDesign Design,
        IReadOnlyList<SignatureBundleAsset> Assets)
    {
        public const string KindValue = "servicedesk.signature";
        public const int CurrentVersion = 1;
    }

    public sealed record SignatureBundleAsset(
        string Id, string MimeType, string Filename, string DataBase64);

    private static void MapAssetEndpoints(RouteGroupBuilder group)
    {
        // Upload an image asset for a signature. Images only — sniffed
        // server-side; a non-image (or HTML masquerading as an image) is
        // refused. Bytes are content-addressed in the blob store.
        group.MapPost("/{id:guid}/assets", async (
            Guid id, HttpContext http,
            ISignatureRepository repo, IBlobStore blobs,
            ISettingsService settings, IAuditLogger audit, CancellationToken ct) =>
        {
            var sig = await repo.GetAsync(id, ct);
            if (sig is null) return Results.NotFound();

            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required." });

            var existingAssets = await repo.ListAssetsAsync(id, ct);
            if (existingAssets.Count >= MaxAssetsPerSignature)
                return Results.BadRequest(new { error = $"A signature may hold at most {MaxAssetsPerSignature} images." });

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Upload a non-empty file in the 'file' field." });

            var maxBytes = await settings.GetAsync<long>(SettingKeys.Storage.InlineImageMaxBytes, ct);
            if (maxBytes <= 0) maxBytes = 2_097_152; // 2 MB safety net for signature imagery
            if (file.Length > maxBytes)
                return Results.Json(new { error = $"Image exceeds the {Math.Max(1, maxBytes / 1_048_576)} MB limit." },
                    statusCode: 413);

            // Signature images are small — read fully, sniff, then store.
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
                return Results.BadRequest(new { error = "Only image files are allowed for signatures." });

            BlobWriteResult written;
            await using (var blobStream = new MemoryStream(bytes, writable: false))
            {
                written = await blobs.WriteAsync(blobStream, ct);
            }

            var filename = SanitizeFilename(file.FileName);
            var assetId = await repo.AddAssetAsync(id, written.ContentHash, sniffed, filename, written.SizeBytes, ct);

            await Audit(audit, http, "signature.asset.add", id, new { assetId, mimeType = sniffed, size = written.SizeBytes });

            return Results.Created($"/api/settings/signatures/{id}/assets/{assetId}", new
            {
                id = assetId,
                url = $"/api/settings/signatures/{id}/assets/{assetId}",
                mimeType = sniffed,
                filename,
                size = written.SizeBytes,
            });
        }).WithName("UploadSignatureAsset").WithOpenApi().DisableRequestTimeout();

        // Serve an asset for the builder preview. Admin-only (same group auth).
        group.MapGet("/{id:guid}/assets/{assetId:guid}", async (
            Guid id, Guid assetId, HttpContext http,
            ISignatureRepository repo, IBlobStore blobs, CancellationToken ct) =>
        {
            var asset = await repo.GetAssetAsync(assetId, ct);
            if (asset is null || asset.SignatureId != id) return Results.NotFound();

            var etag = $"\"{asset.ContentHash}\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, max-age=604800, must-revalidate";
            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag)))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var stream = await blobs.OpenReadAsync(asset.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            var contentType = string.IsNullOrWhiteSpace(asset.MimeType) ? "application/octet-stream" : asset.MimeType;
            return Results.File(stream, contentType, fileDownloadName: null, enableRangeProcessing: true);
        }).WithName("GetSignatureAsset").WithOpenApi();

        group.MapDelete("/{id:guid}/assets/{assetId:guid}", async (
            Guid id, Guid assetId, HttpContext http,
            ISignatureRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var asset = await repo.GetAssetAsync(assetId, ct);
            if (asset is null || asset.SignatureId != id) return Results.NotFound();

            var deleted = await repo.DeleteAssetAsync(assetId, ct);
            if (!deleted) return Results.NotFound();

            await Audit(audit, http, "signature.asset.delete", id, new { assetId });
            return Results.NoContent();
        }).WithName("DeleteSignatureAsset").WithOpenApi();
    }

    public sealed record UpsertSignatureRequest(
        string? Name,
        SignatureDesign? Design,
        bool? IsSystem,
        bool? Enabled,
        int? SortOrder,
        IReadOnlyList<Guid>? QueueIds);

    private static string? Validate(UpsertSignatureRequest req)
    {
        if (req is null) return "Body is required.";
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > MaxNameLength)
            return $"Name is required and must be ≤{MaxNameLength} characters.";
        if (req.Design is not null && SignatureJson.Serialize(req.Design).Length > MaxDesignBytes)
            return $"Design exceeds the {MaxDesignBytes / 1000} KB limit.";
        if (req.QueueIds is not null && req.QueueIds.Count > MaxQueueBindings)
            return $"A signature may be bound to at most {MaxQueueBindings} mailboxes.";
        if (req.QueueIds is not null && req.QueueIds.Distinct().Count() != req.QueueIds.Count)
            return "queueIds must not contain duplicates.";
        return null;
    }

    private static object MapSummary(Signature s, IReadOnlyList<Guid> queueIds) => new
    {
        id = s.Id,
        name = HtmlEncoder.Default.Encode(s.Name),
        isSystem = s.IsSystem,
        enabled = s.Enabled,
        sortOrder = s.SortOrder,
        queueIds,
        updatedUtc = s.UpdatedUtc,
    };

    private static object MapDetail(Signature s, IReadOnlyList<SignatureAsset> assets, IReadOnlyList<Guid> queueIds) => new
    {
        id = s.Id,
        name = HtmlEncoder.Default.Encode(s.Name),
        // design is the block-tree; returned as-is so the builder round-trips it.
        design = s.Design,
        isSystem = s.IsSystem,
        enabled = s.Enabled,
        sortOrder = s.SortOrder,
        queueIds,
        assets = assets.Select(a => new
        {
            id = a.Id,
            url = $"/api/settings/signatures/{s.Id}/assets/{a.Id}",
            mimeType = a.MimeType,
            filename = a.OriginalFilename,
            size = a.SizeBytes,
        }),
        createdUtc = s.CreatedUtc,
        updatedUtc = s.UpdatedUtc,
    };

    private static async Task Audit(IAuditLogger audit, HttpContext http, string eventType, Guid target, object? payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }

    private static string SanitizeFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "image";
        var leaf = Path.GetFileName(input);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = leaf.Select(c => invalid.Contains(c) || c is '<' or '>' or ':' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimStart('.');
        if (string.IsNullOrEmpty(s)) s = "image";
        return s.Length > 200 ? s[..200] : s;
    }
}
