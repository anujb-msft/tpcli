# CLI and supervising hosts

## Profiles and authentication

Copy `infra/profile.example.toml` into a private configuration file; pass it with
`--config` or `TPCLI_CONFIG`. The default is `~/.config/tpcli/config.toml`.
`--profile` / `TPCLI_PROFILE` select a named profile. Configuration rejects
unknown fields and never accepts access tokens or encryption keys.

Production currently targets macOS. `auth login` uses the configured Entra public
client's device authorization flow for the runtime scope, leaves sign-in/MFA to
the user, and stores access/refresh credentials in Keychain. It initializes a
random per-profile SQLCipher key only when no history/key already exists.
`auth status` is a local credential-status check. Neither command dials.

`doctor` is offline: configuration, credential-store key access, SQLCipher version,
encrypted history and integrity, and protocol support. It creates a new encrypted
history if necessary; it never opens an Azure or runtime connection. Run
`auth login` first for an uninitialized production profile. `doctor --online`
adds authenticated, non-dialing runtime readiness checks. It does **not** prove
PSTN licensing/funding, Voice Live media access, or a direct Teams route. Missing
read-only permissions produce unknown/blocked checks, not guessed readiness.

Use distinct data directories for distinct profiles. The database writer lock
prevents a second broker from writing the same profile. The first-release
envelope is one active call per profile; an unconfirmed previous termination
must be reconciled before a new attempt.

## Ownership is not observation

| Entry point | Owner | Exit behavior |
| --- | --- | --- |
| `session exec -- zsh` | The wrapped shell/agent and its foreground wrapper | Child exit, terminal/parent loss, or wrapper kill revokes calls |
| `session run` | The foreground terminal | Interrupt, terminate, hangup, or parent loss revokes calls |
| `session run --owner-stdin --json` | A host-held stdin pipe and foreground broker | Pipe EOF or host/parent loss revokes calls |
| `call TARGET --task-file FILE` | A fresh, single-call broker/scope | Closing this invocation revokes that call |
| `calls start/status/instruct/dtmf/stop` | None; use an existing session | Command exit does not revoke |
| `calls events` / `calls wait` | None; read-only observer | Viewer exit does not revoke |

There is no `--detach`, installed local daemon, implicit background owner, or
automatic ownership adoption. `call` always creates its own scope even when
`TPCLI_SESSION` is set; it refuses a busy profile instead of reusing its owner.

`session exec` gives its child a foreground process group on a terminal and
restores the original foreground group when the child exits. A shell's foreground
viewer receives its own Ctrl+C; this is not a request to kill the supervising shell.
The wrapper preserves the child's exit status.
For a headless host, use `--owner-stdin` and retain the pipe for exactly the host
session's lifetime. Do not leave a pipe held by an unrelated orphan process.
This mode requires an actual pipe, not a terminal or redirected regular file.
The broker observes pipe closure without an uncancellable stdin-reading thread.
Keep the ownership-only pipe open; send commands over IPC, not through that pipe.
Parent reparenting detection supplements the pipe/terminal contract; reusable PID
numbers are not the authorization mechanism.

The host reads the initial `session.ready` JSON for the routing ID and protected
socket path. A wrapper injects `TPCLI_SESSION`, `TPCLI_SOCKET`, `TPCLI_CONFIG`, and
`TPCLI_PROFILE` for its child. These references are not credentials: the socket
directory is user-private, the socket is mode 0600, and peer UID is checked.
An unrelated process with the same OS account is within the local trust boundary.

## Asynchronous commands

The exact protocol is in `contracts/v1/`. Global `--json` selects one JSON result
for finite commands and NDJSON for `--follow` and the attached single-call view.
Operational diagnostics go to stderr. Task/command text comes from stdin or
a user-owned, mode-0600 task file, not command-line arguments.

```json
{
  "target": "pstn:+12025550123",
  "task": "Ask for public opening hours. Identify as an AI assistant. Make no commitments.",
  "max_duration_seconds": 120,
  "allow_voicemail": false
}
```

That is a reserved example destination, not a live test authorization.

```sh
tpcli calls start --session SESSION_ID --request-stdin \
  --idempotency-key deliberate-attempt-1 --json < private-request.json
tpcli commands status COMMAND_ID --json
tpcli calls status CALL_ID --json
printf '%s' 'Ask whether Saturday hours differ.' | tpcli calls instruct CALL_ID --text-stdin --json
printf '%s' '1#' | tpcli calls dtmf CALL_ID --digits-stdin --json
tpcli approvals list --call CALL_ID --json
tpcli approvals resolve APPROVAL_ID --decision deny --action-hash EXACT_HASH --json
tpcli calls stop CALL_ID --json
tpcli calls wait CALL_ID --json
```

The initial call invocation authorizes that attempt; there is no blanket
pre-dial approval flag. **Do not approve a material action without the user's
authorization/delegation for those exact terms.** Approval expiry, changed terms,
owner loss and prior consumption fail closed. No TTY never means consent.

Keep the same idempotency key and identical request after a lost receipt.
Changing payload under a used key is a conflict. A new key represents a deliberate
new attempt, not a retry strategy. A first event can precede its HTTP/CLI receipt;
correlate by command/call IDs, not arrival order.

Human controls use these same commands. The attached `call` view displays events
and pending approval details; Ctrl+C is the visible stop action. Instructions and
approvals may be issued concurrently from another terminal using the displayed
session ID. This initial implementation is an event view, not a full-screen TUI.

## Events, finite polling, and push

```sh
tpcli calls events CALL_ID --after 0 --json
tpcli calls events CALL_ID --after 42 --wait-seconds 10 --json
tpcli calls events CALL_ID --after 42 --follow --json
```

Finite batches contain `events`, `next_cursor`, `has_more`, and `call_state`.
An idle long poll returns an empty batch with the cursor preserved.
`--follow` and `--wait-seconds` are mutually exclusive; long polls are at most
30 seconds. Read further batches while `has_more` is true. Persist the last event
sequence your host actually consumed, not merely the last one it requested.

The broker commits every event to SQLCipher **before** acknowledging the runtime.
It does so with zero viewers. A subscription registers its wake-up channel before
the first database read; replay-to-live delivery cannot miss an ingestion between
the two operations. Subscriber queues and socket writes are bounded. A slow
subscriber is disconnected; it resumes from its own last cursor. Viewers never
renew an owner lease.

Hosts may use the same versioned newline-delimited IPC request as the CLI:

```json
{"schema_version":"1","session_id":"SESSION_ID","op":"events","id":"CALL_ID","after":42,"follow":true}
```

Use the socket from `session.ready`; only the same OS user can connect. Other
operations are `submit`, `command_status`, `call_status`, `approvals`, `ready`,
and `session_close`. A `submit` contains `operation`, `id`, `idempotency_key`,
and `payload`; it returns the same remote command receipt.

**A completed process cannot write more stdout or wake an LLM.** A push-capable
agent host must consume a live IPC/NDJSON subscription and translate events into
its own notifications. Other hosts should issue finite cursor reads between
commands. Merely launching a background viewer is not a notification integration.

## Results, failures, and history

`calls status --offline`, `commands status --offline`, `history list/show`, and
`transcripts show` explicitly read encrypted local data. Offline state includes
timestamps and is not a fresh provider observation. History survives broker exit;
after a broker has closed, finite event snapshots can still be read locally.
Loss of the owner control connection conservatively marks unfinished local calls
as termination-unknown and their transcripts partial; it never fabricates provider
hangup confirmation. Gap boundaries include possible loss after a disconnected
stream, not only known missing sequence ranges. Online status can subsequently
query the runtime's reconciled metadata without renewing the revoked owner.

Exit codes: 0 accepted/successful command, 2 invalid input, 3 auth/configuration or
missing owner, 4 runtime/storage/provider failure, 5 incomplete task or uncertain
termination, 6 stale/conflicting state. For `call` and `calls wait`, 0 requires both
`task_outcome=completed` and `hangup_status=confirmed`. JSON dimensions are
authoritative; connected, generated speech, and accepted hangup are not completion.

`history prune --before YYYY-MM-DD --dry-run` previews old terminal calls.
Omit `--dry-run` only for an intentional deletion. Active/unknown-termination calls
are excluded, and actual pruning refuses while the profile has an active writer.
See [privacy and recovery](privacy.md) before exporting or pruning.

## Local simulation

`provider_mode = "local-fake"` only accepts a loopback HTTP runtime. It never
falls back from Azure and is visibly labeled in every state as `local-fake` /
`simulation`. `TPCLI_FAKE_TOKEN` is an injected local bearer token, not an Azure
credential. Automated tests additionally set `TPCLI_TEST_MODE=1` and inject a
random 32-byte hex `TPCLI_TEST_KEY`. Missing injected keys in this mode fail
without reading Keychain. These keys are never accepted for an Azure profile.

The executable cross-process tests are the reproducible local harness. They
launch actual Rust processes and actual .NET processes rather than mocking HTTP
inside the CLI. Keep the harness isolated from real profiles and production URLs.
