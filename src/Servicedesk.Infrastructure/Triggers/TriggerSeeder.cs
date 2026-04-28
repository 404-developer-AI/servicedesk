using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Triggers;

/// v0.0.24 Blok 8 — seeds a single illustrative trigger so a fresh
/// install ships with a working example admins can adapt: an auto-reply
/// to the customer when a new ticket arrives by mail. The seed is
/// inserted **inactive** (<c>is_active = FALSE</c>) — it shows the
/// shape of conditions + actions + templates without changing customer-
/// facing behaviour out of the box. Admins flip the switch + tweak the
/// body to their tone.
///
/// Idempotency + upgradability: rows the seeder owns carry
/// <c>is_seed = TRUE</c>; on every boot we INSERT … ON CONFLICT
/// (name) DO UPDATE that refreshes <c>conditions</c> and <c>actions</c>
/// when (and only when) <c>is_seed</c> is still TRUE. The admin's first
/// edit through the API flips the flag to FALSE, after which the
/// seeder no longer touches the row even if the canonical content
/// changes in a later release. New installs receive whatever shape the
/// current build ships.
///
/// The "010 - " prefix follows the alphabetical-evaluation-order
/// convention (Zammad MVP convention from <c>ROADMAP.md</c> / Blok 2):
/// admins prefix triggers with 010-/020-/030- when they want fine
/// control over chain-evaluation ordering.
public sealed class TriggerSeeder : IHostedService
{
    private const string Name = "010 - Auto-reply on new ticket";
    private const string Description =
        "Sends a quick acknowledgement to the customer when a new ticket arrives by mail. " +
        "Off by default — review the body and toggle on once you're happy with the wording.";

    // Conditions: article.sender = "Customer" — keeps the auto-reply
    // from firing on agent comments or system notes. Admins typically
    // tighten this further (e.g. status = New) once they've reviewed it.
    private const string Conditions = """
        {
          "op": "AND",
          "items": [
            { "field": "article.sender", "operator": "is", "value": "Customer" }
          ]
        }
        """;

    // Send_mail to the customer with a templated subject + body. The
    // body is HTML-encoded by the templater (Html escape mode) — a
    // requester whose name happens to contain `<script>` stays inert.
    private const string Actions = """
        [
          {
            "kind": "send_mail",
            "to": "customer",
            "subject": "Re: #{ticket.subject} [##{ticket.number}]",
            "body_html": "<p>Hi #{ticket.customer.firstname},</p><p>Thanks for reaching out — we received your message and a colleague will follow up shortly.</p><p>Reference: <strong>##{ticket.number}</strong></p><p>Kind regards,<br/>The team</p>"
          }
        ]
        """;

    private static readonly string Sql = $$"""
        INSERT INTO triggers
            (name, description, is_active, activator_kind, activator_mode,
             conditions, actions, locale, timezone, note, is_seed)
        VALUES
            (@Name, @Description, FALSE, 'action', 'always',
             @Conditions::jsonb, @Actions::jsonb,
             NULL, NULL, '', TRUE)
        ON CONFLICT (name) DO UPDATE SET
            description = EXCLUDED.description,
            conditions  = EXCLUDED.conditions,
            actions     = EXCLUDED.actions,
            updated_utc = now()
            WHERE triggers.is_seed = TRUE
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<TriggerSeeder> _logger;

    public TriggerSeeder(NpgsqlDataSource dataSource, ILogger<TriggerSeeder> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await conn.ExecuteAsync(new CommandDefinition(Sql, new
        {
            Name,
            Description,
            Conditions,
            Actions,
        }, cancellationToken: cancellationToken));
        if (rows > 0)
            _logger.LogInformation("Seeded default trigger '{Name}' (inactive).", Name);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
