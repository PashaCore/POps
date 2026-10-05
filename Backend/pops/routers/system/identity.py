"""Ajan kimliği ve yetkileri: kayıt (enroll) jetonları, kimlik zorlaması, yeniden kayıt izni ve yetenek politikası.

Jeton üretimi/yönetimi burada. Jetonun TÜKETİMİ (ilk bağlanışta doğrula + anahtar ver) ve /ws/agent kimlik zorlaması
routers/agents.py'de (enroll + set_secret, enforce_agent_auth → WS 4401).
"""
import hashlib
import secrets

from fastapi import APIRouter, Depends, HTTPException

from pops import devicelist, timeutil
from pops.routers.system import common


def register(router: APIRouter, d: common.Deps) -> None:
    manager = d.manager

    # --- Ajan kayıt (enroll) jetonları (Faz 3) -----------------------------------
    @router.post("/api/system/enroll-token")
    async def create_enroll_token(data: common.EnrollTokenInput, auth: dict = Depends(d.require_superadmin)):
        ttl = max(1, min(int(data.ttl_hours or 72), 24 * 30))  # 1 saat – 30 gün
        uses = max(1, min(int(data.max_uses or 1), 10000))     # 1 = tek kullanımlık; lab için toplu
        lab = (data.lab_name or "").strip() or None
        note = (data.note or "").strip() or None
        token = secrets.token_urlsafe(24)
        # Veritabanında yalnızca özet ve tanıma ipucu (ilk 6 karakter) kalır; jeton yalnızca şimdi gösterilir
        await d.execute_query(
            "INSERT INTO enroll_tokens (token_hash, token_hint, lab_name, note, expires_at, max_uses) "
            "VALUES ($1, $2, $3, $4, NOW() + make_interval(hours => $5), $6)",
            (hashlib.sha256(token.encode("utf-8")).hexdigest(), token[:6], lab, note, ttl, uses))
        return {"token": token, "lab_name": lab, "note": note, "ttl_hours": ttl, "max_uses": uses}

    @router.get("/api/system/enroll-tokens")
    async def list_enroll_tokens(auth: dict = Depends(d.require_superadmin)):
        rows = await d.execute_query(
            "SELECT id, token_hint, lab_name, note, created_at, expires_at, is_used, used_by, used_at, "
            "max_uses, use_count, (expires_at < NOW() AND NOT is_used) AS expired "
            "FROM enroll_tokens ORDER BY id DESC LIMIT 200", fetch=True)
        return [timeutil.iso_row(r) for r in rows or []]

    @router.delete("/api/system/enroll-token/{token_id}")
    async def revoke_enroll_token(token_id: int, auth: dict = Depends(d.require_superadmin)):
        await d.execute_query("DELETE FROM enroll_tokens WHERE id = $1", (token_id,))
        return {"ok": True}

    @router.post("/api/system/enforce-auth")
    async def set_enforce(data: common.EnforceInput, auth: dict = Depends(d.require_superadmin)):
        """Ajan kimlik zorlamasını aç/kapa. AÇIKKEN secret'sız ajan bağlantıları reddedilir —
        yalnızca tüm filo yeni (kimlik doğrulayan) ajana geçtikten sonra açın."""
        await d.execute_query(
            "INSERT INTO global_settings (key, value) VALUES ('enforce_agent_auth', $1) "
            "ON CONFLICT (key) DO UPDATE SET value = $1", ("1" if data.enabled else "0",))
        await d.add_audit_log("*", "enforce_auth",
                              "Ajan kimlik zorlaması %s" % ("AÇILDI" if data.enabled else "kapatıldı"),
                              {"enabled": data.enabled})
        return {"ok": True, "enforce_agent_auth": data.enabled}

    # --- Ajan yetenek politikası (terminal/Vision) — fail-safe: yalnızca KAPATMA -----
    @router.post("/api/system/set-capabilities")
    async def set_capabilities(data: common.CapabilityInput, auth: dict = Depends(d.require_superadmin)):
        """Bir cihazda terminal/Vision yeteneğini kapatır. FAIL-SAFE: ajan sunucudan gelen "aç"ı
        yok sayar (kalıcı açma yeniden kurulum / offline-imzalı politika ister); bu uç pratikte
        yalnızca KAPATMAK içindir. İstek kalıcı kaydedilir; ajan çevrimdışıysa yeniden bağlanınca
        uygulanır. Panelde açığa çekmek isteği temizler ama ajanı otomatik açmaz."""
        pc = (data.pc_name or "").strip()
        if not pc:
            raise HTTPException(status_code=400, detail="pc_name gerekli")
        msg = {"action": "set_capabilities"}
        sets, params = [], []
        if data.terminal_enabled is not None:
            msg["terminal_enabled"] = bool(data.terminal_enabled)
            params.append(not bool(data.terminal_enabled))
            sets.append("cap_terminal_disable_requested=$%d" % len(params))
        if data.vision_enabled is not None:
            msg["vision_enabled"] = bool(data.vision_enabled)
            params.append(not bool(data.vision_enabled))
            sets.append("cap_vision_disable_requested=$%d" % len(params))
        if len(msg) == 1:
            raise HTTPException(status_code=400, detail="terminal_enabled ve/veya vision_enabled verin.")
        params.append(pc)
        await d.execute_query("UPDATE clients SET %s WHERE pc_name=$%d" % (", ".join(sets), len(params)),
                              tuple(params))
        await devicelist.sync([pc])
        # send_command çevrimdışıysa no-op; online durumunu ayrıca bildiriyoruz. Kapatma isteği
        # kalıcı kaydedildi, ajan sonra bağlanınca /ws/agent 'capabilities' handler'ı uygular.
        online = await manager.is_online(pc)
        await manager.send_command(msg, pc)
        applied = {k: msg[k] for k in msg if k != "action"}
        await d.add_audit_log(pc, "set_capabilities", "Yetenek politikası gönderildi",
                              {**applied, "by": auth.get("sub")})
        return {"ok": True, "delivered_online": online, **applied}

    # --- Yeniden-enroll izni (F2 kurtarma yolu: Deep Freeze / yeniden kurulum) --------
    @router.post("/api/system/allow-reenroll")
    async def allow_reenroll(data: common.ReenrollInput, auth: dict = Depends(d.require_superadmin)):
        """Bir cihaz için tek-seferlik yeniden-enroll iznini aç/kapat. Varsayılan KAPALI: enroll
        token'la mevcut secret'ı ele geçirme engellenir (F2). AÇIKKEN cihaz enroll token'la yeniden
        secret alabilir; sunucu başarılı yeniden-enroll'da bayrağı otomatik FALSE yapar."""
        pc = (data.pc_name or "").strip()
        if not pc:
            raise HTTPException(status_code=400, detail="pc_name gerekli")
        await d.execute_query("UPDATE clients SET allow_reenroll=$1 WHERE pc_name=$2", (bool(data.allow), pc))
        await d.add_audit_log(pc, "allow_reenroll",
                              "Yeniden-enroll izni %s" % ("AÇILDI" if data.allow else "kapatıldı"),
                              {"allow": bool(data.allow), "by": auth.get("sub")})
        return {"ok": True, "pc_name": pc, "allow_reenroll": bool(data.allow)}
