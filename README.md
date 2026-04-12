# Servicedesk

A self-hosted servicedesk app. Built because the existing options were either ugly, slow, expensive, or all three.

> **Status:** v0.0.6 — ticket list, detail (editable subject + description), create, saved views, live agent presence. You can manage tickets now.

## What this is

A small project to build a servicedesk that's actually nice to use. Dark mode, glass, no clutter. Runs on your own box. That's the whole pitch.

## Stack

- ASP.NET Core 8 · React 18 · TypeScript · Vite · Tailwind CSS
- PostgreSQL · EF Core · Dapper for hot paths
- Docker Compose behind Nginx, TLS via Let's Encrypt

## Local development

```bash
# 1. Install PostgreSQL natively, then bootstrap the dev DB:
psql -U postgres -f db/init/000_dev_setup.sql

# 2. Copy env example and fill in your local password:
cp .env.example .env

# 3. Run the backend:
dotnet run --project src/Servicedesk.Api

# 4. In a second terminal, run the frontend:
cd src/Servicedesk.Web
npm install
npm run dev
```

The Vite dev server proxies `/api/*` to the .NET backend on `localhost:5080`.

## License

TBD.
