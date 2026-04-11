using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence;

/// Idempotent schema bootstrap. Creates every table the app expects if it is
/// not already present, so a fresh dev database or a brand-new install is
/// immediately usable. Tables tracked here: <c>audit_log</c>, <c>settings</c>,
/// <c>data_protection_keys</c>, the auth tables (<c>roles</c>, <c>users</c>,
/// <c>user_totp</c>, <c>user_recovery_codes</c>, <c>user_sessions</c>), and
/// the v0.0.5 ticket domain (<c>queues</c>, <c>priorities</c>, <c>statuses</c>,
/// <c>categories</c>, <c>companies</c>, <c>company_domains</c>, <c>contacts</c>,
/// <c>tickets</c>, <c>ticket_bodies</c>, <c>ticket_events</c>).
/// <para>
/// This is intentionally not EF Core Migrations: single-tenant installs with
/// per-customer databases are better served by idempotent raw SQL than by a
/// migration history table. Schema changes are reviewed in-PR by diffing this
/// file. Dapper is used for every read/write path.
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

        -- ===================================================================
        -- v0.0.5 ticket domain
        -- ===================================================================

        CREATE TABLE IF NOT EXISTS queues (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            slug            CITEXT      NOT NULL UNIQUE,
            description     TEXT        NOT NULL DEFAULT '',
            color           TEXT        NOT NULL DEFAULT '#7c7cff',
            icon            TEXT        NOT NULL DEFAULT 'inbox',
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            is_system       BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS priorities (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            slug            CITEXT      NOT NULL UNIQUE,
            level           INTEGER     NOT NULL DEFAULT 0,
            color           TEXT        NOT NULL DEFAULT '#7c7cff',
            icon            TEXT        NOT NULL DEFAULT 'flag',
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            is_system       BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- state_category drives SLA/open-ticket logic regardless of custom
        -- display names. Enum values are validated at the API layer. Allowed:
        -- 'New', 'Open', 'Pending', 'Resolved', 'Closed'.
        CREATE TABLE IF NOT EXISTS statuses (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            slug            CITEXT      NOT NULL UNIQUE,
            state_category  TEXT        NOT NULL,
            color           TEXT        NOT NULL DEFAULT '#7c7cff',
            icon            TEXT        NOT NULL DEFAULT 'circle',
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            is_system       BOOLEAN     NOT NULL DEFAULT FALSE,
            is_default      BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_status_state_category
                CHECK (state_category IN ('New','Open','Pending','Resolved','Closed'))
        );

        CREATE TABLE IF NOT EXISTS categories (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            parent_id       UUID        NULL REFERENCES categories(id) ON DELETE RESTRICT,
            name            TEXT        NOT NULL,
            slug            CITEXT      NOT NULL UNIQUE,
            description     TEXT        NOT NULL DEFAULT '',
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            is_system       BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_categories_parent ON categories (parent_id);

        CREATE TABLE IF NOT EXISTS companies (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            description     TEXT        NOT NULL DEFAULT '',
            website         TEXT        NOT NULL DEFAULT '',
            phone           TEXT        NOT NULL DEFAULT '',
            address_line1   TEXT        NOT NULL DEFAULT '',
            address_line2   TEXT        NOT NULL DEFAULT '',
            city            TEXT        NOT NULL DEFAULT '',
            postal_code     TEXT        NOT NULL DEFAULT '',
            country         TEXT        NOT NULL DEFAULT '',
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS company_domains (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            company_id      UUID        NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            domain          CITEXT      NOT NULL UNIQUE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_company_domains_company ON company_domains (company_id);

        -- company_role: 'Member' or 'TicketManager' (portal visibility scope).
        CREATE TABLE IF NOT EXISTS contacts (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            company_id      UUID        NULL REFERENCES companies(id) ON DELETE SET NULL,
            company_role    TEXT        NOT NULL DEFAULT 'Member',
            first_name      TEXT        NOT NULL DEFAULT '',
            last_name       TEXT        NOT NULL DEFAULT '',
            email           CITEXT      NOT NULL UNIQUE,
            phone           TEXT        NOT NULL DEFAULT '',
            job_title       TEXT        NOT NULL DEFAULT '',
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_contacts_role
                CHECK (company_role IN ('Member','TicketManager'))
        );

        CREATE INDEX IF NOT EXISTS ix_contacts_company ON contacts (company_id);

        -- Monotonic human-readable ticket numbers, independent of uuid PKs.
        CREATE SEQUENCE IF NOT EXISTS ticket_number_seq START WITH 1000 INCREMENT BY 1;

        CREATE TABLE IF NOT EXISTS tickets (
            id                      UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            number                  BIGINT      NOT NULL UNIQUE DEFAULT nextval('ticket_number_seq'),
            subject                 TEXT        NOT NULL,
            requester_contact_id    UUID        NOT NULL REFERENCES contacts(id) ON DELETE RESTRICT,
            assignee_user_id        UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            queue_id                UUID        NOT NULL REFERENCES queues(id) ON DELETE RESTRICT,
            status_id               UUID        NOT NULL REFERENCES statuses(id) ON DELETE RESTRICT,
            priority_id             UUID        NOT NULL REFERENCES priorities(id) ON DELETE RESTRICT,
            category_id             UUID        NULL REFERENCES categories(id) ON DELETE SET NULL,
            source                  TEXT        NOT NULL DEFAULT 'Web',
            external_ref            TEXT        NULL,
            created_utc             TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc             TIMESTAMPTZ NOT NULL DEFAULT now(),
            due_utc                 TIMESTAMPTZ NULL,
            first_response_utc      TIMESTAMPTZ NULL,
            resolved_utc            TIMESTAMPTZ NULL,
            closed_utc              TIMESTAMPTZ NULL,
            is_deleted              BOOLEAN     NOT NULL DEFAULT FALSE,
            search_vector           TSVECTOR    GENERATED ALWAYS AS (to_tsvector('simple', subject)) STORED,
            CONSTRAINT chk_ticket_source
                CHECK (source IN ('Web','Mail','Api','System'))
        );

        -- Hot path: list by queue+status sorted by recency.
        CREATE INDEX IF NOT EXISTS ix_tickets_queue_status_updated
            ON tickets (queue_id, status_id, updated_utc DESC, id DESC)
            WHERE is_deleted = FALSE;

        -- Hot path: agent's own queue.
        CREATE INDEX IF NOT EXISTS ix_tickets_assignee_status
            ON tickets (assignee_user_id, status_id)
            WHERE is_deleted = FALSE AND assignee_user_id IS NOT NULL;

        -- Hot path: "all open tickets" dashboard. A partial index excluding
        -- closed/resolved keeps the index ~10x smaller than a full table scan
        -- once the dataset hits 100K+.
        CREATE INDEX IF NOT EXISTS ix_tickets_open_updated
            ON tickets (updated_utc DESC, id DESC)
            WHERE is_deleted = FALSE AND closed_utc IS NULL AND resolved_utc IS NULL;

        CREATE INDEX IF NOT EXISTS ix_tickets_requester
            ON tickets (requester_contact_id)
            WHERE is_deleted = FALSE;

        CREATE INDEX IF NOT EXISTS ix_tickets_search
            ON tickets USING GIN (search_vector);

        -- Large text lives in its own table so the hot list index doesn't have
        -- to scan it. One-to-one with tickets.
        CREATE TABLE IF NOT EXISTS ticket_bodies (
            ticket_id       UUID        PRIMARY KEY REFERENCES tickets(id) ON DELETE CASCADE,
            body_text       TEXT        NOT NULL DEFAULT '',
            body_html       TEXT        NULL,
            body_search     TSVECTOR    GENERATED ALWAYS AS (to_tsvector('simple', body_text)) STORED
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_bodies_search
            ON ticket_bodies USING GIN (body_search);

        -- Append-only event stream: every mail, comment, note, status change,
        -- assignment change, etc. event_type is validated at the API layer.
        CREATE TABLE IF NOT EXISTS ticket_events (
            id                  BIGSERIAL   PRIMARY KEY,
            ticket_id           UUID        NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            event_type          TEXT        NOT NULL,
            author_user_id      UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            author_contact_id   UUID        NULL REFERENCES contacts(id) ON DELETE SET NULL,
            body_text           TEXT        NULL,
            body_html           TEXT        NULL,
            metadata            JSONB       NOT NULL DEFAULT '{}'::jsonb,
            is_internal         BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_ticket_event_type
                CHECK (event_type IN ('Created','Comment','Mail','Note','StatusChange',
                                      'AssignmentChange','PriorityChange','QueueChange',
                                      'CategoryChange','SystemNote'))
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_events_ticket_created
            ON ticket_events (ticket_id, created_utc DESC, id DESC);
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
            "Database bootstrap complete (audit + auth + ticket domain).");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
