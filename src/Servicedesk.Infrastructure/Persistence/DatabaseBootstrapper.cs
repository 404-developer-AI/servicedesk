using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence;

/// Idempotent schema bootstrap. Creates every table the app expects if it is
/// not already present, so a fresh dev database or a brand-new install is
/// immediately usable. Tables tracked here: <c>audit_log</c>, <c>settings</c>,
/// <c>data_protection_keys</c>, the auth tables (<c>roles</c>, <c>users</c>,
/// <c>user_totp</c>, <c>user_recovery_codes</c>, <c>user_sessions</c>), the
/// v0.0.5 ticket domain (<c>queues</c>, <c>priorities</c>, <c>statuses</c>,
/// <c>categories</c>, <c>companies</c>, <c>company_domains</c>, <c>contacts</c>,
/// <c>tickets</c>, <c>ticket_bodies</c>, <c>ticket_events</c>), the v0.0.6
/// saved views (<c>views</c>), and the v0.0.7 access control tables
/// (<c>user_queue_access</c>, <c>view_groups</c>, <c>view_group_members</c>,
/// <c>view_group_views</c>, <c>user_view_access</c>), and the per-user
/// preference store (<c>user_preferences</c>).
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
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
        CREATE EXTENSION IF NOT EXISTS unaccent;

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
        -- Contact↔Company relationships live in contact_companies (v0.0.9 step 2);
        -- the historical direct company_id FK was dropped as part of that change.
        CREATE TABLE IF NOT EXISTS contacts (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
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

        -- v0.0.9 step 3: company-resolution state frozen on the ticket.
        -- company_id decouples "the ticket's company" from "the requester's
        -- current primary" so moving a contact's primary later doesn't
        -- retroactively reassign historical tickets (supports the jobwissel
        -- flow in ToDo #5). company_resolved_via records which branch of the
        -- mail-intake decision tree picked this company (or 'manual' when an
        -- agent later assigns it). awaiting_company_assignment signals the UI
        -- to prompt for a manual pick when intake couldn't resolve.
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS company_id UUID NULL
                REFERENCES companies(id) ON DELETE SET NULL,
            ADD COLUMN IF NOT EXISTS awaiting_company_assignment BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS company_resolved_via TEXT NULL;

        -- Postgres has no IF NOT EXISTS for CHECK constraints — guard via pg_constraint.
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_ticket_resolved_via') THEN
                ALTER TABLE tickets
                    ADD CONSTRAINT chk_ticket_resolved_via
                    CHECK (company_resolved_via IS NULL
                           OR company_resolved_via IN ('thread_reply','primary','secondary','manual','unresolved'));
            END IF;
        END $$;

        -- One-time backfill (idempotent: filters out rows where company_id is
        -- already set). Populates historical tickets from the requester's
        -- current primary link so the UI keeps showing the same company it
        -- used to derive on-the-fly. Tickets whose requester has no primary
        -- stay NULL and will show "no company" — unchanged from before.
        --
        -- Guarded: contact_companies is created further down this script (it
        -- arrived in v0.0.9 step 2). On a fresh install the table doesn't
        -- exist yet on first read, but there are no historical tickets to
        -- backfill either — so we skip the UPDATE until a subsequent run
        -- (when contact_companies is present) picks it up.
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables
                       WHERE table_schema = 'public' AND table_name = 'contact_companies') THEN
                UPDATE tickets t
                SET company_id = cc.company_id,
                    company_resolved_via = 'primary'
                FROM contact_companies cc
                WHERE cc.contact_id = t.requester_contact_id
                  AND cc.role = 'primary'
                  AND t.company_id IS NULL;
            END IF;
        END $$;

        CREATE INDEX IF NOT EXISTS ix_tickets_company
            ON tickets (company_id)
            WHERE is_deleted = FALSE AND company_id IS NOT NULL;

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
                                      'CategoryChange','SystemNote','MailReceived',
                                      'MailSent','CompanyAssignment','RequesterChange'))
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_events_ticket_created
            ON ticket_events (ticket_id, created_utc DESC, id DESC);

        -- Columns added post-v0.0.6: track whether an event has been edited.
        ALTER TABLE ticket_events
            ADD COLUMN IF NOT EXISTS edited_utc          TIMESTAMPTZ NULL,
            ADD COLUMN IF NOT EXISTS edited_by_user_id   UUID        NULL REFERENCES users(id) ON DELETE SET NULL;

        -- Revision history for edited events.
        -- Stores the OLD values before each edit; current values live on the event row.
        CREATE TABLE IF NOT EXISTS ticket_event_revisions (
            id                  BIGSERIAL       PRIMARY KEY,
            event_id            BIGINT          NOT NULL REFERENCES ticket_events(id) ON DELETE CASCADE,
            revision_number     INT             NOT NULL,
            body_text_before    TEXT            NULL,
            body_html_before    TEXT            NULL,
            is_internal_before  BOOLEAN         NOT NULL,
            edited_by_user_id   UUID            NOT NULL REFERENCES users(id),
            edited_utc          TIMESTAMPTZ     NOT NULL DEFAULT now(),
            CONSTRAINT uq_event_revision UNIQUE (event_id, revision_number)
        );

        CREATE INDEX IF NOT EXISTS ix_event_revisions_event_id
            ON ticket_event_revisions (event_id, revision_number);

        -- ===================================================================
        -- v0.0.6 saved views
        -- ===================================================================

        CREATE TABLE IF NOT EXISTS views (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            user_id         UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            name            TEXT        NOT NULL,
            filters         JSONB       NOT NULL DEFAULT '{}'::jsonb,
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            is_shared       BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_views_user ON views (user_id, sort_order);

        ALTER TABLE views ADD COLUMN IF NOT EXISTS columns TEXT NULL;

        -- ===================================================================
        -- v0.0.7 access control: queue access + view groups
        -- ===================================================================

        -- Many-to-many: which users (agents) can access which queues.
        -- Admins bypass this table entirely (god-mode in service layer).
        CREATE TABLE IF NOT EXISTS user_queue_access (
            user_id     UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            queue_id    UUID        NOT NULL REFERENCES queues(id) ON DELETE CASCADE,
            created_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, queue_id)
        );

        CREATE INDEX IF NOT EXISTS ix_user_queue_access_queue
            ON user_queue_access (queue_id);

        -- Admin-managed groupings that bundle views + agents together.
        CREATE TABLE IF NOT EXISTS view_groups (
            id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name        TEXT        NOT NULL,
            description TEXT        NOT NULL DEFAULT '',
            sort_order  INTEGER     NOT NULL DEFAULT 0,
            created_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Agents assigned to a view group.
        CREATE TABLE IF NOT EXISTS view_group_members (
            view_group_id UUID NOT NULL REFERENCES view_groups(id) ON DELETE CASCADE,
            user_id       UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            created_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (view_group_id, user_id)
        );

        CREATE INDEX IF NOT EXISTS ix_view_group_members_user
            ON view_group_members (user_id);

        -- Views assigned to a view group.
        CREATE TABLE IF NOT EXISTS view_group_views (
            view_group_id UUID NOT NULL REFERENCES view_groups(id) ON DELETE CASCADE,
            view_id       UUID NOT NULL REFERENCES views(id) ON DELETE CASCADE,
            sort_order    INTEGER NOT NULL DEFAULT 0,
            created_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (view_group_id, view_id)
        );

        CREATE INDEX IF NOT EXISTS ix_view_group_views_view
            ON view_group_views (view_id);

        -- Direct view-to-agent assignment (bypass groups).
        CREATE TABLE IF NOT EXISTS user_view_access (
            user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            view_id     UUID NOT NULL REFERENCES views(id) ON DELETE CASCADE,
            created_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, view_id)
        );

        CREATE INDEX IF NOT EXISTS ix_user_view_access_view
            ON user_view_access (view_id);

        -- ===================================================================
        -- User preferences (per-user key-value store)
        -- ===================================================================

        CREATE TABLE IF NOT EXISTS user_preferences (
            user_id     UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            pref_key    TEXT        NOT NULL,
            pref_value  TEXT        NOT NULL,
            updated_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, pref_key)
        );

        -- v0.0.8: user-defined priorities with default flag
        ALTER TABLE priorities
            ADD COLUMN IF NOT EXISTS is_default BOOLEAN NOT NULL DEFAULT FALSE;

        -- v0.0.9: pinned events
        CREATE TABLE IF NOT EXISTS ticket_event_pins (
            id                  BIGSERIAL       PRIMARY KEY,
            event_id            BIGINT          NOT NULL REFERENCES ticket_events(id) ON DELETE CASCADE,
            ticket_id           UUID            NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            pinned_by_user_id   UUID            NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            remark              TEXT            NOT NULL DEFAULT '',
            created_utc         TIMESTAMPTZ     NOT NULL DEFAULT now(),
            CONSTRAINT uq_event_pin UNIQUE (event_id)
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_event_pins_ticket
            ON ticket_event_pins (ticket_id, created_utc);

        -- v0.1.0: view display config (sorting, grouping, priority float)
        ALTER TABLE views ADD COLUMN IF NOT EXISTS display_config JSONB NOT NULL DEFAULT '{}'::jsonb;

        -- v0.1.0: indexes for dynamic sort patterns
        CREATE INDEX IF NOT EXISTS ix_tickets_created_id
            ON tickets (created_utc DESC, id DESC) WHERE is_deleted = FALSE;
        CREATE INDEX IF NOT EXISTS ix_tickets_due_id
            ON tickets (due_utc DESC NULLS LAST, id DESC) WHERE is_deleted = FALSE;

        -- ===================================================================
        -- v0.0.8 mail intake — schema only. No consumers yet; foundation for
        -- the Graph polling loop, mail→ticket conversion, attachment pipeline,
        -- FTS search, and the disk-monitoring sampler that land in later steps.
        -- See ADR-001 in plans/ for the design rationale.
        -- ===================================================================

        -- One row per unique inbound mail. Dedup on RFC-5322 Message-ID so
        -- re-delivery (Graph webhooks + polling fallback) cannot duplicate.
        CREATE TABLE IF NOT EXISTS mail_messages (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            message_id          TEXT        NOT NULL UNIQUE,
            in_reply_to         TEXT        NULL,
            references_header   TEXT        NULL,
            from_address        CITEXT      NOT NULL,
            from_name           TEXT        NOT NULL DEFAULT '',
            to_addresses        JSONB       NOT NULL DEFAULT '[]'::jsonb,
            cc_addresses        JSONB       NOT NULL DEFAULT '[]'::jsonb,
            subject             TEXT        NOT NULL DEFAULT '',
            mailbox_address     CITEXT      NOT NULL,
            received_utc        TIMESTAMPTZ NOT NULL,
            raw_eml_blob_hash   TEXT        NULL,
            ticket_id           UUID        NULL REFERENCES tickets(id) ON DELETE SET NULL,
            ticket_event_id     BIGINT      NULL REFERENCES ticket_events(id) ON DELETE SET NULL,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_mail_messages_received
            ON mail_messages (received_utc DESC, id DESC);
        CREATE INDEX IF NOT EXISTS ix_mail_messages_ticket
            ON mail_messages (ticket_id) WHERE ticket_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_mail_messages_in_reply_to
            ON mail_messages (in_reply_to) WHERE in_reply_to IS NOT NULL;

        -- Content-addressed attachment metadata. The bytes live on disk via
        -- IBlobStore, keyed by content_hash (SHA-256 hex). Dedup is
        -- filesystem-driven: two rows can share the same content_hash.
        CREATE TABLE IF NOT EXISTS attachments (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            content_hash        TEXT        NOT NULL,
            size_bytes          BIGINT      NOT NULL,
            mime_type           TEXT        NOT NULL DEFAULT 'application/octet-stream',
            original_filename   TEXT        NOT NULL DEFAULT '',
            owner_kind          TEXT        NOT NULL,
            owner_id            UUID        NOT NULL,
            is_inline           BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_attachments_owner_kind
                CHECK (owner_kind IN ('Mail','Ticket','User'))
        );

        CREATE INDEX IF NOT EXISTS ix_attachments_content_hash
            ON attachments (content_hash);
        CREATE INDEX IF NOT EXISTS ix_attachments_owner
            ON attachments (owner_kind, owner_id);

        -- Attachment-pipeline state machine. Durable queue backed by Postgres
        -- (no Redis). Workers claim rows via SKIP LOCKED in step 5.
        CREATE TABLE IF NOT EXISTS attachment_jobs (
            id                  BIGSERIAL   PRIMARY KEY,
            kind                TEXT        NOT NULL,
            state               TEXT        NOT NULL DEFAULT 'Pending',
            payload             JSONB       NOT NULL DEFAULT '{}'::jsonb,
            next_attempt_utc    TIMESTAMPTZ NOT NULL DEFAULT now(),
            attempt_count       INTEGER     NOT NULL DEFAULT 0,
            last_error          TEXT        NULL,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_attachment_jobs_kind
                CHECK (kind IN ('Ingest','ExtractText','Scan','Cleanup')),
            CONSTRAINT chk_attachment_jobs_state
                CHECK (state IN ('Pending','Running','Succeeded','Failed','DeadLettered'))
        );

        -- Hot path for the worker: next pending job by schedule.
        CREATE INDEX IF NOT EXISTS ix_attachment_jobs_pending
            ON attachment_jobs (next_attempt_utc, id)
            WHERE state = 'Pending';
        -- Cleanup path: find completed/dead-lettered rows past their retention.
        CREATE INDEX IF NOT EXISTS ix_attachment_jobs_state_updated
            ON attachment_jobs (state, updated_utc);

        -- Append-only audit of every job attempt. One row per try, even on
        -- success, so we can reconstruct retry history and measure durations.
        CREATE TABLE IF NOT EXISTS attachment_job_attempts (
            id              BIGSERIAL   PRIMARY KEY,
            job_id          BIGINT      NOT NULL REFERENCES attachment_jobs(id) ON DELETE CASCADE,
            started_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            finished_utc    TIMESTAMPTZ NULL,
            outcome         TEXT        NULL,
            error_message   TEXT        NULL,
            error_class     TEXT        NULL,
            duration_ms     INTEGER     NULL,
            CONSTRAINT chk_attachment_job_attempts_outcome
                CHECK (outcome IS NULL OR outcome IN ('Succeeded','Failed','Canceled'))
        );

        CREATE INDEX IF NOT EXISTS ix_attachment_job_attempts_job
            ON attachment_job_attempts (job_id, started_utc DESC);

        -- FTS sidecar for ticket_events. normalized_text is the indexable
        -- body (quoted reply history stripped, inline images removed). Kept
        -- separate from ticket_events.body_text so the raw event is never
        -- mutated just to tweak the search index.
        CREATE TABLE IF NOT EXISTS ticket_event_search (
            event_id        BIGINT      PRIMARY KEY REFERENCES ticket_events(id) ON DELETE CASCADE,
            ticket_id       UUID        NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            normalized_text TEXT        NOT NULL DEFAULT '',
            search_vector   TSVECTOR    GENERATED ALWAYS AS (to_tsvector('simple', normalized_text)) STORED
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_event_search_vector
            ON ticket_event_search USING GIN (search_vector);
        CREATE INDEX IF NOT EXISTS ix_ticket_event_search_ticket
            ON ticket_event_search (ticket_id);

        -- Periodic disk snapshots for the admin blob-usage graph and the
        -- warn/critical thresholds (Storage.BlobDiskWarnPercent /
        -- BlobDiskCriticalPercent). Sampler BackgroundService arrives later.
        CREATE TABLE IF NOT EXISTS blob_disk_samples (
            id              BIGSERIAL       PRIMARY KEY,
            sampled_utc     TIMESTAMPTZ     NOT NULL DEFAULT now(),
            root_path       TEXT            NOT NULL,
            total_bytes     BIGINT          NOT NULL,
            free_bytes      BIGINT          NOT NULL,
            used_percent    NUMERIC(5,2)    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_blob_disk_samples_sampled
            ON blob_disk_samples (sampled_utc DESC);

        -- ===================================================================
        -- v0.0.8 step 4: per-queue mailboxes + polling state
        -- ===================================================================

        ALTER TABLE queues
            ADD COLUMN IF NOT EXISTS inbound_mailbox_address  CITEXT NULL,
            ADD COLUMN IF NOT EXISTS outbound_mailbox_address CITEXT NULL;

        -- Each inbound mailbox routes to exactly one queue. Partial unique
        -- index so multiple queues with NULL inbound don't collide.
        CREATE UNIQUE INDEX IF NOT EXISTS ix_queues_inbound_mailbox
            ON queues (inbound_mailbox_address)
            WHERE inbound_mailbox_address IS NOT NULL;

        -- Per-queue Graph delta cursor + health state for the polling loop.
        CREATE TABLE IF NOT EXISTS mail_poll_state (
            queue_id              UUID        PRIMARY KEY REFERENCES queues(id) ON DELETE CASCADE,
            delta_link            TEXT        NULL,
            last_polled_utc       TIMESTAMPTZ NULL,
            last_error            TEXT        NULL,
            consecutive_failures  INTEGER     NOT NULL DEFAULT 0,
            updated_utc           TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_mail_poll_state_last_polled
            ON mail_poll_state (last_polled_utc);

        -- Encrypted key-value store for runtime-editable secrets (e.g. Graph
        -- client secret). Values are protected with IDataProtectionProvider
        -- under purpose "Servicedesk.ProtectedSecrets"; plaintext never hits
        -- the DB or logs.
        CREATE TABLE IF NOT EXISTS protected_secrets (
            key             TEXT        PRIMARY KEY,
            value_protected TEXT        NOT NULL,
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- ===================================================================
        -- v0.0.8 step 6: mail → ticket ingest
        -- ===================================================================

        ALTER TABLE mail_messages
            ADD COLUMN IF NOT EXISTS body_text           TEXT NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS body_html_blob_hash TEXT NULL,
            ADD COLUMN IF NOT EXISTS graph_message_id    TEXT NULL,
            ADD COLUMN IF NOT EXISTS mailbox_moved_utc   TIMESTAMPTZ NULL;

        -- Partial index for the finalizer sweeper: find mails that have been
        -- ingest-attached to a ticket but not yet moved out of the Inbox. We
        -- intentionally do NOT index all rows — the vast majority will be
        -- already-moved and irrelevant to this hot path.
        CREATE INDEX IF NOT EXISTS ix_mail_messages_awaiting_move
            ON mail_messages (received_utc)
            WHERE mailbox_moved_utc IS NULL AND ticket_id IS NOT NULL;

        ALTER TABLE mail_poll_state
            ADD COLUMN IF NOT EXISTS processed_folder_id TEXT NULL,
            ADD COLUMN IF NOT EXISTS last_mailbox_action_error TEXT NULL,
            ADD COLUMN IF NOT EXISTS last_mailbox_action_error_utc TIMESTAMPTZ NULL;

        CREATE TABLE IF NOT EXISTS mail_recipients (
            id              BIGSERIAL   PRIMARY KEY,
            mail_id         UUID        NOT NULL REFERENCES mail_messages(id) ON DELETE CASCADE,
            kind            TEXT        NOT NULL,
            address         CITEXT      NOT NULL,
            display_name    TEXT        NOT NULL DEFAULT '',
            CONSTRAINT chk_mail_recipients_kind CHECK (kind IN ('to','cc','bcc'))
        );

        CREATE INDEX IF NOT EXISTS ix_mail_recipients_mail ON mail_recipients (mail_id);
        CREATE INDEX IF NOT EXISTS ix_mail_recipients_address ON mail_recipients (address);

        -- Extend ticket_events CHECK to allow MailReceived (distinct from the
        -- legacy 'Mail' outbound/reply event type) and CompanyAssignment
        -- (v0.0.9 ToDo #4 manual company-assignment timeline event).
        --
        -- NOT VALID grandfathers any pre-existing row whose event_type is
        -- outside the whitelist (old dev fixtures, manually-inserted debug
        -- rows, data migrated from an earlier prototype) so an upgrade boots
        -- on brownfield installs. The CHECK still rejects all *new*
        -- INSERT/UPDATE, which is the invariant we actually care about —
        -- once legacy rows age out there's nothing to clean up.
        ALTER TABLE ticket_events DROP CONSTRAINT IF EXISTS chk_ticket_event_type;
        ALTER TABLE ticket_events ADD CONSTRAINT chk_ticket_event_type
            CHECK (event_type IN ('Created','Comment','Mail','Note','StatusChange',
                                  'AssignmentChange','PriorityChange','QueueChange',
                                  'CategoryChange','SystemNote','MailReceived',
                                  'CompanyAssignment')) NOT VALID;

        -- ===================================================================
        -- v0.0.8 step 6b: attachments pipeline
        -- ===================================================================

        -- content_hash is populated async by the worker after the blob has been
        -- stored; allow NULL during the Pending window.
        ALTER TABLE attachments ALTER COLUMN content_hash DROP NOT NULL;

        -- MIME Content-ID for inline images; populated for inline attachments so
        -- the timeline renderer can rewrite `cid:<id>` references to download URLs.
        ALTER TABLE attachments ADD COLUMN IF NOT EXISTS content_id TEXT NULL;

        -- Per-attachment lifecycle, independent of the job row (jobs are pruned by
        -- retention; attachments persist for the lifetime of the ticket).
        -- Existing rows (none in practice pre-6b) default to 'Ready'; new rows
        -- from the ingest path start at 'Pending' and are promoted by the worker.
        ALTER TABLE attachments
            ADD COLUMN IF NOT EXISTS processing_state TEXT NOT NULL DEFAULT 'Ready';

        ALTER TABLE attachments DROP CONSTRAINT IF EXISTS chk_attachments_processing_state;
        ALTER TABLE attachments ADD CONSTRAINT chk_attachments_processing_state
            CHECK (processing_state IN ('Pending','Stored','Ready','Failed'));

        -- Hot path for the worker: find pending attachments by age.
        CREATE INDEX IF NOT EXISTS ix_attachments_pending
            ON attachments (created_utc)
            WHERE processing_state = 'Pending';

        -- v0.0.12 step 2: user-uploaded attachments on Notes / Comments. The
        -- attachment row carries owner_kind='Ticket' while the upload is
        -- staged (no post submitted yet); on submit the API stamps event_id
        -- so the timeline-enricher can look up the strip per-event without a
        -- separate join table. Mail-owned rows leave event_id NULL —
        -- inbound/outbound mail attachments still resolve via
        -- (owner_kind='Mail', owner_id=mail_message_id).
        ALTER TABLE attachments ADD COLUMN IF NOT EXISTS event_id BIGINT NULL
            REFERENCES ticket_events(id) ON DELETE CASCADE;

        CREATE INDEX IF NOT EXISTS ix_attachments_event
            ON attachments (event_id)
            WHERE event_id IS NOT NULL;

        -- Extend attachment_jobs state CHECK with 'Cancelled' so an admin can
        -- dismiss dead-lettered jobs from the Health page without losing the
        -- attempt history (attempts stay; the job row flips to terminal state).
        ALTER TABLE attachment_jobs DROP CONSTRAINT IF EXISTS chk_attachment_jobs_state;
        ALTER TABLE attachment_jobs ADD CONSTRAINT chk_attachment_jobs_state
            CHECK (state IN ('Pending','Running','Succeeded','Failed','DeadLettered','Cancelled'));

        -- ===================================================================
        -- Observability — incident log (Warning/Critical events captured from
        -- Serilog sinks and surfaced on /settings/health until acknowledged).
        -- Dedup: identical (subsystem, severity, message) within the last 60s
        -- bumps occurrence_count on the existing open row instead of inserting
        -- a new one, so retry storms do not flood the table.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS incidents (
            id                      BIGSERIAL      PRIMARY KEY,
            subsystem               TEXT           NOT NULL,
            severity                TEXT           NOT NULL,
            message                 TEXT           NOT NULL,
            details                 TEXT           NULL,
            context                 JSONB          NOT NULL DEFAULT '{}'::jsonb,
            first_occurred_utc      TIMESTAMPTZ    NOT NULL DEFAULT now(),
            last_occurred_utc       TIMESTAMPTZ    NOT NULL DEFAULT now(),
            occurrence_count        INTEGER        NOT NULL DEFAULT 1,
            acknowledged_utc        TIMESTAMPTZ    NULL,
            acknowledged_by_user_id UUID           NULL REFERENCES users(id) ON DELETE SET NULL,
            CONSTRAINT chk_incidents_severity CHECK (severity IN ('Warning','Critical'))
        );

        CREATE INDEX IF NOT EXISTS ix_incidents_open
            ON incidents (subsystem, severity)
            WHERE acknowledged_utc IS NULL;
        CREATE INDEX IF NOT EXISTS ix_incidents_last_occurred
            ON incidents (last_occurred_utc DESC);

        -- ===================================================================
        -- v0.1.1 SLA engine — business hours, holidays, policies, per-ticket state
        -- ===================================================================

        CREATE TABLE IF NOT EXISTS business_hours_schemas (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            timezone        TEXT        NOT NULL DEFAULT 'Europe/Brussels',
            country_code    TEXT        NOT NULL DEFAULT '',
            is_default      BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_business_hours_schemas_default
            ON business_hours_schemas ((is_default))
            WHERE is_default = TRUE;

        CREATE TABLE IF NOT EXISTS business_hours_slots (
            id              BIGSERIAL   PRIMARY KEY,
            schema_id       UUID        NOT NULL REFERENCES business_hours_schemas(id) ON DELETE CASCADE,
            day_of_week     INTEGER     NOT NULL,
            start_minute    INTEGER     NOT NULL,
            end_minute      INTEGER     NOT NULL,
            CONSTRAINT chk_bh_slot_day CHECK (day_of_week BETWEEN 0 AND 6),
            CONSTRAINT chk_bh_slot_range CHECK (start_minute BETWEEN 0 AND 1440
                                            AND end_minute BETWEEN 0 AND 1440
                                            AND end_minute > start_minute)
        );

        CREATE INDEX IF NOT EXISTS ix_business_hours_slots_schema
            ON business_hours_slots (schema_id, day_of_week, start_minute);

        CREATE TABLE IF NOT EXISTS holidays (
            id              BIGSERIAL   PRIMARY KEY,
            schema_id       UUID        NOT NULL REFERENCES business_hours_schemas(id) ON DELETE CASCADE,
            holiday_date    DATE        NOT NULL,
            name            TEXT        NOT NULL DEFAULT '',
            source          TEXT        NOT NULL DEFAULT 'manual',
            country_code    TEXT        NOT NULL DEFAULT '',
            CONSTRAINT chk_holidays_source CHECK (source IN ('nager','manual')),
            CONSTRAINT uq_holidays_schema_date UNIQUE (schema_id, holiday_date)
        );

        CREATE INDEX IF NOT EXISTS ix_holidays_schema_date
            ON holidays (schema_id, holiday_date);

        CREATE TABLE IF NOT EXISTS sla_policies (
            id                          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            queue_id                    UUID        NULL REFERENCES queues(id) ON DELETE CASCADE,
            priority_id                 UUID        NOT NULL REFERENCES priorities(id) ON DELETE CASCADE,
            business_hours_schema_id    UUID        NOT NULL REFERENCES business_hours_schemas(id) ON DELETE RESTRICT,
            first_response_minutes      INTEGER     NOT NULL,
            resolution_minutes          INTEGER     NOT NULL,
            pause_on_pending            BOOLEAN     NOT NULL DEFAULT TRUE,
            created_utc                 TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc                 TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_sla_policies_queue_priority
            ON sla_policies (COALESCE(queue_id, '00000000-0000-0000-0000-000000000000'::uuid), priority_id);

        CREATE TABLE IF NOT EXISTS ticket_sla_state (
            ticket_id                       UUID        PRIMARY KEY REFERENCES tickets(id) ON DELETE CASCADE,
            policy_id                       UUID        NULL REFERENCES sla_policies(id) ON DELETE SET NULL,
            first_response_deadline_utc     TIMESTAMPTZ NULL,
            resolution_deadline_utc         TIMESTAMPTZ NULL,
            first_response_met_utc          TIMESTAMPTZ NULL,
            resolution_met_utc              TIMESTAMPTZ NULL,
            first_response_business_minutes INTEGER     NULL,
            resolution_business_minutes     INTEGER     NULL,
            is_paused                       BOOLEAN     NOT NULL DEFAULT FALSE,
            paused_since_utc                TIMESTAMPTZ NULL,
            paused_accum_minutes            INTEGER     NOT NULL DEFAULT 0,
            last_recalc_utc                 TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc                     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_sla_state_pending_fr
            ON ticket_sla_state (first_response_deadline_utc)
            WHERE first_response_met_utc IS NULL;
        CREATE INDEX IF NOT EXISTS ix_ticket_sla_state_pending_res
            ON ticket_sla_state (resolution_deadline_utc)
            WHERE resolution_met_utc IS NULL;

        -- ===================================================================
        -- v0.0.8 step 8: global search (Postgres FTS + trigram fuzzy)
        --
        -- 1) ticket_event_search is auto-populated from ticket_events via a
        --    trigger. normalized_text strips accents and lowercases so matches
        --    are accent- and case-insensitive. body_html is stripped to text
        --    so HTML mails remain searchable even when body_text is sparse.
        -- 2) One-time backfill fills the sidecar for events that existed
        --    before this migration ran (idempotent: ON CONFLICT DO UPDATE).
        -- 3) mail_messages gets its own tsvector covering subject + body_text
        --    + sender identity, for mail-scoped hits that resolve back to the
        --    parent ticket.
        -- 4) contacts uses pg_trgm GIN indexes for similarity lookup on
        --    email + full name.
        -- ===================================================================

        -- Accent-insensitive search was an early ambition but unaccent()
        -- is marked STABLE (dictionary lookup via search_path), which rules
        -- it out of STORED generated columns and expression indexes. The
        -- documented IMMUTABLE-wrapper workaround is fragile across
        -- install layouts, so v1 ships case-insensitive only (lower()).
        -- Revisit when we get a concrete "find café matches cafe" ask.

        CREATE OR REPLACE FUNCTION ticket_event_search_fill() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            v_text TEXT;
        BEGIN
            v_text := lower(
                coalesce(NEW.body_text, '') || ' ' ||
                regexp_replace(coalesce(NEW.body_html, ''), '<[^>]*>', ' ', 'g')
            );
            INSERT INTO ticket_event_search (event_id, ticket_id, normalized_text)
            VALUES (NEW.id, NEW.ticket_id, v_text)
            ON CONFLICT (event_id) DO UPDATE
                SET normalized_text = EXCLUDED.normalized_text,
                    ticket_id = EXCLUDED.ticket_id;
            RETURN NEW;
        END;
        $$;

        DROP TRIGGER IF EXISTS trg_ticket_event_search_fill ON ticket_events;
        CREATE TRIGGER trg_ticket_event_search_fill
            AFTER INSERT OR UPDATE OF body_text, body_html ON ticket_events
            FOR EACH ROW EXECUTE FUNCTION ticket_event_search_fill();

        -- Backfill events that predate this trigger. Safe to re-run.
        INSERT INTO ticket_event_search (event_id, ticket_id, normalized_text)
        SELECT e.id, e.ticket_id,
               lower(
                   coalesce(e.body_text, '') || ' ' ||
                   regexp_replace(coalesce(e.body_html, ''), '<[^>]*>', ' ', 'g')
               )
        FROM ticket_events e
        LEFT JOIN ticket_event_search s ON s.event_id = e.id
        WHERE s.event_id IS NULL;

        -- Mail-scoped FTS: subject + body_text + sender identity.
        ALTER TABLE mail_messages
            ADD COLUMN IF NOT EXISTS search_vector TSVECTOR
                GENERATED ALWAYS AS (
                    to_tsvector('simple',
                        lower(
                            coalesce(subject, '') || ' ' ||
                            coalesce(body_text, '') || ' ' ||
                            coalesce(from_address::text, '') || ' ' ||
                            coalesce(from_name, '')
                        )
                    )
                ) STORED;

        CREATE INDEX IF NOT EXISTS ix_mail_messages_search_vector
            ON mail_messages USING GIN (search_vector);

        -- SLA policy targets are optional (first-response-only or resolution-only).
        ALTER TABLE sla_policies ALTER COLUMN first_response_minutes DROP NOT NULL;
        ALTER TABLE sla_policies ALTER COLUMN resolution_minutes DROP NOT NULL;

        -- Contacts: fuzzy similarity on email + full name for typeahead.
        CREATE INDEX IF NOT EXISTS ix_contacts_email_trgm
            ON contacts USING GIN ((lower(email::text)) gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_contacts_name_trgm
            ON contacts USING GIN (
                (lower(coalesce(first_name, '') || ' ' || coalesce(last_name, ''))) gin_trgm_ops
            );

        -- ===================================================================
        -- Per-queue inbound folder selection (Graph mail folder id + display name)
        -- ===================================================================
        ALTER TABLE queues
            ADD COLUMN IF NOT EXISTS inbound_folder_id   TEXT NULL,
            ADD COLUMN IF NOT EXISTS inbound_folder_name TEXT NULL;

        -- ===================================================================
        -- v0.0.9 Companies: customer identification (code/short name/VAT),
        -- alert/note that can pop up on ticket create and/or ticket open,
        -- and trigram indexes to power the Companies global-search source.
        -- ===================================================================

        ALTER TABLE companies
            ADD COLUMN IF NOT EXISTS code                CITEXT  NULL,
            ADD COLUMN IF NOT EXISTS short_name          TEXT    NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS vat_number          TEXT    NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS alert_text          TEXT    NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS alert_on_create     BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS alert_on_open       BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS alert_on_open_mode  TEXT    NOT NULL DEFAULT 'session',
            ADD COLUMN IF NOT EXISTS email               TEXT    NOT NULL DEFAULT '';

        -- Backfill any existing rows lacking a code so the NOT NULL + UNIQUE
        -- constraints below can be applied without failing.
        UPDATE companies
            SET code = 'LEGACY-' || substr(id::text, 1, 8)
            WHERE code IS NULL;

        ALTER TABLE companies ALTER COLUMN code SET NOT NULL;

        ALTER TABLE companies DROP CONSTRAINT IF EXISTS chk_companies_alert_mode;
        ALTER TABLE companies ADD CONSTRAINT chk_companies_alert_mode
            CHECK (alert_on_open_mode IN ('session','every'));

        CREATE UNIQUE INDEX IF NOT EXISTS ux_companies_code ON companies (code);

        -- Trigram indexes for fuzzy search across name/short_name/code/vat.
        CREATE INDEX IF NOT EXISTS ix_companies_name_trgm
            ON companies USING GIN ((lower(coalesce(name, ''))) gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_companies_short_name_trgm
            ON companies USING GIN ((lower(coalesce(short_name, ''))) gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_companies_code_trgm
            ON companies USING GIN ((lower(code::text)) gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_companies_vat_trgm
            ON companies USING GIN ((lower(coalesce(vat_number, ''))) gin_trgm_ops);

        -- ===================================================================
        -- v0.0.9 Contact ↔ Company many-to-many with role
        --
        -- Replaces the old direct contacts.company_id FK. A contact can now
        -- belong to multiple companies with a role per link: exactly one
        -- 'primary' (the default work address), any number of 'secondary'
        -- (other involvements) and 'supplier' (vendors). The primary link is
        -- what the ticket list joins against to show the requester's company.
        --
        -- Safety invariants:
        --   - CHECK on role keeps the enum honest.
        --   - UNIQUE (contact_id, company_id) prevents duplicate rows
        --     — the same contact can't appear twice in the same company even
        --     in different roles; change the role in place instead.
        --   - Partial UNIQUE on contact_id WHERE role='primary' enforces
        --     at most one primary per contact.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS contact_companies (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            contact_id      UUID        NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            company_id      UUID        NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            role            TEXT        NOT NULL,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_contact_companies_role
                CHECK (role IN ('primary','secondary','supplier')),
            CONSTRAINT uq_contact_companies_pair UNIQUE (contact_id, company_id)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_contact_companies_primary
            ON contact_companies (contact_id) WHERE role = 'primary';
        CREATE INDEX IF NOT EXISTS ix_contact_companies_company_role
            ON contact_companies (company_id, role);
        CREATE INDEX IF NOT EXISTS ix_contact_companies_contact_role
            ON contact_companies (contact_id, role);

        -- Idempotent backfill: every existing contacts.company_id becomes a
        -- 'primary' link. Only runs on databases where the old column still
        -- exists; silently skipped otherwise so re-boots are a no-op.
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'contacts' AND column_name = 'company_id'
            ) THEN
                INSERT INTO contact_companies (contact_id, company_id, role)
                SELECT id, company_id, 'primary'
                FROM contacts
                WHERE company_id IS NOT NULL
                ON CONFLICT (contact_id, company_id) DO NOTHING;
            END IF;
        END $$;

        ALTER TABLE contacts DROP COLUMN IF EXISTS company_id;

        -- ===================================================================
        -- v0.0.12 step 1: outbound mail
        --
        -- mail_messages grows a direction column so both inbound and our own
        -- sent mails live in the same table. received_utc becomes nullable
        -- (outbound rows carry sent_utc instead) and a CHECK enforces that
        -- exactly the right timestamp is populated per direction. Threading
        -- (FindTicketIdByReferences) already matches on message_id, so
        -- replies to our outbound mail resolve back to the same ticket
        -- without any code change.
        --
        -- ticket_events CHECK is extended with MailSent so outbound events
        -- can be persisted alongside MailReceived.
        -- ===================================================================

        ALTER TABLE mail_messages
            ADD COLUMN IF NOT EXISTS direction TEXT        NOT NULL DEFAULT 'Inbound',
            ADD COLUMN IF NOT EXISTS sent_utc  TIMESTAMPTZ NULL;

        ALTER TABLE mail_messages ALTER COLUMN received_utc DROP NOT NULL;

        -- NOT VALID so pre-existing inbound rows that somehow violate the
        -- invariant (e.g. received_utc NULL from an earlier schema iteration)
        -- don't block the bootstrap. New writes still enforce it.
        ALTER TABLE mail_messages DROP CONSTRAINT IF EXISTS chk_mail_messages_direction;
        ALTER TABLE mail_messages ADD CONSTRAINT chk_mail_messages_direction
            CHECK (direction IN ('Inbound','Outbound')) NOT VALID;

        ALTER TABLE mail_messages DROP CONSTRAINT IF EXISTS chk_mail_messages_timestamp;
        ALTER TABLE mail_messages ADD CONSTRAINT chk_mail_messages_timestamp
            CHECK (
                (direction = 'Inbound'  AND received_utc IS NOT NULL) OR
                (direction = 'Outbound' AND sent_utc     IS NOT NULL)
            ) NOT VALID;

        -- See v0.0.8 block above for the NOT VALID rationale — legacy rows
        -- outside the whitelist are grandfathered; only new writes enforce.
        -- v0.0.12 adds 'RequesterChange' for the switch-requester timeline event.
        ALTER TABLE ticket_events DROP CONSTRAINT IF EXISTS chk_ticket_event_type;
        ALTER TABLE ticket_events ADD CONSTRAINT chk_ticket_event_type
            CHECK (event_type IN ('Created','Comment','Mail','Note','StatusChange',
                                  'AssignmentChange','PriorityChange','QueueChange',
                                  'CategoryChange','SystemNote','MailReceived',
                                  'MailSent','CompanyAssignment','RequesterChange')) NOT VALID;

        -- ===============================================================
        -- v0.0.12 stap 4 — mention notifications (@@-tag pipeline)
        -- ===============================================================
        -- One row per (user_id, event_id) pair: when agent A tags agent B in
        -- a post, a row is inserted for B with `source_user_id=A`. The row is
        -- the persistent backstop — SignalR push + toast are fire-and-forget,
        -- the navbar-widget + /profile/mentions page always read from here.
        -- Ticket metadata is denormalised (ticket_number, ticket_subject) so
        -- the history-page renders without joining through tickets for a row
        -- whose ticket was later deleted — ON DELETE CASCADE trims the row,
        -- but until that point the denormalised columns show the last-known
        -- state. source_user_id uses ON DELETE SET NULL so history survives
        -- after the author leaves (a 2026-pattern: audit keeps references
        -- stable even when principals are removed).
        CREATE TABLE IF NOT EXISTS user_notifications (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            user_id             UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            source_user_id      UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            notification_type   TEXT        NOT NULL DEFAULT 'mention',
            ticket_id           UUID        NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            ticket_number       BIGINT      NOT NULL,
            ticket_subject      TEXT        NOT NULL DEFAULT '',
            event_id            BIGINT      NOT NULL REFERENCES ticket_events(id) ON DELETE CASCADE,
            event_type          TEXT        NOT NULL,
            preview_text        TEXT        NOT NULL DEFAULT '',
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            viewed_utc          TIMESTAMPTZ NULL,
            acked_utc           TIMESTAMPTZ NULL,
            email_sent_utc      TIMESTAMPTZ NULL,
            email_error         TEXT        NULL
        );

        -- Hot path: "my open notifications" drives the navbar-widget + pulse.
        -- Partial index keeps the struct small on installs with a lot of
        -- acked history.
        CREATE INDEX IF NOT EXISTS ix_user_notifications_pending
            ON user_notifications (user_id, created_utc DESC)
            WHERE acked_utc IS NULL;

        -- History page: keyset pagination on (created_utc DESC, id DESC).
        CREATE INDEX IF NOT EXISTS ix_user_notifications_user_history
            ON user_notifications (user_id, created_utc DESC, id DESC);

        -- Reverse lookup used by future dedup passes (e.g. only one
        -- notification per (user, event) even if the editor's
        -- mentionedUserIds array sneaks the same id twice).
        CREATE INDEX IF NOT EXISTS ix_user_notifications_event
            ON user_notifications (event_id);

        -- NOT VALID so future notification_type additions can land without
        -- a table-rewrite. The whitelist guards new writes only.
        ALTER TABLE user_notifications DROP CONSTRAINT IF EXISTS chk_user_notifications_type;
        ALTER TABLE user_notifications ADD CONSTRAINT chk_user_notifications_type
            CHECK (notification_type IN ('mention')) NOT VALID;

        -- ===================================================================
        -- v0.0.13 M365 login — per-user auth mode
        -- Local accounts keep password_hash + TOTP; Microsoft accounts carry
        -- an Azure AD object-id (oid claim) and MUST NOT have a local
        -- password (see chk_users_auth_mode below). The two modes are
        -- mutually exclusive per row — a user is either Local or Microsoft,
        -- never both. Upgrading a Local admin to Microsoft drops the
        -- password + TOTP rows in the same transaction (handled by the
        -- user-admin endpoint, not the schema).
        -- ===================================================================

        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS auth_mode         TEXT    NOT NULL DEFAULT 'Local',
            ADD COLUMN IF NOT EXISTS external_provider TEXT    NULL,
            ADD COLUMN IF NOT EXISTS external_subject  TEXT    NULL,
            ADD COLUMN IF NOT EXISTS is_active         BOOLEAN NOT NULL DEFAULT TRUE;

        -- password_hash must be nullable for Microsoft accounts. NULL-ing
        -- the NOT NULL is idempotent — Postgres no-ops if the column is
        -- already nullable.
        ALTER TABLE users ALTER COLUMN password_hash DROP NOT NULL;

        -- One Azure OID maps to at most one row. Partial index so local
        -- accounts (NULL subject) don't collide with each other.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_users_external
            ON users (external_provider, external_subject)
            WHERE external_subject IS NOT NULL;

        -- auth_mode invariant. Drop-then-add so we can tighten the rule
        -- on an existing install without the "constraint already exists"
        -- error. NOT VALID guards new writes only — any pre-existing row
        -- written before this release (all Local with a password_hash) is
        -- already compliant, so validation would pass, but keeping it
        -- NOT VALID matches the pattern used elsewhere in this file.
        ALTER TABLE users DROP CONSTRAINT IF EXISTS chk_users_auth_mode;
        ALTER TABLE users ADD CONSTRAINT chk_users_auth_mode
            CHECK (
                (auth_mode = 'Local'
                    AND password_hash IS NOT NULL
                    AND external_provider IS NULL
                    AND external_subject IS NULL)
                OR
                (auth_mode = 'Microsoft'
                    AND password_hash IS NULL
                    AND external_provider IS NOT NULL
                    AND external_subject IS NOT NULL)
            ) NOT VALID;
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
        // Under Docker-compose on a VPS, Postgres runs native on the host and
        // may be a few seconds behind container start. In dev (Windows) Postgres
        // is always up so the first attempt wins. Same code path for both.
        var delay = TimeSpan.FromMilliseconds(500);
        var maxDelay = TimeSpan.FromSeconds(5);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation(
                    "Database bootstrap complete (audit + auth + ticket domain) after {Attempts} attempt(s).",
                    attempt);
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    _logger.LogError(ex,
                        "Database bootstrap giving up after {Attempts} attempts — Postgres unreachable for 2 minutes.",
                        attempt);
                    throw;
                }

                _logger.LogWarning(
                    "Database bootstrap attempt {Attempt} failed ({ErrorType}). Retrying in {DelayMs}ms…",
                    attempt, ex.GetType().Name, (int)delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
                delay = delay < maxDelay
                    ? TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds))
                    : maxDelay;
            }
        }
    }

    internal static bool IsTransient(Exception ex) => ex switch
    {
        NpgsqlException npg => npg.IsTransient || npg.InnerException is SocketException,
        SocketException => true,
        TimeoutException => true,
        _ => false,
    };

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
