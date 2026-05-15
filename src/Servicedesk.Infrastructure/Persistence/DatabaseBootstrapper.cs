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

        -- v0.0.23 ticket split: extend the source allow-list with 'Split' so the
        -- new ticket can record where it came from. Drop-then-add is idempotent
        -- across re-deploys; existing rows are guaranteed to satisfy the new
        -- predicate (it's a superset of the old one).
        ALTER TABLE tickets DROP CONSTRAINT IF EXISTS chk_ticket_source;
        ALTER TABLE tickets ADD CONSTRAINT chk_ticket_source
            CHECK (source IN ('Web','Mail','Api','System','Split'));

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
