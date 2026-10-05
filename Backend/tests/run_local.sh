#!/usr/bin/env bash
# POps entegrasyon testlerini yerelde çalıştırır (CI'daki 'security' job'ının eşi): test betikleri
# CI ile aynı sırada, geçici olarak başlatılan sunucuya karşı. COVERAGE=1 ile sunucu coverage altında
# çalışır ve sonunda kapsam raporu basılır (coverage paketi gerekir; canlı venv'e kurmayın).
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
export METRICS_TOKEN="${METRICS_TOKEN:-local-metrics-token-0123456789}"
# test_demo.py: salt okunur demo hesabı (sunucu ve test aynı değeri okur)
export POPS_DEMO_USERS="${POPS_DEMO_USERS:-ci_demo}"
# test_sso.py: şifresiz LDAP'ın test bayrağı yalnızca bununla kabul edilir (CI ile aynı; üretimde tanımlanmaz)
export POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1

if [ "${COVERAGE:-0}" = "1" ]; then
  rm -f .coverage .coverage.*
  python -m coverage run --rcfile=.coveragerc tests/test_units.py
else
  python tests/test_units.py
fi
# Protokol testi jsonschema ister (yalnızca test bağımlılığı; CI kurar)
if python -c "import jsonschema" 2>/dev/null; then
  if [ "${COVERAGE:-0}" = "1" ]; then
    python -m coverage run --rcfile=.coveragerc tests/test_protocol.py
  else
    python tests/test_protocol.py
  fi
else
  echo "test_protocol.py atlandı: pip install jsonschema==4.26.0"
fi
python migrate.py
if [ "${COVERAGE:-0}" = "1" ]; then
  python -m coverage run --rcfile=.coveragerc -m uvicorn server:app --host 127.0.0.1 --port 8099 >/tmp/pops-test-uvicorn.log 2>&1 &
else
  python -m uvicorn server:app --host 127.0.0.1 --port 8099 >/tmp/pops-test-uvicorn.log 2>&1 &
fi
UP=$!
trap 'kill "$UP" 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do curl -sf "$POPS_TEST_HTTP/api/health" >/dev/null 2>&1 && break; sleep 1; done
python tests/test_security.py
python tests/test_2fa.py
python tests/test_agent_authz.py
python tests/test_remote_authz.py
python tests/test_device_keys.py
python tests/test_hardening.py
python tests/test_p1.py
python tests/test_review4.py
python tests/test_modules.py
python tests/test_jobs.py
python tests/test_f4_accountability.py
python tests/test_features.py
python tests/test_helpdesk_licenses.py
python tests/test_ops.py
python tests/test_api_tokens.py
python tests/test_demo.py
# LDAP bölümü Docker ister (osixia/openldap kabı); yoksa atlanır. POPS_TEST_SKIP_DOCKER=1 ile de atlanır.
python tests/test_sso.py
if [ "${COVERAGE:-0}" = "1" ]; then
  kill -TERM "$UP"; wait "$UP" 2>/dev/null || true
  python -m coverage combine --rcfile=.coveragerc >/dev/null
  python -m coverage report --rcfile=.coveragerc
fi
