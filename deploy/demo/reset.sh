#!/usr/bin/env bash
# Reset the POps public demo: wipe everything (database and fleet state), start it again, migrate, seed, then start
# the fake fleet. Run nightly by pops-demo-reset.timer; safe to run by hand at any time (same result every time).
#   POPS_DEMO_PROJECT   Compose project name (default pops-demo)
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
PROJECT="${POPS_DEMO_PROJECT:-pops-demo}"
[ -f .env.demo ] || { echo "reset: .env.demo is missing (setup-host.sh creates it)" >&2; exit 1; }
dc() { docker compose -p "$PROJECT" --env-file .env.demo -f docker-compose.demo.yml "$@"; }

echo "== $(date '+%F %T') resetting $PROJECT"
# Containers, database volume and fleet state go; images stay
dc down -v --remove-orphans
# Waits until the database, backend and panel are healthy (the backend applies the migrations as it starts)
dc up -d --wait --wait-timeout 300 db backend dashboard
# Migrations once more on their own: a no-op when the backend already applied them
dc run --rm -T seed python migrate.py
# Demo account, fleet enrollment token and two weeks of history
dc run --rm -T seed
# The fake school: enrolls with the token and keeps running
dc up -d fleet
echo "== $(date '+%F %T') ready on http://127.0.0.1:$(sed -n 's/^POPS_DEMO_PORT=//p' .env.demo | tail -1)/"
