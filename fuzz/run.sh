#!/usr/bin/env bash
# Fuzz hedeflerini sırayla çalıştırır (docs/fuzzing.md). Python'da Backend/requirements.lock ve
# .github/requirements/fuzz.lock kurulu olmalı (atheris; CPython 3.12+).
#
#   fuzz/run.sh                 her hedef 60 saniye
#   fuzz/run.sh 300 agent_ws    yalnızca agent_ws, 5 dakika
#
# Yeni bulunan girdiler geçici bir klasöre yazılır (depodaki tohumlar değişmez). Bir hedef hata bulursa girdisi
# FUZZ_ARTIFACTS klasörüne (varsayılan: geçici klasör, yolu yazılır) "<hedef>-crash-..." adıyla kaydedilir ve betik
# 1 ile çıkar; yeniden üretmek için: python fuzz/fuzz_<hedef>.py <dosya>
set -euo pipefail

cd "$(dirname "$0")/.."
PY=${PYTHON:-python}
DURATION=${1:-60}
shift || true
targets=("$@")
if [ ${#targets[@]} -eq 0 ]; then
    targets=(agent_ws request_models release_manifest notify_settings)
fi
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
artifacts=${FUZZ_ARTIFACTS:-$(mktemp -d)}
mkdir -p "$artifacts"

status=0
for t in "${targets[@]}"; do
    seeds=("fuzz/corpus/$t")
    if [ "$t" = agent_ws ]; then
        seeds+=(docs/protocol/examples/agent-to-server)
    fi
    mkdir -p "$work/$t"
    echo "== $t ($DURATION s)"
    if ! "$PY" "fuzz/fuzz_$t.py" -max_total_time="$DURATION" -timeout=25 -dict=fuzz/json.dict \
            -print_final_stats=1 -artifact_prefix="$artifacts/$t-" "$work/$t" "${seeds[@]}"; then
        echo "fuzz: $t hata buldu; girdi: $artifacts/$t-*" >&2
        status=1
    fi
done
exit $status
