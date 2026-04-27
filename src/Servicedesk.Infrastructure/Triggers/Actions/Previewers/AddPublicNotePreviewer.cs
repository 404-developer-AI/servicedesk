using System.Text.Json;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class AddPublicNotePreviewer : ITriggerActionPreviewer
{
    private readonly ITriggerTemplateRenderer _renderer;

    public AddPublicNotePreviewer(ITriggerTemplateRenderer renderer) => _renderer = renderer;

    public string Kind => "add_public_note";

    public Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
        => Task.FromResult(NotePreview.Build(Kind, isInternal: false, actionJson, ctx, _renderer));
}
