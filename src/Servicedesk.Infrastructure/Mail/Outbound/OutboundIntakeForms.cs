using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.IntakeForms;
using Servicedesk.Infrastructure.IntakeForms;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Outbound;

/// Intake Forms (v0.0.19) part of the outbound send, split out of
/// <see cref="OutboundMailService"/> (file-size budget): mints a token per
/// linked Draft form, embeds the public link in the body, and after Graph
/// accepted the mail flips each instance Draft → Sent.
internal sealed class OutboundIntakeForms
{
    private readonly IIntakeFormRepository _intakeForms;
    private readonly IIntakeFormTokenService _intakeTokens;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger;

    public OutboundIntakeForms(
        IIntakeFormRepository intakeForms, IIntakeFormTokenService intakeTokens,
        ISettingsService settings, ILogger logger)
    {
        _intakeForms = intakeForms;
        _intakeTokens = intakeTokens;
        _settings = settings;
        _logger = logger;
    }

    internal sealed record PreparedIntakeForm(
        Guid InstanceId,
        string TemplateName,
        string RawToken,
        byte[] TokenHash,
        byte[] TokenCipher,
        DateTime ExpiresUtc,
        string SentToEmail);

    internal sealed record IntakePreparationResult(
        string BodyHtml,
        IReadOnlyList<PreparedIntakeForm> Prepared);

    /// Mints tokens, swaps the `::`-mention chip placeholder in the body
    /// for a real anchor, and returns the list of prepared forms. The
    /// state-flip to Sent happens later (post-send) so a failed delivery
    /// doesn't leave orphan Sent rows.
    public async Task<IntakePreparationResult> PrepareAsync(
        OutboundMailRequest request, string bodyHtml, CancellationToken ct)
    {
        if (request.LinkedFormIds is not { Count: > 0 } ids)
            return new IntakePreparationResult(bodyHtml, Array.Empty<PreparedIntakeForm>());

        var baseUrl = (await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct))?.TrimEnd('/') ?? string.Empty;
        var expiryDays = Math.Max(1, await _settings.GetAsync<int>(SettingKeys.IntakeForms.DefaultExpiryDays, ct));
        var now = DateTime.UtcNow;
        var expiresUtc = now.AddDays(expiryDays);

        // "To" first. If no primary recipient (CC/BCC-only send), fall
        // back to the requester email so we still have an audit trail of
        // which address this link went to. Empty → empty string; UI
        // doesn't require a recipient to be present.
        var sentToEmail = request.To.Count > 0 ? request.To[0].Address : string.Empty;

        var prepared = new List<PreparedIntakeForm>(ids.Count);
        var mutated = bodyHtml;

        foreach (var instanceId in ids.Distinct())
        {
            var view = await _intakeForms.GetAgentViewAsync(request.TicketId, instanceId, ct);
            if (view is null)
            {
                _logger.LogWarning(
                    "LinkedFormId {InstanceId} does not belong to ticket {TicketId}; dropping from outbound mail.",
                    instanceId, request.TicketId);
                continue;
            }
            if (view.Instance.Status != IntakeFormStatus.Draft)
            {
                _logger.LogWarning(
                    "LinkedFormId {InstanceId} is not in Draft state (current={Status}); dropping.",
                    instanceId, view.Instance.Status);
                continue;
            }

            var (raw, hash, cipher) = _intakeTokens.Mint();
            var templateName = view.Template.Name;
            prepared.Add(new PreparedIntakeForm(instanceId, templateName, raw, hash, cipher, expiresUtc, sentToEmail));

            mutated = EmbedIntakeLink(mutated, instanceId, raw, templateName, baseUrl);
        }

        return new IntakePreparationResult(mutated, prepared);
    }

    /// Replace the Tiptap-emitted placeholder `<span data-intake-form="{id}">…</span>`
    /// with an anchor to the public form URL. If the placeholder is
    /// absent (agent linked forms without inline mention) we append a
    /// paragraph at the end — still delivers the link, doesn't alter the
    /// agent's carefully-crafted body.
    private static string EmbedIntakeLink(string bodyHtml, Guid instanceId, string rawToken, string templateName, string baseUrl)
    {
        var link = BuildIntakeUrl(baseUrl, rawToken);
        var encodedName = System.Net.WebUtility.HtmlEncode(templateName);
        var anchor = $"<a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">{encodedName}</a>";

        // Match either a self-closing or wrapping span that the Tiptap
        // intake-mention extension emits. We look for the data-attribute
        // specifically so we never touch unrelated markup.
        var marker = $"data-intake-form=\"{instanceId}\"";
        var markerIdx = bodyHtml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx >= 0)
        {
            // Walk back to the opening '<' of the containing element.
            var openIdx = bodyHtml.LastIndexOf('<', markerIdx);
            if (openIdx >= 0)
            {
                // Walk forward to the matching close '>'.
                var tagEndIdx = bodyHtml.IndexOf('>', markerIdx);
                if (tagEndIdx > openIdx)
                {
                    // Self-closing? (ends with "/>")
                    var isSelfClose = tagEndIdx > 0 && bodyHtml[tagEndIdx - 1] == '/';
                    if (isSelfClose)
                        return bodyHtml[..openIdx] + anchor + bodyHtml[(tagEndIdx + 1)..];

                    // Find the matching </span> (shallow — nested mentions
                    // aren't emitted by the editor).
                    var closeIdx = bodyHtml.IndexOf("</span>", tagEndIdx + 1, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx > tagEndIdx)
                        return bodyHtml[..openIdx] + anchor + bodyHtml[(closeIdx + "</span>".Length)..];
                }
            }
        }

        // No placeholder found → append a paragraph. Empty body → emit
        // a clean paragraph. Non-empty body → separate with a new line.
        var suffix = $"<p>{anchor}</p>";
        return string.IsNullOrWhiteSpace(bodyHtml) ? suffix : bodyHtml + suffix;
    }

    private static string BuildIntakeUrl(string baseUrl, string rawToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return $"/intake/{rawToken}";
        return $"{baseUrl}/intake/{rawToken}";
    }

    public async Task FinalizeAsync(
        OutboundMailRequest request, IReadOnlyList<PreparedIntakeForm> prepared, CancellationToken ct)
    {
        foreach (var p in prepared)
        {
            var metadata = JsonSerializer.Serialize(new
            {
                instanceId = p.InstanceId,
                templateName = p.TemplateName,
                expiresUtc = p.ExpiresUtc,
                sentToEmail = p.SentToEmail,
            });

            var sentEventId = await _intakeForms.SendDraftAsync(
                p.InstanceId, request.TicketId, request.AuthorUserId,
                p.TokenHash, p.TokenCipher, p.ExpiresUtc, p.SentToEmail,
                metadata, ct);

            if (sentEventId is null)
            {
                _logger.LogWarning(
                    "Intake form {InstanceId} on ticket {TicketId} could not be finalised — the Draft vanished between token mint and mail send. Link in the delivered mail will 404.",
                    p.InstanceId, request.TicketId);
            }
        }
    }
}
