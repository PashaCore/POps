"""Vision v2 entegrasyon testi: ikili kareler, monitör listesi, görüntüleyici komutları ve pano (docs/vision.md).

Ajan WebSocket ile taklit edilir (/ws/agent ve /ws/vision). Panel soketleri: oturum sahibi admin (ikili kare bildiren
ve bildirmeyen iki soket), başka bir cihazda zorunlu oturumu olan ikinci admin ve viewer. Denetlenen:
  * kareler yalnız oturum sahibinin ikili kare bildiren paneline gider, önek tünelin cihazıdır (ajanın gönderdiği
    kimlik kullanılmaz);
  * bozuk ve 2 MB'tan büyük kareler atılır ve /metrics'te sayılır, bağlantı sürer;
  * select_monitor / set_quality / clipboard ajana yalnız oturum sahibinden, sözleşmedeki alanlarla gider;
  * pano yalnız kullanıcının kabul ettiği oturumda, en çok 64 KB; denetim kaydında içerik yok;
  * eski JSON stream_frame aynı tünelde çalışmaya devam eder.

Ortam: POPS_TEST_HTTP + POPS_TEST_WS + DB_* + JWT_SECRET (sunucuyla aynı) + METRICS_TOKEN (sunucuyla aynı).
"""

import asyncio
import hashlib
import json
import os
import re
import secrets
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import server  # noqa: E402  (create_jwt, JWT_COOKIE_NAME)
import asyncpg  # noqa: E402
import websockets  # noqa: E402
from pops import vision  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http", "ws", 1))
METRICS_TOKEN = os.environ.get("METRICS_TOKEN", "")

PC_A, PC_B = "HW-VISION2A001", "HW-VISION2B001"
SECRETS = {PC_A: "vision2-secret-a", PC_B: "vision2-secret-b"}
JPEG = b"\xff\xd8\xff\xe0" + b"\x00\x10JFIF" + b"j" * 300 + b"\xff\xd9"


def http(path, method="POST", body=None, token=None, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method)
    if data is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=10) as resp:
            raw = resp.read().decode()
            return resp.status, (json.loads(raw) if raw.startswith(("{", "[")) else raw)
    except urllib.error.HTTPError as e:
        return e.code, {}


def events():
    """pops_events_total sayaçları (event -> değer)."""
    s, text = http("/metrics", method="GET", headers={"Authorization": "Bearer " + METRICS_TOKEN})
    assert s == 200, "/metrics okunamadı (METRICS_TOKEN sunucuyla aynı mı?)"
    pattern = r'^pops_events_total\{event="([^"]+)"\} (\S+)$'
    return {m.group(1): float(m.group(2)) for m in re.finditer(pattern, text, re.M)}


async def collect(ws, t=1.0):
    """t saniye sessizlik olana kadar gelen bütün mesajlar."""
    out = []
    while True:
        try:
            out.append(await asyncio.wait_for(ws.recv(), timeout=t))
        except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
            return out


async def first(ws, pred, t=3.0):
    """pred'i sağlayan ilk mesaj (öncekiler atılır); gelmezse None."""
    loop = asyncio.get_event_loop()
    end = loop.time() + t
    while True:
        left = end - loop.time()
        if left <= 0:
            return None
        try:
            m = await asyncio.wait_for(ws.recv(), timeout=left)
        except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
            return None
        if pred(m):
            return m


def jtype(kind, **fields):
    def pred(m):
        if not isinstance(m, str):
            return False
        d = json.loads(m)
        return d.get("type") == kind and all(d.get(k) == v for k, v in fields.items())
    return pred


def binary(m):
    return isinstance(m, bytes)


def cookie(jwt):
    return {"Cookie": "%s=%s" % (server.JWT_COOKIE_NAME, jwt)}


def prefixed(pc, frame):
    return vision.panel_prefix(pc) + frame


async def main():
    c = await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )
    for pc, sec in SECRETS.items():
        await c.execute(
            "INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2) "
            "ON CONFLICT (pc_name) DO UPDATE SET secret_hash=$2",
            pc, hashlib.sha256(sec.encode()).hexdigest(),
        )
    for uname, role in (("vadmin1", "admin"), ("vadmin2", "admin"), ("vviewer", "viewer")):
        await c.execute(
            "INSERT INTO users (username, password_hash, role, permissions, token_version) "
            "VALUES ($1, 'x', $2, '[]', 0) ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            uname, role,
        )
    audit_from = await c.fetchval("SELECT COALESCE(max(id), 0) FROM device_audit_logs")
    passed = 0

    def chk(cond, msg):
        nonlocal passed
        assert cond, "FAIL: " + msg
        passed += 1
        print("  ok:", msg)

    users = (("vadmin1", "admin"), ("vadmin2", "admin"), ("vviewer", "viewer"))
    jwt1, jwt2, jwtv = (server.create_jwt(u, r) for u, r in users)

    # server_info: ajan ikili kareyi yalnız vision_binary görürse gönderir
    agent = await websockets.connect(WS + "/ws/agent/" + PC_A, additional_headers={
        "X-Agent-Secret": SECRETS[PC_A], "X-Agent-Version": "0.1.30-alpha"})
    await agent.send(json.dumps({"status": "Online", "hostname": "vision2-a"}))
    info = await first(agent, lambda m: json.loads(m).get("action") == "server_info", 5)
    features = json.loads(info).get("features", []) if info else []
    chk("vision_binary" in features and "vision_clipboard" in features,
        "server_info.features vision_binary ve vision_clipboard içerir")

    own = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(jwt1))
    own_text = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(jwt1))
    other = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(jwt2))
    viewer = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(jwtv))
    hello = json.dumps({"type": "panel_hello", "features": ["vision_binary"]})
    for p in (own, other, viewer):
        await p.send(hello)

    s, b = http("/api/audit/session/start", body={"target_pc": PC_A, "reason": "v2 test", "is_mandatory": False},
                token=jwt1)
    chk(s == 200 and b.get("session_id"), "vadmin1 A için kullanıcıya sorulan oturum açtı")
    sid_a = b["session_id"]
    start = await first(agent, lambda m: json.loads(m).get("action") == "start_vision_session", 3)
    chk(start and json.loads(start).get("is_mandatory") is False, "ajana start_vision_session gitti")
    s, b = http("/api/audit/session/start", body={"target_pc": PC_B, "reason": "v2 zorunlu", "is_mandatory": True},
                token=jwt2)
    chk(s == 200 and b.get("session_id"), "vadmin2 B için zorunlu oturum açtı")
    sid_b = b["session_id"]

    # Kullanıcı kabul etti: tüneller oturumdan sonra açılır
    vis_a = await websockets.connect(WS + "/ws/vision/" + PC_A, additional_headers={"X-Agent-Secret": SECRETS[PC_A]})
    vis_b = await websockets.connect(WS + "/ws/vision/" + PC_B, additional_headers={"X-Agent-Secret": SECRETS[PC_B]})
    await asyncio.sleep(0.3)

    # monitors: saklanır, yalnız oturum sahibine, ajanın yazdığı kimlik yerine tünelin kimliğiyle
    mons = [{"index": 0, "width": 1920, "height": 1080, "primary": True},
            {"index": 1, "width": 1280, "height": 1024, "primary": False}]
    await vis_a.send(json.dumps({"type": "monitors", "hw_id": PC_B, "list": [dict(mons[0], name="DELL"), mons[1]]}))
    got = await first(own, jtype("monitors"), 3)
    chk(got and json.loads(got) == {"type": "monitors", "hw_id": PC_A, "list": mons},
        "monitors oturum sahibine tünelin kimliğiyle ve yalnız bilinen alanlarla ulaştı (sahte hw_id yok sayıldı)")

    # İkili kareler: tam, bölge, imleç, ikinci monitörün tam karesi
    frames = [
        vision.pack_frame(vision.KIND_FULL, 0, 1, 0, 0, 1920, 1080, 1920, 1080, JPEG),
        vision.pack_frame(vision.KIND_REGION, 0, 2, 100, 200, 64, 32, 1920, 1080, JPEG),
        vision.pack_frame(vision.KIND_CURSOR, 0, 3, 500, 400, 0, 0, 1920, 1080),
        vision.pack_frame(vision.KIND_FULL, 1, 4, 0, 0, 1280, 1024, 1280, 1024, JPEG),
    ]
    for f in frames:
        await vis_a.send(f)
    got = [m for m in await collect(own, 1.0) if binary(m)]
    chk(sorted(got) == sorted(prefixed(PC_A, f) for f in frames),
        "F12: tam/bölge/imleç kareleri oturum sahibine 0x01 + kimlik uzunluğu + kimlik önekiyle ulaştı")
    chk([m for m in got if m.endswith(frames[1])] and got.index(prefixed(PC_A, frames[0]))
        < got.index(prefixed(PC_A, frames[1])), "aynı monitörde sıra korundu (tam kare bölgeden önce)")
    leaks = {name: [m for m in await collect(p, 0.6) if binary(m)]
             for name, p in (("ikili bildirmeyen panel", own_text), ("başka admin", other), ("viewer", viewer))}
    chk(not any(leaks.values()), "F12: kare ikili bildirmeyen panele, oturumsuz admine ve viewer'a gitmedi")

    # Başka tünelin karesi yalnız o cihazın oturum sahibine, kendi önekiyle
    fb = vision.pack_frame(vision.KIND_FULL, 0, 1, 0, 0, 800, 600, 800, 600, JPEG)
    await vis_b.send(fb)
    got_b = await first(other, binary, 3)
    chk(got_b == prefixed(PC_B, fb), "B'nin karesi B'nin oturum sahibine B önekiyle ulaştı")
    chk(not [m for m in await collect(own, 0.6) if binary(m)], "A'nın oturum sahibi B'nin karesini almadı")

    # Bozuk ve büyük kareler atılır ve sayılır; bağlantı sürer
    before = events()
    bad = [
        frames[0][:10],
        b"\x09" + frames[0][1:],
        vision.pack_frame(vision.KIND_REGION, 0, 5, 1900, 0, 64, 32, 1920, 1080, JPEG),
        vision.pack_frame(vision.KIND_FULL, 0, 5, 0, 0, 10, 10, 10, 10, b"GIF89a" + b"g" * 40),
        vision.pack_frame(vision.KIND_CURSOR, 0, 5, 1, 1, 0, 0, 10, 10, JPEG),
        vision.pack_frame(vision.KIND_FULL, 0, 5, 0, 0, 10, 10, 10, 10, JPEG + b"\x00" * vision.MAX_FRAME_BYTES),
    ]
    for f in bad:
        await vis_a.send(f)
    ok_frame = vision.pack_frame(vision.KIND_FULL, 0, 6, 0, 0, 1920, 1080, 1920, 1080, JPEG + b"ok")
    await vis_a.send(ok_frame)
    got = [m for m in await collect(own, 1.0) if binary(m)]
    chk(got == [prefixed(PC_A, ok_frame)], "bozuk/2 MB'tan büyük kareler atıldı, ardından gelen geçerli kare ulaştı")
    after = events()

    def delta(k):
        return after.get(k, 0) - before.get(k, 0)

    chk(delta("vision_frames_malformed") == 5 and delta("vision_frames_oversize") == 1,
        "/metrics: vision_frames_malformed +5, vision_frames_oversize +1")
    chk(delta("vision_frames_binary") >= 1, "/metrics: geçerli ikili kareler sayılıyor")

    # Görüntüleyici komutları: yalnız oturum sahibinden, sözleşmedeki alanlarla
    def ctl(device, action, **kw):
        return json.dumps(dict({"type": "vision_control", "device": device, "action": action}, **kw))

    await own.send(ctl(PC_A, "select_monitor", index=1, extra="x"))
    got = await first(vis_a, lambda m: True, 3)
    chk(got and json.loads(got) == {"action": "select_monitor", "index": 1},
        "select_monitor ajana tam sözleşme biçimiyle")
    await own.send(ctl(PC_A, "set_quality", quality=40, scale=0.75, fps=8))
    got = await first(vis_a, lambda m: True, 3)
    chk(got and json.loads(got) == {"action": "set_quality", "quality": 40, "scale": 0.75, "fps": 8},
        "set_quality ajana iletildi")
    await own.send(ctl(PC_A, "set_quality", quality=90, scale=0.75, fps=8))   # sınır dışı
    await other.send(ctl(PC_A, "select_monitor", index=0))                    # A'da oturumu yok
    await viewer.send(ctl(PC_A, "select_monitor", index=0))                   # viewer
    await other.send(ctl(PC_A, "clipboard", text="oturumsuz"))
    await own.send(ctl(PC_B, "select_monitor", index=0))                      # B'de oturumu yok
    await own.send(ctl(PC_A, "select_monitor", index="all"))
    got = await collect(vis_a, 1.0)
    chk([json.loads(m) for m in got] == [{"action": "select_monitor", "index": "all"}],
        "oturumsuz admin, viewer ve sınır dışı değer ajana ulaşmadı; oturum sahibinin sonraki komutu ulaştı")
    chk(not await collect(vis_b, 0.5), "B'de oturumu olmayan adminin komutu B'ye gitmedi")
    chk(not [m for m in await collect(other, 0.5) if jtype("clipboard_result")(m)],
        "oturumsuz pano isteğine yanıt da yok")

    # Pano: kullanıcının kabul ettiği oturumda iki yön
    marker_out = "panel-pano-" + secrets.token_hex(6)
    await own.send(ctl(PC_A, "clipboard", text=marker_out))
    got = await first(vis_a, lambda m: True, 3)
    chk(got and json.loads(got) == {"action": "clipboard", "text": marker_out}, "pano metni ajana ulaştı")
    res = await first(own, jtype("clipboard_result", hw_id=PC_A), 3)
    chk(res and json.loads(res)["ok"] is True, "panele pano sonucu: gönderildi")
    await own.send(ctl(PC_A, "clipboard", text="x" * (vision.CLIPBOARD_MAX_BYTES + 1)))
    res = await first(own, jtype("clipboard_result", hw_id=PC_A), 3)
    chk(res and json.loads(res) == {"type": "clipboard_result", "hw_id": PC_A, "ok": False, "reason": "invalid"},
        "64 KB'tan uzun pano metni reddedildi")
    chk(not await collect(vis_a, 0.5), "uzun metin ajana gitmedi")

    marker_in = "pc-pano-" + secrets.token_hex(6)
    await vis_a.send(json.dumps({"type": "clipboard", "text": marker_in, "hw_id": PC_B}))
    got = await first(own, jtype("clipboard"), 3)
    chk(got and json.loads(got) == {"type": "clipboard", "hw_id": PC_A, "text": marker_in},
        "bilgisayarın pano metni oturum sahibine tünelin kimliğiyle ulaştı")
    leaks = [m for p in (other, viewer) for m in await collect(p, 0.5) if jtype("clipboard")(m)]
    chk(not leaks, "pano metni başka admine ve viewer'a gitmedi")

    # Tünel açıkken başlayan ikinci "kullanıcıya sor" oturumu: kullanıcının onu kabul ettiği bilinmez, pano kapalı
    s, b = http("/api/audit/session/start", body={"target_pc": PC_A, "reason": "ikinci", "is_mandatory": False},
                token=jwt2)
    sid_a2 = b.get("session_id")
    await other.send(ctl(PC_A, "clipboard", text="ikinci oturum"))
    res = await first(other, jtype("clipboard_result", hw_id=PC_A), 3)
    chk(res and json.loads(res)["ok"] is False and json.loads(res)["reason"] == "not_accepted",
        "tünel açıkken başlayan ikinci oturuma pano kapalı (not_accepted)")
    only_first = "yalniz-ilk-" + secrets.token_hex(4)
    await vis_a.send(json.dumps({"type": "clipboard", "text": only_first}))
    got = await first(own, jtype("clipboard"), 3)
    chk(got and json.loads(got)["text"] == only_first
        and not [m for m in await collect(other, 0.8) if jtype("clipboard")(m)],
        "bilgisayarın panosu yalnız rızası tüneli açan oturumun sahibine gitti")
    http("/api/audit/session/end", body={"session_id": sid_a2, "status": "Completed"}, token=jwt2)

    # Zorunlu oturumda pano yok (kullanıcı kabul etmedi); kareler yine gider
    await other.send(ctl(PC_B, "clipboard", text="zorunlu"))
    res = await first(other, jtype("clipboard_result", hw_id=PC_B), 3)
    chk(res and json.loads(res)["ok"] is False and json.loads(res)["reason"] == "not_accepted",
        "zorunlu oturumda panoya izin yok (not_accepted)")
    await other.send(ctl(PC_B, "select_monitor", index=0))
    got = await collect(vis_b, 1.0)
    chk([json.loads(m) for m in got] == [{"action": "select_monitor", "index": 0}],
        "zorunlu oturumda pano ajana gitmedi, monitör seçimi gitti")
    await vis_b.send(json.dumps({"type": "clipboard", "text": "zorunlu-pc"}))
    fb2 = vision.pack_frame(vision.KIND_REGION, 0, 2, 0, 0, 10, 10, 800, 600, JPEG)
    await vis_b.send(fb2)
    got = await collect(other, 1.0)
    chk(not [m for m in got if jtype("clipboard")(m)] and prefixed(PC_B, fb2) in got,
        "zorunlu oturumda bilgisayarın panosu panele gitmedi, kare gitti")

    # Denetim kaydı: yön ve uzunluk var, içerik yok
    rows = await c.fetch(
        "SELECT hw_id, reason, changes FROM device_audit_logs WHERE id > $1 AND action = 'clipboard' ORDER BY id",
        audit_from,
    )
    dirs = sorted((r["hw_id"], json.loads(r["changes"])["direction"], json.loads(r["changes"])["length"])
                  for r in rows)
    chk(dirs == sorted([(PC_A, "to_pc", len(marker_out)), (PC_A, "from_pc", len(marker_in)),
                        (PC_A, "from_pc", len(only_first))]),
        "pano denetim kaydı: iki yön, uzunluk; reddedilen ve zorunlu oturumdaki aktarım için kayıt yok")
    chk(all(t not in r["reason"] + r["changes"] for r in rows for t in (marker_out, marker_in, only_first)),
        "denetim kaydında pano içeriği yok")

    # Eski ajan: JSON/base64 stream_frame aynı tünelde çalışır
    await vis_a.send(json.dumps({"type": "stream_frame", "hw_id": PC_A, "image": "LEGACYFRAME"}))
    got = await first(own, jtype("stream_frame"), 3)
    chk(got and json.loads(got).get("image") == "LEGACYFRAME", "eski JSON stream_frame oturum sahibine ulaştı")
    chk(not [m for m in await collect(viewer, 0.5) if jtype("stream_frame")(m)], "eski kare viewer'a gitmedi")

    # Oturum bitince kare ve komut akışı durur
    http("/api/audit/session/end", body={"session_id": sid_a, "status": "Completed"}, token=jwt1)
    http("/api/audit/session/end", body={"session_id": sid_b, "status": "Completed"}, token=jwt2)
    await vis_a.send(frames[0])
    chk(not [m for m in await collect(own, 0.8) if binary(m)], "oturum bitince kare gitmiyor")
    await own.send(ctl(PC_A, "select_monitor", index=0))
    chk(not await collect(vis_a, 0.6), "oturum bitince komut gitmiyor")

    for w in (own, own_text, other, viewer, vis_a, vis_b, agent):
        try:
            await w.close()
        except Exception:
            pass
    await c.close()
    print("\nTUM VISION V2 TESTLERI GECTI (%d kontrol)" % passed)


if __name__ == "__main__":
    asyncio.run(main())
