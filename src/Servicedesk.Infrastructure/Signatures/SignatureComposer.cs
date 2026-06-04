using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Signatures;

/// Send-time entry point used by both outbound paths. Resolves the signature
/// for a mailbox (or the configured system signature), renders it for the
/// sending agent, and loads the referenced image bytes so they can be embedded
/// inline (cid). Returns <c>null</c> whenever no signature should be appended —
/// feature disabled, reply-suppressed, or nothing bound to the mailbox — so the
/// caller can no-op cleanly.
public sealed class SignatureComposer : ISignatureComposer
{
    private readonly ISettingsService _settings;
    private readonly ISignatureRepository _repo;
    private readonly ISignatureRenderer _renderer;
    private readonly ISignatureVariableResolver _resolver;
    private readonly IBlobStore _blobs;
    private readonly ILogger<SignatureComposer> _logger;

    public SignatureComposer(
        ISettingsService settings,
        ISignatureRepository repo,
        ISignatureRenderer renderer,
        ISignatureVariableResolver resolver,
        IBlobStore blobs,
        ILogger<SignatureComposer> logger)
    {
        _settings = settings;
        _repo = repo;
        _renderer = renderer;
        _resolver = resolver;
        _blobs = blobs;
        _logger = logger;
    }

    public async Task<ComposedSignature?> ComposeForQueueAsync(
        Guid queueId, Guid senderUserId, bool isReply, CancellationToken ct)
    {
        if (!await ShouldAppendAsync(isReply, ct)) return null;

        var sig = await _repo.ResolveForQueueAsync(queueId, ct);
        if (sig is null) return null;

        var vars = await _resolver.ResolveForUserAsync(senderUserId, ct);
        return await BuildAsync(sig, vars, ct);
    }

    public async Task<ComposedSignature?> ComposeSystemAsync(bool isReply, CancellationToken ct)
    {
        if (!await ShouldAppendAsync(isReply, ct)) return null;

        var idRaw = await _settings.GetAsync<string>(SettingKeys.Signatures.DefaultSystemSignatureId, ct);
        if (string.IsNullOrWhiteSpace(idRaw) || !Guid.TryParse(idRaw, out var sigId)) return null;

        var sig = await _repo.GetAsync(sigId, ct);
        if (sig is null || !sig.Enabled) return null;

        var vars = await _resolver.ResolveSystemAsync(ct);
        return await BuildAsync(sig, vars, ct);
    }

    private async Task<bool> ShouldAppendAsync(bool isReply, CancellationToken ct)
    {
        if (!await _settings.GetAsync<bool>(SettingKeys.Signatures.Enabled, ct)) return false;
        if (isReply && !await _settings.GetAsync<bool>(SettingKeys.Signatures.AppendOnReplies, ct)) return false;
        return true;
    }

    private async Task<ComposedSignature?> BuildAsync(
        Domain.Signatures.Signature sig, Domain.Signatures.SignatureVariables vars, CancellationToken ct)
    {
        var assets = await _repo.ListAssetsAsync(sig.Id, ct);
        var rendered = _renderer.Render(sig.Design, vars, assets);

        var attachments = new List<GraphOutboundAttachment>(rendered.Assets.Count);
        foreach (var a in rendered.Assets)
        {
            await using var blob = await _blobs.OpenReadAsync(a.ContentHash, ct);
            if (blob is null)
            {
                // Image referenced by the design but its bytes are gone. Skip
                // it — the <img cid> will render as nothing rather than blocking
                // the whole send.
                _logger.LogWarning(
                    "Signature {SignatureId} references blob {Hash} which is missing; image omitted.",
                    sig.Id, a.ContentHash);
                continue;
            }
            using var ms = new MemoryStream();
            await blob.CopyToAsync(ms, ct);
            attachments.Add(new GraphOutboundAttachment(
                FileName: a.FileName,
                ContentType: a.MimeType,
                Bytes: ms.ToArray(),
                IsInline: true,
                ContentId: a.Cid));
        }

        return new ComposedSignature(rendered.Html, attachments);
    }
}

/// A ready-to-send signature: the HTML to append to the body, plus the inline
/// image parts (cid) that HTML references.
public sealed record ComposedSignature(string Html, IReadOnlyList<GraphOutboundAttachment> Attachments);

public interface ISignatureComposer
{
    Task<ComposedSignature?> ComposeForQueueAsync(Guid queueId, Guid senderUserId, bool isReply, CancellationToken ct);
    Task<ComposedSignature?> ComposeSystemAsync(bool isReply, CancellationToken ct);
}
