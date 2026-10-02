"""Ajanın heartbeat'te bildirdiği sağlık özeti (0.1.12+, "agent_health"). Yalnızca bilinen alanlar, türleri
denetlenerek saklanır; ajandan gelen metin panelde gösterildiği için kısaltılır."""

import json
from typing import Optional

_TIMES = ("started_at", "last_policy_sync", "last_inventory_upload")
_VISION = ("off", "idle", "connected")


def _unix(value) -> Optional[int]:
    return int(value) if isinstance(value, (int, float)) and not isinstance(value, bool) and value >= 0 else None


def clean(raw) -> Optional[str]:
    """Ham bloğu JSONB'ye yazılacak metne çevirir; blok yoksa ya da bozuksa None."""
    if not isinstance(raw, dict):
        return None
    out = {k: _unix(raw.get(k)) for k in _TIMES}
    for key in ("tray_connected", "screen_locked", "network_isolated"):   # karantina durumu: 0.1.13+
        out[key] = raw.get(key) if isinstance(raw.get(key), bool) else None
    out["vision_channel"] = raw.get("vision_channel") if raw.get("vision_channel") in _VISION else None
    errors = raw.get("loop_errors_1h")
    out["loop_errors_1h"] = min(int(errors), 100000) if isinstance(errors, int) and errors >= 0 else None
    last_error = raw.get("last_error")
    out["last_error"] = last_error[:200] if isinstance(last_error, str) and last_error else None
    isolation_error = raw.get("isolation_error")
    out["isolation_error"] = isolation_error[:200] if isinstance(isolation_error, str) and isolation_error else None
    return json.dumps(out, ensure_ascii=False)


def parse(value) -> Optional[dict]:
    """Veritabanından gelen JSONB (asyncpg metin olarak döndürür) → sözlük."""
    if value is None or isinstance(value, dict):
        return value
    try:
        data = json.loads(value)
    except (TypeError, ValueError):
        return None
    return data if isinstance(data, dict) else None
