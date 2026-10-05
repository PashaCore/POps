"""Ajan güncellemesi: staged imzalı release'in ajanlara dağıtımı (deploy-update) ve gönderilmiş güncellemenin cihaz
cihaz durumu (update-progress; panelin işlem merkezi). Gönderimin izi pops/update_tracking.py'de."""
import base64
import datetime
import json
import os
import shutil
import time

from fastapi import APIRouter, Depends, HTTPException

import release_verify
from pops import peer_cache, tenancy, update_tracking
from pops.models import UpdateProgressInput
from pops.routers.system import common


async def _resolve_targets(d: common.Deps, data: common.DeployUpdateInput):
    if data.target_mode == "ALL":
        rows = await d.execute_query("SELECT pc_name FROM clients", fetch=True)
        return {r["pc_name"] for r in (rows or [])}
    if data.target_mode == "LAB":
        rows = await d.execute_query(
            "SELECT pc_name FROM clients WHERE lab_name = ANY($1::text[])", ([str(x) for x in data.targets],),
            fetch=True)
        return {r["pc_name"] for r in (rows or [])}
    return {t for t in (data.targets or []) if t}


def register(router: APIRouter, d: common.Deps) -> None:
    manager = d.manager

    @router.post("/api/system/update-progress")
    async def update_progress(data: UpdateProgressInput, auth: dict = Depends(d.require_admin)):
        """Gönderilmiş bir ajan güncellemesinin cihaz cihaz durumu (panelin işlem merkezi): bağlı mı, çalışan sürüm,
        sonucu beklenen gönderim var mı (ne zaman gönderildi, ajanın bildirdiği son adım) ve gönderimden sonra gelen
        güncelleme sonucu (başarılı / geri döndü / reddedildi). Zamanlar Unix saniyesi; "now" sunucunun saati."""
        # Kurum birimi kapsamı: kapsam dışındaki cihaz listede yer almaz (pops/tenancy.py)
        pcs = await tenancy.visible_pcs(auth, list(dict.fromkeys(str(p) for p in data.pcs))[:5000])
        if not pcs:
            return {"items": []}
        since = datetime.datetime.fromtimestamp(max(0.0, data.since), datetime.timezone.utc)
        rows = await d.execute_query(
            "SELECT c.pc_name, c.status, c.running_version, av.version AS agent_version FROM clients c "
            "LEFT JOIN agent_versions av ON av.pc_name = c.pc_name WHERE c.pc_name = ANY($1::text[])",
            (pcs,), fetch=True,
        )
        results = await d.execute_query(
            "SELECT DISTINCT ON (hw_id) hw_id, changes FROM device_audit_logs "
            "WHERE action = 'update_result' AND hw_id = ANY($1::text[]) AND timestamp >= $2 ORDER BY hw_id, id DESC",
            (pcs, since), fetch=True,
        )
        last = {}
        for r in results or []:
            try:
                ch = json.loads(r["changes"]) if isinstance(r["changes"], str) else (r["changes"] or {})
            except ValueError:
                ch = {}
            last[r["hw_id"]] = {k: ch.get(k) for k in ("status", "rollback", "to_version", "detail", "agent_state")}
        known = {r["pc_name"]: r for r in rows or []}
        # Sınıf içi eş gönderimi: bilgisayarın rolü (tohum, tohumu bekliyor, eşten) ve sınıf başına özet. Kapsamlı
        # hesap yalnızca kapsamdaki sınıfların özetini görür (tohum ve denenenler o sınıfın bilgisayarlarıdır)
        peer = peer_cache.status_for(pcs)
        scope = await tenancy.scope_of(auth)
        if not scope.is_global:
            peer["labs"] = [x for x in peer["labs"] if scope.allows_lab(x["lab"])]
            peer["items"] = {pc: v for pc, v in peer["items"].items() if scope.allows_lab(v["lab"])}
        await update_tracking.refresh(pcs)   # birden fazla süreçte gönderim ve adım başka süreçte yazılmış olabilir
        items = []
        for pc in pcs:
            r = known.get(pc)
            version = (r and (r["running_version"] or r["agent_version"])) or None
            sent = manager.pending_updates.get(pc)
            # Ajanın bildirdiği son adım (0.1.22+; eski ajanda hep boş). Yalnızca sonucu beklenen gönderimde olur.
            stage = (manager.update_stages.get(pc) or {}) if sent else {}
            items.append({
                "pc": pc,
                "known": r is not None,
                "online": bool(r and str(r["status"] or "").lower() != "offline"),
                "version": version,
                "on_target": bool(version) and common._norm(version) == common._norm(data.version),
                "pending": sent is not None,
                "sent_at": sent[1] if sent else None,
                "stage": stage.get("stage"),
                "detail": stage.get("detail"),
                "attempt": stage.get("attempt"),
                "of": stage.get("of"),
                "stage_at": stage.get("stage_at"),
                "result": last.get(pc),
                "peer": peer["items"].get(pc),
            })
        return {"items": items, "now": time.time(), "peer_labs": peer["labs"]}

    @router.post("/api/system/deploy-update")
    async def deploy_update(data: common.DeployUpdateInput, auth: dict = Depends(d.require_superadmin)):
        """Staged (yüklenip doğrulanmış) imzalı release'i hedef ajanlara dağıtır. Ajana
        {"action":"update_agent","manifest":<b64>,"manifest_sig":<sig>} gönderilir; ajan kendi paketini (Windows MSI,
        Linux .deb) imzalı manifest'ten seçer, kendi ServerUrl'inin /updates/<ad>'ından indirip imza + SHA-256'yı
        kendisi doğrular. Release'te kendi platformunun paketi olmayan cihazlar atlanır (skipped_no_package)."""
        staged = await common.staged_release(d)
        if not staged:
            raise HTTPException(status_code=400,
                                detail="Önce imzalı bir release yükleyin (Sistem > çevrimdışı imzalı paket).")
        version = str(staged.get("version") or "")
        reldir = os.path.join(common.RELEASES_DIR, version.replace(os.sep, "_"))
        mpath = os.path.join(reldir, "manifest.json")
        spath = os.path.join(reldir, "manifest.json.sig")
        if not (os.path.isfile(mpath) and os.path.isfile(spath)):
            raise HTTPException(status_code=409, detail="Staged release dosyaları eksik; tekrar yükleyin.")
        try:
            packages = common._agent_packages(staged)
        except ValueError as exc:
            raise HTTPException(status_code=409, detail="Staged release: %s." % exc)
        if not packages:
            raise HTTPException(status_code=409, detail="Staged release'de ajan paketi (MSI ya da .deb) yok.")
        # Ajan buradan indirir (/updates StaticFiles); yol-gezinme koruması. Yalnızca staged klasörde olan paketler
        # kopyalanır (upload-release'e yalnız MSI yüklenmiş olabilir); paketi olmayan platformun cihazları atlanır.
        os.makedirs(d.updates_dir, exist_ok=True)
        available = {}
        for platform, name in packages.items():
            src = os.path.join(reldir, name)
            if not os.path.isfile(src):
                continue
            dst = os.path.realpath(os.path.join(d.updates_dir, name))
            if os.path.dirname(dst) != os.path.realpath(d.updates_dir):
                raise HTTPException(status_code=400, detail="geçersiz paket adı")
            shutil.copyfile(src, dst)
            available[platform] = name
        if not available:
            raise HTTPException(status_code=409, detail="Paket staged klasörde yok: %s (upload-release'e paketi de "
                                                        "yükleyin)." % ", ".join(packages.values()))
        msi_name, deb_name = available.get("windows"), available.get("linux")
        with open(mpath, "rb") as f:
            manifest_b64 = base64.b64encode(f.read()).decode("ascii")
        with open(spath, "r", encoding="utf-8") as f:
            sig = f.read().strip()
        msg = {"action": "update_agent", "manifest": manifest_b64, "manifest_sig": sig}
        msi_sha = str((release_verify.artifact_entry(staged, msi_name) or {}).get("sha256") or "").lower()

        targets = await _resolve_targets(d, data)
        rows = await d.execute_query(
            "SELECT pc_name, platform FROM clients WHERE pc_name = ANY($1::text[])", (sorted(targets),), fetch=True)
        platform_of = {r["pc_name"]: (r["platform"] or "windows") for r in rows or []}
        no_package = sorted(t for t in targets if platform_of.get(t, "windows") not in available)
        targets = {t for t in targets if t not in no_package}
        # Aynı sürüm son 15 dk içinde gönderildiyse (ya da ajan o sürede adım bildirdiyse) yeniden gönderilmez: ajan
        # kurulum sürerken gelen ikinci emri zaten yok sayar. Kurulum sırasında bağlantısız görünen cihaz da burada.
        already = sorted(await update_tracking.recently_sent(targets, version))
        targets = {t for t in targets if t not in already}
        # Sınıf içi eş önbelleği (pops/peer_cache.py): sınıfın tohumu şimdi, geri kalanı tohum hazır olunca "peers"
        # ile gider; hazır eşi olan sınıfa hemen peers ile; özelliği olmayanlara bugünkü gibi. Eşler MSI'ı paylaşır
        # (msi_sha): yalnızca Windows cihazlar; .deb alan Linux cihazlar bugünkü gibi doğrudan gönderilir
        up = await manager.online_among(targets)   # birden fazla süreçte bütün süreçlerin ajanları
        staging = await peer_cache.plan([t for t in targets if t in up
                                         and platform_of.get(t, "windows") == "windows"], version, msi_sha, msg)
        online = []
        offline = sorted(t for t in targets if t not in up)
        for pc in sorted(t for t in targets if t in up and t not in staging.hold):
            peers = staging.peers.get(pc)
            message = dict(msg, peers=peers) if peers else msg
            if pc in staging.cache:
                message = dict(message, peer_cache=True)   # bu bilgisayar paketi saklar ve sınıfına sunar
            if not await manager.send_command(message, pc):
                offline.append(pc)   # bağlantı bu arada koptu
                await peer_cache.not_sent(pc)
                continue
            online.append(pc)
            # Sonucu beklenen güncelleme; tabloda da tutulur, sunucu yeniden başlasa da izlenir; önceki gönderimin
            # adımı silinir (bkz. pops/update_tracking.py)
            await update_tracking.mark_sent(pc, version)
        waiting = sorted(pc for pc in staging.hold if peer_cache.is_waiting(pc))
        seeds = sorted(pc for pc in staging.seeds if pc in online)
        with_peers = sorted(pc for pc in staging.peers if pc in online)
        await d.add_audit_log("*", "deploy_update", "İmzalı güncelleme dağıtıldı: %s" % version,
                              {"version": version, "msi": msi_name, "deb": deb_name, "dispatched": online,
                               "offline": offline, "no_package": no_package, "already_pending": already,
                               "seeds": seeds, "waiting_for_seed": waiting, "with_peers": with_peers,
                               "by": auth.get("sub")})
        return {"ok": True, "version": version, "msi": msi_name, "deb": deb_name, "dispatched": online,
                "skipped_offline": offline, "skipped_no_package": no_package, "already_pending": already,
                "seeds": seeds, "waiting_for_seed": waiting, "with_peers": with_peers}

    @router.post("/api/system/update-peer-cache")
    async def set_update_peer_cache(data: common.EnforceInput, auth: dict = Depends(d.require_superadmin)):
        """Ajan güncellemesinde sınıf içi eş önbelleği (varsayılan açık). Kapatılınca tohum bekleyen bilgisayarlara
        güncelleme hemen, eşsiz gönderilir; sonraki gönderimler bugünkü gibi hepsine birden gider."""
        await peer_cache.set_enabled(data.enabled)
        await d.add_audit_log("*", "update_peer_cache",
                              "Güncellemede eş önbelleği %s" % ("açıldı" if data.enabled else "kapatıldı"),
                              {"enabled": data.enabled, "by": auth.get("sub")})
        return {"ok": True, "update_peer_cache": data.enabled}
