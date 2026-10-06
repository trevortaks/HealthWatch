# HealthWatch

Self-contained monitor for web applications and databases. One process = scheduler + web dashboard + alerting. State is kept in an embedded SQLite file (`data/healthwatch.db`), so there are no other dependencies.

## Features
- **Applications**: GET a URL, pass if the status is in the accepted list (default `200-399`), with timeout and optional TLS-error bypass.
- **Databases**: open a connection and run a heartbeat query (default `SELECT 1`). SQL Server, PostgreSQL, MySQL/MariaDB (and SQLite).
- Per-target interval, timeout and "alert after N consecutive failures".
- **Alerts**: one DOWN message per incident and one RECOVERED message, via SMTP email, and SMS / WhatsApp through the Gikko API (configurable URL, auth header and JSON body templates with `{to}` / `{message}`).
- **Reports**: uptime %, avg/p95 latency, incidents and downtime for 24h/7d/30d/90d, CSV download, print view, optional daily email summary.
- Connection strings and passwords are encrypted at rest (ASP.NET Data Protection; DPAPI-protected key ring on Windows).

## Run
```bash
dotnet run --project src/HealthWatch
```
Open http://localhost:5080 and sign in with the `Admin:Password` from `appsettings.json` (default `changeme` if blank - change it!).
Then: Settings (SMTP/Gikko) -> Contacts -> Targets.

Config can also come from environment variables, e.g. `Admin__Password`, `Urls`, `DataDir`.

## Install as a service
```bash
dotnet publish src/HealthWatch -c Release -r linux-x64 --self-contained -o publish   # or win-x64
```
- **Windows**: copy `publish` + `deploy/install-service.ps1` to the server, run the script as Administrator.
- **Linux**: copy `publish` to `/opt/healthwatch`, `deploy/healthwatch.service` to `/etc/systemd/system/`, then `systemctl enable --now healthwatch`. Create the `healthwatch` user and `/var/lib/healthwatch` first. Note: on Linux the Data Protection key ring is stored unencrypted in `DataDir/keys` - restrict its permissions (`chmod 700`).

## Test
```bash
dotnet test
```
