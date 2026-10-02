"""PostgreSQL bağlantı havuzu ve execute_query. db_pool açılışta (server.startup_event) atanır;
diğer modüller havuza 'db.db_pool' ile ERİŞİR (import anında None'a bağlanmasın diye)."""

import contextlib

db_pool = None


async def execute_query(query: str, params=(), fetch=False):
    async with db_pool.acquire() as conn:
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
    async with db_pool.acquire() as conn:
        async with conn.transaction():
            yield conn
