#!/usr/bin/env bash
# pops-deploy-backend ve pops-selfupdate davranış testleri (CI: "Server scripts").
# Koşullar check'e bilerek tek tırnakla verilir ve eval ile değerlendirilir: değişkenler o anda açılır (SC2016);
# yalnızca bu koşullarda okunan değişkenler shellcheck'e kullanılmıyor görünür (SC2034).
# shellcheck disable=SC2016,SC2034
# Root GEREKMEZ ve root olarak ÇALIŞMAZ: her yol geçici bir dizindedir; systemctl, curl, sudo, journalctl,
# sleep ve install PATH'in önüne konan taklitlerdir (install gerçek install'a geçer, istenince hata verir),
# pip ise sahte venv'deki bir betiktir. Sistemde hiçbir şeye dokunulmaz.
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
if [ "${KEEP_TMP:-0}" = 1 ]; then echo "geçici dizin: $T"; else trap 'rm -rf "$T"' EXIT; fi
ME=$(id -un)
REAL_INSTALL=$(command -v install)
export PT="$T" PT_INSTALL="$REAL_INSTALL" HOME="$T/home" GIT_CONFIG_NOSYSTEM=1
export GIT_AUTHOR_NAME=POps GIT_AUTHOR_EMAIL=ci@pops.test GIT_COMMITTER_NAME=POps GIT_COMMITTER_EMAIL=ci@pops.test
export POPS_DEPLOY_ALLOW_NONROOT=1 POPS_SELFUPDATE_ALLOW_NONROOT=1
mkdir -p "$HOME" "$T/bin" "$T/ctl" "$T/calls" "$T/etc"

PASS=0; FAIL=0
check() {   # $1=açıklama $2=bash koşulu
    if eval "$2"; then PASS=$((PASS + 1)); echo "  ok    $1"; else FAIL=$((FAIL + 1)); echo "  HATA  $1"; fi
}
has() { grep -qF -- "$1" "$2"; }                  # $1=metin $2=dosya
count() { grep -cF -- "$1" "$2" || true; }

# --- Taklitler ------------------------------------------------------------------------------------------
cat > "$T/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$PT/calls/systemctl"
case "$1" in
    show) if [ -e "$PT/ctl/unit_missing" ]; then echo not-found; else echo loaded; fi ;;
    is-active) [ ! -e "$PT/ctl/inactive" ] ;;
    restart) [ ! -e "$PT/ctl/restart_fail" ] ;;
esac
EOF
cat > "$T/bin/curl" <<'EOF'
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
cat > "$T/bin/sudo" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$PT/calls/sudo"
while [ $# -gt 0 ]; do case "$1" in -u) shift 2 ;; -*) shift ;; *) break ;; esac; done
exec "$@"
EOF
cat > "$T/bin/journalctl" <<'EOF'
#!/usr/bin/env bash
echo "(journalctl taklidi) $*"
EOF
printf '#!/usr/bin/env bash\nexit 0\n' > "$T/bin/sleep"
cat > "$T/bin/install" <<'EOF'
#!/usr/bin/env bash
# ctl/install_fail içindeki desene uyan hedefte hata verir; gerisi gerçek install
if [ -e "$PT/ctl/install_fail" ]; then
    # shellcheck disable=SC2254  # desen dosyadan gelir, glob olarak eşleşmesi istenir
    case "${!#}" in $(cat "$PT/ctl/install_fail")) echo "install: taklit hata: ${!#}" >&2; exit 1 ;; esac
fi
exec "$PT_INSTALL" "$@"
EOF
# pip: venv'i gerçekten değiştirir (paket ekler, dosya değiştirir, siler); ctl/pip_fail varsa yarıda keser
cat > "$T/pip-stub" <<'EOF'
#!/usr/bin/env bash
V=$(cd "$(dirname "$0")/.." && pwd)
SP="$V/lib/python3.9/site-packages"
echo "$*" >> "$PT/calls/pip"
mkdir -p "$SP/newpkg"; echo "new" > "$SP/newpkg/__init__.py"
echo "2.0" > "$SP/oldpkg/version.txt"
if [ -e "$PT/ctl/pip_fail" ]; then echo "ERROR: taklit pip hatası" >&2; exit 1; fi
rm -f "$SP/gone.txt"
printf '#!%s/bin/python3\n' "$V" > "$V/bin/newtool"; chmod 755 "$V/bin/newtool"
EOF
# selfupdate'in çağırdığı deploy (yalnızca çağrıldığını ve o anki HEAD'i kaydeder)
cat > "$T/deploy-stub" <<'EOF'
#!/usr/bin/env bash
git -C "$PT/surepo" rev-parse HEAD >> "$PT/calls/deploy"
EOF
chmod 755 "$T/bin/"* "$T/pip-stub" "$T/deploy-stub"
export PATH="$T/bin:$PATH"

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
make_venv() {   # $1=APP: konsol betiklerinin #! satırı venv'in mutlak yolunu içerir (gerçek venv gibi)
    local v="$1/venv" sp="$1/venv/lib/python3.9/site-packages"
    mkdir -p "$v/bin" "$sp/oldpkg"
    ln -s lib "$v/lib64"; ln -s /usr/bin/python3 "$v/bin/python3"; ln -s python3 "$v/bin/python"
    printf 'home = /usr/bin\n' > "$v/pyvenv.cfg"
    printf '#!%s/bin/python3\nimport uvicorn\n' "$v" > "$v/bin/uvicorn"; chmod 755 "$v/bin/uvicorn"
    echo "1.0" > "$sp/oldpkg/version.txt"; echo "old" > "$sp/oldpkg/__init__.py"; echo "silinecek" > "$sp/gone.txt"
    cp "$T/pip-stub" "$v/bin/pip"
}
install_like() {   # $1=repo $2=APP: install.sh'in yaptığı gibi Backend'in kopyası + VERSION + anahtar + venv
    mkdir -p "$2"; cp -a "$1/Backend/." "$2/"; cp -a "$1/keys" "$2/keys"; cp "$1/VERSION" "$2/VERSION"; make_venv "$2"
}
snap() {   # $1=dizin: dosya listesi (tür, izin, yol, bağ hedefi) + içerik özetleri; .deploy-backups hariç
    ( cd "$1" && find . -path ./.deploy-backups -prune -o -printf '%y %m %p %l\n' | LC_ALL=C sort
      find . -path ./.deploy-backups -prune -o -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum )
}
reset_calls() { for f in systemctl curl sudo pip deploy; do : > "$T/calls/$f"; done; }
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
cat > "$T/etc/deploy.conf" <<EOF
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
check "yeni kod ve migration geldi, silinen modül gitti" \
    '[ -f "$A/extra.py" ] && [ -f "$A/migrations/0002_c.sql" ] && [ ! -e "$A/pops/routers/b.py" ]'
check "pip'in değişikliği kaldı" '[ -f "$A/venv/lib/python3.9/site-packages/newpkg/__init__.py" ]'
check "kenara alınmış venv kopyası bırakılmadı" '[ -z "$(find "$A" -maxdepth 1 -name ".venv.failed-*")" ]'

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

echo
echo "== Sonuç: $PASS geçti, $FAIL başarısız"
[ "$FAIL" = 0 ]
