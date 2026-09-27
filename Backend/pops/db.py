"""PostgreSQL bağlantı havuzu ve execute_query. db_pool açılışta (server.startup_event) atanır;
diğer modüller havuza 'db.db_pool' ile ERİŞİR (import anında None'a bağlanmasın diye)."""

db_pool = None


async def execute_query(query: str, params=(), fetch=False):
    async with db_pool.acquire() as conn:
        if fetch:
            records = await conn.fetch(query, *params)
            return [dict(r) for r in records]
        else:
            await conn.execute(query, *params)
            return True
