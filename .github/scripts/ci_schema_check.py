#!/usr/bin/env python3
"""CI: boş bir veritabanında migrate.py'nin ürettiği şemayı doğrular.

Kritik kolonların (init_db()'den migration'lara taşınırken kaybolmaması gerekenler),
concurrent_limit seed'inin ve schema_migrations kayıt sayısının .sql dosya sayısıyla
eşleştiğini kontrol eder. Python 3.9 uyumlu.
"""
import asyncio
import glob
import os
import sys

import asyncpg

MIG_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Backend", "migrations")

# server.py'nin sorguladığı, init_db()'den gelen ve kaybolmaması gereken kolonlar
CHECKS = [
    ("clients", "display_name"),
    ("users", "permissions"),
    ("lab_settings", "layout_json"),
    ("agent_logs_v2", "meta_data"),
    ("bypass_tokens", "expires_at"),
    ("agent_versions", "version"),
]


async def main():
    conn = await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )
    bad = []
    try:
        for table, column in CHECKS:
            n = await conn.fetchval(
                "SELECT count(*) FROM information_schema.columns "
                "WHERE table_name=$1 AND column_name=$2",
                table, column,
            )
            if n != 1:
                bad.append("%s.%s eksik" % (table, column))
        seed = await conn.fetchval("SELECT count(*) FROM global_settings WHERE key='concurrent_limit'")
        if seed != 1:
            bad.append("concurrent_limit seed yok")
        applied = await conn.fetchval("SELECT count(*) FROM schema_migrations")
        nfiles = len(glob.glob(os.path.join(MIG_DIR, "*.sql")))
        if applied != nfiles:
            bad.append("schema_migrations=%s != %s .sql dosyasi" % (applied, nfiles))
    finally:
        await conn.close()
    if bad:
        sys.exit("SEMA HATASI: " + ", ".join(bad))
    print("OK: %d migration uygulandi; kritik kolonlar ve seed yerinde." % nfiles)


asyncio.run(main())
