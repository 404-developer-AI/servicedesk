# Servicedesk

A premium, self-hosted servicedesk for teams who actually like opening their ticket queue.

Built for speed, designed in glass, and shipped as a single install on your own box.

> **Status:** v0.0.1 — foundations only. Not production-ready. Not even pretty yet.

---

## What this is

A modern internal servicedesk app:

- **Fast** at 10K and at 1M tickets.
- **Premium** dark glassmorphism UI, not the usual bootstrap-and-spinning-wheel energy.
- **Self-hosted**, one install per customer, on a single Ubuntu box.
- **Secure** by default — production app, exposed to the internet, sensitive data.

## Stack

- ASP.NET Core 8 · React 18 · TypeScript · Vite · Tailwind CSS
- PostgreSQL · EF Core · Dapper for hot paths
- Hosted with Docker Compose behind Nginx, TLS via Let's Encrypt

## Roadmap (very) short version

- ✅ **v0.0.1** — repo foundations, version & time endpoints, dev DB
- 🟦 **v0.0.2** — design system + glass app shell
- 🟦 **v0.0.4** — auth (local + Microsoft 365)
- 🟦 **v0.0.5** — tickets
- 🟦 **v0.1.0** — first real install on a real customer

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
