"""POps merkez API — uygulama kurulumu.

Uç noktalar pops/routers/ altında gruplanmıştır (auth, control, agents, devices, tasks, updates);
çekirdek yardımcılar pops/ altındadır. Bu dosya yalnızca FastAPI uygulamasını kurar: rate limiter,
CORS, statik dosya bağlamaları, açılış/kapanış (DB havuzu + migration'lar) ve router'ların bağlanması.
"""

import asyncio
import logging
import os

import asyncpg
import bcrypt
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from slowapi import _rate_limit_exceeded_handler
from slowapi.errors import RateLimitExceeded

from migrate import run_migrations_on
from pops import db, heartbeats, notify, secretbox, update_tracking
from pops.logs import setup_logging
from pops.metrics import RequestContextMiddleware
from pops.audit import add_audit_log
from pops.config import (
    DB_COMMAND_TIMEOUT,
    DB_CONFIG,
    DB_CONNECT_TIMEOUT,
    DB_IDLE_IN_TRANSACTION_MS,
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
    activity,
    agents,
    auth,
    control,
    devices,
    helpdesk,
    inventory,
    licenses,
    modules as modules_router,
    notifications,
    ops,
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

# Log biçimi uygulama kurulmadan ayarlanır: uvicorn'un kendi satırları da aynı biçime yönlenir (bkz. pops/logs.py)
setup_logging()
log = logging.getLogger("pops.server")

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
    allow_headers=["Authorization", "Content-Type", "X-Agent-Version", "X-Requested-With", "X-Request-ID"],
    expose_headers=["X-Request-ID"],
)

# En dıştaki kullanıcı middleware'i (en son eklenen): request_id, istek metrikleri, yakalanmayan hata logu
app.add_middleware(RequestContextMiddleware)


if not os.path.exists(UPLOAD_DIR):
    os.makedirs(UPLOAD_DIR)


if not os.path.exists(UPDATES_DIR):
    os.makedirs(UPDATES_DIR)


# /download artık statik değil: dağıtım paketleri yalnızca imzalı adresle iner (bkz. routers/tasks.py download_file)


app.mount("/updates", StaticFiles(directory=UPDATES_DIR), name="updates")


# NOT: Veritabani semasi artik yalnizca migration'larla (migrate.py + migrations/NNNN_*.sql)
# kurulur. Yeni tablo/kolon eklerken buraya degil, yeni bir numarali .sql dosyasina yazin.


@app.on_event("startup")
async def startup_event():
    log.info("veritabanına bağlanılıyor")
    for i in range(5):
        try:
            # Migration'lar ayrı bağlantıda ve sorgu süre sınırı olmadan (büyük tabloda indeks kurmak uzun sürebilir)
            mig = await asyncpg.connect(**DB_CONFIG, timeout=DB_CONNECT_TIMEOUT)
            try:
                applied = await run_migrations_on(mig, verbose=False)
            finally:
                await mig.close()
            db.db_pool = await asyncpg.create_pool(
                **DB_CONFIG,
                min_size=DB_POOL_MIN,
                max_size=DB_POOL_MAX,
                timeout=DB_CONNECT_TIMEOUT,
                command_timeout=DB_COMMAND_TIMEOUT,
                server_settings={"idle_in_transaction_session_timeout": str(DB_IDLE_IN_TRANSACTION_MS)},
            )
            log.info("veritabanı hazır", extra={"migrations_applied": applied})
            # Düz metin ya da eski anahtarla şifreli 2FA ve bypass anahtarları birincil anahtarla şifrelenir
            # (R-12, B14)
            await secretbox.reseal_totp_secrets(execute_query)
            await secretbox.reseal_bypass_keys(execute_query)
            # Yeniden başlatmadan önce gönderilmiş, sonucu beklenen ajan güncellemeleri (S20)
            await update_tracking.load()
            # Açılışta hiçbir ajan bağlı değil; bağlananlar yeniden Online yazılır
            await execute_query("UPDATE clients SET status = 'Offline' WHERE status IS DISTINCT FROM 'Offline'")

            admin_user = os.environ.get('PANEL_ADMIN_USER', 'admin')
            admin_pass = os.environ.get('PANEL_ADMIN_PASS')
            # Mevcut kayıt varsa sadece yoksa ekle (her restart'ta üzerine yazma)
            existing = await execute_query("SELECT id FROM users WHERE username=$1", (admin_user,), fetch=True)
            if not existing and not admin_pass:
                log.warning("PANEL_ADMIN_PASS tanımlı değil, yönetici hesabı oluşturulmadı (bkz. .env.example)",
                            extra={"user": admin_user})
            elif not existing:
                # bcrypt ile hash'le
                hashed = bcrypt.hashpw(admin_pass.encode(), bcrypt.gensalt()).decode()
                await execute_query(
                    "INSERT INTO users (username, password_hash, role) VALUES ($1, $2, 'superadmin')",
                    (admin_user, hashed),
                )
                log.info("panel yönetici hesabı oluşturuldu", extra={"user": admin_user})
            else:
                log.info("panel yönetici hesabı mevcut", extra={"user": admin_user})
            # Zamanlanmış görevler + güncelleme sonucu gelmeyen ajan uyarısı (30 sn'de bir)
            app.state.scheduler = asyncio.create_task(scheduler_loop())
            # Heartbeat'ler toplu yazılır (bkz. pops/heartbeats.py)
            app.state.heartbeats = asyncio.create_task(heartbeats.flush_loop())
            break
        except Exception as e:
            log.error("veritabanı bağlantı hatası", extra={"attempt": i + 1, "of": 5, "error": repr(e)[:300]})
            # Yarım kalan denemenin havuzu kapatılır (bir sonraki deneme yenisini açar; bağlantılar sızmasın)
            if db.db_pool is not None:
                await db.db_pool.close()
                db.db_pool = None
            if i == 4:
                # Veritabanısız "çalışıyor" görünmek yerine dur: systemd (Restart=always) ve Docker yeniden başlatır,
                # sağlık kontrolü de başarısız görünür
                raise RuntimeError("Veritabanına bağlanılamadı ya da migration'lar uygulanamadı (5 deneme)") from e
            await asyncio.sleep(3)


# Kapanışta beklenecek en uzun süre (saniye); systemd'nin durdurma süresinin (varsayılan 90 sn) altında kalır
SHUTDOWN_DRAIN_SECONDS = 10


@app.on_event("shutdown")
async def shutdown_event():
    """Düzgün kapanış. uvicorn bu noktada yeni bağlantı almayı bırakmış, açık WebSocket'leri kapatmış (ajanlar
    "Offline" yazıldı) ve süren HTTP isteklerini beklemiştir. Kalanlar: zamanlayıcı (turu yarıda kalırsa işlemi
    geri alınır), bellekte bekleyen heartbeat'ler, arka plandaki bildirim gönderimleri ve havuz."""
    for name in ("scheduler", "heartbeats"):
        task = getattr(app.state, name, None)
        if task:
            task.cancel()
            try:
                await task
            except (asyncio.CancelledError, Exception):
                pass
    if db.db_pool:
        try:
            await asyncio.wait_for(heartbeats.flush(), SHUTDOWN_DRAIN_SECONDS)
        except Exception:
            log.warning("kapanışta heartbeat'ler yazılamadı", exc_info=True)
    pending = [t for t in notify._tasks if not t.done()]
    if pending:
        log.info("kapanış: bekleyen bildirim gönderimleri bekleniyor", extra={"count": len(pending)})
        _done, late = await asyncio.wait(pending, timeout=SHUTDOWN_DRAIN_SECONDS)
        for t in late:
            t.cancel()
    if db.db_pool:
        try:
            await asyncio.wait_for(db.db_pool.close(), SHUTDOWN_DRAIN_SECONDS)
        except Exception:
            db.db_pool.terminate()
        db.db_pool = None


# Uç grupları (sıra: özgün tanım sırasına yakın; yol/metot çakışması yok — bkz. rota eşleşme testi)
_ROUTERS = (
    auth, control, agents, tasks, devices, schedules, notifications, inventory, reports, licenses, helpdesk, ops,
    activity, modules_router,
)
for _r in _ROUTERS:
    app.include_router(_r.router)

# Sistem/sürüm/release uçları (system_routes, bağımlılıklar enjekte edilir) en sonda
app.include_router(
    _build_system_router(require_admin, require_superadmin, execute_query, manager, UPDATES_DIR, add_audit_log)
)
