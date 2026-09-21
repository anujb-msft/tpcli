# tpcli: Teams Phone CLI specification

**Version:** 0.3  
**Date:** September 21, 2026  
**Status:** Public implementation specification; live deployment readiness must be established separately.  
**Confirmed product and executable name:** `tpcli`

## 1. Product definition

Build a Rust CLI that delegates a task to an AI phone caller, controls the call through asynchronous commands, and returns its outcome. A C#/.NET runtime integrates **Teams Phone Extensibility (TPE)** with the **Azure Voice Live API**. Humans and AI agents/scripts receive equivalent capabilities. Transcripts and summaries are stored in **encrypted local SQLite**, with **no application audio recording**.

The central design is **short-lived command clients, a session-scoped local broker, and a continuous hosted call runtime**. Individual commands can finish immediately; the broker keeps ingesting results and accepts subsequent commands. Independent subscribers can attach to the event stream. A controlling terminal/agent session owns the broker's call lease; losing that owner terminates the call. This is not an unattended calling service.

The Switchboard phone CLI is the behavioral reference, not a requirement to reuse its code, LiveKit transport, OpenAI endpoint, approval workflow, or transcript file format. [R1]

### Confirmed requirements from the ten questions

| Question | Topic | User decision |
| --- | --- | --- |
| 1 | Purpose | AI-driven calls that complete tasks on the user's behalf. |
| 2 | Demonstrations | All four: external support/IVR; availability and appointments; collecting information from a Teams coworker; customer/partner PSTN updates and responses. |
| 3 | Caller identity | Use the simplest supported identity; a dedicated assistant identity is acceptable. |
| 4 | Integration | A local CLI backed by Teams Phone Extensibility. |
| 5 | Environment | Use a separately configured Teams/Azure development environment; keep deployment identifiers and credentials out of source control. |
| 6 | Lifetime | End the call automatically when the CLI disconnects, including terminal closure or laptop sleep. |
| 7 | Approvals | No blanket explicit approval step; use standard model guardrails to request CLI approval before impactful commitments. |
| 8 | Persistence | Encrypted local SQLite for transcripts and summaries; no saved audio. |
| 9 | Stack | Rust CLI and C#/.NET call runtime. |
| 10 | Consumers | Both interactive humans and AI agents/scripts, with equivalent capabilities. |

Two additional explicit requirements are Azure Voice Live as the conversation engine and a well-defined reconciliation of real-time audio with an asynchronous, command-based CLI.

**Follow-up requirement:** results must remain available to stream and further commands must remain possible regardless of whether the submitting CLI process has finished. The recommended design makes the local broker a first-class component. A finished process cannot continue writing to its stdout; a live subscriber or host connection receives the continuing stream instead.

All architectural choices, numerical limits, and release defaults below are **proposed implementation decisions**, not additional user answers.

## 2. Scope and successful outcomes

### Required v1 scenarios

| Scenario | Desired outcome | Important behavior |
| --- | --- | --- |
| Business support | Reach the relevant person or system and resolve or accurately report the request. | Navigate DTMF menus, tolerate hold music, survive recipient-side transfers, and distinguish partial progress from completion. |
| Availability/appointments | Retrieve options and, when appropriately authorized, confirm a suitable appointment. | Respect dates, time zone, constraints, and selective approval requests; capture confirmation details. |
| Teams coworker | Call a specified coworker, collect requested information, and report back. | Resolve the correct Teams identity and verify the actual Teams/VoIP route and media support before promising this capability. |
| Customer/partner update | Deliver the supplied update and accurately summarize the response. | Identify the assistant truthfully, avoid inventing facts, respect objections, and stop when appropriate. |

The Teams-coworker scenario remains a required product outcome, but its exact integration is a feasibility gate; it must not be silently replaced by a PSTN call.

Common capabilities: call status, streamed transcript text, mid-call instructions, DTMF, selective approval requests, cancellation, bounded duration, final summaries, and machine-readable events.

### Outside v1

- Unattended/detached calls, scheduled dial-outs, bulk campaigns, and automatic redial.
- An inbound AI receptionist, Teams provisioning CLI, or replacement for Teams Admin Center.
- Impersonating an arbitrary Teams user or spoofing caller ID.
- Human microphone participation, audio monitoring, or a terminal softphone.
- Agent-initiated blind transfer that relinquishes the runtime's ability to end the call. Receiving a transfer from the remote business is a separate supported conversation case.
- Automatic CRM/calendar/email writes or unrestricted shell/browser tools. An appointment negotiated verbally is in scope; writing to the user's calendar is not.
- Emergency calling and a production multitenant SaaS offering.

Proposed first-release envelope: one active call per profile, a single configured tenant, English-first conversations, and macOS-first CLI packaging. Keep the Rust core portable; Windows/Linux distribution is not assumed complete.

## 3. Environment prerequisites and feasibility

### Bring your own development environment

The public repository must not contain tenant-specific setup observations,
credentials, private repository content, internal resource names, real contact
details, or local workstation paths.

Developers supply a Teams tenant, a correctly licensed and bound Teams resource
account with a service number, outbound-capable PSTN connectivity, an Azure
Communication Services resource, and an Azure resource with Voice Live access.
Keep concrete settings in ignored local profiles or secure deployment configuration.

Resource acquisition, paid licenses, administrative consent, MFA, number assignment,
and live calls remain explicit operator-controlled actions. Do not change an
existing inbound service as a side effect of developing the CLI.

Build the .NET runtime from the public service contracts and documentation below.
Existing prototypes are not proof that a chosen environment supports the entire
end-to-end architecture.

### Documented platform constraints

**PSTN source identity.** Use a dedicated Teams resource account and its authorized **Teams service number**. TPE's FAQ explicitly excludes ACS-purchased numbers as TPE numbers. Direct ACS PSTN calls may be useful diagnostics, but do not prove the requested TPE route. Never silently fall back to them. [R2, R3]

**Server-side dialing.** Microsoft's outbound guide documents Call Automation with `CreateCallOptions.TeamsAppSource`, set to the resource account identity. Pin a stable .NET SDK version supporting this and the required media APIs after a compile-and-call spike. [R4]

**Outbound funding and licensing.** Verify the resource account license, service-number assignment, server-side access assignment, and outbound-capable connectivity. The current guide requires Pay-As-You-Go Calling Plan arrangements for the documented Calling Plan resource-account outbound scenarios after November 1, 2025; Operator Connect and Direct Routing need their respective checks. A Teams Phone user license alone does not establish readiness. [R4]

**Teams-only calls need separate proof.** TPE's FAQ describes PSTN-only support for its initial GA scope, and its capabilities table is framed around calls with at least one phone-number participant. A separate Call Automation Teams interop document lists direct outbound Teams-user calls, is marked public preview, and lists Desktop/Web rather than mobile-client support. Therefore:

- Keep TPE as the PSTN integration.
- Investigate the explicitly labeled Call Automation Teams-interop path for `teams:` targets.
- Prove direct dialing, source presentation, bidirectional audio, licensing, client support, and tenant permissions together.
- Do not infer full Voice Live support from a table that merely says a Teams user can be called.
- If the combination is unsupported, report a blocked requirement and propose a scope/integration change rather than fabricating support. [R3, R5, R6]

**Voice Live.** Use the Azure Voice Live WebSocket API for the conversation, not a separately orchestrated STT/LLM/TTS chain and not direct OpenAI Realtime. Use the existing Azure resource if its region, kind, model access, and authentication support the required configuration. Endpoint, API version, model, voice, and locale are explicit profile settings. [R7, R8]

The `gpt-realtime` model and a standard Azure voice are starting candidates, not confirmed requirements or proof of access. Pin a documented, tested API version; never silently select a different model or provider.

### Gates before claiming a working v1

| Gate | Required evidence |
| --- | --- |
| G1: TPE readiness | Current read-only confirmation of account binding, access assignment, service number, licensing, and outbound connectivity/funding. |
| G2: Media bridge | A controlled outbound TPE call with bidirectional .NET audio streaming and Azure Voice Live interaction. |
| G3: Teams coworker | A direct call to an authorized test Teams user with working bidirectional Voice Live audio and documented client/tenant constraints. |
| G4: Fail-closed lifetime | Owner loss, laptop/network loss, and worker failure all initiate termination through an independently reachable runtime/watchdog. |
| G5: Local privacy | The shipped Rust binary really encrypts SQLite, including its journal/WAL behavior; no application audio or transcript payload logging. |

Purchases, new resource assignments, consent changes, or live calls require a separately authorized implementation/provisioning task. This spec does not authorize them.

## 4. Architecture

```text
Human terminal / AI-agent host -- owns lifetime --> Rust session broker
                                                       |
Short-lived CLI commands <-- request/receipt via IPC -->|
Independent subscribers <----- replay/live events -----|
                                                       +--> SQLCipher SQLite
                                                       |    single event writer
                                                       |
                                     HTTPS commands + control WebSocket
                                     heartbeat / events / persistence ACKs
                                                       |
                                              ASP.NET Core runtime
                                              per-call state machine
                                                       |
                     +---------------------------------+----------------+
                     |                                 |                |
             ACS Call Automation                Azure Voice Live    Durable control
             TPE / gated Teams interop          WebSocket API       metadata
                     |                                 |                |
                     +---- bidirectional PCM bridge ---+         .NET watchdog
                     |                                                  |
              PSTN / Teams recipient                            termination only
```

Audio stays between ACS, the .NET media bridge, and Voice Live. It does not transit the CLI. No CLI process or command is invoked for each audio frame or speech turn.

There are two distinct long-lived layers: the local broker handles command/event multiplexing and local persistence; the hosted .NET runtime handles actual calling and real-time media. The broker is not another speech engine or a proxy for every audio frame.

### Component responsibilities

| Component | Responsibility |
| --- | --- |
| Rust CLI clients | Parse commands, submit through local IPC, print receipts/results, attach event views, and read permitted local history. Their process lifetimes do not own ongoing calls. |
| Session-scoped Rust broker | Own the remote connection and lease on behalf of the controlling host, multiplex commands/subscribers, ingest events continuously, and persist locally before acknowledging. It is a mode of the CLI, not a session-independent installed daemon. |
| Supervising host/wrapper | Tie broker call ownership to the actual terminal/agent lifetime. A broker process being alive is not sufficient if its controlling host has exited. |
| ASP.NET Core API | Authenticate commands, authorize access by tenant/principal/session, deduplicate submissions, return command receipts, and serve queries/events. |
| .NET call actor | Serialize state changes per call; coordinate telephony, voice, tools, approvals, and termination without blocking the media loop. |
| Telephony adapter | TPE source configuration, create-call, DTMF, provider callbacks, and whole-call hangup. Expose capabilities explicitly. |
| Voice Live adapter | Session configuration, streamed audio, transcript events, interruptions, function calls, and conversation context. |
| Durable control store | Session leases, call/command IDs, provider correlation, deadlines, dispatch status, approval hashes/status, and termination status. No durable transcript, task text, or audio payloads. |
| Independent watchdog | Find expired/revoked owners, deadlines, and failed workers; terminate calls even when the media worker is unavailable. Never create or redial calls. |

Use a small Azure-hosted deployment capable of sustained HTTPS/WebSocket traffic, for example Azure Container Apps with a nonzero minimum runtime replica. A separately scheduled/deployed watchdog and durable control store must not share the media worker's single-process failure boundary.

A laptop/devtunnel arrangement is suitable for development only: if the laptop sleeps, it cannot also be relied upon to enforce remote hangup.

Use the current supported .NET LTS and a pinned Rust toolchain. Prefer the official .NET SDKs where they support the required surfaces; isolate any direct Voice Live WebSocket protocol handling behind one adapter.

Local IPC uses a Unix-domain socket on macOS/Linux and, if Windows packaging is added, an appropriately secured named pipe. Restrict access to the intended OS user and session. Session IDs are routing identifiers, not authentication credentials. Do not introduce an unauthenticated localhost TCP service.

## 5. Reconciling async commands with disconnect-driven hangup

### Definition of "CLI disconnect"

The disconnect that ends a call is loss of its **owning terminal/agent supervision session**, not the normal exit of `calls start`, `calls status`, or a transcript subscriber.

This refines the earlier phrase "CLI disconnect": command lifetime, subscription lifetime, session lifetime, and call lifetime are separate. Only the controlling session's loss is the automatic-hangup trigger.

Three entry points share one broker/backend contract:

1. **Session wrapper, preferred for many commands:** `tpcli session exec -- <shell-or-agent>` runs a broker alongside the child shell/agent, injects its nonsecret session/socket references, and revokes calls when the child or controlling terminal exits. Commands and subscribers can start and finish freely within that scope.
2. **Explicit host integration:** `tpcli session run --json` is a foreground broker kept alive by the operator/agent host. The host must terminate it on session shutdown, or use an explicit monitored control pipe/liveness contract. Do not rely on an orphaned process or a reusable PID alone to detect host ownership.
3. **Single-call shortcut:** `tpcli call ...` creates a temporary broker/ownership scope and attached interactive view for that call. Closing this command ends that scope. Other CLI processes can still control the call using its displayed session/call IDs while the shortcut is running.

A call must have exactly one live owner. Observers and short-lived command clients do not become owners. An existing session does not silently change the single-call shortcut's ownership semantics.

The session wrapper must preserve shell job control: interrupting a foreground viewer or ordinary child command is not equivalent to exiting the shell/agent that owns the session.

The broker ingests and persists events even when **zero viewers are attached**. A human can stop a transcript viewer and later resume it; an agent can finish a command invocation and inspect the resulting events through a different tool invocation or its host's long-lived IPC subscription.

### Proposed lease contract

| Setting or event | Behavior |
| --- | --- |
| Heartbeat cadence | Broker renews every 5 seconds, only while its owning host/scope remains valid. |
| Silent-owner lease | Expires 15 seconds after the last accepted heartbeat, using server time. |
| Watchdog cadence | At most 1 second between checks under normal service availability. |
| Explicit close / detected owner socket closure | Revoke immediately and initiate whole-call termination; do not wait for lease expiry. |
| Kill, laptop sleep, or silent network partition | Lease expiry initiates termination; target at most 16 seconds after the last heartbeat under healthy control-plane conditions. |
| `Ctrl+C` on `call`, `session run`, or the session wrapper | End the ownership scope, send stop, persist final events when possible, and briefly wait for termination confirmation. A second forced exit still leaves watchdog protection. |
| Owning host exits while broker remains momentarily alive | Broker revokes the scope and stops renewing; process survival must not prolong authorization. |
| Broken owner control pipe / unrecoverable broker DB failure | Broker stops renewing and requests termination. Never continue with an unusable supervising host or event store. |
| Observer closes, receives `Ctrl+C`, or breaks its output pipe | Detach only that subscriber; no effect on the call if its owner is still connected. |
| One-shot command exits | No effect on ownership. |
| Owner lost while dialing | Tombstone the call as stopping; cancel where supported and terminate immediately if a late connection arrives. |
| Owner reconnects after revocation | May inspect the outcome, but cannot revive the call. A new attempt requires a new call ID and deliberate invocation. |

There is no unattended `--detach` option in v1 and no silently spawned session-independent daemon. The broker deliberately outlives **commands**, but not its controlling **session**. Automated hosts must keep the ownership scope alive and revoke it when their supervising session ends.

A remote hangup is a distributed operation. The timeout bounds when the runtime **initiates termination**, not a guarantee that a carrier ends the call within that interval. Keep retrying safe termination/reconciliation, expose uncertainty, and alert on orphan risk.

## 6. Call and command protocol

### State is explicit

```text
accepted -> preflight -> dialing -> connected -> ending -> ended
                 \-> failed_before_connect
                                      \-> termination_unknown
```

`termination_unknown` can later become `ended` when definitive provider evidence arrives. It must not be represented as a confirmed hangup merely because local processing stopped.

Connected-call substates include `conversing`, `navigating_ivr`, `waiting_on_remote`, and `awaiting_approval`. Hold/transfer detection is model-driven and may be uncertain; it is not a substitute for provider call state.

Keep these result dimensions separate:

| Field | Values / meaning |
| --- | --- |
| `lifecycle` | Transport/runtime state from the state machine. |
| `task_outcome` | `not_started`, `in_progress`, `completed`, `partial`, `not_completed`, `unknown`. |
| `termination_reason` | For example `task_finished`, `recipient_hangup`, `owner_disconnected`, `user_cancelled`, `deadline`, `provider_error`, `media_error`. |
| `hangup_status` | `not_applicable`, `pending`, `confirmed`, `unknown`. |
| `transcript_status` | `complete`, `partial`, `unknown`; include sequence gaps and interruption metadata. |
| `summary_status` | `pending`, `complete`, `partial`, `unavailable`. |

If create-call submission is ambiguous, `hangup_status` is not `not_applicable`: a remote call may exist.

### Async command semantics

- A mutation returns a `command_id` and, where applicable, a `call_id`. HTTP acceptance or CLI exit code 0 means **accepted**, not connected, spoken, completed, or hung up.
- Command completion/failure is a separate event. `calls wait` provides an explicit blocking bridge for scripts that need a final outcome.
- Submission, observation, and control are independent. A client may issue another command while earlier work or any number of event subscriptions remain active.
- A lost submitting client does not cancel an already accepted operation. Persist its receipt so `commands status` or a same-key retry can recover it without a new dial.
- Record a command receipt and dispatch intent before issuing a telephony side effect.
- Bind idempotency keys to principal, operation, and canonical payload hash. The same key and payload return the same logical receipt; a different payload is a conflict.
- Do not claim exactly-once PSTN dialing. If a create-call response is lost after possible dispatch, reconcile using known correlation/callbacks; do not automatically issue another create.
- Preflight failures known to occur before dispatch are distinct from ambiguous dispatch.
- Duplicate or out-of-order provider callbacks must not regress state, execute tools twice, or produce duplicate local transcript entries.
- Serialize each call's mutable state; use store-backed worker ownership/fencing to prevent competing call actors. Check owner validity again immediately before impactful provider actions.
- Process crashes do not resume a conversation from an incomplete transcript. Recovery is termination and truthful reconciliation, not automatic replay of dialing or spoken commitments.

### Versioned event envelope

Commands and event payloads are defined once in versioned JSON Schema/OpenAPI contracts, with shared fixtures for Rust and .NET.

```json
{
  "schema_version": "1",
  "event_id": "evt_01",
  "session_id": "sess_01",
  "call_id": "call_01",
  "sequence": 42,
  "timestamp": "2026-09-21T15:00:00Z",
  "type": "approval.requested",
  "command_id": null,
  "payload": {
    "approval_id": "apr_01",
    "action": "confirm_appointment",
    "description": "Book the offered appointment at 10:00 AM Pacific on September 24.",
    "action_hash": "sha256:example-placeholder",
    "expires_at": "2026-09-21T15:01:00Z"
  }
}
```

Minimum event families:

- `session.ready`, `session.revoked`, `call.state_changed`.
- `command.accepted`, `command.succeeded`, `command.failed`.
- `transcript.partial`, `transcript.final`, `transcript.interrupted`, `transcript.gap`.
- `approval.requested`, `approval.resolved`, `approval.expired`.
- `call.warning`, `call.failed`, `call.termination_requested`, `call.termination_confirmed`.
- `summary.ready`, `call.result`.

Sequence numbers order a call's emitted events; provider timestamps are retained separately when available. Final segments have stable IDs. Replayed events deduplicate on `(call_id, sequence)`, and partial-segment revisions never append duplicate final text.

The broker persists an event transaction before acknowledging it. Local subscribers replay from encrypted SQLite using a cursor and then transition to live delivery without a replay/live boundary gap. All subscriber streams are read-only with respect to ownership.

Each subscriber has a bounded delivery queue. A slow subscriber is disconnected with a resumable cursor/error rather than blocking audio, command submission, or the broker's persistent event ingestion. Reattachment does not create or restart a call. A command receipt can arrive after its first event; clients correlate by IDs rather than relying on cross-connection arrival order.

Durable control events can be replayed from the hosted runtime after reconnect. Transcript-bearing events have only a bounded in-memory server replay buffer by default; see storage limitations below. The local broker solves submitting-client/subscriber churn, not permanent loss of both local and remote volatile content.

## 7. CLI surface

All interactive actions are wrappers over the same commands and schemas used by scripts. No hidden interactive-only approval or cancellation path.

| Command | Contract |
| --- | --- |
| `auth login` / `auth status` | Authenticate to the runtime/profile; never place a call. |
| `profile show` | Show redacted effective configuration and supported capabilities. |
| `doctor` | Offline checks: configuration, encrypted storage, key access, and supported local versions. |
| `doctor --online` | Explicit connectivity/authorization and resource-readiness checks; no dialing, purchase, or provisioning. Report unknown when required permissions are absent. |
| `call TARGET --task-file FILE` | Interactive supervised call; invocation authorizes the call attempt without another approval flag. |
| `session exec -- COMMAND ...` | Run a shell/agent under a session-scoped broker; inherit its nonsecret session/socket references. Host/terminal exit revokes the scope. |
| `session run` | Explicit foreground broker with control connection, event ingestion, heartbeat, and local persistence. Opening a session alone places no call. |
| `session close SESSION_ID` | Revoke the owner and terminate its active call. |
| `calls start --session ID --request-stdin` | Submit asynchronously under an existing owner; otherwise fail with `SESSION_REQUIRED`. |
| `commands status COMMAND_ID` | Recover the receipt and outcome of a command even after its submitting process exits. |
| `calls status CALL_ID` | Query current runtime state. Explicit `--offline` reads the timestamped local cache. |
| `calls events CALL_ID --after N --follow` | Replay from the local broker and continue live; omit `--follow` for a finite snapshot, or use bounded `--wait-seconds` for long polling. None of these modes owns or renews the call. |
| `calls wait CALL_ID` | Wait for a terminal result; does not keep the call alive independently of its owner. |
| `calls instruct CALL_ID --text-stdin` | Add an authorized mid-call instruction with an applied/failed acknowledgement. |
| `calls dtmf CALL_ID --digits-stdin` | Send validated tones to the permitted participant; useful for manual recovery from an IVR. |
| `calls stop CALL_ID` | Request idempotent whole-call termination. |
| `approvals list --call CALL_ID` | Show pending/previous approval requests. |
| `approvals resolve APPROVAL_ID --decision approve\|deny --action-hash HASH` | Resolve the exact pending action; reject stale, changed, expired, or already-consumed approvals. |
| `history list` / `history show CALL_ID` | Read encrypted local history and summaries. |
| `transcripts show CALL_ID` | Explicitly render/decrypt the local transcript. |
| `history prune --before DATE --dry-run` | Preview targeted local history removal; actual removal is a separate explicit invocation. |

`TARGET` is tagged, not guessed: `pstn:+12025550123` or `teams:<entra-object-id>`. An optional UPN resolver may use permitted directory lookup; ambiguous/unresolved identities fail without dialing. The profile supplies the tenant. The runtime reports the actual route/source identity.

Global `--json` gives one JSON result for one-shot commands and NDJSON for streaming commands. Machine output has no banners or prompts; diagnostics go to stderr. Secrets and sensitive task content must not require process arguments. Use stdin or a protected task file.

The submitting process's stdout ends when it exits. Ongoing results use `calls events --follow`, an interactive attached view, or a supported host IPC event subscription. These consumers may coexist, while independent invocations continue issuing commands through the broker.

For agent hosts that cannot consume a background stream, provide `calls events CALL_ID --after N --wait-seconds 10 --json`. This returns one finite event batch with `events`, `next_cursor`, `has_more`, and `call_state`; an idle timeout returns an explicit empty batch with its cursor preserved. `--follow` and `--wait-seconds` are mutually exclusive. The host may repeat these reads and issue other commands between them without interrupting the call.

Writing to a background stdout stream does not itself notify or wake an LLM. Push integration requires the agent host to consume broker events and deliver them through its own event/notification mechanism; bounded cursor reads are the portable fallback.

### Example: human

```bash
tpcli doctor --profile example-dev
tpcli call pstn:+12025550123 \
  --profile example-dev \
  --task-file ./appointment-brief.txt \
  --max-duration-seconds 600
```

The interactive view shows source identity, destination, state, elapsed/remaining time, transcript, and any pending approval. It provides a clearly visible stop action. Use truthful AI-assistant identification, not a claim that the human is personally on the line.

### Example: asynchronous agent or script

```bash
# Option A: wrap an interactive shell; all commands in it inherit the session.
tpcli session exec -- zsh

# Option B: an existing agent host explicitly supervises this foreground broker.
tpcli session run --profile example-dev --json
```

These are alternative ownership entry points, not two processes to launch for the same call. `session exec` can also wrap an agent executable. With `session run`, the first event supplies `session_id`; with the wrapper, commands inherit it.

Subsequent independent invocations use the owning session:

```bash
tpcli calls start --session SESSION_ID \
  --idempotency-key UNIQUE_KEY --request-stdin --json < request.json
tpcli commands status COMMAND_ID --json
tpcli calls status CALL_ID --json
tpcli calls events CALL_ID --after 0 --follow --json
tpcli approvals resolve APPROVAL_ID \
  --decision approve --action-hash ACTION_HASH --json
tpcli calls stop CALL_ID --json
tpcli calls wait CALL_ID --json
```

Run the event viewer and subsequent control commands above in separate invocations/terminals or through the agent host's concurrent tool interface; they do not depend on the starter remaining open. Killing only the event viewer does not hang up. Closing the ownership scope does.

No approval should be inferred from the example: an automation host sends an approval only with user authorization/delegation for that exact action.

Proposed exit categories: `0` command success/acceptance; `2` invalid input; `3` authentication/configuration; `4` provider/runtime failure; `5` task incomplete or termination uncertain; `6` stale/conflicting state. A blocking `call`/`calls wait` returns 0 only for a completed task with confirmed call termination. Structured fields remain authoritative.

## 8. Real-time conversation and media

The call actor maintains one Voice Live session per call. Establish/configure that session before dialing where practical, so the recipient is not waiting on a cold conversation service after answering.

Configure ACS bidirectional audio streaming and validate the initial audio metadata. Prefer PCM16 mono at 24 kHz on both sides; ACS documents 16 kHz and 24 kHz options, and Voice Live supports matching input rates. Negotiate explicitly rather than assuming the defaults match. [R8, R9]

Required media behavior:

- Route recipient audio to Voice Live and generated audio back to ACS continuously. Exclude the application's own output from the incoming mix.
- Bound queues; proposed maximum queued audio is 2 seconds per direction. Do not accumulate or replay stale speech after an outage.
- On recipient interruption, cancel obsolete model output and flush queued ACS output using the supported stop-audio mechanism. Update conversation context and transcript interruption metadata.
- Distinguish generated text from audio actually sent and from estimated playback. Do not claim the recipient heard an entire generated sentence without evidence.
- Keep DTMF/tool/control work off the media processing loop.
- Disable unsolicited responses during detected hold music where possible, but keep listening for a person/menu. A transfer announcement is not task completion.
- Handle busy/no-answer/voicemail/recipient hangup distinctly. Do not leave detailed voicemail by default; the brief must permit the message.
- Model/voice/provider failure must produce an explicit error and bounded termination behavior, not a silent switch to another provider.
- The hard deadline includes dialing, IVR, hold time, approvals, and model/tool waits. Proposed default 600 seconds, configurable from 30 to 3600 seconds.

The model's call tools are narrow: request approval, send DTMF, report task result, and end the call. Mid-call user instructions enter as authorized operator context, not as simulated recipient speech.

No agent-initiated blind transfer in v1: Microsoft's general Call Automation guidance notes that a 1:1 transfer can remove application control, conflicting with guaranteed owner-driven termination initiation. [R10]

## 9. Selective approvals and guardrails

There is **no mandatory plan/approve/apply cycle before dialing**. Running the call command authorizes that specified call attempt and its ordinary provider charges.

Use the model's standard safety behavior and instructions to recognize impactful commitments, such as booking/cancelling an appointment, accepting a material charge, or sharing sensitive information. Do not invent a second general-purpose policy engine as an MVP requirement.

When the model requests approval:

1. Emit `approval.requested` with an immutable description, structured material terms, action hash, expiration, and call/conversation version.
2. Keep media and supervision alive. Briefly explain the wait to the recipient and block the pending commitment, not the network event loop.
3. Show the same request to an interactive user or emit it to the automation consumer. Noninteractive mode never auto-approves because a TTY is absent.
4. Accept a decision only from an authorized operator for that exact action. Changed terms invalidate the approval and require a new request.
5. Resume the supported Voice Live function-call continuation with the decision. An approval is consumed once.

Proposed approval timeout: 60 seconds, capped by the remaining call deadline. Expiry is a denial of the pending action, never implicit consent. The agent may continue collecting information or end politely if no useful authorized path remains.

Owner loss invalidates pending approvals and triggers hangup. Stop commands preempt approval processing.

**Safety distinction:** enforcing a pending approval's hash, expiry, and one-time use is deterministic. Whether the model recognizes every impactful verbal commitment is model-driven; standard guardrails do not guarantee perfect detection. Test those behaviors and expose this limitation rather than presenting the CLI as a compliance guarantee.

The recipient's speech is untrusted input. It cannot authorize itself, change system safeguards, access credentials, redirect control endpoints, or turn an information request into broader authority.

Proposed disclosure default: identify as an AI assistant calling on behalf of the user, provide appropriate transcription notice, and stop on objection. Deployment must establish the applicable legal basis for calls/transcription; absence of audio recording does not eliminate those obligations.

## 10. Encrypted local persistence and privacy

Use **SQLCipher-backed SQLite** through a Rust integration such as `rusqlite` with the appropriate SQLCipher build. Plain SQLite with a password field or OS file permissions alone does not meet the requirement.

### Local data model

| Table | Contents |
| --- | --- |
| `calls` | IDs, profile/source/destination, brief, timestamps, lifecycle, task outcome, termination reason/status, and completeness. |
| `events` | Deduplicated event envelopes and payloads keyed by call ID and sequence; no audio frames. |
| `transcript_segments` | Stable segment ID, speaker/participant, text, timestamps, final/revision flags, interruption and delivery-confidence metadata. |
| `summaries` | Summary, extracted facts, commitments, outstanding items, source segment/event references, and completeness. |
| `commands` | Local command receipts, idempotency key/hash, status, and relevant result. |
| `approvals` | Exact proposed action, hash, decision, principal, timestamps, and expiration. |
| `schema_migrations` | Versioned database migrations. |

Generate a random per-profile encryption key and store it in the OS credential store, initially macOS Keychain. Do not put the key in TOML, command arguments, logs, source, or a plaintext file beside the database. Define an explicit encrypted backup/recovery-key workflow; losing the only key makes history unrecoverable.

Requirements:

- Verify SQLCipher support at startup; fail closed if the key is unavailable or encryption is not active.
- Restrict local state-directory/file access; ensure WAL/journal and backup paths preserve encryption and do not write plaintext temporary content.
- The active broker is the single event writer and commits atomically before acknowledging. Commands route active-call writes through it; local history readers use bounded database busy timeouts and explicit errors.
- Exports and `transcripts show --json` deliberately produce plaintext for the requesting user/process; never invoke them automatically for diagnostics.
- No silent retention expiration in v1. Keep local history until explicit pruning and document the resulting privacy/storage tradeoff.
- Never send transcript text, prompts, approval details, or credentials to ordinary telemetry. Opt-in diagnostics must still exclude raw audio and secrets.

### Server retention and transcript completeness

The durable Azure store holds minimal control/reconciliation metadata, not transcript text, brief text, or audio. Proposed metadata retention is 72 hours after final termination, subject to organizational policy.

The runtime keeps task/conversation content in memory while active and uses a bounded transcript-event replay buffer: proposed 4 MiB per call, at most 5 minutes after termination. Acknowledged content may be discarded sooner. No persistent cloud transcript spool is included by default.

This has an explicit consequence: a runtime crash or prolonged client loss can leave the encrypted local transcript incomplete. Detect/report sequence gaps; never fabricate missing content. A server restart cannot regenerate missing transcript text from metadata.

Produce the final summary from the available transcript and structured task result through the Voice Live conversation/text capabilities, then persist it locally. Do not delay hangup to finish summarization. If a summary cannot be delivered or generated, record `partial`/`unavailable`; a later explicit summarization operation can use the locally available text and must preserve its completeness warning. Verify the selected Voice Live model/API's text/tool-output behavior in the integration spike.

If lossless recovery across client and runtime failure becomes a requirement, separately approve encrypted transient cloud content storage and its retention. Do not silently add it.

No application component records or intentionally persists raw audio. Cloud services still process audio/text under their data-handling terms; this is neither zero provider retention nor end-to-end PSTN encryption.

## 11. Authentication, operational boundaries, and failure handling

- CLI-to-runtime: Microsoft Entra user authentication, with runtime validation of tenant, audience, principal, and operation permissions. Device/browser sign-in and MFA stay with the user.
- The broker holds the authenticated runtime connection; local clients authenticate through protected OS IPC. Enforce both local session access and remote user/tenant authorization.
- Runtime-to-Azure: prefer managed identity and the least roles needed for ACS and Voice Live. Administrative tenant/Teams setup credentials do not become call-runtime credentials.
- Keep sensitive runtime credentials in the platform secret store if a required SDK surface cannot yet use managed identity. No key in a WebSocket URL.
- Validate ACS callbacks/media connections using the supported service authentication mechanism, expected call correlation, and replay protections. A public endpoint or call ID alone is not authentication.
- Before shipping, resolve the exact ACS callback and media-WebSocket validation mechanisms in the chosen SDK/API. Fail closed if the required callback or media authentication cannot be verified.
- Enforce destination syntax, capability support, configured source identity, duration, concurrency, and supervision independently of the model.
- Treat provider IDs as opaque variable-length values; do not parse them into routing assumptions.
- Do not assume all Teams user policies apply identically to TPE/API-initiated calls. Document and verify the relevant resource-account, directory, and tenant restrictions. [R3, R5]

| Failure | Required response |
| --- | --- |
| Missing service number/route/permissions | Fail before dialing with actionable diagnostics; no ACS-number fallback. |
| Voice Live cannot initialize | Fail preflight before dialing where possible; expose the actual access/model/configuration problem. |
| Create-call times out after possible dispatch | Mark ambiguous, reconcile, and never automatically redial. |
| Media disconnect/overflow | Stop obsolete audio, report the failure, and terminate if safe bounded recovery cannot preserve the conversation. |
| Owner or local encrypted-store failure | Revoke supervision and initiate hangup. |
| Worker restart | Watchdog terminates/reconciles active calls; no silent conversational resume. |
| Hangup request accepted, callback missing | Keep `pending`/`unknown`; query/retry only safe termination operations. Local closure is not proof of remote hangup. |
| Recipient ends call | Finalize based on actual progress; a connected call is not automatically a completed task. |
| Transcript/summary unavailable | Preserve the call outcome and explicit content-completeness status. |

Use correlation IDs, timings, counters, state transitions, and sanitized error codes for observability. No success-shaped fallbacks.

## 12. Acceptance criteria and delivery

All targets below are proposed engineering acceptance criteria, not measured platform guarantees.

### Core acceptance matrix

| ID | Acceptance criterion |
| --- | --- |
| A1 | Each of the four required scenarios succeeds in three consecutive controlled trials with authorized test recipients and documented expected results. A blocked Teams scenario means the full requested v1 is not complete. |
| A2 | Every PSTN demonstration uses the verified TPE resource-account route and source number, never an ACS fallback number. |
| A3 | Interactive and headless consumers can start, observe, instruct, approve/deny, stop, and retrieve equivalent structured outcomes, including concurrent control while another client follows events. |
| A4 | A one-shot start command returns a receipt without blocking on the conversation; exiting it leaves the broker-owned call intact. With zero subscribers, events still persist; a later subscriber replays and follows without gaps or duplicate final text. |
| A5 | Explicit owner closure initiates termination within 2 seconds under normal service availability; silent loss meets the 15-second lease plus 1-second watchdog target. Exercise process kill, sleep/network loss, and dialing-time loss. |
| A6 | Worker failure is detected independently; termination is attempted without the original worker or laptop. Provider uncertainty remains visible. |
| A7 | Retrying an ambiguous create with the same idempotency key does not create a second logical call; ambiguous provider dispatch is not automatically redialed. |
| A8 | Duplicate/reordered callbacks and event replay do not regress call state, duplicate final transcript segments, or execute a commitment twice. |
| A9 | Pending approvals cannot execute after denial, expiry, changed terms, owner loss, or prior consumption. Model-behavior tests cover examples of impactful commitments and prompt injection. |
| A10 | Standard SQLite cannot read the encrypted database; the correct key restores it. Wrong/missing key, unavailable SQLCipher, disk-full, WAL, backup, and export behavior are covered. |
| A11 | No application audio files or raw media payload logs are created. Ordinary logs contain no transcript text, task text, credentials, or approval details. |
| A12 | Interrupted speech, transcript gaps, unavailable summaries, partial task success, and unconfirmed hangup remain distinguishable in both UI and JSON. |
| A13 | Whole-call deadline includes holds and approval waits; no path renews it implicitly or reconnects/redials after expiry. |
| A14 | Offline `doctor` performs no network operations; online checks place no call and change no resource. Any billable Voice Live probe is separately explicit. |
| A15 | Subscriber exit/backpressure does not stop a healthy call or block other commands. Owner-host exit does stop it even if the broker process has not yet exited. |
| A16 | Retrieve a command outcome after its original process exits, including a command whose first event precedes its receipt. Replay/live subscription handoff is race-tested. Finite cursor reads, idle long-poll timeouts, and push subscriptions deliver equivalent ordered events. |

### Performance targets

- Async start receipt: p95 at most 1 second, excluding initial authentication; dialing/model readiness proceeds as explicit subsequent state.
- Conversational response onset: target p95 at most 2 seconds from end of recipient speech to audible response in a defined controlled test, excluding approval/external-tool waits.
- Interruption: issue the outgoing-audio stop within 150 ms of receiving the relevant Voice Live interruption event; separately measure recipient-perceived stop latency.
- No unbounded audio, transcript replay, or pending-command queues. Sustained overflow becomes a surfaced failure, not growing latency.

Specify test location, carrier/Teams client, region, model, warm/cold state, and sampling method alongside measurements. Internal server timings are not substitutes for audible end-to-end latency.

### Suggested implementation layout

```text
crates/tpcli/                 Rust command clients, views, session wrapper
crates/tpcli-broker/          session IPC, supervision, event ingestion, SQLCipher
runtime/Tpcli.Runtime/       ASP.NET Core API, call actors, media bridge
runtime/Tpcli.Watchdog/      independent termination/reconciliation worker
contracts/                   OpenAPI, JSON Schema, shared event fixtures
tests/                       contract, lifecycle, media, and controlled E2E tests
infra/                       deployment definitions and nonsecret profile templates
docs/                        setup, operation, failure semantics, and privacy
```

Implementation repository: `anujb-msft/tpcli`. Developer-specific setup repositories and local primary workspaces are not part of this public project.

### Delivery sequence

| Milestone | Deliverable / exit condition |
| --- | --- |
| M0: Feasibility | Resolve G1-G3 using read-only readiness checks followed by separately authorized controlled calls. Pin SDK/API versions and document the precise Teams route. No full-v1 promise before the Teams-only media path is proven. |
| M1: Contract and lifetime | Rust/.NET shared schemas; local broker/IPC and host ownership; fake-provider call state machine; async receipts; independent replay/live subscribers; owner lease; watchdog; SQLCipher persistence. |
| M2: Real calling | TPE outbound plus Voice Live bidirectional .NET bridge, cancellation, DTMF, interruptions, transcript ingestion, and truthful final state. |
| M3: Task interaction | Mid-call instructions, selective approvals, IVR/hold/recipient-transfer behavior, structured outcomes, and summaries. |
| M4: Release hardening | Human/agent parity, failure/abuse cases, packaging, operating documentation, and the full acceptance matrix including Teams coworker calls. |

A PSTN-only vertical slice is a useful milestone, but does not satisfy the user's request for all four scenarios.

### Principal unresolved implementation facts

1. Whether the chosen deployment satisfies the Teams service-number, licensing, and outbound-funding prerequisites.
2. The supported direct Teams-user route and bidirectional Voice Live media combination, including preview/client limitations.
3. The existing AI resource's actual Voice Live access, region/model compatibility, and required identity roles.
4. Exact SDK versions, callback/media authentication, hosted runtime/watchdog deployment, and encryption packaging.

These are mandatory implementation/readiness gates. Publishing this repository or passing local tests does not establish a working live calling deployment. Calls and provisioning require explicit operator authorization.

## References

Public documentation read on September 21, 2026. The current direct Microsoft documentation takes precedence over stale search summaries.

- **[R1]** Switchboard phone documentation and CLI source: https://github.com/jessfraz/switchboard/blob/main/docs/phone.md and https://github.com/jessfraz/switchboard/tree/main/crates/phone-cli
- **[R2]** TPE overview: https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-overview
- **[R3]** TPE FAQ: https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-faq
- **[R4]** Server-initiated outbound TPE calls and prerequisites: https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-server-outbound-call
- **[R5]** TPE capability matrix: https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-capabilities
- **[R6]** Call Automation Teams interop: https://learn.microsoft.com/en-us/azure/communication-services/concepts/call-automation/call-automation-teams-interop
- **[R7]** Azure Voice Live overview: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live
- **[R8]** Voice Live protocol/configuration guide: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to
- **[R9]** ACS bidirectional audio streaming: https://learn.microsoft.com/en-us/azure/communication-services/how-tos/call-automation/audio-streaming-quickstart
- **[R10]** Call Automation actions and architecture: https://learn.microsoft.com/en-us/azure/communication-services/concepts/call-automation/call-automation
