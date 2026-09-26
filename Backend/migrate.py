#!/usr/bin/env python3
"""POps veritabanı migration çalıştırıcısı — şemanın tek sahibi.

`migrations/NNNN_*.sql` dosyalarını dosya adı sırasına göre, her birini yalnızca
bir kez uygular ve `schema_migrations` tablosuna kaydeder. `0001_baseline.sql`
idempotenttir (CREATE ... IF NOT EXISTS, INSERT ... ON CONFLICT), bu yüzden
şemanın zaten kurulu olduğu canlı veritabanında çalıştırılması bir şeyi değiştirmez;
yalnızca "uygulandı" olarak kaydedilir. Boş bir veritabanında ise tüm şemayı kurar.

Aynı anda iki çalıştırıcının (ör. deploy script'i + yeniden başlayan uvicorn worker'ı)
çakışmaması için tüm çalıştırma bir PostgreSQL advisory lock içinde yapılır: ikinci
çalıştırıcı bekler, kilidi alınca bekleyen migration kalmadığını görür.

Migration'lar salt DDL/parametresizdir (asyncpg simple-query protokolü çoklu ifadeyi
yalnızca parametre olmadan çalıştırır). Her migration kendi transaction'ında atomiktir.

Kullanım:
    python migrate.py            # env'deki DB_* ile bağlan, bekleyenleri uygula
    python migrate.py --status   # neyin uygulandığını yaz, hiçbir şey değiştirme

server.py açılışta `run_migrations(pool)` çağırır (eski init_db() yerine).
"""
import asyncio
import glob
import os
import sys
from typing import List, Optional, Tuple

import asyncpg
from dotenv import load_dotenv

# 'POps' ASCII baytları -> tüm migration çalıştırmaları bu advisory lock'u paylaşır.
_ADVISORY_LOCK_KEY = 0x504F7073  # 1347371635

MIGRATIONS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "migrations")

_SCHEMA_MIGRATIONS_DDL = (
    "CREATE TABLE IF NOT EXISTS schema_migrations ("
    "  version TEXT PRIMARY KEY,"
    "  applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()"
    ")"
)


def _discover() -> List[Tuple[str, str]]:
    """(version, path) çiftlerini dosya adına göre sıralı döndürür (ör. '0001_baseline')."""
    out: List[Tuple[str, str]] = []
    for path in sorted(glob.glob(os.path.join(MIGRATIONS_DIR, "*.sql"))):
        version = os.path.splitext(os.path.basename(path))[0]
        out.append((version, path))
    return out


async def _apply(conn: asyncpg.Connection, verbose: bool = True) -> List[str]:
    """Bekleyen migration'ları advisory lock altında uygular; uygulananların listesini döndürür."""
    applied_now: List[str] = []
    await conn.execute("SELECT pg_advisory_lock($1)", _ADVISORY_LOCK_KEY)
    try:
        await conn.execute(_SCHEMA_MIGRATIONS_DDL)
        rows = await conn.fetch("SELECT version FROM schema_migrations")
        done = {r["version"] for r in rows}
        for version, path in _discover():
            if version in done:
                continue
            with open(path, "r", encoding="utf-8") as f:
                sql = f.read()
            async with conn.transaction():
                await conn.execute(sql)
                await conn.execute(
                    "INSERT INTO schema_migrations (version) VALUES ($1) ON CONFLICT (version) DO NOTHING",
                    version,
                )
            applied_now.append(version)
            if verbose:
                print(f"  ✔ uygulandi: {version}")
    finally:
        # Bağlantı kapansa bile session advisory lock otomatik serbest kalır; yine de açıkça bırak.
        await conn.execute("SELECT pg_advisory_unlock($1)", _ADVISORY_LOCK_KEY)
    return applied_now


async def run_migrations(pool: asyncpg.Pool, verbose: bool = True) -> List[str]:
    """server.py açılışında çağrılır: havuzdan bir bağlantı alıp bekleyen migration'ları uygular."""
    async with pool.acquire() as conn:
        return await _apply(conn, verbose=verbose)


def _db_config() -> dict:
    load_dotenv()
    try:
        return {
            "host": os.environ.get("DB_HOST", "localhost"),
            "port": int(os.environ.get("DB_PORT", "5432")),
            "user": os.environ["DB_USER"],
            "password": os.environ["DB_PASS"],
            "database": os.environ["DB_NAME"],
        }
    except KeyError as exc:
        sys.exit(f"Eksik ortam degiskeni: {exc} (bkz. .env.example: DB_USER / DB_PASS / DB_NAME)")


async def _run_cli(cfg: dict, status_only: bool) -> int:
    conn = await asyncpg.connect(**cfg)
    try:
        if status_only:
            await conn.execute(_SCHEMA_MIGRATIONS_DDL)
            rows = await conn.fetch("SELECT version, applied_at FROM schema_migrations ORDER BY version")
            done = {r["version"] for r in rows}
            print("Uygulanmis migration'lar:")
            for r in rows:
                print(f"  {r['version']}  ({r['applied_at']})")
            pending = [v for v, _ in _discover() if v not in done]
            print(f"Bekleyen: {pending or 'yok'}")
            return 0
        applied = await _apply(conn, verbose=True)
        print(f"Tamam. Bu calistirmada uygulanan: {applied or 'yok (her sey guncel)'}")
        return 0
    finally:
        await conn.close()


def main(argv: Optional[List[str]] = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    status_only = "--status" in argv
    return asyncio.run(_run_cli(_db_config(), status_only))


if __name__ == "__main__":
    sys.exit(main())
