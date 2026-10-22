"""İstek gövdeleri için Pydantic modelleri."""

from typing import List, Literal, Optional, Union

from pydantic import (
    AliasChoices, BaseModel, ConfigDict, Field, StrictFloat, StrictInt, field_validator, model_validator,
)

from pops import power, winget

TargetMode = Literal["ALL", "LAB", "PC"]


class StrictInput(BaseModel):
    """Tanınmayan alanı reddeder (422). Yazılan alan adı yanlışsa istek sessizce varsayılanlarla çalışmasın diye
    (ör. enroll-token'a lab_name yerine lab gönderilince sınıfsız bir jeton üretiliyordu). Panelin ve entegrasyonların
    gönderdiği her gövde modeli bundan türer; bilerek gevşek bırakılanlar aşağıda AGENT_INPUT ile işaretlidir."""
    model_config = ConfigDict(extra="forbid")


# Ajanın gönderdiği gövdeler (envanter, yazılım, Windows Update, olay kaydı, oturum olayları, DNS uyarısı, yardım
# masası talebi) tanınmayan alanı yok sayar: sahada her sürümden ajan çalışır ve yeni bir ajanın eklediği alan eski
# sunucuda 422 alırsa o veri kaybolur (ör. ajan envanterle "dna" gönderir, sunucu okumaz). Bu modeller
# "class X(BaseModel)" olarak kalır: ajanın sözleşme testleri (Agent/POps.Tests, ServerContractTests) alanları buradan
# bu başlıkla okur. Hangi modellerin gevşek olduğunu test_units.py denetler (yeni model varsayılan olarak katıdır).
AGENT_INPUT = ConfigDict(extra="ignore")


def upper_mode(value):
    """target_mode büyük/küçük harf duyarsız: "lab" -> "LAB". Geçersiz değeri Literal reddeder."""
    return value.strip().upper() if isinstance(value, str) else value


class AdminLoginInput(StrictInput):
    username: str
    password: str
    otp: Optional[str] = None


class TotpLoginInput(StrictInput):
    challenge: str
    otp: str


class TotpEnableInput(StrictInput):
    otp: str


class TotpDisableInput(StrictInput):
    otp: Optional[str] = None


class MovePcInput(StrictInput):
    pc_name: str
    new_lab: str


class MovePcsInput(StrictInput):
    pc_names: List[str]
    new_lab: str


class RenameLabInput(StrictInput):
    old_name: str
    new_name: str


class RenameDeviceInput(StrictInput):
    pc_name: str
    display_name: str


class CreateLabInput(StrictInput):
    lab_name: str
    org_unit_id: Optional[int] = None   # kurum birimi (bkz. pops/tenancy.py)


class DeleteLabInput(StrictInput):
    lab_name: str


class SetMainPcInput(StrictInput):
    lab_name: str
    pc_name: str


class SaveLabLayoutInput(StrictInput):
    lab_name: str
    layout_json: str


class AutoEnrollInput(StrictInput):
    target_lab: str
    expire_date: str


class SetLimitInput(StrictInput):
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


class CreatePackageInput(StrictInput):
    id: str
    name: str
    type: str
    meta: str
    command: str
    icon: str
    color: str


class DeletePackageInput(StrictInput):
    id: str


class LogInput(BaseModel):
    model_config = AGENT_INPUT
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
    model_config = AGENT_INPUT
    hw_id: str
    hostname: str
    student_id: str
    message: Optional[str] = ""


class TaskStatusInput(StrictInput):
    ids: List[int] = Field(..., max_length=5000)


class UpdateProgressInput(StrictInput):
    pcs: List[str] = Field(..., max_length=5000)
    version: str = Field(..., max_length=64)
    since: float = 0   # gönderim anı (Unix saniye); öncesindeki güncelleme sonuçları sayılmaz


class TaskActionInput(StrictInput):
    action: str
    target_mode: str
    target_id: str


class RemoteInputData(BaseModel):
    """HTTP uzaktan girdi. Tepsi alanları mesajın kökünde okur (panelin WebSocket yolu gibi): x, y, key, is_down...
    Eski istemciler bunları "data" altında gönderiyordu (F13); ikisi de kabul edilir, tepsiye düz iletilir."""

    # Bilerek extra="allow": girdi alanları (x, y, key, is_down, ctrl ...) gövdenin kökünde gelir ve model_extra'dan
    # okunur; uç (control.py) yalnızca bilinen girdi alanlarını tepsiye iletir, gerisini atar.
    model_config = ConfigDict(extra="allow")
    type: str = "remote_input"
    device: str
    input_type: str
    data: Optional[dict] = None


class StreamStopInput(StrictInput):
    pc_name: str


class HwInventoryInput(BaseModel):
    model_config = AGENT_INPUT
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


class StartAuditSessionInput(StrictInput):
    target_pc: str
    reason: str
    is_mandatory: bool
    admin_id: Optional[int] = None
    admin_name: Optional[str] = None
    admin_role: Optional[str] = None


class EndAuditSessionInput(StrictInput):
    session_id: str
    status: str


class LockdownInput(StrictInput):
    target_pc: str
    reason: str
    admin_name: Optional[str] = None


AuthSource = Literal["local", "ldap", "oidc"]


class UserCreateInput(StrictInput):
    username: str
    password: Optional[str] = None   # yerel hesapta zorunlu; dizin ve OIDC hesabının yerel şifresi olmaz
    role: str
    permissions: str
    auth_source: AuthSource = "local"
    # Kurum birimi kapsamı (bkz. pops/tenancy.py): None = her şey; birim kimlikleri = o birimler ve alt birimleri
    org_scope: Optional[List[int]] = Field(default=None, max_length=200)


class UserUpdateInput(StrictInput):
    username: str
    password: Optional[str] = None
    role: str
    permissions: str
    auth_source: Optional[AuthSource] = None   # None: değişmez
    # Gönderilmezse kapsam değişmez; null gönderilirse kapsam kalkar (her şey)
    org_scope: Optional[List[int]] = Field(default=None, max_length=200)


class AgentPoliciesInput(StrictInput):
    fair_use_text: str
    dns_categories: list
    auto_quarantine: bool
    quarantine_threshold: int
    # F8: ajan (v0.1.4+) tam alan-adı eşleşmesi yapar; kategori -> alan adları. BOŞ ise DNS tespiti
    # kapalı kalır (kaba substring yanlış-alarmı + KVKK riski böyle önlenir).
    dns_domains: Optional[dict] = None  # None: mevcut liste korunur (eski panel sürümü göndermez)


class PolicyAlertInput(BaseModel):
    model_config = AGENT_INPUT
    hw_id: str
    domain: str
    category: str


# ─── Zamanlanmış görevler ─────────────────────────────────────────────────────
class ScheduledTaskInput(StrictInput):
    name: str
    command: str
    target_mode: str  # ALL | LAB | PC
    targets: List[str] = []
    schedule_type: str  # once | daily | weekly
    run_at: Optional[str] = None  # once: "YYYY-MM-DDTHH:MM" (sunucu saati)
    time_of_day: Optional[str] = None  # daily/weekly: "HH:MM"
    weekdays: List[int] = []  # weekly: 1 = Pazartesi … 7 = Pazar
    enabled: bool = True


class ScheduleToggleInput(StrictInput):
    enabled: bool


# ─── Bildirimler ──────────────────────────────────────────────────────────────
class NotifySettingsInput(StrictInput):
    enabled: bool = False
    min_severity: str = "high"  # info | medium | high | critical
    email_to: str = ""  # virgülle ayrılmış adresler
    webhook_url: str = ""


class NotificationsReadInput(StrictInput):
    ids: List[int] = []  # boşsa hepsi okundu


# ─── Yazılım envanteri ve Windows güncelleme durumu (ajan gönderir) ───────────
class SoftwareItem(BaseModel):
    model_config = AGENT_INPUT
    name: str
    version: Optional[str] = ""
    publisher: Optional[str] = None
    install_date: Optional[str] = None


class SoftwareInventoryInput(BaseModel):
    model_config = AGENT_INPUT
    items: List[SoftwareItem] = []


class PatchUpdateItem(BaseModel):
    model_config = AGENT_INPUT
    kb: Optional[str] = None
    title: str
    severity: Optional[str] = None  # Critical | Important | Moderate | Low | None (MSRC)
    categories: List[str] = []
    is_security: bool = False


class PatchStatusInput(BaseModel):
    model_config = AGENT_INPUT
    pending_count: int = 0
    pending_security: int = 0
    pending_critical: int = 0
    reboot_required: bool = False
    last_search: Optional[str] = None  # ISO 8601
    last_install: Optional[str] = None  # ISO 8601
    updates: List[PatchUpdateItem] = []
    last_result: Optional[str] = None


class PatchInstallInput(StrictInput):
    target_mode: str = "PC"  # ALL | LAB | PC
    targets: List[str] = []
    scope: str = "security"  # security | all


# ─── Lisans takibi ────────────────────────────────────────────────────────────
class LicenseInput(StrictInput):
    name: str
    match_pattern: str
    publisher: Optional[str] = None
    seats: Optional[int] = None  # None = sınırsız
    license_type: str = "per_device"  # per_device | site | subscription
    expires_at: Optional[str] = None  # YYYY-MM-DD
    notes: Optional[str] = None
    org_unit_id: Optional[int] = None  # lisansın kurum birimi; None = kurum geneli (bkz. pops/tenancy.py)


# ─── Yardım masası ────────────────────────────────────────────────────────────
class AgentTicketInput(BaseModel):
    model_config = AGENT_INPUT
    subject: str
    body: Optional[str] = ""
    category: Optional[str] = "diger"
    reporter: Optional[str] = None  # oturumdaki kullanıcı adı (ajan doldurur)


class PanelTicketInput(StrictInput):
    subject: str
    body: Optional[str] = ""
    category: Optional[str] = "diger"
    priority: Optional[str] = "normal"
    pc_name: Optional[str] = None
    reporter: Optional[str] = None
    org_unit_id: Optional[int] = None  # cihazsız talebin kurum birimi (kapsamlı hesapta verilmezse kendi birimi)


class TicketUpdateInput(StrictInput):
    status: Optional[str] = None
    priority: Optional[str] = None
    assignee: Optional[str] = None  # "" = atamayı kaldır


class TicketMessageInput(StrictInput):
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
    # Kurum birimi kapsamı: None = her şey (bkz. pops/tenancy.py)
    org_scope: Optional[List[int]] = Field(default=None, max_length=200)


# ─── Sınav modu (pops/exams.py) ───────────────────────────────────────────────
class ExamStartInput(StrictInput):
    # Girişlerin biçimi ve sayısı (en çok 50, tekrarlar birleşir) uçta denetlenir: hatalı girişin adı 400'de yazar
    allow: List[str] = Field(default_factory=list, max_length=200)
    until: Optional[Union[StrictInt, StrictFloat]] = None            # unix saniye; ya bu ya duration_minutes
    duration_minutes: Optional[StrictInt] = Field(default=None, ge=1, le=480)
    message: str = Field(default="", max_length=1000)
    block_apps: List[str] = Field(default_factory=list, max_length=200)
    reason: str = Field(max_length=1000)


class ExamEndInput(StrictInput):
    reason: str = Field(default="", max_length=1000)


# ─── Kurum birimleri (ilçe → okul) ─────────────────────────────────────────────
class OrgUnitInput(StrictInput):
    name: str = Field(min_length=1, max_length=100)
    parent_id: Optional[int] = None   # üst birim (ör. ilçe); None = en üst


class OrgUnitUpdateInput(StrictInput):
    name: Optional[str] = Field(default=None, min_length=1, max_length=100)
    parent_id: Optional[int] = None   # gönderilmezse değişmez; null = en üste taşı


class OrgUnitLabsInput(StrictInput):
    labs: List[str] = Field(max_length=5000)   # birime bağlı sınıfların TAMAMI


# ─── Güç komutları ve kullanıcıya mesaj (pops/power.py) ───────────────────────
class PowerCommandInput(StrictInput):
    """Hedef: target_mode PC (cihaz kimlikleri), LAB (sınıf adları) ya da ALL. Yalnızca çevrimiçi hedeflere görev
    açılır."""
    target_mode: TargetMode = "PC"
    targets: List[str] = Field(default_factory=list, max_length=5000)
    op: Literal["shutdown", "restart", "logoff", "lock"]
    delay: int = Field(default=0, ge=0, le=power.MAX_DELAY)
    # Kullanıcıya not (isteğe bağlı): kontrol karakterleri atılır, sonra en çok 200 karakter
    message: Optional[str] = Field(default=None, max_length=2 * power.MAX_NOTE)
    source: Optional[str] = Field(default=None, max_length=40)
    title: Optional[str] = Field(default=None, max_length=200)

    _mode = field_validator("target_mode", mode="before")(upper_mode)

    @field_validator("message")
    @classmethod
    def _note(cls, value):
        return power.clean_text(value, power.MAX_NOTE) or None


class UserMessageInput(StrictInput):
    target_mode: TargetMode = "PC"
    targets: List[str] = Field(default_factory=list, max_length=5000)
    title: str = Field(max_length=2 * power.MAX_TITLE)
    text: str = Field(max_length=2 * power.MAX_TEXT)
    style: Literal["info", "warning"] = "info"
    requires_ack: bool = False
    source: Optional[str] = Field(default=None, max_length=40)

    _mode = field_validator("target_mode", mode="before")(upper_mode)

    @field_validator("title")
    @classmethod
    def _title(cls, value):
        value = power.clean_text(value, power.MAX_TITLE)
        if not value:
            raise ValueError("Başlık boş olamaz")
        return value

    @field_validator("text")
    @classmethod
    def _text(cls, value):
        value = power.clean_text(value, power.MAX_TEXT, multiline=True)
        if not value:
            raise ValueError("Metin boş olamaz")
        return value


# ─── Dizin (LDAP / AD) ve OpenID Connect ile giriş (pops/sso.py) ─────────────────
# Eşlemenin kurum birimi kapsamı (pops/tenancy.py, D-25): None = ayarlanmadı (yeni hesap en dar kapsamı alır),
# "all" = açıkça bütün kurum, birim kimlikleri = o birimler ve alt birimleri
SsoScope = Optional[Union[Literal["all"], List[int]]]


class SsoGroupMapInput(StrictInput):
    group: str = Field(min_length=1, max_length=512)   # LDAP: grup DN'i; OIDC: grup talebindeki değer
    role: Literal["viewer", "admin", "superadmin"]
    pages: List[str] = Field(default_factory=list, max_length=20)
    org_scope: SsoScope = Field(default=None, max_length=200)


class LdapSettingsInput(StrictInput):
    enabled: bool = False
    host: str = Field(default="", max_length=255)
    port: int = Field(default=636, ge=1, le=65535)
    security: Literal["ldaps", "starttls", "plain"] = "ldaps"
    base_dn: str = Field(default="", max_length=1024)
    bind_dn: str = Field(default="", max_length=1024)
    bind_password: Optional[str] = Field(default=None, max_length=1024)   # None: kayıtlı şifre kalır, "": silinir
    user_filter: str = Field(default="(sAMAccountName={username})", max_length=512)
    username_attribute: str = Field(default="sAMAccountName", max_length=64)
    group_base_dn: str = Field(default="", max_length=1024)
    group_filter: str = Field(default="(|(member={user_dn})(uniqueMember={user_dn}))", max_length=512)
    group_map: List[SsoGroupMapInput] = Field(default_factory=list, max_length=50)
    ca_pem: str = Field(default="", max_length=65536)
    timeout: int = Field(default=5, ge=1, le=30)
    # Yalnızca testler için (şifresiz LDAP); panelde gösterilmez
    allow_insecure_for_tests: bool = False


class LdapTestInput(LdapSettingsInput):
    test_username: Optional[str] = Field(default=None, max_length=256)


class OidcSettingsInput(StrictInput):
    enabled: bool = False
    display_name: str = Field(default="", max_length=60)
    issuer: str = Field(default="", max_length=512)
    client_id: str = Field(default="", max_length=512)
    client_secret: Optional[str] = Field(default=None, max_length=2048)   # None: kayıtlı sır kalır, "": silinir
    redirect_uri: str = Field(default="", max_length=512)
    scopes: str = Field(default="openid email profile", max_length=256)
    username_claim: str = Field(default="email", max_length=64)
    groups_claim: str = Field(default="groups", max_length=64)
    group_map: List[SsoGroupMapInput] = Field(default_factory=list, max_length=50)
    allowed_domains: List[str] = Field(default_factory=list, max_length=20)
    default_role: Literal["", "viewer", "admin"] = ""
    default_pages: List[str] = Field(default_factory=list, max_length=20)
    default_org_scope: SsoScope = Field(default=None, max_length=200)
    ca_pem: str = Field(default="", max_length=65536)
    # Yalnızca testler için (https olmayan sağlayıcı); panelde gösterilmez
    allow_insecure_for_tests: bool = False


class SsoRedeemInput(StrictInput):
    ticket: str = Field(min_length=20, max_length=128)
    binding: str = Field(min_length=20, max_length=128)
