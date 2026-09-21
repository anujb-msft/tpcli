"""Real Rust/.NET process integration. This module never uses an Azure provider."""

import json
from contextlib import closing
import errno
import os
from pathlib import Path
import pty
import queue
import re
import select
import secrets
import selectors
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
CLI = Path(os.environ.get("TPCLI_BINARY", ROOT / "target/debug/tpcli"))
RUNTIME = ROOT / "runtime/Tpcli.Runtime/bin/Debug/net10.0/Tpcli.Runtime.dll"
WATCHDOG = ROOT / "runtime/Tpcli.Watchdog/bin/Debug/net10.0/Tpcli.Watchdog.dll"
TARGET = "pstn:+12025550123"


def eventually(function, predicate=lambda value: bool(value), timeout=8):
    deadline = time.monotonic() + timeout
    while True:
        value = function()
        if predicate(value):
            return value
        if time.monotonic() >= deadline:
            raise AssertionError(f"Condition was not reached within {timeout} seconds")
        time.sleep(0.05)


class Harness:
    def __init__(self, settings=None):
        self.temp = tempfile.TemporaryDirectory(prefix="tpcli-e2e-")
        self.root = Path(self.temp.name)
        self.root.chmod(0o700)
        self.processes = []
        self.logs = []
        self.brokers = []
        self.routes = {}
        self.descriptors = []
        self.token = secrets.token_hex(32)
        self.key = secrets.token_hex(32)
        self.control_path = self.root / "control.db"
        self.config = self.root / "profile.toml"
        port_socket = socket.socket()
        port_socket.bind(("127.0.0.1", 0))
        self.url = f"http://127.0.0.1:{port_socket.getsockname()[1]}"
        port_socket.close()
        self.env = {
            key: value
            for key, value in os.environ.items()
            if not key.lower().startswith(("tpcli", "azure_", "aspnetcore_"))
        }
        self.env.update(
            {
                "Tpcli__Mode": "local-fake",
                "Tpcli__FakeToken": self.token,
                "Tpcli__Store__Provider": "sqlite",
                "Tpcli__Store__ConnectionString": f"Data Source={self.control_path}",
                "ASPNETCORE_URLS": self.url,
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "DOTNET_NOLOGO": "1",
                "TPCLI_CONFIG": str(self.config),
                "TPCLI_PROFILE": "test",
                "TPCLI_TEST_MODE": "1",
                "TPCLI_TEST_KEY": self.key,
                "TPCLI_FAKE_TOKEN": self.token,
            }
        )
        self.env.update(settings or {})
        self.config.write_text(
            '[profiles.test]\nprovider_mode = "local-fake"\n'
            f"runtime_url = {json.dumps(self.url)}\n"
            f"data_dir = {json.dumps(str(self.root / 'history'))}\n"
        )
        self.config.chmod(0o600)

    def close(self):
        for descriptor in self.descriptors:
            os.close(descriptor)
        self.descriptors.clear()
        failures = []
        for process in reversed(self.processes):
            if process.poll() is None:
                process.send_signal(signal.SIGCONT)
                process.terminate()
                try:
                    process.wait(timeout=6)
                except subprocess.TimeoutExpired:
                    process.kill()
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired as error:
                        failures.append(error)
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream is not None:
                    stream.close()
        for log in self.logs:
            log.close()
        self.temp.cleanup()
        if failures:
            raise AssertionError("An owned test process did not exit after cleanup") from failures[0]

    def service(self, dll, name):
        log = open(self.root / f"{name}.log", "w+", encoding="utf-8")
        self.logs.append(log)
        process = subprocess.Popen(
            ["dotnet", str(dll)],
            cwd=self.root,
            env=self.env,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        self.processes.append(process)
        return process

    def launch(self):
        self.runtime = self.service(RUNTIME, "runtime")
        deadline = time.monotonic() + 15
        while True:
            if self.runtime.poll() is not None:
                raise AssertionError(
                    "Runtime failed to start:\n"
                    + (self.root / "runtime.log").read_text()
                )
            try:
                self.http("GET", "/v1/capabilities")
                break
            except urllib.error.URLError:
                if time.monotonic() >= deadline:
                    raise AssertionError("Local runtime never became responsive")
                time.sleep(0.1)
        self.watchdog = self.service(WATCHDOG, "watchdog")
        time.sleep(0.1)
        if self.watchdog.poll() is not None:
            raise AssertionError(
                "Watchdog failed to start:\n"
                + (self.root / "watchdog.log").read_text()
            )
        if not self.control_path.exists():
            raise AssertionError("Runtime did not initialize the configured durable control store")
        return self

    def http(self, method, path, body=None, expected=200):
        request = urllib.request.Request(
            self.url + path,
            data=None if body is None else json.dumps(body).encode(),
            headers={
                "Authorization": "Bearer " + self.token,
                "Content-Type": "application/json",
            },
            method=method,
        )
        try:
            response = urllib.request.urlopen(request, timeout=3)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            content = response.read()
            value = json.loads(content) if content else None
            if response.status != expected:
                raise AssertionError(
                    f"{method} {path}: expected {expected}, got {response.status}: {value}"
                )
            return value

    def cli(self, *args, input=None, session=None, code=0, timeout=12):
        command = [str(CLI), "--json"]
        if session is not None:
            command += ["--session", session]
        result = subprocess.run(
            command + list(args),
            input=input,
            env=self.env,
            cwd=self.root,
            text=True,
            capture_output=True,
            timeout=timeout,
        )
        if result.returncode != code:
            raise AssertionError(
                f"{args}: expected exit {code}, got {result.returncode}\n"
                f"{result.stdout}\n{result.stderr}"
            )
        if self.token in result.stdout + result.stderr or self.key in result.stdout + result.stderr:
            raise AssertionError("A secret was printed by the CLI")
        return json.loads(result.stdout)

    def broker(self):
        process = subprocess.Popen(
            [str(CLI), "--json", "session", "run", "--owner-stdin"],
            env=self.env,
            cwd=self.root,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.processes.append(process)
        selector = selectors.DefaultSelector()
        selector.register(process.stdout, selectors.EVENT_READ)
        try:
            if not selector.select(timeout=8):
                raise AssertionError("Broker readiness handshake timed out")
            line = process.stdout.readline()
        finally:
            selector.close()
        if not line:
            raise AssertionError("Broker exited: " + process.stderr.read().decode())
        ready = json.loads(line)
        if ready.get("type") != "session.ready":
            raise AssertionError(f"Broker readiness failed: {ready}")
        if ready["provider_mode"] != "local-fake":
            raise AssertionError("Test refused a non-simulated provider")
        self.brokers.append(process)
        self.routes[ready["session_id"]] = ready["socket"]
        return process, ready["session_id"]

    def start(self, session, task="public-hours-test-brief", duration=120, key=None):
        return self.cli(
            "calls",
            "start",
            "--request-stdin",
            "--idempotency-key",
            key or str(uuid.uuid4()),
            session=session,
            input=json.dumps(
                {
                    "target": TARGET,
                    "task": task,
                    "max_duration_seconds": duration,
                    "allow_voicemail": False,
                }
            ),
        )

    def state(self, call_id):
        return self.http("GET", f"/v1/calls/{call_id}")

    def connected(self, call_id):
        return eventually(
            lambda: self.state(call_id), lambda state: state["lifecycle"] == "connected"
        )

    def signal(self, call_id, kind, payload, event_id=None):
        return self.http(
            "POST",
            f"/test/calls/{call_id}/signals",
            {
                "type": kind,
                "payload": payload,
                "provider_event_id": event_id or str(uuid.uuid4()),
            },
            expected=202,
        )

    def transcript(self, call_id, text, segment="test-segment", revision=1, final=True):
        self.signal(
            call_id,
            "transcript.final" if final else "transcript.partial",
            {
                "segment_id": segment,
                "speaker": "recipient",
                "text": text,
                "revision": revision,
                "final": final,
                "interrupted": False,
                "delivery": "received",
            },
        )

    def batch(self, session, call_id, after=0, wait=0):
        return self.cli(
            "calls",
            "events",
            call_id,
            "--after",
            str(after),
            "--wait-seconds",
            str(wait),
            session=session,
        )

    def metadata(self, query, parameters=()):
        with closing(sqlite3.connect(f"file:{self.control_path}?mode=ro", uri=True)) as db:
            db.row_factory = sqlite3.Row
            return [dict(row) for row in db.execute(query, parameters).fetchall()]

    def stored_state(self, call_id):
        return json.loads(self.metadata(
            "SELECT state_json FROM calls WHERE id=?", (call_id,)
        )[0]["state_json"])

    def tool(self, call_id, name, arguments, tool_id=None):
        tool_id = tool_id or str(uuid.uuid4())
        self.signal(call_id, "tool.requested", {
            "tool_call_id": tool_id, "name": name, "arguments": arguments,
        })
        return tool_id

    def request_approval(self, session, call_id, tool_id=None):
        self.tool(call_id, "request_approval", {
            "action": "confirm_appointment",
            "description": "Book the offered appointment with no charge.",
            "material_terms": {"time": "2030-01-02T10:00:00Z", "cost": "none"},
        }, tool_id)
        return eventually(
            lambda: self.cli("approvals", "list", "--call", call_id, session=session),
            lambda approvals: any(approval["status"] == "pending" for approval in approvals),
        )

    def stream(self, session, call_id, after=0, consume=True):
        process = subprocess.Popen(
            [
                str(CLI), "--json", "--session", session,
                "calls", "events", call_id, "--after", str(after), "--follow",
            ],
            env=self.env,
            cwd=self.root,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.processes.append(process)
        messages = queue.Queue()
        if consume:
            def read():
                for line in process.stdout:
                    messages.put(json.loads(line))
            thread = threading.Thread(target=read, daemon=True)
            thread.start()
        return process, messages


class CrossProcessTests(unittest.TestCase):
    def harness(self, settings=None):
        harness = Harness(settings)
        self.addCleanup(harness.close)
        return harness.launch()

    def test_fake_provider_stays_local_despite_process_proxy_configuration(self):
        h = self.harness()
        for key in ("http_proxy", "https_proxy", "all_proxy"):
            h.env[key] = h.env[key.upper()] = "http://127.0.0.1:1"
        h.env["no_proxy"] = h.env["NO_PROXY"] = ""
        _, session = h.broker()
        receipt = h.start(session)
        self.assertEqual(h.connected(receipt["call_id"])["provider_mode"], "local-fake")

    def test_short_command_zero_viewers_concurrent_control_and_host_loss(self):
        h = self.harness()
        owner, session = h.broker()
        started = time.monotonic()
        receipt = h.start(session, task="private-brief-not-in-runtime-metadata", key="same-attempt")
        self.assertLess(time.monotonic() - started, 1.0)
        call = receipt["call_id"]
        self.assertEqual(receipt["status"], "accepted")
        self.assertIsNone(owner.poll())
        state = h.connected(call)
        self.assertEqual(state["provider_mode"], "local-fake")
        self.assertEqual(state["route"], "simulation")
        h.transcript(call, "private-transcript-not-in-runtime-metadata")
        batch = eventually(
            lambda: h.batch(session, call),
            lambda value: any(event["type"] == "transcript.final" for event in value["events"]),
        )
        self.assertGreater(len(batch["events"]), 0)
        retry = h.start(session, task="private-brief-not-in-runtime-metadata", key="same-attempt")
        self.assertEqual(receipt["command_id"], retry["command_id"])
        self.assertEqual(call, retry["call_id"])
        viewer, messages = h.stream(session, call)
        instruction = h.cli(
            "calls", "instruct", call, "--text-stdin",
            session=session, input="Ask only for public opening hours.",
        )
        h.cli("commands", "status", instruction["command_id"], session=session)
        h.cli("calls", "dtmf", call, "--digits-stdin", session=session, input="1#")
        eventually(
            lambda: h.cli("commands", "status", instruction["command_id"], session=session),
            lambda result: result["status"] != "accepted",
        )
        self.assertEqual(h.state(call)["lifecycle"], "connected")
        self.assertGreater(messages.qsize(), 0)
        viewer.send_signal(signal.SIGINT)
        viewer.wait(timeout=3)
        self.assertEqual(h.state(call)["lifecycle"], "connected")
        transcript = h.cli("transcripts", "show", call)
        self.assertTrue(any(
            segment["text"] == "private-transcript-not-in-runtime-metadata"
            for segment in transcript["segments"]
        ))
        begin = time.monotonic()
        owner.stdin.close()
        state = eventually(
            lambda: h.state(call),
            lambda value: value["lifecycle"] in ("ending", "ended", "termination_unknown"),
            timeout=2,
        )
        self.assertLess(time.monotonic() - begin, 2)
        self.assertEqual(state["termination_reason"], "owner_disconnected")
        owner.wait(timeout=8)
        self.assertEqual(h.state(call)["hangup_status"], "confirmed")
        h.cli("history", "show", call)
        for path in h.root.glob("*.log"):
            text = path.read_text()
            self.assertNotIn("private-brief-not-in-runtime-metadata", text)
            self.assertNotIn("private-transcript-not-in-runtime-metadata", text)
            self.assertNotIn(h.token, text)
        for path in h.root.glob("control.db*"):
            data = path.read_bytes()
            self.assertNotIn(b"private-brief-not-in-runtime-metadata", data)
            self.assertNotIn(b"private-transcript-not-in-runtime-metadata", data)
        database = h.root / "history/history.db"
        connection = sqlite3.connect(f"file:{database}?mode=ro", uri=True)
        try:
            with self.assertRaises(sqlite3.DatabaseError):
                connection.execute("SELECT * FROM sqlite_master").fetchall()
        finally:
            connection.close()

    def test_replay_live_handoff_finite_poll_and_idle_cursor(self):
        h = self.harness()
        _, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        viewer, messages = h.stream(session, call)
        for index in range(40):
            h.transcript(call, f"handoff-{index}", segment=f"handoff-{index}")
        collected = []
        deadline = time.monotonic() + 8
        while len([e for e in collected if e.get("payload", {}).get("segment_id", "").startswith("handoff-")]) < 40:
            self.assertLess(time.monotonic(), deadline)
            collected.append(messages.get(timeout=2))
        sequences = [event["sequence"] for event in collected]
        self.assertEqual(sequences, sorted(set(sequences)))
        after = 0
        replayed = []
        while True:
            batch = h.batch(session, call, after)
            replayed.extend(batch["events"])
            after = batch["next_cursor"]
            if not batch["has_more"]:
                break
        prefix = [event for event in replayed if event["sequence"] <= sequences[-1]]
        self.assertEqual(prefix, collected)
        idle = h.batch(session, call, after, wait=1)
        self.assertEqual(idle["events"], [])
        self.assertEqual(idle["next_cursor"], after)
        self.assertFalse(idle["has_more"])
        h.cli("calls", "stop", call, session=session)
        h.cli("calls", "wait", call, session=session, code=5)
        viewer.wait(timeout=5)

    def test_duplicate_reordered_segments_and_interrupted_output(self):
        h = self.harness()
        _, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        h.transcript(call, "final revision", segment="same", revision=2)
        h.transcript(call, "stale partial", segment="same", revision=1, final=False)
        payload = {
            "segment_id": "assistant-response", "speaker": "assistant",
            "text": "Generated but interrupted sentence.", "revision": 1,
            "final": True, "interrupted": False, "delivery": "generated",
        }
        h.signal(call, "transcript.final", payload, event_id="duplicate-event")
        h.signal(call, "transcript.final", payload, event_id="duplicate-event")
        h.signal(call, "transcript.interrupted", {**payload, "interrupted": True, "delivery": "unknown"})
        transcript = eventually(
            lambda: h.cli("transcripts", "show", call),
            lambda value: any(segment["interrupted"] for segment in value["segments"]),
        )
        segments = {segment["segment_id"]: segment for segment in transcript["segments"]}
        self.assertEqual(segments["same"]["text"], "final revision")
        self.assertTrue(segments["assistant-response"]["interrupted"])
        self.assertEqual(segments["assistant-response"]["delivery"], "unknown")
        h.cli("calls", "stop", call, session=session)
        h.cli("calls", "wait", call, session=session, code=5)

    def test_approvals_are_exact_one_time_and_expiry_never_approves(self):
        h = self.harness({"Tpcli__Fake__ApprovalTimeoutMilliseconds": "1200"})
        _, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        approval = h.request_approval(session, call)[-1]
        h.cli(
            "approvals", "resolve", approval["approval_id"], "--decision", "approve",
            "--action-hash", "sha256:" + "0" * 64, session=session, code=6,
        )
        resolution = h.cli(
            "approvals", "resolve", approval["approval_id"], "--decision", "approve",
            "--action-hash", approval["action_hash"], session=session,
        )
        eventually(
            lambda: h.cli("commands", "status", resolution["command_id"], session=session),
            lambda receipt: receipt["status"] == "succeeded",
        )
        h.cli(
            "approvals", "resolve", approval["approval_id"], "--decision", "approve",
            "--action-hash", approval["action_hash"], session=session, code=6,
        )
        approvals = h.request_approval(session, call)
        pending = next(item for item in approvals if item["status"] == "pending")
        expired = eventually(
            lambda: h.cli("approvals", "list", "--call", call, session=session),
            lambda values: next(a for a in values if a["approval_id"] == pending["approval_id"])["status"] == "expired",
            timeout=4,
        )
        self.assertTrue(any(item["status"] == "approved" and item.get("actor") for item in expired))
        h.cli(
            "approvals", "resolve", pending["approval_id"], "--decision", "approve",
            "--action-hash", pending["action_hash"], session=session, code=6,
        )
        h.cli("calls", "stop", call, session=session)
        h.cli("calls", "wait", call, session=session, code=5)

    def test_operator_instruction_invalidates_stale_approval(self):
        h = self.harness()
        _, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        approval = h.request_approval(session, call)[-1]
        instruction = h.cli(
            "calls", "instruct", call, "--text-stdin", session=session,
            input="Do not book; gather availability only.",
        )
        eventually(
            lambda: h.cli("commands", "status", instruction["command_id"], session=session),
            lambda receipt: receipt["status"] == "succeeded",
        )
        h.cli(
            "approvals", "resolve", approval["approval_id"], "--decision", "approve",
            "--action-hash", approval["action_hash"], session=session, code=6,
        )
        self.assertEqual(h.state(call)["lifecycle"], "connected")

    def test_ambiguous_create_is_not_redialed_after_reconciliation(self):
        h = self.harness({
            "Tpcli__Fake__CreateAmbiguous": "true",
            "Tpcli__Fake__AutoConnect": "false",
        })
        owner, session = h.broker()
        first = h.start(session, key="ambiguous-attempt")
        call = first["call_id"]
        eventually(
            lambda: h.metadata("SELECT dispatch_status FROM calls WHERE id=?", (call,)),
            lambda rows: rows[0]["dispatch_status"] == "ambiguous",
        )
        retry = h.start(session, key="ambiguous-attempt")
        self.assertEqual(first["command_id"], retry["command_id"])
        self.assertEqual(call, retry["call_id"])
        self.assertEqual(h.metadata("SELECT dial_count FROM fake_calls WHERE call_id=?", (call,))[0]["dial_count"], 1)
        owner.stdin.close()
        h.signal(call, "connected", {"connection_id": "fake:" + call})
        state = eventually(lambda: h.state(call), lambda state: state["hangup_status"] == "confirmed")
        self.assertEqual(state["lifecycle"], "ended")
        self.assertNotEqual(state["task_outcome"], "completed")
        self.assertEqual(h.metadata("SELECT dial_count FROM fake_calls WHERE call_id=?", (call,))[0]["dial_count"], 1)

    def test_owner_loss_while_dialing_tombstones_late_connections(self):
        h = self.harness({"Tpcli__Fake__AutoConnect": "false"})
        owner, session = h.broker()
        call = h.start(session)["call_id"]
        eventually(lambda: h.state(call), lambda state: state["lifecycle"] == "dialing")
        owner.stdin.close()
        eventually(
            lambda: h.state(call),
            lambda state: state["lifecycle"] in ("ending", "ended", "termination_unknown"),
            timeout=2,
        )
        h.signal(call, "connected", {"connection_id": "fake:" + call})
        state = eventually(lambda: h.state(call), lambda state: state["hangup_status"] == "confirmed")
        self.assertEqual(state["lifecycle"], "ended")
        self.assertEqual(state["termination_reason"], "owner_disconnected")

    def test_unknown_hangup_never_becomes_completed_success(self):
        h = self.harness({"Tpcli__Fake__HangupUnknown": "true"})
        _, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        h.tool(call, "report_result", {"outcome": "completed", "summary": "Simulated task result."})
        eventually(lambda: h.state(call), lambda state: state["task_outcome"] == "completed")
        h.cli("calls", "stop", call, session=session)
        result = h.cli("calls", "wait", call, session=session, code=5)
        self.assertEqual(result["hangup_status"], "unknown")
        self.assertEqual(result["lifecycle"], "termination_unknown")

    def test_deadline_preempts_pending_approval(self):
        h = self.harness({"Tpcli__Fake__TestDeadlineMilliseconds": "2200"})
        _, session = h.broker()
        call = h.start(session, duration=30)["call_id"]
        h.connected(call)
        h.request_approval(session, call)
        state = eventually(lambda: h.state(call), lambda value: value["lifecycle"] == "ended", timeout=4)
        self.assertEqual(state["termination_reason"], "deadline")
        self.assertNotEqual(state["task_outcome"], "completed")
        approvals = h.cli("approvals", "list", "--call", call, session=session)
        self.assertTrue(all(item["status"] != "pending" for item in approvals))

    def test_runtime_kill_is_terminated_by_a_separate_watchdog_process(self):
        h = self.harness()
        owner, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        h.runtime.kill()
        h.runtime.wait(timeout=3)
        begin = time.monotonic()
        attempts = eventually(
            lambda: h.metadata(
                "SELECT executor,status,reason FROM termination_attempts WHERE call_id=?",
                (call,),
            ),
            lambda rows: any(row["executor"].startswith("watchdog") for row in rows),
            timeout=6.5,
        )
        self.assertLess(time.monotonic() - begin, 6.5)
        self.assertIsNone(h.watchdog.poll())
        self.assertTrue(any(row["reason"] == "worker_lost" for row in attempts))
        state = eventually(
            lambda: h.stored_state(call),
            lambda value: value["hangup_status"] == "confirmed",
        )
        self.assertEqual(state["lifecycle"], "ended")
        self.assertNotEqual(state["task_outcome"], "completed")
        self.assertNotEqual(state["transcript_status"], "complete")
        owner.wait(timeout=8)
        local = h.cli("calls", "status", call, "--offline")["state"]
        self.assertEqual(local["lifecycle"], "termination_unknown")
        self.assertEqual(local["hangup_status"], "unknown")
        self.assertEqual(local["transcript_status"], "partial")

    def test_lost_client_receipt_is_recovered_without_another_dial(self):
        h = self.harness()
        _, session = h.broker()
        payload = {
            "target": TARGET, "task": "lost-receipt-test", "max_duration_seconds": 120,
            "allow_voicemail": False,
        }
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.connect(h.routes[session])
            client.sendall((json.dumps({
                "schema_version": "1", "session_id": session, "op": "submit",
                "operation": "calls.start", "idempotency_key": "lost-receipt",
                "payload": payload,
            }) + "\n").encode())
            client.shutdown(socket.SHUT_WR)
            eventually(lambda: h.metadata("SELECT id FROM calls"))
        receipt = h.start(session, task="lost-receipt-test", key="lost-receipt")
        call = receipt["call_id"]
        h.connected(call)
        saved = h.cli("commands", "status", receipt["command_id"], "--offline")["receipt"]
        self.assertEqual(saved["call_id"], call)
        self.assertEqual(h.metadata("SELECT dial_count FROM fake_calls WHERE call_id=?", (call,))[0]["dial_count"], 1)
        self.assertEqual(len(h.metadata("SELECT id FROM calls")), 1)

    def test_slow_subscriber_disconnect_does_not_block_controls_or_capture(self):
        h = self.harness()
        _, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as slow:
            slow.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024)
            slow.settimeout(6)
            slow.connect(h.routes[session])
            slow.sendall((json.dumps({
                "schema_version": "1", "session_id": session, "op": "events",
                "id": call, "after": 0, "follow": True,
            }) + "\n").encode())
            slow.shutdown(socket.SHUT_WR)
            for index in range(64):
                h.transcript(call, "bounded-slow-viewer-" + "x" * 24000, segment=f"slow-{index}")
            began = time.monotonic()
            h.cli("calls", "instruct", call, "--text-stdin", session=session, input="Continue asking for public hours only.")
            self.assertLess(time.monotonic() - began, 1)
            time.sleep(4.3)
            self.assertEqual(h.state(call)["lifecycle"], "connected")
            received = bytearray()
            while True:
                data = slow.recv(65536)
                if not data:
                    break
                received.extend(data)
                self.assertLess(len(received), 2 * 1024 * 1024)
        transcript = h.cli("transcripts", "show", call)
        self.assertEqual(len([s for s in transcript["segments"] if s["segment_id"].startswith("slow-")]), 64)
        cursor = max(
            (message.get("sequence", 0) for message in
             (json.loads(line) for line in received.splitlines())), default=0,
        )
        replay = h.batch(session, call, cursor)
        self.assertGreater(replay["next_cursor"], cursor)
        h.cli("calls", "stop", call, session=session)

    def test_parent_loss_revokes_even_while_the_host_pipe_remains_open(self):
        h = self.harness()
        host = subprocess.Popen(
            [sys.executable, "-c",
             "import subprocess,sys; child=subprocess.Popen(sys.argv[1:]); print(child.pid,file=sys.stderr,flush=True); child.wait()",
             str(CLI), "--json", "session", "run", "--owner-stdin"],
            env=h.env, cwd=h.root, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        h.processes.append(host)
        broker_pid = int(host.stderr.readline())
        ready = json.loads(host.stdout.readline())
        session = ready["session_id"]
        call = h.start(session)["call_id"]
        h.connected(call)
        began = time.monotonic()
        host.kill()
        host.wait(timeout=3)
        self.assertFalse(host.stdin.closed)
        state = eventually(
            lambda: h.state(call),
            lambda value: value["lifecycle"] in ("ending", "ended", "termination_unknown"),
            timeout=2,
        )
        self.assertLess(time.monotonic() - began, 2)
        self.assertEqual(state["termination_reason"], "owner_disconnected")
        eventually(lambda: not Path(ready["socket"]).exists(), timeout=8)

        def broker_exited():
            try:
                os.kill(broker_pid, 0)
            except ProcessLookupError:
                return True
            return False

        eventually(broker_exited, timeout=4)
        self.assertFalse(host.stdin.closed)

    def test_session_exec_preserves_shell_job_control_and_viewer_interrupt(self):
        h = self.harness()
        master, slave = pty.openpty()
        h.descriptors.append(master)
        env = h.env.copy()
        env.update({"PS1": "TPCLI_TEST> ", "TERM": "dumb", "PROMPT_COMMAND": ""})
        shell = subprocess.Popen(
            [sys.executable, "-c",
             "import fcntl,os,sys,termios; fcntl.ioctl(0,termios.TIOCSCTTY,0); os.execvpe(sys.argv[1],sys.argv[1:],os.environ)",
             str(CLI), "--json", "session", "exec", "--", "bash", "--noprofile", "--norc", "-i"],
            env=env, cwd=h.root, stdin=slave, stdout=slave, stderr=slave,
            start_new_session=True,
        )
        os.close(slave)
        h.processes.append(shell)

        def until(marker, timeout=8):
            result = bytearray()
            deadline = time.monotonic() + timeout
            while marker not in result:
                remaining = deadline - time.monotonic()
                self.assertGreater(remaining, 0, bytes(result).decode(errors="replace"))
                self.assertTrue(select.select([master], [], [], remaining)[0])
                result.extend(os.read(master, 65536))
            return bytes(result)

        until(b"TPCLI_TEST> ")
        os.write(master, b"printf 'SCOPE:%s\\n' \"$TPCLI_SESSION\"\n")
        output = until(b"TPCLI_TEST> ")
        session = re.search(rb"SCOPE:(sess_[a-f0-9]+)", output).group(1).decode()
        call = h.start(session)["call_id"]
        h.connected(call)
        os.write(master, f"{CLI} --json calls events {call} --follow\n".encode())
        until(b'"sequence"')
        os.write(master, b"\x03")
        until(b"TPCLI_TEST> ")
        self.assertIsNone(shell.poll())
        self.assertEqual(h.state(call)["lifecycle"], "connected")
        began = time.monotonic()
        os.write(master, b"exit 7\n")
        eventually(
            lambda: h.state(call),
            lambda value: value["lifecycle"] in ("ending", "ended", "termination_unknown"),
            timeout=2,
        )
        self.assertLess(time.monotonic() - began, 2)
        deadline = time.monotonic() + 8
        while shell.poll() is None:
            self.assertLess(time.monotonic(), deadline, "Supervised shell did not exit")
            if select.select([master], [], [], 0.05)[0]:
                try:
                    os.read(master, 65536)
                except OSError as error:
                    if error.errno != errno.EIO:
                        raise
        self.assertEqual(shell.returncode, 7)

    @unittest.skipUnless(hasattr(signal, "SIGSTOP"), "Unix supervision test")
    def test_silent_owner_lease_without_client_activity(self):
        h = self.harness()
        owner, session = h.broker()
        call = h.start(session)["call_id"]
        h.connected(call)
        owner.send_signal(signal.SIGSTOP)
        try:
            # Read server timestamps after any already-sent heartbeat has been processed.
            time.sleep(0.15)
            last_heartbeat = h.metadata(
                "SELECT lease_expires_ms-15000 AS accepted_ms FROM sessions WHERE id=?",
                (session,),
            )[0]["accepted_ms"]
            attempts = eventually(
                lambda: h.metadata(
                    "SELECT started_ms,reason FROM termination_attempts WHERE call_id=? ORDER BY started_ms",
                    (call,),
                ),
                lambda rows: bool(rows),
                timeout=18,  # Observation allowance; the server-side assertion is strictly 16000 ms.
            )
            self.assertEqual(attempts[0]["reason"], "owner_disconnected")
            self.assertLessEqual(attempts[0]["started_ms"] - last_heartbeat, 16000)
            effect = eventually(
                lambda: h.metadata("SELECT terminated_ms FROM fake_calls WHERE call_id=?", (call,)),
                lambda rows: rows[0]["terminated_ms"] is not None,
            )[0]["terminated_ms"]
            self.assertLessEqual(effect - last_heartbeat, 16000)
        finally:
            owner.send_signal(signal.SIGCONT)


if __name__ == "__main__":
    unittest.main()
