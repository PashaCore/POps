"""Süreç içi metrikler ve istek middleware'i (bağımlılıksız; Prometheus metin biçimi).

İstek middleware'i (RequestContextMiddleware):
  * her HTTP isteğine / WebSocket bağlantısına request_id verir (gelen X-Request-ID güvenliyse o kullanılır)
    ve yanıta X-Request-ID başlığını ekler;
  * HTTP istek sayısı ve süresini rota ŞABLONUNA göre sayar (/api/devices/{pc_name}); ham yol etiket
    olmaz, yoksa her cihaz adı ayrı bir seri açardı;
  * yakalanmayan istisnayı request_id ile loglar ve yeniden fırlatır (yanıtı Starlette üretir).

Tek uvicorn worker'ı olduğu için sayaçlar süreç içindedir; yeniden başlatmada sıfırlanır (Prometheus bunu
counter reset olarak anlar).
"""

import logging
import re
import time
import uuid

from pops.logs import request_id_var

log = logging.getLogger("pops.http")

STARTED_AT = time.time()

# Saniye; panel uçlarının çoğu <100 ms, dışa aktarma ve dağıtım uçları saniyeler sürebilir
BUCKETS = (0.01, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0)

http_requests = {}      # (method, route, status) -> sayı
http_duration = {}      # route -> [bucket sayıları..., +Inf, toplam süre]
ws_sessions = {}        # route -> kapanan WebSocket oturumu sayısı
unhandled_errors = [0]

_SAFE_RID = re.compile(r"^[A-Za-z0-9._-]{8,64}$")


def _route_of(scope):
    route = scope.get("route")
    path = getattr(route, "path", None)
    if path:
        return path
    raw = scope.get("path") or ""
    # Statik bağlamalar (StaticFiles) rota nesnesi bırakmaz; ilk bölümle etiketlenir
    for prefix in ("/download", "/updates"):
        if raw.startswith(prefix + "/") or raw == prefix:
            return prefix
    return "unmatched"


def _observe(route, seconds):
    row = http_duration.get(route)
    if row is None:
        row = http_duration[route] = [0] * (len(BUCKETS) + 1) + [0.0]
    for i, bound in enumerate(BUCKETS):
        if seconds <= bound:
            row[i] += 1
    row[len(BUCKETS)] += 1
    row[-1] += seconds


class RequestContextMiddleware:
    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] not in ("http", "websocket"):
            await self.app(scope, receive, send)
            return
        incoming = None
        for k, v in scope.get("headers") or ():
            if k == b"x-request-id":
                incoming = v.decode("latin-1")
                break
        rid = incoming if incoming and _SAFE_RID.match(incoming) else uuid.uuid4().hex[:16]
        token = request_id_var.set(rid)
        started = time.perf_counter()
        status = [500]

        async def send_wrapper(message):
            if message["type"] == "http.response.start":
                status[0] = message["status"]
                message.setdefault("headers", [])
                message["headers"] = list(message["headers"]) + [(b"x-request-id", rid.encode())]
            await send(message)

        try:
            await self.app(scope, receive, send_wrapper if scope["type"] == "http" else send)
        except Exception:
            unhandled_errors[0] += 1
            log.exception(
                "yakalanmayan hata", extra={"method": scope.get("method"), "route": _route_of(scope)}
            )
            raise
        finally:
            route = _route_of(scope)
            if scope["type"] == "http":
                key = (scope.get("method", "?"), route, status[0])
                http_requests[key] = http_requests.get(key, 0) + 1
                _observe(route, time.perf_counter() - started)
            else:
                ws_sessions[route] = ws_sessions.get(route, 0) + 1
            request_id_var.reset(token)


def _esc(value):
    return str(value).replace("\\", "\\\\").replace("\n", "\\n").replace('"', '\\"')


def _labels(**kw):
    return "{" + ",".join('%s="%s"' % (k, _esc(v)) for k, v in kw.items()) + "}"


def render(gauges, level_counts, version):
    """Prometheus metin biçimi. gauges: [(ad, yardım, [(etiketler dict, değer), ...]), ...]"""
    out = []

    def metric(name, kind, help_text, samples):
        out.append("# HELP %s %s" % (name, help_text))
        out.append("# TYPE %s %s" % (name, kind))
        for labels, value in samples:
            out.append("%s%s %s" % (name, _labels(**labels) if labels else "", value))

    metric("pops_build_info", "gauge", "Calisan POps sunucu surumu", [({"version": version}, 1)])
    metric("pops_uptime_seconds", "gauge", "Surecin calisma suresi", [({}, round(time.time() - STARTED_AT, 1))])
    metric(
        "pops_http_requests_total", "counter", "HTTP istekleri (rota sablonu, durum kodu)",
        [({"method": m, "route": r, "status": s}, n) for (m, r, s), n in sorted(http_requests.items())],
    )
    out.append("# HELP pops_http_request_duration_seconds HTTP istek suresi")
    out.append("# TYPE pops_http_request_duration_seconds histogram")
    for route, row in sorted(http_duration.items()):
        for i, bound in enumerate(BUCKETS):
            out.append("pops_http_request_duration_seconds_bucket%s %d" % (_labels(route=route, le=bound), row[i]))
        total = row[len(BUCKETS)]
        out.append("pops_http_request_duration_seconds_bucket%s %d" % (_labels(route=route, le="+Inf"), total))
        out.append("pops_http_request_duration_seconds_sum%s %.6f" % (_labels(route=route), row[-1]))
        out.append("pops_http_request_duration_seconds_count%s %d" % (_labels(route=route), row[len(BUCKETS)]))
    metric(
        "pops_websocket_sessions_total", "counter", "Kapanan WebSocket oturumlari (rota sablonu)",
        [({"route": r}, n) for r, n in sorted(ws_sessions.items())],
    )
    metric("pops_unhandled_errors_total", "counter", "Yakalanmayan istisnalar", [({}, unhandled_errors[0])])
    metric(
        "pops_log_messages_total", "counter", "WARNING ve ustu log satirlari",
        [({"level": lvl}, level_counts.get(lvl, 0)) for lvl in ("WARNING", "ERROR", "CRITICAL")],
    )
    for name, help_text, samples in gauges:
        metric(name, "gauge", help_text, samples)
    return "\n".join(out) + "\n"
