"""İstek gövdeleri için Pydantic modelleri."""

from typing import List, Optional

from pydantic import BaseModel


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
    limit: int


class TaskSequenceItem(BaseModel):
    name: str
    type: str
    command: str


class OrchestrationInput(BaseModel):
    target_mode: str
    targets: List[str]
    taskSequence: List[TaskSequenceItem]


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


class TaskActionInput(BaseModel):
    action: str
    target_mode: str
    target_id: str


class RemoteInputData(BaseModel):
    type: str
    device: str
    input_type: str
    data: dict


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
    dns_domains: Optional[dict] = None   # None: mevcut liste korunur (eski panel sürümü göndermez)


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
