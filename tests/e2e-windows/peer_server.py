"""T7 (eş önbelleği) için sahte eş HTTP sunucusu. runner'ın LAN IP'sinde 8818'de dinler ve GET/HEAD
/pops-cache/<sha256> isteğine imzalı MSI'ın baytlarını (ya da --wrong ile bozuk baytları) verir. Gerçek ajan bunu
eş sanıp indirir; her durumda imzalı manifest'teki boyut ve SHA-256'yı kendisi doğrular, bu yüzden bozuk bayt SHA
uyuşmazlığına düşer (ajan sunucuya geri döner).

    python peer_server.py --bind 10.1.0.10 --port 8818 --msi B.msi --sha <sha256> [--wrong]
"""

import argparse
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def build(msi_path, sha, wrong):
    with open(msi_path, "rb") as f:
        data = f.read()
    if wrong:
        # Aynı uzunlukta ama farklı içerik: Content-Length doğru, SHA-256 yanlış (ajan sunucuya düşer)
        data = (b"\x00" + data[1:]) if data[:1] != b"\x00" else (b"\x01" + data[1:])
    path = "/pops-cache/" + sha

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def _send(self, body):
            if self.path != path:
                self.send_response(404)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            if body:
                self.wfile.write(data)

        def do_GET(self):
            self._send(True)

        def do_HEAD(self):
            self._send(False)

        def log_message(self, *a):
            sys.stderr.write("peer: " + (a[0] % a[1:]) + "\n")

    return Handler


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--bind", required=True)
    p.add_argument("--port", type=int, default=8818)
    p.add_argument("--msi", required=True)
    p.add_argument("--sha", required=True)
    p.add_argument("--wrong", action="store_true")
    a = p.parse_args()
    srv = ThreadingHTTPServer((a.bind, a.port), build(a.msi, a.sha, a.wrong))
    sys.stderr.write("peer %s:%d serving /pops-cache/%s (wrong=%s)\n" % (a.bind, a.port, a.sha, a.wrong))
    srv.serve_forever()


if __name__ == "__main__":
    main()
