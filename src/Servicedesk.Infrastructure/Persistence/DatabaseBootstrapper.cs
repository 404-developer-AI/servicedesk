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

        -- v0.0.60 — per-mailbox inbound polling switch. Defaults TRUE so
        -- existing queues keep polling; toggling it off makes MailPollingService
        -- skip the queue while leaving its delta-state intact.
        ALTER TABLE queues
            ADD COLUMN IF NOT EXISTS inbound_polling_enabled BOOLEAN NOT NULL DEFAULT TRUE;

        -- ===================================================================
        -- v0.0.66 — multiple inbound mailboxes per queue
        -- ===================================================================
        -- Each row is one (mailbox, folder) source feeding a queue, with its
        -- own Graph delta cursor + health state. Supersedes the singular
        -- queues.inbound_* columns + the per-queue mail_poll_state table, both
        -- of which are kept: queues.inbound_* now acts as a denormalized mirror
        -- of the first source (so the outbound from-address fallback keeps
        -- working), and mail_poll_state is read once below for the backfill then
        -- left dormant. Must run AFTER the queues.inbound_folder_id /
        -- inbound_folder_name / inbound_polling_enabled columns above exist —
        -- the backfill SELECT below reads them.
        CREATE TABLE IF NOT EXISTS queue_inbound_mailboxes (
            id                            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            queue_id                      UUID        NOT NULL REFERENCES queues(id) ON DELETE CASCADE,
            mailbox_address               CITEXT      NOT NULL,
            folder_id                     TEXT        NULL,
            folder_name                   TEXT        NULL,
            polling_enabled               BOOLEAN     NOT NULL DEFAULT TRUE,
            delta_link                    TEXT        NULL,
            last_polled_utc               TIMESTAMPTZ NULL,
            last_error                    TEXT        NULL,
            consecutive_failures          INTEGER     NOT NULL DEFAULT 0,
            processed_folder_id           TEXT        NULL,
            last_mailbox_action_error     TEXT        NULL,
            last_mailbox_action_error_utc TIMESTAMPTZ NULL,
            created_utc                   TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc                   TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- A given mailbox+folder feeds exactly one queue (exclusivity). Partial
        -- so rows without a folder selected yet don't collide on NULL.
        CREATE UNIQUE INDEX IF NOT EXISTS ix_queue_inbound_mailboxes_source
            ON queue_inbound_mailboxes (mailbox_address, folder_id)
            WHERE folder_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_queue_inbound_mailboxes_queue
            ON queue_inbound_mailboxes (queue_id);

        -- The old one-mailbox-per-queue unique index would now collide whenever
        -- two queues mirror the same mailbox under different folders. Drop it;
        -- exclusivity is enforced per-source above.
        DROP INDEX IF EXISTS ix_queues_inbound_mailbox;

        -- One-time backfill: turn each queue's singular inbound config (and its
        -- per-queue mail_poll_state, if any) into a source row. Idempotent via
        -- the NOT EXISTS guard so re-running bootstrap never duplicates.
        INSERT INTO queue_inbound_mailboxes (
            queue_id, mailbox_address, folder_id, folder_name, polling_enabled,
            delta_link, last_polled_utc, last_error, consecutive_failures,
            processed_folder_id, last_mailbox_action_error, last_mailbox_action_error_utc)
        SELECT q.id, q.inbound_mailbox_address, q.inbound_folder_id, q.inbound_folder_name,
               q.inbound_polling_enabled,
               s.delta_link, s.last_polled_utc, s.last_error, COALESCE(s.consecutive_failures, 0),
               s.processed_folder_id, s.last_mailbox_action_error, s.last_mailbox_action_error_utc
        FROM queues q
        LEFT JOIN mail_poll_state s ON s.queue_id = q.id
        WHERE q.inbound_mailbox_address IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM queue_inbound_mailboxes m WHERE m.queue_id = q.id
          );

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

        -- ===================================================================
        -- v0.0.19 Intake Forms — customer-facing tokenised questionnaires.
        --
        -- intake_templates hold admin-authored reusable question sets.
        -- intake_template_questions carry ordered typed questions (including
        -- 'SectionHeader' which is a layout-only row, not an input). Dropdown
        -- options live in their own child table so a value can be renamed
        -- without rewriting the question row. default_value holds a literal
        -- prefill; default_token (e.g. '{{requester.email}}') is resolved
        -- server-side at send time into the instance's prefill_json snapshot.
        --
        -- intake_form_instances are per-send rows. Token handling mirrors the
        -- MS OIDC challenge pattern: the raw token (32-byte CSPRNG, base64url
        -- encoded in the URL) is never persisted — only sha256(token) as the
        -- lookup key and DataProtection-ciphertext for redisplay. prefill_json
        -- is a {questionId: value} snapshot frozen at send time so token-
        -- resolution drift (requester email change, etc.) doesn't reshape a
        -- form that's already in the customer's inbox.
        --
        -- intake_form_answers store submissions. answer_json is flexible to
        -- avoid per-type columns: string | number | bool | string[] | ISO date.
        -- question_id has no live FK — answers are rendered against the
        -- instance's template_snapshot_json so the admin can freely rewrite
        -- a template after it's been used without breaking historical
        -- timeline rendering.
        -- ===================================================================

        CREATE TABLE IF NOT EXISTS intake_templates (
            id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT NOT NULL,
            description     TEXT NULL,
            is_active       BOOLEAN NOT NULL DEFAULT TRUE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by      UUID NULL REFERENCES users(id) ON DELETE SET NULL
        );

        -- Soft-unique on name among active templates. A deactivated template
        -- keeps its name so audit rows referencing it stay readable.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_intake_templates_active_name
            ON intake_templates (name)
            WHERE is_active;

        CREATE TABLE IF NOT EXISTS intake_template_questions (
            id              BIGSERIAL PRIMARY KEY,
            template_id     UUID NOT NULL REFERENCES intake_templates(id) ON DELETE CASCADE,
            sort_order      INT NOT NULL,
            question_type   TEXT NOT NULL,
            label           TEXT NOT NULL,
            help_text       TEXT NULL,
            is_required     BOOLEAN NOT NULL DEFAULT FALSE,
            default_value   TEXT NULL,
            default_token   TEXT NULL,
            UNIQUE (template_id, sort_order)
        );

        ALTER TABLE intake_template_questions DROP CONSTRAINT IF EXISTS chk_intake_question_type;
        ALTER TABLE intake_template_questions ADD CONSTRAINT chk_intake_question_type
            CHECK (question_type IN (
                'ShortText','LongText','DropdownSingle','DropdownMulti',
                'Number','Date','YesNo','SectionHeader'
            )) NOT VALID;

        CREATE TABLE IF NOT EXISTS intake_template_question_options (
            id              BIGSERIAL PRIMARY KEY,
            question_id     BIGINT NOT NULL REFERENCES intake_template_questions(id) ON DELETE CASCADE,
            sort_order      INT NOT NULL,
            value           TEXT NOT NULL,
            label           TEXT NOT NULL,
            UNIQUE (question_id, sort_order)
        );

        CREATE TABLE IF NOT EXISTS intake_form_instances (
            id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            template_id         UUID NOT NULL REFERENCES intake_templates(id) ON DELETE RESTRICT,
            ticket_id           UUID NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            sent_event_id       BIGINT NULL REFERENCES ticket_events(id) ON DELETE SET NULL,
            submitted_event_id  BIGINT NULL REFERENCES ticket_events(id) ON DELETE SET NULL,
            token_hash          BYTEA NULL,
            token_cipher        BYTEA NULL,
            prefill_json        JSONB NOT NULL DEFAULT '{}'::jsonb,
            status              TEXT NOT NULL DEFAULT 'Draft',
            expires_utc         TIMESTAMPTZ NULL,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            sent_utc            TIMESTAMPTZ NULL,
            submitted_utc       TIMESTAMPTZ NULL,
            submitter_ip        INET NULL,
            submitter_ua        TEXT NULL,
            created_by          UUID NULL REFERENCES users(id) ON DELETE SET NULL,
            sent_to_email       TEXT NULL
        );

        ALTER TABLE intake_form_instances DROP CONSTRAINT IF EXISTS chk_intake_form_status;
        ALTER TABLE intake_form_instances ADD CONSTRAINT chk_intake_form_status
            CHECK (status IN ('Draft','Sent','Submitted','Expired','Cancelled')) NOT VALID;

        -- Partial unique: a token_hash is only meaningful for Sent/Submitted
        -- rows. Draft + Cancelled rows have NULL hash (never left the server)
        -- and must not collide.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_intake_form_instances_token_hash
            ON intake_form_instances (token_hash)
            WHERE token_hash IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_intake_form_instances_ticket
            ON intake_form_instances (ticket_id, created_utc DESC);

        -- Expiry sweeper hot path: only Sent rows can expire.
        CREATE INDEX IF NOT EXISTS ix_intake_form_instances_expiry
            ON intake_form_instances (expires_utc)
            WHERE status = 'Sent';

        CREATE TABLE IF NOT EXISTS intake_form_answers (
            id              BIGSERIAL PRIMARY KEY,
            instance_id     UUID NOT NULL REFERENCES intake_form_instances(id) ON DELETE CASCADE,
            question_id     BIGINT NOT NULL REFERENCES intake_template_questions(id) ON DELETE RESTRICT,
            answer_json     JSONB NOT NULL,
            UNIQUE (instance_id, question_id)
        );

        -- v0.0.19 snapshot-based template history. Instead of locking a
        -- template after the first submission, we freeze the question set
        -- on the instance at Draft → Sent time. Historical submissions
        -- render against this snapshot; live template edits no longer
        -- corrupt timeline rendering of already-submitted forms.
        ALTER TABLE intake_form_instances
            ADD COLUMN IF NOT EXISTS template_snapshot_json JSONB NULL;

        -- Drop the RESTRICT FK on intake_form_answers.question_id. Answers
        -- resolve against the instance's snapshot after this change, so the
        -- referential guarantee to live template_questions is no longer
        -- needed — and it used to block the full-replace update pattern.
        -- Named constraint uses Postgres' default naming convention.
        ALTER TABLE intake_form_answers
            DROP CONSTRAINT IF EXISTS intake_form_answers_question_id_fkey;

        -- One-shot backfill for Sent/Submitted/Expired rows that predate
        -- the snapshot column. Draft instances don't need one — the
        -- repository takes the snapshot at send time. Idempotent via the
        -- IS NULL guard.
        UPDATE intake_form_instances i
        SET template_snapshot_json = (
            SELECT jsonb_build_object(
                'name', t.name,
                'description', t.description,
                'questions', COALESCE((
                    SELECT jsonb_agg(
                        jsonb_build_object(
                            'id', q.id,
                            'sortOrder', q.sort_order,
                            'type', q.question_type,
                            'label', q.label,
                            'helpText', q.help_text,
                            'isRequired', q.is_required,
                            'defaultValue', q.default_value,
                            'defaultToken', q.default_token,
                            'options', COALESCE((
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'id', o.id,
                                        'sortOrder', o.sort_order,
                                        'value', o.value,
                                        'label', o.label
                                    ) ORDER BY o.sort_order
                                )
                                FROM intake_template_question_options o
                                WHERE o.question_id = q.id
                            ), '[]'::jsonb)
                        ) ORDER BY q.sort_order
                    )
                    FROM intake_template_questions q
                    WHERE q.template_id = t.id
                ), '[]'::jsonb)
            )
            FROM intake_templates t
            WHERE t.id = i.template_id
        )
        WHERE i.status IN ('Sent','Submitted','Expired')
          AND i.template_snapshot_json IS NULL;

        -- v0.0.19 extends the ticket_events CHECK with the three intake-form
        -- event types. Same NOT VALID pattern — legacy rows are already
        -- compliant (the enum is append-only), new writes enforce.
        ALTER TABLE ticket_events DROP CONSTRAINT IF EXISTS chk_ticket_event_type;
        ALTER TABLE ticket_events ADD CONSTRAINT chk_ticket_event_type
            CHECK (event_type IN ('Created','Comment','Mail','Note','StatusChange',
                                  'AssignmentChange','PriorityChange','QueueChange',
                                  'CategoryChange','SystemNote','MailReceived',
                                  'MailSent','CompanyAssignment','RequesterChange',
                                  'IntakeFormSent','IntakeFormSubmitted','IntakeFormExpired',
                                  'ParentLinked','ParentUnlinked')) NOT VALID;

        -- v0.0.23 ticket merge: a finalised merge stamps merged_into_ticket_id
        -- on the source ticket so mail-ingest can follow the redirect chain to
        -- the surviving target. merged_utc/merged_by_user_id give the UI a
        -- "Merged into #X on {date} by {agent}" banner without a join. Status
        -- moves to the seeded 'Merged' system status (state_category='Closed'
        -- so existing OpenOnly/list-counter filters keep treating it as
        -- terminal). The dedicated status_id allows the UI to render a
        -- distinct badge and lets agents filter for it explicitly.
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS merged_into_ticket_id UUID NULL
                REFERENCES tickets(id) ON DELETE SET NULL,
            ADD COLUMN IF NOT EXISTS merged_utc           TIMESTAMPTZ NULL,
            ADD COLUMN IF NOT EXISTS merged_by_user_id    UUID NULL
                REFERENCES users(id) ON DELETE SET NULL;

        -- Sparse index — only used to render the "Merged from #A1, #A2" strip
        -- on the target ticket and to list sources during admin debugging.
        CREATE INDEX IF NOT EXISTS ix_tickets_merged_into
            ON tickets (merged_into_ticket_id)
            WHERE merged_into_ticket_id IS NOT NULL;

        -- Manual Main / Sub-ticket links. One parent, N children. Used for
        -- workflows like "support ticket spawns a separate order ticket but
        -- both must remain linked". Distinct from merge (which closes the
        -- source) and split (which forks the body) — the link is purely
        -- relational; both tickets stay independently editable. ON DELETE
        -- SET NULL so a hard-delete of the parent silently un-parents the
        -- child instead of cascading.
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS parent_ticket_id         UUID NULL
                REFERENCES tickets(id) ON DELETE SET NULL,
            ADD COLUMN IF NOT EXISTS parent_linked_utc        TIMESTAMPTZ NULL,
            ADD COLUMN IF NOT EXISTS parent_linked_by_user_id UUID NULL
                REFERENCES users(id) ON DELETE SET NULL;

        -- Sparse index for "list this ticket's sub-tickets" lookups.
        CREATE INDEX IF NOT EXISTS ix_tickets_parent
            ON tickets (parent_ticket_id)
            WHERE parent_ticket_id IS NOT NULL;

        -- v0.0.23 ticket split: when an agent splits a multi-question mail off
        -- into a fresh ticket, the new ticket records its parent here so both
        -- ends can render a "Split from #X" banner and the parent can list its
        -- children via a sparse-index lookup. ON DELETE SET NULL preserves the
        -- child if the parent is hard-deleted.
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS split_from_ticket_id UUID NULL
                REFERENCES tickets(id) ON DELETE SET NULL,
            ADD COLUMN IF NOT EXISTS split_from_utc       TIMESTAMPTZ NULL,
            ADD COLUMN IF NOT EXISTS split_from_user_id   UUID NULL
                REFERENCES users(id) ON DELETE SET NULL;

        CREATE INDEX IF NOT EXISTS ix_tickets_split_from
            ON tickets (split_from_ticket_id)
            WHERE split_from_ticket_id IS NOT NULL;

        -- tickets.source allow-list — kept here at its historical
        -- insertion point so the bootstrap reads top-down. Original was
        -- the inline CHECK in CREATE TABLE (Web/Mail/Api/System). v0.0.23
        -- added 'Split' for the ticket-split feature. v0.0.41 fase 4
        -- added 'Zammad' for the migration import. The allow-list MUST
        -- be the union of every value the codebase writes into this
        -- column — CHECK constraints re-validate the whole row on
        -- UPDATE, so dropping a value here would break SLA recalcs and
        -- other unrelated writes on previously-saved tickets.
        --
        -- The heal-step normalises any historical rows whose source
        -- predates the current allow-list (e.g. typo / lowercase /
        -- removed value) to 'Api' so the ADD CONSTRAINT scan attaches
        -- cleanly. RAISE NOTICE prints the offending values + count for
        -- post-hoc audit.
        DO $$ DECLARE
            bad_count int;
            bad_values text;
        BEGIN
            SELECT count(*), string_agg(DISTINCT source, ', ')
              INTO bad_count, bad_values
              FROM tickets
             WHERE source NOT IN ('Web','Mail','Api','System','Split','Zammad');
            IF bad_count > 0 THEN
                RAISE NOTICE 'chk_ticket_source heal: % row(s) had source outside allow-list (values: %) — normalising to Api.',
                    bad_count, bad_values;
                UPDATE tickets SET source = 'Api'
                 WHERE source NOT IN ('Web','Mail','Api','System','Split','Zammad');
            END IF;
        END $$;

        ALTER TABLE tickets DROP CONSTRAINT IF EXISTS chk_ticket_source;
        ALTER TABLE tickets ADD CONSTRAINT chk_ticket_source
            CHECK (source IN ('Web','Mail','Api','System','Split','Zammad'));

        -- v0.0.24 triggers Blok 3: per-ticket pending-till timestamp written by
        -- the set_pending_till action and consumed by the Blok 5 scheduler
        -- worker (reminder_reached → fire time-based triggers when this passes
        -- now()). Nullable; agents can also clear it manually.
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS pending_till_utc TIMESTAMPTZ NULL;

        -- v0.0.24 Blok 5: partial index for the scheduler's reminder query.
        -- The vast majority of rows have no pending-till and is_deleted=FALSE
        -- already costs a row scan, so we keep the index small by predicating
        -- on both. The 1-minute tick query becomes a tight index range scan.
        CREATE INDEX IF NOT EXISTS ix_tickets_pending_till_utc
            ON tickets (pending_till_utc)
            WHERE pending_till_utc IS NOT NULL AND is_deleted = FALSE;

        -- ===================================================================
        -- v0.0.24 Triggers — admin-configurable automation
        -- ===================================================================
        -- Conditions/actions are JSONB; the schema is enforced at the C# layer
        -- (whitelisted action kinds, condition tree shape) rather than the DB
        -- so adding a new action handler does not require a migration. The
        -- shape of the conditions tree is documented in TRIGGERS.md §4.1.
        -- name is unique because it doubles as evaluation-order key
        -- (alphabetical, MVP convention from Zammad — admins prefix with
        -- 010-/020- when they want fine control).
        CREATE TABLE IF NOT EXISTS triggers (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name                TEXT        NOT NULL UNIQUE,
            description         TEXT        NOT NULL DEFAULT '',
            is_active           BOOLEAN     NOT NULL DEFAULT TRUE,
            activator_kind      TEXT        NOT NULL,
            activator_mode      TEXT        NOT NULL,
            conditions          JSONB       NOT NULL DEFAULT '{"op":"AND","items":[]}'::jsonb,
            actions             JSONB       NOT NULL DEFAULT '[]'::jsonb,
            locale              TEXT        NULL,
            timezone            TEXT        NULL,
            note                TEXT        NOT NULL DEFAULT '',
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by_user_id  UUID        NULL REFERENCES users(id) ON DELETE SET NULL
        );

        -- activator_kind/activator_mode are paired: 'action'-kind triggers run
        -- in selective or always mode (selective = only on attribute change,
        -- always = on every matching mutation), while 'time'-kind triggers
        -- fire from the scheduler worker on reminder/escalation events.
        ALTER TABLE triggers DROP CONSTRAINT IF EXISTS chk_trigger_activator;
        ALTER TABLE triggers ADD CONSTRAINT chk_trigger_activator
            CHECK (
                (activator_kind = 'action' AND activator_mode IN ('selective','always'))
                OR
                (activator_kind = 'time'   AND activator_mode IN ('reminder','escalation','escalation_warning'))
            ) NOT VALID;

        -- Hot path for the evaluator: list active triggers in alphabetical
        -- order. lower(name) keeps the order case-insensitive so admins can
        -- name "010-Route" or "010-route" interchangeably.
        CREATE INDEX IF NOT EXISTS ix_triggers_active_name
            ON triggers (is_active, lower(name));

        -- v0.0.24 batch 3: marker so the seeder can refresh content on
        -- upgrade without overwriting admin tweaks. The first PUT/DELETE
        -- by an admin clears the flag in the API layer.
        ALTER TABLE triggers ADD COLUMN IF NOT EXISTS is_seed BOOLEAN NOT NULL DEFAULT FALSE;

        -- Optional grouping for triggers. Groups are a pure UX construct
        -- (no effect on evaluation order or matching) so the evaluator
        -- ignores them. ON DELETE SET NULL drops the membership but
        -- keeps the trigger — admin chose "Triggers to Ungrouped" over
        -- "blocked delete" when designing the feature.
        CREATE TABLE IF NOT EXISTS trigger_groups (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            color           TEXT        NULL,
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS ix_trigger_groups_sort
            ON trigger_groups (sort_order, lower(name));

        ALTER TABLE triggers ADD COLUMN IF NOT EXISTS group_id UUID NULL
            REFERENCES trigger_groups(id) ON DELETE SET NULL;
        ALTER TABLE triggers ADD COLUMN IF NOT EXISTS sort_order INTEGER NOT NULL DEFAULT 0;

        -- Quick scan for "list this group's triggers in their saved order"
        -- (the admin UI's primary query). NULL group_id is the
        -- "Ungrouped" pseudo-section and is covered by the same index.
        CREATE INDEX IF NOT EXISTS ix_triggers_group_sort
            ON triggers (group_id, sort_order, lower(name));

        -- Append-only audit of every trigger evaluation. Rows are kept
        -- indefinitely in MVP; a retention sweep is added later if volume
        -- becomes a concern (1M tickets × N triggers can grow fast).
        -- ticket_event_id is BIGINT to match ticket_events.id (BIGSERIAL).
        CREATE TABLE IF NOT EXISTS trigger_runs (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            trigger_id          UUID        NOT NULL REFERENCES triggers(id)      ON DELETE CASCADE,
            ticket_id           UUID        NOT NULL REFERENCES tickets(id)       ON DELETE CASCADE,
            ticket_event_id     BIGINT      NULL     REFERENCES ticket_events(id) ON DELETE SET NULL,
            fired_utc           TIMESTAMPTZ NOT NULL DEFAULT now(),
            outcome             TEXT        NOT NULL,
            applied_changes     JSONB       NULL,
            error_class         TEXT        NULL,
            error_message       TEXT        NULL
        );

        ALTER TABLE trigger_runs DROP CONSTRAINT IF EXISTS chk_trigger_runs_outcome;
        ALTER TABLE trigger_runs ADD CONSTRAINT chk_trigger_runs_outcome
            CHECK (outcome IN ('applied','skipped_no_match','skipped_loop','failed')) NOT VALID;

        -- Per-ticket history (timeline drawer: "which triggers touched this ticket?")
        CREATE INDEX IF NOT EXISTS ix_trigger_runs_ticket
            ON trigger_runs (ticket_id, fired_utc DESC);

        -- Per-trigger history (admin run-log page: "is this trigger still firing?")
        CREATE INDEX IF NOT EXISTS ix_trigger_runs_trigger
            ON trigger_runs (trigger_id, fired_utc DESC);

        -- v0.0.24 (post-feature) — chained pending-till. When a trigger's
        -- set_pending_till action carries a `nextTriggerId`, the handler
        -- writes that GUID here. The scheduler's reminder scan checks this
        -- pointer first: if non-null, the chained trigger fires *exclusively*
        -- for that ticket on this pending-cycle (other reminder triggers
        -- skip this ticket until the pointer clears). The pointer is cleared
        -- after the chained trigger runs (Applied/Failed/SkippedNoMatch all
        -- count) so the next pending-cycle is explicit. ON DELETE SET NULL
        -- so removing the chained trigger doesn't strand the ticket.
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS pending_till_next_trigger_id UUID NULL
                REFERENCES triggers(id) ON DELETE SET NULL;

        -- Sparse index for the scheduler's chain-aware reminder scan.
        -- The vast majority of pending-till tickets have no chain, so the
        -- WHERE-clause keeps this index tiny.
        CREATE INDEX IF NOT EXISTS ix_tickets_pending_till_next_trigger
            ON tickets (pending_till_next_trigger_id)
            WHERE pending_till_next_trigger_id IS NOT NULL;

        -- v0.0.25 — Adsolut OAuth integration. Single-row connection state
        -- (one Adsolut administration links to one servicedesk install). The
        -- secrets (client_secret, refresh_token) live in protected_secrets
        -- under the Adsolut.* keys; this row carries the non-secret
        -- bookkeeping the UI shows (who authorized, when, when last refreshed)
        -- so admins can spot a stale connection at a glance. Singleton
        -- enforced via PRIMARY KEY DEFAULT 1 + CHECK; Disconnect deletes the
        -- row and the matching protected_secrets entries together.
        CREATE TABLE IF NOT EXISTS adsolut_connection (
            id                          INTEGER PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            authorized_subject          TEXT        NULL,
            authorized_email            TEXT        NULL,
            authorized_utc              TIMESTAMPTZ NULL,
            last_refreshed_utc          TIMESTAMPTZ NULL,
            access_token_expires_utc    TIMESTAMPTZ NULL,
            last_refresh_error          TEXT        NULL,
            last_refresh_error_utc      TIMESTAMPTZ NULL,
            updated_utc                 TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- v0.0.25 — operational integration log. Distinct from audit_log:
        -- this table captures every upstream call (latency, http status,
        -- upstream error codes) and every healthcheck tick, without the
        -- hash-chain or actor-required columns that audit_log carries.
        -- audit_log keeps the admin-action security trail (who clicked what,
        -- tamper-evident); integration_audit is for "is the integration
        -- healthy and how slow has it been". Outcome is constrained to a
        -- short whitelist so an admin overview can colour-code rows without
        -- a string-soup of variants.
        CREATE TABLE IF NOT EXISTS integration_audit (
            id              BIGSERIAL   PRIMARY KEY,
            utc             TIMESTAMPTZ NOT NULL DEFAULT now(),
            integration     TEXT        NOT NULL,
            event_type      TEXT        NOT NULL,
            outcome         TEXT        NOT NULL CHECK (outcome IN ('ok','warn','error')),
            endpoint        TEXT        NULL,
            http_status     INTEGER     NULL,
            latency_ms      INTEGER     NULL,
            actor_id        TEXT        NULL,
            actor_role      TEXT        NULL,
            error_code      TEXT        NULL,
            payload         JSONB       NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE INDEX IF NOT EXISTS ix_integration_audit_integration_utc
            ON integration_audit (integration, utc DESC, id DESC);
        CREATE INDEX IF NOT EXISTS ix_integration_audit_outcome_utc
            ON integration_audit (outcome, utc DESC)
            WHERE outcome <> 'ok';

        -- v0.0.26 — Adsolut Companies pull. Two columns on companies that
        -- track the Adsolut linkage (adsolut_id is the canonical FK once
        -- the first sync linked the row; adsolut_last_modified mirrors
        -- the Adsolut-side timestamp so the worker can tell whether the
        -- upstream row advanced since the last tick). The sparse unique
        -- index keeps the FK reversible without forcing every legacy
        -- company row to carry an adsolut_id.
        ALTER TABLE companies
            ADD COLUMN IF NOT EXISTS adsolut_id            UUID         NULL,
            ADD COLUMN IF NOT EXISTS adsolut_last_modified TIMESTAMPTZ  NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_companies_adsolut_id
            ON companies (adsolut_id) WHERE adsolut_id IS NOT NULL;

        -- One Adsolut administration (dossier) per servicedesk install. The
        -- column stays NULL between connect and dossier-pick so the UI can
        -- prompt the admin to choose; the sync worker skips ticks while it
        -- is NULL.
        ALTER TABLE adsolut_connection
            ADD COLUMN IF NOT EXISTS administration_id UUID NULL;

        -- Snapshot of Settings.Adsolut.Scopes at the moment the admin last
        -- completed an authorize-callback. The current setting can drift
        -- afterwards (admin edits the picker without reconnecting); the
        -- /status endpoint compares this column against the current value
        -- and surfaces a "Reconnect required" pill when they differ. Only
        -- written on successful callback — refresh-token rotation does not
        -- mint new scopes, so the value persists across refreshes.
        ALTER TABLE adsolut_connection
            ADD COLUMN IF NOT EXISTS scopes_at_authorize TEXT NULL;

        -- Singleton sync-state row. Not joined into adsolut_connection
        -- because the connection row resets on disconnect (and we want the
        -- sync cursor to survive reconnects against the same dossier),
        -- and because lumping operational counters into the auth-state row
        -- would mix concerns the UI surfaces in different sections.
        CREATE TABLE IF NOT EXISTS adsolut_sync_state (
            id                                  INTEGER     PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            last_full_sync_utc                  TIMESTAMPTZ NULL,
            last_delta_sync_utc                 TIMESTAMPTZ NULL,
            last_error                          TEXT        NULL,
            last_error_utc                      TIMESTAMPTZ NULL,
            companies_seen                      INTEGER     NOT NULL DEFAULT 0,
            companies_upserted                  INTEGER     NOT NULL DEFAULT 0,
            companies_skipped_loser_in_conflict INTEGER     NOT NULL DEFAULT 0,
            updated_utc                         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- v0.0.27 — admin "Acknowledge" timestamp for the integrations
        -- health card. The Sync check on /dashboard goes back to green once
        -- acknowledged_utc >= last_error_utc; the next failed tick (which
        -- updates last_error_utc) flips it back to amber automatically.
        ALTER TABLE adsolut_sync_state
            ADD COLUMN IF NOT EXISTS acknowledged_utc TIMESTAMPTZ NULL;

        -- ERP SalesReceipts (verkoopbonnen) mirror. Read-only mirror of the
        -- Adsolut ERP API SalesReceiptInfos endpoint, feeding the Timesheet →
        -- Adsolut tab. Header + two child line-sets (product details +
        -- labour performances). The API exposes NO header total, so
        -- total_excl_vat is computed at sync time as
        --   sum(detail.total_price_excl_vat) + sum(performance.invoice_total).
        -- The list endpoint is "light" (omits performances), so the sync
        -- fetches each receipt by-id; children are replaced wholesale per
        -- receipt on each upsert. ON DELETE CASCADE keeps the child rows in
        -- lockstep when a receipt is removed.
        CREATE TABLE IF NOT EXISTS adsolut_sales_receipts (
            id                      UUID          PRIMARY KEY,
            doc_nr                  INTEGER       NULL,
            book_code               TEXT          NULL,
            customer_adsolut_id     UUID          NULL,
            customer_code           TEXT          NULL,
            customer_name           TEXT          NULL,
            state_id                UUID          NULL,
            state_code              TEXT          NULL,
            state_description       TEXT          NULL,
            sales_receipt_date      TIMESTAMPTZ   NULL,
            description             TEXT          NULL,
            internal_memo           TEXT          NULL,
            memo                    TEXT          NULL,
            employee_code           TEXT          NULL,
            employee_name           TEXT          NULL,
            employee_email          TEXT          NULL,
            representative_code     TEXT          NULL,
            representative_name     TEXT          NULL,
            currency_iso            TEXT          NULL,
            vat_included            BOOLEAN       NOT NULL DEFAULT FALSE,
            total_excl_vat          NUMERIC(18,2) NOT NULL DEFAULT 0,
            adsolut_created_utc     TIMESTAMPTZ   NULL,
            adsolut_last_modified   TIMESTAMPTZ   NULL,
            synced_utc              TIMESTAMPTZ   NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_sales_receipts_state
            ON adsolut_sales_receipts (state_code);
        CREATE INDEX IF NOT EXISTS ix_adsolut_sales_receipts_date
            ON adsolut_sales_receipts (sales_receipt_date DESC);
        CREATE INDEX IF NOT EXISTS ix_adsolut_sales_receipts_customer
            ON adsolut_sales_receipts (customer_adsolut_id);

        CREATE TABLE IF NOT EXISTS adsolut_sales_receipt_lines (
            id                      UUID          PRIMARY KEY,
            receipt_id              UUID          NOT NULL
                REFERENCES adsolut_sales_receipts (id) ON DELETE CASCADE,
            line_nr                 INTEGER       NULL,
            product_code            TEXT          NULL,
            name                    TEXT          NULL,
            description             TEXT          NULL,
            quantity                NUMERIC(18,4) NULL,
            unit_code               TEXT          NULL,
            unit_price              NUMERIC(18,4) NULL,
            total_excl_vat          NUMERIC(18,2) NULL,
            total_incl_vat          NUMERIC(18,2) NULL,
            vat_code                TEXT          NULL
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_sales_receipt_lines_receipt
            ON adsolut_sales_receipt_lines (receipt_id);

        CREATE TABLE IF NOT EXISTS adsolut_sales_receipt_performances (
            id                       UUID          PRIMARY KEY,
            receipt_id               UUID          NOT NULL
                REFERENCES adsolut_sales_receipts (id) ON DELETE CASCADE,
            employee_code            TEXT          NULL,
            performance_date         TIMESTAMPTZ   NULL,
            from_time                TEXT          NULL,
            until_time               TEXT          NULL,
            duration_minutes         NUMERIC(18,4) NULL,
            invoice_duration_minutes NUMERIC(18,4) NULL,
            invoice_unit_price       NUMERIC(18,4) NULL,
            invoice_total            NUMERIC(18,2) NULL,
            performance_code         TEXT          NULL,
            description              TEXT          NULL
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_sales_receipt_perf_receipt
            ON adsolut_sales_receipt_performances (receipt_id);

        -- Singleton sync-state for the SalesReceipts mirror. Separate from
        -- adsolut_sync_state (Companies) so the two delta cursors are
        -- independent — pausing/enabling one never disturbs the other.
        CREATE TABLE IF NOT EXISTS adsolut_sales_receipt_sync_state (
            id                  INTEGER     PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            last_full_sync_utc  TIMESTAMPTZ NULL,
            last_delta_sync_utc TIMESTAMPTZ NULL,
            last_error          TEXT        NULL,
            last_error_utc      TIMESTAMPTZ NULL,
            receipts_seen       INTEGER     NOT NULL DEFAULT 0,
            receipts_upserted   INTEGER     NOT NULL DEFAULT 0,
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Ticket number parsed from the receipt description (pattern
        -- "Ticket#<digits>"). Stored at sync time so the Timesheet → Adsolut
        -- tab can join to tickets and sum the registered timesheet hours live
        -- (the hours themselves are never cached — they change independently
        -- of the receipt). NULL when the description carries no Ticket# ref.
        ALTER TABLE adsolut_sales_receipts
            ADD COLUMN IF NOT EXISTS ticket_number BIGINT NULL;

        CREATE INDEX IF NOT EXISTS ix_adsolut_sales_receipts_ticket_number
            ON adsolut_sales_receipts (ticket_number) WHERE ticket_number IS NOT NULL;

        -- One-shot backfill: receipts mirrored before the ticket_number column
        -- existed have it NULL. Parse it from the already-stored description
        -- ("Ticket#<digits>", case-tolerant on the T) so the registered-hours
        -- column works without forcing a full re-sync from Adsolut. Bounded +
        -- idempotent: only touches rows that are still NULL and actually carry
        -- a Ticket# reference, so it does meaningful work once and then
        -- updates zero rows on subsequent startups.
        UPDATE adsolut_sales_receipts
           SET ticket_number = NULLIF(substring(description from '[Tt]icket#([0-9]+)'), '')::bigint
         WHERE ticket_number IS NULL
           AND description ~ '[Tt]icket#[0-9]';

        -- v0.0.27 (push prep) — companies.adsolut_synced_hash carries the
        -- SHA-256 of the field-set we mirror to/from Adsolut (name, code,
        -- combined VAT, address-blok, phone, email). Set on every successful
        -- pull-update by AdsolutCompanyUpserter and on every successful push
        -- by the v0.0.27 push-tak. Acts as the no-op guard: when the inbound
        -- row hashes to the same value the local row already has, no UPDATE
        -- happens (no SignalR broadcast, no audit-ruis); when the local row
        -- hashes to the same value as adsolut_synced_hash, no PUT happens
        -- (closes the echo-pull loop). NULL until the first sync tick after
        -- this column was added.
        ALTER TABLE companies
            ADD COLUMN IF NOT EXISTS adsolut_synced_hash BYTEA NULL;

        -- v0.0.27 (push fix) — Adsolut's write-shape (UpdateCustomerRequest /
        -- AddCustomerRequest) carries `alphaCode` + `number` (klantnummer),
        -- not the read-side `code` we already mirror. Both must round-trip
        -- on PUT or WK rejects with `UpdateCustomerNumberNotValid` (it
        -- treats an absent `number` as "clear klantnummer" which is
        -- forbidden after creation). We mirror both here so the push-tak
        -- can echo them back unchanged. Pull-side populates them on the
        -- next tick after this column was added; rows that haven't seen a
        -- pull-tick since the column appeared stay NULL — the push-tak
        -- skips a row with NULL adsolut_number on update to avoid the
        -- WK rejection (next pull fixes it).
        ALTER TABLE companies
            ADD COLUMN IF NOT EXISTS adsolut_number      TEXT NULL,
            ADD COLUMN IF NOT EXISTS adsolut_alpha_code  TEXT NULL;

        -- One-shot data migrations (non-idempotent in nature, so they need
        -- a marker rather than relying on IF NOT EXISTS). The schema-side
        -- ALTERs above stay idempotent; this table tracks the rare
        -- one-time UPDATEs that align historical data after a behavioural
        -- change.
        CREATE TABLE IF NOT EXISTS data_migrations (
            name        TEXT        PRIMARY KEY,
            applied_utc TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- v0.0.27 (push prep) — eenmalige aligning van updated_utc met
        -- adsolut_last_modified voor companies-rijen die puur door eerdere
        -- pulls werden geraakt. Vóór v0.0.27 zette de upserter
        -- updated_utc = now() bij elke pull-update; dat patroon
        -- markeerde elke gepullde rij als "lokaal nieuwer" en zou de v0.0.27
        -- push-selector valse positieven laten oppikken zodra hij live gaat.
        --
        -- Conservatieve gates voorkomen dat een echte agent-edit per ongeluk
        -- wordt platgetrokken:
        --  1) adsolut_id IS NOT NULL   — alleen Adsolut-gelinkte rijen
        --  2) adsolut_last_modified IS NOT NULL
        --  3) updated_utc > adsolut_last_modified
        --  4) gap <= 1 minute          — agent-edits >1 min ná de pull
        --                                blijven onaangeraakt
        --
        -- Eén keer uitvoeren via data_migrations marker; volgende restarts
        -- zien hetzelfde marker en slaan over.
        DO $do$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM data_migrations
                WHERE name = 'v0_0_27_align_pull_touched_updated_utc'
            ) THEN
                UPDATE companies
                SET updated_utc = adsolut_last_modified
                WHERE adsolut_id IS NOT NULL
                  AND adsolut_last_modified IS NOT NULL
                  AND updated_utc > adsolut_last_modified
                  AND updated_utc - adsolut_last_modified <= INTERVAL '1 minute';
                INSERT INTO data_migrations (name)
                    VALUES ('v0_0_27_align_pull_touched_updated_utc');
            END IF;
        END $do$;

        -- v0.0.27 — bestaande installs die op de v0.0.26-scope-lijst zaten
        -- ('openid offline_access profile WK.BE.Administrations
        -- WK.BE.Accounting.Read') krijgen WK.BE.Accounting.Write erbij,
        -- anders komt elke push-tak PUT/POST terug als 403 vanuit Wolters
        -- Kluwer. Niet idempotent qua effect (we schrijven settings.value),
        -- dus achter een data_migrations marker. De saved scope-string zal
        -- na deze append verschillen van scopes_at_authorize op de actieve
        -- connectie → de "Reconnect required"-pill in de UI fire'd
        -- automatisch zodra de admin de pagina opent.
        --
        -- We raken alleen de Adsolut.Scopes-rij aan, alleen wanneer de
        -- scope nog ontbreekt, en preserveren de bestaande spaties +
        -- volgorde door eenvoudig te concateneren.
        DO $do$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM data_migrations
                WHERE name = 'v0_0_27_add_accounting_write_scope'
            ) THEN
                UPDATE settings
                SET value = btrim(value || ' WK.BE.Accounting.Write'),
                    updated_utc = now()
                WHERE key = 'Adsolut.Scopes'
                  AND value IS NOT NULL
                  AND value NOT LIKE '%WK.BE.Accounting.Write%';
                INSERT INTO data_migrations (name)
                    VALUES ('v0_0_27_add_accounting_write_scope');
            END IF;
        END $do$;

        -- ===================================================================
        -- v0.0.28 — Adsolut Contacts pull (Adsolut → SD)
        --
        -- Adsolut models one contact-row per customer-relationship: three
        -- "Wendies" on customers A/B/C live as three different UUIDs with
        -- their own lastModified + active. SD's contacts.email is CITEXT
        -- NOT NULL UNIQUE, so the same person across three customers is one
        -- contacts row + three contact_companies links. The per-link Adsolut
        -- state lives on contact_companies, not contacts:
        --
        --   adsolut_contact_id    — Adsolut's UUID for THIS link/relationship.
        --                           Partial-unique where NOT NULL so two
        --                           links can never claim the same UUID.
        --   adsolut_last_modified — UTC timestamp from Adsolut. Drives the
        --                           per-link LWW conflict tie-breaker (the
        --                           link with the highest stamp wins for
        --                           the contact-level fields first/last/
        --                           phone/mobile_phone).
        --   adsolut_active        — true / false from Adsolut. contacts.is_
        --                           active is derived: TRUE iff ≥1 link is
        --                           active OR no Adsolut links exist (pure
        --                           SD-side contact).
        --   adsolut_synced_hash   — SHA-256 over the four mirrored fields.
        --                           No-op guard against echo-pull (and the
        --                           v0.0.29 push) the same way companies'
        --                           adsolut_synced_hash works.
        --
        -- contacts.mobile_phone is the new column for Adsolut's mobilePhone
        -- field. Existing rows back-fill to '' which is fine — the contact
        -- detail page renders both phone fields side by side and an empty
        -- value is hidden in the UI.
        -- ===================================================================
        ALTER TABLE contacts
            ADD COLUMN IF NOT EXISTS mobile_phone TEXT NOT NULL DEFAULT '';

        ALTER TABLE contact_companies
            ADD COLUMN IF NOT EXISTS adsolut_contact_id    UUID         NULL,
            ADD COLUMN IF NOT EXISTS adsolut_last_modified TIMESTAMPTZ  NULL,
            ADD COLUMN IF NOT EXISTS adsolut_active        BOOLEAN      NULL,
            ADD COLUMN IF NOT EXISTS adsolut_synced_hash   BYTEA        NULL;

        -- Sparse unique index — only enforced where the column is populated.
        -- A pure-SD link (never touched by Adsolut sync) keeps the column
        -- NULL and is exempt; the index size stays tiny on installs with a
        -- mix of imported + manually-added contacts.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_contact_companies_adsolut_contact_id
            ON contact_companies (adsolut_contact_id) WHERE adsolut_contact_id IS NOT NULL;

        -- Reconcile-loop hot-path: "all Adsolut-linked contact_companies for
        -- this company". Partial index keeps NULL-link rows out of the scan.
        CREATE INDEX IF NOT EXISTS ix_contact_companies_company_adsolut
            ON contact_companies (company_id) WHERE adsolut_contact_id IS NOT NULL;

        -- ===================================================================
        -- v0.0.31 — Knowledge Base (standalone, agent-internal)
        --
        -- Singleton config row in knowledge_base, supported locales in
        -- kb_locales (nl-BE seeded active, en-US seeded inactive secondary),
        -- recursive section tree (kb_sections) with per-locale labels in
        -- kb_section_translations, and articles with explicit status enum
        -- (Draft|Internal|Published|Archived) + featured flag + private
        -- editor_notes in kb_articles. Per-locale title/body live in
        -- kb_article_translations; body_html is sanitized server-side
        -- before write and body_text (the HTML-stripped form) drives the
        -- generated tsvector search_vector.
        --
        -- Status is an enum, not a triple of nullable timestamps:
        -- last_status_changed_utc + last_status_changed_by_user_id capture
        -- the last flip; full historical flips live in audit_log via
        -- kb.article.status.changed events. publish_at / archive_at are
        -- reserved columns for a future KbScheduleWorker; both stay NULL
        -- in v0.0.31.
        --
        -- Attachments hergebruiken het bestaande blob-pipeline-pattern via
        -- owner_kind='KbArticle'. Geen separate KB-attachment-tabel.
        --
        -- FTS-config: 'simple' + lower() (mirrors the existing pattern in
        -- mail_messages.search_vector — unaccent() is STABLE and cannot
        -- live in a STORED generated column on this install layout).
        -- ===================================================================

        -- Extend the existing attachments owner-kind whitelist to include
        -- KbArticle. Constraint is dropped + re-added so the SQL stays
        -- idempotent across upgrades from older schemas.
        ALTER TABLE attachments DROP CONSTRAINT IF EXISTS chk_attachments_owner_kind;
        ALTER TABLE attachments ADD CONSTRAINT chk_attachments_owner_kind
            CHECK (owner_kind IN ('Mail','Ticket','User','KbArticle'));

        CREATE TABLE IF NOT EXISTS kb_locales (
            code            TEXT        PRIMARY KEY,
            display_name    TEXT        NOT NULL,
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            CONSTRAINT chk_kb_locales_code_format
                CHECK (code ~ '^[a-z]{2,3}(-[A-Z][a-zA-Z]+)?(-[A-Z]{2})?$')
        );

        INSERT INTO kb_locales (code, display_name, is_active, sort_order) VALUES
            ('nl-BE', 'Nederlands (België)', TRUE,  0),
            ('en-US', 'English (United States)', FALSE, 1)
            ON CONFLICT (code) DO NOTHING;

        CREATE TABLE IF NOT EXISTS knowledge_base (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            is_active           BOOLEAN     NOT NULL DEFAULT TRUE,
            default_locale_code TEXT        NOT NULL DEFAULT 'nl-BE'
                                            REFERENCES kb_locales(code) ON UPDATE CASCADE,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Singleton enforcement: at most one row, ever. The ON ((TRUE))
        -- expression yields the same key for every row, so a second insert
        -- always conflicts. The seed below inserts the single row only when
        -- the table is empty.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_knowledge_base_singleton
            ON knowledge_base ((TRUE));

        INSERT INTO knowledge_base (default_locale_code)
            SELECT 'nl-BE'
            WHERE NOT EXISTS (SELECT 1 FROM knowledge_base);

        CREATE TABLE IF NOT EXISTS kb_sections (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            parent_section_id   UUID        NULL REFERENCES kb_sections(id) ON DELETE RESTRICT,
            slug                CITEXT      NOT NULL,
            icon_name           TEXT        NULL,
            position            INTEGER     NOT NULL DEFAULT 0,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by_user_id  UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            updated_by_user_id  UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            CONSTRAINT chk_kb_sections_slug_format
                CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
            CONSTRAINT chk_kb_sections_no_self_parent
                CHECK (parent_section_id IS NULL OR parent_section_id <> id)
        );

        -- Slug-uniqueness within siblings. PostgreSQL treats NULL <> NULL in
        -- composite UNIQUE constraints, so two partial unique indexes are
        -- needed: one for root-level sections (parent_section_id IS NULL),
        -- one for child sections. Multi-level cycles are caught at the API
        -- layer in the section-move endpoint.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_kb_sections_root_slug
            ON kb_sections (slug) WHERE parent_section_id IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_kb_sections_child_slug
            ON kb_sections (parent_section_id, slug) WHERE parent_section_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_kb_sections_parent_position
            ON kb_sections (parent_section_id, position);

        CREATE TABLE IF NOT EXISTS kb_section_translations (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            section_id      UUID        NOT NULL REFERENCES kb_sections(id) ON DELETE CASCADE,
            locale_code     TEXT        NOT NULL REFERENCES kb_locales(code) ON UPDATE CASCADE,
            title           TEXT        NOT NULL,
            description     TEXT        NULL,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_kb_section_translations_title_len
                CHECK (length(title) BETWEEN 1 AND 250),
            CONSTRAINT chk_kb_section_translations_description_len
                CHECK (description IS NULL OR length(description) <= 1000),
            CONSTRAINT ux_kb_section_translations_section_locale
                UNIQUE (section_id, locale_code)
        );

        CREATE INDEX IF NOT EXISTS ix_kb_section_translations_section
            ON kb_section_translations (section_id);

        CREATE TABLE IF NOT EXISTS kb_articles (
            id                              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            section_id                      UUID        NOT NULL REFERENCES kb_sections(id) ON DELETE RESTRICT,
            slug                            CITEXT      NOT NULL,
            status                          TEXT        NOT NULL DEFAULT 'Draft',
            is_featured                     BOOLEAN     NOT NULL DEFAULT FALSE,
            editor_notes                    TEXT        NULL,
            position                        INTEGER     NOT NULL DEFAULT 0,
            publish_at                      TIMESTAMPTZ NULL,
            archive_at                      TIMESTAMPTZ NULL,
            last_status_changed_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_status_changed_by_user_id  UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            created_utc                     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc                     TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by_user_id              UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            updated_by_user_id              UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            CONSTRAINT chk_kb_articles_status
                CHECK (status IN ('Draft','Internal','Published','Archived')),
            CONSTRAINT chk_kb_articles_slug_format
                CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
            CONSTRAINT ux_kb_articles_section_slug
                UNIQUE (section_id, slug)
        );

        -- Listing hot path: articles per section, status-filtered, ordered
        -- by position. Archived rows are excluded so the index stays small
        -- on installs that accumulate retired articles.
        CREATE INDEX IF NOT EXISTS ix_kb_articles_section_status_position
            ON kb_articles (section_id, status, position) WHERE status <> 'Archived';

        -- Featured-tile on /kb landing.
        CREATE INDEX IF NOT EXISTS ix_kb_articles_featured
            ON kb_articles (updated_utc DESC) WHERE is_featured AND status = 'Published';

        -- Reserved for a future KbScheduleWorker (publish_at / archive_at
        -- automatic flips). publish_at is intentionally NULL in v0.0.31; the
        -- partial index has effectively zero rows so the cost is negligible.
        CREATE INDEX IF NOT EXISTS ix_kb_articles_publish_at_pending
            ON kb_articles (publish_at)
            WHERE publish_at IS NOT NULL AND status = 'Draft';

        -- Trigram fuzzy match on slug for typeahead-style lookups.
        CREATE INDEX IF NOT EXISTS ix_kb_articles_slug_trgm
            ON kb_articles USING GIN ((lower(slug::text)) gin_trgm_ops);

        CREATE TABLE IF NOT EXISTS kb_article_translations (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            article_id      UUID        NOT NULL REFERENCES kb_articles(id) ON DELETE CASCADE,
            locale_code     TEXT        NOT NULL REFERENCES kb_locales(code) ON UPDATE CASCADE,
            title           TEXT        NOT NULL,
            body_html       TEXT        NOT NULL DEFAULT '',
            body_text       TEXT        NOT NULL DEFAULT '',
            search_vector   TSVECTOR    GENERATED ALWAYS AS (
                                setweight(to_tsvector('simple', lower(coalesce(title, ''))), 'A') ||
                                setweight(to_tsvector('simple', lower(coalesce(body_text, ''))), 'B')
                            ) STORED,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_kb_article_translations_title_len
                CHECK (length(title) BETWEEN 1 AND 250),
            CONSTRAINT ux_kb_article_translations_article_locale
                UNIQUE (article_id, locale_code)
        );

        CREATE INDEX IF NOT EXISTS ix_kb_article_translations_search_vector
            ON kb_article_translations USING GIN (search_vector);
        CREATE INDEX IF NOT EXISTS ix_kb_article_translations_title_trgm
            ON kb_article_translations USING GIN ((lower(title)) gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_kb_article_translations_article
            ON kb_article_translations (article_id);

        -- Seed a single root section "General" so a fresh install lands on
        -- a non-empty /kb landing page. Admin renames or deletes as needed.
        DO $do$
        DECLARE
            v_section_id        UUID;
            v_default_locale    TEXT;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM kb_sections) THEN
                SELECT default_locale_code INTO v_default_locale FROM knowledge_base LIMIT 1;
                INSERT INTO kb_sections (slug, position) VALUES ('general', 0)
                    RETURNING id INTO v_section_id;
                INSERT INTO kb_section_translations (section_id, locale_code, title, description)
                    VALUES (v_section_id, COALESCE(v_default_locale, 'nl-BE'),
                            'General',
                            'Default section seeded on install. Rename or remove as you organise the knowledge base.');
            END IF;
        END $do$;

        -- ===================================================================
        -- v0.0.34 — Telavox call-popup integration
        -- ===================================================================
        -- E.164-normalised mirrors of phone / mobile_phone. Populated on
        -- every contact write through ContactPhoneNormalizer; existing rows
        -- backfilled lazily by ContactPhoneBackfillService after this
        -- bootstrap. Empty string when the input couldn't be parsed (kept
        -- as '' rather than NULL so phone-search just doesn't hit on
        -- garbage input, no JOIN special-casing).
        ALTER TABLE contacts
            ADD COLUMN IF NOT EXISTS phone_e164         TEXT NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS mobile_phone_e164  TEXT NOT NULL DEFAULT '';

        -- Partial indices: only rows with a parsed E.164 number contribute,
        -- so the index stays compact and an incoming-call phone lookup is
        -- a single equality probe (no LIKE, no trgm).
        CREATE INDEX IF NOT EXISTS ix_contacts_phone_e164
            ON contacts (phone_e164)
            WHERE phone_e164 <> '';
        CREATE INDEX IF NOT EXISTS ix_contacts_mobile_phone_e164
            ON contacts (mobile_phone_e164)
            WHERE mobile_phone_e164 <> '';

        -- SD-user ↔ Telavox-extension mapping. One row per linked agent;
        -- admins create/remove via the Telavox integration page. The
        -- per-agent CAPI token itself lives in protected_secrets under
        -- Telavox.AgentCapiToken.{user_id} (encrypted via DataProtection);
        -- this table only keeps the non-secret bookkeeping the worker and
        -- the UI need. ON DELETE CASCADE so de-provisioning a user wipes
        -- the link, the protected secret is cleared in code alongside.
        CREATE TABLE IF NOT EXISTS telavox_agent_links (
            id                      UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            user_id                 UUID        NOT NULL UNIQUE REFERENCES users(id) ON DELETE CASCADE,
            telavox_extension       TEXT        NOT NULL,
            telavox_user_id         TEXT        NOT NULL,
            capi_user_email         TEXT        NOT NULL,
            provisioned_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_poll_utc           TIMESTAMPTZ NULL,
            last_poll_error         TEXT        NULL,
            consecutive_errors      INT         NOT NULL DEFAULT 0,
            created_utc             TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc             TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_telavox_agent_links_extension
            ON telavox_agent_links (telavox_extension);

        -- Per-agent transition-detection state. The popup fires on
        -- RINGING→ANSWERED on the agent's own extension; last_call_id +
        -- last_state form the baseline the next poll tick compares against
        -- so a long-running ANSWERED call doesn't re-trigger every tick.
        CREATE TABLE IF NOT EXISTS telavox_call_state (
            user_id         UUID        PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            last_call_id    TEXT        NULL,
            last_state      TEXT        NULL,
            last_seen_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- v0.0.78 — persist the call direction alongside the baseline so the
        -- "call completed" activity row can record incoming vs. outgoing on
        -- the answered→idle edge, where the live call snapshot is already
        -- gone. NULL at idle, "incoming" / "outgoing" while a call is active.
        ALTER TABLE telavox_call_state
            ADD COLUMN IF NOT EXISTS last_direction TEXT NULL;

        -- v0.0.78 — talk-time anchor: the UTC moment the active call first
        -- reached the answered state, held steady across ticks for the same
        -- call. The completed-call activity row subtracts this from the
        -- hangup time to report talk-time. NULL while only ringing, and idle.
        ALTER TABLE telavox_call_state
            ADD COLUMN IF NOT EXISTS answered_at_utc TIMESTAMPTZ NULL;

        -- ===================================================================
        -- v0.0.35 Timesheet — per-user feature flags. Two independent
        -- booleans live directly on the users row (no new role beside
        -- Customer/Agent/Admin):
        --   timesheet_enabled  — may register own hours (Tab 1).
        --   timesheet_manager  — may see Tab 2/3 and edit/delete others'
        --                         entries. Independent of timesheet_enabled
        --                         (a manager need not self-register).
        -- Both default FALSE so an upgrade is silent — admins opt agents
        -- in explicitly. Customer rows can carry the flags but the API
        -- layer rejects the mutation for Customers.
        -- ===================================================================
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS timesheet_enabled BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS timesheet_manager BOOLEAN NOT NULL DEFAULT FALSE;

        -- v0.0.35-E — per-user Timesheet preference overrides. NULL means
        -- "use the global default" from the settings table; a non-NULL
        -- value overrides for this one user. Storing override-or-NULL
        -- (instead of always-set) means: (a) an admin who hasn't customised
        -- the user sees blank fields in the UI and (b) bumping the global
        -- default cascades to every uncustomised user automatically.
        --
        -- timesheet_work_days is stored as a comma-separated list of ISO
        -- weekday numbers (1=Mon..7=Sun). The CHECK keeps it parseable —
        -- empty string is fine ("no work days"), otherwise only digits +
        -- commas and only weekday numbers (1..7). A JSON array would be
        -- more "typed" but adds a json_array_elements decode in every
        -- read; CSV stays trivial.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS timesheet_start_minutes              INT  NULL,
            ADD COLUMN IF NOT EXISTS timesheet_target_day_minutes         INT  NULL,
            ADD COLUMN IF NOT EXISTS timesheet_target_week_minutes        INT  NULL,
            ADD COLUMN IF NOT EXISTS timesheet_work_days                  TEXT NULL,
            -- v0.0.36 — daily ceiling on absence-task minutes before the
            -- week is flagged as "target not met". 0 = no limit; the flag
            -- effectively goes back to "only total time matters".
            ADD COLUMN IF NOT EXISTS timesheet_max_absence_day_minutes    INT  NULL,
            -- v0.0.36 — office-hour window. Tab 1 flags row-to-row gaps
            -- and overlaps in red when the mismatch zone falls inside
            -- this window. NULLs fall back to the global default.
            ADD COLUMN IF NOT EXISTS timesheet_office_start_minutes       INT  NULL,
            ADD COLUMN IF NOT EXISTS timesheet_office_end_minutes         INT  NULL;

        DO $ts_user_prefs_constraints$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_ts_start_minutes_range'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT ck_users_ts_start_minutes_range
                    CHECK (timesheet_start_minutes IS NULL
                           OR (timesheet_start_minutes BETWEEN 0 AND 1440));
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_ts_target_day_range'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT ck_users_ts_target_day_range
                    CHECK (timesheet_target_day_minutes IS NULL
                           OR (timesheet_target_day_minutes BETWEEN 0 AND 1440));
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_ts_target_week_range'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT ck_users_ts_target_week_range
                    CHECK (timesheet_target_week_minutes IS NULL
                           OR (timesheet_target_week_minutes BETWEEN 0 AND 10080));
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_ts_work_days_format'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT ck_users_ts_work_days_format
                    CHECK (timesheet_work_days IS NULL
                           OR timesheet_work_days = ''
                           OR timesheet_work_days ~ '^[1-7](,[1-7])*$');
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_ts_max_absence_day_range'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT ck_users_ts_max_absence_day_range
                    CHECK (timesheet_max_absence_day_minutes IS NULL
                           OR (timesheet_max_absence_day_minutes BETWEEN 0 AND 1440));
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_ts_office_start_range'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT ck_users_ts_office_start_range
                    CHECK (timesheet_office_start_minutes IS NULL
                           OR (timesheet_office_start_minutes BETWEEN 0 AND 1440));
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_ts_office_end_range'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT ck_users_ts_office_end_range
                    CHECK (timesheet_office_end_minutes IS NULL
                           OR (timesheet_office_end_minutes BETWEEN 0 AND 1440));
            END IF;
        END
        $ts_user_prefs_constraints$;

        -- ===================================================================
        -- v0.0.35 Timesheet — task catalogue + per-user entries.
        --
        -- timesheet_tasks is an admin-managed catalogue (Settings →
        -- Timesheet tasks). Each row carries two flags:
        --   requires_ticket — agent must link a ticket to the entry.
        --   is_absence      — entries on this task represent leave/sick
        --                     time and roll up separately in Tab 3 so a
        --                     verlof-dag doesn't look like a normal 8u-day.
        -- A partial unique index forbids two ACTIVE tasks with the same
        -- name (case-insensitive); archived rows are excluded so a name
        -- can be reused after retirement.
        --
        -- timesheet_entries stores one row per agent registration. Time
        -- is kept as `entry_date` (DATE) + `start_minutes`/`end_minutes`
        -- (minutes since local midnight, 0..1440). We do NOT store an
        -- absolute UTC timestamp for the work itself because the agent
        -- enters "8:30 to 10:00" as a local-day concept; a UTC timestamp
        -- would shift across DST transitions and be wrong for any agent
        -- in a different zone than the server. Audit columns (`created_*`,
        -- `updated_*`) DO use TIMESTAMPTZ — those are real events.
        --
        -- `minutes` is persisted (not GENERATED) so old PG versions don't
        -- complain and roll-up queries stay index-friendly. A CHECK keeps
        -- it in sync with end-start.
        -- ===================================================================

        CREATE TABLE IF NOT EXISTS timesheet_tasks (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            requires_ticket BOOLEAN     NOT NULL DEFAULT TRUE,
            is_absence      BOOLEAN     NOT NULL DEFAULT FALSE,
            archived        BOOLEAN     NOT NULL DEFAULT FALSE,
            sort_order      INT         NOT NULL DEFAULT 0,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_timesheet_tasks_name_active
            ON timesheet_tasks (lower(name)) WHERE archived = FALSE;

        -- Default catalogue. Idempotent via the same lower(name) gate as
        -- the unique index so admin renames/deletes survive a re-run.
        INSERT INTO timesheet_tasks (name, requires_ticket, is_absence, sort_order)
        SELECT v.name, v.requires_ticket, v.is_absence, v.sort_order
        FROM (VALUES
            ('Servicedesk',    TRUE,  FALSE, 10),
            ('Project',        TRUE,  FALSE, 20),
            ('Administratie',  FALSE, FALSE, 30),
            ('Vergadering',    FALSE, FALSE, 40),
            ('Verlof',         FALSE, TRUE,  50),
            ('Ziek',           FALSE, TRUE,  60)
        ) AS v(name, requires_ticket, is_absence, sort_order)
        WHERE NOT EXISTS (
            SELECT 1 FROM timesheet_tasks t
            WHERE lower(t.name) = lower(v.name)
        );

        -- Per-user default task for Tab-1 new rows. Lives on the user row
        -- (like the other timesheet_* preference columns) but is added here,
        -- after timesheet_tasks exists, so the FK can be declared. NULL = no
        -- preference, the UI then falls back to the first active task by
        -- sort order. ON DELETE SET NULL: archiving is the normal path, but a
        -- hard delete of a task just clears the preference rather than blocking.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS timesheet_default_task_id UUID NULL;
        DO $ts_user_default_task_fk$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'fk_users_ts_default_task'
            ) THEN
                ALTER TABLE users
                    ADD CONSTRAINT fk_users_ts_default_task
                    FOREIGN KEY (timesheet_default_task_id)
                    REFERENCES timesheet_tasks(id) ON DELETE SET NULL;
            END IF;
        END $ts_user_default_task_fk$;

        CREATE TABLE IF NOT EXISTS timesheet_entries (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            user_id         UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            entry_date      DATE        NOT NULL,
            start_minutes   INT         NOT NULL,
            end_minutes     INT         NOT NULL,
            minutes         INT         NOT NULL,
            task_id         UUID        NOT NULL REFERENCES timesheet_tasks(id) ON DELETE RESTRICT,
            ticket_id       UUID        NULL REFERENCES tickets(id) ON DELETE SET NULL,
            description     TEXT        NOT NULL,
            created_by      UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_by      UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT ck_timesheet_entries_time_window
                CHECK (start_minutes >= 0
                       AND end_minutes <= 1440
                       AND end_minutes > start_minutes
                       AND minutes = end_minutes - start_minutes)
        );
        CREATE INDEX IF NOT EXISTS ix_timesheet_entries_user_date
            ON timesheet_entries (user_id, entry_date);
        CREATE INDEX IF NOT EXISTS ix_timesheet_entries_date_user
            ON timesheet_entries (entry_date, user_id);
        CREATE INDEX IF NOT EXISTS ix_timesheet_entries_ticket
            ON timesheet_entries (ticket_id) WHERE ticket_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_timesheet_entries_task
            ON timesheet_entries (task_id);

        -- v0.0.36 — billed/invoiced flag. The toggle UI is added in a later
        -- iteration; for now the column is display-only in the manager grid
        -- and the per-ticket "Time logged" panel so the column shape is
        -- locked in and no second migration is needed when the toggle lands.
        ALTER TABLE timesheet_entries
            ADD COLUMN IF NOT EXISTS invoiced BOOLEAN NOT NULL DEFAULT FALSE;

        -- v0.0.54 — migration provenance. `import_source` names the source
        -- system (e.g. 'legacy-mssql-timesheet'); `import_ref` is that
        -- system's primary key for the row. The partial UNIQUE index makes a
        -- re-run idempotent: the import UPSERTs on (import_source, import_ref)
        -- instead of duplicating. Both are NULL on every normally-created
        -- row, so the partial index imposes no cost on the hot path and the
        -- columns stay invisible to the rest of the app.
        ALTER TABLE timesheet_entries
            ADD COLUMN IF NOT EXISTS import_source TEXT NULL,
            ADD COLUMN IF NOT EXISTS import_ref    TEXT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_timesheet_entries_import
            ON timesheet_entries (import_source, import_ref)
            WHERE import_source IS NOT NULL;

        -- Drop the old global pending-till defaults. Pending-till is now
        -- entirely trigger-driven (set_pending_till action, chained
        -- next_trigger_id for snap-on-expiry). These rows are orphaned
        -- on installs that previously read them and harmless to remove.
        DELETE FROM settings WHERE key IN (
            'Tickets.PendingDefaultBusinessDays',
            'Tickets.PendingDefaultWakeAtLocal',
            'Tickets.PendingExpirySnapToStatusSlug'
        );

        -- ===================================================================
        -- Compose Templates — pre-canned HTML snippets (mail/reply/note).
        --
        -- Agents pull a template into the active editor via the `::` picker
        -- (same trigger as intake forms; the picker merges both sources).
        -- queue_ids is an array of queues the template applies to; an empty
        -- array means "available in every queue" — the repo's filter does
        -- the union so the UI only ever sees relevant templates.
        -- body_html is sanitised on write (admin-authored, but still scoped
        -- through the same allow-list the rich-text bodies use elsewhere).
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS compose_templates (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            description     TEXT        NULL,
            body_html       TEXT        NOT NULL DEFAULT '',
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            queue_ids       UUID[]      NOT NULL DEFAULT '{}',
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by      UUID        NULL REFERENCES users(id) ON DELETE SET NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_compose_templates_active_name
            ON compose_templates (lower(name))
            WHERE is_active;

        CREATE INDEX IF NOT EXISTS ix_compose_templates_queue_ids
            ON compose_templates USING GIN (queue_ids);

        -- ===================================================================
        -- Tagging-only mailboxes — login-less @@-mention targets. Mentioning
        -- one in a note / reply / outbound mail sends a notification e-mail to
        -- its address; it has no user row, no role, no tickets and never signs
        -- in. Managed admin-only as the first card on Settings → Users. Email
        -- is stored lower-cased so the unique index is case-insensitive.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS tagging_mailboxes (
            id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name        TEXT        NOT NULL,
            email       TEXT        NOT NULL,
            is_active   BOOLEAN     NOT NULL DEFAULT TRUE,
            created_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_tagging_mailboxes_email
            ON tagging_mailboxes (lower(email));

        -- ===================================================================
        -- v0.0.38 customer satisfaction surveys (CSAT). See ARCHITECTURE.md →
        -- "Surveys". surveys + survey_questions hold the designer model.
        -- survey_invitations are per-send token-protected rows mirroring
        -- intake_form_instances. Submissions live in survey_responses (1 per
        -- invitation) + survey_answers (1 per answered question) +
        -- survey_agent_scores (1 per rated agent in per-agent rating mode).
        --
        -- Carve-out: a SurveySubmitted ticket-event MUST NOT trigger an
        -- auto-reopen. The response service skips the trigger evaluation
        -- mail-ingest fires; the agent-side reopen-rules can also filter on
        -- event_type != 'SurveySubmitted' if the admin wants to be explicit.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS surveys (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name                TEXT        NOT NULL,
            description         TEXT        NULL,
            intro_html          TEXT        NOT NULL DEFAULT '',
            invite_subject      TEXT        NOT NULL DEFAULT '',
            invite_body_html    TEXT        NOT NULL DEFAULT '',
            is_active           BOOLEAN     NOT NULL DEFAULT TRUE,
            ttl_days            INT         NULL,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by          UUID        NULL REFERENCES users(id) ON DELETE SET NULL
        );

        -- v0.0.38 redesign: per-agent rating is no longer a tri-state mode.
        -- Admins now define agent sub-questions (applies_to='Agent' on
        -- survey_questions); the public page renders each contributing
        -- agent with the full sub-question set. Drop the old column + check
        -- so a stale schema can't leak the deprecated rating mode.
        ALTER TABLE surveys DROP CONSTRAINT IF EXISTS chk_surveys_agent_rating_mode;
        ALTER TABLE surveys DROP COLUMN IF EXISTS agent_rating_mode;

        -- All surfaced text on the public survey is admin-defined so a
        -- Dutch-speaking customer never sees an English fallback. Nullable
        -- so older surveys (pre-rename) keep loading; the API enforces
        -- non-empty values on save.
        ALTER TABLE surveys ADD COLUMN IF NOT EXISTS agent_block_heading TEXT NULL;
        ALTER TABLE surveys ADD COLUMN IF NOT EXISTS submit_button_label TEXT NULL;
        ALTER TABLE surveys ADD COLUMN IF NOT EXISTS thank_you_message   TEXT NULL;
        ALTER TABLE surveys ADD COLUMN IF NOT EXISTS expired_message     TEXT NULL;
        ALTER TABLE surveys ADD COLUMN IF NOT EXISTS not_found_message   TEXT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_surveys_active_name
            ON surveys (lower(name)) WHERE is_active;

        CREATE TABLE IF NOT EXISTS survey_questions (
            id              BIGSERIAL PRIMARY KEY,
            survey_id       UUID NOT NULL REFERENCES surveys(id) ON DELETE CASCADE,
            sort_order      INT  NOT NULL,
            question_type   TEXT NOT NULL,
            label           TEXT NOT NULL,
            help_text       TEXT NULL,
            is_required     BOOLEAN NOT NULL DEFAULT FALSE,
            -- Type-specific options (rating: { points: 5, labels: ["Bad","OK","Good"] },
            -- choice: { options: [{value,label}, ...], multi: false }). Empty
            -- object for Text/Nps which need no config.
            config_json     JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        -- v0.0.38 redesign: scope distinguishes 'Survey' questions (asked
        -- once) from 'Agent' questions (rendered per contributing agent at
        -- submit time). Sort-order is unique within (survey, scope), not
        -- globally, so each list reorders independently.
        ALTER TABLE survey_questions ADD COLUMN IF NOT EXISTS applies_to TEXT NOT NULL DEFAULT 'Survey';
        ALTER TABLE survey_questions DROP CONSTRAINT IF EXISTS chk_survey_question_applies_to;
        ALTER TABLE survey_questions ADD CONSTRAINT chk_survey_question_applies_to
            CHECK (applies_to IN ('Survey','Agent')) NOT VALID;

        -- Replace the legacy (survey_id, sort_order) UNIQUE with one scoped
        -- per applies_to. PG names inline UNIQUE constraints predictably.
        ALTER TABLE survey_questions DROP CONSTRAINT IF EXISTS survey_questions_survey_id_sort_order_key;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_survey_questions_scope_sort
            ON survey_questions (survey_id, applies_to, sort_order);

        ALTER TABLE survey_questions DROP CONSTRAINT IF EXISTS chk_survey_question_type;
        ALTER TABLE survey_questions ADD CONSTRAINT chk_survey_question_type
            CHECK (question_type IN ('Rating','Nps','Text','SingleChoice','MultiChoice')) NOT VALID;

        CREATE TABLE IF NOT EXISTS survey_invitations (
            id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            survey_id               UUID NOT NULL REFERENCES surveys(id) ON DELETE RESTRICT,
            ticket_id               UUID NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            sent_event_id           BIGINT NULL REFERENCES ticket_events(id) ON DELETE SET NULL,
            submitted_event_id      BIGINT NULL REFERENCES ticket_events(id) ON DELETE SET NULL,
            token_hash              BYTEA NOT NULL,
            token_cipher            BYTEA NOT NULL,
            status                  TEXT NOT NULL DEFAULT 'Sent',
            sent_to_email           TEXT NOT NULL,
            sent_utc                TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_utc             TIMESTAMPTZ NOT NULL,
            submitted_utc           TIMESTAMPTZ NULL,
            cancelled_utc           TIMESTAMPTZ NULL,
            -- Snapshot of contributing agent user-ids at send-time. Frozen so
            -- a later assignment-change can't retroactively shuffle the
            -- per-agent rating-blocks the customer answered against.
            attributed_agent_ids    UUID[] NOT NULL DEFAULT '{}',
            -- Frozen survey definition (name + questions + agent-rating mode)
            -- so an admin can rewrite the live survey without corrupting
            -- pending invitations or historical responses.
            survey_snapshot_json    JSONB NOT NULL DEFAULT '{}'::jsonb,
            submitter_ip            INET NULL,
            submitter_ua            TEXT NULL,
            created_by              UUID NULL REFERENCES users(id) ON DELETE SET NULL
        );

        ALTER TABLE survey_invitations DROP CONSTRAINT IF EXISTS chk_survey_invitation_status;
        ALTER TABLE survey_invitations ADD CONSTRAINT chk_survey_invitation_status
            CHECK (status IN ('Sent','Submitted','Expired','Cancelled')) NOT VALID;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_survey_invitations_token_hash
            ON survey_invitations (token_hash);

        CREATE INDEX IF NOT EXISTS ix_survey_invitations_ticket
            ON survey_invitations (ticket_id, sent_utc DESC);

        CREATE INDEX IF NOT EXISTS ix_survey_invitations_survey
            ON survey_invitations (survey_id, sent_utc DESC);

        -- Expiry sweeper hot path: only Sent rows can expire.
        CREATE INDEX IF NOT EXISTS ix_survey_invitations_expiry
            ON survey_invitations (expires_utc) WHERE status = 'Sent';

        -- One active invitation per (survey, ticket). Prevents a chatty
        -- trigger from spamming the customer; resends require the existing
        -- row to be Expired or Cancelled first.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_survey_invitations_active_pair
            ON survey_invitations (survey_id, ticket_id) WHERE status = 'Sent';

        CREATE TABLE IF NOT EXISTS survey_responses (
            id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            invitation_id   UUID NOT NULL UNIQUE REFERENCES survey_invitations(id) ON DELETE CASCADE,
            submitted_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
            comment         TEXT NULL
        );

        -- v0.0.38 redesign: dropped overall_rating (the deprecated Overall
        -- rating-mode column). The new "Per-agent sub-questions" model
        -- attributes ratings via survey_answers.agent_user_id instead.
        ALTER TABLE survey_responses DROP COLUMN IF EXISTS overall_rating;

        CREATE TABLE IF NOT EXISTS survey_answers (
            id              BIGSERIAL PRIMARY KEY,
            response_id     UUID NOT NULL REFERENCES survey_responses(id) ON DELETE CASCADE,
            -- No FK to survey_questions: answers render against
            -- survey_invitations.survey_snapshot_json so live edits to the
            -- survey designer never corrupt historical responses (same
            -- snapshot pattern as intake_form_answers post-v0.0.19).
            question_id     BIGINT NOT NULL,
            value_numeric   NUMERIC NULL,
            value_text      TEXT NULL,
            value_json      JSONB NULL
        );

        -- v0.0.38 redesign: per-agent answers carry an agent_user_id so a
        -- single Agent-scoped question yields N rows (one per attributed
        -- agent). Survey-scoped questions keep agent_user_id NULL and
        -- remain unique per response.
        ALTER TABLE survey_answers ADD COLUMN IF NOT EXISTS agent_user_id UUID NULL
            REFERENCES users(id) ON DELETE RESTRICT;

        -- Replace the legacy (response_id, question_id) UNIQUE with two
        -- partial uniques: one per Survey-scope answer, one per (agent)
        -- pair for Agent-scope. Two partial indexes are cleaner than a
        -- COALESCE expression on the unique key.
        ALTER TABLE survey_answers DROP CONSTRAINT IF EXISTS survey_answers_response_id_question_id_key;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_survey_answers_survey_scope
            ON survey_answers (response_id, question_id) WHERE agent_user_id IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_survey_answers_agent_scope
            ON survey_answers (response_id, question_id, agent_user_id) WHERE agent_user_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_survey_answers_agent
            ON survey_answers (agent_user_id) WHERE agent_user_id IS NOT NULL;

        -- v0.0.38 redesign: drop the single-overall-rating-per-agent table;
        -- per-agent scores now live in survey_answers with agent_user_id.
        DROP TABLE IF EXISTS survey_agent_scores;

        -- Compose template → survey link. When an agent sends a reply using
        -- this template, the reply endpoint dispatches the linked survey.
        ALTER TABLE compose_templates
            ADD COLUMN IF NOT EXISTS linked_survey_id UUID NULL
            REFERENCES surveys(id) ON DELETE SET NULL;

        CREATE INDEX IF NOT EXISTS ix_compose_templates_linked_survey
            ON compose_templates (linked_survey_id) WHERE linked_survey_id IS NOT NULL;

        -- v0.0.38 extends ticket_events CHECK with the three survey event
        -- types. Same NOT VALID drop+recreate pattern as v0.0.19 intake.
        ALTER TABLE ticket_events DROP CONSTRAINT IF EXISTS chk_ticket_event_type;
        ALTER TABLE ticket_events ADD CONSTRAINT chk_ticket_event_type
            CHECK (event_type IN ('Created','Comment','Mail','Note','StatusChange',
                                  'AssignmentChange','PriorityChange','QueueChange',
                                  'CategoryChange','SystemNote','MailReceived',
                                  'MailSent','CompanyAssignment','RequesterChange',
                                  'IntakeFormSent','IntakeFormSubmitted','IntakeFormExpired',
                                  'ParentLinked','ParentUnlinked',
                                  'SurveySent','SurveySubmitted','SurveyExpired')) NOT VALID;

        -- v0.0.38 also allows 'survey_submitted' notification rows so the
        -- @-mention framework can fan results out to rated agents.
        ALTER TABLE user_notifications DROP CONSTRAINT IF EXISTS chk_user_notifications_type;
        ALTER TABLE user_notifications ADD CONSTRAINT chk_user_notifications_type
            CHECK (notification_type IN ('mention','survey_submitted')) NOT VALID;

        -- ===================================================================
        -- v0.0.39 Linked-ticket types + manual trigger activator
        -- ===================================================================
        -- First-class ticket types (support / order / iso27001 / …) drive
        -- the "Create linked X ticket" buttons in the ticket side panel.
        -- A new 'manual' trigger activator kind binds a manual-trigger to
        -- exactly one ticket-type and carries a single create_linked_ticket
        -- action describing the prefill recipe (subject/body templates,
        -- defaults, requester/company sources, optional initial note).
        CREATE TABLE IF NOT EXISTS ticket_types (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            code            CITEXT      NOT NULL UNIQUE,
            label           TEXT        NOT NULL,
            description     TEXT        NOT NULL DEFAULT '',
            icon            TEXT        NOT NULL DEFAULT 'ticket',
            color           TEXT        NOT NULL DEFAULT '#7c7cff',
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
            is_system       BOOLEAN     NOT NULL DEFAULT FALSE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_types_active_sort
            ON ticket_types (is_active, sort_order, lower(label));

        -- ===================================================================
        -- Ticket templates — a pre-canned set of ticket field values an agent
        -- picks while creating a ticket so subject / body / queue / priority /
        -- type / category / status / assignee / initial-note are filled in one
        -- click. Distinct from compose_templates (which only carry a reply
        -- body for the :: picker). Every field is optional: a template that
        -- only sets queue + priority leaves the rest untouched on apply.
        -- subject / body_html / initial_note_html may contain {{tokens}} that
        -- resolve against the chosen requester + company at apply time (same
        -- ComposeTokens engine as compose_templates). All reference fields use
        -- ON DELETE SET NULL so deleting a queue/priority/etc. just clears the
        -- template's pre-fill rather than breaking the row. body_html and
        -- initial_note_html are sanitised on write. Defined here (not next to
        -- compose_templates) because it FKs ticket_types, which is created in
        -- this v0.0.39 block — a single bootstrap batch runs top-to-bottom, so
        -- the referenced table must already exist.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS ticket_templates (
            id                    UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name                  TEXT        NOT NULL,
            description           TEXT        NULL,
            is_active             BOOLEAN     NOT NULL DEFAULT TRUE,
            subject               TEXT        NOT NULL DEFAULT '',
            body_html             TEXT        NOT NULL DEFAULT '',
            initial_note_html     TEXT        NOT NULL DEFAULT '',
            initial_note_internal BOOLEAN     NOT NULL DEFAULT TRUE,
            queue_id              UUID        NULL REFERENCES queues(id)       ON DELETE SET NULL,
            priority_id           UUID        NULL REFERENCES priorities(id)   ON DELETE SET NULL,
            status_id             UUID        NULL REFERENCES statuses(id)     ON DELETE SET NULL,
            category_id           UUID        NULL REFERENCES categories(id)   ON DELETE SET NULL,
            ticket_type_id        UUID        NULL REFERENCES ticket_types(id) ON DELETE SET NULL,
            assignee_user_id      UUID        NULL REFERENCES users(id)        ON DELETE SET NULL,
            created_utc           TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc           TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by            UUID        NULL REFERENCES users(id)        ON DELETE SET NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_ticket_templates_active_name
            ON ticket_templates (lower(name))
            WHERE is_active;

        -- Seed the three types referenced by the LinkedTicketTypeDialog.
        -- 'support' is is_system so it always resolves as the historical
        -- default for existing tickets. 'order' and 'iso27001' ship inactive-
        -- adjacent (still active=true so admins can immediately attach a
        -- manual trigger) and can be deactivated or relabelled without
        -- breaking the FK on the tickets backfill.
        INSERT INTO ticket_types (code, label, description, icon, color, sort_order, is_system)
        VALUES
            ('support',  'Support',         'Standard support request',                 'life-buoy',     '#7c7cff', 10, TRUE),
            ('order',    'Order & Retour',  'Procurement or hardware order request',    'shopping-cart', '#22c55e', 20, FALSE),
            ('iso27001', 'ISO 27001',       'Compliance / information-security ticket', 'shield-check',  '#f59e0b', 30, FALSE)
        ON CONFLICT (code) DO NOTHING;

        -- Idempotent label cleanup for installs that ran an earlier
        -- v0.0.39 dev build with the original 'Order' / 'ISO27001'
        -- labels. Only renames rows that still carry the pre-cleanup
        -- value so an admin who customised the label keeps their text.
        UPDATE ticket_types SET label = 'Order & Retour'
            WHERE code = 'order' AND label = 'Order';
        UPDATE ticket_types SET label = 'ISO 27001'
            WHERE code = 'iso27001' AND label = 'ISO27001';

        -- Add ticket_type_id, backfill existing rows to 'support', then
        -- enforce NOT NULL so callers can rely on the column being set.
        -- Two-step idempotent migration: ADD … NULL → UPDATE backfill →
        -- ALTER COLUMN SET NOT NULL (no-op on subsequent runs).
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS ticket_type_id UUID NULL
                REFERENCES ticket_types(id) ON DELETE RESTRICT;

        UPDATE tickets t
        SET ticket_type_id = tt.id
        FROM ticket_types tt
        WHERE t.ticket_type_id IS NULL AND tt.code = 'support';

        ALTER TABLE tickets ALTER COLUMN ticket_type_id SET NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_tickets_type
            ON tickets (ticket_type_id)
            WHERE is_deleted = FALSE;

        -- Extend chk_trigger_activator with the 'manual' kind. A manual
        -- trigger does not fire automatically — it runs only when an
        -- agent invokes it from the side panel. Sole mode for now is
        -- 'linked_ticket_creator'; future manual modes can be added in
        -- the same constraint slot.
        ALTER TABLE triggers DROP CONSTRAINT IF EXISTS chk_trigger_activator;
        ALTER TABLE triggers ADD CONSTRAINT chk_trigger_activator
            CHECK (
                (activator_kind = 'action' AND activator_mode IN ('selective','always'))
                OR
                (activator_kind = 'time'   AND activator_mode IN ('reminder','escalation','escalation_warning'))
                OR
                (activator_kind = 'manual' AND activator_mode IN ('linked_ticket_creator'))
            ) NOT VALID;

        -- Manual triggers carry the ticket-type they produce. The column
        -- stays nullable for action/time-kind rows; the check enforces
        -- the pairing so a manual trigger without a type cannot exist
        -- and a non-manual trigger cannot accidentally carry one.
        ALTER TABLE triggers
            ADD COLUMN IF NOT EXISTS manual_ticket_type_id UUID NULL
                REFERENCES ticket_types(id) ON DELETE RESTRICT;

        ALTER TABLE triggers DROP CONSTRAINT IF EXISTS chk_trigger_manual_ticket_type;
        ALTER TABLE triggers ADD CONSTRAINT chk_trigger_manual_ticket_type
            CHECK (
                (activator_kind = 'manual' AND manual_ticket_type_id IS NOT NULL)
                OR
                (activator_kind <> 'manual' AND manual_ticket_type_id IS NULL)
            ) NOT VALID;

        -- Hot path for the side-panel: "list active manual triggers for
        -- this ticket-type" runs on every "Create linked ticket" open.
        CREATE INDEX IF NOT EXISTS ix_triggers_manual_active
            ON triggers (manual_ticket_type_id, is_active)
            WHERE activator_kind = 'manual';

        -- Track the linked ticket produced by a manual-trigger run so the
        -- audit log can hop from "this run" to "this ticket". Nullable
        -- because non-manual runs (action/time) do not produce tickets.
        ALTER TABLE trigger_runs
            ADD COLUMN IF NOT EXISTS result_ticket_id UUID NULL
                REFERENCES tickets(id) ON DELETE SET NULL;

        -- ===================================================================
        -- v0.0.40 ISO 27001 workflow — MGM review → DPO ISMS-registration
        -- ===================================================================
        -- Two extra per-user role flags (timesheet-pattern: live next to
        -- role_name so an Agent or Admin can carry the extra hat). Plus
        -- four seed statuses that scope the ISO flow. The statuses live
        -- in the global taxonomy but their labels are prefixed with
        -- "ISO 27001 –" so they are visually scoped in other dropdowns.
        -- A single Iso27001.QueueId setting binds the workflow to one
        -- queue; when unset the classification buttons never appear so
        -- the feature is fully opt-in.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS is_iso_mgm BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS is_iso_dpo BOOLEAN NOT NULL DEFAULT FALSE;

        -- v0.0.40 polish — Knowledge Base is now opt-in per user. FALSE on
        -- upgrade means existing installs see the KB disappear from the
        -- sidebar until an admin opts users in; that mirrors what the
        -- admin asked for ("manual assignment"), and matches the
        -- timesheet/iso pattern above.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS kb_enabled BOOLEAN NOT NULL DEFAULT FALSE;

        -- Seed the three ISO statuses idempotently. The slug is the
        -- stable identifier (admin can re-label freely). is_system is
        -- TRUE so an over-eager admin cannot delete the rows the
        -- classification endpoints depend on.
        INSERT INTO statuses (name, slug, state_category, color, icon, sort_order, is_system, is_active, is_default)
        VALUES
            ('ISO 27001 – MGM review',                  'iso-mgm-review',         'Open',     '#7c7cff', 'shield-alert', 91, TRUE, TRUE, FALSE),
            ('ISO 27001 – Awaiting ISMS registration',  'iso-awaiting-isms',      'Pending',  '#f59e0b', 'shield-check', 92, TRUE, TRUE, FALSE),
            ('ISO 27001 – No incident',                 'iso-no-incident',        'Closed',   '#71717a', 'shield',       93, TRUE, TRUE, FALSE)
        ON CONFLICT (slug) DO NOTHING;

        -- v0.0.40 polish — per-queue status scope. Two nullable extras
        -- on the queues row that together drive the "ISO statuses only
        -- show inside the ISO queue and the default statuses are
        -- hidden there" UX. Backward-compatible: empty array on
        -- allowed_status_ids = current behaviour (all statuses
        -- available); null default_status_id = no auto-flip on queue
        -- change. Existing queues stay on those defaults.
        ALTER TABLE queues
            ADD COLUMN IF NOT EXISTS allowed_status_ids UUID[] NOT NULL DEFAULT '{}'::uuid[],
            ADD COLUMN IF NOT EXISTS default_status_id UUID NULL
                REFERENCES statuses(id) ON DELETE SET NULL;

        -- Per-user Sidebar feature flag for the global search bar.
        -- DEFAULT TRUE so brownfield Agent/Admin users do not lose
        -- the bar on upgrade. Customer rows carry TRUE too but the
        -- API rejects mutations for Customers and the frontend
        -- never reads this for that role.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS search_enabled BOOLEAN NOT NULL DEFAULT TRUE;

        -- An earlier iteration introduced a master `dashboard_enabled`
        -- flag. It was replaced by per-tile feature toggles (see the
        -- user_dashboard_tiles table below) and is dropped here to
        -- keep the column shape clean. The DROP is idempotent so a
        -- fresh install that never had the column is unaffected.
        ALTER TABLE users
            DROP COLUMN IF EXISTS dashboard_enabled;

        -- Per-user Dashboard tile preferences. A row means "this tile
        -- is enabled for this user"; absence means OFF (default for
        -- every user on first upgrade). Tile-id validation happens at
        -- the API boundary against a static allow-list; storing the
        -- string here keeps schema-free when new tiles arrive.
        CREATE TABLE IF NOT EXISTS user_dashboard_tiles (
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            tile_id VARCHAR(64) NOT NULL,
            PRIMARY KEY (user_id, tile_id)
        );

        -- v0.0.42 — per-user "recent tickets" sidebar list, server-side
        -- so the same user sees the same list across browsers / devices.
        -- Position is 0-based; client adds new entries at the tail and
        -- can reorder via drag. Hard cap of 50 entries per user is
        -- enforced by RecentTicketsService — when a 51st is added the
        -- oldest (lowest position) is dropped. FK to tickets cascades on
        -- delete so a deleted ticket disappears from every user's list.
        CREATE TABLE IF NOT EXISTS user_recent_tickets (
            user_id     UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            ticket_id   UUID        NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            position    INT         NOT NULL DEFAULT 0,
            added_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, ticket_id)
        );

        CREATE INDEX IF NOT EXISTS ix_user_recent_tickets_user_pos
            ON user_recent_tickets (user_id, position);

        -- v0.0.42 — per-user layout customisation. position drives the
        -- sort order in the grid; size is one of the four canonical
        -- widths (small=1/4, medium=2/4, wide=3/4, full=4/4 on the lg
        -- breakpoint). Backfill: existing rows get position by tile_id
        -- alphabetic and the registry's default size. Defaults keep a
        -- fresh row useful even without a UI write.
        ALTER TABLE user_dashboard_tiles
            ADD COLUMN IF NOT EXISTS position INT  NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS size     TEXT NOT NULL DEFAULT 'medium';

        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'chk_user_dashboard_tile_size'
            ) THEN
                ALTER TABLE user_dashboard_tiles
                    ADD CONSTRAINT chk_user_dashboard_tile_size
                    CHECK (size IN ('small','medium','wide','full'));
            END IF;
        END $$;

        -- One-shot backfill of position for existing rows (where every
        -- row currently sits on position 0). Skipped on a fresh install
        -- because the unnumbered rows don't exist yet.
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM settings WHERE key = 'DashboardTiles.PositionBackfilled') THEN
                WITH ranked AS (
                    SELECT user_id, tile_id,
                           row_number() OVER (PARTITION BY user_id ORDER BY tile_id) - 1 AS rn
                    FROM user_dashboard_tiles
                )
                UPDATE user_dashboard_tiles t
                    SET position = r.rn
                    FROM ranked r
                    WHERE t.user_id = r.user_id AND t.tile_id = r.tile_id
                      AND t.position = 0 AND r.rn > 0;
                INSERT INTO settings (key, value, value_type, category, description, default_value, updated_utc)
                    VALUES ('DashboardTiles.PositionBackfilled', 'true', 'bool', 'Dashboard',
                            'Internal marker: per-user dashboard tile positions were backfilled on first upgrade.',
                            'true', now())
                    ON CONFLICT (key) DO NOTHING;
            END IF;
        END $$;

        -- ===================================================================
        -- v0.0.41 — Zammad migration link (one-way bridge from a Zammad
        -- install into this servicedesk). Three explicit-mapping tables
        -- let an admin lock down how Zammad groups/states/priorities
        -- translate into local queues/statuses/priorities BEFORE any
        -- ticket is imported. Two run-tables capture every dry-run and
        -- (later, fase 4) real-import as an immutable record so the
        -- admin can audit + roll forward without re-querying Zammad.
        --
        -- Mapping tables share the same shape: zammad_id (the upstream
        -- numeric id, unique) + zammad_name (snapshot for UX so we don't
        -- need a live Zammad call to render the mapping table) + the
        -- target UUID. zammad_id is the natural key — an admin re-mapping
        -- a group performs an UPSERT on zammad_id, not on uuid.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS zammad_group_mappings (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            zammad_group_id     BIGINT      NOT NULL UNIQUE,
            zammad_group_name   TEXT        NOT NULL,
            queue_id            UUID        NOT NULL REFERENCES queues(id) ON DELETE RESTRICT,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS zammad_state_mappings (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            zammad_state_id     BIGINT      NOT NULL UNIQUE,
            zammad_state_name   TEXT        NOT NULL,
            status_id           UUID        NOT NULL REFERENCES statuses(id) ON DELETE RESTRICT,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS zammad_priority_mappings (
            id                      UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            zammad_priority_id      BIGINT      NOT NULL UNIQUE,
            zammad_priority_name    TEXT        NOT NULL,
            priority_id             UUID        NOT NULL REFERENCES priorities(id) ON DELETE RESTRICT,
            created_utc             TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc             TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Run metadata. One row per dry-run (fase 3) or real import (fase 4).
        -- kind drives later branching but the columns are deliberately the
        -- same so the UI's runs-list can render both without a UNION. status
        -- is an explicit short whitelist (matches IntegrationAudit-style
        -- colour coding in the UI). source_filter captures the picker state
        -- at the moment the admin clicked "Dry run" — so an old run remains
        -- reproducible even after the admin changes the filters in the
        -- picker. totals is a denormalised running counter the worker
        -- updates after every batch; the UI polls it for progress.
        CREATE TABLE IF NOT EXISTS zammad_import_runs (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            kind                TEXT        NOT NULL,
            status              TEXT        NOT NULL DEFAULT 'pending',
            started_by_user_id  UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            started_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            finished_utc        TIMESTAMPTZ NULL,
            source_filter       JSONB       NOT NULL DEFAULT '{}'::jsonb,
            totals              JSONB       NOT NULL DEFAULT '{}'::jsonb,
            error_message       TEXT        NULL,
            CONSTRAINT chk_zammad_run_kind   CHECK (kind   IN ('dry_run','import')),
            CONSTRAINT chk_zammad_run_status CHECK (status IN ('pending','running','completed','failed','cancelled'))
        );

        CREATE INDEX IF NOT EXISTS ix_zammad_import_runs_started
            ON zammad_import_runs (started_utc DESC, id DESC);

        -- Per-ticket result. Created in batches by the worker. result is
        -- a short whitelist (NOT a free-form reason string) so the UI can
        -- group + filter without parsing text; the human-readable details
        -- are in unresolved_reasons (TEXT[], can be empty) + mapping (JSONB
        -- snapshot of every resolved target id + name, useful for the
        -- run-detail page's per-row preview without a live Zammad query).
        -- would_create_ticket_id is reserved for fase 4 — fase 3 leaves it
        -- NULL because dry-run never writes a tickets row.
        CREATE TABLE IF NOT EXISTS zammad_import_records (
            id                      UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            run_id                  UUID        NOT NULL REFERENCES zammad_import_runs(id) ON DELETE CASCADE,
            zammad_ticket_id        BIGINT      NOT NULL,
            zammad_ticket_number    TEXT        NULL,
            zammad_ticket_title     TEXT        NULL,
            result                  TEXT        NOT NULL,
            unresolved_reasons      TEXT[]      NOT NULL DEFAULT '{}',
            mapping                 JSONB       NOT NULL DEFAULT '{}'::jsonb,
            would_create_ticket_id  UUID        NULL,
            created_utc             TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_zammad_record_result
                CHECK (result IN (
                    'mapped',
                    'skipped_no_contact',
                    'skipped_no_group_mapping',
                    'skipped_no_state_mapping',
                    'skipped_no_priority_mapping',
                    'failed'
                ))
        );

        CREATE INDEX IF NOT EXISTS ix_zammad_import_records_run
            ON zammad_import_records (run_id, id);
        CREATE INDEX IF NOT EXISTS ix_zammad_import_records_run_result
            ON zammad_import_records (run_id, result);
        CREATE INDEX IF NOT EXISTS ix_zammad_import_records_ticket
            ON zammad_import_records (zammad_ticket_id);

        -- v0.0.41 fase 4 — real-import additions.
        --
        -- The result-CHECK on zammad_import_records gains 'imported' and
        -- 'already_imported' for import-kind runs. Postgres can't ALTER
        -- a CHECK in place, so we drop and re-add idempotently.
        ALTER TABLE zammad_import_records DROP CONSTRAINT IF EXISTS chk_zammad_record_result;
        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint WHERE conname = 'chk_zammad_record_result'
            ) THEN
                ALTER TABLE zammad_import_records
                    ADD CONSTRAINT chk_zammad_record_result
                    CHECK (result IN (
                        'mapped',
                        'skipped_no_contact',
                        'skipped_no_group_mapping',
                        'skipped_no_state_mapping',
                        'skipped_no_priority_mapping',
                        'failed',
                        'imported',
                        'already_imported'
                    ));
            END IF;
        END $$;

        -- Sparse Zammad-link on tickets. zammad_ticket_id is nullable
        -- because the vast majority of rows are not migrated; the
        -- partial UNIQUE index guarantees the same upstream id can never
        -- be imported twice without imposing a NULL collation cost on
        -- the millions of non-Zammad rows. zammad_ticket_number is the
        -- human-friendly number (Zammad's column is a STRING — leading
        -- zeroes etc).
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS zammad_ticket_id      BIGINT NULL,
            ADD COLUMN IF NOT EXISTS zammad_ticket_number  TEXT   NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ix_tickets_zammad_id
            ON tickets (zammad_ticket_id)
            WHERE zammad_ticket_id IS NOT NULL;

        -- (Note: the tickets.source CHECK with 'Zammad' in its allow-
        -- list lives further up next to the v0.0.23 split-source block,
        -- to keep the constraint defined in one place. Add new
        -- INSERT-INTO-tickets sources there.)

        -- ===================================================================
        -- v0.0.42 — Agent activity feed
        --
        -- Append-only feed that captures every agent / admin action across
        -- the app (ticket mutations, KB edits, Telavox calls, auth, profile,
        -- settings). Per-user opt-in for *viewing* only — every agent +
        -- admin is always logged. Retention is settings-driven (default
        -- 365d) and pruned by ActivityRetentionWorker.
        --
        -- entity_type / entity_id / entity_extra is a generic pointer so
        -- the feed can link back to the source object (ticket #N, KB
        -- article, settings key, …). metadata jsonb carries the extras
        -- (call direction, duration, internal-vs-public note, …).
        --
        -- Ticket coverage is provided by a trigger on ticket_events so we
        -- never miss a code path. Non-ticket subsystems push directly via
        -- IActivityRecorder.RecordAsync. Both paths emit a `NOTIFY
        -- agent_activity_event, <id>` so the SignalR worker can broadcast
        -- the row to clients with the viewing flag enabled.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS agent_activity_events (
            id              BIGSERIAL   PRIMARY KEY,
            occurred_utc    TIMESTAMPTZ NOT NULL DEFAULT now(),
            agent_id        UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            agent_role      TEXT        NOT NULL,
            event_type      TEXT        NOT NULL,
            entity_type     TEXT        NULL,
            entity_id       TEXT        NULL,
            entity_extra    TEXT        NULL,
            summary         TEXT        NOT NULL,
            metadata        JSONB       NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE INDEX IF NOT EXISTS ix_agent_activity_events_agent_time
            ON agent_activity_events (agent_id, occurred_utc DESC, id DESC);
        CREATE INDEX IF NOT EXISTS ix_agent_activity_events_time
            ON agent_activity_events (occurred_utc DESC, id DESC);
        CREATE INDEX IF NOT EXISTS ix_agent_activity_events_event_type
            ON agent_activity_events (event_type);

        -- Per-user opt-in flag for *viewing* the activity feed. On first
        -- upgrade Admins are backfilled to TRUE so the feature is
        -- immediately usable; everyone else starts FALSE. Toggling the
        -- flag flows through the existing feature-flags update path
        -- and is captured in audit_log.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS activity_feed_enabled BOOLEAN NOT NULL DEFAULT FALSE;

        -- One-shot backfill for existing admin rows. Idempotent: re-runs
        -- on every boot but only flips rows currently false, so an admin
        -- who explicitly switched themselves off does not get re-enabled.
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM settings WHERE key = 'ActivityFeed.AdminsBackfilled') THEN
                UPDATE users SET activity_feed_enabled = TRUE
                    WHERE role_name = 'Admin' AND activity_feed_enabled = FALSE;
                INSERT INTO settings (key, value, value_type, category, description, default_value, updated_utc)
                    VALUES ('ActivityFeed.AdminsBackfilled', 'true', 'bool', 'ActivityFeed',
                            'Internal marker: admins were backfilled to activity_feed_enabled=true on first upgrade.',
                            'true', now())
                    ON CONFLICT (key) DO NOTHING;
            END IF;
        END $$;

        -- Trigger that mirrors every agent-authored ticket_events row into
        -- agent_activity_events. The trigger runs AFTER INSERT so it only
        -- fires for committed rows. Customer-authored rows (author_user_id
        -- IS NULL but author_contact_id IS NOT NULL) are skipped — the
        -- feed tracks agent + admin activity only.
        --
        -- The summary string is a short human-readable verb; the UI
        -- enriches it with the ticket subject + number via the LEFT JOIN
        -- in the feed query, so re-titling a ticket does not leave stale
        -- text in this table.
        CREATE OR REPLACE FUNCTION agent_activity_from_ticket_event()
            RETURNS TRIGGER
            LANGUAGE plpgsql
            AS $fn$
        DECLARE
            v_role TEXT;
            v_summary TEXT;
            v_event_type TEXT;
            v_new_id BIGINT;
        BEGIN
            IF NEW.author_user_id IS NULL THEN
                RETURN NEW;
            END IF;

            SELECT role_name INTO v_role FROM users WHERE id = NEW.author_user_id;
            IF v_role IS NULL OR v_role = 'Customer' THEN
                RETURN NEW;
            END IF;

            -- Translate the ticket-event kind into a short feed verb.
            -- Note + Mail carry the internal/public distinction in their
            -- metadata; we surface that via two distinct event_type values
            -- so the page filter can split them.
            v_event_type := CASE NEW.event_type
                WHEN 'Created'            THEN 'ticket_created'
                WHEN 'StatusChange'       THEN 'ticket_status_changed'
                WHEN 'AssignmentChange'   THEN 'ticket_assigned'
                WHEN 'PriorityChange'     THEN 'ticket_priority_changed'
                WHEN 'QueueChange'        THEN 'ticket_queue_changed'
                WHEN 'CategoryChange'     THEN 'ticket_category_changed'
                WHEN 'CompanyAssignment'  THEN 'ticket_company_assigned'
                WHEN 'RequesterChange'    THEN 'ticket_requester_changed'
                WHEN 'Note'               THEN CASE WHEN NEW.is_internal THEN 'ticket_note_internal' ELSE 'ticket_note_public' END
                WHEN 'Comment'            THEN 'ticket_comment'
                WHEN 'Mail'               THEN 'ticket_mail_sent'
                WHEN 'MailSent'           THEN 'ticket_mail_sent'
                WHEN 'MailReceived'       THEN 'ticket_mail_received'
                WHEN 'SystemNote'         THEN 'ticket_system_note'
                ELSE 'ticket_' || lower(NEW.event_type)
            END;

            v_summary := CASE v_event_type
                WHEN 'ticket_created'             THEN 'created ticket'
                WHEN 'ticket_status_changed'      THEN 'changed ticket status'
                WHEN 'ticket_assigned'            THEN 'changed ticket assignment'
                WHEN 'ticket_priority_changed'    THEN 'changed ticket priority'
                WHEN 'ticket_queue_changed'       THEN 'changed ticket queue'
                WHEN 'ticket_category_changed'    THEN 'changed ticket category'
                WHEN 'ticket_company_assigned'    THEN 'assigned ticket to company'
                WHEN 'ticket_requester_changed'   THEN 'changed ticket requester'
                WHEN 'ticket_note_internal'       THEN 'added internal note'
                WHEN 'ticket_note_public'         THEN 'added public note'
                WHEN 'ticket_comment'             THEN 'added comment'
                WHEN 'ticket_mail_sent'           THEN 'sent mail reply'
                WHEN 'ticket_mail_received'       THEN 'received mail'
                WHEN 'ticket_system_note'         THEN 'added system note'
                ELSE 'ticket activity'
            END;

            INSERT INTO agent_activity_events
                (occurred_utc, agent_id, agent_role, event_type,
                 entity_type, entity_id, summary, metadata)
            VALUES
                (NEW.created_utc, NEW.author_user_id, v_role, v_event_type,
                 'ticket', NEW.ticket_id::text, v_summary,
                 jsonb_build_object(
                     'ticket_event_id', NEW.id,
                     'is_internal', NEW.is_internal))
            RETURNING id INTO v_new_id;

            PERFORM pg_notify('agent_activity_event', v_new_id::text);
            RETURN NEW;
        END;
        $fn$;

        DROP TRIGGER IF EXISTS trg_agent_activity_from_ticket_event ON ticket_events;
        CREATE TRIGGER trg_agent_activity_from_ticket_event
            AFTER INSERT ON ticket_events
            FOR EACH ROW EXECUTE FUNCTION agent_activity_from_ticket_event();

        -- Companion trigger that fires NOTIFY for rows inserted directly
        -- by ActivityRecorder (non-ticket subsystems). One channel for
        -- both paths so the listener has a single subscription point.
        -- We use a WHEN clause to avoid firing the NOTIFY twice for
        -- ticket-sourced rows (the trigger above already emits it).
        CREATE OR REPLACE FUNCTION agent_activity_notify_direct()
            RETURNS TRIGGER
            LANGUAGE plpgsql
            AS $fn$
        BEGIN
            PERFORM pg_notify('agent_activity_event', NEW.id::text);
            RETURN NEW;
        END;
        $fn$;

        DROP TRIGGER IF EXISTS trg_agent_activity_notify_direct ON agent_activity_events;
        CREATE TRIGGER trg_agent_activity_notify_direct
            AFTER INSERT ON agent_activity_events
            FOR EACH ROW
            WHEN (NEW.entity_type IS DISTINCT FROM 'ticket')
            EXECUTE FUNCTION agent_activity_notify_direct();

        -- ===================================================================
        -- v0.0.42 — Status-change gates (interactive triggers)
        --
        -- A gate is a trigger that intercepts an agent-initiated status
        -- change in the UI and forces a confirmation dialog before the
        -- mutation is applied. Modeled as a new activator pair
        -- (gate:status_change) so all existing trigger CRUD, conditions,
        -- audit, and run-history machinery is reused. The single action
        -- 'prompt_confirm' carries the prompt payload + the source/target
        -- status pair + an internal/public note template that is appended
        -- to the ticket timeline when the agent confirms.
        --
        -- Automation paths bypass entirely: gates are enforced only inside
        -- the agent-facing PATCH /api/tickets/{id} endpoint. TriggerService
        -- and mail-ingest call the repository directly and never see a
        -- gate prompt — required for surveys, mail-replies, and chained
        -- triggers to keep functioning.
        -- ===================================================================
        ALTER TABLE triggers DROP CONSTRAINT IF EXISTS chk_trigger_activator;
        ALTER TABLE triggers ADD CONSTRAINT chk_trigger_activator
            CHECK (
                (activator_kind = 'action' AND activator_mode IN ('selective','always'))
                OR
                (activator_kind = 'time'   AND activator_mode IN ('reminder','escalation','escalation_warning'))
                OR
                (activator_kind = 'manual' AND activator_mode IN ('linked_ticket_creator'))
                OR
                (activator_kind = 'gate'   AND activator_mode IN ('status_change'))
            ) NOT VALID;

        -- Hot path for the gate-matching endpoint: "list active
        -- gate:status_change triggers" runs once per status-change attempt
        -- so the active rows are filtered cheaply.
        CREATE INDEX IF NOT EXISTS ix_triggers_gate_active
            ON triggers (is_active)
            WHERE activator_kind = 'gate';

        -- ===================================================================
        -- v0.0.42 — Compose templates: auto-insert on internal note + status scope.
        --
        -- Two additive columns:
        --   status_ids           — multi-status scope mirroring queue_ids.
        --                          Empty array = any status. Both filters AND
        --                          together for picker + auto-insert matching.
        --   auto_insert_on_note  — when TRUE the template is selected as the
        --                          initial body of the "Write an internal note"
        --                          composer whenever the agent opens an empty
        --                          composer on a ticket that matches both the
        --                          queue and status scope. Picker behaviour is
        --                          unchanged — the same template still surfaces
        --                          under the :: dropdown.
        --
        -- Tie-breaker when more than one template matches: most-recently-updated
        -- row wins (deterministic, predictable for admins editing their templates).
        -- ===================================================================
        ALTER TABLE compose_templates
            ADD COLUMN IF NOT EXISTS status_ids UUID[] NOT NULL DEFAULT '{}';

        ALTER TABLE compose_templates
            ADD COLUMN IF NOT EXISTS auto_insert_on_note BOOLEAN NOT NULL DEFAULT FALSE;

        CREATE INDEX IF NOT EXISTS ix_compose_templates_status_ids
            ON compose_templates USING GIN (status_ids);

        -- Partial index used by the default-for-note matcher — keeps the
        -- index tight (only auto-insert rows) so the lookup stays cheap as
        -- the templates table grows.
        CREATE INDEX IF NOT EXISTS ix_compose_templates_auto_insert
            ON compose_templates (is_active, updated_utc DESC)
            WHERE auto_insert_on_note = TRUE;

        -- ===================================================================
        -- v0.0.43 — Zammad Knowledge Base import (one-way bridge)
        --
        -- Reuses the existing Zammad token + base URL + integration_audit
        -- pipeline from the ticket-import (v0.0.41). KB-side runs live in
        -- their own tables so the existing zammad_import_runs / records
        -- table stays focused on tickets — the two flows have different
        -- shapes (categories+articles vs tickets+articles) and different
        -- result-vocabularies.
        --
        -- Flow: admin starts a run → worker fetches Zammad categories +
        -- builds proposed_tree JSONB → admin approves/edits in UI → worker
        -- materialises kb_sections + writes section mappings → admin
        -- picks articles via search/filter → worker imports articles with
        -- author email-match, slug regen, HTML sanitize, image rewrite.
        --
        -- Idempotency: kb_article_import_mappings rows guard against
        -- re-imports. Re-runs skip mapped articles with 'already_imported'.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS kb_import_runs (
            id                       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            status                   TEXT        NOT NULL DEFAULT 'pending',
            started_by_user_id       UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            started_utc              TIMESTAMPTZ NOT NULL DEFAULT now(),
            finished_utc             TIMESTAMPTZ NULL,
            source_kb_id             BIGINT      NULL,
            source_kb_name           TEXT        NULL,
            -- Section proposal — produced in the Categories phase, edited
            -- by the admin in the UI, frozen on Apply. Shape:
            -- { "nodes": [ { "zammadCategoryId", "zammadParentId",
            --                "proposedTitle", "proposedSlug", "depth",
            --                "action": "create" | "merge" | "skip",
            --                "targetSectionId": "<uuid>"|null } ] }
            proposed_tree            JSONB       NOT NULL DEFAULT '{}'::jsonb,
            -- Article picker snapshot — captured when admin clicks "Start
            -- import" so the run is reproducible even after the picker is
            -- refilled. Shape: { "answerIds": [...], "filters": {...} }
            article_selection        JSONB       NOT NULL DEFAULT '{}'::jsonb,
            totals                   JSONB       NOT NULL DEFAULT '{}'::jsonb,
            error_message            TEXT        NULL,
            CONSTRAINT chk_kb_import_run_status
                CHECK (status IN ('pending','proposing','awaiting_approval','approved','importing','completed','failed','cancelled'))
        );

        CREATE INDEX IF NOT EXISTS ix_kb_import_runs_started
            ON kb_import_runs (started_utc DESC, id DESC);

        -- Per-article result — one row per Zammad answer the worker
        -- attempted to import. Vocabulary mirrors the ticket-import
        -- variant for UI consistency. mapping JSONB carries the resolved
        -- target_section_id, status, author resolution outcome and any
        -- per-article warnings (e.g. "html_sanitized: <count> nodes
        -- stripped", "attachment_skipped: too_large").
        CREATE TABLE IF NOT EXISTS kb_import_records (
            id                       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            run_id                   UUID        NOT NULL REFERENCES kb_import_runs(id) ON DELETE CASCADE,
            zammad_answer_id         BIGINT      NOT NULL,
            zammad_category_id       BIGINT      NULL,
            zammad_title             TEXT        NULL,
            result                   TEXT        NOT NULL,
            unresolved_reasons       TEXT[]      NOT NULL DEFAULT '{}',
            mapping                  JSONB       NOT NULL DEFAULT '{}'::jsonb,
            target_article_id        UUID        NULL,
            created_utc              TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_kb_import_record_result
                CHECK (result IN (
                    'imported',
                    'already_imported',
                    'skipped_no_section_mapping',
                    'skipped_no_translation',
                    'skipped_section_skipped',
                    'failed'
                ))
        );

        CREATE INDEX IF NOT EXISTS ix_kb_import_records_run
            ON kb_import_records (run_id, id);
        CREATE INDEX IF NOT EXISTS ix_kb_import_records_run_result
            ON kb_import_records (run_id, result);
        CREATE INDEX IF NOT EXISTS ix_kb_import_records_answer
            ON kb_import_records (zammad_answer_id);

        -- Section mapping: Zammad category id → local KbSection. action
        -- captures the admin's decision in the proposal-review step:
        --   'create' — section was created by the import, target_section_id
        --              points at the new row.
        --   'merge'  — articles in this Zammad category should land in an
        --              existing KbSection (admin-picked target).
        --   'skip'   — category is not imported. Articles under it are
        --              recorded as skipped_section_skipped.
        -- Unique on zammad_category_id so re-runs converge on the same
        -- target row (idempotent UPSERT).
        CREATE TABLE IF NOT EXISTS kb_section_import_mappings (
            id                       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            zammad_category_id       BIGINT      NOT NULL UNIQUE,
            zammad_parent_id         BIGINT      NULL,
            zammad_title             TEXT        NOT NULL,
            target_section_id        UUID        NULL REFERENCES kb_sections(id) ON DELETE SET NULL,
            action                   TEXT        NOT NULL,
            run_id                   UUID        NULL REFERENCES kb_import_runs(id) ON DELETE SET NULL,
            created_utc              TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc              TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_kb_section_mapping_action
                CHECK (action IN ('create','merge','skip'))
        );

        -- Per-article idempotency mapping. content_hash is a SHA-256 of
        -- the upstream title + body so a future "rebuild detected
        -- changes" feature can be layered without schema change. v0.0.43
        -- only writes it; reads are limited to "exists?" lookups.
        CREATE TABLE IF NOT EXISTS kb_article_import_mappings (
            id                       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            zammad_answer_id         BIGINT      NOT NULL UNIQUE,
            target_article_id        UUID        NOT NULL REFERENCES kb_articles(id) ON DELETE CASCADE,
            content_hash             BYTEA       NULL,
            run_id                   UUID        NULL REFERENCES kb_import_runs(id) ON DELETE SET NULL,
            imported_utc             TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_kb_article_import_mappings_target
            ON kb_article_import_mappings (target_article_id);

        -- External-author metadata for articles that were imported but
        -- whose Zammad author could not be email-matched to a local user.
        -- Shape: { "source":"zammad", "email":"...", "name":"...",
        --          "zammadUserId": <id>, "importedUtc":"..." }
        -- Null on locally-authored articles. Surfaced read-only in the
        -- article-detail UI ("Originally authored by X" tooltip) — a
        -- future admin-flow can offer a "remap to user" action.
        ALTER TABLE kb_articles
            ADD COLUMN IF NOT EXISTS external_author_metadata JSONB NULL;

        -- ===================================================================
        -- v0.0.52 — Tactical RMM integration & Assets
        --
        -- One TRMM install per Servicedesk install. A background poller
        -- mirrors clients/sites/agents into the three tables below so the
        -- Assets page can filter and sort offline and the global search
        -- can register a search-source against the local rows.
        --
        -- Client name format in TRMM is <c>[CODE] Customer Name</c>. The
        -- bracketed code is matched (case-insensitive) against
        -- <c>companies.code</c> for auto-linking — admins can override
        -- per client via the Integrations page; manual overrides survive
        -- re-syncs (auto_matched = FALSE pins the link).
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS trmm_clients (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            trmm_client_id      BIGINT      NOT NULL UNIQUE,
            name                TEXT        NOT NULL,
            code                TEXT        NULL,
            company_id          UUID        NULL REFERENCES companies(id) ON DELETE SET NULL,
            auto_matched        BOOLEAN     NOT NULL DEFAULT TRUE,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_trmm_clients_company
            ON trmm_clients (company_id);
        CREATE INDEX IF NOT EXISTS ix_trmm_clients_code
            ON trmm_clients (code);

        CREATE TABLE IF NOT EXISTS trmm_sites (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            trmm_site_id        BIGINT      NOT NULL UNIQUE,
            trmm_client_id      BIGINT      NOT NULL,
            name                TEXT        NOT NULL,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_trmm_sites_client
            ON trmm_sites (trmm_client_id);

        CREATE TABLE IF NOT EXISTS trmm_agents (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            -- TRMM agent_id is a string UUID, not a numeric id. Stored as
            -- TEXT so the schema doesn't have to know the upstream shape.
            trmm_agent_id       TEXT        NOT NULL UNIQUE,
            hostname            TEXT        NOT NULL,
            agent_type          TEXT        NOT NULL,
            os_name             TEXT        NULL,
            os_family           TEXT        NULL,
            os_build            TEXT        NULL,
            last_seen_utc       TIMESTAMPTZ NULL,
            online              BOOLEAN     NOT NULL DEFAULT FALSE,
            public_ip           TEXT        NULL,
            trmm_client_id      BIGINT      NOT NULL,
            trmm_site_id        BIGINT      NOT NULL,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_sync_utc       TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT chk_trmm_agent_type
                CHECK (agent_type IN ('server','workstation'))
        );

        -- Idempotent column-add for installs that ran a pre-os_family
        -- bootstrap (i.e. early v0.0.52 testers). New installs get the
        -- column from the CREATE TABLE above and this is a no-op.
        ALTER TABLE trmm_agents
            ADD COLUMN IF NOT EXISTS os_family TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_trmm_agents_client
            ON trmm_agents (trmm_client_id);
        CREATE INDEX IF NOT EXISTS ix_trmm_agents_site
            ON trmm_agents (trmm_site_id);
        CREATE INDEX IF NOT EXISTS ix_trmm_agents_type
            ON trmm_agents (agent_type);
        CREATE INDEX IF NOT EXISTS ix_trmm_agents_build
            ON trmm_agents (os_build);
        CREATE INDEX IF NOT EXISTS ix_trmm_agents_family
            ON trmm_agents (os_family);
        CREATE INDEX IF NOT EXISTS ix_trmm_agents_last_seen
            ON trmm_agents (last_seen_utc DESC NULLS LAST);
        CREATE INDEX IF NOT EXISTS ix_trmm_agents_hostname_trgm
            ON trmm_agents USING GIN (hostname gin_trgm_ops);

        -- Sync metadata — single row keyed on a constant so the poller
        -- can read/write the last-sync timestamp + status without a
        -- second table for one value pair.
        CREATE TABLE IF NOT EXISTS trmm_sync_state (
            id                  TEXT        PRIMARY KEY DEFAULT 'singleton',
            last_sync_utc       TIMESTAMPTZ NULL,
            last_status         TEXT        NULL,
            last_error          TEXT        NULL,
            last_counts         JSONB       NOT NULL DEFAULT '{}'::jsonb,
            CONSTRAINT chk_trmm_sync_singleton CHECK (id = 'singleton')
        );

        INSERT INTO trmm_sync_state (id)
            VALUES ('singleton')
            ON CONFLICT (id) DO NOTHING;

        -- Per-user opt-in flag for the Assets page (mirrors kb_enabled,
        -- timesheet_enabled, activity_feed_enabled). Customer-rol blijft
        -- altijd geblokkeerd op route- en search-source-niveau; deze flag
        -- bestaat alleen voor Agent en Admin.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS assets_enabled BOOLEAN NOT NULL DEFAULT FALSE;

        -- One-shot backfill for existing agent + admin rows. Idempotent:
        -- re-runs on every boot but only flips rows currently false, so a
        -- user who explicitly switched themselves off does not get
        -- re-enabled. Customer rows are never touched.
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM settings WHERE key = 'Trmm.AgentsAdminsBackfilled') THEN
                UPDATE users SET assets_enabled = TRUE
                    WHERE role_name IN ('Agent','Admin') AND assets_enabled = FALSE;
                INSERT INTO settings (key, value, value_type, category, description, default_value, updated_utc)
                    VALUES ('Trmm.AgentsAdminsBackfilled', 'true', 'bool', 'Tactical RMM',
                            'Internal marker: agents + admins were backfilled to assets_enabled=true on first upgrade.',
                            'true', now())
                    ON CONFLICT (key) DO NOTHING;
            END IF;
        END $$;

        -- Per-user opt-in flag for the Adsolut timesheet tab (mirrors
        -- kb_enabled, timesheet_enabled, assets_enabled). The tab is only
        -- shown when this flag is TRUE *and* the Adsolut integration is
        -- connected; the flag alone surfaces nothing without the
        -- integration. Default FALSE — no backfill, this is strictly
        -- opt-in per user. Customer rows stay blocked at the feature-flags
        -- update path (Agent/Admin only).
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS adsolut_timesheet_enabled BOOLEAN NOT NULL DEFAULT FALSE;

        -- ===================================================================
        -- v0.0.56 Timesheet back-office — Resolved + CWI tabs.
        --
        -- Per-user opt-in flag for the two back-office timesheet tabs
        -- (Resolved / CWI). Mirrors the other per-user feature flags
        -- (kb_enabled, assets_enabled, adsolut_timesheet_enabled): default
        -- FALSE, no backfill, strictly opt-in, Agent/Admin only (the
        -- feature-flags update path rejects Customers). Independent of the
        -- timesheet_manager flag — a back-office reviewer need not be a
        -- timesheet manager.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS timesheet_backoffice_enabled BOOLEAN NOT NULL DEFAULT FALSE;

        -- "Back Office checked" marker on the back-office tabs. Presence of a
        -- row = checked; absence = unchecked. Scoped per tab via `context`:
        --   'resolved' / 'cwi' → entity_id is a ticket id
        --   'adsolut'          → entity_id is an Adsolut sales-receipt id
        -- so the same entity can be checked independently on each tab. The
        -- key is a bare UUID with no FK (the referenced table differs per
        -- context); a deleted ticket/receipt simply leaves a harmless orphan
        -- row that no query ever surfaces (the list joins filter it out).
        -- checked_by / checked_utc record who ticked it and when (shown on
        -- hover) and survive the checker being deleted (SET NULL). Unticking
        -- deletes the row, so re-ticking later records the new checker.
        CREATE TABLE IF NOT EXISTS timesheet_bo_checks (
            entity_id   UUID        NOT NULL,
            context     TEXT        NOT NULL,
            checked_by  UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            checked_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (entity_id, context)
        );
        -- Migrate the original ticket-only shape (first v0.0.56 dev build:
        -- `ticket_id` column, FK to tickets, narrow context CHECK) to the
        -- generic entity_id shape. Idempotent: each step is guarded / IF
        -- EXISTS, and existing 'resolved'/'cwi' rows satisfy the new CHECK.
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name = 'timesheet_bo_checks' AND column_name = 'ticket_id') THEN
                ALTER TABLE timesheet_bo_checks RENAME COLUMN ticket_id TO entity_id;
            END IF;
        END $$;
        ALTER TABLE timesheet_bo_checks DROP CONSTRAINT IF EXISTS timesheet_bo_checks_ticket_id_fkey;
        ALTER TABLE timesheet_bo_checks DROP CONSTRAINT IF EXISTS chk_timesheet_bo_checks_context;
        ALTER TABLE timesheet_bo_checks ADD CONSTRAINT chk_timesheet_bo_checks_context
            CHECK (context IN ('resolved','cwi','adsolut'));
        CREATE INDEX IF NOT EXISTS ix_timesheet_bo_checks_context
            ON timesheet_bo_checks (context, entity_id);

        -- ===================================================================
        -- v0.0.52 — End-of-life data (endoflife.date mirror)
        --
        -- A background worker pulls the Microsoft Windows + Windows Server
        -- registries from endoflife.date weekly and upserts the rows below
        -- so the Assets page can flag agents whose OS is past or near
        -- end-of-support without a live network call on every render.
        -- Composite key (product, cycle) lets us share the table between
        -- the desktop and server feeds without name collisions.
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS eol_releases (
            product             TEXT        NOT NULL,
            cycle               TEXT        NOT NULL,
            release_label       TEXT        NULL,
            eol_utc             TIMESTAMPTZ NULL,
            lts                 BOOLEAN     NOT NULL DEFAULT FALSE,
            last_refreshed_utc  TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (product, cycle),
            CONSTRAINT chk_eol_product
                CHECK (product IN ('windows','windows-server'))
        );

        CREATE INDEX IF NOT EXISTS ix_eol_releases_eol
            ON eol_releases (eol_utc);

        -- ===================================================================
        -- v0.0.58 — Email signatures
        --
        -- Admin-managed, mailbox-scoped HTML signatures rendered from a
        -- block-tree design (mail_signatures.design). Per-sender variables
        -- (FullName/JobTitle/Phone/Mobile/Photo/…) are filled at send-time
        -- from Microsoft Entra ID with a per-user local override carried on
        -- the users table below. Image assets are stored content-addressed in
        -- IBlobStore (signature_assets) and embedded inline as cid attachments
        -- on each send so a recipient never sees a broken/blocked image. A
        -- dedicated is_system signature covers trigger/automated mail (no
        -- person variables). The whole feature is opt-in (Signatures.Enabled).
        -- ===================================================================
        CREATE TABLE IF NOT EXISTS mail_signatures (
            id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            name            TEXT        NOT NULL,
            design          JSONB       NOT NULL DEFAULT '{}'::jsonb,
            is_system       BOOLEAN     NOT NULL DEFAULT FALSE,
            enabled         BOOLEAN     NOT NULL DEFAULT TRUE,
            sort_order      INTEGER     NOT NULL DEFAULT 0,
            created_by      UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Which mailbox (queue) a signature is active on. queue_id is the PK
        -- so a queue resolves to exactly one signature — the send-time lookup
        -- is unambiguous. A signature can still be assigned to many queues.
        CREATE TABLE IF NOT EXISTS mail_signature_mailboxes (
            queue_id        UUID        PRIMARY KEY REFERENCES queues(id) ON DELETE CASCADE,
            signature_id    UUID        NOT NULL REFERENCES mail_signatures(id) ON DELETE CASCADE,
            created_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_mail_signature_mailboxes_signature
            ON mail_signature_mailboxes (signature_id);

        -- Image bytes live on disk via IBlobStore keyed by content_hash
        -- (SHA-256 hex), same as attachments — but kept in a dedicated table
        -- so the signature images never enter the attachment-jobs pipeline.
        CREATE TABLE IF NOT EXISTS signature_assets (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            signature_id        UUID        NOT NULL REFERENCES mail_signatures(id) ON DELETE CASCADE,
            content_hash        TEXT        NOT NULL,
            mime_type           TEXT        NOT NULL DEFAULT 'image/png',
            original_filename   TEXT        NOT NULL DEFAULT '',
            size_bytes          BIGINT      NOT NULL DEFAULT 0,
            created_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_signature_assets_signature
            ON signature_assets (signature_id);
        CREATE INDEX IF NOT EXISTS ix_signature_assets_hash
            ON signature_assets (content_hash);

        -- Agent profile fields for signature variables. Each column is a local
        -- override: NULL means "fall back to the Entra ID value at render time"
        -- (or collapse the token if Entra has nothing either). entra_synced_utc
        -- stamps the last successful Graph pull; photo bytes are content-
        -- addressed in IBlobStore via photo_blob_hash.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS display_name     TEXT        NULL,
            ADD COLUMN IF NOT EXISTS job_title        TEXT        NULL,
            ADD COLUMN IF NOT EXISTS work_phone       TEXT        NULL,
            ADD COLUMN IF NOT EXISTS mobile_phone     TEXT        NULL,
            ADD COLUMN IF NOT EXISTS photo_blob_hash  TEXT        NULL,
            ADD COLUMN IF NOT EXISTS photo_mime       TEXT        NULL,
            ADD COLUMN IF NOT EXISTS entra_synced_utc TIMESTAMPTZ NULL;

        -- ===================================================================
        -- v0.0.59 Adsolut ERP Orders (bestellingen) mirror.
        --
        -- Read-only mirror of the Adsolut ERP OrderInfos endpoint
        -- (GET /erp/v1/adm/{adm}/OrderInfos). Unlike SalesReceipts, the Orders
        -- list view returns the FULL order including its detail lines inline,
        -- so the sync upserts straight from the list page (no per-order by-id
        -- fetch); by-id is used only for a manual per-row resync and the
        -- ::-link single fetch. The API DOES expose header totals
        -- (totalPriceExclVat / totalPriceInclVat), so they are stored verbatim
        -- (no compute). ON DELETE CASCADE keeps the line rows in lockstep.
        --
        -- Status filter is DISPLAY-ONLY: the mirror always pulls every status;
        -- the admin's status selection only narrows the overview + global
        -- search. So there is no status-skip during sync and no purge here.
        CREATE TABLE IF NOT EXISTS adsolut_orders (
            id                       UUID          PRIMARY KEY,
            doc_nr                   INTEGER       NULL,
            book_code                TEXT          NULL,
            kluwer_ref               TEXT          NULL,
            customer_adsolut_id      UUID          NULL,
            customer_code            TEXT          NULL,
            customer_name            TEXT          NULL,
            state_id                 UUID          NULL,
            state_code               TEXT          NULL,
            state_description        TEXT          NULL,
            order_date               TIMESTAMPTZ   NULL,
            requested_delivery_date  TIMESTAMPTZ   NULL,
            confirmed_delivery_date  TIMESTAMPTZ   NULL,
            remark                   TEXT          NULL,
            internal_memo            TEXT          NULL,
            representative_code      TEXT          NULL,
            representative_name      TEXT          NULL,
            currency_iso             TEXT          NULL,
            total_excl_vat           NUMERIC(18,2) NOT NULL DEFAULT 0,
            total_incl_vat           NUMERIC(18,2) NOT NULL DEFAULT 0,
            ticket_number            BIGINT        NULL,
            adsolut_created_utc      TIMESTAMPTZ   NULL,
            adsolut_last_modified    TIMESTAMPTZ   NULL,
            synced_utc               TIMESTAMPTZ   NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_orders_state
            ON adsolut_orders (state_code);
        CREATE INDEX IF NOT EXISTS ix_adsolut_orders_date
            ON adsolut_orders (order_date DESC);
        CREATE INDEX IF NOT EXISTS ix_adsolut_orders_customer
            ON adsolut_orders (customer_adsolut_id);
        CREATE INDEX IF NOT EXISTS ix_adsolut_orders_ticket_number
            ON adsolut_orders (ticket_number) WHERE ticket_number IS NOT NULL;

        CREATE TABLE IF NOT EXISTS adsolut_order_lines (
            id                       UUID          PRIMARY KEY,
            order_id                 UUID          NOT NULL
                REFERENCES adsolut_orders (id) ON DELETE CASCADE,
            line_nr                  INTEGER       NULL,
            product_id               UUID          NULL,
            product_code             TEXT          NULL,
            description              TEXT          NULL,
            quantity                 NUMERIC(18,4) NULL,
            unit_code                TEXT          NULL,
            unit_description         TEXT          NULL,
            delivered                NUMERIC(18,4) NULL,
            gross_unit_price         NUMERIC(18,4) NULL,
            unit_price               NUMERIC(18,4) NULL,
            discount1                NUMERIC(18,4) NULL,
            discount2                NUMERIC(18,4) NULL,
            price_excl_vat           NUMERIC(18,2) NULL,
            price_incl_vat           NUMERIC(18,2) NULL,
            vat_code                 TEXT          NULL,
            vat_description          TEXT          NULL,
            state_code               TEXT          NULL,
            state_description        TEXT          NULL,
            requested_delivery_date  TIMESTAMPTZ   NULL,
            confirmed_delivery_date  TIMESTAMPTZ   NULL
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_order_lines_order
            ON adsolut_order_lines (order_id);

        -- Singleton sync-state for the Orders mirror. Separate cursor from
        -- Companies + SalesReceipts so enabling/pausing one never disturbs
        -- the others.
        CREATE TABLE IF NOT EXISTS adsolut_order_sync_state (
            id                  INTEGER     PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            last_full_sync_utc  TIMESTAMPTZ NULL,
            last_delta_sync_utc TIMESTAMPTZ NULL,
            last_error          TEXT        NULL,
            last_error_utc      TIMESTAMPTZ NULL,
            orders_seen         INTEGER     NOT NULL DEFAULT 0,
            orders_upserted     INTEGER     NOT NULL DEFAULT 0,
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Ticket ↔ Adsolut order links created via the ticket editor's "::"
        -- picker. FK to adsolut_orders ON DELETE CASCADE so a purged/removed
        -- order takes its links with it; FK to tickets ON DELETE CASCADE so a
        -- deleted ticket cleans up too. linked_by/linked_utc record who linked
        -- it and when (linker deletion SET NULL leaves the link intact).
        CREATE TABLE IF NOT EXISTS ticket_order_links (
            ticket_id   UUID        NOT NULL REFERENCES tickets (id) ON DELETE CASCADE,
            order_id    UUID        NOT NULL REFERENCES adsolut_orders (id) ON DELETE CASCADE,
            linked_by   UUID        NULL REFERENCES users (id) ON DELETE SET NULL,
            linked_utc  TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (ticket_id, order_id)
        );

        CREATE INDEX IF NOT EXISTS ix_ticket_order_links_order
            ON ticket_order_links (order_id);

        -- Per-user opt-in flag for the Adsolut Orders feature (navbar overview
        -- under Assets, order detail, the ticket "Sync orders" button and the
        -- "::" order linking). Mirrors the other per-user feature flags
        -- (kb_enabled, assets_enabled, adsolut_timesheet_enabled): default
        -- FALSE, no backfill, strictly opt-in, Agent/Admin only (the
        -- feature-flags update path rejects Customers). The flag alone surfaces
        -- nothing without the Adsolut integration being connected.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS adsolut_orders_enabled BOOLEAN NOT NULL DEFAULT FALSE;

        -- v0.0.59 (orders fix) — Adsolut supplier orders ("bestellingen", Doc
        -- "BL"). Read-mirror of the ERP SupplierOrderInfos endpoint, which
        -- carries the REAL per-article procurement status (ONTV/Ontvangen,
        -- OPEN), supplier (leverancier), order date and delivered qty — none of
        -- which exist on OrderInfos. Each supplier-order line nests an
        -- orderInfoDetail.orderInfoId that links back to the sales order HEADER
        -- (there is no order-LINE id on the BL line + the product id is shared
        -- across duplicate article lines, so the link is header-level only).
        -- The order-detail "Bestellingen" block lists these lines by
        -- linked_order_id. Denormalised into one table (supplier + date copied
        -- onto each line) so the detail query needs no join; lines are replaced
        -- wholesale per supplier order (grouped by supplier_order_id).
        CREATE TABLE IF NOT EXISTS adsolut_supplier_order_lines (
            id                     UUID          PRIMARY KEY,
            supplier_order_id      UUID          NOT NULL,
            bl_doc_nr              INTEGER       NULL,
            bl_book_code           TEXT          NULL,
            supplier_name          TEXT          NULL,
            supplier_code          TEXT          NULL,
            supplier_order_date    TIMESTAMPTZ   NULL,
            header_state_code      TEXT          NULL,
            warehouse_id           UUID          NULL,
            warehouse_code         TEXT          NULL,
            warehouse_location_id  UUID          NULL,
            warehouse_location_code TEXT         NULL,
            line_nr                INTEGER       NULL,
            product_id             UUID          NULL,
            product_code           TEXT          NULL,
            name                   TEXT          NULL,
            description            TEXT          NULL,
            quantity               NUMERIC(18,4) NULL,
            delivered              NUMERIC(18,4) NULL,
            unit_code              TEXT          NULL,
            gross_unit_price       NUMERIC(18,4) NULL,
            unit_price             NUMERIC(18,4) NULL,
            discount1              NUMERIC(18,4) NULL,
            status_code            TEXT          NULL,
            linked_order_id        UUID          NULL,
            linked_order_doc_nr    INTEGER       NULL,
            adsolut_last_modified  TIMESTAMPTZ   NULL,
            synced_utc             TIMESTAMPTZ   NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_supplier_order_lines_linked_order
            ON adsolut_supplier_order_lines (linked_order_id);
        CREATE INDEX IF NOT EXISTS ix_adsolut_supplier_order_lines_supplier_order
            ON adsolut_supplier_order_lines (supplier_order_id);
        CREATE INDEX IF NOT EXISTS ix_adsolut_supplier_order_lines_status
            ON adsolut_supplier_order_lines (status_code);

        -- Supplier-orders delta cursor lives alongside the orders cursor in the
        -- singleton sync-state (same worker tick + same Adsolut.Erp.Orders
        -- toggle, independent ModifiedSince so pausing one never disturbs the
        -- other).
        ALTER TABLE adsolut_order_sync_state
            ADD COLUMN IF NOT EXISTS supplier_last_delta_sync_utc TIMESTAMPTZ NULL,
            ADD COLUMN IF NOT EXISTS supplier_orders_seen         INTEGER NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS supplier_orders_upserted     INTEGER NOT NULL DEFAULT 0;

        -- Per-line warehouse (Stock) + warehouseLocation (Location) on each
        -- supplier-order line: id + code stored, the display name resolved at
        -- read time against the Warehouses mirror below.
        ALTER TABLE adsolut_supplier_order_lines
            ADD COLUMN IF NOT EXISTS warehouse_id            UUID NULL,
            ADD COLUMN IF NOT EXISTS warehouse_code          TEXT NULL,
            ADD COLUMN IF NOT EXISTS warehouse_location_id   UUID NULL,
            ADD COLUMN IF NOT EXISTS warehouse_location_code TEXT NULL;

        -- Warehouses ("magazijnen") reference mirror — small list pulled from
        -- GET /erp/v1/adm/{adm}/Warehouses each Orders tick. Resolves the
        -- supplier-order line's warehouse {id, code} → a readable name (Stock),
        -- and its locations resolve warehouseLocation.id → name (Location).
        CREATE TABLE IF NOT EXISTS adsolut_warehouses (
            id          UUID        PRIMARY KEY,
            code        TEXT        NULL,
            name        TEXT        NULL,
            active      BOOLEAN     NOT NULL DEFAULT TRUE,
            standard    BOOLEAN     NOT NULL DEFAULT FALSE,
            synced_utc  TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS adsolut_warehouse_locations (
            id           UUID        PRIMARY KEY,
            warehouse_id UUID        NULL,
            name         TEXT        NULL,
            is_default   BOOLEAN     NOT NULL DEFAULT FALSE,
            synced_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_warehouse_locations_warehouse
            ON adsolut_warehouse_locations (warehouse_id);

        -- ===================================================================
        -- v0.0.69 Statistics — per-user feature flags.
        --
        -- Two per-user opt-in flags for the Statistics feature (a light
        -- Power BI-style tile builder). Mirrors the other per-user feature
        -- flags (kb_enabled, assets_enabled, adsolut_*): default FALSE, no
        -- backfill, strictly opt-in, Agent/Admin only (the feature-flags
        -- update path rejects Customers).
        --   statistics_read  → may view the Statistics page and the tiles
        --                      assigned to them.
        --   statistics_write → may build statistic tiles and assign them to
        --                      read-enabled agents. Independent of read: a
        --                      builder is normally also given read, but the
        --                      flags are stored separately so the UI gates
        --                      each capability on its own.
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS statistics_read  BOOLEAN NOT NULL DEFAULT FALSE,
            ADD COLUMN IF NOT EXISTS statistics_write BOOLEAN NOT NULL DEFAULT FALSE;

        -- Author-defined statistic tiles. A statistics_write user composes a
        -- tile (metric + period + grouping + chart type + scope) and assigns
        -- it to statistics_read agents. metric_key / chart_type / period /
        -- grouping / scope are validated in code against the catalogue, not by
        -- a DB CHECK, so adding catalogue entries never needs a migration.
        --   scope          'viewer_self' → rebinds to whoever views the tile
        --                  'user'        → scope_user_id (a single technician)
        --                  'team'        → all Agent/Admin users
        --   scope_user_id  the target technician for scope='user' (SET NULL on
        --                  user delete → the engine then yields an empty tile)
        --   filters_json   reserved for the later generic builder; '{}' for now
        CREATE TABLE IF NOT EXISTS statistic_tiles (
            id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            title         TEXT        NOT NULL,
            metric_key    TEXT        NOT NULL,
            chart_type    TEXT        NOT NULL,
            period        TEXT        NOT NULL,
            grouping      TEXT        NOT NULL DEFAULT 'none',
            scope         TEXT        NOT NULL,
            scope_user_id UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            scope_user_ids TEXT       NULL,
            filters_json  JSONB       NOT NULL DEFAULT '{}'::jsonb,
            created_by    UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            created_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        -- v0.0.69 (compare scope) — CSV of technician ids for scope='users'
        -- (multi-technician comparison in one tile). Added here for installs
        -- created before the column existed.
        ALTER TABLE statistic_tiles
            ADD COLUMN IF NOT EXISTS scope_user_ids TEXT NULL;

        -- Which read-agents a tile is assigned to. Deleting the tile or the
        -- user cascades the assignment away.
        CREATE TABLE IF NOT EXISTS statistic_tile_assignments (
            tile_id          UUID        NOT NULL REFERENCES statistic_tiles(id) ON DELETE CASCADE,
            assigned_user_id UUID        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            assigned_by      UUID        NULL REFERENCES users(id) ON DELETE SET NULL,
            assigned_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (tile_id, assigned_user_id)
        );
        CREATE INDEX IF NOT EXISTS ix_statistic_tile_assignments_user
            ON statistic_tile_assignments (assigned_user_id);

        -- Per-viewer layout of their assigned tiles (position / size / hidden).
        -- Mirrors user_dashboard_tiles but adds `hidden` so a read-agent can
        -- hide an assigned tile without it being un-assigned. A missing row
        -- means "use defaults" (appended at the end, medium, visible).
        CREATE TABLE IF NOT EXISTS statistic_tile_layout (
            user_id  UUID    NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            tile_id  UUID    NOT NULL REFERENCES statistic_tiles(id) ON DELETE CASCADE,
            position INT     NOT NULL,
            size     TEXT    NOT NULL DEFAULT 'medium',
            hidden   BOOLEAN NOT NULL DEFAULT FALSE,
            PRIMARY KEY (user_id, tile_id)
        );

        -- ===================================================================
        -- Title-review gate — interactive first-open gate.
        --
        -- Reuses the v0.0.42 gate machinery (activator_kind='gate') with a
        -- new mode 'first_open': instead of gating a status change, it gates
        -- the first time an agent opens a ticket. The agent reviews/edits the
        -- subject in a blocking dialog before they can work the ticket. The
        -- single gate action 'title_review' carries the dialog payload; any
        -- regular actions on the same trigger (e.g. add_internal_note) run
        -- after a successful confirmation, so the approval note is a normal
        -- composable action rather than baked into the gate.
        -- ===================================================================
        ALTER TABLE triggers DROP CONSTRAINT IF EXISTS chk_trigger_activator;
        ALTER TABLE triggers ADD CONSTRAINT chk_trigger_activator
            CHECK (
                (activator_kind = 'action' AND activator_mode IN ('selective','always'))
                OR
                (activator_kind = 'time'   AND activator_mode IN ('reminder','escalation','escalation_warning'))
                OR
                (activator_kind = 'manual' AND activator_mode IN ('linked_ticket_creator'))
                OR
                (activator_kind = 'gate'   AND activator_mode IN ('status_change','first_open'))
            ) NOT VALID;

        -- One-time marker that a ticket's title has been reviewed at first
        -- open. Set atomically by the first agent who confirms the gate, so
        -- the blocking dialog surfaces exactly once per ticket regardless of
        -- how many agents open it. Imported / app-native tickets created
        -- before the feature simply have NULL here and get the dialog on the
        -- next open if a matching gate exists.
        ALTER TABLE tickets
            ADD COLUMN IF NOT EXISTS title_reviewed_utc          TIMESTAMPTZ NULL,
            ADD COLUMN IF NOT EXISTS title_reviewed_by_user_id   UUID        NULL
                REFERENCES users(id) ON DELETE SET NULL;

        -- ===================================================================
        -- v0.0.76 Contracts — per-user feature flag.
        --
        -- Opt-in flag for the Contracts page (tile hub; the contract data
        -- model lands in a later release). Mirrors the other per-user
        -- feature flags (kb_enabled, assets_enabled, statistics_*): default
        -- FALSE, no backfill, strictly opt-in, Agent/Admin only (the
        -- feature-flags update path rejects Customers).
        ALTER TABLE users
            ADD COLUMN IF NOT EXISTS contracts_enabled BOOLEAN NOT NULL DEFAULT FALSE;

        -- v0.0.76 Contracts — Articles mirror. Read-mirror of the Adsolut ERP
        -- Articles endpoint (GET /erp/v1/adm/{adm}/Articles), the catalogue of
        -- article/product master records. First data-bearing module behind the
        -- Contracts hub ("Contract Articles" tile). Same ERP machinery as the
        -- Orders/SalesReceipts slices (WK.BE.ERP.Read scope, cursor pagination,
        -- ModifiedSince delta). A flat reference list — no lines, no customer,
        -- no status. name/description come back as multi-language Translation[]
        -- (the Nl value is stored); vat_code/vat_rate come from the inline
        -- vatCode object (code + the Nl description, e.g. "21%").
        CREATE TABLE IF NOT EXISTS adsolut_articles (
            id                     UUID        PRIMARY KEY,
            code                   TEXT        NULL,
            name                   TEXT        NULL,
            description            TEXT        NULL,
            vat_code               TEXT        NULL,
            vat_rate               TEXT        NULL,
            active                 BOOLEAN     NOT NULL DEFAULT TRUE,
            adsolut_created_utc    TIMESTAMPTZ NULL,
            adsolut_last_modified  TIMESTAMPTZ NULL,
            synced_utc             TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_articles_code
            ON adsolut_articles (code);
        CREATE INDEX IF NOT EXISTS ix_adsolut_articles_active
            ON adsolut_articles (active);

        -- Singleton sync-state for the Articles mirror. Own cursor, separate
        -- from Orders/SalesReceipts/Companies so enabling/pausing one never
        -- disturbs the others.
        CREATE TABLE IF NOT EXISTS adsolut_article_sync_state (
            id                  INTEGER     PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            last_full_sync_utc  TIMESTAMPTZ NULL,
            last_delta_sync_utc TIMESTAMPTZ NULL,
            last_error          TEXT        NULL,
            last_error_utc      TIMESTAMPTZ NULL,
            articles_seen       INTEGER     NOT NULL DEFAULT 0,
            articles_upserted   INTEGER     NOT NULL DEFAULT 0,
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- ERP Contracts (contracten) mirror → Contracts overview (Contracts hub
        -- → Contracts overview tile). v0.0.76. Same ERP machinery as Orders:
        -- the Contracts list view returns the full contract incl. its article
        -- lines inline, so the sync upserts straight from the list page (no
        -- per-contract by-id N+1); by-id exists only for a manual resync.
        --
        -- Unlike verkoopbonnen/orders, a contract carries NO Ticket# ref — it
        -- keys off the customer. customer_adsolut_id = the Adsolut customerId
        -- (UUID), which maps 1:1 to companies.adsolut_id; the overview LEFT
        -- JOINs companies on that id to surface the linked relation (clickable)
        -- + its relation code (companies.adsolut_number). customer_name is the
        -- company name copied onto the contract, shown as the fallback when no
        -- local company matches. contractState is inline (code + per-language
        -- label), so the dynamic status filter is derived from the distinct
        -- state codes mirrored here — no separate ContractStates reference table.
        --
        -- Status filter is DISPLAY-ONLY: the mirror always pulls every status;
        -- the admin's selection only narrows the overview + global search. So
        -- there is no status-skip during sync and no purge here.
        CREATE TABLE IF NOT EXISTS adsolut_contracts (
            id                          UUID          PRIMARY KEY,
            doc_nr                      INTEGER       NULL,
            customer_adsolut_id         UUID          NULL,
            invoice_customer_adsolut_id UUID          NULL,
            customer_name               TEXT          NULL,
            state_code                  TEXT          NULL,
            state_description           TEXT          NULL,
            doc_date                    TIMESTAMPTZ   NULL,
            start_date                  TIMESTAMPTZ   NULL,
            stop_date                   TIMESTAMPTZ   NULL,
            end_date                    TIMESTAMPTZ   NULL,
            description                 TEXT          NULL,
            memo                        TEXT          NULL,
            periodicity_code            TEXT          NULL,
            periodicity_label           TEXT          NULL,
            invoicing_periodicity_code  TEXT          NULL,
            invoicing_periodicity_label TEXT          NULL,
            number_of_terms             INTEGER       NULL,
            total_excl_vat              NUMERIC(18,2) NOT NULL DEFAULT 0,
            total_vat                   NUMERIC(18,2) NOT NULL DEFAULT 0,
            total_incl_vat              NUMERIC(18,2) NOT NULL DEFAULT 0,
            adsolut_created_utc         TIMESTAMPTZ   NULL,
            adsolut_last_modified       TIMESTAMPTZ   NULL,
            synced_utc                  TIMESTAMPTZ   NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_contracts_state
            ON adsolut_contracts (state_code);
        CREATE INDEX IF NOT EXISTS ix_adsolut_contracts_customer
            ON adsolut_contracts (customer_adsolut_id);
        CREATE INDEX IF NOT EXISTS ix_adsolut_contracts_end_date
            ON adsolut_contracts (end_date DESC);

        -- Contract article lines (contractDetailArticles). The contract payload
        -- has no line number, so line_nr is the array ordinal assigned at parse
        -- time for a stable display order. ON DELETE CASCADE keeps lines in
        -- lockstep with the header (lines are replaced wholesale per upsert).
        CREATE TABLE IF NOT EXISTS adsolut_contract_lines (
            id                  UUID          PRIMARY KEY,
            contract_id         UUID          NOT NULL
                REFERENCES adsolut_contracts (id) ON DELETE CASCADE,
            line_nr             INTEGER       NULL,
            article_id          UUID          NULL,
            name                TEXT          NULL,
            description         TEXT          NULL,
            quantity            NUMERIC(18,4) NULL,
            gross_unit_price    NUMERIC(18,4) NULL,
            discount1           NUMERIC(18,4) NULL,
            discount2           NUMERIC(18,4) NULL,
            unit_price          NUMERIC(18,4) NULL,
            unit_price_incl     NUMERIC(18,4) NULL,
            start_date          TIMESTAMPTZ   NULL,
            end_date            TIMESTAMPTZ   NULL
        );

        CREATE INDEX IF NOT EXISTS ix_adsolut_contract_lines_contract
            ON adsolut_contract_lines (contract_id);
        -- The Microsoft 365 matching list filters lines by article_id, so index it.
        CREATE INDEX IF NOT EXISTS ix_adsolut_contract_lines_article
            ON adsolut_contract_lines (article_id);

        -- Singleton sync-state for the Contracts mirror. Own cursor, separate
        -- from Orders/SalesReceipts/Articles/Companies so enabling/pausing one
        -- never disturbs the others.
        CREATE TABLE IF NOT EXISTS adsolut_contract_sync_state (
            id                  INTEGER     PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            last_full_sync_utc  TIMESTAMPTZ NULL,
            last_delta_sync_utc TIMESTAMPTZ NULL,
            last_error          TEXT        NULL,
            last_error_utc      TIMESTAMPTZ NULL,
            contracts_seen      INTEGER     NOT NULL DEFAULT 0,
            contracts_upserted  INTEGER     NOT NULL DEFAULT 0,
            updated_utc         TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- ERP customer (relation) resolution map. A contract references its
        -- customer by the ERP customer GUID (customerId); the ERP and Accounting
        -- APIs hold the SAME relations but assign DIFFERENT GUIDs, so that GUID
        -- does not match companies.adsolut_id. The shared key is the relation
        -- CODE: GET /erp/v1/adm/{adm}/Customers/{id} returns it, and it equals
        -- companies.adsolut_number (exactly how Orders/SalesReceipts bridge to a
        -- local company). This table caches id → code resolved during the
        -- Contracts sync so the matching join stays a fast pure-DB lookup. A row
        -- with NULL code means "resolved, but the ERP customer carried no code".
        CREATE TABLE IF NOT EXISTS adsolut_erp_customers (
            id         UUID        PRIMARY KEY,
            code       TEXT        NULL,
            name       TEXT        NULL,
            synced_utc TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS ix_adsolut_erp_customers_code
            ON adsolut_erp_customers (code);

        -- ===================================================================
        -- v0.0.77 Outbound mail — large attachments (uploadSession).
        --
        -- The outbound total-attachment cap default rises 3 MB → 25 MB now
        -- that parts above Graph's ~3 MB single-request limit ship via a
        -- chunked upload session on the draft. Existing installs whose
        -- Mail.MaxOutboundTotalBytes still sits on the old 3 MB default get
        -- bumped to the new default once; any admin-tuned value (≠ 3145728)
        -- is preserved. Behind a data_migrations marker because the effect
        -- (a settings.value write) is not idempotent. On a fresh install the
        -- settings row doesn't exist yet when this runs — the UPDATE matches
        -- nothing and EnsureDefaultsAsync later seeds 25 MB directly.
        DO $do$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM data_migrations
                WHERE name = 'v0_0_77_raise_outbound_mail_cap_default'
            ) THEN
                UPDATE settings
                SET value = '26214400',
                    updated_utc = now()
                WHERE key = 'Mail.MaxOutboundTotalBytes'
                  AND value = '3145728';
                INSERT INTO data_migrations (name)
                    VALUES ('v0_0_77_raise_outbound_mail_cap_default');
            END IF;
        END $do$;

        -- ===================================================================
        -- Microsoft 365 per-customer connect (consent + Graph read).
        --
        -- The MSP owns one multi-tenant app (the M365.* settings + the
        -- protected client secret). Each customer admin grants admin consent
        -- once, after which we read that customer's tenant app-only. Three
        -- concerns, three+1 tables: a short-lived anti-CSRF state for the
        -- consent round-trip, the per-company tenant link, the synced mailbox
        -- mirror, and per-company sync metadata.

        -- Pending consent round-trips. The state token is the entire trust
        -- model of the PUBLIC callback (the customer admin is not signed in to
        -- Servicedesk): single-use, server-time-boxed, tied to the initiating
        -- company + agent. Consumed/expired rows are swept best-effort.
        CREATE TABLE IF NOT EXISTS m365_consent_states (
            state        TEXT        PRIMARY KEY,
            company_id   UUID        NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            initiated_by UUID        NULL,
            created_utc  TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_utc  TIMESTAMPTZ NOT NULL,
            consumed_utc TIMESTAMPTZ NULL
        );
        CREATE INDEX IF NOT EXISTS ix_m365_consent_states_company
            ON m365_consent_states (company_id);

        -- One link per company. tenant_id is the customer's directory id
        -- returned by Azure on consent — an identifier, not a secret. status
        -- drives the button: connected / needs_reconsent / disconnected / error.
        CREATE TABLE IF NOT EXISTS m365_tenant_links (
            company_id        UUID        PRIMARY KEY REFERENCES companies(id) ON DELETE CASCADE,
            tenant_id         TEXT        NOT NULL,
            status            TEXT        NOT NULL DEFAULT 'connected',
            consented_at      TIMESTAMPTZ NULL,
            granted_by        UUID        NULL,
            last_verified_utc TIMESTAMPTZ NULL,
            last_error        TEXT        NULL,
            created_utc       TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc       TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Per-company sync metadata. content_hash lets a tick cheaply detect
        -- "nothing changed" and record last_checked without bumping last_changed.
        CREATE TABLE IF NOT EXISTS m365_company_sync (
            company_id       UUID        PRIMARY KEY REFERENCES companies(id) ON DELETE CASCADE,
            last_checked_utc TIMESTAMPTZ NULL,
            last_changed_utc TIMESTAMPTZ NULL,
            last_status      TEXT        NULL,
            last_error       TEXT        NULL,
            mailbox_count    INT         NOT NULL DEFAULT 0,
            content_hash     TEXT        NULL,
            duration_ms      INT         NULL
        );

        -- Synced mailbox mirror. One row per directory object that has a
        -- mailbox. licenses is a readable, comma-separated SKU part-number list
        -- (display copy). mailbox_type is the mailboxSettings.userPurpose value.
        CREATE TABLE IF NOT EXISTS m365_mailboxes (
            company_id   UUID        NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            object_id    TEXT        NOT NULL,
            mailbox_type TEXT        NULL,
            display_name TEXT        NULL,
            given_name   TEXT        NULL,
            surname      TEXT        NULL,
            upn          TEXT        NULL,
            mail         TEXT        NULL,
            enabled      BOOLEAN     NULL,
            licenses     TEXT        NULL,
            synced_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (company_id, object_id)
        );
        CREATE INDEX IF NOT EXISTS ix_m365_mailboxes_company
            ON m365_mailboxes (company_id);

        -- ===================================================================
        -- Contract reports (v0.1.x). Agent-authored email templates plus a
        -- per-company "Send report" audit trail. Lives behind the Contracts
        -- hub (contracts_enabled flag), available to anyone with contracts
        -- access — not admin-only. The first report kind is the Microsoft 365
        -- matching overview (which mailboxes are spam/backup protected); the
        -- `purpose` discriminator keeps the door open for future report kinds
        -- and the planned bulk-send screen without a schema change.
        -- ===================================================================

        -- One reusable report email template. body_html carries {{tokens}}
        -- including {{report.table}} (where the generated overview is injected)
        -- and summary tokens. queue_id picks the FROM mailbox (the queue's
        -- outbound/inbound address) so the sender is fixed at authoring time.
        -- columns is the default column selection; scope is 'all' or
        -- 'unprotected'. attach_pdf toggles the PDF copy of the overview.
        CREATE TABLE IF NOT EXISTS report_templates (
            id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            purpose     TEXT        NOT NULL DEFAULT 'm365',
            name        TEXT        NOT NULL,
            description TEXT        NULL,
            subject     TEXT        NOT NULL DEFAULT '',
            body_html   TEXT        NOT NULL DEFAULT '',
            queue_id    UUID        NULL REFERENCES queues(id) ON DELETE SET NULL,
            columns     TEXT[]      NOT NULL DEFAULT '{}',
            scope       TEXT        NOT NULL DEFAULT 'all',
            attach_pdf  BOOLEAN     NOT NULL DEFAULT TRUE,
            is_active   BOOLEAN     NOT NULL DEFAULT TRUE,
            created_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by  UUID        NULL,
            CONSTRAINT chk_report_templates_scope CHECK (scope IN ('all','unprotected'))
        );
        -- At most one active template per (purpose, name) — case-insensitive.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_report_templates_active_name
            ON report_templates (purpose, lower(name)) WHERE is_active;

        -- Per-company send audit / history. Drives the "last sent" stamp on the
        -- matching list and a future send-history view. recipients is a frozen
        -- JSON snapshot of who it went to; the summary counts let the history
        -- show protection at the time of sending without re-deriving it.
        CREATE TABLE IF NOT EXISTS m365_report_sends (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            company_id          UUID        NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            template_id         UUID        NULL REFERENCES report_templates(id) ON DELETE SET NULL,
            template_name       TEXT        NULL,
            sent_by             UUID        NULL,
            sent_by_name        TEXT        NULL,
            sent_utc            TIMESTAMPTZ NOT NULL DEFAULT now(),
            subject             TEXT        NOT NULL DEFAULT '',
            recipients          JSONB       NOT NULL DEFAULT '[]'::jsonb,
            columns             TEXT[]      NOT NULL DEFAULT '{}',
            scope               TEXT        NOT NULL DEFAULT 'all',
            mailbox_count       INT         NOT NULL DEFAULT 0,
            spam_protected      INT         NULL,
            exchange_protected  INT         NULL,
            onedrive_protected  INT         NULL,
            internet_message_id TEXT        NULL,
            status              TEXT        NOT NULL DEFAULT 'sent',
            error               TEXT        NULL,
            CONSTRAINT chk_m365_report_sends_status CHECK (status IN ('sent','failed'))
        );
        CREATE INDEX IF NOT EXISTS ix_m365_report_sends_company_sent
            ON m365_report_sends (company_id, sent_utc DESC);

        -- Reporting contacts: a per-(contact,company) flag marking which linked
        -- contacts receive contract reports by default. The send screen
        -- pre-fills these; a company with none triggers a warning + inline
        -- "designate" action. Lives on the link table because the same contact
        -- can be a reporting contact for one company but not another.
        ALTER TABLE contact_companies
            ADD COLUMN IF NOT EXISTS is_reporting_contact BOOLEAN NOT NULL DEFAULT FALSE;
        CREATE INDEX IF NOT EXISTS ix_contact_companies_reporting
            ON contact_companies (company_id) WHERE is_reporting_contact;

        -- ===================================================================
        -- Sophos Central spam-filter matching (v0.0.78).
        --
        -- MSP partner model: one credential pair (in protected_secrets) lists
        -- every tenant the partner manages. Each tenant's showAs carries the
        -- customer code as "[NNN] Name" — the same relation-code bridge the
        -- M365 matching uses (companies.adsolut_number). For tenants matched to
        -- an M365-connected company we also pull the tenant's protected mailbox
        -- addresses; the M365 company view marks each M365 mailbox
        -- Protected/Unprotected by membership in that set.

        -- Partner tenant snapshot. tenant_id + api_host are identifiers, not
        -- secrets. company_code is the parsed [NNN]; company_id is resolved
        -- against companies.adsolut_number after each pull (NULL = unmatched).
        CREATE TABLE IF NOT EXISTS sophos_tenants (
            tenant_id        TEXT        PRIMARY KEY,
            name             TEXT        NULL,
            show_as          TEXT        NULL,
            company_code     TEXT        NULL,
            company_id       UUID        NULL REFERENCES companies(id) ON DELETE SET NULL,
            api_host         TEXT        NULL,
            status           TEXT        NULL,
            data_region      TEXT        NULL,
            billing_type     TEXT        NULL,
            mailbox_count    INT         NOT NULL DEFAULT 0,
            last_synced_utc  TIMESTAMPTZ NULL,
            created_utc      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_utc      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS ix_sophos_tenants_company_code
            ON sophos_tenants (company_code);
        CREATE INDEX IF NOT EXISTS ix_sophos_tenants_company
            ON sophos_tenants (company_id);

        -- Protected (spam-filtered) mailbox addresses for the M365-matched
        -- tenants. email is citext so the Protected/Unprotected match against
        -- an M365 mailbox is case-insensitive. Cascades when its tenant leaves
        -- the partner snapshot.
        CREATE TABLE IF NOT EXISTS sophos_mailboxes (
            tenant_id     TEXT        NOT NULL REFERENCES sophos_tenants(tenant_id) ON DELETE CASCADE,
            email         CITEXT      NOT NULL,
            display_name  TEXT        NULL,
            mailbox_type  TEXT        NULL,
            synced_utc    TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, email)
        );
        CREATE INDEX IF NOT EXISTS ix_sophos_mailboxes_email
            ON sophos_mailboxes (email);

        -- Singleton global sync state (one row, id = 1). content_hash lets a
        -- tick cheaply detect "nothing changed" and record last_checked without
        -- bumping last_changed.
        CREATE TABLE IF NOT EXISTS sophos_sync_state (
            id               INT         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            last_checked_utc TIMESTAMPTZ NULL,
            last_changed_utc TIMESTAMPTZ NULL,
            last_status      TEXT        NULL,
            last_error       TEXT        NULL,
            tenant_count     INT         NOT NULL DEFAULT 0,
            mailbox_count    INT         NOT NULL DEFAULT 0,
            content_hash     TEXT        NULL,
            duration_ms      INT         NULL
        );

        -- ===================================================================
        -- Veeam backup matching (Veeam Service Provider Console).
        --
        -- For each Microsoft 365-connected company we resolve its relation code
        -- (companies.adsolut_number) to a VSPC company (ownerCredentials.userName)
        -- and pull that company's VB365 protected objects. VSPC exposes no email
        -- / UPN / Entra id at the object level — only the display name — so the
        -- per-mailbox backup status is keyed by the normalized display name and
        -- joined to the M365 mailbox table on that. The M365 company view shows
        -- each mailbox an OneDrive / Exchange Protected (or Unprotected) pill.

        -- One row per company that matched a VSPC company. Presence (with a
        -- non-null vspc_company_uid) is what makes the company "known in Veeam"
        -- and surfaces the backup columns. object_count is the raw VB365
        -- protected-object count last seen for the company.
        CREATE TABLE IF NOT EXISTS veeam_companies (
            company_id        UUID        PRIMARY KEY REFERENCES companies(id) ON DELETE CASCADE,
            vspc_company_uid  TEXT        NULL,
            vspc_company_name TEXT        NULL,
            object_count      INT         NOT NULL DEFAULT 0,
            last_synced_utc   TIMESTAMPTZ NULL
        );

        -- Per-mailbox backup status for a matched company, keyed by the
        -- normalized display name (lower(trim(name))). Exchange and OneDrive
        -- fold into one row. Cascades when its company row leaves the snapshot.
        CREATE TABLE IF NOT EXISTS veeam_backups (
            company_id               UUID        NOT NULL REFERENCES veeam_companies(company_id) ON DELETE CASCADE,
            match_key                TEXT        NOT NULL,
            display_name             TEXT        NULL,
            exchange_protected       BOOLEAN     NOT NULL DEFAULT false,
            exchange_restore_points  INT         NULL,
            exchange_last_backup_utc TIMESTAMPTZ NULL,
            onedrive_protected       BOOLEAN     NOT NULL DEFAULT false,
            onedrive_restore_points  INT         NULL,
            onedrive_last_backup_utc TIMESTAMPTZ NULL,
            synced_utc               TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (company_id, match_key)
        );
        CREATE INDEX IF NOT EXISTS ix_veeam_backups_company
            ON veeam_backups (company_id);

        -- Singleton global sync state (one row, id = 1). content_hash lets a
        -- tick cheaply detect "nothing changed" and record last_checked without
        -- bumping last_changed.
        CREATE TABLE IF NOT EXISTS veeam_sync_state (
            id               INT         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            last_checked_utc TIMESTAMPTZ NULL,
            last_changed_utc TIMESTAMPTZ NULL,
            last_status      TEXT        NULL,
            last_error       TEXT        NULL,
            company_count    INT         NOT NULL DEFAULT 0,
            object_count     INT         NOT NULL DEFAULT 0,
            content_hash     TEXT        NULL,
            duration_ms      INT         NULL
        );
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

                // On a fresh database the CREATE EXTENSION statements above
                // install citext/pg_trgm/unaccent/pgcrypto. Npgsql caches the
                // type-OID → CLR-type map on first-connection (e.g. for
                // DataProtection's keyring read that happens before this
                // hosted service). Connections opened before the extensions
                // existed return DataTypeName "-" for citext columns, which
                // crashes Dapper later (TaxonomyRepository.ListQueuesAsync).
                // ReloadTypes refreshes the cache for this connection, and
                // ClearPool discards every other pooled connection so future
                // rents re-fetch the now-complete type catalogue.
                connection.ReloadTypes();
                NpgsqlConnection.ClearPool(connection);

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
