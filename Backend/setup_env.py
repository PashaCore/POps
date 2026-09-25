#!/usr/bin/env python3
"""POps sunucu kurulum ve şifre yenileme betiği.

Sunucuda, projenin kök dizininden bir kez çalıştırın:

    python3 Backend/setup_env.py

Yaptıkları:
  1. Backend bağımlılıklarını kurar (pip install -r Backend/requirements.txt).
  2. .env.example'ı şablon alarak proje kökünde .env üretir; mevcut .env değerleri korunur.
  3. Boş gizli değerleri güçlü rastgele değerlerle doldurur:
     JWT_SECRET, BYPASS_SECRET, POPS_WOL_CONFIRM_PASSWORD.
  4. Git geçmişinde açığa çıkmış şifreleri yeniler:
     - PostgreSQL kullanıcısının şifresi (sunucuya düz metin değil, SCRAM-SHA-256 özeti gönderilir)
     - Panel yönetici hesabının (PANEL_ADMIN_USER) şifresi
  5. Ajanlara BypassSecret dağıtmak için panele yapıştırılacak komutu üretir.

Seçenekler için: python3 Backend/setup_env.py --help
"""
import argparse
import asyncio
import base64
import getpass
import hashlib
import hmac
import os
import re
import secrets
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEMPLATE = os.path.join(ROOT, ".env.example")
REQUIREMENTS = os.path.join(ROOT, "Backend", "requirements.txt")
ROLE_NAME_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


def parse_env(path):
    """KEY=VALUE satırlarını okur (yorumlar ve boş satırlar atlanır)."""
    values = {}
    if not os.path.isfile(path):
        return values
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip().strip("'\"")
    return values


def render_env(values):
    """.env.example'ı şablon olarak kullanır: yorumlar korunur, anahtarlar doldurulur."""
    lines, seen = [], set()
    with open(TEMPLATE, encoding="utf-8") as f:
        for line in f:
            stripped = line.strip()
            if stripped and not stripped.startswith("#") and "=" in stripped:
                key = stripped.split("=", 1)[0].strip()
                seen.add(key)
                lines.append(f"{key}={values.get(key, '')}\n")
            else:
                lines.append(line if line.endswith("\n") else line + "\n")
    extra = [k for k in values if k not in seen]
    if extra:
        lines.append("\n# ─── Önceki .env dosyasından korunan diğer değerler ───\n")
        lines.extend(f"{k}={values[k]}\n" for k in extra)
    return "".join(lines)


def scram_sha256_verifier(password, iterations=4096):
    """PostgreSQL'in sakladığı SCRAM-SHA-256 özeti; şifre sunucuya ve loglara düz metin gitmez."""
    salt = secrets.token_bytes(16)
    salted = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, iterations)
    client_key = hmac.new(salted, b"Client Key", "sha256").digest()
    stored_key = hashlib.sha256(client_key).digest()
    server_key = hmac.new(salted, b"Server Key", "sha256").digest()

    def b64(data):
        return base64.b64encode(data).decode()

    return f"SCRAM-SHA-256${iterations}:{b64(salt)}${b64(stored_key)}:{b64(server_key)}"


def agent_bypass_command(secret):
    """Ajanın C:\\POps\\appsettings.json dosyasına BypassSecret yazan tek satırlık komut."""
    script = (
        "$p='C:\\POps\\appsettings.json';"
        "$c=if(Test-Path $p){Get-Content $p -Raw|ConvertFrom-Json}else{[pscustomobject]@{}};"
        f"$c|Add-Member -NotePropertyName BypassSecret -NotePropertyValue '{secret}' -Force;"
        "$c|ConvertTo-Json -Depth 10|Set-Content -Path $p -Encoding UTF8;"
        "Write-Output 'POps BypassSecret yazildi.'"
    )
    encoded = base64.b64encode(script.encode("utf-16-le")).decode()
    return f"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}"


def install_requirements():
    print("→ Backend bağımlılıkları kuruluyor...")
    subprocess.check_call([sys.executable, "-m", "pip", "install", "-q", "-r", REQUIREMENTS])


def ask(prompt, default=""):
    answer = input(f"{prompt}{f' [{default}]' if default else ''}: ").strip()
    return answer or default


async def connect(cfg, password):
    import asyncpg
    return await asyncpg.connect(host=cfg["DB_HOST"], port=int(cfg["DB_PORT"]), user=cfg["DB_USER"],
                                 password=password, database=cfg["DB_NAME"], timeout=10)


async def check_connection(cfg, password):
    conn = await connect(cfg, password)
    await conn.close()


async def rotate_db_password(cfg, current_password, new_password):
    """ALTER ROLE ile şifreyi değiştirir, yeni şifreyle bağlanmayı doğrular; olmazsa eskisine döner."""
    role = cfg["DB_USER"]
    conn = await connect(cfg, current_password)
    try:
        await conn.execute(f"ALTER ROLE \"{role}\" WITH PASSWORD '{scram_sha256_verifier(new_password)}'")
        try:
            check = await connect(cfg, new_password)
            await check.close()
        except Exception as exc:
            await conn.execute(f"ALTER ROLE \"{role}\" WITH PASSWORD '{scram_sha256_verifier(current_password)}'")
            raise RuntimeError(f"Yeni şifreyle bağlantı doğrulanamadı, eski şifre geri yüklendi: {exc}")
    finally:
        await conn.close()


async def reset_admin_password(cfg, db_password, admin_user, new_password):
    """Panel yöneticisinin şifresini yeniler. users tablosu yoksa False döner (sunucu ilk açılışta oluşturur)."""
    import bcrypt
    conn = await connect(cfg, db_password)
    try:
        if not await conn.fetchval("SELECT to_regclass('public.users') IS NOT NULL"):
            return False
        hashed = bcrypt.hashpw(new_password.encode(), bcrypt.gensalt()).decode()
        result = await conn.execute("UPDATE users SET password_hash=$1 WHERE username=$2", hashed, admin_user)
        if result == "UPDATE 0":
            await conn.execute("INSERT INTO users (username, password_hash, role) VALUES ($1, $2, 'superadmin')",
                               admin_user, hashed)
        return True
    finally:
        await conn.close()


def main():
    parser = argparse.ArgumentParser(description="POps .env üretimi ve şifre yenileme")
    parser.add_argument("--env", default=os.path.join(ROOT, ".env"), help=".env dosyasının yolu (varsayılan: proje kökü)")
    parser.add_argument("--skip-install", action="store_true", help="pip install adımını atla")
    parser.add_argument("--no-rotate-db", action="store_true", help="veritabanı şifresini değiştirme")
    parser.add_argument("--no-rotate-admin", action="store_true", help="panel yönetici şifresini değiştirme")
    parser.add_argument("--yes", action="store_true", help="onay sormadan devam et")
    args = parser.parse_args()

    if not args.skip_install:
        install_requirements()

    # Değer önceliği: mevcut .env > ortam değişkenleri > .env.example varsayılanları
    template = parse_env(TEMPLATE)
    existing = parse_env(args.env)
    values = dict(template)
    for key in template:
        if os.environ.get(key):
            values[key] = os.environ[key]
    values.update({k: v for k, v in existing.items() if v != ""})
    for key in ("DB_USER", "DB_NAME"):
        if not values.get(key):
            values[key] = ask(f"{key}")
    if not ROLE_NAME_RE.fullmatch(values["DB_USER"]):
        sys.exit(f"DB_USER geçersiz: {values['DB_USER']!r} (yalnızca harf, rakam ve _ desteklenir)")
    current_db_password = values.get("DB_PASS") or getpass.getpass("Mevcut veritabanı şifresi (DB_PASS): ")
    admin_user = values.get("PANEL_ADMIN_USER") or "admin"

    print("\nYapılacaklar:")
    print(f"  • {args.env} yazılacak (mevcut değerler korunur, boş gizli değerler üretilir)")
    if not args.no_rotate_db:
        print(f"  • PostgreSQL kullanıcısı '{values['DB_USER']}' için yeni şifre belirlenecek")
    if not args.no_rotate_admin:
        print(f"  • Panel yöneticisi '{admin_user}' için yeni şifre belirlenecek")
    if not args.yes and ask("Devam edilsin mi? (e/h)", "h").lower() not in ("e", "evet", "y", "yes"):
        sys.exit("İptal edildi.")

    try:
        asyncio.run(check_connection(values, current_db_password))
    except Exception as exc:
        sys.exit(f"Veritabanına bağlanılamadı, hiçbir şey değiştirilmedi: {exc}")

    # Boş gizli değerleri üret
    if not values.get("JWT_SECRET"):
        values["JWT_SECRET"] = secrets.token_hex(32)
    if not values.get("BYPASS_SECRET"):
        values["BYPASS_SECRET"] = secrets.token_urlsafe(24)
    if not values.get("POPS_WOL_CONFIRM_PASSWORD"):
        values["POPS_WOL_CONFIRM_PASSWORD"] = f"{secrets.randbelow(10**6):06d}"
    new_db_password = secrets.token_urlsafe(24) if not args.no_rotate_db else current_db_password
    values["DB_PASS"] = new_db_password
    new_admin_password = secrets.token_urlsafe(12)
    if not args.no_rotate_admin:
        values["PANEL_ADMIN_PASS"] = ""  # yönetici şifresi diskte düz metin tutulmaz

    # .env önce geçici dosyaya yazılır; DB şifresi değiştikten sonra yerine taşınır
    env_dir = os.path.dirname(os.path.abspath(args.env))
    fd, tmp_path = tempfile.mkstemp(prefix=".env.", dir=env_dir)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(render_env(values))
        os.chmod(tmp_path, 0o600)
        if not args.no_rotate_db:
            print("→ Veritabanı şifresi yenileniyor...")
            asyncio.run(rotate_db_password(values, current_db_password, new_db_password))
        os.replace(tmp_path, args.env)
    except BaseException:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)
        raise
    print(f"→ {args.env} yazıldı (izinler: 600)")

    admin_created_by_server = False
    if not args.no_rotate_admin:
        print("→ Panel yönetici şifresi yenileniyor...")
        try:
            admin_updated = asyncio.run(reset_admin_password(values, new_db_password, admin_user, new_admin_password))
        except Exception as exc:
            sys.exit(f".env yazıldı ancak yönetici şifresi yenilenemedi: {exc}\n"
                     "Betiği --no-rotate-db ile tekrar çalıştırabilirsiniz.")
        if not admin_updated:
            # Boş veritabanı: yönetici hesabını sunucu ilk açılışta PANEL_ADMIN_PASS ile oluşturur
            values["PANEL_ADMIN_PASS"] = new_admin_password
            with open(args.env, "w", encoding="utf-8") as f:
                f.write(render_env(values))
            admin_created_by_server = True

    print("\n================ ÖZET ================")
    if not args.no_rotate_admin:
        print(f"Panel yöneticisi : {admin_user}")
        print(f"Yeni şifre       : {new_admin_password}   (bir kez gösterilir, şimdi kaydedin)")
        if admin_created_by_server:
            print("                   Hesap, backend ilk açıldığında oluşturulacak; sonra .env'deki")
            print("                   PANEL_ADMIN_PASS değerini silebilirsiniz.")
    print(f"WOL onay şifresi : {values['POPS_WOL_CONFIRM_PASSWORD']}  (Log & Envanter > tüm cihazları uyandır)")
    print("\nSonraki adımlar:")
    print("  1. Backend servisini yeniden başlatın (örn. sudo systemctl restart <pops-servis-adı>).")
    print("     Servis tanımında (systemd Environment=, docker-compose vb.) DB_PASS, JWT_SECRET gibi")
    print("     değişkenler varsa .env'deki değerleri ezer; bunları kaldırın.")
    print("  2. Dashboard bu dizinden sunuluyorsa .env'i kendisi okur. Farklı bir dizinden sunuluyorsa")
    print("     POPS_API_INTERNAL_URL ve POPS_WOL_CONFIRM_PASSWORD'ü web sunucusu ortamına ekleyin.")
    print("  3. Ajanlara BypassSecret göndermek için panelde Dosya Dağıtımı > Sistem Betiği ile tüm ağa")
    print("     (veya Terminal'den lab bazında) şu komutu çalıştırın; kapalı cihazlar açılınca alır:\n")
    print(agent_bypass_command(values["BYPASS_SECRET"]))
    print()


if __name__ == "__main__":
    main()
