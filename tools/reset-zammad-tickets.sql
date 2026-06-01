-- =====================================================================
-- reset-zammad-tickets.sql  (dev utility)
--
-- Hard-deletes Zammad-imported tickets so they can be re-imported with the
-- v0.0.54 inline-image rewriter applied. DESTRUCTIVE — review the matched
-- list it prints before re-running with apply=yes.
--
-- Safe by default: with no apply flag it runs a DRY RUN (prints what WOULD
-- be deleted, then ROLLBACKs) so you can preview the scope first.
--
-- Scope is chosen via psql -v variables:
--   scope   = all  | list      (default: list)
--   numbers = comma-separated LOCAL ticket numbers — the #N shown in the UI
--             (used when scope=list)
--   apply   = yes  | no         (default: no  -> dry run)
--
-- NOTE: re-importing assigns FRESH local numbers (the #N sequence advances),
-- so a deleted #1200 comes back under a new number. Fine on dev; just don't
-- rely on the old numbers afterwards.
--
-- Examples (PowerShell; PGPASSWORD already in env):
--   # Preview a single ticket
--   psql ... -v scope=list -v numbers=1200 -f tools/reset-zammad-tickets.sql
--   # Delete a single ticket
--   psql ... -v scope=list -v numbers=1200 -v apply=yes -f tools/reset-zammad-tickets.sql
--   # Delete a list
--   psql ... -v scope=list -v numbers=1200,1201,1205 -v apply=yes -f tools/reset-zammad-tickets.sql
--   # Delete ALL Zammad-imported tickets (full clean re-import)
--   psql ... -v scope=all -v apply=yes -f tools/reset-zammad-tickets.sql
--
-- After deleting, re-run the Zammad ticket import; the new ticket bodies
-- will relink inline images to /api/tickets/{id}/attachments/{localId}.
-- =====================================================================

\set ON_ERROR_STOP on

-- Defaults for any variable not supplied on the command line.
\if :{?apply}
\else
  \set apply no
\endif
\if :{?scope}
\else
  \set scope list
\endif
\if :{?numbers}
\else
  \set numbers ''
\endif

BEGIN;

-- Resolve the target set once. zammad_ticket_id IS NOT NULL guarantees we
-- only ever touch imported tickets, never natively-created ones.
-- Match the LOCAL ticket number (#N in the UI). nullif guards the empty-list
-- case so the int[] cast never sees an empty string.
CREATE TEMP TABLE target_tickets ON COMMIT DROP AS
SELECT id, number, zammad_ticket_id, subject
FROM tickets
WHERE zammad_ticket_id IS NOT NULL
  AND ( :'scope' = 'all'
        OR number = ANY (string_to_array(nullif(:'numbers', ''), ',')::int[]) );

\echo '--- Tickets matched for reset (scope=' :scope ', apply=' :apply ') ---'
SELECT number AS ticket_no, zammad_ticket_id AS zammad_id, left(subject, 60) AS subject
FROM target_tickets
ORDER BY number;
SELECT count(*) AS total_matched FROM target_tickets;

\if :apply
  -- mail_messages.ticket_id is ON DELETE SET NULL; its unique message_id
  -- would otherwise linger and collide on re-import. Remove the anchors
  -- first (mail_recipients cascade from this).
  DELETE FROM mail_messages m USING target_tickets t WHERE m.ticket_id = t.id;

  -- ticket_bodies / ticket_events / event attachments / sla state all
  -- cascade from the ticket delete.
  DELETE FROM tickets t USING target_tickets tt WHERE t.id = tt.id;

  COMMIT;
  \echo '>>> DELETED. Now re-run the Zammad ticket import to recreate them.'
\else
  ROLLBACK;
  \echo '>>> DRY RUN — nothing deleted. Add  -v apply=yes  to perform the delete.'
\endif
