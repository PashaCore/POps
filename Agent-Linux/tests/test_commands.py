import asyncio
import time

from pops_agent import commands


def run(coro):
    return asyncio.run(coro)


def test_success_output_and_exit_code():
    r = run(commands.CommandRunner().run(1, "echo merhaba; echo dünya"))
    assert r.exit_code == 0 and r.output == "merhaba\ndünya"


def test_failure_formats_like_windows():
    r = run(commands.CommandRunner().run(2, "echo yarım; echo bozuk >&2; exit 3"))
    assert r.exit_code == 3
    assert r.output == "[ÇIKIŞ KODU: 3]\n[HATA]:\nbozuk\n[ÇIKTI]:\nyarım"


def test_no_output():
    r = run(commands.CommandRunner().run(3, "true"))
    assert r.output == "Komut çalıştı (Çıkış: 0) ancak çıktı üretilmedi."


def test_runs_with_sh_and_clean_environment():
    r = run(commands.CommandRunner().run(4, 'echo "$DEBIAN_FRONTEND|$PATH|${POPS_SECRET_TEST:-yok}"; pwd'))
    first, cwd = r.output.splitlines()
    assert first.startswith("noninteractive|/usr/local/sbin:") and first.endswith("|yok") and cwd == "/"


def test_timeout_kills_process_group():
    runner = commands.CommandRunner(max_duration=1)
    started = time.monotonic()
    r = run(runner.run(5, "echo basladi; sleep 30 & sleep 30"))
    assert r.exit_code == commands.EXIT_TIMEOUT and time.monotonic() - started < 15
    assert r.output.startswith("[HATA]: İşlem") and r.output.endswith("[ÇIKTI]:\nbasladi")


def test_cancel_and_service_stop():
    async def scenario():
        runner = commands.CommandRunner()
        task = asyncio.ensure_future(runner.run(6, "sleep 30"))
        await asyncio.sleep(0.3)
        assert runner.is_running(6) and runner.cancel(6) and not runner.cancel(99)
        cancelled = await task
        stopping = asyncio.Event()
        task = asyncio.ensure_future(runner.run(7, "sleep 30", stopping))
        await asyncio.sleep(0.3)
        stopping.set()
        return cancelled, await task, runner.running_count

    cancelled, stopped, running = run(scenario())
    assert cancelled.exit_code == commands.EXIT_CANCELLED and cancelled.output.startswith("[İPTAL EDİLDİ]")
    assert stopped.exit_code == commands.EXIT_SERVICE_STOPPING and stopped.output.startswith("[DURDURULDU]")
    assert running == 0


def test_duplicate_task_not_started_twice():
    async def scenario():
        runner = commands.CommandRunner()
        first = asyncio.ensure_future(runner.run(8, "sleep 0.5; echo bir"))
        await asyncio.sleep(0.1)
        second = await runner.run(8, "echo iki")
        return await first, second

    first, second = run(scenario())
    assert first.output == "bir" and second.exit_code == commands.EXIT_DUPLICATE


def test_output_cap_while_reading():
    runner = commands.CommandRunner(max_chars=1000)
    r = run(runner.run(9, "yes x | head -c 200000"))
    assert r.exit_code == 0 and "[ÇIKTI KISALTILDI: " in r.output and len(r.output) < 1200


def test_signal_exit_code_is_128_plus_signal():
    r = run(commands.CommandRunner().run(10, "kill -9 $$"))
    assert r.exit_code == 137


def test_truncate_output_limit():
    text = "a" * (commands.MAX_OUTPUT_CHARS + 10)
    out = commands.truncate_output(text)
    assert len(out) == commands.MAX_OUTPUT_CHARS and out.endswith("karakter]")
    assert commands.truncate_output(None) == ""


def test_permission_messages_match_server_convention():
    off = commands.permission(False, True)
    assert not off.allowed and off.rejection.startswith("[REDDEDİLDİ]") and off.reason is None
    module = commands.permission(True, False)
    assert not module.allowed and module.rejection.startswith("[REDDEDİLDİ]") and module.reason == "module_disabled"
    assert commands.permission(False, False).reason is None   # yerel kilit önce gelir
    assert commands.permission(True, True).allowed
    assert commands.DISABLED_MESSAGE == ("[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek politikası); "
                                         "komut çalıştırılmadı.")


def test_windows_power_buttons_translated():
    restart = commands.translate_power_command("shutdown /r /f /t 5")
    assert "--on-active=5s" in restart and "systemctl reboot" in restart
    assert "systemctl poweroff" in commands.translate_power_command("SHUTDOWN /s /f /t 30")
    for other in ("shutdown /r /t 5", "shutdown -r now", "shutdown /r /f /t 5; rm -rf /", "echo shutdown /r /f /t 5"):
        assert commands.translate_power_command(other) is None
