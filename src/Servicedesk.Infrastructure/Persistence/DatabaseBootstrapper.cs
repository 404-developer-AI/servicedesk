using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence;

/// Idempotent schema bootstrap. Creates every table the app expects if it is
/// not already present, so a fresh dev database or a brand-new install is
/// immediately usable. Tables tracked here: <c>audit_log</c>, <c>settings</c>,
/// <c>data_protection_keys</c>, and (from v0.0.4) the auth tables
/// <c>roles</c>, <c>users</c>, <c>user_totp</c>, <c>user_recovery_codes</c>,
/// <c>user_sessions</c>.
/// <para>
/// This is intentionally not EF Core Migrations: rewriting a handful of tables
/// behind an ORM adds no value. The project switches to EF Migrations in
/// v0.0.5 when the ticket schema lands — see ARCHITECTURE.md.
/// </para>
public sealed class DatabaseBootstrapper : IHostedService
{
    private const string Sql = """
        CREATE EXTENSION IF NOT EXISTS citext;
        CREATE EXTENSION IF NOT EXISTS pgcrypto;

        CREATE TABLE IF NOT EXISTS audit_log (
            id              BIGSERIAL PRIMARY KEY,
            utc             TIMESTAMPTZ NOT NULL DEFAULT now(),
            actor           TEXT        NOT NULL,
            actor_role      TEXT        NOT NULL,
            event_type      TEXT        NOT NULL,
            target          TEXT        NULL,
            client_ip       TEXT        NULL,
            user_agent      TEXT        NULL,
            payload         JSONB       NOT NULL DEFAULT '{}'::jsonb,
            prev_hash       BYTEA       NOT NULL,
            entry_hash      BYTEA       NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_audit_log_utc_id ON audit_log (utc DESC, id DESC);
        CREATE INDEX IF NOT EXISTS ix_audit_log_event_type ON audit_log (event_type);
        CREATE INDEX IF NOT EXISTS ix_audit_log_actor ON audit_log (actor);

        CREATE TABLE IF NOT EXISTS settings (
            key             TEXT        PRIMARY KEY,
            value           TEXT        NOT NULL,
            value_type      TEXT        NOT NULL,
            category        TEXT        NOT NULL,
            description     TEXT        NOT NULL,
            default_value   TEXT        NOT NULL,
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS data_protection_keys (
            id              BIGSERIAL   PRIMARY KEY,
            friendly_name   TEXT        NOT NULL,
            nonce           BYTEA       NOT NULL,
            ciphertext      BYTEA       NOT NULL,
            tag             BYTEA       NOT NULL,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS roles (
            name            TEXT        PRIMARY KEY
        );
        INSERT INTO roles (name) VALUES ('Customer'), ('Agent'), ('Admin')
            ON CONFLICT (name) DO NOTHING;

        CREATE TABLE IF NOT EXISTS users (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            email               CITEXT      NOT NULL UNIQUE,
            password_hash       TEXT        NOT NULL,
            role_name           TEXT        NOT NULL REFERENCES roles(name),
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_login_utc      TIMESTAMPTZ NULL,
            failed_attempts     INTEGER     NOT NULL DEFAULT 0,
            lockout_until_utc   TIMESTAMPTZ NULL
        );

        CREATE TABLE IF NOT EXISTS user_totp (
            user_id             UUID        PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            secret_ciphertext   BYTEA       NOT NULL,
            enabled             BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS user_recovery_codes (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            user_id             UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            code_ciphertext     BYTEA       NOT NULL,
            used_utc            TIMESTAMPTZ NULL
        );

        CREATE INDEX IF NOT EXISTS ix_user_recovery_codes_user
            ON user_recovery_codes (user_id) WHERE used_utc IS NULL;

        CREATE TABLE IF NOT EXISTS user_sessions (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            user_id         UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_seen_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_utc     TIMESTAMPTZ NOT NULL,
            ip              TEXT        NULL,
            user_agent      TEXT        NULL,
            amr             TEXT        NOT NULL DEFAULT 'pwd',
            revoked_utc     TIMESTAMPTZ NULL
        );

        CREATE INDEX IF NOT EXISTS ix_user_sessions_active
            ON user_sessions (user_id) WHERE revoked_utc IS NULL;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabaseBootstrapper> _logger;

    public DatabaseBootstrapper(NpgsqlDataSource dataSource, ILogger<DatabaseBootstrapper> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation(
            "Database bootstrap complete (audit_log, settings, data_protection_keys, roles, users, user_totp, user_recovery_codes, user_sessions).");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
