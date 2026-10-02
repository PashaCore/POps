"""PostgreSQL bağlantı havuzu ve execute_query. db_pool açılışta (server.startup_event) atanır;
diğer modüller havuza 'db.db_pool' ile ERİŞİR (import anında None'a bağlanmasın diye).

Havuzdan bağlantı her zaman acquire() ile alınır: havuz dolu ve veritabanı yanıtsızken DB_ACQUIRE_TIMEOUT saniye
sonra asyncio.TimeoutError verir (eskiden süresiz beklenirdi). Tek sorgunun sınırı havuzda (command_timeout)."""

import contextlib

from pops import metrics
from pops.config import DB_ACQUIRE_TIMEOUT

db_pool = None


def acquire():
    return db_pool.acquire(timeout=DB_ACQUIRE_TIMEOUT)


_WRITE_VERBS = ("INSERT", "UPDATE", "DELETE", "WITH")


async def execute_query(query: str, params=(), fetch=False):
    metrics.db_query(not fetch or query.lstrip()[:6].upper().startswith(_WRITE_VERBS))
    async with acquire() as conn:
        if fetch:
            records = await conn.fetch(query, *params)
            return [dict(r) for r in records]
        else:
            await conn.execute(query, *params)
            return True


@contextlib.asynccontextmanager
async def transaction():
    """Tek bağlantıda, tek işlem (transaction): blok hata verirse hepsi geri alınır.

        async with db.transaction() as conn:
            row = await conn.fetchrow("UPDATE ... RETURNING id", ...)
    """
    async with acquire() as conn:
        async with conn.transaction():
            yield conn
