"""Faz 5: sunucu backend self-update (SSH'siz, panelden).

Backend root değildir: yalnızca bir istek dosyası yazar; root systemd path-unit (pops-selfupdate.path) bunu görüp
deploy'u çalıştırır (bkz. docs/self-update.md). İstek dosyasının içeriği çalıştırılmaz."""
import json
import os
import time

from fastapi import APIRouter, Depends, HTTPException

from pops.routers.system import common


def _selfupdate_configured() -> bool:
    return os.path.isdir(common.SELFUPDATE_DIR) and os.access(common.SELFUPDATE_DIR, os.W_OK)


def register(router: APIRouter, d: common.Deps) -> None:
    @router.get("/api/system/self-update/status")
    async def self_update_status(auth: dict = Depends(d.require_admin)):
        """Son self-update denemesinin durumunu (deploy-status.json) ve kurulu olup
        olmadığını döner. Root deploy betiği bu dosyayı yazar."""
        return {
            "configured": _selfupdate_configured(),
            "pending": os.path.exists(os.path.join(common.SELFUPDATE_DIR, "deploy-request.json")),
            "status": common._read_deploy_status(),
        }

    @router.post("/api/system/self-update")
    async def self_update(auth: dict = Depends(d.require_superadmin)):
        """origin/main'den sunucu backend'ini güncellemeyi KUYRUKLAR. Backend root
        olmadığından yalnızca bir istek dosyası yazar; root systemd path-unit bunu görüp
        git ff-only çeker ve pops-deploy-backend'i (sağlık kontrolü + geri dönüşlü) çalıştırır.
        Keyfi kod yürütülmez; yalnızca origin/main yeniden dağıtılır."""
        if not _selfupdate_configured():
            raise HTTPException(
                status_code=503,
                detail="Self-update kurulu değil. systemd path-unit'i etkinleştirin (bkz. docs/self-update.md).")
        target = "origin/main" if common._selfupdate_channel() == "main" else "latest-release"
        payload = {
            "requested_by": auth.get("sub"),
            "requested_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "target": target,   # bilgi amaçlı: root betiği kanalı kendi ayar dosyasından okur
        }
        req_path = os.path.join(common.SELFUPDATE_DIR, "deploy-request.json")
        tmp = req_path + ".tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(payload, f)
            os.replace(tmp, req_path)   # atomik: path-unit yarım dosya görmesin
        except OSError as exc:
            raise HTTPException(status_code=503, detail="İstek yazılamadı: %s" % exc)
        await d.add_audit_log("*", "self_update",
                              "Sunucu self-update kuyruklandı (%s)" % (auth.get("sub") or "?"),
                              {"target": target})
        return {"ok": True, "queued": True}
