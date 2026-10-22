"""LDAP / Active Directory ile şifre doğrulama (ldap3, saf Python, eşzamanlı: çağıran asyncio.to_thread ile çalıştırır).

Akış: hizmet hesabıyla bağlan → kullanıcıyı ayarlı filtreyle ara (kullanıcı adı filtre için kaçırılır) → bulunan DN
ve kullanıcının şifresiyle yeniden bağlan → hesap kapalı mı, hangi gruplarda (memberOf ve isteğe bağlı grup araması).

TLS zorunludur: LDAPS (636) ya da StartTLS (389). Sertifika ve sunucu adı her zaman doğrulanır (ldap3'ün kendi
varsayılanı doğrulamaz; burada kendi SSL bağlamımız kullanılır, en az TLS 1.2). Ayarda CA sertifikası verilmişse
yalnızca ona güvenilir, verilmemişse sistemin CA deposuna. Şifresiz LDAP yalnızca testler için açılabilir
(allow_insecure_for_tests; panelde yoktur). Şifreler hiçbir yere yazılmaz.
"""

import logging
import ssl
import uuid
from typing import List, Optional

from ldap3 import BASE, NONE, SIMPLE, SUBTREE, Connection, Server, Tls
from ldap3.core.exceptions import LDAPException
from ldap3.utils.conv import escape_bytes, escape_filter_chars

from pops import config

log = logging.getLogger("pops.sso.ldap")

# AD userAccountControl: ACCOUNTDISABLE
_UAC_DISABLED = 0x2
_LOCK_ATTRS = ("userAccountControl", "nsAccountLock", "pwdAccountLockedTime")
_ID_ATTRS = ("entryUUID", "objectGUID")


class LdapError(Exception):
    """Dizine ulaşılamadı ya da ayar hatası (kullanıcının şifresiyle ilgisi yok). message panelde gösterilir."""

    def __init__(self, message: str):
        super().__init__(message)
        self.message = message


class LdapResult:
    """status: ok | bad_credentials | not_found | ambiguous | disabled."""

    def __init__(self, status: str, username: str = "", dn: str = "", external_id: str = "",
                 groups: Optional[List[str]] = None):
        self.status = status
        self.username = username
        self.dn = dn
        self.external_id = external_id
        self.groups = groups or []


def tls_context(ca_pem: str) -> ssl.SSLContext:
    ctx = ssl.create_default_context(cadata=ca_pem or None)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.check_hostname = True
    ctx.verify_mode = ssl.CERT_REQUIRED
    return ctx


class _StrictTls(Tls):
    """ldap3'ün Tls'i sunucu adını el yordamıyla (ve Python 3.12'de kaldırılmış match_hostname ile) denetler; bunun
    yerine standart bağlam: zincir ve ad (IP dahil) el sıkışmada doğrulanır."""

    def __init__(self, ctx: ssl.SSLContext, host: str):
        super().__init__(validate=ssl.CERT_REQUIRED)
        self._ctx = ctx
        self._host = host

    def wrap_socket(self, connection, do_handshake=False):
        connection.socket = self._ctx.wrap_socket(
            connection.socket, server_side=False, do_handshake_on_connect=do_handshake,
            server_hostname=self._host.strip("[]"))


def _server(cfg: dict) -> Server:
    sec = cfg.get("security")
    if sec == "plain":
        if not (cfg.get("allow_insecure_for_tests") and config.SSO_ALLOW_INSECURE_FOR_TESTS):
            raise LdapError("Şifresiz LDAP kabul edilmez; LDAPS ya da StartTLS kullanın.")
        tls = None
    elif sec in ("ldaps", "starttls"):
        try:
            tls = _StrictTls(tls_context(cfg.get("ca_pem") or ""), cfg["host"])
        except (ssl.SSLError, ValueError):
            raise LdapError("CA sertifikası okunamadı.")
    else:
        raise LdapError("Bağlantı türü ldaps ya da starttls olmalı.")
    return Server(cfg["host"], port=int(cfg["port"]), use_ssl=(sec == "ldaps"), tls=tls, get_info=NONE,
                  connect_timeout=int(cfg.get("timeout") or 5))


def _describe(exc: BaseException) -> str:
    text = str(exc)
    if "CERTIFICATE_VERIFY_FAILED" in text or "certificate verify failed" in text:
        if "Hostname mismatch" in text or "IP address mismatch" in text:
            return "TLS doğrulanamadı: sertifikadaki ad sunucu adıyla uyuşmuyor."
        return "TLS doğrulanamadı: sunucu sertifikası güvenilir bir CA'dan değil (CA sertifikasını ekleyin)."
    if "timed out" in text.lower():
        return "Dizin sunucusu zamanında yanıt vermedi."
    return "Dizin sunucusuna bağlanılamadı (%s)." % type(exc).__name__


def _open(cfg: dict, user: Optional[str], password: Optional[str]) -> Connection:
    """Bağlantı açar, gerekiyorsa StartTLS yapar; bağlanma (bind) yapılmaz. Ulaşılamazsa LdapError."""
    server = _server(cfg)
    conn = Connection(server, user=user, password=password, authentication=SIMPLE if user else None,
                      auto_bind=False, read_only=True, raise_exceptions=False, auto_referrals=False,
                      receive_timeout=int(cfg.get("timeout") or 5))
    try:
        conn.open()
        if cfg.get("security") == "starttls" and not conn.start_tls():
            raise LdapError("StartTLS başlatılamadı.")
    except LdapError:
        conn.unbind()
        raise
    except (LDAPException, OSError, ssl.SSLError) as exc:
        conn.unbind()
        raise LdapError(_describe(exc))
    return conn


def _bind(conn: Connection) -> bool:
    try:
        return bool(conn.bind())
    except (LDAPException, OSError, ssl.SSLError) as exc:
        raise LdapError(_describe(exc))


def _service(cfg: dict, bind_password: Optional[str]) -> Connection:
    if cfg.get("bind_dn") and not bind_password:
        raise LdapError("Hizmet hesabının şifresi girilmemiş.")
    conn = _open(cfg, cfg.get("bind_dn") or None, bind_password or None)
    if not _bind(conn):
        conn.unbind()
        raise LdapError("Hizmet hesabıyla bağlanılamadı (DN ya da şifre yanlış).")
    return conn


def _attrs(entry: dict) -> dict:
    return {str(k).lower(): v for k, v in (entry.get("attributes") or {}).items()}


def _first(values) -> Optional[str]:
    if isinstance(values, (list, tuple)):
        values = values[0] if values else None
    if values is None:
        return None
    return values.decode("utf-8", "replace") if isinstance(values, bytes) else str(values)


def is_disabled(attrs: dict) -> bool:
    """AD (userAccountControl ACCOUNTDISABLE), 389-DS/FreeIPA (nsAccountLock), OpenLDAP ppolicy (pwdAccountLockedTime).
    Çoğu dizin kapalı hesabın bağlanmasını zaten reddeder; bu ikinci denetimdir."""
    a = {str(k).lower(): v for k, v in attrs.items()}
    uac = _first(a.get("useraccountcontrol"))
    if uac:
        try:
            if int(uac) & _UAC_DISABLED:
                return True
        except ValueError:
            pass
    if (_first(a.get("nsaccountlock")) or "").strip().lower() == "true":
        return True
    return bool(_first(a.get("pwdaccountlockedtime")))


def _external_id(entry: dict, dn: str) -> str:
    raw = {str(k).lower(): v for k, v in (entry.get("raw_attributes") or {}).items()}
    guid = raw.get("objectguid")
    if guid and isinstance(guid[0], bytes) and len(guid[0]) == 16:
        return "guid:" + str(uuid.UUID(bytes_le=guid[0]))
    eu = _first(_attrs(entry).get("entryuuid"))
    if eu:
        return "uuid:" + eu.lower()
    return "dn:" + dn.lower()


def _find_user(conn: Connection, cfg: dict, username: str) -> List[dict]:
    flt = cfg["user_filter"].replace("{username}", escape_filter_chars(username))
    attrs = list({cfg.get("username_attribute") or "sAMAccountName", "memberOf", *_LOCK_ATTRS, *_ID_ATTRS})
    conn.search(cfg["base_dn"], flt, search_scope=SUBTREE, attributes=attrs, size_limit=2,
                time_limit=int(cfg.get("timeout") or 5))
    _check_search(conn)
    return [e for e in (conn.response or []) if e.get("type") == "searchResEntry"]


def _check_search(conn: Connection) -> None:
    """Arama başarılı değilse (0 ya da 4 dışındaki her sonuç) LdapError: dizin ya da yetki hatası "kullanıcı yok"
    sayılmaz (yanlış şifre gibi görünmesin, oturum kapatmasın)."""
    code = (conn.result or {}).get("result")
    if code in (0, 4):   # 4: sizeLimitExceeded (en çok iki sonuç istenir; iki sonuç da yeter)
        return
    if code == 32:   # noSuchObject: arama kökü yok ya da okunamıyor
        raise LdapError("Arama kökü (base DN) dizinde yok ya da hizmet hesabı onu okuyamıyor.")
    if code is None:
        raise LdapError("Dizin sunucusu yanıt vermedi.")
    log.warning("dizin araması başarısız",
                extra={"result": code, "description": (conn.result or {}).get("description")})
    raise LdapError("Dizin araması başarısız oldu (LDAP sonuç kodu %s)." % code)


def _groups(conn: Connection, cfg: dict, entry: dict, username: str) -> List[str]:
    groups = []
    member_of = _attrs(entry).get("memberof") or []
    for g in member_of if isinstance(member_of, (list, tuple)) else [member_of]:
        g = _first(g)
        if g:
            groups.append(g)
    if cfg.get("group_base_dn") and cfg.get("group_filter"):
        flt = cfg["group_filter"].replace("{user_dn}", escape_filter_chars(entry["dn"])) \
            .replace("{username}", escape_filter_chars(username))
        conn.search(cfg["group_base_dn"], flt, search_scope=SUBTREE, attributes=["cn"], size_limit=500,
                    time_limit=int(cfg.get("timeout") or 5))
        groups += [e["dn"] for e in (conn.response or []) if e.get("type") == "searchResEntry"]
    seen = set()
    return [g for g in groups if not (g.lower() in seen or seen.add(g.lower()))]


def authenticate(cfg: dict, bind_password: Optional[str], username: str, password: str) -> LdapResult:
    """Kullanıcının dizindeki şifresini doğrular. Dizine ulaşılamazsa LdapError (yerel hesaplar etkilenmez)."""
    # Boş şifreyle "basit bağlanma" çoğu dizinde kimliksiz (anonim) bağlanma sayılır ve başarılı döner: hiç sorulmaz
    if not username or not password or len(username) > 256:
        return LdapResult("bad_credentials")
    try:
        return _authenticate(cfg, bind_password, username, password)
    except (LDAPException, OSError, ssl.SSLError) as exc:
        raise LdapError(_describe(exc))


def _authenticate(cfg: dict, bind_password: Optional[str], username: str, password: str) -> LdapResult:
    svc = _service(cfg, bind_password)
    try:
        entries = _find_user(svc, cfg, username)
        if not entries:
            return LdapResult("not_found")
        if len(entries) > 1:
            log.warning("dizinde aynı kullanıcı adıyla birden çok kayıt", extra={"user": username})
            return LdapResult("ambiguous")
        entry = entries[0]
        dn = entry["dn"]
        user_conn = _open(cfg, dn, password)
        try:
            if not _bind(user_conn):
                return LdapResult("bad_credentials")
        finally:
            user_conn.unbind()
        canonical = _first(_attrs(entry).get((cfg.get("username_attribute") or "sAMAccountName").lower())) or username
        result = LdapResult("ok", username=canonical.strip(), dn=dn, external_id=_external_id(entry, dn))
        if is_disabled(entry.get("attributes") or {}):
            result.status = "disabled"
            return result
        result.groups = _groups(svc, cfg, entry, username)
        return result
    finally:
        svc.unbind()


def exists(cfg: dict, bind_password: Optional[str], external_id: str) -> bool:
    """Bağlı hesap dizinde hâlâ var mı (değişmez kimliğiyle; ad değişse de bulunur). Dizine ulaşılamazsa LdapError."""
    try:
        svc = _service(cfg, bind_password)
        try:
            kind, _, value = external_id.partition(":")
            if kind == "dn":
                svc.search(value, "(objectClass=*)", search_scope=BASE, attributes=[], size_limit=1)
                if (svc.result or {}).get("result") == 32:
                    return False
            else:
                if kind == "guid":
                    flt = "(objectGUID=%s)" % escape_bytes(uuid.UUID(value).bytes_le)
                elif kind == "uuid":
                    flt = "(entryUUID=%s)" % escape_filter_chars(value)
                else:
                    return True   # bilinmeyen biçim: emin olunamıyorsa oturumlara dokunulmaz
                svc.search(cfg["base_dn"], flt, search_scope=SUBTREE, attributes=[], size_limit=1,
                           time_limit=int(cfg.get("timeout") or 5))
            _check_search(svc)
            return any(e.get("type") == "searchResEntry" for e in (svc.response or []))
        finally:
            svc.unbind()
    except (LDAPException, OSError, ssl.SSLError, ValueError) as exc:
        raise LdapError(_describe(exc))


def test(cfg: dict, bind_password: Optional[str], username: Optional[str]) -> dict:
    """"Bağlantıyı sına": bağlantı + TLS, hizmet hesabı, isteğe bağlı olarak bir kullanıcının aranması (şifresiz)."""
    try:
        return _test(cfg, bind_password, username)
    except (LDAPException, OSError, ssl.SSLError) as exc:
        raise LdapError(_describe(exc))


def _test(cfg: dict, bind_password: Optional[str], username: Optional[str]) -> dict:
    svc = _service(cfg, bind_password)
    try:
        out = {"ok": True, "security": cfg["security"]}
        if username:
            entries = _find_user(svc, cfg, username)
            if not entries:
                out.update(ok=False, message="Kullanıcı bulunamadı: filtreyi ve arama kökünü denetleyin.")
                return out
            if len(entries) > 1:
                out.update(ok=False, message="Bu adla birden çok kayıt bulundu; filtre tek kullanıcı döndürmeli.")
                return out
            entry = entries[0]
            out.update(user_dn=entry["dn"], groups=_groups(svc, cfg, entry, username),
                       disabled=is_disabled(entry.get("attributes") or {}))
        return out
    finally:
        svc.unbind()
