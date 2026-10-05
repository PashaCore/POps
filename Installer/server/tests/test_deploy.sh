#!/usr/bin/env bash
# pops-deploy-backend ve pops-selfupdate davranış testleri (CI: "Server scripts").
# Koşullar check'e bilerek tek tırnakla verilir ve eval ile değerlendirilir: değişkenler o anda açılır (SC2016);
# yalnızca bu koşullarda okunan değişkenler shellcheck'e kullanılmıyor görünür (SC2034).
# shellcheck disable=SC2016,SC2034
# Root GEREKMEZ ve root olarak ÇALIŞMAZ: her yol geçici bir dizindedir; systemctl, curl, sudo, journalctl,
# sleep ve install PATH'in önüne konan taklitlerdir (install gerçek install'a geçer, istenince hata verir);
# pip sahte venv'deki bir betik, Python yorumlayıcıları (venv'inki ve "sunucudaki" python3.X'ler) sürümünü
# söyleyen ve sahte venv kuran bir betiktir. Sistemde hiçbir şeye dokunulmaz.
#     bash Installer/server/tests/test_deploy.sh
# KEEP_TMP=1: geçici dizin silinmez (yolu yazılır). POPS_TEST_TMP: geçici dizinin açılacağı yer.
set -euo pipefail
umask 022

SRV=$(cd "$(dirname "$0")/.." && pwd)        # Installer/server
DEPLOY_SH="$SRV/pops-deploy-backend"
SELFUPDATE_SH="$SRV/pops-selfupdate"
if [ "$(id -u)" -eq 0 ]; then echo "Bu test root olarak çalıştırılmaz (yanlışlıkla gerçek sisteme dokunmasın)."; exit 1; fi
for c in git ssh-keygen tar sha256sum; do command -v "$c" >/dev/null || { echo "$c gerekli"; exit 1; }; done

T=$(mktemp -d "${POPS_TEST_TMP:-${TMPDIR:-/tmp}}/pops-deploy-test.XXXXXX")
set -u
# Taklitler (systemctl, pg_dump, ...) yalnızca bu geçici dizine yazılır. T boş ya da /tmp dışında olursa
# "$T/bin/pg_dump" gerçek /bin/pg_dump olur: dur. Betiğin parçaları elle çalıştırılmaz; yalnızca bütünü.
case "$T" in
    /tmp/?*|"${TMPDIR:-/tmp}"/?*|"${POPS_TEST_TMP:-/tmp}"/?*) ;;
    *) echo "güvensiz geçici dizin: T=$T"; exit 1 ;;
esac
if [ "${KEEP_TMP:-0}" = 1 ]; then echo "geçici dizin: $T"; else trap 'rm -rf "${T:?}"' EXIT; fi
ME=$(id -un)
REAL_INSTALL=$(command -v install)
export PT="$T" PT_INSTALL="$REAL_INSTALL" HOME="$T/home" GIT_CONFIG_NOSYSTEM=1
export GIT_AUTHOR_NAME=POps GIT_AUTHOR_EMAIL=ci@pops.test GIT_COMMITTER_NAME=POps GIT_COMMITTER_EMAIL=ci@pops.test
export POPS_DEPLOY_ALLOW_NONROOT=1 POPS_SELFUPDATE_ALLOW_NONROOT=1
mkdir -p "$HOME" "${T:?}/bin" "${T:?}/ctl" "${T:?}/calls" "${T:?}/etc" "${T:?}/pybin"
[ -d "${T:?}/bin" ] || exit 1

PASS=0; FAIL=0
check() {   # $1=açıklama $2=bash koşulu
    if eval "$2"; then PASS=$((PASS + 1)); echo "  ok    $1"; else FAIL=$((FAIL + 1)); echo "  HATA  $1"; fi
}
has() { grep -qF -- "$1" "$2"; }                  # $1=metin $2=dosya
count() { grep -cF -- "$1" "$2" || true; }

# --- Taklitler ------------------------------------------------------------------------------------------
cat > "${T:?}/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$PT/calls/systemctl"
case "$1" in
    show) if [ -e "$PT/ctl/unit_missing" ]; then echo not-found; else echo loaded; fi ;;
    is-active) [ ! -e "$PT/ctl/inactive" ] ;;
    restart) [ ! -e "$PT/ctl/restart_fail" ] ;;
esac
EOF
cat > "${T:?}/bin/curl" <<'EOF'
#!/usr/bin/env bash
url=${!#}
echo "$url" >> "$PT/calls/curl"
case "$url" in
    */api/health) if [ -e "$PT/ctl/health_fail" ]; then printf 500; else printf 200; fi ;;
    */api/agent_policies) printf 200 ;;
    */api/devices) printf 401 ;;
    *) printf 000; exit 7 ;;
esac
EOF
cat > "${T:?}/bin/sudo" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$PT/calls/sudo"
while [ $# -gt 0 ]; do case "$1" in -u) shift 2 ;; -*) shift ;; *) break ;; esac; done
exec "$@"
EOF
cat > "${T:?}/bin/journalctl" <<'EOF'
#!/usr/bin/env bash
echo "(journalctl taklidi) $*"
EOF
printf '#!/usr/bin/env bash\nexit 0\n' > "${T:?}/bin/sleep"
# psql: schema_migrations sorgusuna ctl/applied'deki sürümler, boyut sorgusuna 2048 (KB); ctl/psql_fail: bağlanamadı
cat > "${T:?}/bin/psql" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$PT/calls/psql"
if [ -e "$PT/ctl/psql_fail" ]; then echo "psql: taklit bağlantı hatası" >&2; exit 2; fi
case "$*" in
    *schema_migrations*) cat "$PT/ctl/applied" 2>/dev/null || true ;;
    *pg_database_size*) echo 2048 ;;
esac
EOF
# pg_dump: -f dosyasını yazar; servis o anda yeniden başlatılmış mı, onu da kaydeder (yedek restart'tan ÖNCE olmalı)
cat > "${T:?}/bin/pg_dump" <<'EOF'
#!/usr/bin/env bash
out=""; prev=""
for a in "$@"; do if [ "$prev" = -f ]; then out=$a; fi; prev=$a; done
if grep -q restart "$PT/calls/systemctl" 2>/dev/null; then when=after; else when=before; fi
echo "$when PGPASSWORD=${PGPASSWORD:-} $*" >> "$PT/calls/pg_dump"
if [ -e "$PT/ctl/pg_dump_fail" ]; then echo "pg_dump: taklit hata" >&2; exit 1; fi
echo "dump" > "$out"
EOF
cat > "${T:?}/bin/install" <<'EOF'
#!/usr/bin/env bash
# ctl/install_fail içindeki desene uyan hedefte hata verir; gerisi gerçek install
if [ -e "$PT/ctl/install_fail" ]; then
    # shellcheck disable=SC2254  # desen dosyadan gelir, glob olarak eşleşmesi istenir
    case "${!#}" in $(cat "$PT/ctl/install_fail")) echo "install: taklit hata: ${!#}" >&2; exit 1 ;; esac
fi
exec "$PT_INSTALL" "$@"
EOF
# pip: venv'i gerçekten değiştirir (paket ekler, dosya değiştirir, siler); ctl/pip_fail varsa yarıda keser.
# --require-hashes ile gerçek pip gibi davranır: -r dosyasındaki her paket == ile sabit olmalı ve "PyPI"deki dosyanın
# özeti (burada sha256("ad==sürüm")) o paketin --hash'leri arasında olmalı; değilse venv'e dokunmadan durur.
cat > "${T:?}/pip-stub" <<'EOF'
#!/usr/bin/env bash
V=$(cd "$(dirname "$0")/.." && pwd)
sps=("$V"/lib/python*/site-packages); SP=${sps[0]}
echo "$V: $*" >> "$PT/calls/pip"
req=""; hashes=0; prev=""
for a in "$@"; do
    if [ "$prev" = -r ]; then req=$a; fi
    if [ "$a" = --require-hashes ]; then hashes=1; fi
    prev=$a
done
if [ "$hashes" = 1 ]; then
    # Yorumlar atılır, "\" ile süren satırlar birleştirilir: "ad==sürüm [; işaret] --hash=sha256:... ..."
    while read -r spec rest; do
        [ -n "$spec" ] || continue
        case "$spec" in *==*) ;; *) echo "ERROR: hash modunda her paket == ile sabitlenmeli: $spec" >&2; exit 1 ;; esac
        want=$(printf '%s' "$spec" | sha256sum | cut -d' ' -f1)
        case " $rest " in
            *" --hash=sha256:$want "*) ;;
            *) echo "ERROR: THESE PACKAGES DO NOT MATCH THE HASHES FROM THE REQUIREMENTS FILE ($spec)" >&2; exit 1 ;;
        esac
    done < <(sed -e 's/#.*//' "$req" | sed -e ':a' -e '/\\$/N; s/\\\n/ /; ta')
fi
mkdir -p "$SP/newpkg" "$SP/oldpkg"; echo "new" > "$SP/newpkg/__init__.py"
echo "2.0" > "$SP/oldpkg/version.txt"
if [ -e "$PT/ctl/pip_fail" ]; then echo "ERROR: taklit pip hatası" >&2; exit 1; fi
rm -f "$SP/gone.txt"
printf '#!%s/bin/python3\n' "$V" > "$V/bin/newtool"; chmod 755 "$V/bin/newtool"
[ -e "$V/bin/uvicorn" ] || { printf '#!%s/bin/python3\nimport uvicorn\n' "$V" > "$V/bin/uvicorn"; chmod 755 "$V/bin/uvicorn"; }
EOF
# Python: sürüm venv'in içindeyse pyvenv.cfg'den, değilse adından (python3.12 -> 3.12.0). "-m venv [--upgrade-deps]
# DİZİN" sahte bir venv kurar (ctl/venv_fail: hata); diğer çağrılar (deploy'un "-I -S -c ..." sorusu) "X.Y" yazar.
cat > "${T:?}/fakepy" <<'EOF'
#!/usr/bin/env bash
cfg="$(dirname "$0")/../pyvenv.cfg"
if [ -f "$cfg" ]; then full=$(sed -n 's/^version = //p' "$cfg"); else n=${0##*/}; full="${n#python}.0"; fi
if [ "${1:-}" = -m ] && [ "${2:-}" = venv ]; then
    shift 2; [ "${1:-}" != --upgrade-deps ] || shift
    echo "$full $1" >> "$PT/calls/venv"
    if [ -e "$PT/ctl/venv_fail" ]; then echo "Error: taklit venv hatası" >&2; exit 1; fi
    mkdir -p "$1/bin" "$1/lib/python${full%.*}/site-packages"
    printf 'home = %s\nversion = %s\n' "$(dirname "$0")" "$full" > "$1/pyvenv.cfg"
    cp "$PT/fakepy" "$1/bin/python3"; ln -s python3 "$1/bin/python"; cp "$PT/pip-stub" "$1/bin/pip"
    exit 0
fi
echo "${full%.*}"
EOF
# selfupdate'in çağırdığı deploy (yalnızca çağrıldığını ve o anki HEAD'i kaydeder)
cat > "${T:?}/deploy-stub" <<'EOF'
#!/usr/bin/env bash
git -C "$PT/surepo" rev-parse HEAD >> "$PT/calls/deploy"
EOF
chmod 755 "${T:?}/bin/"* "${T:?}/pip-stub" "${T:?}/deploy-stub" "${T:?}/fakepy"
for v in 3.9 3.10 3.12 3.13; do cp "${T:?}/fakepy" "${T:?}/pybin/python$v"; done
export PATH="$T/bin:$PATH"
# Deploy'un yeni venv için aradığı yorumlayıcılar (sistemdekiler değil); durumlar kendi listesini verir
export POPS_DEPLOY_PYTHONS="$T/pybin/python3.12"

# --- Yardımcılar ----------------------------------------------------------------------------------------
write_v() {   # $1=çalışma kopyası $2=sürüm: POps deposunun deploy'un okuduğu kısmı
    local d=$1 v=$2
    mkdir -p "$d/Backend/pops/routers" "$d/Backend/migrations" "$d/Backend/tests" "$d/keys"
    echo "$v" > "$d/VERSION"
    echo "acik-anahtar" > "$d/keys/pops_release_ed25519.pub.pem"
    echo "# server $v" > "$d/Backend/server.py"
    echo "# migrate" > "$d/Backend/migrate.py"
    : > "$d/Backend/pops/__init__.py"
    echo "# a $v" > "$d/Backend/pops/routers/a.py"
    echo "CREATE TABLE t ();" > "$d/Backend/migrations/0001_init.sql"
    [ -e "$d/Backend/requirements.txt" ] || echo "fastapi==1" > "$d/Backend/requirements.txt"
    echo "# test" > "$d/Backend/tests/test_x.py"
}
commit() { git -C "$1" add -A && git -C "$1" commit -qm "$2"; }
write_lock() {   # $1=çalışma kopyası [$2=good|bad] [$3=dolaylı paket, ad==sürüm]: tools/backend_lock.sh biçiminde kilit
    local d=$1 mode=${2:-good} spec h
    {
        echo "# This file was autogenerated by uv via the following command:"
        echo "#    tools/backend_lock.sh"
        for spec in $(grep -v '^#' "$d/Backend/requirements.txt") ${3:-}; do
            h=$(printf '%s' "$spec" | sha256sum | cut -d' ' -f1)
            if [ "$mode" = bad ]; then h=$(printf 'kurcalanmis %s' "$spec" | sha256sum | cut -d' ' -f1); fi
            printf '%s \\\n    --hash=sha256:%s\n    # via -r Backend/requirements.txt\n' "$spec" "$h"
        done
    } > "$d/Backend/requirements.lock"
}
make_venv() {   # $1=APP [$2=Python sürümü, 3.12.4]: konsol betiklerinin #! satırı venv'in mutlak yolunu içerir
    local ver=${2:-3.12.4}
    local v="$1/venv" sp="$1/venv/lib/python${ver%.*}/site-packages"
    mkdir -p "$v/bin" "$sp/oldpkg"
    ln -s lib "$v/lib64"; cp "$T/fakepy" "$v/bin/python3"; ln -s python3 "$v/bin/python"
    printf 'home = /usr/bin\nversion = %s\n' "$ver" > "$v/pyvenv.cfg"
    printf '#!%s/bin/python3\nimport uvicorn\n' "$v" > "$v/bin/uvicorn"; chmod 755 "$v/bin/uvicorn"
    echo "1.0" > "$sp/oldpkg/version.txt"; echo "old" > "$sp/oldpkg/__init__.py"; echo "silinecek" > "$sp/gone.txt"
    cp "$T/pip-stub" "$v/bin/pip"
}
install_like() {   # $1=repo $2=APP [$3=venv Python sürümü]: install.sh gibi Backend'in kopyası + VERSION + anahtar + venv
    mkdir -p "$2"; cp -a "$1/Backend/." "$2/"; cp -a "$1/keys" "$2/keys"; cp "$1/VERSION" "$2/VERSION"; make_venv "$2" "${3:-}"
}
snap() {   # $1=dizin: dosya listesi (tür, izin, yol, bağ hedefi) + içerik özetleri; .deploy-backups hariç
    ( cd "$1" && find . -path ./.deploy-backups -prune -o -printf '%y %m %p %l\n' | LC_ALL=C sort
      find . -path ./.deploy-backups -prune -o -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum )
}
reset_calls() { for f in systemctl curl sudo pip deploy venv psql pg_dump; do : > "$T/calls/$f"; done; }
run_deploy() {   # [$1=ayar dosyası]; çıktı $T/out, dönüş kodu RC
    reset_calls; RC=0
    POPS_DEPLOY_CONF="${1:-$T/etc/deploy.conf}" bash "$DEPLOY_SH" > "$T/out" 2>&1 || RC=$?
}
show_out() { sed 's/^/        | /' "$T/out"; }

# =========================================================================================================
echo "== pops-deploy-backend"
R="$T/repo"; A="$T/app"
git init -q -b main "$R"; write_v "$R" 0.0.1; commit "$R" v0.0.1
install_like "$R" "$A"
cat > "${T:?}/etc/deploy.conf" <<EOF
REPO=$R
APP=$A
SVC=popstest
OWNER=$ME
HEALTH_BASE=http://127.0.0.1:18123
KEEP_BACKUPS=3
EOF
chmod 644 "$T/etc/deploy.conf"

echo "-- (a) normal deploy"
write_v "$R" 0.0.2; echo "# b" > "$R/Backend/pops/routers/b.py"; commit "$R" v0.0.2
run_deploy
check "çıkış 0 ve 'canlıda'" '[ "$RC" = 0 ] && has canlıda "$T/out"'
check "kod kopyalandı (server.py, yeni modül, VERSION 0.0.2)" \
    'cmp -s "$R/Backend/server.py" "$A/server.py" && [ -f "$A/pops/routers/b.py" ] && [ "$(cat "$A/VERSION")" = 0.0.2 ]'
check "testler dağıtılmadı (Backend/tests)" '[ ! -e "$A/tests/test_x.py" ] || cmp -s "$A/tests/test_x.py" "$R/Backend/tests/test_x.py"'
check "requirements aynı: pip çağrılmadı, venv görüntüsü yok" \
    '[ ! -s "$T/calls/pip" ] && [ -z "$(find "$A/.deploy-backups" -name "venv-*")" ]'
check "(d) SVC ayardan: 'restart popstest'" 'has "restart popstest" "$T/calls/systemctl"'
check "(d) HEALTH_BASE ayardan: 127.0.0.1:18123" 'has "http://127.0.0.1:18123/api/health" "$T/calls/curl"'
run_deploy
check "ikinci çalıştırma: 'zaten', restart yok" '[ "$RC" = 0 ] && has zaten "$T/out" && ! has restart "$T/calls/systemctl"'

# Sonraki sürüm: requirements değişir, üst seviyeye yeni .py, yeni migration, bir modül silinir
echo "fastapi==2" > "$R/Backend/requirements.txt"; write_v "$R" 0.0.3
echo "# extra" > "$R/Backend/extra.py"; echo "ALTER TABLE t ADD c int;" > "$R/Backend/migrations/0002_c.sql"
git -C "$R" rm -q Backend/pops/routers/b.py; commit "$R" v0.0.3

echo "-- (b) sağlık kontrolü başarısız: kod VE venv birebir geri"
BEFORE=$(snap "$A"); touch "$T/ctl/health_fail"
run_deploy; rm -f "$T/ctl/health_fail"
check "çıkış 1, 'Sağlık kontrolü başarısız, önceki kod seti geri yükleniyor'" \
    '[ "$RC" != 0 ] && has "Sağlık kontrolü başarısız, önceki kod seti geri yükleniyor" "$T/out"'
check "pip çalıştı (venv gerçekten değişti) ve venv geri yüklendi" \
    '[ -s "$T/calls/pip" ] && has "venv de geri yükleniyor" "$T/out"'
check "(d) pip OWNER olarak çalıştı" 'has "-H -u $ME" "$T/calls/sudo"'
check "APP birebir aynı (dosya listesi + izinler + bağlar + SHA-256)" '[ "$BEFORE" = "$(snap "$A")" ]'
check "konsol betiğinin #! satırı aynı mutlak yolda" '[ "$(head -1 "$A/venv/bin/uvicorn")" = "#!$A/venv/bin/python3" ]'
check "venv görüntüsü kod yedeğinin yanında" '[ -n "$(find "$A/.deploy-backups" -name "venv-*.tgz")" ]'
check "iki restart (deploy + geri dönüş)" '[ "$(count "restart popstest" "$T/calls/systemctl")" = 2 ]'
[ "$BEFORE" = "$(snap "$A")" ] || { show_out; diff <(echo "$BEFORE") <(snap "$A") | head -20 || true; }

echo "-- (c) pip hatası: hiçbir şey değişmemiş kalır"
BEFORE=$(snap "$A"); touch "$T/ctl/pip_fail"
run_deploy; rm -f "$T/ctl/pip_fail"
check "çıkış 1, 'pip install başarısız'" '[ "$RC" != 0 ] && has "pip install başarısız" "$T/out"'
check "APP birebir aynı (pip'in yarım bıraktığı venv dahil)" '[ "$BEFORE" = "$(snap "$A")" ]'
check "sağlık kontrolüne gelinmedi" '[ ! -s "$T/calls/curl" ]'
[ "$BEFORE" = "$(snap "$A")" ] || show_out

echo "-- (c) venv'de OWNER'a ait olmayan dosya: pip'e gelinmeden durur"
OTHER=""
for u in nobody daemon bin; do
    if [ "$u" != "$ME" ] && id -u "$u" >/dev/null 2>&1; then OTHER=$u; break; fi
done
if [ -n "$OTHER" ]; then
    # Root olmadan başka kullanıcıya ait dosya yaratılamaz; bunun yerine OWNER başka bir kullanıcı: venv'deki her dosya
    # "OWNER'a ait değil" görünür
    sed "s/^OWNER=.*/OWNER=$OTHER/" "$T/etc/deploy.conf" > "$T/etc/deploy-foreign.conf"; chmod 644 "$T/etc/deploy-foreign.conf"
    BEFORE=$(snap "$A")
    run_deploy "$T/etc/deploy-foreign.conf"
    check "çıkış 1, sebep ve chown önerisi" \
        '[ "$RC" != 0 ] && has "kullanıcısına ait olmayan" "$T/out" && has "chown -R $OTHER:" "$T/out"'
    check "pip çağrılmadı, restart yok" '[ ! -s "$T/calls/pip" ] && ! has restart "$T/calls/systemctl"'
    check "APP birebir aynı" '[ "$BEFORE" = "$(snap "$A")" ]'
    check "yedek bırakılmadı" '[ -z "$(find "$A/.deploy-backups" -newer "$T/etc/deploy-foreign.conf" -name "code-*")" ]'
    [ "$BEFORE" = "$(snap "$A")" ] || show_out
else
    echo "  atlandı: başka bir sistem kullanıcısı yok"
fi

echo "-- (c) kod kopyalanırken hata (set -e yolu): yine tek geri dönüş"
BEFORE=$(snap "$A"); echo '*/pops/routers/a.py' > "$T/ctl/install_fail"
run_deploy; rm -f "$T/ctl/install_fail"
check "çıkış 1, 'Hata (satır N), önceki kod seti geri yükleniyor'" \
    '[ "$RC" != 0 ] && grep -qE "Hata \(satır [0-9]+\), önceki kod seti geri yükleniyor" "$T/out"'
check "APP birebir aynı" '[ "$BEFORE" = "$(snap "$A")" ]'
[ "$BEFORE" = "$(snap "$A")" ] || show_out

echo "-- (d) ayar dosyası"
check "KEEP_BACKUPS=3: en çok 3 kod yedeği" '[ "$(find "$A/.deploy-backups" -name "code-*.tgz" | wc -l)" -le 3 ]'
orphan=0
for v in "$A/.deploy-backups"/venv-*.tgz; do [ -e "$A/.deploy-backups/code-${v##*/venv-}" ] || orphan=1; done
check "her venv görüntüsünün kod yedeği duruyor (birlikte silinir)" '[ "$orphan" = 0 ]'
BEFORE=$(snap "$A")
for mode in 666 664 646; do
    cp "$T/etc/deploy.conf" "$T/etc/bad.conf"; chmod "$mode" "$T/etc/bad.conf"
    run_deploy "$T/etc/bad.conf"
    check "izin $mode olan ayar reddedildi, systemctl'e bile gidilmedi" \
        '[ "$RC" != 0 ] && has "güvenli değil" "$T/out" && [ ! -s "$T/calls/systemctl" ]'
done
mkdir -p "$T/etc-gw"; chmod 777 "$T/etc-gw"; cp "$T/etc/deploy.conf" "$T/etc-gw/deploy.conf"
run_deploy "$T/etc-gw/deploy.conf"
check "herkesin yazabildiği dizindeki ayar reddedildi" '[ "$RC" != 0 ] && has "güvenli değil" "$T/out"'
ln -sf "$T/etc/deploy.conf" "$T/etc/link.conf"
run_deploy "$T/etc/link.conf"
check "sembolik bağ olan ayar reddedildi" '[ "$RC" != 0 ] && has "güvenli değil" "$T/out"'
sed "s#^APP=.*#APP=$T/yok#" "$T/etc/deploy.conf" > "$T/etc/noapp.conf"
run_deploy "$T/etc/noapp.conf"
check "APP yoksa erken dur" '[ "$RC" != 0 ] && has "APP bulunamadı: " "$T/out" && has "$T/yok" "$T/out" && [ ! -s "$T/calls/systemctl" ]'
sed "s#^REPO=.*#REPO=$T/yok#" "$T/etc/deploy.conf" > "$T/etc/norepo.conf"
run_deploy "$T/etc/norepo.conf"
check "REPO yoksa erken dur" '[ "$RC" != 0 ] && has "REPO bulunamadı: " "$T/out" && has "$T/yok" "$T/out" && [ ! -s "$T/calls/systemctl" ]'
touch "$T/ctl/unit_missing"; run_deploy; rm -f "$T/ctl/unit_missing"
check "systemd birimi yoksa erken dur" '[ "$RC" != 0 ] && has "systemd birimi bulunamadı" "$T/out"'
if [ ! -e /opt/pops ]; then
    run_deploy "$T/etc/yok.conf"
    check "ayar dosyası yoksa install.sh varsayılanları (burada /opt/pops yok: erken dur)" \
        '[ "$RC" != 0 ] && has bulunamadı "$T/out" && [ ! -s "$T/calls/systemctl" ]'
fi
check "reddedilen çalıştırmaların hiçbiri APP'i değiştirmedi" '[ "$BEFORE" = "$(snap "$A")" ]'

echo "-- (a) requirements değişikliğiyle başarılı deploy"
run_deploy
check "çıkış 0, venv görüntüsü raporlandı" '[ "$RC" = 0 ] && has "(venv:" "$T/out"'
check "kilit yok (eski checkout): pip requirements.txt'ten, özet denetimi olmadan" \
    'has "-r $A/requirements.txt" "$T/calls/pip" && ! has "--require-hashes" "$T/calls/pip" && [ ! -e "$A/requirements.lock" ]'
check "yeni kod ve migration geldi, silinen modül gitti" \
    '[ -f "$A/extra.py" ] && [ -f "$A/migrations/0002_c.sql" ] && [ ! -e "$A/pops/routers/b.py" ]'
check "pip'in değişikliği kaldı" '[ -f "$A/venv/lib/python3.12/site-packages/newpkg/__init__.py" ]'
check "kenara alınmış venv kopyası bırakılmadı" '[ -z "$(find "$A" -maxdepth 1 -name ".venv.failed-*")" ]'
check "venv'in Python'u yeterli: yeni venv kurulmadı" '[ ! -s "$T/calls/venv" ] && [ ! -L "$A/venv" ]'

echo "-- (e) veritabanını dönüştüren migration: restart'tan önce pg_dump"
printf 'DB_HOST=127.0.0.1\nDB_PORT=5433\nDB_USER=pops\nDB_PASS="gizli parola"\nDB_NAME=popsdb\n' > "$A/.env"
heavy_migration() {   # $1=sürüm adı: başında işaret satırı olan migration
    printf -- '-- pops: dump-before\nALTER TABLE t ALTER c TYPE bigint;\n' > "$R/Backend/migrations/$1.sql"
    commit "$R" "$1"
}
echo "ALTER TABLE t ADD d int;" > "$R/Backend/migrations/0003_d.sql"; commit "$R" 0003
run_deploy
check "işaretsiz migration: yedek yok, pg_dump çağrılmadı" '[ "$RC" = 0 ] && [ ! -s "$T/calls/pg_dump" ] && ! has "veritabanı:" "$T/out"'
heavy_migration 0004_tip
run_deploy
check "işaretli migration: pg_dump restart'tan ÖNCE, .env'deki bağlantıyla" \
    '[ "$RC" = 0 ] && has "before PGPASSWORD=gizli parola -Fc -h 127.0.0.1 -p 5433 -U pops -d popsdb" "$T/calls/pg_dump"'
dump=$(find "$A/.deploy-backups" -name "db-*.dump" | head -1)
check "yedek dosyası 600 izinle, çıktıda adı" '[ -n "$dump" ] && [ "$(stat -c %a "$dump")" = 600 ] && has "(veritabanı: $dump)" "$T/out"'
run_deploy
check "aynı migration yeniden: yeni yedek yok (artık APP'te)" '[ ! -s "$T/calls/pg_dump" ]'
heavy_migration 0005_tip; echo 0005_tip > "$T/ctl/applied"
run_deploy; rm -f "$T/ctl/applied"
check "veritabanında zaten uygulanmış: yedek yok" '[ "$RC" = 0 ] && [ ! -s "$T/calls/pg_dump" ]'
for n in 6 7 8; do heavy_migration "000${n}_tip"; run_deploy; done
check "en çok 3 veritabanı yedeği saklanır" '[ "$(find "$A/.deploy-backups" -name "db-*.dump" | wc -l)" = 3 ]'
heavy_migration 0009_tip
BEFORE=$(snap "$A")
POPS_DEPLOY_FREE_KB=1000 run_deploy
check "yer yetmezse dur: hiçbir şey değişmedi, restart yok" \
    '[ "$RC" != 0 ] && has "yer yok" "$T/out" && has "Hiçbir şey değiştirilmedi" "$T/out" && ! has restart "$T/calls/systemctl" && [ "$BEFORE" = "$(snap "$A")" ]'
touch "$T/ctl/pg_dump_fail"; run_deploy; rm -f "$T/ctl/pg_dump_fail"
check "pg_dump hatası: dur, hiçbir şey değişmedi" \
    '[ "$RC" != 0 ] && has "pg_dump başarısız" "$T/out" && ! has restart "$T/calls/systemctl" && [ "$BEFORE" = "$(snap "$A")" ]'
touch "$T/ctl/psql_fail"; run_deploy; rm -f "$T/ctl/psql_fail"
check "veritabanına bağlanılamazsa dur" '[ "$RC" != 0 ] && has "veritabanına bağlanılamadı" "$T/out" && [ "$BEFORE" = "$(snap "$A")" ]'
touch "$T/ctl/health_fail"; run_deploy; rm -f "$T/ctl/health_fail"
check "sonra sağlık kontrolü başarısız: kod geri, yedeği eski kodla geri yükleme komutu yazıldı" \
    '[ "$RC" != 0 ] && has "pg_restore --clean --if-exists" "$T/out" && has "db-" "$T/out" && [ "$BEFORE" = "$(snap "$A")" ]'
run_deploy
check "son olarak başarılı deploy" '[ "$RC" = 0 ] && [ -s "$T/calls/pg_dump" ]'
git -C "$R" rm -q Backend/migrations/000[3-9]_*.sql; commit "$R" "migration'lar geri"
rm -f "$A/.env"

# =========================================================================================================
echo "== pops-deploy-backend: venv'in Python'u eski (3.9 kurulumu, yeni sürüm >=3.11 ister)"
R3="$T/repo3"; A3="$T/app3"; C3="$T/etc/deploy3.conf"
git init -q -b main "$R3"; write_v "$R3" 0.0.1; commit "$R3" v0.0.1
install_like "$R3" "$A3" 3.9.25
printf 'REPO=%s\nAPP=%s\nSVC=popstest\nOWNER=%s\nHEALTH_BASE=http://127.0.0.1:18123\nKEEP_BACKUPS=3\n' "$R3" "$A3" "$ME" > "$C3"
chmod 644 "$C3"
printf '# requires-python: >=3.11\nfastapi==3\n' > "$R3/Backend/requirements.txt"; write_v "$R3" 0.0.2; commit "$R3" v0.0.2
venv_extras() { find "$A3" -maxdepth 1 \( -name 'venv-py*' -o -name '.venv.old-*' -o -name '.venv.failed-*' \) | sort; }

echo "-- (e) uygun yorumlayıcı yok: erken ve açık hata, hiçbir şey değişmez"
BEFORE=$(snap "$A3")
POPS_DEPLOY_PYTHONS="$T/yok/python3.12 $T/pybin/python3.10" run_deploy "$C3"
check "çıkış 3, gereken sürüm ve kurulum komutu söylendi" \
    '[ "$RC" = 3 ] && has "Python 3.11 ya da daha yenisi gerekli" "$T/out" && has "dnf install python3.12" "$T/out" && has "Hiçbir şey değiştirilmedi" "$T/out"'
check "venv'in sürümü raporlandı (3.9)" 'has "venv'"'"'in Python'"'"'u (3.9)" "$T/out"'
check "pip, venv kurulumu ve restart yok" '[ ! -s "$T/calls/pip" ] && [ ! -s "$T/calls/venv" ] && ! has restart "$T/calls/systemctl"'
check "APP birebir aynı, yedek klasörü bile açılmadı" '[ "$BEFORE" = "$(snap "$A3")" ] && [ ! -e "$A3/.deploy-backups" ]'
[ "$RC" = 3 ] || show_out

echo "-- (e) yeni venv kurulamadı (pip hatası): canlıya dokunulmadan durur"
BEFORE=$(snap "$A3"); touch "$T/ctl/pip_fail"
POPS_DEPLOY_PYTHONS="$T/pybin/python3.12" run_deploy "$C3"; rm -f "$T/ctl/pip_fail"
check "çıkış 1, 'yeni venv kurulamadı ... Hiçbir şey değiştirilmedi'" \
    '[ "$RC" = 1 ] && has "yeni venv kurulamadı" "$T/out" && has "Hiçbir şey değiştirilmedi" "$T/out"'
check "restart yok, yarım venv silindi, APP birebir aynı" \
    '! has restart "$T/calls/systemctl" && [ -z "$(venv_extras)" ] && [ "$BEFORE" = "$(snap "$A3")" ]'

echo "-- (e) yeni venv kuruldu ama sağlık kontrolü başarısız: kod VE eski venv geri"
BEFORE=$(snap "$A3"); touch "$T/ctl/health_fail"
POPS_DEPLOY_PYTHONS="$T/yok/python3.12 $T/pybin/python3.10 $T/pybin/python3.12" run_deploy "$C3"; rm -f "$T/ctl/health_fail"
check "3.10 atlandı (>=3.11 isteniyor), python3.12 ile venv kuruldu, pip yeni venv'e kurdu" \
    'has "3.12.0 $A3/venv-py3.12-" "$T/calls/venv" && [ "$(wc -l < "$T/calls/venv")" = 1 ] && has "$A3/venv-py3.12-" "$T/calls/pip"'
check "çıkış 1, 'Sağlık kontrolü başarısız', 'eski venv geri konuyor'" \
    '[ "$RC" != 0 ] && has "Sağlık kontrolü başarısız" "$T/out" && has "eski venv geri konuyor" "$T/out"'
check "APP birebir aynı: venv yine gerçek dizin (3.9), yeni venv ve kenardaki kopya yok" \
    '[ "$BEFORE" = "$(snap "$A3")" ] && [ ! -L "$A3/venv" ] && [ -z "$(venv_extras)" ]'
check "iki restart (deploy + geri dönüş)" '[ "$(count "restart popstest" "$T/calls/systemctl")" = 2 ]'
[ "$BEFORE" = "$(snap "$A3")" ] || { show_out; diff <(echo "$BEFORE") <(snap "$A3") | head -20 || true; }

echo "-- (e) yeni venv kuruldu ve yerine kondu"
POPS_DEPLOY_PYTHONS="$T/pybin/python3.12" run_deploy "$C3"
NV=$(readlink -f "$A3/venv")
check "çıkış 0, 'yeniden kuruldu' ve 'canlıda'" '[ "$RC" = 0 ] && has "Python 3.12 ile yeniden kuruldu" "$T/out" && has canlıda "$T/out"'
check "\$APP/venv artık venv-py3.12-* dizinine göreli bağ" \
    '[ -L "$A3/venv" ] && case "$(readlink "$A3/venv")" in venv-py3.12-*) true ;; *) false ;; esac'
check "yeni venv 3.12, paketler orada; uvicorn'un #! satırı yeni dizinin gerçek yolu" \
    'grep -qx "version = 3.12.0" "$NV/pyvenv.cfg" && [ -f "$NV/lib/python3.12/site-packages/newpkg/__init__.py" ] && [ "$(head -1 "$A3/venv/bin/uvicorn")" = "#!$NV/bin/python3" ]'
check "eski 3.9 venv silindi, yalnızca görüntüsünde (venv-*.tgz)" \
    '[ "$(venv_extras)" = "$NV" ] && tar tzf "$(find "$A3/.deploy-backups" -name "venv-*.tgz" -newer "$C3" | sort | tail -1)" | grep -q "^venv/lib/python3.9/"'
check "yeni kod, requirements ve VERSION canlıda; tek restart" \
    'cmp -s "$R3/Backend/requirements.txt" "$A3/requirements.txt" && [ "$(cat "$A3/VERSION")" = 0.0.2 ] && [ "$(count "restart popstest" "$T/calls/systemctl")" = 1 ]'
[ "$RC" = 0 ] || show_out

echo "-- (e) bağlı venv'de yerinde pip + sağlık hatası: gerçek dizin geri, bağ aynı"
printf '# requires-python: >=3.11\nfastapi==4\n' > "$R3/Backend/requirements.txt"; write_v "$R3" 0.0.3; commit "$R3" v0.0.3
BEFORE=$(snap "$A3"); touch "$T/ctl/health_fail"
run_deploy "$C3"; rm -f "$T/ctl/health_fail"
check "yeni venv kurulmadı, pip bağlı venv'e kurdu, geri dönüşte venv görüntüsü açıldı" \
    '[ ! -s "$T/calls/venv" ] && has "$A3/venv: install" "$T/calls/pip" && has "venv de geri yükleniyor" "$T/out"'
check "APP birebir aynı (bağ + gerçek dizin)" '[ "$BEFORE" = "$(snap "$A3")" ] && [ -L "$A3/venv" ]'
[ "$BEFORE" = "$(snap "$A3")" ] || { show_out; diff <(echo "$BEFORE") <(snap "$A3") | head -20 || true; }

echo "-- (e) ikinci yeniden kurulum (bağlı venv, >=3.13): hata olursa eski bağ geri, sonra başarılı"
printf '# requires-python: >=3.13\nfastapi==5\n' > "$R3/Backend/requirements.txt"; write_v "$R3" 0.0.4; commit "$R3" v0.0.4
BEFORE=$(snap "$A3"); OLDNV=$(readlink -f "$A3/venv"); touch "$T/ctl/health_fail"
POPS_DEPLOY_PYTHONS="$T/pybin/python3.12 $T/pybin/python3.13" run_deploy "$C3"; rm -f "$T/ctl/health_fail"
check "geri dönüş: bağ eski venv'i gösteriyor, APP birebir aynı" \
    '[ "$RC" != 0 ] && [ "$(readlink -f "$A3/venv")" = "$OLDNV" ] && [ "$BEFORE" = "$(snap "$A3")" ]'
POPS_DEPLOY_PYTHONS="$T/pybin/python3.12 $T/pybin/python3.13" run_deploy "$C3"
check "python3.13 ile kuruldu, bağ yeni dizinde, önceki venv-py3.12 dizini silindi" \
    '[ "$RC" = 0 ] && case "$(readlink "$A3/venv")" in venv-py3.13-*) true ;; *) false ;; esac && [ ! -e "$OLDNV" ] && [ "$(venv_extras)" = "$(readlink -f "$A3/venv")" ]'
[ "$RC" = 0 ] || show_out

# =========================================================================================================
echo "== pops-deploy-backend: kilit dosyası (Backend/requirements.lock, özetli)"
R5="$T/repo5"; A5="$T/app5"; C5="$T/etc/deploy5.conf"
git init -q -b main "$R5"; write_v "$R5" 0.0.1; commit "$R5" v0.0.1   # kilidi olmayan sürümle kurulmuş sunucu
install_like "$R5" "$A5"
printf 'REPO=%s\nAPP=%s\nSVC=popstest\nOWNER=%s\nHEALTH_BASE=http://127.0.0.1:18123\nKEEP_BACKUPS=3\n' "$R5" "$A5" "$ME" > "$C5"
chmod 644 "$C5"

echo "-- (f) kilit geldi: pip özet denetimiyle kilitten kurar, kilit requirements.txt'in yanına kopyalanır"
echo "fastapi==2" > "$R5/Backend/requirements.txt"; write_lock "$R5" good h11==1; write_v "$R5" 0.0.2; commit "$R5" v0.0.2
run_deploy "$C5"
check "çıkış 0; pip '--require-hashes -r \$APP/requirements.lock' ile çalıştı" \
    '[ "$RC" = 0 ] && has "$A5/venv: install -q --disable-pip-version-check --require-hashes -r $A5/requirements.lock" "$T/calls/pip"'
check "requirements.txt ve kilit APP'te, repodakiyle aynı" \
    'cmp -s "$R5/Backend/requirements.txt" "$A5/requirements.txt" && cmp -s "$R5/Backend/requirements.lock" "$A5/requirements.lock"'
[ "$RC" = 0 ] || show_out
run_deploy "$C5"
check "ikinci çalıştırma: 'zaten', pip yok" '[ "$RC" = 0 ] && has zaten "$T/out" && [ ! -s "$T/calls/pip" ]'

echo "-- (f) yalnızca kilit değişti (dolaylı bağımlılık): deploy edilir, pip kilitten kurar"
write_lock "$R5" good h11==2; commit "$R5" "yalniz kilit"
run_deploy "$C5"
check "'zaten' denmedi, pip kilitle çalıştı, yeni kilit APP'te" \
    '[ "$RC" = 0 ] && ! has zaten "$T/out" && has "--require-hashes -r $A5/requirements.lock" "$T/calls/pip" && cmp -s "$R5/Backend/requirements.lock" "$A5/requirements.lock"'
[ "$RC" = 0 ] || show_out

echo "-- (f) özet tutmuyor (yerinde pip): pip venv'e dokunmadan durur, kod ve venv birebir geri"
echo "fastapi==3" > "$R5/Backend/requirements.txt"; write_lock "$R5" bad h11==2; write_v "$R5" 0.0.4; commit "$R5" v0.0.4
BEFORE=$(snap "$A5")
run_deploy "$C5"
check "çıkış 1: pip'in özet hatası, 'pip install başarısız'" \
    '[ "$RC" != 0 ] && has "DO NOT MATCH THE HASHES" "$T/out" && has "pip install başarısız" "$T/out"'
check "APP birebir aynı (kod, requirements.txt, kilit, venv), sağlık kontrolüne gelinmedi" \
    '[ "$BEFORE" = "$(snap "$A5")" ] && [ ! -s "$T/calls/curl" ]'
[ "$BEFORE" = "$(snap "$A5")" ] || { show_out; diff <(echo "$BEFORE") <(snap "$A5") | head -20 || true; }

echo "-- (f) özet tutmuyor (yeni venv): canlıya hiç dokunulmadan durur"
printf '# requires-python: >=3.13\nfastapi==3\n' > "$R5/Backend/requirements.txt"; write_lock "$R5" bad; commit "$R5" v0.0.5
BEFORE=$(snap "$A5")
POPS_DEPLOY_PYTHONS="$T/pybin/python3.13" run_deploy "$C5"
check "çıkış 1, 'yeni venv kurulamadı', özet hatası; restart yok" \
    '[ "$RC" = 1 ] && has "yeni venv kurulamadı" "$T/out" && has "DO NOT MATCH THE HASHES" "$T/out" && ! has restart "$T/calls/systemctl"'
check "yeni venv'e kilidin kopyasından kurulmaya çalışıldı" \
    'has "--require-hashes -r $A5/venv-py3.13-" "$T/calls/pip" && has "/pops-requirements.lock" "$T/calls/pip"'
check "APP birebir aynı, yarım venv silindi" \
    '[ "$BEFORE" = "$(snap "$A5")" ] && [ -z "$(find "$A5" -maxdepth 1 -name "venv-py*")" ]'
write_lock "$R5" good; commit "$R5" "kilit duzeldi"
POPS_DEPLOY_PYTHONS="$T/pybin/python3.13" run_deploy "$C5"
check "doğru kilitle yeni venv kuruldu ve canlıda; venv'deki geçici kopya silindi" \
    '[ "$RC" = 0 ] && has "Python 3.13 ile yeniden kuruldu" "$T/out" && [ ! -e "$(readlink -f "$A5/venv")/pops-requirements.lock" ] && cmp -s "$R5/Backend/requirements.lock" "$A5/requirements.lock"'
[ "$RC" = 0 ] || show_out

# =========================================================================================================
echo "== pops-selfupdate (CHANNEL=release, imzalı etiketler)"
O="$T/origin.git"; D="$T/dev"; S="$T/surepo"; E="$T/su/etc"
mkdir -p "$E" "$T/su/spool"
git init -q --bare -b main "$O"
git clone -q "$O" "$D" 2>/dev/null
git -C "$D" symbolic-ref HEAD refs/heads/main   # boş depo klonunda dal adı git sürümüne göre değişebilir
write_v "$D" 0.0.1; commit "$D" v0.0.1; git -C "$D" tag -a v0.0.1 -m v0.0.1; git -C "$D" push -q origin main v0.0.1
git clone -q "$O" "$S"
printf 'REPO=%s\n' "$S" > "$E/deploy.conf"
printf 'CHANNEL=release\nALLOWED_SIGNERS=%s\n' "$E/allowed_signers" > "$E/selfupdate.conf"
chmod 644 "$E/deploy.conf" "$E/selfupdate.conf"
ssh-keygen -q -t ed25519 -N '' -C anahtar-a -f "$T/keyA"
ssh-keygen -q -t ed25519 -N '' -C anahtar-b -f "$T/keyB"
SIGNERS="$T/allowed_signers"
printf 'release@pops.test namespaces="git" %s\n' "$(cut -d' ' -f1,2 "$T/keyA.pub")" > "$SIGNERS"
STATUS="$T/su/state/deploy-status.json"; LOG="$T/su/state/deploy.log"

release() {   # $1=etiket $2=A | B (o anahtarla imzalı) | none (imzasız) | light (hafif etiket)
    write_v "$D" "${1#v}"; commit "$D" "$1"
    case "$2" in
        A|B) git -C "$D" -c gpg.format=ssh -c user.signingkey="$T/key$2" tag -s "$1" -m "$1" ;;
        none) git -C "$D" tag -a "$1" -m "$1" ;;
        light) git -C "$D" tag "$1" ;;
    esac
    git -C "$D" push -q origin main "$1"
}
run_su() {   # DRY=1: deneme. Çıktı $T/out, RC; bu çalıştırmanın log satırları $T/newlog
    reset_calls; RC=0
    LOGN=0; if [ -f "$LOG" ]; then LOGN=$(wc -l < "$LOG"); fi
    [ "${DRY:-0}" = 1 ] || : > "$T/su/spool/deploy-request.json"
    POPS_SELFUPDATE_DRYRUN="${DRY:-0}" POPS_SELFUPDATE_DIR="$T/su/spool" POPS_STATE_DIR="$T/su/state" \
        POPS_SELFUPDATE_CONF="$E/selfupdate.conf" POPS_DEPLOY_CONF="$E/deploy.conf" \
        POPS_DEPLOY_SCRIPT="${SU_DEPLOY:-$T/deploy-stub}" bash "$SELFUPDATE_SH" > "$T/out" 2>&1 || RC=$?
    tail -n +"$((LOGN + 1))" "$LOG" > "$T/newlog" 2>/dev/null || : > "$T/newlog"
}
su_head() { git -C "$S" rev-parse HEAD; }
tag_commit() { git -C "$D" rev-parse "$1^{commit}"; }
refused() {   # $1=beklenen HEAD. İmza reddi: merge yok, deploy yok, durum failed + imza mesajı
    [ "$RC" != 0 ] && [ "$(su_head)" = "$1" ] && [ ! -s "$T/calls/deploy" ] \
        && has '"state":"failed"' "$STATUS" && has "etiket imzasi dogrulanamadi" "$STATUS"
}

echo "-- allowed_signers yok, REQUIRE_SIGNED_TAGS=0 (eski kurulum): imzasız etiket uyarıyla geçer"
release v0.0.2 none; run_su
check "v0.0.2'ye geçildi, deploy çağrıldı, durum ok" \
    '[ "$RC" = 0 ] && [ "$(su_head)" = "$(tag_commit v0.0.2)" ] && [ -s "$T/calls/deploy" ] && has "\"state\":\"ok\"" "$STATUS"'
check "logda 'imzasız etiket' uyarısı" 'has "imzasız etiket" "$T/newlog"'

install -m 644 "$SIGNERS" "$E/allowed_signers"
echo "-- allowed_signers var (zorlama açık)"
release v0.0.3 A; run_su
check "A ile imzalı v0.0.3 doğrulandı ve dağıtıldı" \
    '[ "$RC" = 0 ] && [ "$(su_head)" = "$(tag_commit v0.0.3)" ] && [ -s "$T/calls/deploy" ] && has doğrulandı "$T/newlog"'
GOOD=$(su_head)
release v0.0.4 none; run_su
check "imzasız v0.0.4 reddedildi (merge yok, deploy yok, durum failed)" 'refused "$GOOD"'
release v0.0.5 B; run_su
check "listede olmayan B anahtarıyla imzalı v0.0.5 reddedildi" 'refused "$GOOD"'
release v0.0.6 light; run_su
check "hafif (imzasız) etiket v0.0.6 reddedildi" 'refused "$GOOD"'

echo "-- DRYRUN hiçbir zaman merge etmez, imza sonucunu söyler"
ST=$(sha256sum < "$STATUS")
DRY=1 run_su
check "DRYRUN (kötü etiket): 'imza=gecersiz', HEAD ve durum aynı, deploy yok" \
    '[ "$RC" = 0 ] && has imza=gecersiz "$T/out" && [ "$(su_head)" = "$GOOD" ] && [ "$(sha256sum < "$STATUS")" = "$ST" ] && [ ! -s "$T/calls/deploy" ]'
release v0.0.7 A
DRY=1 run_su
check "DRYRUN (iyi etiket): 'imza=gecerli', HEAD ve durum aynı, deploy yok" \
    '[ "$RC" = 0 ] && has imza=gecerli "$T/out" && [ "$(su_head)" = "$GOOD" ] && [ "$(sha256sum < "$STATUS")" = "$ST" ] && [ ! -s "$T/calls/deploy" ]'
run_su
check "gerçek çalıştırma v0.0.7'ye geçer" '[ "$RC" = 0 ] && [ "$(su_head)" = "$(tag_commit v0.0.7)" ] && [ -s "$T/calls/deploy" ]'
GOOD=$(su_head)

echo "-- güvenli olmayan allowed_signers / REQUIRE_SIGNED_TAGS=1 / güvenli olmayan selfupdate.conf"
chmod 666 "$E/allowed_signers"; release v0.0.8 A; run_su; chmod 644 "$E/allowed_signers"
check "herkesin yazabildiği allowed_signers'a güvenilmedi" 'refused "$GOOD" && has "güvenli değil" "$T/newlog"'
mv "$E/allowed_signers" "$E/allowed_signers.off"; echo "REQUIRE_SIGNED_TAGS=1" >> "$E/selfupdate.conf"
run_su
check "REQUIRE_SIGNED_TAGS=1 ve allowed_signers yok: reddedildi" 'refused "$GOOD"'
mv "$E/allowed_signers.off" "$E/allowed_signers"
run_su
check "dosya geri gelince v0.0.8 doğrulanıp dağıtıldı" '[ "$RC" = 0 ] && [ "$(su_head)" = "$(tag_commit v0.0.8)" ] && [ -s "$T/calls/deploy" ]'
chmod 666 "$E/selfupdate.conf"; release v0.0.9 A; run_su; chmod 644 "$E/selfupdate.conf"
check "herkesin yazabildiği selfupdate.conf okunmadı: durum failed, deploy yok, HEAD aynı" \
    '[ "$RC" != 0 ] && [ "$(su_head)" = "$(tag_commit v0.0.8)" ] && [ ! -s "$T/calls/deploy" ] && has "ayar dosyasi guvenli degil" "$STATUS"'

echo "-- uzaktan gelen tırnaklı etiket adı durum dosyasını bozmaz"
release 'v0.0.11"x' none; run_su
check "tırnaklı etiket adı reddedildi; durum geçerli JSON, deploy yok, HEAD aynı" \
    '[ "$RC" != 0 ] && [ ! -s "$T/calls/deploy" ] && [ "$(su_head)" = "$(tag_commit v0.0.8)" ] && has "gecersiz etiket adi" "$STATUS" && python3 -c "import json, sys; json.load(open(sys.argv[1]))" "$STATUS"'
git -C "$D" push -q origin ':refs/tags/v0.0.11"x'; git -C "$D" tag -d 'v0.0.11"x' >/dev/null; git -C "$S" tag -d 'v0.0.11"x' >/dev/null

echo "-- CHANNEL=main (geliştirme kanalı): imza denetlenmez"
sed -i 's/^CHANNEL=.*/CHANNEL=main/' "$E/selfupdate.conf"
write_v "$D" 0.0.10-dev; commit "$D" dev; git -C "$D" push -q origin main
run_su
check "origin/main'e geçildi, deploy çağrıldı" '[ "$RC" = 0 ] && [ "$(su_head)" = "$(git -C "$D" rev-parse HEAD)" ] && [ -s "$T/calls/deploy" ]'
check "logda 'imzası denetlenmez'" 'has "imzası denetlenmez" "$T/newlog"'
sed -i 's/^CHANNEL=.*/CHANNEL=release/' "$E/selfupdate.conf"

echo "-- uçtan uca: selfupdate -> gerçek pops-deploy-backend"
A2="$T/app2"; install_like "$S" "$A2"
printf 'REPO=%s\nAPP=%s\nSVC=popstest\nOWNER=%s\nHEALTH_BASE=http://127.0.0.1:18123\n' "$S" "$A2" "$ME" > "$E/deploy.conf"
release v0.1.0 A
SU_DEPLOY="$DEPLOY_SH" run_su
check "imzalı v0.1.0 dağıtıldı: durum ok, APP'te VERSION 0.1.0" \
    '[ "$RC" = 0 ] && has "\"state\":\"ok\"" "$STATUS" && [ "$(cat "$A2/VERSION")" = 0.1.0 ]'
check "deploy çıktısı deploy.log'da" 'has "Backend $(git -C "$S" rev-parse --short HEAD) canlıda" "$T/newlog"'

echo "-- uçtan uca: venv'in Python'u eski ve sunucuda yeni Python yok (deploy çıkış 3)"
A4="$T/app4"; install_like "$S" "$A4" 3.9.25
printf 'REPO=%s\nAPP=%s\nSVC=popstest\nOWNER=%s\nHEALTH_BASE=http://127.0.0.1:18123\n' "$S" "$A4" "$ME" > "$E/deploy.conf"
printf '# requires-python: >=3.10\nfastapi==9\n' > "$D/Backend/requirements.txt"; release v0.1.1 A
BEFORE=$(snap "$A4")
POPS_DEPLOY_PYTHONS="$T/yok/python3.12" SU_DEPLOY="$DEPLOY_SH" run_su
check "durum failed, panel mesajı 'python3.12 kurun', ASCII ve geçerli JSON" \
    'has "\"state\":\"failed\"" "$STATUS" && has "python3.12 kurun (EL9: dnf install python3.12)" "$STATUS" && ! LC_ALL=C grep -q "[^ -~]" "$STATUS" && python3 -c "import json, sys; json.load(open(sys.argv[1]))" "$STATUS"'
check "deploy.log'da deploy'un Türkçe açıklaması, APP birebir aynı" \
    'has "Hiçbir şey değiştirilmedi" "$T/newlog" && [ "$BEFORE" = "$(snap "$A4")" ]'

echo
echo "== Sonuç: $PASS geçti, $FAIL başarısız"
[ "$FAIL" = 0 ]
