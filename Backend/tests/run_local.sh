#!/usr/bin/env bash
# POps güvenlik entegrasyon testini yerelde çalıştırır (CI'daki 'security' job'ının eşi).
#
# Önce şunları export edin — DB_NAME BOŞ bir test veritabanı olmalı (migrate.py şemayı kurar):
#   export DB_HOST=127.0.0.1 DB_PORT=5432 DB_USER=<u> DB_PASS=<p> DB_NAME=pops_sec_test JWT_SECRET=dev
# Boş DB'yi önce siz oluşturun (ör. createdb pops_sec_test). Sunucu 8099'da geçici başlatılır.
set -euo pipefail
cd "$(dirname "$0")/.."   # Backend/
export POPS_TEST_HTTP="${POPS_TEST_HTTP:-http://127.0.0.1:8099}"
: "${DB_USER:?DB_USER export edin}" "${DB_PASS:?DB_PASS export edin}" "${DB_NAME:?DB_NAME (bos test DB) export edin}" "${JWT_SECRET:?JWT_SECRET export edin}"

python migrate.py
python -m uvicorn server:app --host 127.0.0.1 --port 8099 >/tmp/pops-test-uvicorn.log 2>&1 &
UP=$!
trap 'kill "$UP" 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do curl -sf "$POPS_TEST_HTTP/api/health" >/dev/null 2>&1 && break; sleep 1; done
python tests/test_security.py
python tests/test_2fa.py
