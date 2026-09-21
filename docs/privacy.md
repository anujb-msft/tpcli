# Privacy, storage, recovery, and operations

## Data boundaries

- Raw audio exists only transiently in the hosted ACS / Voice Live media bridge.
  The CLI, broker, local database and control store never receive audio frames.
- Tasks, transcript text, approval material terms, and summaries are deliberately
  available to the authorized operator, not ordinary diagnostic logs.
- Local history uses **genuine bundled SQLCipher 4**, with a raw 256-bit key,
  encrypted WAL/journal content, in-memory temporary storage, FULL synchronous
  commits and secure deletion. A mode-0600 plaintext SQLite file is not an
  alternative. Startup checks cipher support and actually reads keyed schema.
- A user-private state directory, UID-checked Unix socket, and single-writer lock
  complement encryption; they do not replace it.
- The hosted durable store contains allowlisted reconciliation/control metadata,
  never a durable transcript/task/approval-content spool. The runtime has bounded
  volatile event replay. A worker failure or a prolonged client outage can lose
  content. Gaps and interrupted output remain explicit; summaries may be absent.

`transcripts show`, event subscriptions, and history rendering deliberately
decrypt content to stdout for the requesting process. Redirecting that output
creates an operator-owned plaintext export. The application never does that for
diagnostics. Secure the consuming agent host, terminal scrollback, and any export.
Disable body tracing and memory/core dumps on media workers: a dump can contain
transient audio, conversation content or credentials even without audio recording.

No application audio recording does **not** imply zero Azure/carrier retention or
end-to-end PSTN encryption. Azure services process audio and text under their
own terms. Operators must establish consent/transcription notice, calling laws,
data handling policies, permitted recipients and a legal basis for use.

## Keys and encrypted backups

Production macOS credentials live in Keychain under service `tpcli`, with
per-profile entries for SQLCipher and Entra tokens. No key belongs in TOML,
arguments, source, a plaintext sidecar, or an ordinary log. On first authorized
sign-in, key generation refuses to overwrite an inaccessible/invalid key or
initialize a new key over existing history. Key access failure stops supervision.

```sh
tpcli history backup --destination /private/backup/directory/history-backup.db
```

The destination directory must be user-owned/private; the destination must not
exist. This uses SQLite's online backup API between two **keyed SQLCipher**
connections, then validates the result. It handles an active WAL without blindly
copying only the main database. The backup uses the same Keychain key; it is not
a plaintext export and contains no embedded recovery secret.

**Recovery workflow:** retain an encrypted macOS system/Time Machine backup that
includes both the login Keychain and the encrypted database/backup. Protect its
recovery password separately. Restore the original Keychain to the intended user
account through the OS recovery/migration workflow, then restore the encrypted
database into its private profile directory while no broker is running.
Run offline `doctor` and explicitly inspect history. Do not overwrite an existing
history with an unverified backup.

This initial implementation does not offer a portable passphrase-encrypted key
escrow format. A database-only backup is insufficient if Keychain is lost. Losing
the only original key makes the data unrecoverable; generating a replacement key
does not recover it. OS-level key backup/recovery must be exercised by the operator,
not by CI using real credentials. Test harness keys are ephemeral and disposable,
not a production recovery mechanism.

## Retention and deletion

There is no silent local expiration. History accumulates until explicit pruning.
Preview removal with `history prune --before DATE --dry-run`, then intentionally
repeat without `--dry-run`. Unconfirmed/active calls are protected from removal.
Keep the broker stopped while performing the single-writer pruning operation.
Prune encrypted backups separately if that is the desired retention policy.
Filesystem/SSD snapshots and OS backups may retain previously encrypted pages;
local pruning is not a promise of physical media erasure.
Unresolved submissions with no recoverable call ID retain their encrypted
idempotency intent for reconciliation; pruning some other call does not silently
discard that recovery record.

Minimal server control metadata has a separate, proposed 72-hour post-termination
retention policy. Apply an approved server-store retention process only to
definitively terminated records; do not delete unresolved provider correlation.
An absence of transcript payloads does not make telephone destinations, timestamps,
principal identifiers, or call metadata non-sensitive.

## Failure and emergency operations

A call has one supervising owner. Commands and viewers are not owners. The
runtime lease and the independently deployed watchdog initiate whole-call hangup
after ownership loss or deadline; a watchdog sharing only the failed process is
not adequate. Monitor unknown terminations, expired owners, worker heartbeat loss,
and failed termination attempts with sanitized IDs/codes.

An accepted hangup request is not proof of remote hangup. A failed runtime, remote
network, or carrier can leave termination uncertain. Keep retrying only safe
termination/reconciliation; never redial automatically. Provide an operator
escalation path through the provider's call controls for orphan risk.

Do not grant the application general Teams administration, unrestricted directory
access, calendar/CRM writes, shell/browser execution, blind transfer, or arbitrary
caller ID selection. Administrative setup remains separate from runtime identity.
Model instructions ask for selective approvals, but are not a guarantee that every
impactful verbal commitment will be recognized. Deterministic enforcement begins
when an exact pending approval exists.

## Private live smoke-test procedure

Execute only after a separately authorized recipient and a ready real deployment
have been confirmed and the supervising operator releases the test:

1. Check the real TPE resource-account/service-number route, Entra authorization,
   Azure Voice Live configuration, reachable authenticated callback/media ingress,
   and independently running watchdog. Do not replace a blocked prerequisite.
2. Supply the actual authorized destination and brief privately via stdin. Set a
   120-second total deadline, `allow_voicemail=false`, and a unique deliberate
   attempt key. Never put that request in this public repository or CI.
3. Identify as an AI test assistant, explain local text transcription and no
   application audio recording, exchange a short two-way phrase, allow a brief
   recipient interruption, and stop immediately on objection or request.
4. Verify encrypted local events/transcript/summary and actual provider-confirmed
   remote hangup. If any result is uncertain, report it privately and reconcile;
   do not retry or automatically redial.

Implementation tests or repository publication are not authorization to execute
this procedure. Record real call evidence privately, not in public PRs or issues.
