import json
import os
import stat

from pops_agent import protocol, spool


def result(tid):
    return {"type": "result", "pc_name": "HW-1", "task_id": tid, "output": "ok %d" % tid, "exit_code": 0}


def test_results_kept_on_disk_until_ack(tmp_path):
    path = str(tmp_path / "pending-results.json")
    s = spool.ResultSpool(path)
    s.add(1, result(1))
    s.add(2, result(2))
    assert stat.S_IMODE(os.stat(path).st_mode) == 0o600
    again = spool.ResultSpool(path)   # ajan yeniden başladı
    assert again.task_ids() == [1, 2] and again.all()[0][1]["output"] == "ok 1"
    assert again.remove(1) and not again.remove(1)
    assert again.remove(2) and not os.path.exists(path)   # son onayda dosya silinir


def test_unsent_and_reconnect(tmp_path):
    s = spool.ResultSpool(str(tmp_path / "r.json"))
    s.add(5, result(5))
    s.mark_sent(5)
    assert s.unsent() == []
    s.on_connected()   # yeni bağlantıda onaysızlar yeniden gönderilir
    assert [t for t, _ in s.unsent()] == [5]
    s.add(5, dict(result(5), output="yeni"))   # aynı görev: yerini alır
    assert s.count == 1 and s.all()[0][1]["output"] == "yeni"


def test_limit_drops_oldest(tmp_path):
    s = spool.ResultSpool(str(tmp_path / "r.json"))
    for i in range(spool.MAX_RESULTS + 3):
        s.add(i, result(i))
    assert s.count == spool.MAX_RESULTS and s.task_ids()[0] == 3


def test_corrupt_file_ignored(tmp_path):
    path = tmp_path / "r.json"
    path.write_text("{bozuk")
    assert spool.ResultSpool(str(path)).count == 0
    path.write_text(json.dumps([{"task_id": "x", "result": {}}, {"task_id": 7, "result": result(7)}, 3]))
    assert spool.ResultSpool(str(path)).task_ids() == [7]


def test_discard_for_clone(tmp_path):
    s = spool.ResultSpool(str(tmp_path / "r.json"))
    s.add(1, result(1))
    assert s.discard() == 1 and s.count == 0


class Clock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t


def test_server_info_fifteen_second_rule():
    clock = Clock()
    h = protocol.ServerHandshake(clock)
    assert h.last_known("result_ack") is None
    h.on_connected()
    assert h.supports("result_ack") is None   # henüz bilinmiyor
    h.on_server_info({"action": "server_info", "features": ["update_result_ack", "result_ack", 5]})
    assert h.supports("result_ack") is True and h.supports("other") is False
    h.on_connected()
    assert h.supports("result_ack") is None and h.last_known("result_ack") is True
    clock.t += 16   # server_info gelmedi: eski sunucu
    assert h.supports("result_ack") is False and h.last_known("result_ack") is False


def test_update_result_reporter_steps():
    clock = Clock()
    r = protocol.UpdateResultReporter(protocol.ServerHandshake(clock))
    r.on_connected()
    assert r.next(None) == protocol.NOTHING
    assert r.next("abc") == protocol.WAIT
    r.handshake.on_server_info({"features": ["update_result_ack"]})
    assert r.next("abc") == protocol.SEND_AND_KEEP
    r.sent("abc")
    assert r.next("abc") == protocol.NOTHING
    clock.t += 61
    assert r.next("abc") == protocol.SEND_AND_KEEP
    assert protocol.UpdateResultReporter.acknowledges({"result_id": "abc"}, "abc")
    assert not protocol.UpdateResultReporter.acknowledges({"result_id": "abd"}, "abc")
    r.on_connected()
    clock.t += 16
    assert r.next("abc") == protocol.SEND_AND_MARK   # eski sunucu: gönder ve kenara al


def test_backoff():
    assert protocol.ceiling(0) == 2 and protocol.ceiling(3) == 16 and protocol.ceiling(9) == 60
    assert protocol.delay(0, None, lambda: 1.0) == 2
    assert protocol.delay(5, "auth", lambda: 0.5) == 60 + 30
    assert protocol.delay(5, "clone", lambda: 0.0) == 600
    assert protocol.rejection(4401) == "auth" and protocol.rejection(4409) == "clone"
    assert protocol.rejection(1006) is None
    assert [protocol.next_attempt(a) for a in (0, 1, 4, 5, 9)] == [1, 2, 5, 5, 5]
