# Hosted runtime and independent watchdog

These are deployment **definitions**, not a ready deployment or permission to
provision anything. No deployment, tenant write, license purchase, number
assignment, admin consent, new credentials or live call is performed by tests/CI.
The product remains a development preview with live gates in
[platform evidence](../docs/platform.md).

## Supported architecture

Use two separately scheduled Container Apps: the ASP.NET Core media/control
runtime, and an independently executing .NET termination watchdog. Both use the
same external durable control store. The watchdog has no public ingress, no
conversation, and no ability to dial. The media runtime has TLS HTTPS/WebSocket
ingress. Each has at least one replica; the initial runtime deployment is limited
to one active replica, in addition to store-backed ownership checks.

Do not host the only watchdog inside the media worker, on the user's laptop, or
in the local broker. A dev tunnel is not remote owner-loss protection if the same
laptop sleeps. Two apps protect against the single media-process failure boundary,
not an entire region/control-plane outage. Monitor orphan risk and retain a manual
provider-termination escalation route.

`main.bicep` references an **existing** Container Apps environment, registry,
user-assigned identity, Key Vault secrets, and shared control-store configuration.
It creates only the runtime/watchdog app definitions when an operator separately
deploys it. It does not grant permissions, create accounts, provision Teams,
replace an inbound application, or select a source number.

## Build, configure, and release separately

The operator may build `runtime.Dockerfile` and `watchdog.Dockerfile` after selecting
the repository license and reviewing dependency distribution obligations. Images
run as the non-root .NET application user. No image/push/release is automated here.

Provide runtime and watchdog settings as nonsecret environment entries. Pass
sensitive connection strings through existing Key Vault secret references and
Container Apps `secretRef`, never through a committed parameter file or a shell
argument. Both processes must select the same production control-store backend;
local SQLite is for the deterministic local harness, not a shared Azure Files
distributed database.

Set the exact Azure options documented in [platform evidence](../docs/platform.md),
including the public callback/media origin, ACS resource, authorized Teams source,
service-number readiness assertions, Voice Live endpoint/model/version/voice, and
the relevant managed identity. Callback/media ingress must be authenticated as
documented; an opaque call ID or a public tunnel is not authentication.

Configure Entra tenant, audience, and the `tpcli.control` delegated runtime
permission. Runtime identities must have separately approved minimum ACS /
Voice Live / Key Vault / registry access. Do not give the runtime tenant
administration credentials. Put a TLS-validating connection configuration on the
shared database and apply least-privilege database permissions.

The runtime origin output goes into a private CLI profile based on
`profile.example.toml`. This public example contains only placeholder UUIDs,
a reserved phone number in documentation, and `.invalid` hostnames.

## Before any real recipient

Verify source binding, Teams service number, licensing/outbound funding, ACS
permissions, Azure Voice Live access, both authenticated callback/media paths, and
the deployed watchdog's access to provider termination and the control store.
`doctor --online` is non-dialing and cannot prove the complete media path.

Perform separately authorized process-failure and owner-loss tests against the
deployment, then an operator-released controlled call. Confirm actual provider
hangup rather than merely an accepted request. Collect destination/call evidence
privately, never in public CI artifacts, issue text, or PR descriptions.

Restrict diagnostic logging to sanitized identifiers and error codes; disable
payload logging, raw audio capture, body tracing and memory/core dumps for the
media worker. Apply approved metadata retention only after final termination;
never delete unresolved correlation needed to terminate an uncertain call.
