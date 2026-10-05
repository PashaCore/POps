"""Dizin (LDAP / Active Directory) ve OpenID Connect ile panel girişi — entegrasyon testi (CI 'security' işi).

LDAP: geçici bir OpenLDAP kabı (osixia/openldap, yalnızca 127.0.0.1'e bağlı) test anında üretilen bir CA'nın
sertifikasıyla LDAPS (636) ve StartTLS (389) sunar. AD'yi taklit etmek için küçük bir şema eklenir (sAMAccountName,
userAccountControl); gruplar groupOfUniqueNames, kullanıcılarda memberOf. Docker yoksa ya da POPS_TEST_SKIP_DOCKER=1
ise LDAP bölümü atlanır (OIDC bölümü yine çalışır).
  - giriş, grup → rol ve sayfalar, büyük harfle yazılan ad, StartTLS; yanlış şifre 401, boş şifre ve filtre enjeksiyonu
    401, devre dışı hesap ve eşlenen grupta olmayan hesap reddedilir; aynı adlı yerel hesap ele geçirilemez
  - şifresiz LDAP bayraksız reddedilir; yanlış CA ve sertifikadaki ad uyuşmazlığı reddedilir
  - 2FA dizin hesabında da sorulur; dizinden silinen hesabın açık oturumu kapanır; grup değişince rol güncellenir
  - dizin kapalıyken yerel süper admin girer, dizin hesabı 503 alır; ilk yerel süper admin dizin hesabına çevrilemez
  - ayarlar yalnızca süper admine açık, sır dönmez ve şifreli saklanır, değişiklik denetim kaydına sırsız yazılır
OIDC: test içinde https'li sahte bir sağlayıcı (keşif, JWKS, authorize, token; test RSA anahtarı; PKCE ve istemci
sırrını denetler). Kod akışı sunucu üzerinden baştan sona yürür; yanlış state/çerez, yanlış nonce, süresi dolmuş jeton,
yanlış aud ve iss, geçersiz imza, HS256, açık yönlendirme denemeleri, bilet tekrarı ve başka tarayıcının bileti
reddedilir; e-posta alan adı ve varsayılan rol, 2FA, ad çakışması.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET (sunucuyla aynı).
  python Backend/tests/test_sso.py            # testler
  python Backend/tests/test_sso.py --serve    # tarayıcı denemesi: dizin + sağlayıcı ayarlanıp çalışır kalır
"""

import asyncio
import base64
import datetime
import hashlib
import http.cookiejar
import http.server
import ipaddress
import json
import os
import secrets
import shutil
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import bcrypt  # noqa: E402
import jwt  # noqa: E402
from cryptography import x509  # noqa: E402
from cryptography.hazmat.primitives import hashes, serialization  # noqa: E402
from cryptography.hazmat.primitives.asymmetric import rsa  # noqa: E402
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID  # noqa: E402

import server  # noqa: E402  (create_jwt, aynı JWT_SECRET)
from pops import secretbox  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"].rstrip("/")
IMAGE = os.environ.get("POPS_TEST_LDAP_IMAGE", "osixia/openldap:1.5.0")
BASE = "dc=pops,dc=test"
ADMIN_DN = "cn=admin," + BASE
LDAP_ADMIN_PW = "ldap-admin-" + secrets.token_hex(6)
SVC_PW = "svc-" + secrets.token_hex(8)
LOCAL_PW = "Local-Super-" + secrets.token_hex(4)
# Şifresiz LDAP yalnızca sunucu bu değişkenle başlatıldıysa (CI ve run_local.sh verir) ayarda açılabilir
INSECURE_OK = os.environ.get("POPS_SSO_ALLOW_INSECURE_FOR_TESTS") == "1"
CLIENT_ID = "pops-panel"
CLIENT_SECRET = "oidc-secret-" + secrets.token_hex(8)
PAGES_VIEW = ["devices", "logger"]
USERS = ["ssolocal", "ssoadmin", "ssoplain", "ldapclash", "ayse", "mehmet", "zeynep", "totpuser",
         "veli@okul.test", "deniz@okul.test", "kaan@okul.test", "ssolocal@okul.test", "oidc2fa@okul.test"]
FAILS = []
_xff = [0]


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def ip():
    """Her giriş ayrı bir istemci adresinden: dakikada 10 deneme sınırına ve öteki testlerin kotasına takılmasın."""
    _xff[0] += 1
    return "10.66.%d.%d" % (_xff[0] // 250, _xff[0] % 250 + 1)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def req(path_or_url, token=None, body=None, method=None, headers=None, jar=None, ctx=None):
    """(durum, gövde (JSON ya da metin), başlıklar). Yönlendirme izlenmez."""
    url = path_or_url if path_or_url.startswith("http") else HTTP + path_or_url
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(url, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    r.add_header("X-Forwarded-For", ip())
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    handlers = [_NoRedirect()]
    if jar is not None:
        handlers.append(urllib.request.HTTPCookieProcessor(jar))
    if ctx is not None:
        handlers.append(urllib.request.HTTPSHandler(context=ctx))
    opener = urllib.request.build_opener(*handlers)
    try:
        resp = opener.open(r, timeout=40)
        status, raw, hdrs = resp.status, resp.read(), resp.headers
    except urllib.error.HTTPError as e:
        status, raw, hdrs = e.code, e.read(), e.headers
    try:
        out = json.loads(raw) if raw and "json" in (hdrs.get("Content-Type") or "") else raw.decode("utf-8", "replace")
    except ValueError:
        out = None
    return status, out, hdrs


def login(username, password, otp=None):
    body = {"username": username, "password": password}
    if otp:
        body["otp"] = otp
    s, b, _ = req("/api/admin/login", body=body)
    return s, b if isinstance(b, dict) else {}


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def totp(secret_b32, step_offset=0):
    key = base64.b32decode(secret_b32 + "=" * ((8 - len(secret_b32) % 8) % 8))
    counter = int(time.time() // 30) + step_offset
    d = __import__("hmac").new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
    o = d[-1] & 0x0F
    return str((struct.unpack(">I", d[o:o + 4])[0] & 0x7FFFFFFF) % 1000000).zfill(6)


# ── Geçici PKI ──────────────────────────────────────────────────────────────────


def _pem(cert):
    return cert.public_bytes(serialization.Encoding.PEM).decode()


def make_ca(name):
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.datetime.now(datetime.timezone.utc)
    subj = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, name)])
    cert = (x509.CertificateBuilder().subject_name(subj).issuer_name(subj).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(hours=1))
            .not_valid_after(now + datetime.timedelta(days=2))
            .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
            .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, False, False), critical=True)
            .sign(key, hashes.SHA256()))
    return key, cert


def make_server_cert(ca_key, ca_cert):
    """Yalnızca IP:127.0.0.1 adına: "localhost" ile bağlanmak ad uyuşmazlığı verir (testte kullanılır)."""
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "127.0.0.1")]))
            .issuer_name(ca_cert.subject).public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(hours=1)).not_valid_after(now + datetime.timedelta(days=2))
            .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address("127.0.0.1"))]),
                           critical=False)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .sign(ca_key, hashes.SHA256()))
    key_pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL,
                                serialization.NoEncryption()).decode()
    return key_pem, _pem(cert)


class Pki:
    def __init__(self, tmp):
        ca_key, ca = make_ca("POps SSO test CA")
        self.ca_pem = _pem(ca)
        self.other_ca_pem = _pem(make_ca("Some other CA")[1])
        self.key_pem, self.cert_pem = make_server_cert(ca_key, ca)
        self.dir = os.path.join(tmp, "certs")
        os.makedirs(self.dir)
        for name, text in (("ca.crt", self.ca_pem), ("ldap.crt", self.cert_pem), ("ldap.key", self.key_pem)):
            with open(os.path.join(self.dir, name), "w") as f:
                f.write(text)
            os.chmod(os.path.join(self.dir, name), 0o644)   # kabın içindeki kök kopyalar (geçici test anahtarı)
        os.chmod(self.dir, 0o755)
        self.client_ctx = ssl.create_default_context(cadata=self.ca_pem)


# ── OpenLDAP kabı ───────────────────────────────────────────────────────────────

AD_SCHEMA = """dn: cn=popsadtest,cn=schema,cn=config
objectClass: olcSchemaConfig
cn: popsadtest
olcAttributeTypes: ( 1.3.6.1.4.1.32473.1.1 NAME 'sAMAccountName' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE )
olcAttributeTypes: ( 1.3.6.1.4.1.32473.1.2 NAME 'userAccountControl' EQUALITY integerMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.27 SINGLE-VALUE )
olcObjectClasses: ( 1.3.6.1.4.1.32473.2.1 NAME 'popsTestAdUser' SUP top AUXILIARY MAY ( sAMAccountName $ userAccountControl ) )
"""  # noqa: E501  (OID 32473: RFC 5612 belgeleme numarası)

DIR_USERS = {
    # ad: (şifre, userAccountControl, gruplar)
    "ayse": ("Ayse-Pass-1", 512, ["pops-admins"]),
    "mehmet": ("Mehmet-Pass-1", 512, ["pops-viewers"]),
    "zeynep": ("Zeynep-Pass-1", 512, ["pops-superadmins", "pops-viewers"]),
    "ali.kapali": ("Ali-Pass-1", 514, ["pops-admins"]),
    "grupsuz": ("Grupsuz-Pass-1", 512, ["baska-grup"]),
    "ldapclash": ("Clash-Dir-1", 512, ["pops-admins"]),
    "totpuser": ("Totp-Pass-1", 512, ["pops-admins"]),
}


def gdn(name):
    return "cn=%s,ou=groups,%s" % (name, BASE)


def udn(name):
    return "uid=%s,ou=people,%s" % (name, BASE)


class LdapContainer:
    def __init__(self, pki):
        self.pki = pki
        self.name = "pops-sso-test-%d-%s" % (os.getpid(), secrets.token_hex(3))
        self.ldaps_port = free_port()
        self.ldap_port = free_port()

    def start(self):
        cmd = ["docker", "run", "-d", "--rm", "--name", self.name,
               "-p", "127.0.0.1:%d:636" % self.ldaps_port, "-p", "127.0.0.1:%d:389" % self.ldap_port,
               "-v", "%s:/seed/certs:ro" % self.pki.dir,
               "-e", "LDAP_DOMAIN=pops.test", "-e", "LDAP_ADMIN_PASSWORD=" + LDAP_ADMIN_PW,
               "-e", "LDAP_SEED_INTERNAL_LDAP_TLS_CRT_FILE=/seed/certs/ldap.crt",
               "-e", "LDAP_SEED_INTERNAL_LDAP_TLS_KEY_FILE=/seed/certs/ldap.key",
               "-e", "LDAP_SEED_INTERNAL_LDAP_TLS_CA_CRT_FILE=/seed/certs/ca.crt",
               "-e", "LDAP_TLS_VERIFY_CLIENT=never",
               # Hizmet hesabı: kabın salt okunur kullanıcısı (cn=pops-svc,<kök>; bütün dizini okur)
               "-e", "LDAP_READONLY_USER=true", "-e", "LDAP_READONLY_USER_USERNAME=pops-svc",
               "-e", "LDAP_READONLY_USER_PASSWORD=" + SVC_PW, IMAGE]
        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, timeout=600)
        # Kap ilk açılışta slapd'yi geçici olarak başlatıp yeniden başlatır; LDAPS'le bağlanabilmek son slapd'nin
        # (TLS ayarlı) çalıştığını gösterir
        deadline = time.time() + 120
        while True:
            try:
                self.admin().unbind()
                break
            except Exception:
                if time.time() > deadline:
                    raise RuntimeError("OpenLDAP kabı hazır olmadı")
                time.sleep(1)
        for attempt in range(10):
            r = subprocess.run(["docker", "exec", "-i", self.name, "ldapadd", "-Q", "-Y", "EXTERNAL", "-H",
                                "ldapi:///"], input=AD_SCHEMA, capture_output=True, text=True)
            if r.returncode == 0:
                break
            time.sleep(1)
        else:
            raise RuntimeError("şema eklenemedi: " + r.stderr)
        self._seed()

    def admin(self):
        from ldap3 import NONE, Connection, Server, Tls
        tls = Tls(validate=ssl.CERT_REQUIRED, ca_certs_data=self.pki.ca_pem)
        c = Connection(Server("127.0.0.1", port=self.ldaps_port, use_ssl=True, tls=tls, get_info=NONE),
                       ADMIN_DN, LDAP_ADMIN_PW, auto_bind=True)
        return c

    def _seed(self):
        c = self.admin()
        for ou in ("people", "groups"):
            c.add("ou=%s,%s" % (ou, BASE), ["organizationalUnit"], {"ou": ou})
        for name, (pw, uac, _groups) in DIR_USERS.items():
            ok = c.add(udn(name), ["inetOrgPerson", "popsTestAdUser"], {
                "uid": name, "cn": name, "sn": name, "sAMAccountName": name, "userAccountControl": str(uac),
                "userPassword": pw, "mail": name + "@okul.test"})
            assert ok, c.result
        groups = {}
        for name, (_pw, _uac, gs) in DIR_USERS.items():
            for g in gs:
                groups.setdefault(g, []).append(udn(name))
        for g, members in groups.items():
            assert c.add(gdn(g), ["groupOfUniqueNames"], {"cn": g, "uniqueMember": members}), c.result
        c.unbind()

    def delete_user(self, name):
        c = self.admin()
        c.delete(udn(name))
        c.unbind()

    def set_groups(self, name, add=(), remove=()):
        from ldap3 import MODIFY_ADD, MODIFY_DELETE
        c = self.admin()
        for g in add:
            c.modify(gdn(g), {"uniqueMember": [(MODIFY_ADD, [udn(name)])]})
        for g in remove:
            c.modify(gdn(g), {"uniqueMember": [(MODIFY_DELETE, [udn(name)])]})
        c.unbind()

    def stop(self):
        subprocess.run(["docker", "rm", "-f", self.name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def docker_ok():
    if os.environ.get("POPS_TEST_SKIP_DOCKER") == "1" or not shutil.which("docker"):
        return False
    return subprocess.run(["docker", "info"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def ldap_settings(lc, **over):
    cfg = {
        "enabled": True, "host": "127.0.0.1", "port": lc.ldaps_port, "security": "ldaps", "base_dn": BASE,
        "bind_dn": "cn=pops-svc," + BASE,
        "user_filter": "(&(objectClass=inetOrgPerson)(sAMAccountName={username}))",
        "username_attribute": "sAMAccountName", "group_base_dn": "ou=groups," + BASE,
        "group_filter": "(uniqueMember={user_dn})", "ca_pem": lc.pki.ca_pem, "timeout": 5,
        "group_map": [
            {"group": gdn("pops-admins"), "role": "admin", "pages": ["devices", "tasks", "deploy"]},
            {"group": "CN=Pops-Viewers, OU=Groups, DC=pops, DC=test", "role": "viewer", "pages": PAGES_VIEW},
            {"group": gdn("pops-superadmins"), "role": "superadmin", "pages": []},
        ],
    }
    cfg.update(over)
    return cfg


# ── Sahte OpenID Connect sağlayıcısı ────────────────────────────────────────────


class FakeIdp:
    """https://127.0.0.1:<port>: keşif, JWKS, authorize (kullanıcıyı sormadan onaylar), token (PKCE + istemci sırrı)."""

    def __init__(self, pki, redirect_uri):
        self.port = free_port()
        self.issuer = "https://127.0.0.1:%d" % self.port
        self.redirect_uri = redirect_uri
        self.ca_pem, self.other_ca_pem, self.client_ctx = pki.ca_pem, pki.other_ca_pem, pki.client_ctx
        self.key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        self.other_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        self.user = {}
        self.mutate = None
        self.codes = {}
        self.token_calls = []
        idp = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _json(self, code, obj):
                raw = json.dumps(obj).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)

            def do_GET(self):
                u = urllib.parse.urlsplit(self.path)
                q = dict(urllib.parse.parse_qsl(u.query))
                if u.path == "/.well-known/openid-configuration":
                    return self._json(200, idp.discovery())
                if u.path == "/jwks":
                    return self._json(200, idp.jwks())
                if u.path == "/authorize":
                    return idp.authorize(self, q)
                self._json(404, {"error": "not_found"})

            def do_POST(self):
                if urllib.parse.urlsplit(self.path).path != "/token":
                    return self._json(404, {"error": "not_found"})
                n = int(self.headers.get("Content-Length") or 0)
                form = dict(urllib.parse.parse_qsl(self.rfile.read(n).decode()))
                code, obj = idp.token(form, self.headers.get("Authorization", ""))
                self._json(code, obj)

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", self.port), H)
        sctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        certfile = os.path.join(pki.dir, "idp.pem")
        with open(certfile, "w") as f:
            f.write(pki.cert_pem + pki.key_pem)
        sctx.load_cert_chain(certfile)
        self.httpd.socket = sctx.wrap_socket(self.httpd.socket, server_side=True)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)

    def start(self):
        self.thread.start()

    def stop(self):
        self.httpd.shutdown()

    def discovery(self):
        return {"issuer": self.issuer, "authorization_endpoint": self.issuer + "/authorize",
                "token_endpoint": self.issuer + "/token", "jwks_uri": self.issuer + "/jwks",
                "response_types_supported": ["code"], "subject_types_supported": ["public"],
                "id_token_signing_alg_values_supported": ["RS256"], "code_challenge_methods_supported": ["S256"],
                "token_endpoint_auth_methods_supported": ["client_secret_basic"]}

    def jwks(self):
        jwk = json.loads(jwt.algorithms.RSAAlgorithm.to_jwk(self.key.public_key()))
        jwk.update(kid="k1", use="sig", alg="RS256")
        return {"keys": [jwk]}

    def authorize(self, h, q):
        if q.get("client_id") != CLIENT_ID or q.get("redirect_uri") != self.redirect_uri:
            return h._json(400, {"error": "invalid_request"})
        if q.get("code_challenge_method") != "S256" or not q.get("code_challenge") or not q.get("nonce"):
            return h._json(400, {"error": "invalid_request", "error_description": "PKCE/nonce"})
        if self.mutate == "deny":
            params = {"error": "access_denied", "state": q.get("state", "")}
        else:
            code = secrets.token_urlsafe(24)
            self.codes[code] = {"nonce": q["nonce"], "challenge": q["code_challenge"], "user": dict(self.user),
                                "redirect_uri": q["redirect_uri"]}
            params = {"code": code, "state": q.get("state", "")}
        h.send_response(302)
        h.send_header("Location", q["redirect_uri"] + "?" + urllib.parse.urlencode(params))
        h.send_header("Content-Length", "0")
        h.end_headers()

    def token(self, form, auth):
        self.token_calls.append(form.get("grant_type"))
        want = "Basic " + base64.b64encode(("%s:%s" % (CLIENT_ID, CLIENT_SECRET)).encode()).decode()
        if auth != want:
            return 401, {"error": "invalid_client"}
        c = self.codes.pop(form.get("code", ""), None)
        if not c or form.get("grant_type") != "authorization_code" or form.get("redirect_uri") != c["redirect_uri"]:
            return 400, {"error": "invalid_grant"}
        verifier = form.get("code_verifier", "")
        chal = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip("=")
        if chal != c["challenge"]:
            return 400, {"error": "invalid_grant", "error_description": "PKCE"}
        now = int(time.time())
        claims = {"iss": self.issuer, "aud": CLIENT_ID, "sub": c["user"].get("sub", "sub-x"), "iat": now,
                  "exp": now + 300, "nonce": c["nonce"]}
        claims.update({k: v for k, v in c["user"].items() if k != "sub"})
        key, alg, headers = self.key, "RS256", {"kid": "k1"}
        m = self.mutate
        if m == "nonce":
            claims["nonce"] = "someone-else"
        elif m == "expired":
            claims["iat"], claims["exp"] = now - 3600, now - 1800
        elif m == "aud":
            claims["aud"] = "another-client"
        elif m == "iss":
            claims["iss"] = "https://evil.example"
        elif m == "badsig":
            key = self.other_key
        elif m == "hs256":
            key, alg = CLIENT_SECRET, "HS256"
        return 200, {"access_token": "at-" + secrets.token_hex(8), "token_type": "Bearer",
                     "id_token": jwt.encode(claims, key, algorithm=alg, headers=headers)}


def oidc_settings(idp, **over):
    cfg = {
        "enabled": True, "display_name": "Okul hesabı", "issuer": idp.issuer, "client_id": CLIENT_ID,
        "client_secret": CLIENT_SECRET, "redirect_uri": idp.redirect_uri, "scopes": "openid email profile",
        "username_claim": "email", "groups_claim": "groups", "ca_pem": idp.ca_pem,
        "group_map": [{"group": "pops-admins", "role": "admin", "pages": ["devices", "tasks"]},
                      {"group": "/staff/viewers", "role": "viewer", "pages": PAGES_VIEW}],
        "allowed_domains": [], "default_role": "", "default_pages": [],
    }
    cfg.update(over)
    return cfg


def oidc_flow(idp, user, next_path=None, binding=None, tamper_state=False, drop_cookie=False):
    """Tarayıcının yaptığını yapar: start → sağlayıcı → callback. (callback'in Location'ı, bağ, çerez satırı)."""
    idp.user = user
    binding = binding or secrets.token_urlsafe(32)
    jar = http.cookiejar.CookieJar()
    q = {"b": hashlib.sha256(binding.encode()).hexdigest()}
    if next_path is not None:
        q["next"] = next_path
    s, body, h = req("/api/auth/oidc/start?" + urllib.parse.urlencode(q), jar=jar)
    if s != 302:
        return {"start_status": s, "start_body": body}
    set_cookie = h.get("Set-Cookie", "")
    s2, _, h2 = req(h["Location"], jar=jar, ctx=idp.client_ctx)
    loc = h2.get("Location", "")
    if tamper_state:
        loc = loc.replace("state=", "state=x")
    if drop_cookie:
        jar = http.cookiejar.CookieJar()
    s3, _, h3 = req(loc, jar=jar)
    final = h3.get("Location", "")
    qs = dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(final).query))
    return {"start_status": 302, "authorize": h["Location"], "set_cookie": set_cookie, "callback_status": s3,
            "location": final, "ticket": qs.get("sso"), "error": qs.get("sso_error"), "binding": binding,
            "cleared": h3.get("Set-Cookie", "")}


def redeem(ticket, binding):
    s, b, _ = req("/api/auth/sso/redeem", body={"ticket": ticket, "binding": binding})
    return s, b if isinstance(b, dict) else {}


# ── Testler ─────────────────────────────────────────────────────────────────────


async def cleanup(c):
    await c.execute("DELETE FROM users WHERE username = ANY($1::text[])", USERS)
    await c.execute("DELETE FROM sso_providers")
    await c.execute("DELETE FROM sso_flows")


async def audit_rows(c, action, since_id):
    rows = await c.fetch("SELECT changes FROM device_audit_logs WHERE action = $1 AND id > $2 ORDER BY id",
                         action, since_id)
    return [json.loads(r["changes"]) for r in rows]


async def test_settings_access(c, sa, admin):
    print("== ayarlar: yalnızca süper admin")
    chk(req("/api/sso/settings", admin)[0] == 403, "yönetici ayarları okuyamaz")
    chk(req("/api/sso/settings/ldap", admin, {"enabled": False}, method="PUT")[0] == 403, "yönetici yazamaz")
    chk(req("/api/sso/test/ldap", admin, {"host": "127.0.0.1"})[0] == 403, "yönetici sınayamaz")
    s, b, _ = req("/api/auth/sso")
    chk(s == 200 and b == {"ldap": False, "oidc": False, "oidc_name": ""}, "giriş sayfası: ikisi de kapalı")
    chk(req("/api/auth/oidc/start?b=" + "a" * 64)[0] == 404, "OIDC kapalıyken start 404")


async def test_ldap(c, sa, lc):
    print("== LDAP ayarları")
    since = await c.fetchval("SELECT coalesce(max(id), 0) FROM device_audit_logs")
    cfg = ldap_settings(lc)
    bad = dict(cfg, security="plain", port=lc.ldap_port, bind_password=SVC_PW)
    s, b, _ = req("/api/sso/settings/ldap", sa, bad, method="PUT")
    chk(s == 400 and "Şifresiz" in (b or {}).get("detail", ""), "şifresiz LDAP bayraksız reddedildi")
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(cfg, user_filter="(uid=x)", bind_password=SVC_PW), method="PUT")
    chk(s == 400, "{username} içermeyen filtre reddedildi")
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(cfg, enabled=True), method="PUT")
    chk(s == 400 and "şifresi" in (b or {}).get("detail", ""), "hizmet hesabı şifresiz etkinleştirilemez")
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(cfg, bind_password=SVC_PW), method="PUT")
    chk(s == 200 and b.get("enabled") and b.get("has_secret") and "bind_password" not in b
        and SVC_PW not in json.dumps(b), "kaydedildi; sır dönmez")
    stored = await c.fetchval("SELECT secret FROM sso_providers WHERE kind = 'ldap'")
    chk(stored.startswith("v1:") and SVC_PW not in stored and secretbox.unseal(stored) == SVC_PW,
        "hizmet hesabı şifresi şifreli saklanır")
    s, b, _ = req("/api/sso/settings", sa)
    chk(s == 200 and b["ldap"]["host"] == "127.0.0.1" and b["ldap"]["has_secret"] and SVC_PW not in json.dumps(b),
        "GET ayarları sırsız döner")
    s, b, _ = req("/api/auth/sso")
    chk(b.get("ldap") is True and b.get("oidc") is False, "giriş sayfası: LDAP açık")
    audits = await audit_rows(c, "sso_settings", since)
    chk(audits and audits[-1].get("kind") == "ldap" and audits[-1].get("secret_changed") is True
        and SVC_PW not in json.dumps(audits), "değişiklik denetim kaydında, şifresiz")
    # Sunucu değişip şifre yeniden girilmezse kayıtlı şifre kullanılmaz (başka adrese gönderilmesin)
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(cfg, host="localhost"), method="PUT")
    chk(s == 400 and "yeniden" in (b or {}).get("detail", ""), "sunucu değişince şifre yeniden istenir")

    print("== Bağlantıyı sına")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, test_username="ayse"))
    chk(s == 200 and b.get("ok") and b.get("security") == "ldaps" and b.get("role") == "admin"
        and b.get("user_dn") == udn("ayse") and not b.get("disabled"), "LDAPS: hizmet hesabı + kullanıcı + rol")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, ca_pem=lc.pki.other_ca_pem, bind_password=SVC_PW))
    chk(s == 200 and not b.get("ok") and "güvenilir" in b.get("message", ""), "yanlış CA reddedildi")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, ca_pem=lc.pki.other_ca_pem))
    chk(s == 200 and not b.get("ok") and "şifresi" in b.get("message", ""),
        "CA değişince kayıtlı şifre kullanılmaz (başka bir CA'nın sunucusuna gitmez)")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, security="starttls", port=lc.ldap_port))
    chk(s == 200 and not b.get("ok") and "şifresi" in b.get("message", ""),
        "bağlantı türü değişince kayıtlı şifre kullanılmaz")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, host="localhost", bind_password=SVC_PW))
    chk(s == 200 and not b.get("ok") and "uyuşmuyor" in b.get("message", ""), "sertifikadaki ad uyuşmazlığı reddedildi")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, host="localhost"))
    chk(s == 200 and not b.get("ok") and "şifresi" in b.get("message", ""),
        "başka sunucuya kayıtlı şifre gönderilmez")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, ca_pem="", bind_password=SVC_PW))
    chk(s == 200 and not b.get("ok") and "güvenilir" in b.get("message", ""),
        "CA verilmezse sistem deposu: test CA'sına güvenilmez")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, security="starttls", port=lc.ldap_port, bind_password=SVC_PW))
    chk(s == 200 and b.get("ok") and b.get("security") == "starttls", "StartTLS sınandı")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, security="plain", port=lc.ldap_port))
    chk(s == 400, "şifresiz LDAP sınanmaz bile")
    plain = dict(cfg, security="plain", port=lc.ldap_port, allow_insecure_for_tests=True, bind_password=SVC_PW)
    s, b, _ = req("/api/sso/test/ldap", sa, plain)
    if INSECURE_OK:
        chk(s == 200 and b.get("ok") and b.get("security") == "plain",
            "test bayrağı + POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1: şifresiz LDAP (yalnızca test)")
    else:
        chk(s == 400 and "POPS_SSO_ALLOW_INSECURE_FOR_TESTS" in (b or {}).get("detail", ""),
            "sunucu ortamında izin yokken test bayrağı reddedildi")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, bind_password="wrong"))
    chk(s == 200 and not b.get("ok") and "Hizmet hesabıyla" in b.get("message", ""), "yanlış hizmet hesabı şifresi")
    s, b, _ = req("/api/sso/test/ldap", sa, dict(cfg, test_username="yok-boyle"))
    chk(s == 200 and not b.get("ok") and "bulunamadı" in b.get("message", ""), "olmayan kullanıcı")

    print("== LDAP ile giriş")
    s, b = login("ayse", DIR_USERS["ayse"][0])
    chk(s == 200 and b.get("status") == "success" and b.get("role") == "admin" and b.get("username") == "ayse"
        and json.loads(b.get("permissions")) == ["deploy", "devices", "tasks"], "LDAPS ile giriş, grup → yönetici")
    ayse_token = b.get("token")
    row = await c.fetchrow("SELECT * FROM users WHERE username = 'ayse'")
    chk(row and row["auth_source"] == "ldap" and row["password_hash"] == "!sso"
        and (row["external_id"] or "").startswith("uuid:") and row["last_login"], "yerel kayıt: ldap, şifresiz, bağlı")
    chk(req("/api/devices", ayse_token)[0] == 200, "dizin hesabının oturumu çalışır")
    s, b = login("AYSE", DIR_USERS["ayse"][0])
    chk(s == 200 and b.get("username") == "ayse", "büyük harfle yazılan ad aynı hesaba girer")
    chk(await c.fetchval("SELECT count(*) FROM users WHERE lower(username) = 'ayse'") == 1, "ikinci kayıt açılmadı")
    s, b = login("mehmet", DIR_USERS["mehmet"][0])
    chk(s == 200 and b.get("role") == "viewer" and json.loads(b.get("permissions")) == PAGES_VIEW,
        "izleyici ve sayfaları (DN büyük/küçük harf ve boşluk farkı önemsiz)")
    s, b = login("zeynep", DIR_USERS["zeynep"][0])
    chk(s == 200 and b.get("role") == "superadmin" and json.loads(b.get("permissions")) == [],
        "en yüksek rol kazanır (süper admin)")
    created = await audit_rows(c, "sso_user_created", since)
    chk({a.get("user") for a in created} >= {"ayse", "mehmet", "zeynep"}, "ilk girişte oluşturma denetim kaydında")

    print("== LDAP: reddedilenler")
    s, b = login("ayse", "wrong-password")
    chk(s == 401 and b.get("detail") == "Geçersiz kullanıcı adı veya şifre", "yanlış şifre 401")
    s, b = login("ayse", "")
    chk(s == 401, "boş şifre 401 (anonim bağlanma sayılmaz)")
    for inj in ("*", "ays*", "ayse)(sAMAccountName=*", "*)(|(objectClass=*"):
        chk(login(inj, DIR_USERS["ayse"][0])[0] == 401, "filtre enjeksiyonu 401: %s" % inj)
    s, b = login("ali.kapali", DIR_USERS["ali.kapali"][0])
    chk(s == 403 and "devre dışı" in b.get("detail", ""), "devre dışı hesap reddedildi")
    s, b = login("ali.kapali", "wrong")
    chk(s == 401, "devre dışı hesap yanlış şifreyle 401 (durumu sızdırmaz)")
    s, b = login("grupsuz", DIR_USERS["grupsuz"][0])
    chk(s == 403 and "eşlenen" in b.get("detail", ""), "eşlenen grupta olmayan reddedildi")
    chk(not await c.fetchval("SELECT count(*) FROM users WHERE username IN ('ali.kapali', 'grupsuz')"),
        "reddedilenlere kayıt açılmadı")
    s, b = login("ldapclash", DIR_USERS["ldapclash"][0])
    chk(s == 401, "aynı adlı yerel hesap dizin şifresiyle açılmaz")
    s, b = login("ldapclash", "Clash-Local-1")
    chk(s == 200 and b.get("role") == "viewer", "yerel hesap kendi şifresiyle girer")
    chk(await c.fetchval("SELECT auth_source FROM users WHERE username = 'ldapclash'") == "local",
        "yerel hesap dizine bağlanmadı")
    s, b = login("LDAPclash", DIR_USERS["ldapclash"][0])
    chk(s == 401, "büyük/küçük harf farkıyla yazılan yerel ad da dizine gitmez")
    # Filtre e-postayla da bulursa dizindeki ad (sAMAccountName) yerel hesabın adı olur: bağlanmaz, reddedilir
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(
        cfg, user_filter="(|(sAMAccountName={username})(mail={username}))", bind_password=SVC_PW), method="PUT")
    s, b = login("ldapclash@okul.test", DIR_USERS["ldapclash"][0])
    chk(s == 403 and "başka bir hesaba" in b.get("detail", ""), "dizin adı yerel hesapla çakışınca bağlanmaz")
    chk(await c.fetchval("SELECT count(*) FROM users WHERE lower(username) = 'ldapclash'") == 1
        and await c.fetchval("SELECT auth_source FROM users WHERE username = 'ldapclash'") == "local",
        "çakışmada yerel hesap değişmedi")
    # Şifre doğrulanmadan kimsenin oturumu kapanmaz: filtre bulamasa da hesap dizinde duruyorsa
    tv = await c.fetchval("SELECT token_version FROM users WHERE username = 'zeynep'")
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(cfg, user_filter="(mail={username})", bind_password=SVC_PW),
                  method="PUT")
    s, b = login("zeynep", "anything")
    chk(s == 401 and await c.fetchval("SELECT token_version FROM users WHERE username = 'zeynep'") == tv,
        "bulunamayan ama dizinde duran hesabın oturumları kapanmadı")
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(cfg, bind_password=SVC_PW), method="PUT")
    chk(s == 200, "filtre geri alındı")

    print("== LDAP: 2FA, StartTLS, grup değişimi, dizinden silinme")
    s, b = login("totpuser", DIR_USERS["totpuser"][0])
    chk(s == 200, "2FA öncesi giriş")
    secret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP"
    await c.execute("UPDATE users SET totp_secret = $1, totp_enabled = TRUE WHERE username = 'totpuser'",
                    secretbox.seal(secret))
    s, b = login("totpuser", DIR_USERS["totpuser"][0])
    chk(s == 200 and b.get("status") == "totp_required" and b.get("challenge"), "2FA açık dizin hesabı kod ister")
    s, b2, _ = req("/api/admin/login/totp", body={"challenge": b.get("challenge"), "otp": totp(secret)})
    chk(s == 200 and b2.get("status") == "success" and b2.get("username") == "totpuser", "kodla giriş tamam")
    s, b = login("totpuser", "wrong")
    chk(s == 401, "2FA'lı hesapta yanlış dizin şifresi 401")

    starttls = dict(cfg, security="starttls", port=lc.ldap_port, bind_password=SVC_PW)
    s, b, _ = req("/api/sso/settings/ldap", sa, starttls, method="PUT")
    chk(s == 200, "StartTLS'e geçildi")
    s, b = login("ayse", DIR_USERS["ayse"][0])
    chk(s == 200 and b.get("role") == "admin", "StartTLS ile giriş")
    s, b, _ = req("/api/sso/settings/ldap", sa, dict(cfg, group_base_dn="", bind_password=SVC_PW), method="PUT")
    chk(s == 200, "yalnızca memberOf (grup araması kapalı)")
    s, b = login("mehmet", DIR_USERS["mehmet"][0])
    chk(s == 200 and b.get("role") == "viewer", "memberOf ile grup bulundu")

    tv = await c.fetchval("SELECT token_version FROM users WHERE username = 'mehmet'")
    lc.set_groups("mehmet", add=["pops-admins"])
    s, b = login("mehmet", DIR_USERS["mehmet"][0])
    chk(s == 200 and b.get("role") == "admin", "gruba eklenince rol yükseldi")
    chk(await c.fetchval("SELECT token_version FROM users WHERE username = 'mehmet'") == tv + 1,
        "rol değişince eski oturumlar kapanır")
    synced = await audit_rows(c, "sso_role_synced", since)
    chk(any(a.get("user") == "mehmet" and a["after"]["role"] == "admin" for a in synced), "rol değişimi denetimde")
    lc.set_groups("mehmet", remove=["pops-admins", "pops-viewers"])
    mehmet_token = b.get("token")
    s, b = login("mehmet", DIR_USERS["mehmet"][0])
    chk(s == 403, "gruptan çıkarılan reddedildi")
    chk(req("/api/devices", mehmet_token)[0] == 401, "gruptan çıkarılanın açık oturumu kapandı")

    s, b = login("ayse", DIR_USERS["ayse"][0])
    ayse_token = b.get("token")
    lc.delete_user("ayse")
    s, b = login("ayse", DIR_USERS["ayse"][0])
    chk(s == 401, "dizinden silinen hesap giremez")
    chk(req("/api/devices", ayse_token)[0] == 401, "dizinden silinen hesabın açık oturumu kapandı")

    print("== yerel hesaplar ve dizin kapalıyken")
    first = await c.fetchval("SELECT min(id) FROM users WHERE role = 'superadmin' AND auth_source = 'local'")
    frow = await c.fetchrow("SELECT username, role, permissions FROM users WHERE id = $1", first)
    s, b, _ = req("/api/admin/users/%d" % first, sa, {"username": frow["username"], "role": "superadmin",
                                                      "permissions": frow["permissions"] or "[]",
                                                      "auth_source": "ldap"}, method="PUT")
    chk(s == 400 and "İlk süper admin" in (b or {}).get("detail", ""), "ilk yerel süper admin dizine çevrilemez")
    uid = await c.fetchval("SELECT id FROM users WHERE username = 'ssoadmin'")
    s, b, _ = req("/api/admin/users/%d" % uid, sa, {"username": "ssoadmin", "role": "admin", "permissions": "[]",
                                                    "auth_source": "ldap"}, method="PUT")
    chk(s == 200, "yerel yönetici dizin hesabına çevrildi")
    r = await c.fetchrow("SELECT auth_source, password_hash FROM users WHERE id = $1", uid)
    chk(r["auth_source"] == "ldap" and r["password_hash"] == "!sso", "çevrilen hesabın yerel şifresi silindi")
    s, b, _ = req("/api/admin/users/%d" % uid, sa, {"username": "ssoadmin", "role": "admin", "permissions": "[]",
                                                    "auth_source": "local"}, method="PUT")
    chk(s == 400, "yerele dönüş şifresiz olmaz")
    s, b, _ = req("/api/admin/users/%d" % uid, sa, {"username": "ssoadmin", "role": "admin", "permissions": "[]",
                                                    "auth_source": "local", "password": "Back-Local-1"}, method="PUT")
    chk(s == 200 and login("ssoadmin", "Back-Local-1")[0] == 200, "yerele döndü, yeni şifreyle girer")
    s, b, _ = req("/api/admin/users", sa, {"username": "ssoplain", "role": "viewer", "permissions": "[]",
                                           "auth_source": "ldap", "password": "x"})
    chk(s == 400, "dizin hesabına yerel şifre verilemez")
    s, users, _ = req("/api/admin/users", sa)
    chk(s == 200 and all("auth_source" in u for u in users["users"]), "kullanıcı listesinde kimlik kaynağı")

    lc.stop()
    t0 = time.time()
    s, b = login("ssolocal", LOCAL_PW)
    chk(s == 200 and b.get("role") == "superadmin", "dizin kapalıyken yerel süper admin girer")
    s, b = login("zeynep", DIR_USERS["zeynep"][0])
    chk(s == 503 and "yerel" in b.get("detail", ""), "dizin kapalıyken dizin hesabı 503 (%.1f sn)" % (time.time() - t0))


async def test_oidc(c, sa, pki):
    print("== OIDC ayarları")
    since = await c.fetchval("SELECT coalesce(max(id), 0) FROM device_audit_logs")
    idp = FakeIdp(pki, HTTP + "/api/auth/oidc/callback")
    idp.start()
    try:
        await _oidc(c, sa, idp, since)
    finally:
        idp.stop()


async def _oidc(c, sa, idp, since):
    cfg = oidc_settings(idp)
    s, b, _ = req("/api/sso/settings/oidc", sa, dict(cfg, issuer=idp.issuer.replace("https", "http")), method="PUT")
    chk(s == 400 and "https" in (b or {}).get("detail", ""), "https olmayan sağlayıcı reddedildi")
    s, b, _ = req("/api/sso/settings/oidc", sa, dict(cfg, redirect_uri="https://evil.example/cb"), method="PUT")
    chk(s == 400, "dönüş adresi /api/auth/oidc/callback ile bitmeli")
    s, b, _ = req("/api/sso/settings/oidc", sa, dict(cfg, redirect_uri="http://pops.okul.test/api/auth/oidc/callback"),
                  method="PUT")
    chk(s == 400, "loopback dışında http dönüş adresi reddedildi")
    s, b, _ = req("/api/sso/settings/oidc", sa, dict(cfg, default_role="superadmin"), method="PUT")
    chk(s == 422, "alan adına süper admin verilemez")
    s, b, _ = req("/api/sso/test/oidc", sa, cfg)
    chk(s == 200 and b.get("ok") and b.get("keys") == 1 and b.get("pkce") and b.get("issuer") == idp.issuer,
        "Bağlantıyı sına: keşif + JWKS")
    s, b, _ = req("/api/sso/test/oidc", sa, dict(cfg, ca_pem=idp.other_ca_pem))
    chk(s == 200 and not b.get("ok") and "TLS" in b.get("message", ""), "yanlış CA ile sağlayıcı reddedildi")
    s, b, _ = req("/api/sso/settings/oidc", sa, cfg, method="PUT")
    chk(s == 200 and b.get("has_secret") and CLIENT_SECRET not in json.dumps(b), "kaydedildi; sır dönmez")
    stored = await c.fetchval("SELECT secret FROM sso_providers WHERE kind = 'oidc'")
    chk(stored.startswith("v1:") and CLIENT_SECRET not in stored, "istemci sırrı şifreli")
    s, b, _ = req("/api/auth/sso")
    chk(b.get("oidc") is True and b.get("oidc_name") == "Okul hesabı", "giriş sayfası: OIDC düğmesi adıyla")
    s, b, _ = req("/api/sso/settings/oidc", sa, dict(cfg, issuer=idp.issuer + "/x", client_secret=None),
                  method="PUT")
    chk(s == 400, "sağlayıcı değişince istemci sırrı yeniden istenir")

    print("== OIDC: kod akışı")
    user = {"sub": "s-veli", "email": "veli@okul.test", "email_verified": True, "groups": ["pops-admins"]}
    f = oidc_flow(idp, user, next_path="/devices")
    auth_q = dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(f["authorize"]).query))
    chk(auth_q.get("code_challenge_method") == "S256" and auth_q.get("nonce") and auth_q.get("state")
        and auth_q.get("redirect_uri") == idp.redirect_uri and "openid" in auth_q.get("scope", ""),
        "yetkilendirme isteği: PKCE S256, state, nonce, ayardaki dönüş adresi")
    ck = f["set_cookie"].lower()
    chk("pops_oidc_state=" in ck and "httponly" in ck and "samesite=lax" in ck and "path=/api/auth/oidc" in ck,
        "state çerezi HttpOnly, SameSite=Lax, yolu /api/auth/oidc")
    chk(f["location"].startswith(HTTP + "/login?sso=") and f["ticket"] and "max-age=0" in f["cleared"].lower(),
        "geri dönüş: giriş sayfasına bilet, state çerezi silindi")
    s, b = redeem(f["ticket"], f["binding"])
    chk(s == 200 and b.get("status") == "success" and b.get("role") == "admin" and b.get("username") == "veli@okul.test"
        and b.get("next") == "/devices" and json.loads(b["permissions"]) == ["devices", "tasks"],
        "bilet bozduruldu: oturum, rol ve dönüş yolu")
    chk(req("/api/devices", b.get("token"))[0] == 200, "OIDC oturumu çalışır")
    row = await c.fetchrow("SELECT * FROM users WHERE username = 'veli@okul.test'")
    chk(row["auth_source"] == "oidc" and row["external_id"] == idp.issuer + "|s-veli"
        and row["password_hash"] == "!sso",
        "yerel kayıt: oidc, iss|sub ile bağlı")
    chk(redeem(f["ticket"], f["binding"])[0] == 401, "bilet ikinci kez kullanılamaz")
    f = oidc_flow(idp, user)
    chk(f["ticket"] and not f["error"], "ikinci akış bilet aldı")
    chk(redeem(f["ticket"], secrets.token_urlsafe(32))[0] == 401, "başka tarayıcının (bağın) bileti reddedildi")
    chk(redeem(f["ticket"], f["binding"])[0] == 401, "reddedilen bilet de yandı")
    chk(idp.token_calls and set(idp.token_calls) == {"authorization_code"}, "kod PKCE ve istemci sırrıyla değişti")
    s, b, _ = req("/api/auth/sso/redeem", body={"ticket": "x" * 43, "binding": "y" * 43})
    chk(s == 401, "uydurma bilet 401")

    print("== OIDC: reddedilenler")
    chk(oidc_flow(idp, user, tamper_state=True)["error"] == "state", "yanlış state reddedildi")
    unv = {"sub": "s-unverified", "email": "veli@okul.test", "email_verified": False, "groups": ["pops-admins"]}
    chk(oidc_flow(idp, unv)["error"] == "access", "doğrulanmamış e-posta kullanıcı adı olamaz (başkasının adı)")
    chk(oidc_flow(idp, user, drop_cookie=True)["error"] == "state", "state çerezi yoksa reddedildi")
    for m, what in (("nonce", "yanlış nonce"), ("expired", "süresi dolmuş jeton"), ("aud", "yanlış aud"),
                    ("iss", "yanlış iss"), ("badsig", "geçersiz imza"), ("hs256", "HS256 (simetrik) imza")):
        idp.mutate = m
        f = oidc_flow(idp, user)
        chk(f["error"] == "token" and not f["ticket"], "%s reddedildi" % what)
    idp.mutate = "deny"
    chk(oidc_flow(idp, user)["error"] == "provider", "sağlayıcının reddi (access_denied)")
    idp.mutate = None
    for evil in ("https://evil.example/", "//evil.example", "/\\evil.example", "javascript:alert(1)",
                 "/devices/../../x", "http:/evil.example"):
        f = oidc_flow(idp, user, next_path=evil)
        chk(f.get("start_status") == 400, "açık yönlendirme reddedildi: %s" % evil)
    s, _, h = req("/api/auth/oidc/start?b=%s&redirect_uri=https://evil.example/cb" % ("a" * 64))
    chk(s == 302 and "evil.example" not in h.get("Location", ""), "istekteki redirect_uri yok sayılır")
    chk(req("/api/auth/oidc/start?b=short")[0] == 422, "bağsız start reddedildi")
    s, _, h = req("/api/auth/oidc/callback?state=nope&code=x")
    chk(s == 302 and h.get("Location") == HTTP + "/login?sso_error=state", "uydurma state: yalnızca giriş sayfası")

    print("== OIDC: alan adı, varsayılan rol, çakışma, 2FA")
    nog = {"sub": "s-deniz", "email": "deniz@okul.test", "email_verified": True, "groups": ["other"]}
    chk(oidc_flow(idp, nog)["error"] == "access", "eşlenen grubu yok ve varsayılan rol yok: reddedildi")
    dom = dict(cfg, allowed_domains=["okul.test"], default_role="viewer", default_pages=["reports"])
    s, b, _ = req("/api/sso/settings/oidc", sa, dom, method="PUT")
    chk(s == 200, "alan adı + varsayılan rol kaydedildi")
    f = oidc_flow(idp, nog)
    s, b = redeem(f["ticket"], f["binding"])
    chk(s == 200 and b.get("role") == "viewer" and json.loads(b["permissions"]) == ["reports"],
        "izinli alan adı varsayılan rolü aldı")
    unv = {"sub": "s-kaan", "email": "kaan@okul.test", "email_verified": False, "groups": []}
    chk(oidc_flow(idp, unv)["error"] == "access", "doğrulanmamış e-posta varsayılan rolü almaz")
    other = {"sub": "s-x", "email": "x@baska.test", "email_verified": True, "groups": ["pops-admins"]}
    chk(oidc_flow(idp, other)["error"] == "access", "listede olmayan alan adı grupla da giremez")
    clash = {"sub": "s-clash", "email": "ssolocal@okul.test", "email_verified": True, "groups": ["pops-admins"]}
    await c.execute("INSERT INTO users (username, password_hash, role, permissions) "
                    "VALUES ('ssolocal@okul.test', $1, 'admin', '[]')",
                    bcrypt.hashpw(b"x-local-1", bcrypt.gensalt(4)).decode())
    chk(oidc_flow(idp, clash)["error"] == "conflict", "aynı adlı yerel hesap OIDC ile ele geçirilemez")
    tfa = {"sub": "s-2fa", "email": "oidc2fa@okul.test", "email_verified": True, "groups": ["pops-admins"]}
    f = oidc_flow(idp, tfa)
    chk(redeem(f["ticket"], f["binding"])[0] == 200, "2FA öncesi OIDC girişi")
    secret = "KRSXG5CTMVRXEZLUKN2XAZLSKNSWG4TF"
    await c.execute("UPDATE users SET totp_secret = $1, totp_enabled = TRUE WHERE username = 'oidc2fa@okul.test'",
                    secretbox.seal(secret))
    f = oidc_flow(idp, tfa)
    s, b = redeem(f["ticket"], f["binding"])
    chk(s == 200 and b.get("status") == "totp_required" and "token" not in b, "2FA açık OIDC hesabı kod ister")
    s, b2, _ = req("/api/admin/login/totp", body={"challenge": b.get("challenge"), "otp": totp(secret)})
    chk(s == 200 and b2.get("status") == "success", "OIDC + 2FA tamam")
    chk(login("veli@okul.test", "anything")[0] == 401, "OIDC hesabı şifreyle giremez")

    f = oidc_flow(idp, user)
    s, b = redeem(f["ticket"], f["binding"])
    veli_token = b.get("token")
    chk(s == 200 and req("/api/devices", veli_token)[0] == 200, "kapatmadan önce OIDC oturumu açık")
    s, b, _ = req("/api/sso/settings/oidc", sa, dict(cfg, enabled=False, client_secret=None), method="PUT")
    chk(s == 200 and not b.get("enabled") and b.get("has_secret"), "kapatıldı, sır kaldı")
    chk(req("/api/devices", veli_token)[0] == 401, "sağlayıcı kapatılınca OIDC hesaplarının oturumları kapandı")
    chk(req("/api/auth/oidc/start?b=" + "a" * 64)[0] == 404, "kapalıyken start 404")
    audits = await audit_rows(c, "sso_settings", since)
    chk(audits and all(CLIENT_SECRET not in json.dumps(a) for a in audits) and audits[-1]["kind"] == "oidc",
        "OIDC değişiklikleri denetimde, sırsız")


async def setup_users(c):
    await c.execute(
        "INSERT INTO users (username, password_hash, role, permissions) VALUES "
        "('ssolocal', $1, 'superadmin', '[]'), ('ssoadmin', $2, 'admin', '[]'), ('ldapclash', $3, 'viewer', '[]')",
        bcrypt.hashpw(LOCAL_PW.encode(), bcrypt.gensalt(4)).decode(),
        bcrypt.hashpw(b"Admin-Pass-1", bcrypt.gensalt(4)).decode(),
        bcrypt.hashpw(b"Clash-Local-1", bcrypt.gensalt(4)).decode())


async def main():
    c = await conn()
    await cleanup(c)
    await setup_users(c)
    sa = server.create_jwt("ssolocal", "superadmin", 0)
    admin = server.create_jwt("ssoadmin", "admin", 0)
    tmp = tempfile.mkdtemp(prefix="pops-sso-")
    pki = Pki(tmp)
    lc = None
    try:
        await test_settings_access(c, sa, admin)
        await test_oidc(c, sa, pki)
        if docker_ok():
            lc = LdapContainer(pki)
            lc.start()
            await test_ldap(c, sa, lc)
        else:
            print("== LDAP: ATLANDI (docker yok ya da POPS_TEST_SKIP_DOCKER=1)")
    finally:
        if lc:
            lc.stop()
        await cleanup(c)
        await c.close()
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM SSO TESTLERI GECTI")


async def serve():
    """Tarayıcı denemesi: dizin ve sahte sağlayıcı çalışır, sunucu ikisine ayarlanır. Çıkış: Ctrl+C / SIGTERM."""
    import signal
    c = await conn()
    tmp = tempfile.mkdtemp(prefix="pops-sso-serve-")
    pki = Pki(tmp)
    lc = LdapContainer(pki)
    lc.start()
    panel = os.environ.get("POPS_TEST_PANEL", HTTP).rstrip("/")
    idp = FakeIdp(pki, panel + "/api/auth/oidc/callback")
    idp.user = {"sub": "s-veli", "email": "veli@okul.test", "email_verified": True, "name": "Veli Öğretmen",
                "groups": ["pops-admins"]}
    idp.start()
    who = await c.fetchval("SELECT username FROM users WHERE role = 'superadmin' AND auth_source = 'local' "
                           "ORDER BY id LIMIT 1")
    sa = server.create_jwt(who, "superadmin",
                           await c.fetchval("SELECT token_version FROM users WHERE username = $1", who))
    print(req("/api/sso/settings/ldap", sa, dict(ldap_settings(lc), bind_password=SVC_PW), method="PUT")[0])
    print(req("/api/sso/settings/oidc", sa, oidc_settings(idp), method="PUT")[0])
    print("LDAP kullanıcıları:", {k: v[0] for k, v in DIR_USERS.items()})
    print("hazır; sağlayıcı", idp.issuer, flush=True)
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)
    await stop.wait()
    idp.stop()
    lc.stop()
    shutil.rmtree(tmp, ignore_errors=True)
    await c.close()


if __name__ == "__main__":
    asyncio.run(serve() if "--serve" in sys.argv else main())
