"""Denetim zincirini (device_audit_logs) komut satırından doğrular.

Yedekten dönüşten sonra ve yedeğin geri yüklenebildiğini sınarken (pops-restore) kullanılır; panelde
Sistem sayfasının kullandığı /api/system/audit-verify ile aynı kontroldür.

    cd <uygulama dizini> && venv/bin/python audit_verify.py

Veritabanı ayarlarını ortamdan, yoksa çalışma dizinindeki .env'den okur (DB_HOST, DB_PORT, DB_USER, DB_PASS,
DB_NAME; ortamdaki değer .env'dekini ezer). Çıkış kodu: 0 zincir sağlam, 3 zincir kırık, 2 bağlanılamadı
(1: Python hatası; kırık zincirle karışmasın diye 1 kullanılmaz).
"""

import asyncio
import json
import os
import sys

import asyncpg
from dotenv import load_dotenv

from pops import auditchain, timeutil


async def _run():
    load_dotenv()
    try:
        conn = await asyncpg.connect(
            host=os.environ.get("DB_HOST", "127.0.0.1"),
            port=int(os.environ.get("DB_PORT", "5432")),
            user=os.environ["DB_USER"],
            password=os.environ["DB_PASS"],
            database=os.environ["DB_NAME"],
        )
    except (KeyError, OSError, asyncpg.PostgresError) as exc:
        print(json.dumps({"ok": False, "error": "bağlanılamadı: %r" % exc}, ensure_ascii=False))
        return 2
    try:
        # Zaman özete denetim saat diliminde (veritabanında sabit) metin olarak girer
        await timeutil.configure_from_db(conn)
        result = await auditchain.verify_batched(conn.fetch)
        migrations = await conn.fetchval("SELECT count(*) FROM schema_migrations")
    finally:
        await conn.close()
    result["migrations"] = migrations
    print(json.dumps(result, ensure_ascii=False))
    return 0 if result["ok"] else 3


if __name__ == "__main__":
    sys.exit(asyncio.run(_run()))
