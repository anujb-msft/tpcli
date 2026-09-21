"""CLI-only privacy and offline checks. No cloud or OS credentials are used."""

import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
CLI = Path(os.environ.get("TPCLI_BINARY", ROOT / "target/debug/tpcli"))


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tpcli-offline-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.root.chmod(0o700)
        self.data = self.root / "data"
        self.config = self.root / "config.toml"
        self.env = {
            **os.environ,
            "TPCLI_CONFIG": str(self.config),
            "TPCLI_PROFILE": "test",
            "TPCLI_TEST_MODE": "1",
            "TPCLI_TEST_KEY": secrets.token_hex(32),
            "TPCLI_FAKE_TOKEN": secrets.token_hex(32),
        }
        self.env.pop("TPCLI_SESSION", None)
        self.env.pop("TPCLI_SOCKET", None)
        self.configure("http://127.0.0.1:1")

    def configure(self, url):
        self.config.write_text(
            '[profiles.test]\nprovider_mode = "local-fake"\n'
            f"runtime_url = {json.dumps(url)}\n"
            f"data_dir = {json.dumps(str(self.data))}\n"
        )
        self.config.chmod(0o600)

    def cli(self, *arguments, input=None, code=0, env=None):
        result = subprocess.run(
            [str(CLI), "--json", *arguments],
            input=input,
            text=True,
            capture_output=True,
            env=self.env if env is None else env,
            timeout=8,
        )
        self.assertEqual(
            result.returncode,
            code,
            f"{arguments}: {result.stdout} {result.stderr}",
        )
        for secret_name in ("TPCLI_TEST_KEY", "TPCLI_FAKE_TOKEN"):
            self.assertNotIn(self.env[secret_name], result.stdout + result.stderr)
        return json.loads(result.stdout)

    def test_doctor_and_auth_status_have_zero_network_operations(self):
        listener = socket.socket()
        self.addCleanup(listener.close)
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        listener.settimeout(0.05)
        connections = []
        stop = threading.Event()

        def accept():
            while not stop.is_set():
                try:
                    client, _ = listener.accept()
                except TimeoutError:
                    continue
                except OSError:
                    return
                connections.append(True)
                client.close()

        thread = threading.Thread(target=accept)
        thread.start()
        self.configure(f"http://127.0.0.1:{listener.getsockname()[1]}")
        try:
            doctor = self.cli("doctor")
            self.assertFalse(doctor["online"])
            self.assertEqual(doctor["runtime"]["network_operations"], 0)
            self.assertTrue(self.cli("auth", "status")["authenticated"])
        finally:
            stop.set()
            thread.join(timeout=1)
        self.assertEqual(connections, [])

    def test_real_cipher_is_unreadable_by_standard_sqlite(self):
        self.cli("doctor")
        path = self.data / "history.db"
        self.assertNotEqual(path.read_bytes()[:16], b"SQLite format 3\x00")
        connection = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        try:
            with self.assertRaises(sqlite3.DatabaseError):
                connection.execute("SELECT name FROM sqlite_master").fetchall()
        finally:
            connection.close()
        wrong = {**self.env, "TPCLI_TEST_KEY": secrets.token_hex(32)}
        self.assertEqual(
            self.cli("doctor", code=4, env=wrong)["error"]["code"],
            "ENCRYPTED_STORE_FAILED",
        )
        self.cli("doctor")

    def test_missing_test_key_does_not_consult_keychain(self):
        missing = dict(self.env)
        del missing["TPCLI_TEST_KEY"]
        error = self.cli("doctor", code=3, env=missing)["error"]
        self.assertEqual(error["code"], "KEY_UNAVAILABLE")
        self.assertIn("OS credentials are never consulted", error["message"])
        self.assertFalse(self.data.exists())

    def test_plaintext_existing_database_never_becomes_a_fallback(self):
        self.data.mkdir(mode=0o700)
        path = self.data / "history.db"
        connection = sqlite3.connect(path)
        connection.execute("CREATE TABLE plaintext_test(value TEXT)")
        connection.commit()
        connection.close()
        path.chmod(0o600)
        original = path.read_bytes()
        self.cli("doctor", code=4)
        self.assertEqual(path.read_bytes(), original)

    def test_start_requires_supervision_and_never_echoes_task(self):
        marker = "PRIVATE_TASK_NOT_AN_ERROR_MESSAGE"
        error = self.cli(
            "calls",
            "start",
            "--request-stdin",
            input=json.dumps({"target": "pstn:+12025550123", "task": marker}),
            code=3,
        )["error"]
        self.assertEqual(error["code"], "SESSION_REQUIRED")
        self.assertNotIn(marker, json.dumps(error))

    def test_headless_run_requires_a_host_pipe(self):
        error = self.cli("session", "run", input="", code=3)["error"]
        self.assertEqual(error["code"], "SESSION_REQUIRED")

    def test_unsafe_brief_file_is_rejected_before_network(self):
        brief = self.root / "brief.txt"
        brief.write_text("A private briefing.")
        brief.chmod(0o644)
        error = self.cli(
            "call", "pstn:+12025550123", "--task-file", str(brief), code=2
        )["error"]
        self.assertIn("0600", error["message"])

    def test_fake_configuration_cannot_reach_a_public_host(self):
        self.configure("http://runtime.example.invalid")
        self.assertEqual(
            self.cli("doctor", code=3)["error"]["code"], "CONFIGURATION"
        )

    def test_conflicting_stream_modes_return_a_structured_error(self):
        result = self.cli(
            "calls", "events", "call-id", "--follow", "--wait-seconds", "1", code=2
        )
        self.assertEqual(result["error"]["code"], "INVALID_INPUT")

    def test_test_key_is_never_accepted_for_azure(self):
        self.config.write_text(
            '[profiles.test]\nprovider_mode = "azure"\n'
            'runtime_url = "https://runtime.example.invalid"\n'
            'tenant_id = "00000000-0000-4000-8000-000000000001"\n'
            'client_id = "00000000-0000-4000-8000-000000000002"\n'
            'scope = "api://00000000-0000-4000-8000-000000000003/tpcli.control"\n'
            f"data_dir = {json.dumps(str(self.data))}\n"
        )
        self.assertEqual(
            self.cli("doctor", code=3)["error"]["code"], "TEST_MODE_REQUIRED"
        )


if __name__ == "__main__":
    unittest.main()
