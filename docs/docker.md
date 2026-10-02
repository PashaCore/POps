# Running the POps Server with Docker (optional)

The native install ([`installation.md`](installation.md)) is the primary, supported path.
Docker Compose is an alternative for hosts that already run containers. It starts three
containers:

| Service     | What it runs                                                                                                               | Volumes                          |
|-------------|----------------------------------------------------------------------------------------------------------------------------|----------------------------------|
| `db`        | PostgreSQL (`postgres:17`). No published port.                                                                             | `pgdata`                         |
| `backend`   | FastAPI/uvicorn on port 8000 as a non-root user (`docker/backend.Dockerfile`). Applies the migrations on every start.      | `storage`, `updates`, `releases` |
| `dashboard` | PHP panel on Apache (`docker/dashboard.Dockerfile`). Proxies `/api`, `/ws`, `/updates` and `/download` to the backend.     | none                             |

Only the dashboard publishes a port: plain HTTP on `127.0.0.1:8080` by default. TLS is
not included; put a reverse proxy in front (see [TLS and agents](#tls-and-agents)).

## Quick start

```bash
git clone https://github.com/PashaCore/POps.git
cd POps
cp .env.example .env
# Fill in at least: JWT_SECRET, BYPASS_SECRET, DB_USER, DB_PASS, DB_NAME, PANEL_ADMIN_PASS
#   secrets:  openssl rand -hex 32
docker compose up -d --build
docker compose ps                         # all three should become "healthy"
curl http://127.0.0.1:8080/api/health     # {"status":"ok","database":true,...}
```

Then open `http://127.0.0.1:8080/login.php` (or your TLS address) and sign in with
`PANEL_ADMIN_USER` / `PANEL_ADMIN_PASS`. The admin account is created on the first start
only if it does not exist yet; changing these values later has no effect (change the
password in the panel).

Values in `.env` are read by Compose, so avoid `$` in them or wrap the value in single quotes.

## Configuration

The backend reads `.env` exactly like a native install (see [`configuration.md`](configuration.md)),
with these differences:

- `DB_HOST` / `DB_PORT` are ignored: the backend always uses the `db` service. `DB_USER`,
  `DB_PASS` and `DB_NAME` are also used to initialise the database on the first start;
  changing them afterwards does not change the existing database.
- `POPS_API_INTERNAL_URL` is ignored: the panel always talks to `http://backend:8000`.
- The dashboard does not receive `.env`; it only needs the variables set in `docker-compose.yml`.

Variables used only by `docker-compose.yml` (put them in `.env`):

| Variable               | Default           | Meaning                                                                                                    |
|------------------------|-------------------|------------------------------------------------------------------------------------------------------------|
| `POPS_HTTP_PORT`       | `8080`            | Host port of the dashboard.                                                                                |
| `POPS_HTTP_BIND`       | `127.0.0.1`       | Host address the port binds to. Keep loopback unless the TLS proxy runs on another machine.               |
| `TZ`                   | `Europe/Istanbul` | Time zone of the backend and database. Must match the agents: timestamps and the daily offline bypass code use local time. |
| `POPS_TRUSTED_PROXIES` | container gateway | Who may set `X-Forwarded-For`; see [Client addresses](#client-addresses).                                   |

## TLS and agents

Agents refuse a plain `http://` server address unless it is loopback, so production needs
`https://` / `wss://` in front of the dashboard port. Any TLS reverse proxy works if it:

- forwards WebSocket upgrades on `/ws/` (agents and the panel keep long-lived connections),
- preserves the `Host` header and sets `X-Forwarded-For` and `X-Forwarded-Proto: https`,
- allows large request bodies only on `/api/upload` and `/api/system/upload-release` (deployment packages
  and signed releases) and keeps the limit small elsewhere.

Example for nginx on the Docker host (add the certificate, e.g. with certbot):

```nginx
server {
    listen 443 ssl;
    server_name pops.example.com;
    ssl_certificate     /etc/letsencrypt/live/pops.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/pops.example.com/privkey.pem;
    client_max_body_size 8m;

    location ~ ^/api/(upload|system/upload-release)$ {
        client_max_body_size 600m;
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 300s;
    }
    location /ws/ {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 3600s;
    }
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
    }
}
server { listen 80; server_name pops.example.com; return 301 https://$host$request_uri; }
```

Agents are installed as usual and pointed at the TLS address, e.g. the agent MSI with
`SERVER_URL=https://pops.example.com` and `ENROLL_TOKEN=...` (see [`agent.md`](agent.md)).

## Client addresses

The backend uses the client address for the login rate limit and for the agent IP it
records. The dashboard accepts `X-Forwarded-For` only from `POPS_TRUSTED_PROXIES` and passes a single,
clean value to the backend. When it is empty (the default), only the container's gateway is
trusted: that is where connections from a proxy on the Docker host to `127.0.0.1:8080`
come from, so the example above needs no setting.

If the proxy connects some other way (from another machine, or as a container on another
network), set `POPS_TRUSTED_PROXIES` to its address as the dashboard sees it (several
addresses or CIDR ranges, space separated) and run `docker compose up -d`. To check, look at
`docker compose logs dashboard`: the first field of each access-log line is the address the
panel and backend use. Do not publish the backend's port 8000: it trusts forwarded
addresses because only the dashboard can reach it.

## Not supported in Docker

- **Server self-update from the panel.** It relies on a systemd path unit on the host
  ([`self-update.md`](self-update.md)); in Docker the **Sistem** page reports it as not
  installed. Upgrade as described below. Agent updates (upload or fetch a signed release, then
  dispatch) work normally; the files are kept in the `updates` and `releases` volumes.
- **Wake-on-LAN broadcast from the server.** The backend's own broadcast
  (`WOL_BROADCAST_ADDR`) generally does not reach the school LAN from the Docker bridge
  network. For devices assigned to a lab, wake requests are also relayed through an online
  agent in that lab, which is unaffected. If you depend on the server's broadcast, use the native install.
- **TLS.** Not included; see above.
- **Scaling the backend.** It must run as one container with one process, because connected
  agents and panels are tracked in memory. Do not use `--scale backend=N`.

## Upgrades

Back up first (below), then:

```bash
git pull
docker compose up -d --build
docker compose logs backend | grep uygulandi   # migrations applied on this start, if any
```

Migrations run automatically when the backend starts. Add `--pull` to the build
(`docker compose build --pull && docker compose up -d`) to pick up base image security updates.

The PostgreSQL major version is fixed by the `pgdata` volume: do not change the `postgres:17`
tag on an existing volume. To move to a new major version, dump the database, remove the
volume, change the tag, start again and restore the dump.

## Backups

Run from the directory that contains `docker-compose.yml`:

```bash
# database
docker compose exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > pops-db-$(date +%F).dump
# uploaded files, agent update packages and verified releases
docker compose exec -T backend tar czf - -C /app storage updates releases > pops-files-$(date +%F).tgz
```

Also keep a private copy of `.env`: `BYPASS_SECRET` must match the agents and the database
credentials must match the `pgdata` volume.

Restore:

```bash
docker compose exec -T db sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner' < pops-db-YYYY-MM-DD.dump
docker compose exec -T backend tar xzf - -C /app < pops-files-YYYY-MM-DD.tgz
docker compose restart backend
```

The same restore moves a native install into Docker: dump the native database with
`pg_dump -Fc`, archive `storage`, `updates` and `releases` from the app directory, copy
`BYPASS_SECRET` (and `JWT_SECRET`) into the new `.env`, then restore as above. Agents keep
working if the server address stays the same.

## Troubleshooting

- `docker compose ps` shows health; `docker compose logs -f backend` shows startup,
  migrations and errors.
- `/api/health` reports `"database": false`: the backend stops retrying the database about
  15 seconds after it starts. Once the `db` container is healthy, run `docker compose restart backend`.
- No admin account: `PANEL_ADMIN_PASS` was empty on the first start. Set it in `.env` and run
  `docker compose up -d` (the account is created only if it does not exist).
