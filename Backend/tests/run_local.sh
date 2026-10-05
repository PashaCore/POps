#!/usr/bin/env bash
# POps entegrasyon testlerini yerelde çalıştırır (CI'daki 'security' job'ının eşi): önce sunucusuz testler,
# sonra test betikleri CI ile aynı sırada, geçici olarak başlatılan sunucuya karşı (pytest; bkz. tests/conftest.py).
# COVERAGE=1 ile sunucu coverage altında çalışır ve sonunda kapsam raporu basılır. pytest ve coverage gerekir;
# canlı venv'e kurmayın:
#   pip install --require-hashes -r ../.github/requirements/pytest.lock && pip install coverage==7.10.7
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
export GLPI_ALLOW_PRIVATE=1   # test_glpi.py'nin sahte GLPI sunucusu da 127.0.0.1'de
export CORS_ALLOWED_ORIGINS=""
export METRICS_TOKEN="${METRICS_TOKEN:-local-metrics-token-0123456789}"
# test_demo.py: salt okunur demo hesabı (sunucu ve test aynı değeri okur)
export POPS_DEMO_USERS="${POPS_DEMO_USERS:-ci_demo}"
# test_peer_cache.py: tohumun süresi kısa (sunucu ve test aynı değeri okur)
export PEER_CACHE_SEED_TIMEOUT_SECONDS="${PEER_CACHE_SEED_TIMEOUT_SECONDS:-4}"
# test_sso.py: şifresiz LDAP'ın test bayrağı yalnızca bununla kabul edilir (CI ile aynı; üretimde tanımlanmaz)
export POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1

# Sunucusuz testler. Protokol testi jsonschema ister (yalnızca test bağımlılığı; CI kurar)
# test_ha.py ayrı veritabanı işi yapar ve kendi süreçlerini açar: sonda, yalnızca POPS_TEST_REDIS verildiyse
skip=(--deselect tests/test_ha.py::test_ha)
if ! python -c "import jsonschema" 2>/dev/null; then
  echo "test_protocol.py atlandı: pip install jsonschema==4.26.0"
  skip+=(--deselect tests/test_protocol.py::test_protocol)
fi
if [ "${COVERAGE:-0}" = "1" ]; then
  rm -f .coverage .coverage.*
  POPS_TEST_COVERAGE=1 python -m coverage run --rcfile=.coveragerc -m pytest -q -m "not integration" ${skip[@]+"${skip[@]}"} tests
else
  python -m pytest -q -m "not integration" ${skip[@]+"${skip[@]}"} tests
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
# Sunucu isteyen betikler; sonuç sunucu kapatıldıktan sonra bildirilir (kapsam raporu yine basılır)
rc=0
python -m pytest -v -m integration tests || rc=$?
# Birden fazla backend süreci (docs/ha.md): yalnızca bir Redis verildiyse; kendi iki sürecini 9997/9998'de açar
if [ -n "${POPS_TEST_REDIS:-}" ]; then
  python -m pytest -v tests/test_ha.py || rc=$?
fi
if [ "${COVERAGE:-0}" = "1" ]; then
  kill -TERM "$UP"; wait "$UP" 2>/dev/null || true
  python -m coverage combine --rcfile=.coveragerc >/dev/null
  python -m coverage report --rcfile=.coveragerc
fi
exit "$rc"
