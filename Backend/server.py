
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
import shutil
import asyncio
import socket
import hashlib
import zipfile
import secrets
import bcrypt
from jose import JWTError, jwt
from slowapi import Limiter, _rate_limit_exceeded_handler
from slowapi.util import get_remote_address
from slowapi.errors import RateLimitExceeded

from dotenv import load_dotenv
load_dotenv()

# ─── Güvenlik Sabitleri ────────────────────────────────────────────────────────
JWT_SECRET   = os.getenv('JWT_SECRET', secrets.token_hex(32))   # .env'den oku, yoksa random üret
JWT_ALGO     = 'HS256'
JWT_EXPIRE_H = int(os.getenv('JWT_EXPIRE_HOURS', '12'))         # Token ömrü (saat)
BYPASS_SECRET = os.getenv('BYPASS_SECRET', 'POps_Bypass_2026') # bypass token salt

limiter = Limiter(key_func=get_remote_address)
security_scheme = HTTPBearer(auto_error=False)

def create_jwt(username: str, role: str) -> str:
    expire = datetime.datetime.utcnow() + datetime.timedelta(hours=JWT_EXPIRE_H)
    return jwt.encode({'sub': username, 'role': role, 'exp': expire}, JWT_SECRET, algorithm=JWT_ALGO)

def verify_jwt(token: str) -> dict:
    try:
        return jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALGO])
    except JWTError:
        return None

async def require_auth(creds: HTTPAuthorizationCredentials = Depends(security_scheme)):
    if not creds:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail='Token gerekli')
    payload = verify_jwt(creds.credentials)
    if not payload:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail='Geçersiz veya süresi dolmuş token')
    return payload

async def require_admin(payload: dict = Depends(require_auth)):
    if payload.get('role') not in ['admin', 'superadmin']:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Bu islem icin admin yetkisi gereklidir.")
    return payload

def ws_check_token(token: Optional[str]) -> bool:
    """WebSocket bağlantılarında ?token= query parametresi ile doğrulama."""
    if not token:
        return False
    return verify_jwt(token) is not None
# ──────────────────────────────────────────────────────────────────────────────

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
UPLOAD_DIR = os.path.join(BASE_DIR, "storage")
UPDATES_DIR = os.path.join(BASE_DIR, "updates")

USE_V2_SCHEMA = True

app = FastAPI(title="POps Merkez API")

# Rate limiter hata yöneticisi
app.state.limiter = limiter
app.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)

# İzin verilen originler — hem dev hem prod
ALLOWED_ORIGINS = [
    "https://dev.pashacore.com.tr",
    "http://localhost",
    "http://127.0.0.1",
]
app.add_middleware(
    CORSMiddleware,
    allow_origins=ALLOWED_ORIGINS,
    allow_credentials=True,
    allow_methods=["GET", "POST", "PUT", "DELETE"],
    allow_headers=["Authorization", "Content-Type", "X-Agent-Version"],
)

if not os.path.exists(UPLOAD_DIR): os.makedirs(UPLOAD_DIR)
if not os.path.exists(UPDATES_DIR): os.makedirs(UPDATES_DIR)

app.mount("/download", StaticFiles(directory=UPLOAD_DIR), name="download")
app.mount("/updates", StaticFiles(directory=UPDATES_DIR), name="updates")

DB_DSN = f"postgresql://{os.getenv('DB_USER', 'pasha_user')}:{os.getenv('DB_PASS', 'PashaCore_2026!')}@127.0.0.1:5432/{os.getenv('DB_NAME', 'pashacore_db')}"
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

async def init_db():
    async with db_pool.acquire() as conn:
        await conn.execute('''CREATE TABLE IF NOT EXISTS users (
            id SERIAL PRIMARY KEY, username TEXT UNIQUE NOT NULL, password_hash TEXT NOT NULL,
            role TEXT DEFAULT 'admin', last_login TEXT, permissions TEXT DEFAULT '[]')''')
        try:
            await conn.execute("ALTER TABLE users ADD COLUMN permissions TEXT DEFAULT '[]'")
        except:
            pass
        await conn.execute('''CREATE TABLE IF NOT EXISTS clients (
            pc_name TEXT PRIMARY KEY, hostname TEXT, lab_name TEXT, last_seen TEXT, status TEXT, 
            active_window TEXT, boot_count INTEGER DEFAULT 0, logged_user TEXT DEFAULT '-', 
            ip_address TEXT, dna_uuid TEXT, dna_bios TEXT, dna_disk TEXT, dna_mac TEXT, 
            dna_ram TEXT, cap_ram_readable BOOLEAN DEFAULT TRUE, is_quarantined BOOLEAN DEFAULT FALSE)''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS device_audit_logs (
            id SERIAL PRIMARY KEY, hw_id TEXT, action TEXT, reason TEXT, changes TEXT, timestamp TEXT)''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS lab_settings (lab_name TEXT PRIMARY KEY, main_pc TEXT)''')
        try:
            await conn.execute("ALTER TABLE lab_settings ADD COLUMN layout_json TEXT DEFAULT '{}'")
        except:
            pass
        await conn.execute('''CREATE TABLE IF NOT EXISTS global_settings (key TEXT PRIMARY KEY, value TEXT)''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS packages (id TEXT PRIMARY KEY, name TEXT, type TEXT, meta TEXT, command TEXT, icon TEXT, color TEXT)''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS custom_labs (lab_name TEXT PRIMARY KEY)''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS hw_inventory (
            pc_name TEXT PRIMARY KEY, hostname TEXT, cpu TEXT, ram TEXT, motherboard TEXT, 
            gpu TEXT, os_version TEXT, ip_address TEXT, mac_address TEXT, disk_info TEXT, last_updated TEXT)''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS agent_logs (id SERIAL PRIMARY KEY, pc_name TEXT, log_type TEXT, message TEXT, timestamp TEXT)''')
        # Tek kullanımlık bypass token tablosu
        await conn.execute('''CREATE TABLE IF NOT EXISTS bypass_tokens (
            id SERIAL PRIMARY KEY,
            pc_name TEXT NOT NULL,
            token TEXT NOT NULL UNIQUE,
            created_at TIMESTAMPTZ DEFAULT NOW(),
            expires_at TIMESTAMPTZ NOT NULL,
            is_used BOOLEAN DEFAULT FALSE
        )''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS agent_logs_v2 (
            id SERIAL PRIMARY KEY, 
            pc_name TEXT, 
            actor_id TEXT, 
            event_type TEXT, 
            category TEXT, 
            action TEXT, 
            risk_level TEXT, 
            reason TEXT, 
            message TEXT, 
            meta_data JSONB, 
            timestamp TEXT
        )''')
        await conn.execute('''CREATE TABLE IF NOT EXISTS agent_versions (pc_name TEXT PRIMARY KEY, version TEXT, last_update TEXT)''')
        
        await conn.execute("INSERT INTO global_settings (key, value) VALUES ('concurrent_limit', '5') ON CONFLICT (key) DO NOTHING")
        await conn.execute("CREATE INDEX IF NOT EXISTS idx_dna_uuid ON clients(dna_uuid)")

@app.on_event("startup")
async def startup_event():
    global db_pool
    print("⏳ Veritabanı motoru başlatılıyor...")
    for i in range(5):
        try:
            db_pool = await asyncpg.create_pool(DB_DSN, min_size=5, max_size=100)
            await init_db()
            print("✅ PostgreSQL Bağlantısı Başarılı!")
            
            admin_user = os.getenv('PANEL_ADMIN_USER', 'admin')
            admin_pass = os.getenv('PANEL_ADMIN_PASS', 'POpsAdmin2026')
            # bcrypt ile hash'le
            hashed = bcrypt.hashpw(admin_pass.encode(), bcrypt.gensalt()).decode()
            # Mevcut kayıt varsa sadece yoksa ekle (her restart'ta üzerine yazma)
            existing = await execute_query("SELECT id FROM users WHERE username=$1", (admin_user,), fetch=True)
            if not existing:
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
            s.sendto(data, ('255.255.255.255', 9))
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

    def disconnect_agent(self, pc_name: str):
        if pc_name in self.active_agents: del self.active_agents[pc_name]
        if pc_name in self.active_vision_ws: del self.active_vision_ws[pc_name]

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
class UpdateAgentInput(BaseModel): download_url: Optional[str] = None; version: Optional[str] = None
class RemoteInputData(BaseModel): type: str; device: str; input_type: str; data: dict
class HwInventoryInput(BaseModel): hw_id: Optional[str] = None; hostname: Optional[str] = None; cpu: str = "-"; ram: str = "-"; motherboard: str = "-"; gpu: str = "-"; os_version: str = "-"; ip_address: str = "-"; mac_address: str = "-"; disk_info: str = "-"
class StartAuditSessionInput(BaseModel): admin_id: int; admin_name: str; admin_role: str; target_pc: str; reason: str; is_mandatory: bool
class EndAuditSessionInput(BaseModel): session_id: str; status: str
class LockdownInput(BaseModel): admin_name: str; target_pc: str; reason: str

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
    
    # bcrypt veya eski SHA256 hash kontrolü (geçiş dönemi)
    try:
        valid = bcrypt.checkpw(data.password.encode(), stored_hash.encode())
    except Exception:
        # Eski SHA256 hash ise doğrula ve bcrypt'e güncelle
        valid = (hashlib.sha256(data.password.encode()).hexdigest() == stored_hash)
        if valid:
            new_hash = bcrypt.hashpw(data.password.encode(), bcrypt.gensalt()).decode()
            await execute_query("UPDATE users SET password_hash=$1 WHERE id=$2", (new_hash, u['id']))
    
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

@app.post("/api/admin/users")
async def create_user(data: UserCreateInput, auth=Depends(require_admin)):
    hashed_pw = bcrypt.hashpw(data.password.encode(), bcrypt.gensalt()).decode()
    try:
        await execute_query("INSERT INTO users (username, password_hash, role, permissions) VALUES ($1, $2, $3, $4)",
                           (data.username, hashed_pw, data.role, data.permissions))
        return {"status": "success", "message": "Kullanıcı başarıyla oluşturuldu."}
    except Exception as e:
        return {"status": "error", "message": str(e)}

@app.put("/api/admin/users/{user_id}")
async def update_user(user_id: int, data: UserUpdateInput, auth=Depends(require_admin)):
    try:
        if data.password:
            hashed_pw = bcrypt.hashpw(data.password.encode(), bcrypt.gensalt()).decode()
            await execute_query("UPDATE users SET username=$1, password_hash=$2, role=$3, permissions=$4 WHERE id=$5",
                               (data.username, hashed_pw, data.role, data.permissions, user_id))
        else:
            await execute_query("UPDATE users SET username=$1, role=$2, permissions=$3 WHERE id=$4",
                               (data.username, data.role, data.permissions, user_id))
        return {"status": "success", "message": "Kullanıcı başarıyla güncellendi."}
    except Exception as e:
        return {"status": "error", "message": str(e)}

@app.delete("/api/admin/users/{user_id}")
async def delete_user(user_id: int, auth=Depends(require_admin)):
    try:
        await execute_query("DELETE FROM users WHERE id=$1", (user_id,))
        return {"status": "success", "message": "Kullanıcı silindi."}
    except Exception as e:
        return {"status": "error", "message": str(e)}

@app.delete("/api/devices/{pc_name}")
async def delete_device(pc_name: str):
    try:
        await execute_query("DELETE FROM clients WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM hw_inventory WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM agent_logs WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM agent_versions WHERE pc_name = $1", (pc_name,))
        if pc_name in manager.active_connections:
            await manager.disconnect(manager.active_connections[pc_name], pc_name)
        return {"status": "success", "message": f"{pc_name} silindi."}
    except Exception as e:
        return {"status": "error", "message": str(e)}

@app.post("/api/audit/session/start")
async def start_audit_session(data: StartAuditSessionInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    session_id = f"SES-{secrets.token_hex(6).upper()}"
    
    await execute_query("""
        INSERT INTO enterprise_audit_logs 
        (session_id, admin_id, admin_name, admin_role, target_pc, start_time, end_time, reason, is_notified, is_mandatory, status) 
        VALUES ($1, $2, $3, $4, $5, $6, NULL, $7, TRUE, $8, 'Active')
    """, (session_id, data.admin_id, data.admin_name, data.admin_role, data.target_pc, now, data.reason, data.is_mandatory))
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
        "admin_name": data.admin_name,
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
    await log_audit_event(data.target_pc, "Critical Security", f"🚨 KARANTİNA BAŞLATILDI by {data.admin_name} - Neden: {data.reason}", actor_id=data.admin_name, event_type="security.lockdown", category="security", action="lockdown", risk_level="critical", reason=data.reason)
    
    # Cihazı karantina moduna al
    await execute_query("UPDATE clients SET is_quarantined = TRUE WHERE pc_name = $1", (data.target_pc,))
    
    # Ajanı kilitleme emri gönder
    await manager.send_command({"action": "lockdown", "reason": data.reason}, data.target_pc)
    
    return {"status": "success", "message": "Karantina sinyali gönderildi."}

@app.post("/api/security/unlock")
async def unlock_pc(data: LockdownInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    
    # Karantina logunu yaz
    await log_audit_event(data.target_pc, "Critical Security", f"✅ KARANTİNA KALDIRILDI by {data.admin_name} - Neden: {data.reason}", actor_id=data.admin_name, event_type="security.unlock", category="security", action="unlock", risk_level="info", reason=data.reason)
    
    # Cihazı karantina modundan çıkar
    await execute_query("UPDATE clients SET is_quarantined = FALSE WHERE pc_name = $1", (data.target_pc,))
    
    # Ajanı kilit açma emri gönder
    await manager.send_command({"action": "unlock"}, data.target_pc)
    
    return {"status": "success", "message": "Karantina kaldırma sinyali gönderildi."}

@app.get("/api/security/bypass_token/{pc_name}")
async def get_bypass_token(pc_name: str, auth=Depends(require_auth)):
    # Tek kullanımlık, 5 dakika TTL'li token
    token = secrets.token_hex(3).upper()  # 6 karakter hex = 16^6 = 16M kombinasyon
    expires = datetime.datetime.utcnow() + datetime.timedelta(minutes=5)
    # Mevcut kullanılmamış tokenları iptal et
    await execute_query(
        "UPDATE bypass_tokens SET is_used=TRUE WHERE pc_name=$1 AND is_used=FALSE",
        (pc_name,)
    )
    await execute_query(
        "INSERT INTO bypass_tokens (pc_name, token, expires_at) VALUES ($1, $2, $3)",
        (pc_name, token, expires.strftime("%Y-%m-%d %H:%M:%S"))
    )
    return {"status": "success", "token": token, "expires_in_seconds": 300}

@app.post("/api/auth/login")
async def auth_login(data: AuthEventInput):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await log_audit_event(data.hw_id, "Security", f"🟢 GİRİŞ: {data.student_id}", actor_id=data.student_id, event_type="auth.login", category="security", action="login", risk_level="info")
    await execute_query("UPDATE clients SET logged_user=$1 WHERE pc_name=$2", (data.student_id, data.hw_id))
    return {"status": "success"}

@app.post("/api/auth/failed")
async def auth_failed(data: AuthEventInput):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await log_audit_event(data.hw_id, "Security", f"🔴 RED: {data.student_id} ({data.message})", actor_id=data.student_id, event_type="auth.failed", category="security", action="login_failed", risk_level="medium", reason=data.message)
    return {"status": "success"}

@app.post("/api/auth/logout")
async def auth_logout(data: AuthEventInput):
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
async def websocket_panel(websocket: WebSocket, token: Optional[str] = None):
    if not ws_check_token(token):
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
    await manager.connect_vision(websocket, pc_name)
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
    manager.active_agents[active_hwid] = websocket
    agent_version = websocket.headers.get("X-Agent-Version", "unknown")

    async def handle_routine_payload(pld):
        nonlocal active_hwid
        current_time = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        current_hostname = pld.get("hostname", active_hwid)
        if pld.get("type") == "result":
            await execute_query("UPDATE tasks SET status = 'Completed', output = $1 WHERE id = $2", (pld.get("output"), pld.get("task_id")))
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
            await handle_routine_payload(payload)
    except WebSocketDisconnect:
        manager.disconnect_agent(active_hwid)
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
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
async def handle_task_action(data: TaskActionInput):
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
    peer = await execute_query("SELECT pc_name FROM clients WHERE lab_name = $1 AND status = 'online' LIMIT 1", (lab_name,), fetch=True)
    if peer:
        peer_name = peer[0]["pc_name"]
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
            "agent_version": r.get("version") or "Bilinmiyor"
        } 
        for r in (rows or [])
    ]

@app.post("/api/inventory/{pc_name}")
async def update_inventory(pc_name: str, data: HwInventoryInput):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await execute_query('''INSERT INTO hw_inventory (pc_name, hostname, cpu, ram, motherboard, gpu, os_version, ip_address, mac_address, disk_info, last_updated) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) ON CONFLICT (pc_name) DO UPDATE SET hostname=EXCLUDED.hostname, cpu=EXCLUDED.cpu, ram=EXCLUDED.ram, motherboard=EXCLUDED.motherboard, gpu=EXCLUDED.gpu, os_version=EXCLUDED.os_version, ip_address=EXCLUDED.ip_address, mac_address=EXCLUDED.mac_address, disk_info=EXCLUDED.disk_info, last_updated=EXCLUDED.last_updated''', (pc_name, data.hostname, data.cpu, data.ram, data.motherboard, data.gpu, data.os_version, data.ip_address, data.mac_address, data.disk_info, now))
    return {"status": "success"}

@app.get("/api/inventory")
async def get_all_inventory(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM hw_inventory", fetch=True)
    return rows if rows else []

@app.post("/api/logs/{pc_name}")
async def add_log(pc_name: str, data: LogInput):
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
    await execute_query("UPDATE clients SET lab_name = $1 WHERE lab_name = $2", (data.new_name, data.old_name))
    await execute_query("UPDATE custom_labs SET lab_name = $1 WHERE lab_name = $2", (data.new_name, data.old_name))
    return {"status": "success"}

@app.post("/api/rename_device")
async def rename_device(data: RenameDeviceInput, auth: dict = Depends(require_admin)):
    await execute_query("UPDATE clients SET display_name = $1 WHERE pc_name = $2", (data.display_name, data.pc_name))
    return {"status": "success"}

@app.post("/api/delete_lab")
async def delete_lab(data: DeleteLabInput, auth: dict = Depends(require_admin)):
    await execute_query("DELETE FROM custom_labs WHERE lab_name = $1", (data.lab_name,))
    await execute_query("UPDATE clients SET lab_name = 'Atanmamis_Cihazlar' WHERE lab_name = $1", (data.lab_name,))
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
async def upload_file(request: Request, file: UploadFile = File(...)):
    file_path = os.path.join(UPLOAD_DIR, file.filename)
    with open(file_path, "wb") as buffer: shutil.copyfileobj(file.file, buffer)
    return {"status": "success", "url": f"{request.base_url}download/{file.filename}"}

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
        size_row = await execute_query("SELECT pg_total_relation_size('agent_logs') as size", fetch=True)
        log_bytes = size_row[0]['size'] if size_row else 0
        
        trend_rows = await execute_query("""
            SELECT SUBSTRING(timestamp FROM 1 FOR 10) as day, COUNT(*) as c 
            FROM agent_logs 
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

@app.post("/api/update_agent/{hw_id}")
async def update_single_agent(hw_id: str, data: UpdateAgentInput):
    url = data.download_url
    update_hash = None
    if not url:
        row = await execute_query("SELECT key, value FROM global_settings WHERE key IN ('latest_update_url', 'latest_update_hash')", fetch=True)
        settings = {r["key"]: r["value"] for r in (row or [])}
        url = settings.get("latest_update_url")
        update_hash = settings.get("latest_update_hash")
    if not url: return {"status": "error"}
    
    if hw_id in manager.active_agents:
        await manager.send_command({"action": "update_agent", "download_url": url, "hash": update_hash}, hw_id)
        return {"status": "success"}
    return {"status": "error", "message": "Offline"}

@app.get("/api/broadcast_update")
async def broadcast_update(auth: dict = Depends(require_admin)):
    row = await execute_query("SELECT key, value FROM global_settings WHERE key IN ('latest_update_url', 'latest_update_hash')", fetch=True)
    settings = {r["key"]: r["value"] for r in (row or [])}
    if not settings.get("latest_update_url"): return {"status": "error"}
    
    msg = {"action": "update_agent", "download_url": settings.get("latest_update_url"), "hash": settings.get("latest_update_hash")}
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
async def delete_update(filename: str):
    file_path = os.path.join(UPDATES_DIR, filename)
    if os.path.exists(file_path):
        os.remove(file_path)
        return {"status": "success"}
    return {"status": "error"}

@app.get("/api/stream/start/{pc_name}")
async def start_stream(pc_name: str, auth: dict = Depends(require_auth)):
    await manager.send_command({"action": "start_stream"}, pc_name)
    return {"status": "started"}

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
async def send_remote_input(data: RemoteInputData):
    target = data.device
    sent = await manager.send_remote_input_to_vision(data.dict(), target)
    if not sent:
        if target in manager.active_agents:
            await manager.send_command(data.dict(), target)
            return {"status": "success"}
        return {"status": "error"}
    return {"status": "success"}

@app.post("/api/agent_policies")
async def save_policies(data: AgentPoliciesInput):
    val = json.dumps({"fair_use_text": data.fair_use_text, "dns_categories": data.dns_categories, "auto_quarantine": data.auto_quarantine, "quarantine_threshold": data.quarantine_threshold}, ensure_ascii=False)
    await execute_query("INSERT INTO global_settings (key, value) VALUES ('agent_policies', $1) ON CONFLICT (key) DO UPDATE SET value = $1", (val,))
    return {"status": "success"}

@app.get("/api/agent_policies")
async def get_policies(auth: dict = Depends(require_auth)):
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'agent_policies'", fetch=True)
    if row:
        return json.loads(row[0]["value"])
    return {"fair_use_text": "Bu cihaz POps platformu tarafından izlenmekte ve yönetilmektedir.", "dns_categories": ["yasadisi_bahis", "pornografi"], "auto_quarantine": False, "quarantine_threshold": 5}

@app.post("/api/policy_alert")
async def add_policy_alert(data: PolicyAlertInput):
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
