import email.policy
import ssl
from email.message import EmailMessage
import unittest
from unittest.mock import MagicMock, patch

from forward_mail import envelope, forward


class RelayTests(unittest.TestCase):
    receipt = "a" * 20
    proof = "b" * 48

    def dm(self):
        m = EmailMessage(policy=email.policy.SMTP)
        m["X-ToolBox-ID"] = self.receipt
        m["X-ToolBox-Proof"] = self.proof
        m.set_content("Discord 1\n\n留言".encode(), maintype="text", subtype="plain", cte="base64")
        m.set_param("charset", "utf-8")
        return m.as_bytes()

    def test_relay_preserves_utf8_body(self):
        route, data = envelope(self.dm())
        self.assertEqual("received", route)
        self.assertEqual({"id": self.receipt, "proof": self.proof, "body": "Discord 1\n\n留言"}, data)
        self.assertIsNone(envelope(b"Subject: Unrelated mail\r\n\r\nHello"))

    def test_dsn_correlates_the_original_envelope(self):
        report = (
            'Content-Type: multipart/report; report-type=delivery-status; boundary="report"\r\n\r\n'
            '--report\r\nContent-Type: message/delivery-status\r\n\r\n'
            f'Original-Envelope-Id: {self.receipt}.{self.proof}\r\nReporting-MTA: dns; mx.example.invalid\r\n\r\n'
            'Final-Recipient: rfc822; user@example.invalid\r\nAction: failed\r\nStatus: 5.1.1\r\n\r\n--report--\r\n'
        ).encode()
        self.assertEqual(("status", {"id": self.receipt, "proof": self.proof, "result": "EmailFailed"}), envelope(report))
        self.assertIsNone(envelope(report.replace(self.proof.encode(), b"unknown")))

    @patch.dict("os.environ", {"TOOLBOX_URL": "https://toolbox.example.invalid", "TOOLBOX_IMAP_HOST": "imap.example.invalid",
                               "TOOLBOX_IMAP_USER": "test", "TOOLBOX_IMAP_PASSWORD": "test"})
    @patch("forward_mail.urllib.request.build_opener")
    @patch("forward_mail.imaplib.IMAP4_SSL")
    def test_imap_acknowledges_only_after_bot_confirmation(self, imap, opener):
        box = imap.return_value.__enter__.return_value
        box.select.return_value = ("OK", [])
        stored = []
        def uid(operation, *args):
            if operation == "search":
                return "OK", [b"7"]
            if operation == "fetch":
                self.assertIn("PEEK", args[-1])
                return "OK", [(b"header", self.dm())]
            stored.append(args)
            return "OK", []
        box.uid.side_effect = uid
        http = opener.return_value
        response = MagicMock(status=200)
        response.read.return_value = b'{"code":"Saved"}'
        http.open.return_value.__enter__.return_value = response
        self.assertEqual(0, forward())
        tls = imap.call_args.kwargs["ssl_context"]
        self.assertTrue(tls.check_hostname)
        self.assertEqual(ssl.CERT_REQUIRED, tls.verify_mode)
        self.assertEqual(1, len(stored))
        http.open.side_effect = OSError("connection lost")
        with patch("sys.stderr"):
            self.assertEqual(1, forward())
        self.assertEqual(1, len(stored))


if __name__ == "__main__":
    unittest.main()
