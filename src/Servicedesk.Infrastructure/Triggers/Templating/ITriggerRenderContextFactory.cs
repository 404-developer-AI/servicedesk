namespace Servicedesk.Infrastructure.Triggers.Templating;

public interface ITriggerRenderContextFactory
{
    Task<TriggerRenderContext> BuildAsync(
        TriggerEvaluationContext ctx,
        string? triggerLocale,
        string? triggerTimeZone,
        CancellationToken ct);
}
