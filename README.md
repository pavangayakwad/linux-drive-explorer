# Linux File Explorer

A browser-based file manager for a Linux host, styled after Windows Explorer's Details
view. ASP.NET Core 10 API (running as root, with full filesystem access) + SQLite +
Angular/PrimeNG frontend.

## Features

- Details-view file listing with sortable columns, Windows-style date grouping
  (Today / Yesterday / Last week / …), multi-select, keyboard navigation
  (arrows, Shift+arrows, Ctrl+A, Enter, Backspace, F2, Delete, Ctrl+C/X/V), and a
  right-click context menu.
- Copy / move / delete run as durable background jobs (they keep running even if you
  close the browser), with live progress on the **Tasks** page over SignalR.
- Deletes move items to a per-drive hidden Trash instead of deleting immediately;
  restore or permanently purge from the **Trash** page.
- Left-hand drive list with free-space bars; the sidebar is resizable and shared
  across Explorer/Tasks/Trash.
- Properties dialog: size/type/dates, plus a Permissions tab to view/change
  owner, group and rwx bits (chmod/chown) - only functional when the API runs on Linux.
- File preview (images, text, PDF, audio, video) and recursive filename search.
- JWT-based login with a change-password flow.

## Architecture

```
backend/FileExplorer.Api/   ASP.NET Core 10 Web API, EF Core + SQLite, SignalR
frontend/                   Angular (standalone + signals) + PrimeNG, axios for HTTP
docker-compose.yml           two containers: api (Kestrel) + web (nginx serving the
                             built Angular app and reverse-proxying /api and /hubs)
```

The API treats one configured physical directory as its "root" (`FileSystem:RootPath`,
default `/host_root`) - every path the client sees is relative to that. In Docker, the
host's real `/` is bind-mounted there, so the API can reach anything the host can.

**This API is designed to run as root** so it can browse, chmod and chown anywhere on
the host. Treat it accordingly: keep it off the public internet, put it behind a
trusted network/VPN, and always set a strong `JWT_KEY` and admin password (see below).

## Local development (without Docker)

Requires the .NET 10 SDK and Node 22+.

```bash
# Terminal 1 - API (listens on http://localhost:5266, per Properties/launchSettings.json)
cd backend/FileExplorer.Api
dotnet run

# Terminal 2 - Angular dev server (proxies /api and /hubs to the API - see proxy.conf.json)
cd frontend
npm install
npm start
```

Open http://localhost:4200. In development, `FileSystem:RootPath` points at
`backend/.devroot` (a small sandbox folder, not your real filesystem) and the admin
login is `admin` / `admin123` - see `appsettings.Development.json`. This sandboxing is
intentional: local dev never touches your real files, and drives won't show up in the
sidebar unless `.devroot` happens to contain mount points of its own.

## Running with Docker (production)

1. Copy `.env.example` to `.env` and fill in `JWT_KEY` (and optionally
   `ADMIN_USERNAME`/`ADMIN_PASSWORD` - if you leave the password blank, a random one is
   generated and printed once to the logs on first startup).

   ```bash
   cp .env.example .env
   openssl rand -base64 48   # paste the result into JWT_KEY
   ```

2. Start it:

   ```bash
   docker compose up -d --build
   ```

3. Open `http://<host>:8080`. Check the generated admin password if you didn't set one:

   ```bash
   docker compose logs api | grep -A2 "Password:"
   ```

### Volume mounts - adjust for your host

The default `docker-compose.yml` bind-mounts the entire host `/` into the `api`
container at `/host_root` with `rslave` propagation, so every mount already on the
host (including anything under `/mnt`) is browsable, and drives mounted *after* the
container starts also appear automatically. This is the simplest setup and matches
"browse everything."

If you'd rather expose only specific locations, replace that mount with a narrower
list, e.g.:

```yaml
volumes:
  - fx-data:/data
  - /home:/host_root/home
  - /mnt:/host_root/mnt
```

Whatever you mount, it must appear under `/host_root` inside the container (or update
`FileSystem__RootPath` to match a different mount point).

### Troubleshooting: "port is not available" on Windows (Docker Desktop)

Windows periodically reserves ranges of TCP ports for Hyper-V/WSL, which can make port
8080 (or others) fail to bind with a permissions-looking error even though nothing else
is using it. Check with:

```powershell
netsh interface ipv4 show excludedportrange protocol=tcp
```

If 8080 falls in a listed range, either restart Windows (ranges are reassigned on
reboot) or just map `web` to a different, free host port in `docker-compose.yml`
(e.g. `"9500:80"`).

### HTTPS

The `web` container serves plain HTTP on port 8080. For anything beyond a trusted LAN,
put a TLS-terminating reverse proxy (Caddy, Traefik, or nginx with a cert) in front of
it rather than exposing 8080 directly.

## Project layout reference

- `backend/FileExplorer.Api/Controllers` - REST endpoints (auth, files, drives,
  operations, tasks, trash, permissions, search)
- `backend/FileExplorer.Api/Services/Jobs` - background job queue + worker that
  executes copy/move/delete/purge
- `frontend/src/app/features` - `shell` (sidebar/layout), `explorer`, `tasks`, `trash`,
  `login`
- `frontend/src/app/core` - axios client, auth/token handling, typed API services
