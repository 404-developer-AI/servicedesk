using Npgsql;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;

namespace Servicedesk.Api.Tests.Database;

/// The shared trigger field write over a real database: real writer, real
/// status scope, the real ticket repository. No checklist blocks (those have
/// their own tests), and a reporter that must never be reached.
internal static class TriggerWriteKit
{
    public static TriggerFieldWrite Create(NpgsqlDataSource ds, ISlaEngine sla, InMemorySettingsService? settings = null) =>
        new(
            new TicketFieldRules(
                new QueueStatusScope(new TaxonomyRepository(ds)),
                Fake<IChecklistCloseGuard>.Create((_, _) => Task.FromResult<IReadOnlyList<ChecklistBlocker>>(Array.Empty<ChecklistBlocker>())),
                settings ?? new InMemorySettingsService(),
                TimeProvider.System,
                new UserService(ds)),
            new TicketFieldWriter(ds),
            sla,
            Fake<IChecklistCloseBlockReporter>.Create((m, _) => throw new InvalidOperationException("Unexpected: " + m.Name)),
            new TriggerRepository(ds),
            new TicketRepository(ds));

    /// An SLA engine that accepts every hook (and records the calls when a list is given).
    public static ISlaEngine Sla(List<string>? calls = null) => Fake<ISlaEngine>.Create((method, _) =>
    {
        calls?.Add(method.Name);
        return method.ReturnType == typeof(Task)
            ? Task.CompletedTask
            : throw new NotImplementedException($"Unexpected call: {method.Name}");
    });
}
