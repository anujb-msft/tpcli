# Azure platform adapter: evidence, configuration, and release gates

**Evidence accessed: September 21, 2026.** This is a public-source implementation,
not a report about a particular tenant. No cloud calls, Voice Live sessions,
provisioning, purchases, administrative changes, or credential acquisition were
used to validate this implementation.

## Current status: deliberately not live-ready

The real .NET SDK adapters and offline tests compile. **The shipped Azure factory
does not dial.** `UnverifiedMediaAuthentication` blocks preparation with
`MEDIA_AUTH_UNVERIFIED`, before obtaining credentials or opening the potentially
billable Voice Live session. No configuration switch bypasses this gate.

Microsoft documents JWT authentication for Call Automation **webhook callbacks**.
The audio-streaming guide documents the WebSocket's
`x-ms-call-connection-id` and `x-ms-call-correlation-id` headers, but does not
establish a service-authenticated WebSocket handshake. Correlation headers are
not credentials. Neither the pinned `MediaStreamingOptions` surface nor the
cited streaming guide establishes a supported custom authorization-header
configuration. The webhook JWT mechanism must not simply be assumed to apply to
media. This is an unresolved evidence/deployment gate, **not a claim that Azure
can never support authenticated streaming**. [M6, M7, M10]

The media route rejects rather than accepting an anonymous connection. There is
no secret URL, query-string token, IP-only authorization, shared callback-token
shortcut, or success stub in this initial checkpoint. The approved
[v0.4 specification](SPEC.md#approved-disposable-media-capability) now permits
disposable application media grants; their integration is active work, not part
of this checkpoint's readiness claim. Releasing the gate requires reviewed,
scoped one-use enforcement, call/owner/worker binding, expiry/replay checks,
deployed URL-log controls, and an explicitly authorized end-to-end test. Such a
grant is application capability authentication, not Microsoft-issued identity.

Direct `teams:` targets return `CAPABILITY_UNSUPPORTED`, before voice creation
or dialing, and are never rewritten as PSTN destinations.

## Documented facts versus compiled facts versus live evidence

| Area | Primary Microsoft evidence | Compiled/offline-tested implementation | Still unverified live |
| --- | --- | --- | --- |
| .NET | .NET 10 is current LTS, supported through November 14, 2028; latest servicing patch listed September 8 is 10.0.12. [M1] | `net10.0`, SDK 10.0.400; local test host reported runtime 10.0.11. | Production must use a current serviced runtime. Compilation on the installed patch is not evidence of production patch compliance. |
| TPE source | Current outbound guide specifies resource-account OID through `MicrosoftTeamsAppIdentifier`. [M2] | `Azure.Communication.CallAutomation` **1.6.0**, API **2026-03-12**: `CreateCallOptions.TeamsAppSource` is actually `MicrosoftTeamsAppIdentifier`. [M10] | Account binding, actual source presentation, number assignment, tenant restrictions. |
| PSTN identity | TPE uses Teams service numbers assigned to resource accounts; ACS-purchased numbers are not TPE numbers. [M3] | `CallInvite` has a PSTN recipient and **null** ACS caller-number argument, plus `TeamsAppSource`. The configured Teams number is required; no alternate number/provider exists. | The configured number must be the number really assigned to the selected resource account. It is not an arbitrary caller-ID override. |
| Funding | Resource account licensing, server access assignment, and outbound connectivity are required. Calling Plan resource-account outbound scenarios changed November 1, 2025. [M2] | Expiring, explicit operator attestations, never inferred from a Teams user license or SDK success. | Calling Plan PAYG/funding, or the relevant Operator Connect/Direct Routing prerequisites. |
| Direct Teams | TPE FAQ initial GA is PSTN-only. Its matrix is scoped to calls with a phone-number participant. Separate Call Automation Teams interop remains labelled public preview and lists desktop/web, not iOS/Android. [M3–M5] | Explicitly unsupported; no PSTN substitution, transfer, or preview assumption. | Direct call + Voice Live duplex media + source presentation + client support + permissions/licensing together. |
| Audio | ACS documents PCM16 mono, 16/24 kHz, bidirectionality, unmixed streams, and stop-audio helpers. [M6] | 24 kHz mono validation, recipient identity filtering, SDK outbound helpers, bounded queues, cancellation, stop and truncate. | Authenticated ingress; source/participant formats on actual TPE calls; carrier buffering and latency. |
| Voice Live | Official Voice Live endpoint, bearer authentication, session options and shared realtime events. [M8, M9] | Official `Azure.AI.VoiceLive` **1.2.0**, explicit GA API **2026-07-15**. No OpenAI endpoint/client and no assembled STT/LLM/TTS chain. [M11] | Region/model/voice/transcription access, session acknowledgements, natural-language behavior, summary availability. |
| Webhooks | ACS signed JWT, OIDC signing keys, issuer, resource audience. [M7] | Local RSA-signed fixture validation, expiration, issuer/audience, correlation, changed-body replay rejection, idempotency. | Actual service claims/key rotation and proxy behavior. |
| Termination | Whole-call termination is supported. [M4, M10] | `HangUpAsync(forEveryone: true)`; independent terminator takes only configuration and opaque connection ID. | Carrier termination timing and independently deployed watchdog behavior. Accepted hangup is **pending**, not confirmation. |

No G1–G4 or four-scenario acceptance claim follows from this table. G5 (local
SQLCipher/privacy) belongs to the broker implementation, not these adapters.

## Host integration

```csharp
using Tpcli.Azure;

builder.Services.AddSingleton<IProviderEventSink, YourRuntimeEventSink>();
builder.Services.AddSingleton<IAzureCallCorrelation, YourDurableCallCorrelation>();
builder.Services.AddTpcliAzure(builder.Configuration);
// Build the app and configure trusted forwarding/TLS and CLI authentication.
app.MapTpcliAzureEndpoints();
```

The extension registers `ICallProviderFactory` (`Mode == "azure"`) and
`ICallTerminator`. It does not register or replace the runtime's event sink.
The shared `Tpcli.Contracts` interfaces remain unchanged.

`IAzureCallCorrelation.TryBindAsync(callId, connectionId, serverCallId, token)`
must use the **durable control store**:

* Accept only an existing Azure call with a recorded dispatch intent, including
  an ambiguous dispatch or an already-ending/revoked call.
* Atomically bind its first opaque provider ID or match an existing binding.
  Reject replacement IDs and cross-call reuse.
* Retain bindings/tombstones for late callbacks and worker restarts.
* Do not authorize a call merely because a supplied call ID exists.

Absent this registration, `CALLBACK_CORRELATION_UNCONFIGURED` is reported and
correlation fails closed. Callbacks publish to the runtime sink even when no
in-memory media worker exists. The runtime must deduplicate provider event IDs
durably, preserve monotonic lifecycle state, and terminate late connections
whose owner/deadline has expired. The adapter's bounded in-memory replay guard
is an additional defense, not a replacement for those durable invariants.

A watchdog can resolve `ICallTerminator` without a media worker, task content,
Voice Live configuration, source-attestation state, or event sink. It needs
only the ACS endpoint, runtime identity, and stored connection ID. A 404, a
local socket close, or an accepted hangup request does not establish carrier
termination; only definitive provider evidence may yield `Confirmed`.

## Exact configuration

All settings are under `Azure` in `IConfiguration`. Environment variables use
double underscores. Do not commit populated configuration, use process arguments
for credentials, or copy workstation/tenant configuration into this repository.

| Environment key | Required value / behavior |
| --- | --- |
| `Azure__Identity__Mode` | `managed_identity` (default) or explicitly `developer_azure_cli`. No implicit developer credential fallback. |
| `Azure__Identity__ManagedIdentityClientId` | Optional user-assigned managed-identity client GUID; omitted means system-assigned identity. |
| `Azure__Identity__DeveloperTenantId` | Tenant GUID required with `developer_azure_cli`. The operator signs in separately. No interactive sign-in or provisioning in the adapter. |
| `Azure__CallAutomation__Endpoint` | Resource HTTPS origin, e.g. `https://RESOURCE.communication.azure.com/`. No query, user-info, path, or non-443 port. |
| `Azure__CallAutomation__ResourceId` | ACS **immutable resource GUID** expected as webhook JWT audience; not an ARM `/subscriptions/...` path. Check the official resource-ID guidance. [M12] |
| `Azure__CallAutomation__ResourceAccountObjectId` | Entra object GUID of the specifically authorized Teams resource account. Not an ACS user identifier. |
| `Azure__CallAutomation__TeamsServiceNumber` | Verified resource-account Teams service number in E.164 form. No ACS-number fallback. |
| `Azure__CallAutomation__PublicBaseUrl` | HTTPS origin of the runtime with valid public TLS; no path/query/user-info or non-443 port. |
| `Azure__VoiceLive__Endpoint` | HTTPS resource origin ending `.services.ai.azure.com` or legacy `.cognitiveservices.azure.com`. Not `.openai.azure.com`. |
| `Azure__VoiceLive__ApiVersion` | Exactly `2026-07-15`; deliberately explicit, not automatically upgraded. |
| `Azure__VoiceLive__Model` | Explicit, operator-verified Voice Live model, e.g. candidate `gpt-realtime`. No model fallback. |
| `Azure__VoiceLive__Voice` | Explicit Azure standard voice name, e.g. candidate `en-US-AvaNeural`; its availability is not assumed. |
| `Azure__VoiceLive__Locale` | Explicit English locale, e.g. `en-US`; other languages are currently gated. |
| `Azure__VoiceLive__TranscriptionModel` | Explicit compatible input transcription model inside Voice Live, e.g. candidate `gpt-4o-transcribe` for `gpt-realtime`. This is not a separate transcription endpoint. [M8] |
| `Azure__Evidence__ResourceAccountBound` | `true` only after external, authorized verification of this resource-account/ACS binding. |
| `Azure__Evidence__ServiceNumberAssigned` | `true` only after verifying the specified Teams service number is assigned to that account. |
| `Azure__Evidence__ResourceAccountLicensed` | `true` only after verifying the required resource-account licensing. |
| `Azure__Evidence__ServerCallingAuthorized` | `true` only after verifying server-side Teams Phone access assignment for this route. |
| `Azure__Evidence__OutboundPstnFunded` | `true` only after verifying the applicable carrier/connectivity/funding prerequisites. |
| `Azure__Evidence__ValidUntilUtc` | UTC ISO 8601 expiry of those operator attestations, in the future and no more than seven days ahead. Default is absent/unverified. |

The evidence fields are **operator attestations**, reported as `attested`, not a
fabricated automated audit. They cannot lift the media-authentication or direct
Teams gates. An endpoint, a valid credential, and an arbitrary set of `true`
values do not prove that the deployment is ready.

Managed identity uses `ManagedIdentityCredential` directly rather than an
unrestricted `DefaultAzureCredential` chain. Development uses only the explicitly
selected `AzureCliCredential` and tenant. API keys/connection strings are not
accepted by this implementation. Assign least-privilege roles and complete
Teams administration separately; runtime credentials must not be admin setup
credentials. Voice Live's current guide documents `Cognitive Services User` and
`Foundry User` role requirements for its recommended Entra configuration. [M8]

The pinned SDK builds:

```text
wss://RESOURCE.services.ai.azure.com/voice-live/realtime
    ?api-version=2026-07-15&model=EXPLICIT_MODEL
```

It obtains scope `https://ai.azure.com/.default` and sends the token in the
`Authorization` **header**, not the URL. This was checked against the tagged SDK
source, not inferred from Azure OpenAI. The ACS SDK uses the ACS token audience;
the read-only token check uses `https://communication.azure.com/.default`.

## Authentication and endpoint behavior

* `POST /azure/callbacks/{callId}` accepts HTTPS only, no query parameters, and
  bounded CloudEvents batches (256 KiB, at most 16 events).
* It manually validates the ACS bearer JWT, independently of the CLI's Entra
  authentication policy. It requires RS256, valid signature, expiration,
  issuer `https://acscallautomation.communication.azure.com`, and the configured
  ACS immutable resource GUID audience, with 30-second clock skew.
* OIDC configuration comes only from
  `https://acscallautomation.communication.azure.com/calling/.well-known/acsopenidconfiguration`.
  Microsoft.IdentityModel handles JWKS discovery/caching and a key-refresh attempt.
  There is no configurable arbitrary issuer or key-server URL.
* JWT authentication authenticates the bearer/service request. The documentation
  does **not** say that the CloudEvent body is independently JWS-signed.
  TLS, expected call/operation/connection correlation, token/body binding, event
  IDs, and replay checks all remain necessary.
* Expired JWTs and events more than 30 seconds in the future are rejected.
  Old event timestamps with a currently valid service bearer and durable call
  correlation can still reach termination logic; late cleanup must not be lost
  merely because a worker is gone. Duplicate authenticated retries are idempotent. A reused bearer
  with changed request bytes is rejected. A sink failure permits an identical
  retry rather than marking an undelivered event complete.
* `GET /azure/media/{callId}` never upgrades in the shipped configuration:
  authentication is unresolved. The independent bridge additionally requires
  matching call/connection identity and the correlation ID from the authenticated
  connected callback, and accepts only one attachment.
* Proxies must preserve the authorization/ACS headers and WebSocket upgrade.
  Configure trusted forwarding so the host correctly knows the original HTTPS
  scheme. Do not trust arbitrary client-supplied forwarded headers.

The routes opt out of **CLI** authentication because its issuer/audience is
different, not because callbacks are anonymous: their own ACS validator runs
before any event publication. Unauthorized callbacks return a sanitized code.
No unauthenticated sample route is included.

## Media, commands, and transcript semantics

Preparation configures the voice session and waits for `session.updated`
confirming the selected model, voice, PCM16 formats, and 24 kHz input **before**
`DialAsync`. The initial response is requested only after call connection and
validated media metadata, with AI-assistant identity/transcription disclosure.
There is no additional pre-dial approval command.

Unmixed ACS input must first supply PCM/24000/mono metadata. Only the selected
PSTN recipient's `participantRawID` is forwarded; other participants/application
audio are excluded, and missing identity is rejected. No resampling, mixed
fallback, or audio recording is performed.
Recipient frame timestamps must be increasing, no more than two seconds old,
and not more than 30 seconds in the future. Run a clock-synchronized host;
old network-buffered frames are not made fresh by assigning a new receive time.

Each direction reserves at most **96,000 PCM bytes (two seconds)**, including an
in-flight frame. Outbound packets are paced in 20 ms slices rather than sent
as an unbounded provider playback backlog. A frame older than two seconds,
an overflow, failed I/O, or disconnected stream fails the call and initiates
whole-call termination; there is no audio replay/reconnection strategy.
An overrun fails immediately rather than silently dropping speech. Metadata
has a five-second timeout, a connected call must establish media within ten
seconds, and an established ACS stream must not stop delivering frames for
five seconds. Hold/silence/carrier behavior must be checked in G2 before release.

Incoming JSON follows the documented ACS format. Outbound audio and flush
messages use the **actual pinned SDK** `OutStreamingData.GetAudioDataForOutbound`
and `GetStopAudioForOutbound` helpers, including their Pascal-case serialization.
Do not replace them with an invented `outputStop` message.

On `input_audio_buffer.speech_started`, the adapter invalidates the current
response, clears buffered output, issues ACS StopAudio, sends `response.cancel`
with that response ID when still active, and sends `conversation.item.truncate`.
The audio end is a wall-clock estimate capped by PCM bytes actually
sent, not a claim of recipient audibility. It waits for `response.done` (or the
explicit no-active-response cancellation race); missing acknowledgement after
two seconds fails the call. Obsolete response audio cannot be replayed.
The 150 ms stop-write bound is an implementation bound, **not a measured
recipient-perceived interruption latency**.

Transcript signals have stable `segment_id`, `speaker`, `text`, increasing
`revision`, `final`, `interrupted`, and `delivery`. Recipient text is `received`;
assistant text starts `generated` and is upgraded to `sent` only when all
associated generated audio has been sent. Interrupted full sentences are
`unknown` delivery. **No “heard” confirmation is produced**; actual carrier
playback acknowledgements are not available here. Missing transcription emits
`transcript.gap`, never invented words. Text retention is bounded and RAM-only.

A bounded, separate signal pump delivers transcript/tool/control events. Audio
loops do not wait for operator approvals or perform DTMF. The narrow model tools:

| Tool | Arguments |
| --- | --- |
| `request_approval` | `action`, `description`, `material_terms` |
| `send_dtmf` | `digits` (`0–9`, `*`, `#`, 1–32 tones), always the current recipient |
| `report_result` | `outcome`, `summary`, `facts`, `commitments`, `outstanding_items`, `source_references`; arrays may be empty |
| `end_call` | Empty object; ending a call does not mark its task completed |

`CompleteToolAsync` consumes the known tool call once, sends
`conversation.item.create` containing `function_call_output`, matching `call_id`
and JSON-string `output`, then requests a response. While approval is pending,
automatic VAD responses are disabled; input/transcription and supervision remain
active. The runtime, not speech or tool arguments, supplies authorized decisions.
Mid-call instructions are JSON-delimited operator context in a user
conversation item, not an injected system-message override.

Guardrails require selective approval before impactful commitments, treat the
recipient as untrusted, prohibit blind transfer, identify the assistant
truthfully, and stop on objection/disallowed voicemail. **Recognizing every
commitment, objection, voicemail, or hold signal is model-driven, not a
deterministic compliance guarantee.** There is no claimed automatic answering
machine detector. Generic create failures remain `TPE_CREATE_FAILED`; precise
carrier busy/no-answer classification is not fabricated from undocumented
subcodes. DTMF API acceptance is not proof that an IVR acted on the tones.

`report_result` supplies the model's structured summary when available.
References/facts/commitments are model-produced claims requiring appropriate
completeness and evidence handling in the runtime/broker. There is no separate
LLM summarizer, invented final summary, or delay to hangup while summarizing.
The host must report `partial`/`unavailable` if the selected model/session cannot
produce usable text/tools or a crash loses RAM-only content.

## Nonbillable doctor limitations

Offline readiness performs only configuration/capability checks: it does not
acquire credentials or contact a network endpoint. Online readiness performs
only public ACS OIDC discovery and Entra token acquisition for ACS/Voice Live.
It never calls `CreateCall`, opens a Voice Live WebSocket, probes a model, changes
a tenant, or provisions anything.

An acquired token proves neither service RBAC nor resource-account binding,
licensing, funding, model/voice access, source presentation, or functioning
media. These checks stay `unknown`/`attested`/`blocked` rather than claiming
success. Perform authorized read-only administrative checks separately; live
integration calls require a distinct explicit operator authorization.

## Privacy, validation, and reproducibility

Task/voice/transcript/tool content exists only in RAM and authenticated transport
to the runtime/broker. There are no audio files, transcript spools, or ordinary
payload log statements. Azure SDK HTTP/content/distributed diagnostics are
disabled. Voice Live uses the official SDK's raw command/update methods to avoid
its typed-event opt-in GenAI content tracing. The extension suppresses ASP.NET
HTTP logging fields for `/azure` routes. Hosts/proxies must also exclude bodies,
bearers, task text, audio, tool arguments, and credential-bearing URLs from
diagnostics; do not enable GenAI content capture or request-body capture.

The isolated tests use synthetic in-memory audio, mock SDK calls/WebSockets,
and locally generated RSA keys; no internet, tenant, microphone, or provider
credentials are needed. Tests cover source identity/no caller fallback, actual
SDK whole-call hangup/DTMF overloads, gate-before-voice/dial, JWT validation and
replay, callback correlation/late delivery, metadata/participant rejection,
bounded/stale audio, interruption/cancel/StopAudio/truncation, transcript
revisions/delivery, function continuation, deadlines, overflow/disconnect
termination, and registration.

Normal public NuGet environment:

```sh
dotnet test runtime/Tpcli.Azure.Tests/Tpcli.Azure.Tests.csproj
```

On the validation host NuGet.org had a TLS transport failure. Restoring the same
public pinned packages succeeded with Microsoft's public, no-auth SDK and
dotnet feeds; no private feed or user/global configuration was changed:

```sh
dotnet restore runtime/Tpcli.Azure.Tests/Tpcli.Azure.Tests.csproj \
  --source https://pkgs.dev.azure.com/azure-sdk/public/_packaging/azure-sdk-for-net/nuget/v3/index.json \
  --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json
dotnet test runtime/Tpcli.Azure.Tests/Tpcli.Azure.Tests.csproj --no-restore
```

For concurrent worktree validation, the same commands can use
`--artifacts-path runtime/Tpcli.Azure/.build/artifacts` to isolate generated
assets. Local compilation/tests are **not live acceptance tests**.

## Primary references

All links below were accessed September 21, 2026. SDK links are pinned to the
released tags rather than `main`.

* **[M1]** [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
* **[M2]** [Server-initiated outbound TPE calls, source identity, licensing and funding](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-server-outbound-call).
* **[M3]** [TPE FAQ: service numbers, PSTN-only initial GA, policies](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-faq).
* **[M4]** [TPE capability matrix and PSTN-participant scope](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-capabilities).
* **[M5]** [Separate Call Automation Teams interop, preview/client limitations](https://learn.microsoft.com/en-us/azure/communication-services/concepts/call-automation/call-automation-teams-interop).
* **[M6]** [ACS streaming metadata, bidirectional PCM, correlation headers, outbound and stop-audio helpers](https://learn.microsoft.com/en-us/azure/communication-services/how-tos/call-automation/audio-streaming-quickstart).
* **[M7]** [Securing Call Automation webhook callbacks: JWT, issuer, audience and OIDC/JWKS](https://learn.microsoft.com/en-us/azure/communication-services/how-tos/call-automation/secure-webhook-endpoint).
* **[M8]** [Voice Live endpoint/authentication, session options, transcription compatibility and voice configuration](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to).
* **[M9]** Voice Live 1.2.0 generated protocol models for [response-specific cancellation](https://github.com/Azure/azure-sdk-for-net/blob/Azure.AI.VoiceLive_1.2.0/sdk/voicelive/Azure.AI.VoiceLive/src/Generated/Models/ClientEventResponseCancel.cs) and [conversation audio truncation](https://github.com/Azure/azure-sdk-for-net/blob/Azure.AI.VoiceLive_1.2.0/sdk/voicelive/Azure.AI.VoiceLive/src/Generated/Models/ClientEventConversationItemTruncate.cs). The general realtime reference linked by the Voice Live guide now redirects elsewhere; the pinned Voice Live models are the concrete protocol evidence here.
* **[M10]** [Call Automation 1.6.0 released API surface](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Communication.CallAutomation_1.6.0/sdk/communication/Azure.Communication.CallAutomation/api/Azure.Communication.CallAutomation.netstandard2.0.cs) and [outbound streaming helper source](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Communication.CallAutomation_1.6.0/sdk/communication/Azure.Communication.CallAutomation/src/Models/Streaming/OutStreamingData.cs).
* **[M11]** [Voice Live 1.2.0 release/API versions](https://github.com/Azure/azure-sdk-for-net/blob/Azure.AI.VoiceLive_1.2.0/sdk/voicelive/Azure.AI.VoiceLive/CHANGELOG.md), [released API surface](https://github.com/Azure/azure-sdk-for-net/blob/Azure.AI.VoiceLive_1.2.0/sdk/voicelive/Azure.AI.VoiceLive/api/Azure.AI.VoiceLive.net8.0.cs), [endpoint construction](https://github.com/Azure/azure-sdk-for-net/blob/Azure.AI.VoiceLive_1.2.0/sdk/voicelive/Azure.AI.VoiceLive/src/VoiceLiveClient.WebSockets.cs), and [scope/bearer-header implementation](https://github.com/Azure/azure-sdk-for-net/blob/Azure.AI.VoiceLive_1.2.0/sdk/voicelive/Azure.AI.VoiceLive/src/Customizations/VoiceLiveSession.Protocol.cs).
* **[M12]** [Finding the immutable ACS resource ID](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/voice-video-calling/get-resource-id).
