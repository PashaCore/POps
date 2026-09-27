#!/usr/bin/env bash
# POps entegrasyon testlerini yerelde çalıştırır (CI'daki 'security' job'ının eşi): yedi test betiği,
# CI ile aynı sırada, geçici olarak başlatılan sunucuya karşı.
#
# Önce şunları export edin — DB_NAME BOŞ bir test veritabanı olmalı (migrate.py şemayı kurar):
#   export DB_HOST=127.0.0.1 DB_PORT=5432 DB_USER=<u> DB_PASS=<p> DB_NAME=pops_sec_test JWT_SECRET=dev
# Boş DB'yi önce siz oluşturun (ör. createdb pops_sec_test). Sunucu 8099'da geçici başlatılır.
set -euo pipefail
cd "$(dirname "$0")/.."   # Backend/
export POPS_TEST_HTTP="${POPS_TEST_HTTP:-http://127.0.0.1:8099}"
: "${DB_USER:?DB_USER export edin}" "${DB_PASS:?DB_PASS export edin}" "${DB_NAME:?DB_NAME (bos test DB) export edin}" "${JWT_SECRET:?JWT_SECRET export edin}"
# CI ile aynı: test_features.py kendi webhook alıcısını 127.0.0.1'de açar, bu yüzden sunucu iç adrese
# webhook göndermeye izin vermeli. Boş CORS listesi .env'deki değeri ezer.
export NOTIFY_WEBHOOK_ALLOW_PRIVATE=1
export CORS_ALLOWED_ORIGINS=""

python migrate.py
python -m uvicorn server:app --host 127.0.0.1 --port 8099 >/tmp/pops-test-uvicorn.log 2>&1 &
UP=$!
trap 'kill "$UP" 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do curl -sf "$POPS_TEST_HTTP/api/health" >/dev/null 2>&1 && break; sleep 1; done
python tests/test_security.py
python tests/test_2fa.py
python tests/test_agent_authz.py
python tests/test_remote_authz.py
python tests/test_f4_accountability.py
python tests/test_features.py
python tests/test_helpdesk_licenses.py
