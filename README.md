# tpcli

Give an AI assistant a phone task, then follow, steer, or end its call from your
terminal. tpcli uses **Teams Phone** for the phone call and **Azure Voice Live**
for the assistant's spoken conversation.

**Development preview:** the local system and real Azure adapters are implemented,
but live calling still requires deployment and readiness verification. The examples
below show intended usage after those gates are resolved; the local simulation
never places a real call.

**Give a task -> let the assistant call and converse -> review the result.**

## A basic call

Write a short brief describing what the assistant should find out and what it
must not do. This example asks for opening hours, without booking or buying:

```sh
umask 077
printf '%s\n' 'Ask for public opening hours. Do not book or buy anything.' > brief.txt

tpcli call pstn:+12025550123 --profile example-dev \
  --task-file ./brief.txt --max-duration-seconds 120
```

The number above is **fictitious**. Replace it only with an authorized recipient,
and configure `example-dev` as described in the [setup and usage guide](docs/usage.md).
The running command displays the session ID, call ID, state and transcript events.
Keep it open to supervise this call; press **Ctrl+C** to request hangup.

### Watch, steer, or stop

In another terminal, replace `SESSION_ID` and `CALL_ID` with the IDs displayed by
the running call. These commands use that same call, not a new attempt:

```sh
# Watch the conversation; Ctrl+C here closes only this viewer.
tpcli calls events CALL_ID --session SESSION_ID --profile example-dev --follow

# In a separate invocation, give an additional instruction while it is talking.
printf '%s' 'Also ask whether Saturday hours differ.' |
  tpcli calls instruct CALL_ID --session SESSION_ID --profile example-dev --text-stdin

# Request a stop, then inspect the saved outcome.
tpcli calls stop CALL_ID --session SESSION_ID --profile example-dev
tpcli history show CALL_ID --profile example-dev
```

The **controlling session** keeps the call supervised; closing an event viewer is
not the same as ending that session. A stop request is not proof of remote hangup:
the result reports whether termination was confirmed or remains uncertain.

Transcripts and summaries stay in **encrypted local history**. The application
does not record raw audio, but Azure/carriers still process the conversation under
their own terms. Material commitments can require a specific approval; this is
not a guarantee that a model will recognize every sensitive action.

## Scripts and agent hosts

For several short-lived commands, start a supervised shell (or an agent executable):

```sh
tpcli session exec --profile example-dev -- zsh
# Inside that shell, commands inherit the session.
tpcli calls start --request-stdin --idempotency-key deliberate-attempt-1 --json < private-request.json
tpcli calls events CALL_ID --after 0 --wait-seconds 10 --json
tpcli calls stop CALL_ID --json
tpcli calls wait CALL_ID --json
```

`calls start` returns a receipt containing `call_id` and `command_id`; substitute
the returned call ID in subsequent commands. The private request contains the
target, brief, and deadline; see the [request example and host contract](docs/usage.md).
A start receipt means **accepted**, not connected or completed. `calls wait`
succeeds only when the task is completed **and** remote hangup is confirmed.
Finishing a one-shot command does not stop the supervising shell's call.

## Develop locally

The Rust toolchain is pinned in `rust-toolchain.toml`; .NET 10 is pinned in
`global.json`. A C compiler, Perl, and Make are needed for bundled SQLCipher and
OpenSSL. Python 3 is used for cross-process tests, with no Python packages needed.

```sh
cargo build --locked
cargo test --workspace --locked
dotnet build runtime/Tpcli.slnx
dotnet test runtime/Tpcli.slnx --no-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
```

The cross-process harness creates its own temporary, private profiles, random
test keys, local runtime and independently running watchdog. It never consults
Keychain, contacts Azure, or places a real call. No account is needed.

## Architecture and live readiness

A Rust CLI and session-scoped local broker supervise a C#/.NET call runtime.
The broker persists replayable events in SQLCipher; an independent hosted watchdog
handles owner loss and deadlines even if the media worker fails.

Real calling requires a verified Teams Phone Extensibility resource-account and
service-number route, licensed outbound connectivity, Azure Voice Live access,
authenticated callback/media endpoints, and a separately running watchdog.
An uncertain create is never automatically redialed. Direct `teams:` calls remain
blocked pending separate bidirectional-media proof; there is no PSTN, ACS-number,
or alternate-provider fallback. These gates prevent a full-v1 claim.

## Documentation

- [Public specification and acceptance criteria](docs/SPEC.md)
- [CLI, host integration, and local development](docs/usage.md)
- [Platform evidence and live feasibility gates](docs/platform.md)
- [Privacy, encryption, recovery, and operational boundaries](docs/privacy.md)
- [Validation evidence and remaining acceptance gates](docs/acceptance.md)
- [Hosted deployment](infra/README.md)

See the [implementation specification](docs/SPEC.md) for requirements, architecture,
acceptance criteria, and the live-service feasibility gates.

Deployments require separately provisioned Teams and Azure resources. Never commit
credentials, tenant-specific profiles, transcripts, or private environment details.

**License:** the repository owner has not selected a license. No MIT/Apache or
other license grant is implied. The build does not publish packages or releases.
Third-party dependencies retain their own licenses; see [dependency notes](docs/dependencies.md).
