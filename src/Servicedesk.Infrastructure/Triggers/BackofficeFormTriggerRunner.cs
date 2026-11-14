using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Forms;

namespace Servicedesk.Infrastructure.Triggers;

/// v0.1.54 — runs the action-trigger pass for a back-office form step
/// (submitted / handled / reopened). The pass carries the form event on its
/// change set, so only triggers with a <c>form.*</c> condition take part
/// (see <see cref="TriggerService"/>), and what happens on the ticket —
/// a note, a status or queue change, a bell notification — is entirely the
/// admin's trigger configuration.
public sealed class BackofficeFormTriggerRunner : IBackofficeFormTriggerRunner
{
    private readonly ITriggerService _triggers;
    private readonly NpgsqlDataSource _ds;

    public BackofficeFormTriggerRunner(ITriggerService triggers, NpgsqlDataSource ds)
    {
        _triggers = triggers;
        _ds = ds;
    }

    public async Task RunAsync(BackofficeFormEvent evt, CancellationToken ct)
    {
        // The form's latest note on the timeline — what a notify_agent bell
        // links to (the pass itself has no triggering article).
        long? anchor;
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        {
            anchor = await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT event_id FROM ticket_form_versions
                WHERE form_id = @formId AND event_id IS NOT NULL
                ORDER BY version DESC LIMIT 1
                """, new { formId = evt.FormId }, cancellationToken: ct));
        }

        var formEvent = new TriggerFormEvent(
            evt.Kind, evt.FormId, evt.TemplateId, evt.FormName,
            evt.SubmittedByUserId, evt.SubmittedByName, evt.ActorUserId,
            evt.HandledByName, evt.HandledNote, anchor);

        await _triggers.EvaluateAsync(
            evt.TicketId, ticketEventId: null, TriggerActivatorKind.Action,
            TriggerChangeSet.ForForm(formEvent), ct);
    }
}
