# Security Policy

This project is provided free of charge and **as is**, with no warranty and no
liability — see the [LICENSE](LICENSE) for the full terms. You run it at your own
risk. Nothing in this document creates any obligation, service-level commitment,
or guarantee on the part of the authors or contributors.

That said, responsible disclosure is welcome and appreciated.

## Reporting a vulnerability

**Please report vulnerabilities privately.** Do not open a public issue, pull
request, or discussion for anything security-sensitive — that would expose the
problem before any fix could exist.

Use GitHub's private vulnerability reporting:

1. Open the **Security** tab of this repository.
2. Click **Report a vulnerability**.
3. Describe the issue in the advisory form.

A useful report includes the affected version or commit, steps to reproduce or a
proof of concept, the impact you believe it has, and any suggested fix.

## What happens next

Reports are reviewed on a **best-effort basis**, in the maintainers' own time.
There is no guaranteed response time, no commitment to investigate or fix any
particular report, and no guaranteed disclosure timeline. If a fix is made, it
will target the latest release only — older releases receive nothing. Please do
not rely on a fix being produced.

## Scope

This is a self-hosted application: one install per deployment, on infrastructure
the operator controls. The operator is solely responsible for their deployment —
host hardening, database exposure, OS credentials, TLS, backups, updates, and the
security of the data they process. Reports that suggest safer defaults in the
application code or deployment scripts are welcome, but securing and operating any
given installation is entirely the operator's responsibility.
