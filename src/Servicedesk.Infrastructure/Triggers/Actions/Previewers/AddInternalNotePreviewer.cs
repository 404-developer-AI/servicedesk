using System.Text.Json;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class AddInternalNotePreviewer : ITriggerActionPreviewer
{
    private readonly ITriggerTemplateRenderer _renderer;

    public AddInternalNotePreviewer(ITriggerTemplateRenderer renderer) => _renderer = renderer;

    public string Kind => "add_internal_note";

    public Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
        => Task.FromResult(NotePreview.Build(Kind, isInternal: true, actionJson, ctx, _renderer));
}
