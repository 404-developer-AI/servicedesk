# Timesheet migrator

One-time tool that copies historical time registrations from the legacy
**MSSQL `TimeSheet`** database into a target Servicedesk install, through the
secret-gated migration import surface (`/api/timesheet/import`).

It is **not** part of the application: it carries an MSSQL client dependency
that has no place in the production container, and it only ever runs from an
operator workstation that can reach **both** the legacy SQL server and the
target Servicedesk (dev or prod). It is excluded from `Servicedesk.slnx` and
the Dockerfile on purpose.

## What it does

- Reads `dbo.task_model`, `dbo.employee_model` and `dbo.time_slot_model`
  (read-only — it never writes to the source).
- Lets you map source tasks → target tasks and source employees → target
  users in a reviewable `mapping.json`.
- Converts each slot's `start_time` / `end_time` into the target's
  single-day model (entry date + minutes since midnight).
- Sends rows in batches. The server resolves each Zammad ticket-number to a
  ticket (empty link when there's no match, by design) and upserts on
  `(import_source, import_ref)` so re-runs never duplicate.

## Prerequisites

1. On the target Servicedesk: **Settings → Timesheet → Migration import** →
   enable the toggle and set an import token.
2. Network access from this machine to the MSSQL server and to the target
   Servicedesk URL.

## Configuration

Pass on the command line or via environment variables:

| Option | Env var | Meaning |
|---|---|---|
| `--source` | `TS_SOURCE_SQL` | MSSQL connection string |
| `--target` | `TS_TARGET_URL` | Servicedesk base URL (dev or prod) |
| `--token`  | `TS_TARGET_TOKEN` | the import token from the settings panel |

Example source connection string:

```
Server=172.16.10.93,1433;Database=TimeSheet;User Id=read_user;Password=...;TrustServerCertificate=True;Encrypt=True
```

Keep secrets out of git — prefer environment variables. `mapping.json` and
`.env` are already git-ignored in this folder.

## Usage

```powershell
# 1. Generate a best-guess mapping file
dotnet run --project tools/TimesheetMigrator -- map

# 2. Open mapping.json and fill any unmatched targetTaskId / targetUserId

# 3. Dry run against DEV first — transforms everything, sends nothing
dotnet run --project tools/TimesheetMigrator -- import --dry-run

# 4. Real import against DEV, verify in the app
dotnet run --project tools/TimesheetMigrator -- import

# 5. Repeat 3–4 against PROD with the SAME mapping.json (change --target/--token)
```

Re-running `import` is safe: rows upsert on their source id, so a second pass
updates in place instead of duplicating.

## What gets skipped (and reported)

- **unmapped task / employee** — the source value has no target in
  `mapping.json`. Fill it and re-run.
- **crosses midnight** — a slot whose end is on a later date than its start;
  it can't be expressed in the single-day model.
- **bad time window** — end not after start, or out of the 0–24h range.
- **server side** — e.g. a mapped user/task id that doesn't exist on the
  target (foreign-key violation).

All four are counted in the final summary; nothing is dropped silently.
