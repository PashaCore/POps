#!/usr/bin/env bash
# pops-restore --list davranış testleri (CI: "Server scripts").
# Root GEREKMEZ ve root olarak ÇALIŞMAZ: her yol geçici bir dizindedir.
# Koşullar check'e bilerek tek tırnakla verilir ve eval ile değerlendirilir: değişkenler o anda açılır (SC2016);
# yalnızca bu koşullarda okunan değişkenler shellcheck'e kullanılmıyor görünür (SC2034).
# shellcheck disable=SC2016,SC2034
# Sistemde hiçbir şeye dokunulmaz.
#     bash Installer/server/tests/test_restore.sh
set -euo pipefail

SRV=$(cd "$(dirname "$0")/.." && pwd)        # Installer/server
RESTORE_SH="$SRV/pops-restore"

if [ "$(id -u)" -eq 0 ]; then
    echo "Bu test root olarak çalıştırılmaz (yanlışlıkla gerçek sisteme dokunmasın)."
    exit 1
fi

T=$(mktemp -d "${TMPDIR:-/tmp}/pops-restore-test.XXXXXX")
trap 'rm -rf "${T:?}"' EXIT

PASS=0
FAIL=0

check() {   # $1=açıklama $2=bash koşulu
    if eval "$2"; then
        PASS=$((PASS + 1))
        echo "  ok    $1"
    else
        FAIL=$((FAIL + 1))
        echo "  HATA  $1"
    fi
}

echo "== 1. -h yardım çıktısı --list seçeneğini içerir"
HELP_OUT=$("$RESTORE_SH" -h)
check "yardım metninde --list var" 'echo "$HELP_OUT" | grep -q -- "--list"'
check "yardım metninde 'Mevcut yedekleri listeler' var" 'echo "$HELP_OUT" | grep -q "Mevcut yedekleri listeler"'

echo "== 2. Olmayan yedek dizini hata verir"
RC=0
ERR_OUT=$(BACKUP_DIR="$T/nonexistent" "$RESTORE_SH" --list 2>&1) || RC=$?
check "olmayan dizin hata koduyla biter" '[ "$RC" != 0 ]'
check "hata mesajında 'yedek dizini yok' var" 'echo "$ERR_OUT" | grep -q "yedek dizini yok"'

echo "== 3. Boş yedek dizini başarıyla biter (çıkış 0, boş çıktı)"
EMPTY_DIR="$T/empty"
mkdir -p "$EMPTY_DIR"
LIST_EMPTY=$(BACKUP_DIR="$EMPTY_DIR" "$RESTORE_SH" --list)
check "boş dizinde çıkış 0 ve çıktı boş" '[ -z "$LIST_EMPTY" ]'

echo "== 4. Geçerli yedek düzgün listelenir"
BACKUPS="$T/backups"
B1="$BACKUPS/20261025T140000Z"
mkdir -p "$B1"
cat > "$B1/meta.json" <<'JSON'
{"created_at":"20261025T140000Z","version":"0.1.25","db_name":"pops_prod"}
JSON
touch "$B1/SHA256SUMS"
touch "$B1/db.dump"

LIST_B1=$(BACKUP_DIR="$BACKUPS" "$RESTORE_SH" --list)
check "klasör adı çıktıda var" 'echo "$LIST_B1" | grep -q "20261025T140000Z"'
check "tarih çıktıda var" 'echo "$LIST_B1" | grep -q "20261025T140000Z"'
check "sürüm çıktıda var" 'echo "$LIST_B1" | grep -q "0.1.25"'
check "geri yüklenemez işareti yok" '! echo "$LIST_B1" | grep -q "geri yüklenemez"'

echo "== 5. .failed klasörü ve eksik dosyalı klasörler 'geri yüklenemez' olarak işaretlenir"
B2="$BACKUPS/20261025T130000Z.failed"
mkdir -p "$B2"
cat > "$B2/meta.json" <<'JSON'
{"created_at":"20261025T130000Z","version":"0.1.25"}
JSON

B3="$BACKUPS/20261025T120000Z"
mkdir -p "$B3"
# meta.json ve SHA256SUMS yok

LIST_ALL=$(BACKUP_DIR="$BACKUPS" "$RESTORE_SH" --list)
check ".failed klasörü geri yüklenemez işaretli" 'echo "$LIST_ALL" | grep "20261025T130000Z.failed" | grep -q "geri yüklenemez: .failed"'
check "eksik meta.json klasörü geri yüklenemez işaretli" 'echo "$LIST_ALL" | grep "20261025T120000Z" | grep -q "geri yüklenemez: meta.json yok"'

echo "== 6. Sıralama en yeni klasör başta olacak şekildedir"
FIRST_LINE=$(echo "$LIST_ALL" | head -n1)
check "en yeni 140000Z ilk satırda" 'echo "$FIRST_LINE" | grep -q "20261025T140000Z"'

echo "== 7. --list ile geçersiz seçenek kombinasyonları reddedilir"
RC_FOLDER=0
"$RESTORE_SH" --list "$B1" >/dev/null 2>&1 || RC_FOLDER=$?
check "--list ile klasör verilince hata" '[ "$RC_FOLDER" != 0 ]'

RC_YES=0
"$RESTORE_SH" --list --yes >/dev/null 2>&1 || RC_YES=$?
check "--list ile --yes verilince hata" '[ "$RC_YES" != 0 ]'

RC_CHECK=0
"$RESTORE_SH" --list --check >/dev/null 2>&1 || RC_CHECK=$?
check "--list ile --check verilince hata" '[ "$RC_CHECK" != 0 ]'

RC_DB=0
"$RESTORE_SH" --list --db-only >/dev/null 2>&1 || RC_DB=$?
check "--list ile --db-only verilince hata" '[ "$RC_DB" != 0 ]'

RC_FILES=0
"$RESTORE_SH" --list --files-only >/dev/null 2>&1 || RC_FILES=$?
check "--list ile --files-only verilince hata" '[ "$RC_FILES" != 0 ]'

echo
echo "== Sonuç: $PASS geçti, $FAIL başarısız"
[ "$FAIL" = 0 ]
