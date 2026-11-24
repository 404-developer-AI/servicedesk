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

/// The shared ticket write path over a real database: real writer, real
/// status scope, the real ticket repository. No checklist blocks unless a
/// guard is passed (the close block has its own tests), and a reporter that
/// must never be reached.
internal static class TriggerWriteKit
{
    public static readonly IChecklistCloseGuard NoBlockers =
        Fake<IChecklistCloseGuard>.Create((_, _) => Task.FromResult<IReadOnlyList<ChecklistBlocker>>(Array.Empty<ChecklistBlocker>()));

    public static TicketFieldWriter Writer(NpgsqlDataSource ds, IChecklistCloseGuard? guard = null) =>
        new(ds, guard ?? NoBlockers);

    public static TicketRepository Tickets(NpgsqlDataSource ds, IChecklistCloseGuard? guard = null) =>
        new(ds, Writer(ds, guard));

    public static TriggerFieldWrite Create(
        NpgsqlDataSource ds, ISlaEngine sla, InMemorySettingsService? settings = null,
        IChecklistCloseGuard? writerGuard = null, IChecklistCloseBlockReporter? reporter = null) =>
        new(
            new TicketFieldRules(
                new QueueStatusScope(new TaxonomyRepository(ds)),
                NoBlockers,
                settings ?? new InMemorySettingsService(),
                TimeProvider.System,
                new UserService(ds)),
            Writer(ds, writerGuard),
            sla,
            reporter ?? Fake<IChecklistCloseBlockReporter>.Create((m, _) => throw new InvalidOperationException("Unexpected: " + m.Name)),
            new TriggerRepository(ds),
            Tickets(ds));

    /// An SLA engine that accepts every hook (and records the calls when a list is given).
    public static ISlaEngine Sla(List<string>? calls = null) => Fake<ISlaEngine>.Create((method, _) =>
    {
        calls?.Add(method.Name);
        return method.ReturnType == typeof(Task)
            ? Task.CompletedTask
            : throw new NotImplementedException($"Unexpected call: {method.Name}");
    });
}
