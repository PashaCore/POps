
from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect, File, UploadFile, Request, Depends, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse
from fastapi.security import HTTPBearer, HTTPAuthorizationCredentials
from pydantic import BaseModel
import asyncpg
import datetime
from typing import List, Dict, Optional
import json
import os
import re
import shutil
import asyncio
import socket
import hashlib
import hmac
import zipfile
import secrets
import bcrypt
import jwt  # PyJWT
from werkzeug.utils import secure_filename
from slowapi import Limiter, _rate_limit_exceeded_handler
from slowapi.util import get_remote_address
from slowapi.errors import RateLimitExceeded

from dotenv import load_dotenv
load_dotenv()

# Sema artik migration'larla kurulur (migrate.py); init_db() kaldirildi.
from migrate import run_migrations

def require_env(name: str) -> str:
    """Zorunlu ortam değişkenini oku; tanımlı değilse sunucu açıklayıcı bir hatayla durur."""
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"Ortam değişkeni tanımlı değil: {name} (bkz. .env.example)")
    return value

# ─── Güvenlik Sabitleri ────────────────────────────────────────────────────────
# Şifre, anahtar ve IP gibi ortama özel değerler koda gömülmez; .env / os.environ'dan okunur.
JWT_SECRET   = require_env('JWT_SECRET')
JWT_ALGO     = 'HS256'
JWT_EXPIRE_H = int(os.environ.get('JWT_EXPIRE_HOURS', '12'))         # Token ömrü (saat)
# Çevrimdışı bypass kodları için ajanlarla paylaşılan gizli anahtar (ajan: BypassSecret / POPS_BYPASS_SECRET)
BYPASS_SECRET = os.environ.get('BYPASS_SECRET', '').strip()

# Panel JWT'yi httpOnly çerezde taşır (JS erişemez); diğer istemciler Authorization: Bearer kullanabilir
JWT_COOKIE_NAME = 'pops_jwt'
CSRF_SAFE_METHODS = {"GET", "HEAD", "OPTIONS"}

limiter = Limiter(key_func=get_remote_address)
security_scheme = HTTPBearer(auto_error=False)

def create_jwt(username: str, role: str) -> str:
    expire = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=JWT_EXPIRE_H)
    return jwt.encode({'sub': username, 'role': role, 'exp': expire}, JWT_SECRET, algorithm=JWT_ALGO)

def verify_jwt(token: str) -> dict:
    try:
        return jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALGO])
    except jwt.PyJWTError:
        return None

async def require_auth(request: Request, creds: HTTPAuthorizationCredentials = Depends(security_scheme)):
    token = creds.credentials if creds else request.cookies.get(JWT_COOKIE_NAME)
    if not token:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail='Token gerekli')
    # Çerezle doğrulanan, durum değiştiren isteklerde CSRF koruması: özel başlık zorunlu
    # (başka bir site bu başlığı CORS izni olmadan gönderemez)
    if not creds and request.method not in CSRF_SAFE_METHODS and request.headers.get('X-Requested-With') != 'XMLHttpRequest':
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail='CSRF doğrulaması başarısız')
    payload = verify_jwt(token)
    if not payload:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail='Geçersiz veya süresi dolmuş token')
    return payload

async def require_admin(payload: dict = Depends(require_auth)):
    if payload.get('role') not in ['admin', 'superadmin']:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Bu islem icin admin yetkisi gereklidir.")
    return payload

async def require_superadmin(payload: dict = Depends(require_auth)):
    if payload.get('role') != 'superadmin':
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Bu islem icin superadmin yetkisi gereklidir.")
    return payload

def ws_check_token(token: Optional[str]) -> bool:
    """WebSocket bağlantılarında httpOnly JWT çereziyle doğrulama."""
    if not token:
        return False
    return verify_jwt(token) is not None

# ── Ajan kimlik doğrulama (Faz 3): enroll token + per-cihaz secret ──────────────
def _hash_secret(secret: str) -> str:
    return hashlib.sha256(secret.encode('utf-8')).hexdigest()

async def enforce_agent_auth_enabled() -> bool:
    """global_settings.enforce_agent_auth = '1' ise kimliksiz ajan bağlantıları reddedilir.
    Varsayılan KAPALI (accept-both): ajan güncellenene kadar mevcut (secret'sız) ajanlar düşmez."""
    try:
        rows = await execute_query("SELECT value FROM global_settings WHERE key='enforce_agent_auth'", fetch=True)
    except Exception:
        return False
    return bool(rows and str(rows[0]["value"]) == '1')

async def verify_agent_secret(pc_name: str, secret: Optional[str]) -> bool:
    """Ajanın sunduğu secret, o cihaz için saklanan SHA-256 hash ile sabit-zamanlı karşılaştırılır."""
    if not secret:
        return False
    try:
        rows = await execute_query("SELECT secret_hash FROM agent_secrets WHERE pc_name=$1", (pc_name,), fetch=True)
    except Exception:
        return False
    if not rows:
        return False
    return hmac.compare_digest(str(rows[0]["secret_hash"]), _hash_secret(secret))

async def valid_enroll_token(token: Optional[str]) -> Optional[dict]:
    """Kullanılmamış ve süresi dolmamış enroll jetonunu döndürür (henüz tüketmez); yoksa None."""
    if not token:
        return None
    try:
        rows = await execute_query(
            "SELECT id, lab_name FROM enroll_tokens "
            "WHERE token=$1 AND NOT is_used AND expires_at > NOW() AND use_count < max_uses",
            (token,), fetch=True)
    except Exception:
        return None
    return rows[0] if rows else None

async def agent_http_auth(request: Request):
    """Ajan HTTP uçları (inventory/logs/auth/policy_alert) için accept-both kimlik.
    enforce_agent_auth KAPALIYKEN serbest (mevcut ajanlar etkilenmez); AÇIKKEN ajan
    X-Agent-Id + X-Agent-Secret göndermeli, yoksa 401. Panel uçları bundan etkilenmez."""
    if not await enforce_agent_auth_enabled():
        return
    hwid = request.headers.get("X-Agent-Id")
    if hwid and await verify_agent_secret(hwid, request.headers.get("X-Agent-Secret")):
        return
    raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Ajan kimlik dogrulamasi gerekli")
# ──────────────────────────────────────────────────────────────────────────────

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
# Yükleme klasörü sabit ve çözümlenmiş (realpath) bir yoldur; kullanıcı girdisinden türetilmez
UPLOAD_DIR = os.path.realpath(os.path.join(BASE_DIR, "storage"))
UPDATES_DIR = os.path.join(BASE_DIR, "updates")

USE_V2_SCHEMA = True
# Ajan loglarının yazıldığı tablo; istatistik ve silme işlemleri de bunu kullanır
LOG_TABLE = "agent_logs_v2" if USE_V2_SCHEMA else "agent_logs"

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

if not os.path.exists(UPLOAD_DIR): os.makedirs(UPLOAD_DIR)
if not os.path.exists(UPDATES_DIR): os.makedirs(UPDATES_DIR)

app.mount("/download", StaticFiles(directory=UPLOAD_DIR), name="download")
app.mount("/updates", StaticFiles(directory=UPDATES_DIR), name="updates")

# Parametreler ayrı verilir; şifredeki '@', ':' gibi karakterler DSN'i bozmaz
DB_CONFIG = {
    "host": os.environ.get('DB_HOST', 'localhost'),
    "port": int(os.environ.get('DB_PORT', '5432')),
    "user": require_env('DB_USER'),
    "password": require_env('DB_PASS'),
    "database": require_env('DB_NAME'),
}

# Wake-on-LAN yayın hedefi ('<broadcast>' = 255.255.255.255)
WOL_BROADCAST_ADDR = os.environ.get('WOL_BROADCAST_ADDR') or '<broadcast>'
WOL_PORT = int(os.environ.get('WOL_PORT', '9'))
db_pool = None

async def execute_query(query: str, params=(), fetch=False):
    async with db_pool.acquire() as conn:
        if fetch:
            records = await conn.fetch(query, *params)
            return [dict(r) for r in records]
        else:
            await conn.execute(query, *params)
            return True


async def log_audit_event(pc_name: str, log_type: str, message: str, actor_id: str="System", event_type: str="system", category: str="legacy", action: str="unknown", risk_level: str="info", reason: str="", meta_data: dict=None):
    if meta_data is None: meta_data = {}
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    if USE_V2_SCHEMA:
        cat = category if category != "legacy" else log_type.lower().replace(" ", "_")
        if risk_level == "info":
            if log_type in ["Error", "Critical Security"]: risk_level = "critical"
            elif log_type in ["Security", "Warning"]: risk_level = "medium"
        await execute_query("""
            INSERT INTO agent_logs_v2 (pc_name, actor_id, event_type, category, action, risk_level, reason, message, meta_data, timestamp)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
        """, (pc_name, actor_id, event_type, cat, action, risk_level, reason, message, json.dumps(meta_data), now))
    else:
        await execute_query("INSERT INTO agent_logs (pc_name, log_type, message, timestamp) VALUES ($1, $2, $3, $4)", (pc_name, log_type, message, now))

# NOT: Veritabani semasi artik yalnizca migration'larla (migrate.py + migrations/NNNN_*.sql)
# kurulur. Yeni tablo/kolon eklerken buraya degil, yeni bir numarali .sql dosyasina yazin.

@app.on_event("startup")
async def startup_event():
    global db_pool
    print("⏳ Veritabanı motoru başlatılıyor...")
    for i in range(5):
        try:
            db_pool = await asyncpg.create_pool(**DB_CONFIG, min_size=5, max_size=100)
            await run_migrations(db_pool)
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
                    (admin_user, hashed)
                )
                print(f"👑 Panel Admin Hesabı Oluşturuldu: {admin_user}")
            else:
                print(f"👑 Panel Admin Hesabı Mevcut: {admin_user}")
            break
        except Exception as e:
            print(f"⚠️ Veritabanı bağlantı hatası (deneme {i+1}/5): {e}")
            await asyncio.sleep(3)

@app.on_event("shutdown")
async def shutdown_event():
    if db_pool:
        await db_pool.close()

def send_wol_packet(mac_address: str):
    try:
        clean_mac = mac_address.replace(":", "").replace("-", "").replace(".", "")
        if len(clean_mac) != 12: return False
        data = bytes.fromhex('FFFFFFFFFFFF' + clean_mac * 16)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            s.sendto(data, (WOL_BROADCAST_ADDR, WOL_PORT))
        return True
    except: return False

class ConnectionManager:
    def __init__(self):
        self.active_agents: Dict[str, WebSocket] = {}
        self.active_panels: List[WebSocket] = []
        self.pending_thumbnails: Dict[str, List[asyncio.Future]] = {}
        self.active_vision_ws: Dict[str, WebSocket] = {}

    async def connect_agent(self, websocket: WebSocket, pc_name: str):
        self.active_agents[pc_name] = websocket

    async def connect_panel(self, websocket: WebSocket):
        await websocket.accept()
        self.active_panels.append(websocket)
        
    async def connect_vision(self, websocket: WebSocket, pc_name: str):
        await websocket.accept()
        self.active_vision_ws[pc_name] = websocket

    def disconnect_agent(self, pc_name: str, websocket: Optional[WebSocket] = None) -> bool:
        """Ajan soketini kayıttan düşürür. websocket verilirse yalnızca kayıtlı soket o ise
        silinir; böylece geç kapanan eski bir bağlantı, yeniden bağlanan ajanın yeni soketini
        silmez. Kayıt gerçekten silindiyse True döner."""
        if websocket is not None and self.active_agents.get(pc_name) is not websocket:
            return False
        removed = self.active_agents.pop(pc_name, None) is not None
        self.active_vision_ws.pop(pc_name, None)
        return removed

    def disconnect_panel(self, websocket: WebSocket):
        if websocket in self.active_panels: self.active_panels.remove(websocket)
            
    def disconnect_vision(self, pc_name: str):
        if pc_name in self.active_vision_ws: del self.active_vision_ws[pc_name]
            
    def rename_agent(self, old_name: str, new_name: str):
        if old_name in self.active_agents: self.active_agents[new_name] = self.active_agents.pop(old_name)
        if old_name in self.active_vision_ws: self.active_vision_ws[new_name] = self.active_vision_ws.pop(old_name)

    async def send_command(self, message: dict, pc_name: str):
        if pc_name in self.active_agents:
            try: await self.active_agents[pc_name].send_text(json.dumps(message))
            except Exception: self.disconnect_agent(pc_name)

    async def broadcast_to_panels(self, message: dict):
        disconnected = []
        for panel in self.active_panels:
            try: await panel.send_text(json.dumps(message))
            except: disconnected.append(panel)
        for p in disconnected: self.disconnect_panel(p)
    
    async def send_remote_input_to_vision(self, message: dict, pc_name: str):
        if pc_name in self.active_vision_ws:
            try:
                await self.active_vision_ws[pc_name].send_text(json.dumps(message))
                return True
            except: self.disconnect_vision(pc_name)
        return False

manager = ConnectionManager()

class AdminLoginInput(BaseModel): username: str; password: str
class TaskInput(BaseModel): target_pc: str; target_lab: str; script_path: str
class MovePcInput(BaseModel): pc_name: str; new_lab: str
class MovePcsInput(BaseModel): pc_names: List[str]; new_lab: str
class RenameLabInput(BaseModel): old_name: str; new_name: str
class RenameDeviceInput(BaseModel): pc_name: str; display_name: str
class CreateLabInput(BaseModel): lab_name: str
class DeleteLabInput(BaseModel): lab_name: str
class SetMainPcInput(BaseModel): lab_name: str; pc_name: str
class SaveLabLayoutInput(BaseModel): lab_name: str; layout_json: str
class AutoEnrollInput(BaseModel): target_lab: str; expire_date: str
class SetLimitInput(BaseModel): limit: int
class TaskSequenceItem(BaseModel): name: str; type: str; command: str
class OrchestrationInput(BaseModel): target_mode: str; targets: List[str]; taskSequence: List[TaskSequenceItem]
class CreatePackageInput(BaseModel): id: str; name: str; type: str; meta: str; command: str; icon: str; color: str
class DeletePackageInput(BaseModel): id: str
class LogInput(BaseModel):
    log_type: Optional[str] = "System"
    message: Optional[str] = ""
    actor_id: Optional[str] = "Agent"
    event_type: Optional[str] = "agent.log"
    category: Optional[str] = "legacy"
    action: Optional[str] = "unknown"
    risk_level: Optional[str] = "info"
    reason: Optional[str] = ""
    meta_data: Optional[dict] = {}
class AuthEventInput(BaseModel): hw_id: str; hostname: str; student_id: str; message: Optional[str] = ""
class TaskActionInput(BaseModel): action: str; target_mode: str; target_id: str
class RemoteInputData(BaseModel): type: str; device: str; input_type: str; data: dict
class HwInventoryInput(BaseModel): hw_id: Optional[str] = None; hostname: Optional[str] = None; cpu: str = "-"; ram: str = "-"; motherboard: str = "-"; gpu: str = "-"; os_version: str = "-"; ip_address: str = "-"; mac_address: str = "-"; disk_info: str = "-"
# admin_id/admin_name/admin_role geriye uyumluluk için kabul edilir ama kullanılmaz; kimlik JWT'den alınır
class StartAuditSessionInput(BaseModel): target_pc: str; reason: str; is_mandatory: bool; admin_id: Optional[int] = None; admin_name: Optional[str] = None; admin_role: Optional[str] = None
class EndAuditSessionInput(BaseModel): session_id: str; status: str
class LockdownInput(BaseModel): target_pc: str; reason: str; admin_name: Optional[str] = None

class UserCreateInput(BaseModel): username: str; password: str; role: str; permissions: str
class UserUpdateInput(BaseModel): username: str; password: Optional[str] = None; role: str; permissions: str

class AgentPoliciesInput(BaseModel):
    fair_use_text: str
    dns_categories: list
    auto_quarantine: bool
    quarantine_threshold: int

class PolicyAlertInput(BaseModel):
    hw_id: str
    domain: str
    category: str

@app.post("/api/admin/login")
@limiter.limit("10/minute")
async def admin_login(request: Request, data: AdminLoginInput):
    user = await execute_query(
        "SELECT id, username, role, permissions, password_hash FROM users WHERE username = $1",
        (data.username,), fetch=True
    )
    if not user:
        # Sabit süre bekleyerek timing attack'ı engelle
        bcrypt.checkpw(b'dummy', bcrypt.hashpw(b'dummy', bcrypt.gensalt()))
        raise HTTPException(status_code=401, detail="Geçersiz kullanıcı adı veya şifre")
    
    u = user[0]
    stored_hash = u['password_hash']
    
    # Yalnızca bcrypt kabul edilir. Tuzsuz SHA256 özetleri ve '!disabled' gibi
    # geçersiz değerler bcrypt'te ValueError verir; bu hesaplar giriş yapamaz.
    try:
        valid = bcrypt.checkpw(data.password.encode(), stored_hash.encode())
    except ValueError:
        valid = False

    if not valid:
        raise HTTPException(status_code=401, detail="Geçersiz kullanıcı adı veya şifre")
    
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await execute_query("UPDATE users SET last_login=$1 WHERE id=$2", (now, u['id']))
    
    token = create_jwt(u['username'], u['role'])
    return {
        "status": "success",
        "message": "Giriş Başarılı",
        "role": u['role'],
        "username": u['username'],
        "permissions": u.get('permissions', '[]'),
        "token": token
    }

@app.get("/api/admin/users")
async def get_users(auth=Depends(require_admin)):
    users = await execute_query("SELECT id, username, role, last_login, permissions FROM users ORDER BY id ASC", fetch=True)
    return {"status": "success", "users": users}

VALID_ROLES = ('superadmin', 'admin', 'viewer')

def _clean_user_fields(username: str, role: str, permissions: str) -> tuple:
    """Kullanıcı alanlarını doğrular; hatada 400 döner. Yetki listesi JSON dizisi olarak normalize edilir."""
    username = (username or '').strip()
    if not username:
        raise HTTPException(status_code=400, detail="Kullanıcı adı boş olamaz.")
    if role not in VALID_ROLES:
        raise HTTPException(status_code=400, detail="Geçersiz rol.")
    try:
        perms = json.loads(permissions or '[]')
    except ValueError:
        perms = None
    if not isinstance(perms, list) or not all(isinstance(p, str) for p in perms):
        raise HTTPException(status_code=400, detail="Yetki listesi geçerli bir JSON dizisi olmalı.")
    return username, json.dumps(perms)

async def _superadmin_count(exclude_id: Optional[int] = None) -> int:
    rows = await execute_query("SELECT COUNT(*) AS c FROM users WHERE role = 'superadmin' AND password_hash LIKE '$2%' AND id IS DISTINCT FROM $1",
                               (exclude_id,), fetch=True)
    return rows[0]["c"] if rows else 0

# Kullanıcı oluşturma, düzenleme ve silme yalnızca superadmin'e açıktır;
# aksi halde bir admin kendine superadmin hesabı açabilirdi.
@app.post("/api/admin/users")
async def create_user(data: UserCreateInput, auth=Depends(require_superadmin)):
    username, permissions = _clean_user_fields(data.username, data.role, data.permissions)
    if not data.password:
        raise HTTPException(status_code=400, detail="Şifre boş olamaz.")
    hashed_pw = bcrypt.hashpw(data.password.encode(), bcrypt.gensalt()).decode()
    try:
        await execute_query("INSERT INTO users (username, password_hash, role, permissions) VALUES ($1, $2, $3, $4)",
                           (username, hashed_pw, data.role, permissions))
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail="Bu kullanıcı adı zaten var.")
    return {"status": "success", "message": "Kullanıcı başarıyla oluşturuldu."}

@app.put("/api/admin/users/{user_id}")
async def update_user(user_id: int, data: UserUpdateInput, auth=Depends(require_superadmin)):
    username, permissions = _clean_user_fields(data.username, data.role, data.permissions)
    current = await execute_query("SELECT role FROM users WHERE id=$1", (user_id,), fetch=True)
    if not current:
        raise HTTPException(status_code=404, detail="Kullanıcı bulunamadı.")
    if current[0]["role"] == 'superadmin' and data.role != 'superadmin' and await _superadmin_count(exclude_id=user_id) == 0:
        raise HTTPException(status_code=400, detail="Son superadmin hesabının rolü düşürülemez.")
    try:
        if data.password:
            hashed_pw = bcrypt.hashpw(data.password.encode(), bcrypt.gensalt()).decode()
            await execute_query("UPDATE users SET username=$1, password_hash=$2, role=$3, permissions=$4 WHERE id=$5",
                               (username, hashed_pw, data.role, permissions, user_id))
        else:
            await execute_query("UPDATE users SET username=$1, role=$2, permissions=$3 WHERE id=$4",
                               (username, data.role, permissions, user_id))
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail="Bu kullanıcı adı zaten var.")
    return {"status": "success", "message": "Kullanıcı başarıyla güncellendi."}

@app.delete("/api/admin/users/{user_id}")
async def delete_user(user_id: int, auth=Depends(require_superadmin)):
    target = await execute_query("SELECT username, role FROM users WHERE id=$1", (user_id,), fetch=True)
    if not target:
        raise HTTPException(status_code=404, detail="Kullanıcı bulunamadı.")
    if target[0]["username"] == auth.get('sub'):
        raise HTTPException(status_code=400, detail="Kendi hesabınızı silemezsiniz.")
    if target[0]["role"] == 'superadmin' and await _superadmin_count(exclude_id=user_id) == 0:
        raise HTTPException(status_code=400, detail="Son superadmin hesabı silinemez.")
    await execute_query("DELETE FROM users WHERE id=$1", (user_id,))
    return {"status": "success", "message": "Kullanıcı silindi."}

@app.delete("/api/devices/{pc_name}")
async def delete_device(pc_name: str, auth: dict = Depends(require_admin)):
    try:
        await execute_query("DELETE FROM clients WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM hw_inventory WHERE pc_name = $1", (pc_name,))
        await execute_query(f"DELETE FROM {LOG_TABLE} WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM agent_versions WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM agent_secrets WHERE pc_name = $1", (pc_name,))
        # Cihaz çevrimiçiyse ajan bağlantısını da kapat
        agent_ws = manager.active_agents.get(pc_name)
        manager.disconnect_agent(pc_name)
        if agent_ws:
            try: await agent_ws.close(code=4000, reason="Cihaz silindi")
            except Exception: pass
        return {"status": "success", "message": f"{pc_name} silindi."}
    except Exception as e:
        return {"status": "error", "message": str(e)}

@app.post("/api/audit/session/start")
async def start_audit_session(data: StartAuditSessionInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    session_id = f"SES-{secrets.token_hex(6).upper()}"
    # Rıza sorulmadan açılan (zorunlu) oturum için gerekçe şarttır
    if data.is_mandatory and not data.reason.strip():
        raise HTTPException(status_code=400, detail="Zorunlu oturum için gerekçe yazılmalıdır.")
    admin_name, admin_role = auth.get('sub'), auth.get('role')
    admin_row = await execute_query("SELECT id FROM users WHERE username = $1", (admin_name,), fetch=True)
    admin_id = admin_row[0]["id"] if admin_row else None

    await execute_query("""
        INSERT INTO enterprise_audit_logs
        (session_id, admin_id, admin_name, admin_role, target_pc, start_time, end_time, reason, is_notified, is_mandatory, status)
        VALUES ($1, $2, $3, $4, $5, $6, NULL, $7, TRUE, $8, 'Active')
    """, (session_id, admin_id, admin_name, admin_role, data.target_pc, now, data.reason, data.is_mandatory))
    # Karantina durumunu kontrol et
    rows = await execute_query("SELECT is_quarantined FROM clients WHERE pc_name = $1", (data.target_pc,), fetch=True)
    is_quarantined = False
    if rows and len(rows) > 0:
        is_quarantined = rows[0].get("is_quarantined", False)
        
    countdown = 5 if is_quarantined else 30
    if not data.is_mandatory:
        countdown = 0

    # Hedef PC'ye bağlantı komutunu (token ile birlikte) gönder
    payload = {
        "action": "start_vision_session",
        "session_id": session_id,
        "is_mandatory": data.is_mandatory,
        "admin_name": admin_name,
        "reason": data.reason,
        "countdown_seconds": countdown,
        "is_quarantined": is_quarantined
    }
    await manager.send_command(payload, data.target_pc)
    
    return {"status": "success", "session_id": session_id, "countdown_seconds": countdown}

@app.post("/api/audit/session/end")
async def end_audit_session(data: EndAuditSessionInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await execute_query("UPDATE enterprise_audit_logs SET end_time = $1, status = $2 WHERE session_id = $3", (now, data.status, data.session_id))
    return {"status": "success"}

@app.post("/api/security/lockdown")
async def lockdown_pc(data: LockdownInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    
    # Karantina logunu yaz
    admin_name = auth.get('sub')
    await log_audit_event(data.target_pc, "Critical Security", f"🚨 KARANTİNA BAŞLATILDI by {admin_name} - Neden: {data.reason}", actor_id=admin_name, event_type="security.lockdown", category="security", action="lockdown", risk_level="critical", reason=data.reason)
    
    # Cihazı karantina moduna al
    await execute_query("UPDATE clients SET is_quarantined = TRUE WHERE pc_name = $1", (data.target_pc,))
    
    # Ajanı kilitleme emri gönder
    await manager.send_command({"action": "lockdown", "reason": data.reason}, data.target_pc)
    
    return {"status": "success", "message": "Karantina sinyali gönderildi."}

@app.post("/api/security/unlock")
async def unlock_pc(data: LockdownInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    
    # Karantina logunu yaz
    admin_name = auth.get('sub')
    await log_audit_event(data.target_pc, "Critical Security", f"✅ KARANTİNA KALDIRILDI by {admin_name} - Neden: {data.reason}", actor_id=admin_name, event_type="security.unlock", category="security", action="unlock", risk_level="info", reason=data.reason)
    
    # Cihazı karantina modundan çıkar
    await execute_query("UPDATE clients SET is_quarantined = FALSE WHERE pc_name = $1", (data.target_pc,))
    
    # Ajanı kilit açma emri gönder
    await manager.send_command({"action": "unlock"}, data.target_pc)
    
    return {"status": "success", "message": "Karantina kaldırma sinyali gönderildi."}

def offline_bypass_code(hw_id: str, day: datetime.date) -> str:
    """Ajanın ağ bağlantısı olmadan doğruladığı günlük 6 haneli bypass kodu.

    Formül POpsAgent Worker.cs (UNLOCK_BYPASS) ile aynıdır: SHA-256(hw_id + BYPASS_SECRET + yyyy-MM-dd).
    Kod sunucunun yerel tarihine göre üretilir; sunucu ve ajanlar aynı saat diliminde olmalıdır.
    """
    raw = f"{hw_id}{BYPASS_SECRET}{day.strftime('%Y-%m-%d')}"
    return hashlib.sha256(raw.encode('utf-8')).hexdigest()[:6].upper()

@app.get("/api/security/bypass_token/{pc_name}")
async def get_bypass_token(pc_name: str, auth: dict = Depends(require_admin)):
    # Karantinadaki (çevrimdışı) cihaz için tepsi uygulamasına girilecek kod
    if not BYPASS_SECRET:
        return {"status": "error", "message": "BYPASS_SECRET tanımlı değil (bkz. .env.example)"}
    today = datetime.date.today()
    token = offline_bypass_code(pc_name, today)
    await log_audit_event(pc_name, "Security", "🔑 Çevrimdışı bypass kodu üretildi", actor_id=auth.get('sub', 'admin'), event_type="security.bypass_code", category="security", action="bypass_code", risk_level="medium")
    return {"status": "success", "token": token, "valid_for": today.isoformat()}

@app.post("/api/auth/login")
async def auth_login(data: AuthEventInput, _auth=Depends(agent_http_auth)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await log_audit_event(data.hw_id, "Security", f"🟢 GİRİŞ: {data.student_id}", actor_id=data.student_id, event_type="auth.login", category="security", action="login", risk_level="info")
    await execute_query("UPDATE clients SET logged_user=$1 WHERE pc_name=$2", (data.student_id, data.hw_id))
    return {"status": "success"}

@app.post("/api/auth/failed")
async def auth_failed(data: AuthEventInput, _auth=Depends(agent_http_auth)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await log_audit_event(data.hw_id, "Security", f"🔴 RED: {data.student_id} ({data.message})", actor_id=data.student_id, event_type="auth.failed", category="security", action="login_failed", risk_level="medium", reason=data.message)
    return {"status": "success"}

@app.post("/api/auth/logout")
async def auth_logout(data: AuthEventInput, _auth=Depends(agent_http_auth)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await log_audit_event(data.hw_id, "Security", "⚪ OTURUM KAPATILDI", actor_id="System", event_type="auth.logout", category="security", action="logout", risk_level="info")
    await execute_query("UPDATE clients SET logged_user='-' WHERE pc_name=$1", (data.hw_id,))
    return {"status": "success"}

async def process_queue():
    limit_row = await execute_query("SELECT value FROM global_settings WHERE key = 'concurrent_limit'", fetch=True)
    limit = int(limit_row[0]["value"]) if limit_row else 5
    running_row = await execute_query("SELECT COUNT(DISTINCT target_pc) as c FROM tasks WHERE status = 'Running'", fetch=True)
    running_pcs_count = running_row[0]["c"] if running_row else 0
    available_slots = limit - running_pcs_count

    if available_slots > 0 or limit == 0:
        online_pcs = list(manager.active_agents.keys())
        if online_pcs:
            busy_rows = await execute_query("SELECT DISTINCT target_pc FROM tasks WHERE status = 'Running'", fetch=True)
            busy_pcs = [r["target_pc"] for r in (busy_rows or [])]
            idle_online_pcs = [pc for pc in online_pcs if pc not in busy_pcs]

            for pc in idle_online_pcs:
                if limit > 0 and available_slots <= 0: break
                task_row = await execute_query("SELECT * FROM tasks WHERE status = 'Pending' AND target_pc = $1 ORDER BY id ASC LIMIT 1", (pc,), fetch=True)
                if task_row:
                    task = task_row[0]
                    await execute_query("UPDATE tasks SET status = 'Running' WHERE id = $1", (task["id"],))
                    await manager.send_command({"action": "execute", "task_id": task["id"], "script_path": task["script_path"]}, pc)
                    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
                    await log_audit_event(pc, "Deploy", f"Görev: {task['script_path'][:50]}", actor_id="System/Queue", event_type="deploy.execution", category="system_maintenance", action="execute_queue", risk_level="info", meta_data={"raw_command": task["script_path"]})
                    available_slots -= 1

async def add_audit_log(hw_id, action, reason, changes):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await execute_query("INSERT INTO device_audit_logs (hw_id, action, reason, changes, timestamp) VALUES ($1, $2, $3, $4, $5)", (hw_id, action, reason, json.dumps(changes, ensure_ascii=False), now))

def calculate_dna_score(incoming_hw, db_hw, incoming_caps, db_caps):
    if incoming_hw.get('uuid') in ["NULL", "-"] and incoming_hw.get('mac') in ["00:00:00:00:00:00", "-", "NULL"]: return 11, 11 
    score = 0
    max_score = 11
    if incoming_hw.get('uuid') != "NULL" and incoming_hw.get('uuid') == db_hw.get('dna_uuid'): score += 4
    if incoming_hw.get('bios_sn') != "NULL" and incoming_hw.get('bios_sn') == db_hw.get('dna_bios'): score += 3
    if incoming_hw.get('disk_sn') != "NULL" and incoming_hw.get('disk_sn') == db_hw.get('dna_disk'): score += 2
    if incoming_hw.get('mac') != "NULL" and incoming_hw.get('mac') == db_hw.get('dna_mac'): score += 1
    db_ram_readable = db_caps.get('cap_ram_readable', True) if db_caps else True
    inc_ram_readable = incoming_caps.get('ram_readable', True)
    if not db_ram_readable: max_score = 10
    else:
        if inc_ram_readable and incoming_hw.get('ram_sn') != "NULL" and incoming_hw.get('ram_sn') == db_hw.get('dna_ram'): score += 1
    return score, max_score

async def reconcile_device(claimed_hwid: str, dna_payload: dict, client_ip: str, ws: WebSocket):
    hw = dna_payload.get("hardware", {})
    caps = dna_payload.get("capabilities", {})
    existing_pc = await execute_query("SELECT * FROM clients WHERE pc_name = $1", (claimed_hwid,), fetch=True)
    if existing_pc:
        db_record = existing_pc[0]
        score, max_score = calculate_dna_score(hw, db_record, caps, db_record)
        threshold = 5.5 if hw.get('uuid') != "NULL" and hw.get('uuid') == db_record.get('dna_uuid') and hw.get('bios_sn') == db_record.get('dna_bios') else 6
        if score >= threshold:
            await execute_query("UPDATE clients SET dna_uuid=$1, dna_bios=$2, dna_disk=$3, dna_mac=$4, dna_ram=$5, cap_ram_readable=$6 WHERE pc_name=$7", 
                                (hw.get('uuid'), hw.get('bios_sn'), hw.get('disk_sn'), hw.get('mac'), hw.get('ram_sn'), caps.get('ram_readable', True), claimed_hwid))
            return claimed_hwid
        else:
            new_hwid = "HW-" + hashlib.md5((hw.get('uuid', '') + hw.get('mac', '') + str(datetime.datetime.now().timestamp())).encode()).hexdigest()[:12].upper()
            await add_audit_log(claimed_hwid, "CLONE_DETECTED", f"Skor: {score}/{max_score}", {"old_hw": db_record.get('dna_uuid'), "new_hw": hw.get('uuid')})
            await ws.send_text(json.dumps({"action": "set_identity", "new_hw_id": new_hwid}))
            return new_hwid
    else:
        all_pcs = await execute_query("SELECT * FROM clients", fetch=True)
        best_match, best_score, best_max = None, 0, 11
        for pc in all_pcs:
            s, m = calculate_dna_score(hw, pc, caps, pc)
            if s > best_score: best_score, best_max, best_match = s, m, pc
        if best_match and best_score >= 6:
            real_hwid = best_match["pc_name"]
            await add_audit_log(real_hwid, "RECOVERED_IDENTITY", f"Kurtarıldı: {best_score}/{best_max}", {"temp_id": claimed_hwid})
            await ws.send_text(json.dumps({"action": "set_identity", "new_hw_id": real_hwid}))
            return real_hwid
        else:
            await add_audit_log(claimed_hwid, "NEW_DEVICE", "Yeni Cihaz", hw)
            return claimed_hwid

@app.websocket("/ws/panel")
async def websocket_panel(websocket: WebSocket):
    if not ws_check_token(websocket.cookies.get(JWT_COOKIE_NAME)):
        await websocket.accept()
        await websocket.close(code=4001, reason="Kimlik doğrulama hatası")
        return
    await manager.connect_panel(websocket)
    try:
        while True: 
            data = await websocket.receive_text()
            try:
                payload = json.loads(data)
                if payload.get("type") == "remote_input":
                    target = payload.get("device")
                    if target:
                        sent = await manager.send_remote_input_to_vision(payload, target)
                        if not sent: await manager.send_command(payload, target)
                elif payload.get("type") == "ping":
                    await websocket.send_text(json.dumps({"type": "pong"}))
            except json.JSONDecodeError: pass
    except WebSocketDisconnect:
        manager.disconnect_panel(websocket)

@app.websocket("/ws/vision/{pc_name}")
async def websocket_vision(websocket: WebSocket, pc_name: str):
    await websocket.accept()
    # Faz 3 accept-both: enforce açıkken kimliksiz vision tüneli reddedilir (sahte ekran engellenir)
    if await enforce_agent_auth_enabled() and not (
        await verify_agent_secret(pc_name, websocket.headers.get("X-Agent-Secret"))
        or await valid_enroll_token(websocket.headers.get("X-Enroll-Token"))
    ):
        await add_audit_log(pc_name, "auth_reject", "Kimliksiz vision baglantisi reddedildi (enforce acik)", {})
        await websocket.close(code=4401, reason="Ajan kimlik dogrulamasi gerekli")
        return
    manager.active_vision_ws[pc_name] = websocket
    try:
        while True:
            data = await websocket.receive_text()
            try:
                payload = json.loads(data)
                if payload.get("type") in ["stream_frame", "thumbnail"]:
                    await manager.broadcast_to_panels(payload)
            except json.JSONDecodeError: pass
    except WebSocketDisconnect:
        manager.disconnect_vision(pc_name)

@app.websocket("/ws/agent/{pc_name}")
async def websocket_agent(websocket: WebSocket, pc_name: str):
    await websocket.accept()
    forwarded = websocket.headers.get("X-Forwarded-For")
    client_ip = forwarded.split(",")[0] if forwarded else (websocket.client.host if websocket.client else "Bilinmiyor")
    active_hwid = pc_name
    agent_version = websocket.headers.get("X-Agent-Version", "unknown")

    # ── Faz 3 kimlik doğrulama (accept-both) ──
    # Secret ya da geçerli enroll token varsa kimlikli; hiçbiri yoksa "legacy".
    # enforce_agent_auth KAPALIYKEN legacy bağlantı KABUL edilir (mevcut ajan düşmez);
    # AÇIKKEN reddedilir. Değerlendirme reconcile'dan önce, URL pc_name'e karşı yapılır.
    auth_method = "none"
    pending_enroll = None
    if await verify_agent_secret(active_hwid, websocket.headers.get("X-Agent-Secret")):
        auth_method = "secret"
    else:
        pending_enroll = await valid_enroll_token(websocket.headers.get("X-Enroll-Token"))
        if pending_enroll:
            auth_method = "enroll"
    if auth_method == "none" and await enforce_agent_auth_enabled():
        # Kimliksiz: audit'i ajanların YAZAMADIĞI device_audit_logs'a düş, sonra reddet.
        await add_audit_log(active_hwid, "auth_reject",
                            "Kimliksiz ajan bağlantısı reddedildi (enforce açık)",
                            {"ip": client_ip, "agent_version": agent_version})
        await websocket.close(code=4401, reason="Ajan kimlik dogrulamasi gerekli")
        return

    manager.active_agents[active_hwid] = websocket

    async def handle_routine_payload(pld):
        current_time = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        current_hostname = pld.get("hostname", active_hwid)
        if pld.get("type") == "result":
            # Ajan yalnızca kendisine atanmış görevin sonucunu yazabilir
            await execute_query("UPDATE tasks SET status = 'Completed', output = $1 WHERE id = $2 AND target_pc = $3", (pld.get("output"), pld.get("task_id"), active_hwid))
            await manager.broadcast_to_panels({"type": "terminal_output", "id": active_hwid, "pc_name": current_hostname, "output": pld.get("output"), "task_id": pld.get("task_id")})
            await process_queue()
            return
        if "status" in pld:
            await execute_query("UPDATE clients SET last_seen=$1, status=$2, active_window=$3, hostname=$4, ip_address=$5 WHERE pc_name=$6", 
                                (current_time, pld.get("status"), pld.get("active_window", "-"), current_hostname, client_ip, active_hwid))

    try:
        data = await websocket.receive_text()
        payload = json.loads(data)
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")

        if "dna_payload" in payload:
            verified_hwid = await reconcile_device(active_hwid, payload.get("dna_payload"), client_ip, websocket)
            if verified_hwid != active_hwid:
                manager.rename_agent(active_hwid, verified_hwid)
                # Enrolled secret'ı çözümlenen yeni kimliğe taşı (hedefte yoksa)
                await execute_query(
                    "UPDATE agent_secrets SET pc_name=$1 WHERE pc_name=$2 "
                    "AND NOT EXISTS (SELECT 1 FROM agent_secrets WHERE pc_name=$1)",
                    (verified_hwid, active_hwid))
                active_hwid = verified_hwid
            hw = payload.get("dna_payload", {}).get("hardware", {})
            caps = payload.get("dna_payload", {}).get("capabilities", {})
            real_hostname = payload.get("hostname", active_hwid)

            await execute_query("UPDATE tasks SET status = 'Completed (Rebooted)' WHERE target_pc = $1 AND status = 'Running'", (active_hwid,))
            await execute_query("""
                INSERT INTO clients (pc_name, hostname, lab_name, last_seen, status, active_window, boot_count, ip_address, dna_uuid, dna_bios, dna_disk, dna_mac, dna_ram, cap_ram_readable) 
                VALUES ($1, $2, 'Atanmamis_Cihazlar', $3, 'Online', '-', 1, $4, $5, $6, $7, $8, $9, $10)
                ON CONFLICT (pc_name) DO UPDATE SET status='Online', last_seen=$3, ip_address=$4, boot_count=clients.boot_count + 1, hostname=$2, dna_uuid=$5, dna_bios=$6, dna_disk=$7, dna_mac=$8, dna_ram=$9, cap_ram_readable=$10
            """, (active_hwid, real_hostname, now, client_ip, hw.get('uuid'), hw.get('bios_sn'), hw.get('disk_sn'), hw.get('mac'), hw.get('ram_sn'), caps.get('ram_readable', True)))
            
            await execute_query("INSERT INTO agent_versions (pc_name, version, last_update) VALUES ($1, $2, $3) ON CONFLICT (pc_name) DO UPDATE SET version=$2, last_update=$3", (active_hwid, agent_version, now))

            # Enroll token ile bağlandıysa: tüket, kalıcı secret üret+sakla, ajana gönder, laba ata.
            if auth_method == "enroll" and pending_enroll:
                new_secret = secrets.token_urlsafe(32)
                await execute_query(
                    "INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2) "
                    "ON CONFLICT (pc_name) DO UPDATE SET secret_hash=$2, rotated_at=NOW()",
                    (active_hwid, _hash_secret(new_secret)))
                await execute_query(
                    "UPDATE enroll_tokens SET use_count = use_count + 1, "
                    "is_used = (use_count + 1 >= max_uses), used_by=$1, used_at=NOW() "
                    "WHERE id=$2 AND NOT is_used AND use_count < max_uses",
                    (active_hwid, pending_enroll["id"]))
                if pending_enroll.get("lab_name"):
                    await execute_query("UPDATE clients SET lab_name=$1 WHERE pc_name=$2",
                                        (pending_enroll["lab_name"], active_hwid))
                await add_audit_log(active_hwid, "enroll", "Ajan enroll token ile kaydoldu",
                                    {"lab": pending_enroll.get("lab_name"), "ip": client_ip})
                try:
                    await websocket.send_text(json.dumps({"action": "set_secret", "secret": new_secret}))
                except Exception:
                    pass
                pending_enroll = None
                auth_method = "secret"

            hw_exists = await execute_query("SELECT cpu FROM hw_inventory WHERE pc_name = $1", (active_hwid,), fetch=True)
            if not hw_exists or hw_exists[0]["cpu"] == "-": await manager.send_command({"action": "get_hardware"}, active_hwid)
            await process_queue()

        await handle_routine_payload(payload)

        while True:
            data = await websocket.receive_text()
            payload = json.loads(data)
            if payload.get("type") == "thumbnail":
                hwid = payload.get("hw_id")
                if hwid in manager.pending_thumbnails:
                    for fut in manager.pending_thumbnails[hwid]:
                        if not fut.done(): fut.set_result(payload.get("image", ""))
                    manager.pending_thumbnails[hwid] = []
                await manager.broadcast_to_panels(payload)
                continue
            if payload.get("type") == "vision_rejected":
                await manager.broadcast_to_panels(payload)
                continue
            if payload.get("type") == "update_result":
                # Ajanın güncelleme sonucu (POpsUpdater update-result.json'ından). Ajanların
                # yazamadığı device_audit_logs'a düşür + panele bildir.
                detail = {k: payload.get(k) for k in ("status", "from_version", "to_version", "detail")}
                await add_audit_log(active_hwid, "update_result",
                                    f"Ajan guncelleme sonucu: {payload.get('status', '?')}", detail)
                await manager.broadcast_to_panels({"type": "update_result", "pc_name": active_hwid, **detail})
                continue
            await handle_routine_payload(payload)
    except WebSocketDisconnect:
        pass
    except Exception as e:
        # Bozuk mesaj veya beklenmeyen hata: soket kapansın ki cihaz yanlışlıkla Online görünmesin
        print(f"⚠️ /ws/agent/{active_hwid} hata: {e}")
        try:
            await websocket.close(code=1011)
        except Exception:
            pass
    finally:
        # Yeniden bağlanan ajanın yeni soketi kayıtlıysa ona dokunulmaz
        if manager.disconnect_agent(active_hwid, websocket):
            await execute_query("UPDATE clients SET status = 'Offline' WHERE pc_name = $1", (active_hwid,))

@app.get("/api/tasks")
async def get_tasks(limit: int = 1000, auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM tasks ORDER BY id DESC LIMIT $1", (limit,), fetch=True)
    return rows if rows else []

@app.post("/api/flush_queue")
async def flush_queue(auth: dict = Depends(require_admin)):
    await execute_query("DELETE FROM tasks")
    return {"status": "success"}

@app.post("/api/tasks/action")
async def handle_task_action(data: TaskActionInput, auth: dict = Depends(require_admin)):
    action = data.action.upper()
    mode = data.target_mode.upper()
    tid = data.target_id
    new_status = {"CANCEL": "Cancelled", "RETRY": "Pending", "PAUSE": "Paused", "RESUME": "Pending"}.get(action)
    status_condition = "1=1" if action == "RETRY" else "status IN ('Pending', 'Running', 'Paused')"
    
    if mode == "TASK": await execute_query(f"UPDATE tasks SET status = $1 WHERE id = $2 AND {status_condition}", (new_status, int(tid)))
    elif mode == "LAB": await execute_query(f"UPDATE tasks SET status = $1 WHERE target_lab = $2 AND {status_condition}", (new_status, tid))
    elif mode == "PC": await execute_query(f"UPDATE tasks SET status = $1 WHERE target_pc = $2 AND {status_condition}", (new_status, tid))
    elif mode == "ALL": await execute_query(f"UPDATE tasks SET status = $1 WHERE {status_condition}", (new_status,))
    if action in ["RESUME", "RETRY"]: await process_queue()
    return {"status": "success"}

async def attempt_p2p_wol(mac_address: str, lab_name: str):
    # Veritabanı durumu 'Online' olarak yazılır; ayrıca soketi gerçekten açık olan bir eş seçilir
    peers = await execute_query("SELECT pc_name FROM clients WHERE lab_name = $1 AND status = 'Online'", (lab_name,), fetch=True)
    for peer in peers or []:
        peer_name = peer["pc_name"]
        if peer_name in manager.active_agents:
            await manager.send_command({"action": "wake_peer", "mac": mac_address}, peer_name)
            return True
    return False

@app.post("/api/wake_pc/{pc_name}")
async def wake_pc(pc_name: str, auth: dict = Depends(require_admin)):
    row = await execute_query("SELECT c.lab_name, h.mac_address FROM clients c LEFT JOIN hw_inventory h ON c.pc_name = h.pc_name WHERE c.pc_name = $1", (pc_name,), fetch=True)
    if not row or not row[0]["mac_address"] or row[0]["mac_address"] == "-":
        return {"status": "error", "message": "MAC adresi bulunamadı."}
    mac = row[0]["mac_address"]
    lab_name = row[0]["lab_name"]
    send_wol_packet(mac)
    if lab_name and lab_name != "Atanmamis_Cihazlar":
        await attempt_p2p_wol(mac, lab_name)
    return {"status": "success", "message": "WOL gönderildi."}

@app.post("/api/wake_lab/{lab_name}")
async def wake_lab(lab_name: str, auth: dict = Depends(require_admin)):
    rows = await execute_query("SELECT hw_inventory.mac_address FROM hw_inventory JOIN clients ON hw_inventory.pc_name = clients.pc_name WHERE clients.lab_name = $1", (lab_name,), fetch=True)
    count = 0
    for r in (rows or []):
        mac = r["mac_address"]
        if mac and mac != "-":
            send_wol_packet(mac)
            await attempt_p2p_wol(mac, lab_name)
            count += 1
    return {"status": "success", "woken_pcs": count}

@app.post("/api/wake_all")
async def wake_all(auth: dict = Depends(require_admin)):
    rows = await execute_query("SELECT c.lab_name, h.mac_address FROM hw_inventory h JOIN clients c ON h.pc_name = c.pc_name", fetch=True)
    count = 0
    for r in (rows or []):
        mac = r["mac_address"]
        lab = r["lab_name"]
        if mac and mac != "-":
            send_wol_packet(mac)
            if lab and lab != "Atanmamis_Cihazlar":
                await attempt_p2p_wol(mac, lab)
            count += 1
    return {"status": "success", "woken_pcs": count}

@app.get("/api/devices")
async def get_devices(auth: dict = Depends(require_auth)):
    query = """
    SELECT 
        c.pc_name, c.hostname, c.display_name, c.lab_name, c.last_seen, c.status, c.active_window,
        c.boot_count, c.logged_user, c.ip_address, c.cap_ram_readable, c.is_quarantined,
        av.version AS agent_version
    FROM clients c
    LEFT JOIN agent_versions av ON c.pc_name = av.pc_name
    """
    rows = await execute_query(query, fetch=True)
    return [
        {
            "hostname": r["pc_name"], 
            "real_hostname": r["hostname"] or r["pc_name"], 
            "display_name": r["display_name"],
            "pc_name": r["hostname"] or r["pc_name"],
            "hw_id": r["pc_name"],
            "ip": r["ip_address"],
            "lab": r["lab_name"], 
            "status": r["status"], 
            "last_seen": r["last_seen"], 
            "active_window": r["active_window"], 
            "boot_count": r["boot_count"], 
            "current_user": r.get("logged_user", "-"), 
            "is_quarantined": r.get("is_quarantined", False),
            "agent_version": r.get("agent_version") or "Bilinmiyor"
        } 
        for r in (rows or [])
    ]

@app.post("/api/inventory/{pc_name}")
async def update_inventory(pc_name: str, data: HwInventoryInput, _auth=Depends(agent_http_auth)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await execute_query('''INSERT INTO hw_inventory (pc_name, hostname, cpu, ram, motherboard, gpu, os_version, ip_address, mac_address, disk_info, last_updated) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) ON CONFLICT (pc_name) DO UPDATE SET hostname=EXCLUDED.hostname, cpu=EXCLUDED.cpu, ram=EXCLUDED.ram, motherboard=EXCLUDED.motherboard, gpu=EXCLUDED.gpu, os_version=EXCLUDED.os_version, ip_address=EXCLUDED.ip_address, mac_address=EXCLUDED.mac_address, disk_info=EXCLUDED.disk_info, last_updated=EXCLUDED.last_updated''', (pc_name, data.hostname, data.cpu, data.ram, data.motherboard, data.gpu, data.os_version, data.ip_address, data.mac_address, data.disk_info, now))
    return {"status": "success"}

@app.get("/api/inventory")
async def get_all_inventory(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM hw_inventory", fetch=True)
    return rows if rows else []

@app.post("/api/logs/{pc_name}")
async def add_log(pc_name: str, data: LogInput, _auth=Depends(agent_http_auth)):
    await log_audit_event(
        pc_name=pc_name, log_type=data.log_type or "System", message=data.message or "", 
        actor_id=data.actor_id or "Agent", event_type=data.event_type or "agent.log", 
        category=data.category or "legacy", action=data.action or "unknown", 
        risk_level=data.risk_level or "info", reason=data.reason or "", meta_data=data.meta_data or {}
    )
    return {"status": "success"}

@app.get("/api/logs")
async def get_all_logs(limit: int = 1000, auth: dict = Depends(require_auth)):
    if USE_V2_SCHEMA:
        rows = await execute_query("SELECT * FROM agent_logs_v2 ORDER BY id DESC LIMIT $1", (limit,), fetch=True)
    else:
        rows = await execute_query("SELECT * FROM agent_logs ORDER BY id DESC LIMIT $1", (limit,), fetch=True)
    return rows if rows else []

@app.post("/api/create_lab")
async def create_lab(data: CreateLabInput, auth: dict = Depends(require_admin)):
    await execute_query("INSERT INTO custom_labs (lab_name) VALUES ($1) ON CONFLICT DO NOTHING", (data.lab_name,))
    return {"status": "success"}

@app.get("/api/custom_labs")
async def get_custom_labs(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT lab_name FROM custom_labs", fetch=True)
    return [row["lab_name"] for row in (rows or [])]

@app.post("/api/rename_lab")
async def rename_lab(data: RenameLabInput, auth: dict = Depends(require_admin)):
    # Oturma planı (lab_settings) ve görev kayıtları da yeni ada taşınır; hepsi tek işlemde
    async with db_pool.acquire() as conn:
        async with conn.transaction():
            await conn.execute("UPDATE clients SET lab_name = $1 WHERE lab_name = $2", data.new_name, data.old_name)
            await conn.execute("UPDATE custom_labs SET lab_name = $1 WHERE lab_name = $2", data.new_name, data.old_name)
            await conn.execute("""UPDATE lab_settings SET lab_name = $1 WHERE lab_name = $2
                                  AND NOT EXISTS (SELECT 1 FROM lab_settings WHERE lab_name = $1)""", data.new_name, data.old_name)
            await conn.execute("UPDATE tasks SET target_lab = $1 WHERE target_lab = $2", data.new_name, data.old_name)
    return {"status": "success"}

@app.post("/api/rename_device")
async def rename_device(data: RenameDeviceInput, auth: dict = Depends(require_admin)):
    await execute_query("UPDATE clients SET display_name = $1 WHERE pc_name = $2", (data.display_name, data.pc_name))
    return {"status": "success"}

@app.post("/api/delete_lab")
async def delete_lab(data: DeleteLabInput, auth: dict = Depends(require_admin)):
    async with db_pool.acquire() as conn:
        async with conn.transaction():
            await conn.execute("DELETE FROM custom_labs WHERE lab_name = $1", data.lab_name)
            await conn.execute("UPDATE clients SET lab_name = 'Atanmamis_Cihazlar' WHERE lab_name = $1", data.lab_name)
            await conn.execute("DELETE FROM lab_settings WHERE lab_name = $1", data.lab_name)
    return {"status": "success"}

@app.post("/api/move_pc")
async def move_pc(data: MovePcInput, auth: dict = Depends(require_admin)):
    await execute_query("UPDATE clients SET lab_name = $1 WHERE pc_name = $2", (data.new_lab, data.pc_name))
    return {"status": "success"}

@app.post("/api/move_pcs")
async def move_pcs(data: MovePcsInput, auth: dict = Depends(require_admin)):
    for pc in data.pc_names: await execute_query("UPDATE clients SET lab_name = $1 WHERE pc_name = $2", (data.new_lab, pc))
    return {"status": "success"}

@app.post("/api/set_main_pc")
async def set_main_pc(data: SetMainPcInput, auth: dict = Depends(require_admin)):
    current = await execute_query("SELECT main_pc FROM lab_settings WHERE lab_name = $1", (data.lab_name,), fetch=True)
    if current and current[0]["main_pc"] == data.pc_name:
        await execute_query("UPDATE lab_settings SET main_pc = NULL WHERE lab_name = $1", (data.lab_name,))
        return {"status": "success", "message": f"{data.pc_name} ana bilgisayar yetkisi kaldırıldı."}
    
    await execute_query("INSERT INTO lab_settings (lab_name, main_pc) VALUES ($1, $2) ON CONFLICT (lab_name) DO UPDATE SET main_pc=EXCLUDED.main_pc", (data.lab_name, data.pc_name))
    return {"status": "success", "message": f"{data.pc_name} ana bilgisayar yapıldı."}

@app.post("/api/save_lab_layout")
async def save_lab_layout(data: SaveLabLayoutInput, auth: dict = Depends(require_admin)):
    await execute_query("INSERT INTO lab_settings (lab_name, layout_json) VALUES ($1, $2) ON CONFLICT (lab_name) DO UPDATE SET layout_json=EXCLUDED.layout_json", (data.lab_name, data.layout_json))
    return {"status": "success"}

@app.get("/api/lab_settings")
async def get_lab_settings(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT lab_name, main_pc, layout_json FROM lab_settings", fetch=True)
    return {row["lab_name"]: {"main_pc": row["main_pc"], "layout_json": row["layout_json"] or "{}"} for row in (rows or [])}

@app.post("/api/set_auto_enroll")
async def set_auto_enroll(data: AutoEnrollInput, auth: dict = Depends(require_admin)):
    await execute_query("INSERT INTO global_settings (key, value) VALUES ('auto_enroll_lab', $1) ON CONFLICT (key) DO UPDATE SET value=EXCLUDED.value", (data.target_lab,))
    return {"status": "success"}

@app.post("/api/set_concurrent_limit")
async def set_concurrent_limit(data: SetLimitInput, auth: dict = Depends(require_admin)):
    await execute_query("INSERT INTO global_settings (key, value) VALUES ('concurrent_limit', $1) ON CONFLICT (key) DO UPDATE SET value=EXCLUDED.value", (str(data.limit),))
    await process_queue()
    return {"status": "success"}

@app.get("/api/get_concurrent_limit")
async def get_concurrent_limit(auth: dict = Depends(require_auth)):
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'concurrent_limit'", fetch=True)
    return {"limit": int(row[0]["value"]) if row else 5}

@app.post("/api/upload")
async def upload_file(request: Request, file: UploadFile = File(...), auth: dict = Depends(require_admin)):
    # Dosya adını temizle ("../", mutlak yol, ayraç vb. atılır)
    filename = secure_filename(file.filename or "")
    if not filename:
        raise HTTPException(status_code=400, detail="Geçersiz dosya adı")
    # Son yol mutlaka UPLOAD_DIR'in doğrudan içinde olmalı (path traversal / symlink engeli)
    file_path = os.path.realpath(os.path.join(UPLOAD_DIR, filename))
    if os.path.dirname(file_path) != UPLOAD_DIR:
        raise HTTPException(status_code=400, detail="Geçersiz dosya yolu")
    with open(file_path, "wb") as buffer: shutil.copyfileobj(file.file, buffer)
    return {"status": "success", "filename": filename, "url": f"{request.base_url}download/{filename}"}

@app.post("/api/add_package")
async def add_package(data: CreatePackageInput, auth: dict = Depends(require_admin)):
    await execute_query("INSERT INTO packages (id, name, type, meta, command, icon, color) VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name, type=EXCLUDED.type, meta=EXCLUDED.meta, command=EXCLUDED.command, icon=EXCLUDED.icon, color=EXCLUDED.color", (data.id, data.name, data.type, data.meta, data.command, data.icon, data.color))
    return {"status": "success"}

@app.post("/api/delete_package")
async def delete_package(data: DeletePackageInput, auth: dict = Depends(require_admin)):
    await execute_query("DELETE FROM packages WHERE id = $1", (data.id,))
    return {"status": "success"}

@app.get("/api/packages")
async def get_packages(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM packages", fetch=True)
    return rows if rows else []

def get_folder_size(folder):
    total = 0
    if os.path.exists(folder):
        for dirpath, _, filenames in os.walk(folder):
            for f in filenames:
                fp = os.path.join(dirpath, f)
                if not os.path.islink(fp):
                    total += os.path.getsize(fp)
    return total

@app.get("/api/storage")
async def api_storage(auth: dict = Depends(require_auth)):
    upload_size = get_folder_size(UPLOAD_DIR)
    updates_size = get_folder_size(UPDATES_DIR)
    used_bytes = upload_size + updates_size
    total_bytes = 20 * 1024 * 1024 * 1024  # 20 GB
    
    try:
        size_row = await execute_query(f"SELECT pg_total_relation_size('{LOG_TABLE}') as size", fetch=True)
        log_bytes = size_row[0]['size'] if size_row else 0

        trend_rows = await execute_query(f"""
            SELECT SUBSTRING(timestamp FROM 1 FOR 10) as day, COUNT(*) as c
            FROM {LOG_TABLE}
            WHERE timestamp >= to_char(current_date - interval '6 days', 'YYYY-MM-DD')
            GROUP BY SUBSTRING(timestamp FROM 1 FOR 10) 
            ORDER BY day ASC
        """, fetch=True)
        log_trend = [{"day": r['day'], "count": r['c']} for r in trend_rows] if trend_rows else []
    except Exception as e:
        print(f"Log stat error: {e}")
        log_bytes = 0
        log_trend = []

    return {
        "status": "success",
        "used_bytes": used_bytes,
        "total_bytes": total_bytes,
        "free_bytes": max(0, total_bytes - used_bytes),
        "log_bytes": log_bytes,
        "log_trend": log_trend
    }

@app.post("/api/deploy_orchestration")
async def deploy_orchestration(data: OrchestrationInput, auth: dict = Depends(require_admin)):
    target_pcs = []
    if data.target_mode == 'ALL':
        res = await execute_query("SELECT pc_name, lab_name FROM clients", fetch=True)
        target_pcs = [{"pc": r["pc_name"], "lab": r["lab_name"]} for r in (res or [])]
    elif data.target_mode == 'LAB':
        for lab in data.targets:
            res = await execute_query("SELECT pc_name, lab_name FROM clients WHERE lab_name = $1", (lab,), fetch=True)
            target_pcs.extend([{"pc": r["pc_name"], "lab": r["lab_name"]} for r in (res or [])])
    else: 
        for pc in data.targets:
            res = await execute_query("SELECT lab_name FROM clients WHERE pc_name = $1", (pc,), fetch=True)
            target_pcs.append({"pc": pc, "lab": res[0]["lab_name"] if res else "Bilinmeyen Lab"})

    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    for target in target_pcs:
        for task in data.taskSequence:
            await execute_query("INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at) VALUES ($1, $2, $3, 'Pending', $4)", (target["pc"], target["lab"], task.command, now))
    await process_queue()
    return {"status": "success"}

@app.post("/api/upload_update")
async def upload_update(request: Request, file: UploadFile = File(...), auth: dict = Depends(require_admin)):
    try:
        timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        filename = f"pops_update_{timestamp}.zip"
        file_path = os.path.join(UPDATES_DIR, filename)
        content = await file.read()
        with open(file_path, "wb") as f: f.write(content)
        
        has_agent, has_updater, has_vision, has_watchdog = False, False, False, False
        try:
            with zipfile.ZipFile(file_path, 'r') as zf:
                for name in zf.namelist():
                    if "POpsAgent" in name: has_agent = True
                    if "POpsUpdater" in name: has_updater = True
                    if "POpsVision" in name: has_vision = True
                    if "POpsWatchdog" in name: has_watchdog = True
        except Exception:
            os.remove(file_path)
            return {"status": "error", "message": "ZIP dosyası bozuk"}
        
        if not has_agent or not has_updater:
            os.remove(file_path)
            return {"status": "error", "message": "ZIP dosyası gerekli exeleri içermiyor!"}
        
        file_hash = hashlib.sha256(content).hexdigest()
        
        dl_url = f"{request.base_url}updates/{filename}"
        await execute_query("INSERT INTO global_settings (key, value) VALUES ('latest_update_url', $1) ON CONFLICT (key) DO UPDATE SET value=$1", (dl_url,))
        await execute_query("INSERT INTO global_settings (key, value) VALUES ('latest_update_version', $1) ON CONFLICT (key) DO UPDATE SET value=$1", (f"update_{timestamp}",))
        await execute_query("INSERT INTO global_settings (key, value) VALUES ('latest_update_hash', $1) ON CONFLICT (key) DO UPDATE SET value=$1", (file_hash,))
        return {"status": "success", "download_url": dl_url, "hash": file_hash}
    except Exception as e: return {"status": "error", "message": str(e)}

@app.get("/api/latest_update")
async def get_latest_update(auth: dict = Depends(require_auth)):
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'latest_update_url'", fetch=True)
    return {"download_url": row[0]["value"] if row else None}

SHA256_HEX_RE = re.compile(r"[0-9a-f]{64}")

async def get_update_command():
    """Sunucuya yüklenmiş son paketten ajan güncelleme emrini üretir.

    Paket adresi istekten (dışarıdan) alınmaz, yalnızca /api/upload_update ile bu sunucuya
    yüklenen paket gönderilir. SHA-256 özeti zorunludur; özeti olmayan paket gönderilmez.
    Hata durumunda (None, mesaj) döner.
    """
    rows = await execute_query("SELECT key, value FROM global_settings WHERE key IN ('latest_update_url', 'latest_update_hash')", fetch=True)
    settings = {r["key"]: r["value"] for r in (rows or [])}
    url = settings.get("latest_update_url")
    file_hash = (settings.get("latest_update_hash") or "").lower()
    if not url:
        return None, "Sunucuda güncelleme paketi yok"
    if not SHA256_HEX_RE.fullmatch(file_hash):
        return None, "Paketin SHA-256 özeti yok, paketi yeniden yükleyin"
    package = os.path.basename(url.rstrip("/"))
    if not os.path.isfile(os.path.join(UPDATES_DIR, package)):
        return None, "Güncelleme paketi sunucuda bulunamadı, paketi yeniden yükleyin"
    return {"action": "update_agent", "download_url": url, "hash": file_hash}, None

@app.post("/api/update_agent/{hw_id}")
async def update_single_agent(hw_id: str, auth: dict = Depends(require_admin)):
    # İstek gövdesi okunmaz: indirme adresi dışarıdan kabul edilmez
    msg, error = await get_update_command()
    if not msg: return {"status": "error", "message": error}

    if hw_id in manager.active_agents:
        await manager.send_command(msg, hw_id)
        return {"status": "success"}
    return {"status": "error", "message": "Offline"}

@app.get("/api/broadcast_update")
async def broadcast_update(auth: dict = Depends(require_admin)):
    msg, error = await get_update_command()
    if not msg: return {"status": "error", "message": error}

    for pc_name in list(manager.active_agents.keys()):
        await manager.send_command(msg, pc_name)
    return {"status": "success"}

@app.get("/api/agent_versions")
async def get_agent_versions(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT av.pc_name, av.version, av.last_update, c.status, c.hostname FROM agent_versions av LEFT JOIN clients c ON av.pc_name = c.pc_name", fetch=True)
    return rows if rows else []

@app.get("/api/updates")
async def list_updates(request: Request, auth: dict = Depends(require_auth)):
    updates = []
    if os.path.exists(UPDATES_DIR):
        for f in os.listdir(UPDATES_DIR):
            if f.endswith('.zip'):
                updates.append({
                    "filename": f, "size_mb": round(os.path.getsize(os.path.join(UPDATES_DIR, f)) / (1024 * 1024), 2),
                    "uploaded_at": datetime.datetime.fromtimestamp(os.path.getmtime(os.path.join(UPDATES_DIR, f))).strftime("%Y-%m-%d %H:%M:%S"),
                    "url": f"{request.base_url}updates/{f}"
                })
    return sorted(updates, key=lambda x: x["uploaded_at"], reverse=True)

@app.delete("/api/updates/{filename}")
async def delete_update(filename: str, auth: dict = Depends(require_admin)):
    # Yalnızca UPDATES_DIR'in doğrudan içindeki bir .zip silinebilir (path traversal engeli)
    updates_root = os.path.realpath(UPDATES_DIR)
    file_path = os.path.realpath(os.path.join(updates_root, filename))
    if os.path.dirname(file_path) != updates_root or not file_path.endswith(".zip"):
        raise HTTPException(status_code=400, detail="Geçersiz dosya adı")
    if os.path.isfile(file_path):
        os.remove(file_path)
        return {"status": "success"}
    return {"status": "error"}

# Ekran akışı yalnızca Vision oturumu (rıza/bildirim akışı) üzerinden başlatılır;
# rıza sormadan yakalama başlatan eski /api/stream/start ucu kaldırıldı.

@app.get("/api/stream/stop/{pc_name}")
async def stop_stream(pc_name: str, auth: dict = Depends(require_auth)):
    await manager.send_command({"action": "stop_stream"}, pc_name)
    return {"status": "stopped"}

@app.get("/api/thumbnail/{pc_name}")
async def get_thumbnail(pc_name: str, auth: dict = Depends(require_auth)):
    if pc_name not in manager.active_agents: return {"status": "error", "image": None}
    loop = asyncio.get_event_loop()
    fut = loop.create_future()
    if pc_name not in manager.pending_thumbnails: manager.pending_thumbnails[pc_name] = []
    manager.pending_thumbnails[pc_name].append(fut)
    await manager.send_command({"type": "remote_input", "device": pc_name, "action": "get_thumbnail"}, pc_name)
    try:
        image_data = await asyncio.wait_for(fut, timeout=5.0)
        return {"status": "success", "image": image_data}
    except: return {"status": "timeout", "image": None}
    finally:
        if pc_name in manager.pending_thumbnails and fut in manager.pending_thumbnails[pc_name]:
            manager.pending_thumbnails[pc_name].remove(fut)

@app.post("/api/remote_input")
async def send_remote_input(data: RemoteInputData, auth: dict = Depends(require_auth)):
    target = data.device
    sent = await manager.send_remote_input_to_vision(data.dict(), target)
    if not sent:
        if target in manager.active_agents:
            await manager.send_command(data.dict(), target)
            return {"status": "success"}
        return {"status": "error"}
    return {"status": "success"}

@app.post("/api/agent_policies")
async def save_policies(data: AgentPoliciesInput, auth: dict = Depends(require_admin)):
    val = json.dumps({"fair_use_text": data.fair_use_text, "dns_categories": data.dns_categories, "auto_quarantine": data.auto_quarantine, "quarantine_threshold": data.quarantine_threshold}, ensure_ascii=False)
    await execute_query("INSERT INTO global_settings (key, value) VALUES ('agent_policies', $1) ON CONFLICT (key) DO UPDATE SET value = $1", (val,))
    return {"status": "success"}

@app.get("/api/agent_policies")
async def get_policies():
    # Ajanlar JWT taşımaz; adil kullanım metni ve DNS kategorilerini okuyabilmeleri için bu uç
    # kimlik doğrulaması istemez. Politikayı değiştirmek (POST) admin JWT gerektirir.
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'agent_policies'", fetch=True)
    if row:
        return json.loads(row[0]["value"])
    return {"fair_use_text": "Bu cihaz POps platformu tarafından izlenmekte ve yönetilmektedir.", "dns_categories": ["yasadisi_bahis", "pornografi"], "auto_quarantine": False, "quarantine_threshold": 5}

@app.post("/api/policy_alert")
async def add_policy_alert(data: PolicyAlertInput, _auth=Depends(agent_http_auth)):
    await log_audit_event(
        pc_name=data.hw_id, 
        log_type="Security", 
        message=f"🚨 KURAL İHLALİ: {data.domain} ({data.category})", 
        actor_id=data.hw_id, 
        event_type="policy.alert", 
        category="restricted_content", 
        action="dns_block", 
        risk_level="high", 
        reason="DNS Kural İhlali", 
        meta_data={"domain": data.domain, "violation_category": data.category}
    )
    return {"status": "success"}


# Sistem/sürüm/release uçları ayrı router'da (server.py şişmesin). Döngüsel import olmasın diye
# bağımlılıklar enjekte edilir; manager ve add_audit_log dosyanın bu noktasında tanımlı.
from system_routes import build_router as _build_system_router
app.include_router(_build_system_router(require_admin, require_superadmin, execute_query, manager, UPDATES_DIR, add_audit_log))
