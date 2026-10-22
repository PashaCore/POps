"""Sürüm uçları: sağlık (/api/health), çalışan ve GitHub'daki son sürüm, sunucu güncellemesi ve sürüm notları."""
import asyncio
import json
import re
import time
from typing import List, Optional

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse

from pops import peer_cache, tenancy
from pops.routers.system import common

# Sunucu güncelleme sorgusu: canlıdaki commit (son self-update) ile GitHub main karşılaştırılır
_REV_RE = re.compile(r"^[0-9a-f]{7,40}$")
_SERVER_PATHS = ("Backend/", "Dashboard/", "keys/", "VERSION")
_COMPARE_TTL = 600.0
_compare_cache = {"rev": None, "at": 0.0, "data": None}
# Sürüm notları: GitHub'daki CHANGELOG.md (commit'e sabit sürümler kalıcı, main 10 dk) önbelleği
_changelog_cache = {}


def _fetch_github_compare(rev: str) -> Optional[dict]:
    """Canlıdaki commit (rev) ile GitHub main arası: kaç commit ileride, sunucuyu (Backend/Dashboard/
    VERSION/keys) etkiliyor mu, son değişikliklerin başlıkları. Hata olursa None (çevrimdışı-güvenli)."""
    url = "https://api.github.com/repos/%s/compare/%s...main" % (common.GITHUB_REPO, rev)
    try:
        data = json.loads(common._http_get(url, 20 * 1024 * 1024, "application/vnd.github+json").decode("utf-8"))
    except Exception:
        return None
    files = [str(f.get("filename", "")) for f in data.get("files", [])]
    commits = data.get("commits", [])   # eskiden yeniye
    return {
        "status": data.get("status"),   # identical | ahead | behind | diverged
        "ahead_by": int(data.get("ahead_by") or 0),
        "server_changed": any(f.startswith(p) for f in files for p in _SERVER_PATHS),
        "version_changed": "VERSION" in files,
        "commits": [str(c.get("commit", {}).get("message", "")).split("\n", 1)[0] for c in commits[-15:]][::-1],
    }


async def _server_update(force: bool = False) -> dict:
    """Sunucu güncel mi?

    release kanalı (varsayılan): çalışan sürüm (VERSION) GitHub'daki son sürüm etiketiyle karşılaştırılır; ara
    commit'ler sayılmaz. main kanalı (geliştirme): canlı commit (son BAŞARILI self-update'in rev'i) origin/main ile
    karşılaştırılır."""
    st = common._read_deploy_status() or {}
    rev = str(st.get("rev") or "")
    channel = common._selfupdate_channel()
    out = {"rev": rev or None, "deployed_at": st.get("at"), "last_state": st.get("state"), "channel": channel,
           "checked": False, "update_available": None, "ahead_by": 0, "commits": [], "version_changed": False}
    if channel == "release":
        latest = await common._github_latest(force=force)
        if latest:
            out.update({"checked": True, "latest_release": latest,
                        "update_available": common._newer(latest, common._read_version())})
        return out
    if st.get("state") != "ok" or not _REV_RE.match(rev):
        return out   # canlı commit bilinmiyor (hiç self-update yok ya da son deneme başarısız)
    now = time.time()
    if force or _compare_cache["rev"] != rev or (now - _compare_cache["at"]) >= _COMPARE_TTL:
        data = await asyncio.to_thread(_fetch_github_compare, rev)
        if data is not None:
            _compare_cache.update({"rev": rev, "at": now, "data": data})
    data = _compare_cache["data"] if _compare_cache["rev"] == rev else None
    if data:
        out.update({"checked": True, "ahead_by": data["ahead_by"], "commits": data["commits"],
                    "version_changed": data["version_changed"],
                    "update_available": data["ahead_by"] > 0 and data["server_changed"]})
    return out


def _fetch_changelog(ref: str) -> Optional[str]:
    """GitHub'daki CHANGELOG.md (belirli commit ya da main). Hata olursa None (çevrimdışı-güvenli)."""
    now = time.time()
    hit = _changelog_cache.get(ref)
    if hit and (_REV_RE.match(ref) or now - hit[0] < _COMPARE_TTL):
        return hit[1]
    try:
        text = common._http_get("https://raw.githubusercontent.com/%s/%s/CHANGELOG.md" % (common.GITHUB_REPO, ref),
                                2 * 1024 * 1024, "text/plain").decode("utf-8")
    except Exception:
        return hit[1] if hit else None
    _changelog_cache[ref] = (now, text)
    return text


def _parse_changelog(text: str) -> List[dict]:
    """Keep a Changelog biçimi -> [{version, date, intro, groups: [{kind, items: [str]}]}]."""
    sections, cur, group = [], None, None
    for raw in (text or "").splitlines():
        line = raw.rstrip()
        if line.startswith("## ["):
            ver = line[4:line.index("]")] if "]" in line else line[4:]
            date = line.split(" - ", 1)[1].strip() if " - " in line else None
            cur = {"version": ver, "date": date, "intro": "", "groups": []}
            group = None
            sections.append(cur)
        elif cur is None:
            continue
        elif line.startswith("### "):
            group = {"kind": line[4:].strip(), "items": []}
            cur["groups"].append(group)
        elif line.startswith("- ") and group is not None:
            group["items"].append(line[2:].strip())
        elif line.startswith("  ") and group is not None and group["items"]:
            group["items"][-1] += " " + line.strip()
        elif line and group is None:
            cur["intro"] = (cur["intro"] + " " + line.strip()).strip()
    return sections


def _section(sections: List[dict], version: Optional[str]) -> Optional[dict]:
    want = common._norm(version)
    for sec in sections:
        if common._norm(sec["version"]) == want:
            return sec
    return None


def _new_items(new: Optional[dict], old: Optional[dict]) -> Optional[dict]:
    """`new` bölümünde olup `old` bölümünde olmayan maddeler (aynı sürüm bölümünün iki hali)."""
    if not new:
        return None
    seen = {i for g in (old or {}).get("groups", []) for i in g["items"]}
    groups = [{"kind": g["kind"], "items": [i for i in g["items"] if i not in seen]} for g in new["groups"]]
    groups = [g for g in groups if g["items"]]
    return {**new, "groups": groups} if groups else None


async def _enforce_enabled(d: common.Deps) -> bool:
    rows = await d.execute_query(
        "SELECT value FROM global_settings WHERE key='enforce_agent_auth'", fetch=True)
    return bool(rows and str(rows[0]["value"]) == "1")


def register(router: APIRouter, d: common.Deps) -> None:
    @router.get("/api/health")
    async def health():
        try:
            await d.execute_query("SELECT 1", fetch=True)
            db_ok = True
        except Exception:
            db_ok = False
        body = {"status": "ok" if db_ok else "degraded", "database": db_ok, "version": common._read_version()}
        # Veritabanı yoksa hizmet hazır değildir: 503 (curl -f, ters vekil ve Docker sağlık kontrolü bunu görür)
        return body if db_ok else JSONResponse(body, status_code=503)

    @router.get("/api/system/version")
    async def system_version(check: bool = False, auth: dict = Depends(d.require_admin)):
        running = common._read_version()
        latest = await common._github_latest(force=check)
        staged = await common.staged_release(d)
        staged_version = staged.get("version") if staged else None
        update_available = common._newer(latest, running)
        # Doğrulanmış ajan paketi GitHub'daki son sürüm değil: panel "GitHub'dan indir" düğmesini gösterir
        release_available = common._newer(latest, staged_version)
        args = []
        in_scope = tenancy.lab_sql(await tenancy.scope_of(auth), "c.lab_name", args)
        counts = await d.execute_query(
            "SELECT count(*) AS total, count(s.pc_name) AS enrolled "
            "FROM clients c LEFT JOIN agent_secrets s ON s.pc_name = c.pc_name WHERE " + in_scope, tuple(args),
            fetch=True)
        return {
            "server": await _server_update(force=check),
            "agents_total": int(counts[0]["total"]) if counts else 0,
            "agents_enrolled": int(counts[0]["enrolled"]) if counts else 0,
            "running": running,
            "latest": latest,           # offline ise None olabilir
            "update_available": update_available,
            "checked_github": common._latest_cache["checked"],
            "repo": common.GITHUB_REPO,
            "staged_version": staged_version,   # offline'da yüklenip doğrulanan sürüm
            "staged_tag": staged.get("tag") if staged else None,
            "release_available": release_available,
            "enforce_agent_auth": await _enforce_enabled(d),
            "update_peer_cache": await peer_cache.enabled(),
        }

    @router.get("/api/system/release-notes")
    async def release_notes(auth: dict = Depends(d.require_admin)):
        """Sürüm notları (GitHub'daki CHANGELOG.md'den): sunucuda kurulu kodun içeriği, güncellemeyle gelecek
        yenilikler ve ajan paketinin notları. GitHub'a ulaşılamazsa available=false (çevrimdışı-güvenli)."""
        running = common._read_version()
        st = common._read_deploy_status() or {}
        rev = str(st.get("rev") or "") if st.get("state") == "ok" else ""
        main_text = await asyncio.to_thread(_fetch_changelog, "main")
        if main_text is None:
            return {"available": False}
        main_secs = _parse_changelog(main_text)
        rev_secs = None
        if _REV_RE.match(rev):
            rev_text = await asyncio.to_thread(_fetch_changelog, rev)
            rev_secs = _parse_changelog(rev_text) if rev_text is not None else None
        base = rev_secs if rev_secs is not None else main_secs
        installed = [sec for sec in (_section(base, "Unreleased"), _section(base, running)) if sec and sec["groups"]]
        incoming = []
        if rev_secs is not None:
            rev_versions = {common._norm(sec["version"]) for sec in rev_secs}
            for sec in main_secs:
                if sec["version"] == "Unreleased":
                    diff = _new_items(sec, _section(rev_secs, "Unreleased"))
                    if diff:
                        incoming.append(diff)
                elif common._norm(sec["version"]) not in rev_versions:
                    incoming.append(sec)
        latest = await common._github_latest(force=False)
        return {
            "available": True,
            "running": running,
            "rev": rev or None,
            "installed": installed,
            "incoming": incoming,
            "agent": _section(main_secs, latest) if latest else None,
        }
