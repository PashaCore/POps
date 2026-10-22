#!/usr/bin/env python3
"""Fuzz hedefi: bildirim ayarlarının denetimi (Backend/pops/routers/notifications.py _validated, pops/notify.py
resolve_webhook): yöneticinin yazdığı e-posta listesi ve webhook adresi, adresin DNS yanıtı.

Girdi satırları: 1. webhook adresi, 2. e-posta listesi, kalan baytlar DNS yanıtı (ilk bayt: tek ise
NOTIFY_WEBHOOK_ALLOW_PRIVATE=1; sonra adres başına bir tür baytı, çiftse 4 baytlık IPv4, tekse 16 baytlık IPv6). DNS
sahtedir, ağa çıkılmaz: IP yazılmış adresler gerçek getaddrinfo ile (yalnızca sayısal, AI_NUMERICHOST) çözülür.

Denetlenen: _validated() ayarları döndürür ya da 400 verir, başka hiçbir hata yok; kabul edilen e-postalar en çok 20,
her biri tek bir adres; kabul edilen webhook http/https, en çok 500 karakter ve DNS'in döndürdüğü HER adres izinli
(izin kapalıyken loopback, özel ağ, link-local / bulut metadata, CGNAT, ULA, belirtilmemiş, çoklu ve genel yayın
adresleri hiçbir yazımla geçmez; IPv4-mapped IPv6 dahil); bağlanılacak IP DNS'in döndürdüklerinden biri (DNS
rebinding'e karşı).

    python fuzz/fuzz_notify_settings.py -max_total_time=60 <yeni-girdiler-klasörü> fuzz/corpus/notify_settings
"""

import ipaddress
import socket

import common

from fastapi import HTTPException  # noqa: E402

with common.backend_imports():
    from pops import config, notify
    from pops.models import NotifySettingsInput
    from pops.routers import notifications

# İzin kapalıyken hiçbir zaman geçmemesi gereken ağlar (kodun is_global kuralından bağımsız liste)
FORBIDDEN = [ipaddress.ip_network(n) for n in (
    "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16",
    "224.0.0.0/4", "240.0.0.0/4", "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
)]

_real_getaddrinfo = socket.getaddrinfo
DNS = []


def fake_getaddrinfo(host, port, *args, **kwargs):
    try:
        return _real_getaddrinfo(host, port, type=socket.SOCK_STREAM, flags=socket.AI_NUMERICHOST)
    except socket.gaierror:
        pass
    if not DNS:
        raise socket.gaierror(socket.EAI_NONAME, "Name or service not known")
    return [(socket.AF_INET6 if ":" in a else socket.AF_INET, socket.SOCK_STREAM, 6, "", (a, port)) for a in DNS]


socket.getaddrinfo = fake_getaddrinfo


def forbidden(address):
    a = ipaddress.ip_address(address.split("%")[0])
    if isinstance(a, ipaddress.IPv6Address) and a.ipv4_mapped is not None:
        a = a.ipv4_mapped
    return a == ipaddress.ip_address("255.255.255.255") or any(a in n for n in FORBIDDEN if n.version == a.version)


def parse_dns(raw):
    allow_private = bool(raw[:1] and raw[0] & 1)
    out, i = [], 1
    while i < len(raw):
        if raw[i] % 2 == 0:
            chunk, i = raw[i + 1:i + 5], i + 5
            if len(chunk) == 4:
                out.append(str(ipaddress.IPv4Address(chunk)))
        else:
            chunk, i = raw[i + 1:i + 17], i + 17
            if len(chunk) == 16:
                out.append(str(ipaddress.IPv6Address(chunk)))
    return allow_private, out


def TestOneInput(data):
    url, _, rest = data.partition(b"\n")
    emails, _, dns = rest.partition(b"\n")
    allow_private, DNS[:] = parse_dns(dns)
    config.NOTIFY_WEBHOOK_ALLOW_PRIVATE = allow_private
    settings = NotifySettingsInput(
        enabled=True, min_severity="high", email_to=emails.decode("utf-8", "replace"),
        webhook_url=url.decode("utf-8", "replace"),
    )
    try:
        out = notifications._validated(settings)
    except HTTPException as exc:
        assert exc.status_code == 400, exc.status_code
        return
    accepted = [e.strip() for e in out["notify_email_to"].split(",") if e.strip()]
    assert len(accepted) <= 20 and all(len(e) <= 254 and "@" in e and not any(c.isspace() for c in e)
                                       for e in accepted), accepted
    hook = out["notify_webhook_url"]
    if not hook:
        return
    assert hook.startswith(("http://", "https://")) and len(hook) <= 500, hook
    u, ip = notify.resolve_webhook(hook)
    assert u.scheme in ("http", "https") and u.hostname, hook
    answers = sorted({i[4][0] for i in socket.getaddrinfo(u.hostname, u.port or 80)})
    assert ip in answers, "bağlanılacak IP DNS yanıtında yok: %s %s" % (ip, answers)
    for a in answers:
        assert not ipaddress.ip_address(a.split("%")[0]).is_multicast, "çoklu yayın adresi kabul edildi: %s" % a
        assert allow_private or not forbidden(a), "iç ağ adresi kabul edildi: %s (%s)" % (a, hook)


if __name__ == "__main__":
    common.run(TestOneInput)
