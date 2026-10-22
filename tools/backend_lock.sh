#!/usr/bin/env bash
# Backend/requirements.lock: Backend/requirements.txt'in bütün bağımlılıkları (dolaylılar dahil) sabit sürüm ve
# SHA-256 özetleriyle; Python 3.10+ ve her platform için tek dosya (uv --universal). install.sh, pops-deploy-backend,
# Docker imajı ve CI "pip install --require-hashes -r requirements.lock" ile kurar: PyPI'deki bir dosya değişirse
# kurulum durur. Doğrudan bağımlılıkların sürümü requirements.txt'te seçilir (Dependabot onu günceller); bu betik
# kilidi ondan üretir.
#
# CI'ın kendi araçları da aynı yolla kilitlenir: .github/requirements/<ad>.txt (sürümler, Dependabot günceller) ->
# <ad>.lock (özetli). İş akışları yalnızca "pip install --require-hashes -r <kilit>" ile kurar. Backend kilidiyle aynı
# ortama kurulan araçların ortak paketleri Backend kilidindeki sürümde tutulur (--constraint).
#
#   tools/backend_lock.sh            girdiler değişince kilitleri günceller; kilitlerdeki öteki sürümler korunur
#   tools/backend_lock.sh --upgrade  dolaylı bağımlılıkları da izin verilen en yeni sürüme çeker (bunları Dependabot
#                                    görmez; pip-audit bir açık bildirirse ya da arada bir çalıştırın)
#   tools/backend_lock.sh --check    kilitler girdileriyle uyumlu mu (CI); değilse farkı yazar, 1 ile çıkar
#
# uv gerekir, CI ile aynı sürüm: pip install --require-hashes -r .github/requirements/lock-tools.lock (başka bir sürüm
# dosyaları farklı biçimde yazabilir). Kaynak dağıtımı derlemez (--no-build): çözümleme sırasında paketlerden kod
# çalışmaz.
set -euo pipefail

cd "$(dirname "$0")/.."
REQ=.github/requirements
UV_VERSION=$(sed -n 's/^uv==\([^ ;]*\).*/\1/p' "$REQ/lock-tools.txt")
UV=${UV:-uv}
command -v "$UV" >/dev/null 2>&1 || {
    echo "backend_lock: uv bulunamadı (pip install --require-hashes -r $REQ/lock-tools.lock)" >&2
    exit 2
}
have=$("$UV" --version | awk '{print $2}')
if [ "$have" != "$UV_VERSION" ]; then
    echo "backend_lock: UYARI: uv $have; CI uv $UV_VERSION kullanır, çıktı farklı biçimde olabilir" >&2
fi

# CI araçlarının kilitleri:  <ad>  <en düşük Python>  <Backend kilidiyle aynı ortama kurulur mu>
# backend-ci ve pytest CI'da 3.10 ve 3.12'de kurulur; atheris'in tekerlekleri 3.12+ içindir (fuzz işi 3.12).
CI_LOCKS=(
    "backend-ci 3.10 yes"
    "pytest 3.10 yes"
    "signing 3.10 no"
    "lock-tools 3.10 no"
    "fuzz 3.12 yes"
)

compile() {   # $1=girdi, $2=çıktı dosyası, $3=en düşük Python, sonra ek seçenekler. Var olan çıktıdaki sürümler tercih edilir.
    local in=$1 out=$2 py=$3
    shift 3
    "$UV" pip compile "$in" --universal --python-version "$py" --generate-hashes --no-build \
        --custom-compile-command "tools/backend_lock.sh" --quiet --output-file "$out" "$@"
}

compile_all() {   # $1=çıktı klasörü (kilitlerin yerinde güncellenmesi için "."), sonra ek seçenekler
    local dir=$1 name py shared
    shift
    compile Backend/requirements.txt "$dir/Backend/requirements.lock" 3.10 "$@"
    for spec in "${CI_LOCKS[@]}"; do
        read -r name py shared <<<"$spec"
        if [ "$shared" = yes ]; then
            # Kısıt her zaman depodaki Backend kilidi (yol kilidin "# via" satırlarına yazılır). --check'te Backend
            # kilidi eskiyse zaten fark çıkar.
            compile "$REQ/$name.txt" "$dir/$REQ/$name.lock" "$py" --constraint Backend/requirements.lock "$@"
        else
            compile "$REQ/$name.txt" "$dir/$REQ/$name.lock" "$py" "$@"
        fi
    done
}

locks() {
    echo Backend/requirements.lock
    for spec in "${CI_LOCKS[@]}"; do echo "$REQ/${spec%% *}.lock"; done
}

case "${1:-}" in
    "") compile_all . ;;
    --upgrade) compile_all . --upgrade ;;
    --check)
        tmp=$(mktemp -d)
        trap 'rm -rf "$tmp"' EXIT
        mkdir -p "$tmp/Backend" "$tmp/$REQ"
        while read -r lock; do
            if [ -f "$lock" ]; then cp "$lock" "$tmp/$lock"; fi
        done < <(locks)
        compile_all "$tmp"
        stale=0
        while read -r lock; do
            if ! diff -u "$lock" "$tmp/$lock"; then
                echo "backend_lock: $lock girdisiyle uyumlu değil." >&2
                stale=1
            fi
        done < <(locks)
        if [ "$stale" != 0 ]; then
            echo "Yeniden üretin ve commit'leyin:  pip install --require-hashes -r $REQ/lock-tools.lock && tools/backend_lock.sh" >&2
            exit 1
        fi
        echo "backend_lock: kilitler güncel ($(locks | wc -l) dosya)."
        ;;
    *) echo "kullanım: tools/backend_lock.sh [--check | --upgrade]" >&2; exit 2 ;;
esac
