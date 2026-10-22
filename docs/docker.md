# Running the POps Server with Docker (optional)

The native install ([`installation.md`](installation.md)) is the primary, supported path.
Docker Compose is an alternative for hosts that already run containers. It starts three
containers:

| Service     | What it runs                                                                                                               | Volumes                          |
|-------------|----------------------------------------------------------------------------------------------------------------------------|----------------------------------|
| `db`        | PostgreSQL (`postgres:17`). No published port.                                                                             | `pgdata`                         |
| `backend`   | FastAPI/uvicorn on port 8000 as a non-root user (`ghcr.io/pashacore/pops-backend`). Applies the migrations on every start. | `storage`, `updates`, `releases`, `transfers` |
| `dashboard` | PHP panel on Apache (`ghcr.io/pashacore/pops-dashboard`). Proxies `/api`, `/ws`, `/updates` and `/download` to the backend. | none                             |

Every release publishes the backend and dashboard images on GitHub Container Registry, so
nothing has to be built: Compose pulls them (see [Images](#images)). They are built from
`docker/backend.Dockerfile` and `docker/dashboard.Dockerfile`, and you can
[build them from source](#building-from-source) instead.

Only the dashboard publishes a port: plain HTTP on `127.0.0.1:8080` by default. TLS is
not included; put a reverse proxy in front (see [TLS and agents](#tls-and-agents)).

## Quick start

```bash
git clone https://github.com/PashaCore/POps.git
cd POps
cp .env.example .env
# Fill in at least: JWT_SECRET, BYPASS_SECRET, DB_USER, DB_PASS, DB_NAME, PANEL_ADMIN_PASS
#   secrets:  openssl rand -hex 32
docker compose up -d                      # pulls the published images, no build
docker compose ps                         # all three should become "healthy"
curl http://127.0.0.1:8080/api/health     # {"status":"ok","database":true,...}
```

Then open `http://127.0.0.1:8080/login.php` (or your TLS address) and sign in with
`PANEL_ADMIN_USER` / `PANEL_ADMIN_PASS`. The admin account is created on the first start
only if it does not exist yet; changing these values later has no effect (change the
password in the panel).

Values in `.env` are read by Compose, so avoid `$` in them or wrap the value in single quotes.

## Images

| Image                              | Built from                    |
|------------------------------------|-------------------------------|
| `ghcr.io/pashacore/pops-backend`   | `docker/backend.Dockerfile`   |
| `ghcr.io/pashacore/pops-dashboard` | `docker/dashboard.Dockerfile` |

The release tag `v0.1.22-alpha` pushes both images as `0.1.22-alpha` (the version without
the `v`) and moves `latest` to it; every release does the same. The images are `linux/amd64`, carry the OCI labels
`org.opencontainers.image.source`, `.version`, `.revision` and `.licenses` (Apache-2.0), and
come with a build provenance attestation and an SBOM:

```bash
docker buildx imagetools inspect ghcr.io/pashacore/pops-backend:latest --format '{{ json .Provenance }}'
docker buildx imagetools inspect ghcr.io/pashacore/pops-backend:latest --format '{{ json .SBOM }}'
```

### Pinning a version

`docker-compose.yml` uses the tag in `POPS_IMAGE_TAG`, `latest` when it is not set. To stay on
one release, set it in `.env` and apply it:

```bash
echo 'POPS_IMAGE_TAG=0.1.22-alpha' >> .env
docker compose up -d
```

Both images always use the same tag. Moving forward is safe (the backend applies the new
migrations on start); moving back to an older tag is not, because the database keeps the
newer schema: restore a backup taken before the upgrade instead. Do not use `POPS_VERSION`
for this: that variable overrides the version the backend reports and should stay unset.

### Building from source

```bash
docker compose up -d --build              # or: docker compose build && docker compose up -d
```

builds both images from the checkout (build context: the repository root, filtered by the
`.dockerignore` allowlist) instead of pulling them, for local changes or for a host that is
not amd64. The result gets the image names from `docker-compose.yml` (with the tag in
`POPS_IMAGE_TAG`), so later `docker compose up -d` keeps using the local build;
`docker compose pull` replaces it with the published images. Compose also builds by itself
when an image cannot be pulled (no network, a tag that does not exist, or a host that is not
amd64), so keep the whole checkout or the `pops-server-<version>.tar.gz` release package,
not only `docker-compose.yml`.

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
| `POPS_IMAGE_TAG`       | `latest`          | Tag of both published images, for example `0.1.22-alpha`; see [Pinning a version](#pinning-a-version).     |
| `POPS_HTTP_PORT`       | `8080`            | Host port of the dashboard.                                                                                |
| `POPS_HTTP_BIND`       | `127.0.0.1`       | Host address the port binds to. Keep loopback unless the TLS proxy runs on another machine.               |
| `TZ`                   | `Europe/Istanbul` | Time zone of the backend and database. Must match the agents: timestamps and the daily offline bypass code use local time. |
| `POPS_TRUSTED_PROXIES` | container gateway | Who may set `X-Forwarded-For`; see [Client addresses](#client-addresses).                                   |

## TLS and agents

Agents refuse a plain `http://` server address unless it is loopback, so production needs
`https://` / `wss://` in front of the dashboard port. Any TLS reverse proxy works if it:

- forwards WebSocket upgrades on `/ws/` (agents and the panel keep long-lived connections),
- preserves the `Host` header and sets `X-Forwarded-For` and `X-Forwarded-Proto: https`,
- allows large request bodies only on `/api/upload`, `/api/system/upload-release` (deployment packages
  and signed releases), `/api/files/push` and `/api/files/<id>/upload` (file transfer, at most 200 MB) and keeps
  the limit small elsewhere.

Example for nginx on the Docker host (add the certificate, e.g. with certbot):

```nginx
server {
    listen 443 ssl;
    server_name pops.example.com;
    ssl_certificate     /etc/letsencrypt/live/pops.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/pops.example.com/privkey.pem;
    client_max_body_size 8m;

    location ~ ^/api/(v1/)?(upload|files|files/push|files/[A-Za-z0-9_-]+/upload|system/upload-release)$ {
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
- **Scaling the backend.** Without Redis it must run as one container with one process, because connected
  agents and panels are tracked in memory: do not use `--scale backend=N`. With a Redis service and `REDIS_URL`
  set for the backend, several containers work (see [`ha.md`](ha.md)); `storage`, `updates`, `releases` and
  `transfers` must then be shared volumes.

## Upgrades

Back up first (below), then, with the published images (set the new `POPS_IMAGE_TAG` in
`.env` first if you pinned one):

```bash
git pull                                       # changes to docker-compose.yml, if any (or unpack the new release package)
docker compose pull
docker compose up -d
docker compose logs backend | grep uygulandi   # migrations applied on this start, if any
```

When building from source:

```bash
git pull
docker compose up -d --build
```

Migrations run automatically when the backend starts. Published images are rebuilt for every
release. The Dockerfiles pin their base images (`python:3.12-slim`, `php:8.3-apache`) by digest,
and Dependabot moves each digest to the newest build of the same tag every week, so base image
security updates arrive with each release, and with `git pull` for source builds.

Installs set up before the images were published built them locally (`pops-backend`,
`pops-dashboard`). `docker compose up -d --build` keeps doing that; `docker compose pull`
followed by `docker compose up -d` switches to the published images, after which the old
local images can be removed with `docker image rm pops-backend pops-dashboard`.

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
- `docker compose up` reports a failed pull (`denied`, `manifest unknown`, `no matching
  manifest`) and then builds: the image or tag is not published (yet), or the host is not
  amd64. The local build works the same way; to use the published images, check
  `POPS_IMAGE_TAG` and run `docker compose pull`.

## Maintainer notes

- `.github/workflows/release.yml` (job `images`) builds and pushes both images on a `v*` tag,
  after the GitHub Release has been published, so only when the CI tests, the
  tag/VERSION/CHANGELOG check and the `release` environment approval have passed. When the
  workflow changes on `main` it only builds them, without pushing.
- **New GHCR packages are private.** After the first release that pushes them, make both
  packages public once: GitHub → PashaCore → **Packages** → `pops-backend` →
  **Package settings** → **Danger Zone** → **Change visibility** → **Public**, then the same
  for `pops-dashboard`. If **Public** is not offered, allow public packages in the organisation
  settings (**Packages** → package creation). Until then pulls fail with `denied` and Compose
  builds from source.
