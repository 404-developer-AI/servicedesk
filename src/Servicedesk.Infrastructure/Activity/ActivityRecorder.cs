using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Activity;

/// Dapper-backed writer for the agent activity feed. Resolves the
/// agent's role at write time (one JOIN cost we accept so the row
/// carries a self-contained snapshot — re-classifying an agent later
/// does not retroactively rewrite history). Silently skips rows for
/// Customer accounts because the feed is agent + admin only.
public sealed class ActivityRecorder : IActivityRecorder
{
    private const string InsertSql = """
        INSERT INTO agent_activity_events
            (agent_id, agent_role, event_type, entity_type, entity_id, entity_extra, summary, metadata)
        SELECT @AgentId, u.role_name, @EventType, @EntityType, @EntityId, @EntityExtra, @Summary, @MetadataJson::jsonb
        FROM users u
        WHERE u.id = @AgentId AND u.role_name <> 'Customer'
        RETURNING id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ActivityRecorder> _logger;

    public ActivityRecorder(NpgsqlDataSource dataSource, ILogger<ActivityRecorder> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.EventType))
        {
            throw new ArgumentException("EventType is required.", nameof(record));
        }
        if (record.AgentId == Guid.Empty)
        {
            return;
        }

        var metadataJson = record.Metadata is null
            ? "{}"
            : JsonSerializer.Serialize(record.Metadata);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                InsertSql,
                new
                {
                    record.AgentId,
                    record.EventType,
                    record.EntityType,
                    record.EntityId,
                    record.EntityExtra,
                    Summary = record.Summary ?? "",
                    MetadataJson = metadataJson,
                },
                cancellationToken: cancellationToken));
        }
        catch (Exception ex)
        {
            // Best-effort — never break the calling action because the
            // activity feed could not write. The audit log + ticket
            // history paths remain intact regardless.
            _logger.LogWarning(ex,
                "ActivityRecorder failed to persist event {EventType} for agent {AgentId}.",
                record.EventType, record.AgentId);
        }
    }
}
