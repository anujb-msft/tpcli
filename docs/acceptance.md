# Validation and release gates

This is a **development implementation, not a completed or live-verified v1**.
The deterministic `local-fake` provider is deliberately separate from Azure.
Passing its tests is evidence about the software and supervision protocol, not
evidence that Teams Phone, Azure Voice Live, or a real recipient worked.

## Reproducible local evidence

The initial implementation checkpoint was exercised on macOS with Rust 1.89.0
and .NET SDK 10.0.400. The verification commands are:

```sh
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo build --workspace --locked
dotnet restore runtime/Tpcli.slnx --locked-mode
dotnet build runtime/Tpcli.slnx --no-restore
dotnet format runtime/Tpcli.slnx --verify-no-changes --no-restore
dotnet test runtime/Tpcli.slnx --no-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
az bicep build --file infra/main.bicep --stdout
```

Rust tests exercise actual SQLCipher, encryption/keys/WAL/backup, migrations,
deduplication, transcript revisions, incomplete local state, late receipts,
approval query/event ordering, protocol validation and shared JSON fixtures.
The command subprocess tests exercise the shipped binary, not an in-memory CLI
substitute.

The v0.4 local checkpoint passes **13 Rust tests, 291 .NET tests** (134 Azure
adapter and 157 runtime/control tests), and **29 Python subprocess tests**
(10 CLI and 19 cross-process scenarios). The integrated .NET build has no
warnings or errors; Rust strict clippy, both format checks, and offline Bicep
compilation also pass. These counts describe local evidence, not live trials.

An intermittent request-limit test failure was reproduced as a **TestServer
teardown race**, not a failed size-limit assertion: owner-WebSocket cleanup could
recreate SQLite sidecars while the test directory was being deleted. The harness
now aborts its owned sockets and awaits request cleanup before disposing the store.
The original test passed 20 consecutive repetitions after that change. Additional
tests cover 65,535/65,536/65,537-byte requests with and without Content-Length, and
16,383/16,384/16,385-byte UTF-8 tasks. No request or task limit was widened.

`tests/test_e2e.py` starts real Rust CLI/broker processes, the ASP.NET Core runtime,
and a separate .NET watchdog process. It uses ephemeral profiles, random injected
test keys and loopback-only fake service configuration. Tests include a real PTY
and interactive shell, stopped/killed processes, held-open owner pipes, slow
Unix-socket readers, and encrypted history with no viewer attached.

The GitHub Actions matrix runs Ubuntu and macOS. The production OS credential
store implementation is currently macOS Keychain; Linux CI uses explicit
ephemeral test keys and does not establish Linux production credential support.

## Acceptance coverage

| Criterion | Implemented/local evidence | Remaining release evidence |
| --- | --- | --- |
| A1: four scenarios | Task briefs, narrow tools, outcomes and a deterministic harness exist. | No controlled live trials performed. Direct Teams remains blocked; full v1 is not complete. |
| A2: authorized PSTN source | Pinned TPE SDK adapter uses `TeamsAppSource`, with no ACS-number or other-provider fallback. | Verify source account/number, licensing, funding, permissions and actual caller ID privately. |
| A3: equivalent controls | CLI/JSON/IPC share commands; instructions and DTMF work while another process follows events; exact approvals use the same API. | Real recipient behavior and model-driven task execution remain unverified. |
| A4: async ownership and capture | Short command exit leaves the broker alive; zero-viewer transcripts persist; late viewers replay and follow ordered events. | Validate the deployed transport under representative real load. |
| A5: bounded owner loss | Pipe EOF, host-parent loss with a surviving pipe, PTY shell exit, silent broker suspension, and dialing-time loss are subprocess-tested. | Validate deployed network/sleep scenarios and provider behavior. |
| A6: worker-independent termination | Killing the runtime leaves a separate watchdog process that uses durable control metadata to terminate the fake call. Unknown hangup remains unknown. | Prove the watchdog can reach the real provider and shared store independently of the media worker/laptop. |
| A7: no ambiguous redial | Same-key retries recover the original call/command; ambiguous create and lost client receipt tests assert exactly one fake dial. | Verify real provider reconciliation and operational escalation for unknown create responses. |
| A8: monotonic state and replay | Duplicate/reordered signals, final transcript revisions, interrupted output, terminal tombstones and one-use approval tests. | Exercise real callback/media retry and ordering behavior. |
| A9: selective approvals | Exact hashes, expiry, stale conversation rejection, explicit denial with actor audit, one-time consumption, late approval after owner loss, deadline preemption and no timeout approval. | Model/prompt-injection evaluations are not a guarantee that every spoken or IVR commitment is recognized. |
| A10: genuine encryption | Standard SQLite cannot open history; wrong/missing key, absent cipher, full disk, WAL, encrypted backup, migration and pruning checks. | Exercise operator key recovery and deployment-specific backup/retention procedures. |
| A11: content privacy | Fake runtime logs/control database exclude synthetic task/transcript markers; no application audio-file writer is used. | Audit deployed ingress, SDK/APM, traces, crash dumps and carrier/Azure retention. |
| A12: explicit uncertainty | Distinct result dimensions; completed-task/confirmed-hangup/encrypted-summary roundtrip preserving facts, commitments, outstanding items and sources; interrupted output, gap markers, incomplete local state after worker loss, and nonzero unknown-hangup results. | Measure real speech interruption/delivery and summary quality. |
| A13: hard deadline | Whole-call deadline terminates during a pending approval; approval waits do not extend it. | Validate real carrier setup/hold/termination timing. |
| A14: honest doctor | Offline doctor and auth status make zero network requests. Online capability checks are non-dialing. | Resource existence is not proof of working model, voice, TPE source or end-to-end media. |
| A15: observers are not owners | Viewer Ctrl+C, actual PTY shell job control, slow-reader disconnection, concurrent control and actual orphan-broker exit. | Validate the chosen agent host's lifetime and notification integration. |
| A16: durable async results | Lost receipt recovery, event-before-receipt storage, queried approvals before events, replay/live equality and idle cursor preservation. | A host must consume push events or issue finite polls; an exited stdout process cannot notify it. |
| A17: disposable media grants | Application capabilities are issued for a specific call/session/tenant/principal, endpoint and current worker/fence/owner generation. Durable digest-only storage atomically permits one consumer; expiry, revocation, stale scope, races and captured-log redaction are tested. | Verify PostgreSQL coordination and every deployed ingress/APM/logging layer. An expiring logging attestation is required; it is not a substitute for deployment evidence or proof of native ACS media identity. |

For the silent-owner A5 test, the assertions use **server timestamps**:
the fixed 15-second lease expiry identifies the last accepted heartbeat, and both
the recorded termination-attempt start and fake provider termination effect must
be at most **16,000 ms** later. The longer test polling timeout is only an
observation allowance, not a relaxed server-side requirement. Explicit owner
loss is observed within two seconds in the local subprocess tests.
`requested_ms` records queued termination intent; nullable `started_ms` is written
on the database clock only after invoking the provider abstraction, not when work
is queued. Deterministic tests separately exercise polling phase and dispatch
jitter. A 250 ms watchdog cadence leaves initiation margin without increasing the
one-second minimum retry cadence. No timestamp proves carrier-confirmed hangup.

No local test establishes the conversational onset p95 or recipient-perceived
interruption target. A single fast fake receipt is not a production p95 study.
Those measurements need an authorized recipient, carrier/client, region,
model/voice, warm/cold state and a documented sampling method.

## Deployment and operational limits

The supported hosted design uses separate runtime/watchdog processes and a
shared PostgreSQL control store. The local executable harness uses SQLite for
minimal server metadata; the client's transcript database is SQLCipher in both
modes. Hosted PostgreSQL, container images and cloud deployment have not been
exercised by this local harness.

The Bicep definitions are compiled without deployment. They do not provision
Teams numbers, grant tenant permissions, create credentials, replace another
application or authorize calls. The repository owner has not chosen a license;
no package/image release or public license grant is included.

Each live check and recipient trial requires separate operational authorization.
Record tenant/resource/destination details and resulting content privately,
never in public fixtures, CI logs, PR descriptions or issues.
