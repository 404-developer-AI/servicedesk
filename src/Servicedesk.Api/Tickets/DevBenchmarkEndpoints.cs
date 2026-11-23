using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Surveys;
using Servicedesk.Infrastructure.TaggingMailboxes;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Servicedesk.Infrastructure.Triggers.FirstOpenGate;
using Servicedesk.Infrastructure.Workflow;

namespace Servicedesk.Api.Tickets;

public static class DevBenchmarkEndpoints
{

    /// Development-only seed + benchmark harness. Gated by the hosting
    /// environment so the routes literally don't exist in production
    /// builds. Used to prove list/search/detail stay fast at 1M rows.
    public static IEndpointRouteBuilder MapDevBenchmarkEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment env)
    {
        if (!env.IsDevelopment()) return app;

        var group = app.MapGroup("/api/dev")
            .WithTags("Dev")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPost("/seed-tickets", async (int? count, ITicketRepository repo, CancellationToken ct) =>
        {
            var n = Math.Clamp(count ?? 1000, 1, 1_000_000);
            var sw = Stopwatch.StartNew();
            var inserted = await repo.InsertFakeBatchAsync(n, ct);
            sw.Stop();
            return Results.Ok(new
            {
                requested = n,
                inserted,
                elapsedMs = sw.ElapsedMilliseconds,
                rowsPerSecond = inserted / Math.Max(1, sw.Elapsed.TotalSeconds),
            });
        }).WithName("DevSeedTickets");

        group.MapGet("/benchmarks", async (ITicketRepository repo, CancellationToken ct) =>
        {
            var results = new List<object>();

            async Task Measure(string label, Func<Task> action)
            {
                var sw = Stopwatch.StartNew();
                await action();
                sw.Stop();
                results.Add(new { label, elapsedMs = sw.Elapsed.TotalMilliseconds });
            }

            await Measure("list first 50 (all queues)", async () =>
            {
                await repo.SearchAsync(new TicketQuery(Limit: 50), VisibilityScope.All, null, null, ct);
            });

            await Measure("list open only 50", async () =>
            {
                await repo.SearchAsync(new TicketQuery(Limit: 50, OpenOnly: true), VisibilityScope.All, null, null, ct);
            });

            await Measure("search subject contains 'ticket'", async () =>
            {
                await repo.SearchAsync(new TicketQuery(Search: "ticket", Limit: 50), VisibilityScope.All, null, null, ct);
            });

            await Measure("open counts by queue", async () =>
            {
                await repo.GetOpenCountsByQueueAsync(ct);
            });

            return Results.Ok(new { benchmarks = results });
        }).WithName("DevBenchmarks");

        group.MapPost("/seed-agent", async (
            [FromQuery] string email,
            [FromQuery] string password,
            [FromServices] IPasswordHasher hasher,
            [FromServices] Npgsql.NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var hash = hasher.Hash(password);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var id = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(conn, new Dapper.CommandDefinition(
                """
                INSERT INTO users (email, password_hash, role_name)
                VALUES (@email, @hash, 'Agent')
                ON CONFLICT (email) DO NOTHING
                RETURNING id
                """,
                new { email, hash }, cancellationToken: ct));
            if (id == Guid.Empty)
                return Results.Conflict(new { error = "User already exists" });

            return Results.Ok(new { id, email, role = "Agent" });
        }).WithName("DevSeedAgent");

        return app;
    }
}
