"""Local in-app notification stub for Phase 4.

Accepts POST /dispatch with a JSON body and writes a delivery receipt to
/receipts/in_app/. Returns 200 with the receipt id so the
NotificationChannelAdapter can assert delivery during local smoke runs.
"""
from __future__ import annotations

import json
import os
import uuid
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer

CHANNEL = "in_app"
PORT = 9401
RECEIPTS_DIR = f"/receipts/{CHANNEL}"


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):  # noqa: N802 (stdlib contract)
        if self.path != "/dispatch":
            self.send_response(404)
            self.end_headers()
            return
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError:
            self.send_response(400)
            self.end_headers()
            return
        os.makedirs(RECEIPTS_DIR, exist_ok=True)
        receipt_id = str(uuid.uuid4())
        receipt = {
            "receipt_id": receipt_id,
            "channel": CHANNEL,
            "dispatched_at": datetime.now(timezone.utc).isoformat(),
            "payload": payload,
        }
        with open(f"{RECEIPTS_DIR}/{receipt_id}.json", "w", encoding="utf-8") as f:
            json.dump(receipt, f, ensure_ascii=False, indent=2)
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps({"receipt_id": receipt_id, "channel": CHANNEL}).encode("utf-8"))

    def do_GET(self):  # noqa: N802
        if self.path == "/health":
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"ok")
            return
        self.send_response(404)
        self.end_headers()

    def log_message(self, format, *args):  # noqa: A002
        return


if __name__ == "__main__":
    HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
