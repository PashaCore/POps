"""pops-agent komutları.

  pops-agent run                    servis (systemd pops-agent.service bunu çalıştırır)
  pops-agent configure --server URL [--token JETON] [--ca DOSYA] [--no-restart]
  pops-agent status                 kimlik, kayıt, sunucu, yetenekler, son güncelleme
  pops-agent generalize [--yes]     disk imajı almadan önce: kimliği ve cihaz anahtarını siler
  pops-agent capabilities --reset   sunucunun kapattığı uzak komutu yeniden açar (yerel root)
  pops-agent audit-verify           yerel denetim izinin zincirini doğrular
  pops-agent dna                    sunucuya gidecek donanım DNA'sını yazdırır
  pops-agent version
Testler ve geliştirme için klasörler --config-dir / --state-dir / --log-dir ile değiştirilebilir.
"""

import argparse
import asyncio
import json
import os
import subprocess
import sys

from pops_agent import PLATFORM, __version__, audit, capabilities, config, identity, inventory, logsetup, paths, store

SERVICE = "pops-agent.service"


def _paths(args) -> paths.Paths:
    return paths.Paths(args.config_dir, args.state_dir, args.log_dir)


def _custom_dirs(args) -> bool:
    return (args.config_dir, args.state_dir, args.log_dir) != (paths.CONFIG_DIR, paths.STATE_DIR, paths.LOG_DIR)


def _need_root(args) -> None:
    if os.geteuid() != 0 and not _custom_dirs(args):
        sys.exit("pops-agent: bu komut root olarak çalışmalı (sudo).")


def _systemd() -> bool:
    return os.path.isdir("/run/systemd/system")


def cmd_run(args) -> int:
    _need_root(args)
    p = _paths(args)
    store.ensure_dir(p.state_dir, 0o700)
    logsetup.setup(None if args.no_log_file else p.agent_log, args.verbose)
    from pops_agent.agent import Agent

    try:
        return asyncio.run(Agent(p, hw_root=args.hw_root).run())
    finally:
        logsetup.shutdown()


def cmd_configure(args) -> int:
    _need_root(args)
    p = _paths(args)
    if args.server is not None and not config.is_secure_server_url(args.server.strip()):
        sys.exit("pops-agent: sunucu adresi https:// olmalı (şifresiz http yalnızca bu bilgisayardaki sunucu için).")
    if args.token is not None and args.token.strip() and not config.TOKEN_RE.match(args.token.strip()):
        sys.exit("pops-agent: kayıt jetonunun biçimi geçersiz.")
    if args.ca is not None and args.ca.strip():
        problem = config.ca_file_problem(args.ca.strip())
        if problem:
            sys.exit("pops-agent: kurum sertifikası %s: %s." % (args.ca.strip(), problem))
    store.ensure_dir(p.config_dir, 0o700)
    config.write_config(p.config_file, args.server, args.token, args.ca)
    print("%s yazıldı (0600)." % p.config_file)
    if not args.no_restart and not _custom_dirs(args) and _systemd():
        subprocess.run(["systemctl", "restart", SERVICE], check=False)
        print("%s yeniden başlatıldı." % SERVICE)
    return 0


def cmd_status(args) -> int:
    _need_root(args)   # ayar ve durum dosyaları yalnızca root'a açık
    p = _paths(args)
    cfg = config.load(p.config_file)
    caps = capabilities.Capabilities(p.capabilities_file, p.capability_state)
    caps.load()
    try:
        secret = store.read_json(p.secret)
    except (ValueError, OSError):
        secret = None
    try:
        result = store.read_json(p.update_result) or store.read_json(p.update_result_reported)
    except (ValueError, OSError):
        result = None
    try:
        pending = len(store.read_json(p.results) or [])
    except (ValueError, OSError):
        pending = "?"
    out = {
        "version": __version__,
        "platform": PLATFORM,
        "hw_id": (store.read_text(p.identity) or "").strip() or None,
        "enrolled": bool(isinstance(secret, dict) and secret.get("secret")),
        "server_url": cfg.server_url or None,
        "server_ca": cfg.ca_file or "system",
        "enroll_token_set": cfg.enroll_token is not None,
        "capabilities": caps.describe(),
        "pending_results": pending,
        "last_update": {k: result.get(k) for k in ("outcome", "from_version", "to_version", "rollback", "detail")}
        if isinstance(result, dict) else None,
    }
    if _systemd():
        state = subprocess.run(["systemctl", "is-active", SERVICE], stdout=subprocess.PIPE, check=False)
        out["service"] = state.stdout.decode().strip()
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


def cmd_generalize(args) -> int:
    """Disk imajı (Clonezilla vb.) almadan önce: imajdan açılan her bilgisayar kendi kimliğini türetir ve kayıt
    jetonuyla ayrı cihaz olarak kaydolur. Windows ajanının --generalize seçeneğinin karşılığı."""
    _need_root(args)
    p = _paths(args)
    if not args.yes:
        answer = input("Kimlik ve cihaz anahtarı silinecek; bu bilgisayar yeniden kayıt jetonuyla kaydolmalı. "
                       "Devam? [e/H] ")
        if answer.strip().lower() not in ("e", "evet", "y", "yes"):
            return 1
    if not _custom_dirs(args) and _systemd():
        subprocess.run(["systemctl", "stop", SERVICE], check=False)
    removed = [n for n in identity.CLONE_FILES if store.delete(os.path.join(p.state_dir, n))]
    audit.LocalAudit(p.audit_log, not _custom_dirs(args)).write("identity_changed", reason="generalize",
                                                                removed=", ".join(removed))
    print("Silindi: %s" % (", ".join(removed) or "-"))
    print("İmajı almadan önce %s içinde ENROLL_TOKEN= satırına bir sınıf jetonu yazın; servis durduruldu ve imajdan "
          "açılınca başlar." % p.config_file)
    return 0


def cmd_capabilities(args) -> int:
    _need_root(args)
    p = _paths(args)
    caps = capabilities.Capabilities(p.capabilities_file, p.capability_state)
    if args.reset:
        removed = caps.reset_server_state()
        if removed:
            audit.LocalAudit(p.audit_log, not _custom_dirs(args)).write(
                "capability_changed", capability="terminal", old=False, new=True, source="local_reset")
        print("Sunucunun kapattığı yetenek kaydı %s." % ("silindi" if removed else "zaten yoktu"))
        if not _custom_dirs(args) and _systemd():
            subprocess.run(["systemctl", "restart", SERVICE], check=False)
    caps.load()
    print(caps.describe())
    return 0


def cmd_audit_verify(args) -> int:
    _need_root(args)
    ok, count, problems = audit.verify(_paths(args).audit_log)
    print("%d kayıt; zincir %s." % (count, "sağlam" if ok else "BOZUK"))
    for line in problems[:50]:
        print("  " + line)
    return 0 if ok else 1


def cmd_dna(args) -> int:
    hw = identity.hardware()
    print(json.dumps(identity.dna_payload(hw, inventory.os_name(inventory.os_release())), ensure_ascii=False, indent=2))
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="pops-agent", description="POps Linux ajanı %s" % __version__)
    parser.add_argument("--config-dir", default=paths.CONFIG_DIR)
    parser.add_argument("--state-dir", default=paths.STATE_DIR)
    parser.add_argument("--log-dir", default=paths.LOG_DIR)
    sub = parser.add_subparsers(dest="cmd")
    r = sub.add_parser("run", help="servisi ön planda çalıştır")
    r.add_argument("--verbose", action="store_true")
    r.add_argument("--no-log-file", action="store_true", help="yalnızca stderr/journald")
    r.add_argument("--hw-root", help=argparse.SUPPRESS)   # testler: sahte /sys, /run, /dev
    r.set_defaults(fn=cmd_run)
    c = sub.add_parser("configure", help="agent.conf'u yaz")
    c.add_argument("--server")
    c.add_argument("--token")
    c.add_argument("--ca")
    c.add_argument("--no-restart", action="store_true")
    c.set_defaults(fn=cmd_configure)
    sub.add_parser("status").set_defaults(fn=cmd_status)
    g = sub.add_parser("generalize", help="disk imajından önce kimliği sil")
    g.add_argument("--yes", action="store_true")
    g.set_defaults(fn=cmd_generalize)
    k = sub.add_parser("capabilities", help="yetenekleri göster / sunucunun kapattığını aç")
    k.add_argument("--reset", action="store_true")
    k.set_defaults(fn=cmd_capabilities)
    sub.add_parser("audit-verify").set_defaults(fn=cmd_audit_verify)
    sub.add_parser("dna").set_defaults(fn=cmd_dna)
    sub.add_parser("version").set_defaults(fn=lambda a: print(__version__) or 0)
    args = parser.parse_args(argv)
    if not getattr(args, "fn", None):
        parser.print_help()
        return 2
    return args.fn(args)
