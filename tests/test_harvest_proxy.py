import importlib.util
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import socket
import threading
import unittest

import requests


SOURCE = Path(__file__).resolve().parents[1] / "scripts" / "harvest-fanza-synopsis.py"
SPEC = importlib.util.spec_from_file_location("harvest_fanza_synopsis", SOURCE)
harvest = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(harvest)


class ProxyHandler(BaseHTTPRequestHandler):
    seen = []

    def do_GET(self):
        self.seen.append(self.path)
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"<html><title>test product</title></html>")

    def log_message(self, *_args):
        pass


class HarvestProxyTests(unittest.TestCase):
    def test_worker_request_uses_only_its_proxy(self):
        ProxyHandler.seen = []
        server = ThreadingHTTPServer(("127.0.0.1", 0), ProxyHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            client = harvest.Client(0, f"http://127.0.0.1:{server.server_port}")
            self.assertFalse(client.session.trust_env)
            response = client.get("http://www.dmm.co.jp/test-proxy-route")
            self.assertEqual(response.status_code, 200)
            self.assertEqual(ProxyHandler.seen, ["http://www.dmm.co.jp/test-proxy-route"])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_proxy_failure_does_not_retry_direct(self):
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        client = harvest.Client(0, f"http://127.0.0.1:{port}")
        with self.assertRaises(requests.exceptions.ProxyError):
            client.get("http://www.dmm.co.jp/test-proxy-route")


if __name__ == "__main__":
    unittest.main()
