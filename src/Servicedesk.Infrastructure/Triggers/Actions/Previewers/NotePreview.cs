using System.Text.Json;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

/// Shared note-preview builder for <see cref="AddInternalNotePreviewer"/>
/// and <see cref="AddPublicNotePreviewer"/> — both have identical preview
/// shape (render template against context, return rendered body) and only
/// differ in the <c>isInternal</c> flag exposed to the UI.
internal static class NotePreview
{
    public static TriggerActionPreviewResult Build(
        string kind,
        bool isInternal,
        JsonElement actionJson,
        TriggerEvaluationContext ctx,
        ITriggerTemplateRenderer renderer)
    {
        var hasHtml = ActionJson.TryReadString(actionJson, "body_html", out var bodyHtml);
        var hasText = ActionJson.TryReadString(actionJson, "body_text", out var bodyText);
        if (!hasHtml && !hasText)
            return TriggerActionPreviewResult.Failed(kind, "Action requires 'body_html' or 'body_text'.");

        if (ctx.RenderContext is { } rc)
        {
            if (hasHtml) bodyHtml = renderer.Render(bodyHtml, TemplateEscapeMode.Html, rc);
            if (hasText) bodyText = renderer.Render(bodyText, TemplateEscapeMode.PlainText, rc);
        }

        return TriggerActionPreviewResult.WouldApply(kind, new
        {
            isInternal,
            bodyHtml = hasHtml ? bodyHtml : null,
            bodyText = hasText ? bodyText : null,
        });
    }
}
