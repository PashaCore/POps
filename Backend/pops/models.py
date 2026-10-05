"""İstek gövdeleri için Pydantic modelleri."""

from typing import List, Literal, Optional

from pydantic import AliasChoices, BaseModel, ConfigDict, Field, field_validator, model_validator

from pops import winget

TargetMode = Literal["ALL", "LAB", "PC"]


class StrictInput(BaseModel):
    """Tanınmayan alanı reddeder (422). Yazılan alan adı yanlışsa istek sessizce varsayılanlarla çalışmasın diye
    (ör. enroll-token'a lab_name yerine lab gönderilince sınıfsız bir jeton üretiliyordu)."""
    model_config = ConfigDict(extra="forbid")


def upper_mode(value):
    """target_mode büyük/küçük harf duyarsız: "lab" -> "LAB". Geçersiz değeri Literal reddeder."""
    return value.strip().upper() if isinstance(value, str) else value


class AdminLoginInput(BaseModel):
    username: str
    password: str
    otp: Optional[str] = None


class TotpLoginInput(BaseModel):
    challenge: str
    otp: str


class TotpEnableInput(BaseModel):
    otp: str


class TotpDisableInput(BaseModel):
    otp: Optional[str] = None


class TaskInput(BaseModel):
    target_pc: str
    target_lab: str
    script_path: str


class MovePcInput(BaseModel):
    pc_name: str
    new_lab: str


class MovePcsInput(BaseModel):
    pc_names: List[str]
    new_lab: str


class RenameLabInput(BaseModel):
    old_name: str
    new_name: str


class RenameDeviceInput(BaseModel):
    pc_name: str
    display_name: str


class CreateLabInput(BaseModel):
    lab_name: str


class DeleteLabInput(BaseModel):
    lab_name: str


class SetMainPcInput(BaseModel):
    lab_name: str
    pc_name: str


class SaveLabLayoutInput(BaseModel):
    lab_name: str
    layout_json: str


class AutoEnrollInput(BaseModel):
    target_lab: str
    expire_date: str


class SetLimitInput(BaseModel):
    # 0 = sınırsız; eksi değer kuyruğu sessizce durdururdu (F21)
    limit: int = Field(ge=0, le=10000)


class WingetPackage(StrictInput):
    """WINGET adımının paketi: winget kimliği ve isteğe bağlı sürüm (null: en son sürüm). Bkz. pops/winget.py."""
    id: str
    version: Optional[str] = None

    _id = field_validator("id")(winget.clean_id)
    _version = field_validator("version")(winget.clean_version)


class TaskSequenceItem(StrictInput):
    name: str
    # CMD (serbest komut), package / script (kitaplıktan, komut panelde üretilir) ya da WINGET (winget paketi)
    type: str
    command: Optional[str] = None
    winget: Optional[WingetPackage] = None

    @model_validator(mode="after")
    def _payload(self):
        if self.is_winget:
            if self.winget is None:
                raise ValueError("WINGET adımı winget paketini ister ({\"id\": ..., \"version\": ...})")
            if self.command:
                raise ValueError("WINGET adımı komut taşımaz")
        elif self.winget is not None:
            raise ValueError("winget alanı yalnızca WINGET adımında olur")
        elif self.command is None:
            raise ValueError("Adımın komutu yok")
        return self

    @property
    def is_winget(self) -> bool:
        return (self.type or "").strip().upper() == "WINGET"


class OrchestrationInput(StrictInput):
    target_mode: TargetMode
    targets: List[str]
    # Yeni istemciler task_sequence yazar; panel ve eski betikler taskSequence gönderir (ikisi de kabul edilir, ikisi
    # birden gönderilirse 422). Şemada (docs/openapi.json) yalnızca task_sequence görünür.
    task_sequence: List[TaskSequenceItem] = Field(validation_alias=AliasChoices("task_sequence", "taskSequence"))
    # Bağlam (isteğe bağlı): işin okunur adı, isteğin geldiği panel sayfası ve gerekçe görev kaydında saklanır
    title: Optional[str] = Field(default=None, max_length=200)
    source: Optional[str] = Field(default=None, max_length=40)
    reason: Optional[str] = Field(default=None, max_length=500)

    _mode = field_validator("target_mode", mode="before")(upper_mode)


class CreatePackageInput(BaseModel):
    id: str
    name: str
    type: str
    meta: str
    command: str
    icon: str
    color: str


class DeletePackageInput(BaseModel):
    id: str


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


class AuthEventInput(BaseModel):
    hw_id: str
    hostname: str
    student_id: str
    message: Optional[str] = ""


class TaskStatusInput(BaseModel):
    ids: List[int] = Field(..., max_length=5000)


class UpdateProgressInput(BaseModel):
    pcs: List[str] = Field(..., max_length=5000)
    version: str = Field(..., max_length=64)
    since: float = 0   # gönderim anı (Unix saniye); öncesindeki güncelleme sonuçları sayılmaz


class TaskActionInput(BaseModel):
    action: str
    target_mode: str
    target_id: str


class RemoteInputData(BaseModel):
    """HTTP uzaktan girdi. Tepsi alanları mesajın kökünde okur (panelin WebSocket yolu gibi): x, y, key, is_down...
    Eski istemciler bunları "data" altında gönderiyordu (F13); ikisi de kabul edilir, tepsiye düz iletilir."""

    model_config = ConfigDict(extra="allow")
    type: str = "remote_input"
    device: str
    input_type: str
    data: Optional[dict] = None


class StreamStopInput(BaseModel):
    pc_name: str


class HwInventoryInput(BaseModel):
    hw_id: Optional[str] = None
    hostname: Optional[str] = None
    cpu: str = "-"
    ram: str = "-"
    motherboard: str = "-"
    gpu: str = "-"
    os_version: str = "-"
    ip_address: str = "-"
    mac_address: str = "-"
    disk_info: str = "-"


# admin_id/admin_name/admin_role geriye uyumluluk için kabul edilir ama kullanılmaz; kimlik JWT'den alınır


class StartAuditSessionInput(BaseModel):
    target_pc: str
    reason: str
    is_mandatory: bool
    admin_id: Optional[int] = None
    admin_name: Optional[str] = None
    admin_role: Optional[str] = None


class EndAuditSessionInput(BaseModel):
    session_id: str
    status: str


class LockdownInput(BaseModel):
    target_pc: str
    reason: str
    admin_name: Optional[str] = None


class UserCreateInput(BaseModel):
    username: str
    password: str
    role: str
    permissions: str


class UserUpdateInput(BaseModel):
    username: str
    password: Optional[str] = None
    role: str
    permissions: str


class AgentPoliciesInput(BaseModel):
    fair_use_text: str
    dns_categories: list
    auto_quarantine: bool
    quarantine_threshold: int
    # F8: ajan (v0.1.4+) tam alan-adı eşleşmesi yapar; kategori -> alan adları. BOŞ ise DNS tespiti
    # kapalı kalır (kaba substring yanlış-alarmı + KVKK riski böyle önlenir).
    dns_domains: Optional[dict] = None  # None: mevcut liste korunur (eski panel sürümü göndermez)


class PolicyAlertInput(BaseModel):
    hw_id: str
    domain: str
    category: str


# ─── Zamanlanmış görevler ─────────────────────────────────────────────────────
class ScheduledTaskInput(BaseModel):
    name: str
    command: str
    target_mode: str  # ALL | LAB | PC
    targets: List[str] = []
    schedule_type: str  # once | daily | weekly
    run_at: Optional[str] = None  # once: "YYYY-MM-DDTHH:MM" (sunucu saati)
    time_of_day: Optional[str] = None  # daily/weekly: "HH:MM"
    weekdays: List[int] = []  # weekly: 1 = Pazartesi … 7 = Pazar
    enabled: bool = True


class ScheduleToggleInput(BaseModel):
    enabled: bool


# ─── Bildirimler ──────────────────────────────────────────────────────────────
class NotifySettingsInput(BaseModel):
    enabled: bool = False
    min_severity: str = "high"  # info | medium | high | critical
    email_to: str = ""  # virgülle ayrılmış adresler
    webhook_url: str = ""


class NotificationsReadInput(BaseModel):
    ids: List[int] = []  # boşsa hepsi okundu


# ─── Yazılım envanteri ve Windows güncelleme durumu (ajan gönderir) ───────────
class SoftwareItem(BaseModel):
    name: str
    version: Optional[str] = ""
    publisher: Optional[str] = None
    install_date: Optional[str] = None


class SoftwareInventoryInput(BaseModel):
    items: List[SoftwareItem] = []


class PatchUpdateItem(BaseModel):
    kb: Optional[str] = None
    title: str
    severity: Optional[str] = None  # Critical | Important | Moderate | Low | None (MSRC)
    categories: List[str] = []
    is_security: bool = False


class PatchStatusInput(BaseModel):
    pending_count: int = 0
    pending_security: int = 0
    pending_critical: int = 0
    reboot_required: bool = False
    last_search: Optional[str] = None  # ISO 8601
    last_install: Optional[str] = None  # ISO 8601
    updates: List[PatchUpdateItem] = []
    last_result: Optional[str] = None


class PatchInstallInput(BaseModel):
    target_mode: str = "PC"  # ALL | LAB | PC
    targets: List[str] = []
    scope: str = "security"  # security | all


# ─── Lisans takibi ────────────────────────────────────────────────────────────
class LicenseInput(BaseModel):
    name: str
    match_pattern: str
    publisher: Optional[str] = None
    seats: Optional[int] = None  # None = sınırsız
    license_type: str = "per_device"  # per_device | site | subscription
    expires_at: Optional[str] = None  # YYYY-MM-DD
    notes: Optional[str] = None


# ─── Yardım masası ────────────────────────────────────────────────────────────
class AgentTicketInput(BaseModel):
    subject: str
    body: Optional[str] = ""
    category: Optional[str] = "diger"
    reporter: Optional[str] = None  # oturumdaki kullanıcı adı (ajan doldurur)


class PanelTicketInput(BaseModel):
    subject: str
    body: Optional[str] = ""
    category: Optional[str] = "diger"
    priority: Optional[str] = "normal"
    pc_name: Optional[str] = None
    reporter: Optional[str] = None


class TicketUpdateInput(BaseModel):
    status: Optional[str] = None
    priority: Optional[str] = None
    assignee: Optional[str] = None  # "" = atamayı kaldır


class TicketMessageInput(BaseModel):
    body: str
    internal: bool = False


# ─── REST adları (/api/v1): yol parametresi gövdedeki alanın yerini alır ───────
class LabRenameInput(StrictInput):
    new_name: str


class DeviceUpdateInput(StrictInput):
    display_name: str


class MainPcInput(StrictInput):
    pc_name: str


class LabLayoutInput(StrictInput):
    layout_json: str


class QuarantineInput(StrictInput):
    reason: str


# ─── API jetonları ────────────────────────────────────────────────────────────
class ApiTokenCreateInput(StrictInput):
    name: str = Field(min_length=1, max_length=64)
    role: Literal["viewer", "admin"]
    expires_days: Optional[int] = Field(default=None, ge=1, le=3650)   # None = süresiz
