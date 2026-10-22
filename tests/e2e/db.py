#!/usr/bin/env python3
"""Uçtan uca testlerin veritabanı adımları (global-setup.js çağırır). Arka ucun asyncpg'sini kullanır.

  db.py prepare      DB_NAME boşsa e2e işaret tablosunu kurar; önceki bir e2e çalıştırmasından kalmışsa (işaret
                     tablosu var) bütün tabloları siler. Başka her veritabanında (tablo var, işaret yok) durur:
                     testler sınıf siler, ayar değiştirir; gerçek bir veritabanına asla dokunulmaz.
  db.py seed FILE    SQL dosyasını tek oturumda çalıştırır (seed.sql).

Bağlantı: DB_HOST, DB_PORT, DB_USER, DB_PASS, DB_NAME.
"""

import asyncio
import os
import sys

import asyncpg

MARKER = "pops_e2e_marker"


async def _connect():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
        timeout=15,
    )


async def prepare():
    conn = await _connect()
    try:
        rows = await conn.fetch("SELECT tablename FROM pg_tables WHERE schemaname = 'public'")
        tables = {r["tablename"] for r in rows}
        if tables and MARKER not in tables:
            sys.exit(
                "db.py: DB_NAME=%s boş değil ve e2e testlerinin değil (%s tablosu yok). Testler veriyi değiştirir; "
                "boş, atılabilir bir veritabanı verin." % (os.environ["DB_NAME"], MARKER)
            )
        if tables:
            # Önceki e2e çalıştırması: şemayı sıfırla (migration'lar yalnızca tablo ve bağlı dizileri kurar)
            await conn.execute(
                """
                DO $$ DECLARE r record; BEGIN
                    FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
                        EXECUTE format('DROP TABLE IF EXISTS public.%I CASCADE', r.tablename);
                    END LOOP;
                    FOR r IN SELECT sequence_name FROM information_schema.sequences
                             WHERE sequence_schema = 'public' LOOP
                        EXECUTE format('DROP SEQUENCE IF EXISTS public.%I CASCADE', r.sequence_name);
                    END LOOP;
                END $$;
                """
            )
            print("db.py: önceki e2e şeması silindi (%d tablo)" % len(tables))
        await conn.execute("CREATE TABLE %s (created_at timestamptz NOT NULL DEFAULT now())" % MARKER)
    finally:
        await conn.close()


async def seed(path):
    with open(path, encoding="utf-8") as f:
        sql = f.read()
    conn = await _connect()
    try:
        async with conn.transaction():
            await conn.execute(sql)
    finally:
        await conn.close()
    print("db.py: örnek veri yüklendi (%s)" % os.path.basename(path))


def main():
    if len(sys.argv) >= 2 and sys.argv[1] == "prepare":
        asyncio.run(prepare())
    elif len(sys.argv) == 3 and sys.argv[1] == "seed":
        asyncio.run(seed(sys.argv[2]))
    else:
        sys.exit("kullanım: db.py prepare | db.py seed <dosya.sql>")


if __name__ == "__main__":
    main()
