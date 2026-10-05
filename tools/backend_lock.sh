#!/usr/bin/env bash
# Backend/requirements.lock: Backend/requirements.txt'in bütün bağımlılıkları (dolaylılar dahil) sabit sürüm ve
# SHA-256 özetleriyle; Python 3.10+ ve her platform için tek dosya (uv --universal). install.sh, pops-deploy-backend,
# Docker imajı ve CI "pip install --require-hashes -r requirements.lock" ile kurar: PyPI'deki bir dosya değişirse
# kurulum durur. Doğrudan bağımlılıkların sürümü requirements.txt'te seçilir (Dependabot onu günceller); bu betik
# kilidi ondan üretir.
#
#   tools/backend_lock.sh            requirements.txt değişince kilidi günceller; kilitteki öteki sürümler korunur
#   tools/backend_lock.sh --upgrade  dolaylı bağımlılıkları da izin verilen en yeni sürüme çeker (bunları Dependabot
#                                    görmez; pip-audit bir açık bildirirse ya da arada bir çalıştırın)
#   tools/backend_lock.sh --check    kilit requirements.txt'le uyumlu mu (CI); değilse farkı yazar, 1 ile çıkar
#
# uv gerekir, CI ile aynı sürüm: pip install uv==0.12.23 (başka bir sürüm dosyayı farklı biçimde yazabilir).
# Kaynak dağıtımı derlemez (--no-build): çözümleme sırasında paketlerden kod çalışmaz.
set -euo pipefail

UV_VERSION=0.12.23
cd "$(dirname "$0")/.."
UV=${UV:-uv}
command -v "$UV" >/dev/null 2>&1 || { echo "backend_lock: uv bulunamadı (pip install uv==$UV_VERSION)" >&2; exit 2; }
have=$("$UV" --version | awk '{print $2}')
if [ "$have" != "$UV_VERSION" ]; then
    echo "backend_lock: UYARI: uv $have; CI uv $UV_VERSION kullanır, çıktı farklı biçimde olabilir" >&2
fi

compile() {   # $1=çıktı dosyası, sonra ek seçenekler. Var olan çıktı dosyasındaki sürümler tercih edilir.
    local out=$1
    shift
    "$UV" pip compile Backend/requirements.txt --universal --python-version 3.10 --generate-hashes --no-build \
        --custom-compile-command "tools/backend_lock.sh" --quiet --output-file "$out" "$@"
}

case "${1:-}" in
    "") compile Backend/requirements.lock ;;
    --upgrade) compile Backend/requirements.lock --upgrade ;;
    --check)
        tmp=$(mktemp -d)
        trap 'rm -rf "$tmp"' EXIT
        if [ -f Backend/requirements.lock ]; then cp Backend/requirements.lock "$tmp/requirements.lock"; fi
        compile "$tmp/requirements.lock"
        if ! diff -u Backend/requirements.lock "$tmp/requirements.lock"; then
            echo "backend_lock: Backend/requirements.lock, Backend/requirements.txt ile uyumlu değil." >&2
            echo "Yeniden üretin ve commit'leyin:  pip install uv==$UV_VERSION && tools/backend_lock.sh" >&2
            exit 1
        fi
        echo "backend_lock: Backend/requirements.lock güncel."
        ;;
    *) echo "kullanım: tools/backend_lock.sh [--check | --upgrade]" >&2; exit 2 ;;
esac
