#!/usr/bin/env python3
"""Run periodically for the dedicated ToolBox relay mailbox."""
import email
import email.policy
import imaplib
import json
import os
import re
import ssl
import sys
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def envelope(raw):
    msg = email.message_from_bytes(raw, policy=email.policy.default)
    if msg.get_content_type() == "multipart/report":
        part = next((p for p in msg.walk() if p.get_content_type() == "message/delivery-status"), None)
        if part is None:
            return None
        fields = part.get_payload()
        if not isinstance(fields, list) or len(fields) < 2:
            return None
        origin = str(fields[0].get("Original-Envelope-Id", ""))
        match = re.fullmatch(r"([a-f0-9]{20})\.([a-f0-9]{48})", origin)
        action = str(fields[1].get("Action", "")).strip().lower()
        status = {"delivered": "EmailDelivered", "failed": "EmailFailed", "delayed": "EmailDelayed"}.get(action)
        if match and status:
            return "status", {"id": match[1], "proof": match[2], "result": status}
        return None
    receipt = str(msg.get("X-ToolBox-ID", ""))
    proof = str(msg.get("X-ToolBox-Proof", ""))
    if not re.fullmatch("[a-f0-9]{20}", receipt) or not re.fullmatch("[a-f0-9]{48}", proof):
        return None
    if msg.get_content_type() != "text/plain":
        return None
    return "received", {"id": receipt, "proof": proof, "body": msg.get_content()}


def forward():
    endpoint = os.environ["TOOLBOX_URL"].rstrip("/") + "/v1/mail/"
    url = urllib.parse.urlparse(endpoint)
    if url.scheme != "https" or not url.hostname or url.username or url.password:
        raise ValueError("TOOLBOX_URL requires HTTPS")
    opener = urllib.request.build_opener(NoRedirect())
    failed = False
    with imaplib.IMAP4_SSL(os.environ["TOOLBOX_IMAP_HOST"], ssl_context=ssl.create_default_context(), timeout=15) as box:
        box.login(os.environ["TOOLBOX_IMAP_USER"], os.environ["TOOLBOX_IMAP_PASSWORD"])
        if box.select("INBOX")[0] != "OK":
            raise OSError("Cannot select INBOX")
        status, data = box.uid("search", None, "UNSEEN", "SMALLER", "131072")
        if status != "OK":
            raise OSError("Cannot search the relay mailbox")
        for uid in data[0].split():
            status, rows = box.uid("fetch", uid, "(BODY.PEEK[])")
            if status != "OK":
                failed = True
                continue
            raw = next((row[1] for row in rows if isinstance(row, tuple)), None)
            if not raw:
                continue
            item = envelope(raw)
            if not item:
                continue
            route, payload = item
            request = urllib.request.Request(endpoint + route, json.dumps(payload).encode(), {"Content-Type": "application/json"})
            try:
                with opener.open(request, timeout=15) as response:
                    if response.status != 200 or json.load(response).get("code") != "Saved":
                        failed = True
                        continue
            except (OSError, ValueError) as exc:
                # Do not expose the envelope, credentials or message in logs.
                print("Delivery requires attention", payload["id"], type(exc).__name__, file=sys.stderr)
                failed = True
                continue
            if box.uid("store", uid, "+FLAGS.SILENT", r"(\Seen)")[0] != "OK":
                failed = True
    return int(failed)


if __name__ == "__main__":
    sys.exit(forward())
