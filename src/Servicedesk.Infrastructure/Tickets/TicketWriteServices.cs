using Microsoft.Extensions.DependencyInjection;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers.Actions;

namespace Servicedesk.Infrastructure.Tickets;

/// Registration of everything that writes ticket fields — the agent side
/// and the trigger side share one rule set and one write path, so they are
/// wired together here.
internal static class TicketWriteServices
{
    public static IServiceCollection AddTicketWriteServices(this IServiceCollection services)
    {
        // v0.0.102 — the shared agent-side mutation flow (precheck / apply /
        // publish) used by the single-ticket endpoints and bulk actions alike.
        services.AddScoped<ITicketMutationService, TicketMutationService>();
        services.AddScoped<ITicketBulkActionService, TicketBulkActionService>();
        services.AddSingleton<IQueueStatusScope, QueueStatusScope>();
        // Agent-supplied ticket HTML is sanitised at the API boundary
        // (defence in depth on top of DOMPurify at render).
        services.AddSingleton<ITicketHtmlSanitizer, TicketHtmlSanitizer>();

        // v0.1.65 (ROADMAP §2 C3) — one rule set and one field write for
        // agents and triggers. Singletons: the trigger handlers are too.
        services.AddSingleton<ITicketFieldRules, TicketFieldRules>();
        services.AddSingleton<TicketFieldWriter>();
        services.AddSingleton<TriggerFieldWrite>();
        // Trigger-only reminder writes (set_pending_till, scheduler clean-up).
        services.AddSingleton<SystemFieldMutator>();
        return services;
    }
}
