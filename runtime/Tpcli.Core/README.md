# Runtime and independent watchdog

These projects implement the protocol in `contracts/v1` on .NET 10. Local tests
exercise a real SQLite control store, ASP.NET HTTP/WebSockets, and a separately
executed watchdog. They neither contact Azure nor place telephone calls.

## Local simulation

From the repository root, build the solution:

```sh
dotnet build runtime/Tpcli.slnx
dotnet test runtime/Tpcli.Runtime.Tests/Tpcli.Runtime.Tests.csproj --no-build
```

Inject a fresh fake authentication token into the environment; never save it in
a profile, command argument, source file, or log. For example, in a supervised
development shell:

```sh
mkdir -p .local
export Tpcli__Mode=local-fake
export Tpcli__FakeToken="$(openssl rand -hex 32)"
export Tpcli__Store__Provider=sqlite
export Tpcli__Store__ConnectionString="Data Source=$PWD/.local/control.db"
export ASPNETCORE_URLS=http://127.0.0.1:5080
dotnet run --project runtime/Tpcli.Runtime --no-build
```

Send the injected bearer credential in the Authorization header on every
`/v1` request, the owning WebSocket handshake, and fake signal injection.
No credential is returned or
printed. Fake HTTP refuses non-loopback listeners, non-loopback peers/Host
headers, and HTTPS configuration. Its source/mode is `local-fake`, its route is
`simulation`, and it never proves Azure readiness.

Run the watchdog **as a separate process**, using the same mode and absolute
control-store connection string. It needs no fake HTTP token:

```sh
dotnet run --project runtime/Tpcli.Watchdog --no-build
# Or a bounded, single sweep that waits for its termination attempts:
dotnet run --project runtime/Tpcli.Watchdog --no-build -- --once
```

The long-running watchdog checks at most one second apart. It only terminates:
it does not prepare providers, dial, reconnect Voice Live, or resume an actor.
For local process-kill tests, leave this process alive when stopping/killing
the runtime. Do not treat a watchdog on a sleeping laptop as hosted protection.

### Configuration

All keys below use the `Tpcli__` environment prefix. `Store__ConnectionString`
is required; no implicit in-memory database is used.

| Key | Default / meaning |
| --- | --- |
| `Mode` | Required: `local-fake` or `azure` |
| `Profile` | `default`; active-call exclusion is tenant/principal/profile scoped |
| `FakeToken` | Required in the HTTP fake runtime, 32–4096 characters |
| `Store__Provider` | `sqlite` locally; **`postgres` required in Azure mode** |
| `Store__ConnectionString` | SQLite file or Npgsql connection string, injected securely |
| `ActorQueueCapacity` | 64, bounded between 4 and 1024 |
| `SubscriberQueueCapacity` | 128, bounded between 4 and 1024 |
| `MaxResidentCalls` | 1024, bounded between 1 and 10000 |
| `Fake__ConnectDelayMilliseconds` | 100, 0–60000 |
| `Fake__PrepareDelayMilliseconds` | 0, 0–60000 |
| `Fake__AutoConnect` | `true`; false leaves the simulated call dialing |
| `Fake__EmitTranscript` | `true`; one explicitly simulated greeting |
| `Fake__CreateAmbiguous` | `false`; create simulation state, then lose the create response |
| `Fake__HangupUnknown` | `false`; termination remains unknown even across watchdog processes |
| `Fake__FailPreflight` | `false`; known failure before dispatch |
| `Fake__FailDialBeforeDispatch` | `false`; known non-dispatched create failure |
| `Fake__TeamsEnabled` | `false`; gates simulated Teams UUID targets, never a PSTN fallback |
| `Fake__ApprovalTimeoutMilliseconds` | 60000; fake-only short approval expiry |
| `Fake__TestDeadlineMilliseconds` | Unset; explicit fake-only effective TTL override |

Public `max_duration_seconds` still validates 30–3600 even with the fake TTL
override. Leases are always 15 seconds with five-second owner heartbeats;
worker leases are five seconds, refreshed by the runtime. Heartbeats never
extend a call deadline. Fake handles are stable `fake:<call_id>`.

To inject a provider event, `POST /test/calls/{call_id}/signals` with
`{ "type": "...", "payload": { ... }, "provider_event_id": "stable-event-id" }`.
This endpoint exists only in fake mode and checks both authentication and call
ownership. Supported signal types are `connected`, `disconnected`,
`transcript.partial`, `transcript.final`, `transcript.interrupted`,
`transcript.gap`, `tool.requested`, `warning`, and `failure`. Connection signals
can include `connection_id` and `server_call_id`. Transcript objects follow the
shared schema. Warnings/failures expose allowlisted codes, not provider text.

Tool signals use
`{ "tool_call_id": "...", "name": "...", "arguments": { ... } }`:

- `request_approval`: `action`, `description`, and object `material_terms`.
- `send_dtmf`: `digits`, restricted to 1–32 of `0-9 * #`.
- `report_result`: `outcome` (`completed`, `partial`, `not_completed`, or
  `unknown`) and optional nonblank `summary`. `task_outcome` is also accepted.
  Optional string arrays `facts`, `commitments`, `outstanding_items`, and
  `source_references` accompany the summary in the bounded volatile
  `summary.ready` event only, never the durable control store.
- `end_call`: ends the transport; it never invents a completed task result.
  Optional `reason` preserves safe codes `task_finished`, `recipient_objection`,
  `voicemail_not_allowed`, or `no_authorized_path`. Objection/disallowed-voicemail
  aliases are normalized; unknown model text maps to `agent_ended`, never raw
  text or an operator/owner-loss claim.

Unknown tools cannot execute actions. Tool IDs and canonical argument hashes
are deduplicated durably. Pending approval hashes bind canonical action,
material terms, and conversation version; descriptions and terms stay only
in the bounded content buffer. Instructions/new requests invalidate stale
approvals. Expiry denies; it never approves. The authenticated principal is
recorded on consumption. Stop does not wait for a function continuation.

## Wire behavior

Opening `POST /v1/sessions` returns `201 SessionInfo` but places no call.
Attach the one authenticated owner WebSocket at
`/v1/sessions/{session_id}/control` before submitting a start command.
`session.ready` is a full `EventEnvelope`, with `call_id: null`, sequence zero,
and payload `{ "session": <SessionInfo>, "heartbeat_seconds": 5,
"lease_seconds": 15 }`. Session revocation uses the same shape.

The socket accepts only `{"type":"heartbeat"}` and
`{"type":"ack","call_id":"...","sequence":N}`. An ACK is the broker's assertion
that its SQLCipher event transaction has **already committed**; the runtime
does not persist transcripts on the broker's behalf. Closing/breaking the
owner socket revokes immediately; neither a second socket nor a reconnect
can take over or revive that session.

Commands return `202 CommandReceipt` after durable acceptance; this is never
evidence of connection, spoken instructions, task success, or hangup.
State-event and result payloads are `{ "state": <CallState> }`; command-event
payloads are `{ "receipt": <CommandReceipt> }`. Receipt results contain only
safe provider mode/route metadata. Polling, observers, and command completion
never renew ownership.

`GET /v1/calls/{id}/events?after=N&wait_seconds=0` returns at most 128 events,
ordered by durable per-call sequence. Idle long polls preserve the cursor.
The WebSocket uses the same cursor reads, so replay/live handoff cannot skip
a committed sequence. Bounded slow delivery disconnects without blocking a
producer. Lost/acknowledged/evicted volatile rows replay as `transcript.gap`
with `from_sequence`, `to_sequence`, and `volatile_content_unavailable`.
No missing content is fabricated.

Terminal/uncertain-result snapshots advertise the sequence of the transaction's final
`call.result`, including preceding command/approval resolution events. This
cursor can exceed the snapshot envelope's own sequence: a waiting broker must
drain through it before treating the terminal result as fully persisted.
`termination_unknown` has a result for waiting clients but still occupies the
active-call slot and remains eligible for safe watchdog termination retries.

All queries and controls check tenant/principal/session ownership. A revoked
owner may inspect results and recover an existing idempotent receipt but
cannot create a new call under that session. A `termination_unknown` call
still occupies the active-call slot.

## Durable control plane and privacy

SQLite is an actual local WAL/FULL-synchronous metadata store. It is **not**
the broker's encrypted history database and **must not** be put on Azure
Files or another shared filesystem for hosted/distributed coordination.

Azure mode requires Npgsql/PostgreSQL with certificate-verified TLS. Short
database advisory-lock transactions serialize metadata changes across
workers and watchdogs; no provider I/O occurs while holding the lock.
Ownership/fence/deadline are checked immediately before provider actions.
Receipt and dispatch intent precede create; ambiguous creates are never
redialed. A returned/late provider handle is retained even after owner loss,
then terminated. Recovery is termination, not conversation reconstruction.

The runtime implements the Azure adapter's `IAzureCallCorrelation` boundary
using `DurableCallCorrelation`, without changing the shared provider contracts.
It atomically binds opaque connection/server-call IDs only to existing Azure
calls with durable dispatch intent, rejects replacement/cross-call bindings,
and preserves matches after worker restart or owner revocation. Binding never
revives an ended call. The watchdog does not expose callback ingress.

`DurableMediaGrants` implements the separate `IAzureMediaGrantStore` boundary.
Media authority is stricter than callback reconciliation: it requires the
current worker/fence, owner connection generation, live owner and worker leases,
matching call/session/tenant/principal, a non-ending Azure call and its hard
deadline. One digest-only grant per call is issued before dispatch; atomic
consumption additionally requires durable dispatch intent. Expiry, owner loss,
worker loss, fencing or termination revoke unused grants, with no reissue or
reconnection. Additive metadata tables support existing control databases.

The v0.4 `IAzureMediaGrantStore` adapter uses `DurableMediaGrants` and the same
transaction/fencing mechanism. `owner_generations` records ownership changes;
`media_grants` stores only the SHA-256 digest, call/session/tenant/principal,
worker/fence/owner generation, origin/path audience, and issue/expiry/consume/
revoke timestamps. The raw disposable bearer stays in the Azure adapter's RAM,
never the control store. A grant is issued once per call, expires within 120
seconds and the hard deadline, and has exactly one atomic consumer even across
independent store clients. Expired, consumed, or revoked grants cannot be
reissued to reconnect or resume a call.

The worker identity comes from the server's `CallRuntime.WorkerId`, not an HTTP
argument. Each operation rechecks the current live owner connection, ownership
generation, worker lease/fence, call ownership, and non-ending lifecycle.
Consumption also requires durable dispatch intent. The registered Azure
adapter supplies its configured `Azure__CallAutomation__PublicBaseUrl` audience
and exact `/azure/media/{call_id}` path; consumption must match the issued
audience. A media request cannot establish a provider ID binding. Independent
watchdog/control transactions revoke unused grants on expiry or authority loss.

The store allowlists state/identity/route/deadline/lease/fence metadata,
provider handles, command and payload hashes, approval hashes/status/actor,
deduplication hashes, and safe control events. It never stores brief,
instruction, transcript, approval terms/description, summary, audio, or bearer
token content. Content event rows contain only metadata and a null payload.
The per-call volatile buffer is capped at 4 MiB and expires five minutes after
completion; ACKs can trim it earlier. Availability/completeness fields remain
explicit after loss. Unconfirmed termination is not converted to success.

`termination_attempts` durably records call ID, `provider_mode`, reason,
`started_ms`, `completed_ms`, status, executor, and handle. The fake terminator
returns confirmed only for explicit simulation state in `fake_calls`; a
missing handle/state or provider error remains unknown. These tables allow
external process-kill tests to inspect independent watchdog behavior.
`fake_operations` records only instruction/DTMF/tool counters, so tests can
prove delivery and deduplication without persisting the corresponding content.
Confirmed terminal metadata is pruned after 72 hours; uncertain calls are
not silently removed.

## Production authentication/transport

In Azure mode also configure `Tpcli__TenantId` (tenant GUID),
`Tpcli__Audience`, and optionally `Tpcli__Scope` (`tpcli.control`).
JWT validation requires the exact tenant v2 issuer, audience, `tid`, valid
`oid`, expiry/signature, and required delegated scope. Fake tokens are
forbidden. Configure the Azure adapter separately as documented in its
project.

The runtime requires HTTPS unless explicitly behind allowlisted forwarding
peers configured as `Tpcli__TrustedProxies__0`, etc. Only those exact IPs can
supply a single-hop forwarded scheme/address, and application requests still
must resolve to HTTPS. No arbitrary forwarded headers/networks are trusted.
`/healthz` is unauthenticated process liveness only, not provider readiness.
HTTP bodies are capped at 64 KiB and task/instruction UTF-8 content at 16 KiB.
The Azure startup privacy filter removes media capability queries (including
malformed and misdirected queries) from `QueryString`, parsed query collections,
and `RawTarget` before runtime middleware. It scrubs URL-related activity tags
and sets `Referrer-Policy: no-referrer`. Host postconfiguration prevents
provider-specific verbose logging rules from re-enabling ASP.NET/Kestrel,
HTTP/WebSocket transport, Azure SDK, or telemetry logger output. Error responses
contain only sanitized codes; safe runtime diagnostics remain available.

Application log filtering is **not** proof that a proxy, access-log collector,
startup hook, automatic instrumenter, or external activity exporter is safe.
No unverified URL-collecting instrumentation may be enabled. The adapter's
expiring `Azure__Media__UrlLoggingVerified` /
`Azure__Media__UrlLoggingValidUntilUtc` evidence remains a separate live-readiness
gate, not an insecure logging bypass. Database and provider credentials belong
in deployment secrets, never public profiles.

Local tests are not G1–G3 deployment evidence. PostgreSQL schema/coordination
is implemented, but exercising a hosted database, Azure authentication, live
callbacks/media, licensing, or the Teams route requires separately authorized
deployment tests.
The Azure adapter requires the actual durable grant store and current media-URL
logging evidence. Missing prerequisites remain explicit preflight failures;
implementing application capabilities does not establish hosted readiness.
When PSTN capability is blocked, start requests expose the first allowlisted
blocked prerequisite code (configuration, source/evidence, media, or callback
correlation) rather than masking it as generic unsupported PSTN. Provider
diagnostic messages are not echoed, and no provider preparation occurs.
