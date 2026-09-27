"""POps merkez API — uygulama kurulumu.

Uç noktalar pops/routers/ altında gruplanmıştır (auth, control, agents, devices, tasks, updates);
çekirdek yardımcılar pops/ altındadır. Bu dosya yalnızca FastAPI uygulamasını kurar: rate limiter,
CORS, statik dosya bağlamaları, açılış/kapanış (DB havuzu + migration'lar) ve router'ların bağlanması.
"""

import asyncio
import os

import asyncpg
import bcrypt
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from slowapi import _rate_limit_exceeded_handler
from slowapi.errors import RateLimitExceeded

from migrate import run_migrations
from pops import db
from pops.audit import add_audit_log
from pops.config import (
    DB_CONFIG,
    DB_POOL_MAX,
    DB_POOL_MIN,
    JWT_ALGO,
    JWT_COOKIE_NAME,
    JWT_SECRET,
    UPDATES_DIR,
    UPLOAD_DIR,
)
from pops.db import execute_query
from pops.manager import manager
from pops.routers import (
    agents,
    auth,
    control,
    devices,
    helpdesk,
    inventory,
    licenses,
    notifications,
    reports,
    schedules,
    tasks,
)
from pops.scheduler import scheduler_loop
from pops.security import _totp_code, create_jwt, limiter, require_admin, require_superadmin
from system_routes import build_router as _build_system_router

# server modülünden dışarıya açılan adlar: uvicorn için 'app'; testler ve geri uyum için JWT/TOTP
# yardımcıları (eskiden hepsi bu dosyadaydı, artık pops/ altında).
__all__ = ["app", "create_jwt", "_totp_code", "JWT_SECRET", "JWT_ALGO", "JWT_COOKIE_NAME"]

# API şeması ve etkileşimli dokümantasyon (/docs, /redoc, /openapi.json) dışarıya sunulmaz
app = FastAPI(title="POps Merkez API", docs_url=None, redoc_url=None, openapi_url=None)


# Rate limiter hata yöneticisi
app.state.limiter = limiter


app.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)


# İzin verilen originler — CORS_ALLOWED_ORIGINS (virgülle ayrılmış) ortam değişkeninden
ALLOWED_ORIGINS = [o.strip() for o in os.environ.get('CORS_ALLOWED_ORIGINS', '').split(',') if o.strip()]


app.add_middleware(
    CORSMiddleware,
    allow_origins=ALLOWED_ORIGINS,
    allow_credentials=True,
    allow_methods=["GET", "POST", "PUT", "DELETE"],
    allow_headers=["Authorization", "Content-Type", "X-Agent-Version", "X-Requested-With"],
)


if not os.path.exists(UPLOAD_DIR):
    os.makedirs(UPLOAD_DIR)


if not os.path.exists(UPDATES_DIR):
    os.makedirs(UPDATES_DIR)


app.mount("/download", StaticFiles(directory=UPLOAD_DIR), name="download")


app.mount("/updates", StaticFiles(directory=UPDATES_DIR), name="updates")


# NOT: Veritabani semasi artik yalnizca migration'larla (migrate.py + migrations/NNNN_*.sql)
# kurulur. Yeni tablo/kolon eklerken buraya degil, yeni bir numarali .sql dosyasina yazin.


@app.on_event("startup")
async def startup_event():
    print("⏳ Veritabanı motoru başlatılıyor...")
    for i in range(5):
        try:
            db.db_pool = await asyncpg.create_pool(**DB_CONFIG, min_size=DB_POOL_MIN, max_size=DB_POOL_MAX)
            await run_migrations(db.db_pool)
            print("✅ PostgreSQL Bağlantısı Başarılı!")
            # Açılışta hiçbir ajan bağlı değil; bağlananlar yeniden Online yazılır
            await execute_query("UPDATE clients SET status = 'Offline' WHERE status IS DISTINCT FROM 'Offline'")

            admin_user = os.environ.get('PANEL_ADMIN_USER', 'admin')
            admin_pass = os.environ.get('PANEL_ADMIN_PASS')
            # Mevcut kayıt varsa sadece yoksa ekle (her restart'ta üzerine yazma)
            existing = await execute_query("SELECT id FROM users WHERE username=$1", (admin_user,), fetch=True)
            if not existing and not admin_pass:
                print(f"⚠️ PANEL_ADMIN_PASS tanımlı değil, '{admin_user}' hesabı oluşturulmadı (bkz. .env.example)")
            elif not existing:
                # bcrypt ile hash'le
                hashed = bcrypt.hashpw(admin_pass.encode(), bcrypt.gensalt()).decode()
                await execute_query(
                    "INSERT INTO users (username, password_hash, role) VALUES ($1, $2, 'superadmin')",
                    (admin_user, hashed),
                )
                print(f"👑 Panel Admin Hesabı Oluşturuldu: {admin_user}")
            else:
                print(f"👑 Panel Admin Hesabı Mevcut: {admin_user}")
            # Zamanlanmış görevler + güncelleme sonucu gelmeyen ajan uyarısı (30 sn'de bir)
            app.state.scheduler = asyncio.create_task(scheduler_loop())
            break
        except Exception as e:
            print(f"⚠️ Veritabanı bağlantı hatası (deneme {i+1}/5): {e}")
            await asyncio.sleep(3)


@app.on_event("shutdown")
async def shutdown_event():
    task = getattr(app.state, "scheduler", None)
    if task:
        task.cancel()
    if db.db_pool:
        await db.db_pool.close()


# Uç grupları (sıra: özgün tanım sırasına yakın; yol/metot çakışması yok — bkz. rota eşleşme testi)
for _r in (auth, control, agents, tasks, devices, schedules, notifications, inventory, reports, licenses, helpdesk):
    app.include_router(_r.router)

# Sistem/sürüm/release uçları (system_routes, bağımlılıklar enjekte edilir) en sonda
app.include_router(
    _build_system_router(require_admin, require_superadmin, execute_query, manager, UPDATES_DIR, add_audit_log)
)
