use crate::{
    auth::CipherKey,
    config::private_dir,
    error::db_error,
    protocol::{CallState, CommandReceipt, CommandRequest, Event, EventBatch, BATCH_SIZE},
    Error, Result,
};
use rusqlite::{params, Connection, OpenFlags, OptionalExtension, Transaction};
use serde::{de::DeserializeOwned, Serialize};
use serde_json::{json, Value};
use std::{
    fs::OpenOptions,
    os::unix::fs::{MetadataExt, OpenOptionsExt},
    path::Path,
    time::Duration,
};

pub struct Store {
    connection: Connection,
}

fn encode<T: Serialize>(value: &T) -> Result<String> {
    serde_json::to_string(value)
        .map_err(|_| Error::new("PROTOCOL_ERROR", "Cannot encode stored protocol data."))
}

fn decode<T: DeserializeOwned>(value: &str) -> Result<T> {
    serde_json::from_str(value).map_err(|_| {
        Error::new(
            "ENCRYPTED_STORE_FAILED",
            "Invalid encrypted history record.",
        )
    })
}

impl Store {
    pub fn open(path: &Path, key: &CipherKey, writable: bool) -> Result<Self> {
        let parent = path
            .parent()
            .ok_or_else(|| Error::new("CONFIGURATION", "History path has no parent."))?;
        private_dir(parent)?;
        if writable && !path.exists() {
            match OpenOptions::new()
                .write(true)
                .create_new(true)
                .mode(0o600)
                .custom_flags(libc::O_NOFOLLOW)
                .open(path)
            {
                Ok(_) => {}
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {}
                Err(_) => return Err(db_error(rusqlite::Error::InvalidQuery)),
            }
        }
        let metadata = std::fs::symlink_metadata(path).map_err(|_| {
            Error::new(
                "ENCRYPTED_STORE_FAILED",
                "History does not exist or is inaccessible.",
            )
        })?;
        if !metadata.is_file()
            || metadata.file_type().is_symlink()
            || metadata.uid() != unsafe { libc::geteuid() }
            || metadata.mode() & 0o077 != 0
        {
            return Err(Error::new(
                "IPC_PERMISSIONS",
                "History must be a private, user-owned regular file.",
            ));
        }
        let flags = if writable {
            OpenFlags::SQLITE_OPEN_READ_WRITE
        } else {
            OpenFlags::SQLITE_OPEN_READ_ONLY
        } | OpenFlags::SQLITE_OPEN_NO_MUTEX
            | OpenFlags::SQLITE_OPEN_NOFOLLOW;
        let resolved = parent
            .canonicalize()
            .map_err(|_| {
                Error::new(
                    "ENCRYPTED_STORE_FAILED",
                    "Cannot resolve the private history directory.",
                )
            })?
            .join(
                path.file_name()
                    .ok_or_else(|| Error::new("CONFIGURATION", "Invalid history filename."))?,
            );
        let connection = Connection::open_with_flags(resolved, flags).map_err(db_error)?;
        let cipher: Option<String> = connection
            .query_row("PRAGMA cipher_version", [], |row| row.get(0))
            .optional()
            .map_err(db_error)?;
        require_cipher(cipher.as_deref())?;
        let secret = key.hex();
        connection
            .execute_batch(&format!(
                "PRAGMA key = \"x'{}'\"; PRAGMA cipher_memory_security = ON;\
                 PRAGMA temp_store = MEMORY; PRAGMA trusted_schema = OFF;\
                 PRAGMA foreign_keys = ON;",
                secret.as_str()
            ))
            .map_err(db_error)?;
        connection
            .busy_timeout(Duration::from_secs(2))
            .map_err(db_error)?;
        connection
            .query_row("SELECT count(*) FROM sqlite_master", [], |r| {
                r.get::<_, i64>(0)
            })
            .map_err(db_error)?;
        let mut store = Self { connection };
        if writable {
            store
                .connection
                .execute_batch("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA secure_delete = ON;")
                .map_err(db_error)?;
            store.migrate()?;
        } else {
            store
                .connection
                .execute_batch("PRAGMA query_only = ON;")
                .map_err(db_error)?;
            let version: i64 = store
                .connection
                .query_row("SELECT max(version) FROM schema_migrations", [], |r| {
                    r.get(0)
                })
                .map_err(db_error)?;
            if version != 2 {
                return Err(Error::new("SCHEMA_VERSION", "Unsupported history schema."));
            }
        }
        Ok(store)
    }

    fn migrate(&mut self) -> Result<()> {
        let transaction = self.connection.transaction().map_err(db_error)?;
        transaction
            .execute_batch(
                "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY);
                 CREATE TABLE IF NOT EXISTS calls(
                   call_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, updated_at TEXT NOT NULL,
                   state TEXT NOT NULL, brief TEXT);
                 CREATE TABLE IF NOT EXISTS events(
                   call_id TEXT NOT NULL, sequence INTEGER NOT NULL, event_id TEXT NOT NULL UNIQUE,
                   event TEXT NOT NULL, PRIMARY KEY(call_id, sequence));
                 CREATE TABLE IF NOT EXISTS transcript_segments(
                   call_id TEXT NOT NULL, segment_id TEXT NOT NULL, revision INTEGER NOT NULL,
                   final INTEGER NOT NULL, interrupted INTEGER NOT NULL, payload TEXT NOT NULL,
                   first_sequence INTEGER NOT NULL, PRIMARY KEY(call_id, segment_id));
                 CREATE TABLE IF NOT EXISTS summaries(call_id TEXT PRIMARY KEY, payload TEXT NOT NULL);
                 CREATE TABLE IF NOT EXISTS commands(
                   command_id TEXT PRIMARY KEY, call_id TEXT, receipt TEXT NOT NULL);
                 CREATE TABLE IF NOT EXISTS command_intents(
                   idempotency_key TEXT NOT NULL, session_id TEXT NOT NULL, request TEXT NOT NULL,
                   PRIMARY KEY(session_id, idempotency_key));
                 CREATE TABLE IF NOT EXISTS approvals(
                   approval_id TEXT PRIMARY KEY, call_id TEXT NOT NULL, payload TEXT NOT NULL);
                 CREATE TABLE IF NOT EXISTS gaps(
                   call_id TEXT NOT NULL, first_sequence INTEGER NOT NULL, last_sequence INTEGER NOT NULL,
                   PRIMARY KEY(call_id, first_sequence));
                 INSERT OR IGNORE INTO schema_migrations(version) VALUES (1);",
            )
            .map_err(db_error)?;
        let version: i64 = transaction
            .query_row("SELECT max(version) FROM schema_migrations", [], |r| {
                r.get(0)
            })
            .map_err(db_error)?;
        if version == 1 {
            transaction
                .execute_batch(
                    "ALTER TABLE command_intents ADD COLUMN call_id TEXT;
                 CREATE TABLE pending_briefs(call_id TEXT PRIMARY KEY, brief TEXT NOT NULL);
                 INSERT INTO schema_migrations(version) VALUES(2);",
                )
                .map_err(db_error)?;
        } else if version != 2 {
            return Err(Error::new("SCHEMA_VERSION", "Unsupported history schema."));
        }
        transaction.commit().map_err(db_error)
    }

    pub fn cipher_version(&self) -> Result<String> {
        self.connection
            .query_row("PRAGMA cipher_version", [], |r| r.get(0))
            .map_err(db_error)
    }

    pub fn integrity_check(&self) -> Result<()> {
        let status: String = self
            .connection
            .query_row("PRAGMA integrity_check", [], |r| r.get(0))
            .map_err(db_error)?;
        if status != "ok" {
            return Err(Error::new(
                "ENCRYPTED_STORE_FAILED",
                "Encrypted history failed integrity checking.",
            ));
        }
        Ok(())
    }

    pub fn save_intent(&mut self, command: &CommandRequest) -> Result<()> {
        let text = encode(command)?;
        let existing: Option<String> = self
            .connection
            .query_row(
                "SELECT request FROM command_intents WHERE session_id=?1 AND idempotency_key=?2",
                params![command.session_id, command.idempotency_key],
                |r| r.get(0),
            )
            .optional()
            .map_err(db_error)?;
        if let Some(existing) = existing {
            if existing != text {
                return Err(Error::new(
                    "IDEMPOTENCY_CONFLICT",
                    "This key already identifies a different command in this session.",
                ));
            }
            return Ok(());
        }
        self.connection
            .execute(
                "INSERT INTO command_intents(session_id,idempotency_key,request,call_id) VALUES(?1,?2,?3,?4)",
                params![command.session_id, command.idempotency_key, text, command.call_id],
            )
            .map_err(db_error)?;
        Ok(())
    }

    pub fn save_receipt(&mut self, receipt: &CommandReceipt) -> Result<()> {
        let transaction = self.connection.transaction().map_err(db_error)?;
        Self::receipt_in(&transaction, receipt)?;
        transaction.commit().map_err(db_error)
    }

    pub fn link_receipt(
        &mut self,
        command: &CommandRequest,
        receipt: &CommandReceipt,
    ) -> Result<()> {
        let transaction = self.connection.transaction().map_err(db_error)?;
        Self::receipt_in(&transaction, receipt)?;
        if let Some(id) = &receipt.call_id {
            transaction.execute(
                "UPDATE command_intents SET call_id=?1 WHERE session_id=?2 AND idempotency_key=?3",
                params![id,command.session_id,command.idempotency_key]
            ).map_err(db_error)?;
            if command.operation == "calls.start" {
                let brief = command.payload["task"].as_str().ok_or_else(|| {
                    Error::new("PROTOCOL_ERROR", "Accepted start request has no task.")
                })?;
                transaction
                    .execute(
                        "INSERT INTO pending_briefs(call_id,brief) VALUES(?1,?2)
                     ON CONFLICT(call_id) DO NOTHING",
                        params![id, brief],
                    )
                    .map_err(db_error)?;
                transaction
                    .execute(
                        "UPDATE calls SET brief=?1 WHERE call_id=?2",
                        params![brief, id],
                    )
                    .map_err(db_error)?;
            }
        }
        transaction.commit().map_err(db_error)
    }

    fn receipt_in(transaction: &Transaction<'_>, receipt: &CommandReceipt) -> Result<()> {
        let old: Option<String> = transaction
            .query_row(
                "SELECT receipt FROM commands WHERE command_id=?1",
                [&receipt.command_id],
                |r| r.get(0),
            )
            .optional()
            .map_err(db_error)?;
        if let Some(old) = old {
            let old: CommandReceipt = decode(&old)?;
            if old.updated_at > receipt.updated_at
                || old.status != "accepted" && receipt.status == "accepted"
            {
                return Ok(());
            }
        }
        transaction
            .execute(
                "INSERT INTO commands(command_id,call_id,receipt) VALUES(?1,?2,?3)
                 ON CONFLICT(command_id) DO UPDATE SET receipt=excluded.receipt,call_id=excluded.call_id",
                params![receipt.command_id, receipt.call_id, encode(receipt)?],
            )
            .map_err(db_error)?;
        Ok(())
    }

    pub fn receipt(&self, id: &str) -> Result<CommandReceipt> {
        let text = self
            .connection
            .query_row(
                "SELECT receipt FROM commands WHERE command_id=?1",
                [id],
                |r| r.get::<_, String>(0),
            )
            .optional()
            .map_err(db_error)?
            .ok_or_else(|| Error::new("NOT_FOUND", "Command is not in encrypted history."))?;
        decode(&text)
    }

    pub fn ingest(&mut self, event: &Event) -> Result<bool> {
        event.validate()?;
        let Some(call_id) = &event.call_id else {
            return Ok(false);
        };
        let text = encode(event)?;
        let transaction = self.connection.transaction().map_err(db_error)?;
        let old: Option<String> = transaction
            .query_row(
                "SELECT event FROM events WHERE call_id=?1 AND sequence=?2",
                params![call_id, event.sequence],
                |r| r.get(0),
            )
            .optional()
            .map_err(db_error)?;
        if let Some(old) = old {
            if old != text {
                return Err(Error::new(
                    "EVENT_CONFLICT",
                    "Conflicting runtime event sequence.",
                ));
            }
            return Ok(false);
        }
        let last: u64 = transaction
            .query_row(
                "SELECT coalesce(max(sequence),0) FROM events WHERE call_id=?1",
                [call_id],
                |r| r.get(0),
            )
            .map_err(db_error)?;
        if event.sequence > last + 1 {
            transaction.execute(
                "INSERT OR IGNORE INTO gaps(call_id,first_sequence,last_sequence) VALUES(?1,?2,?3)",
                params![call_id, last + 1, event.sequence - 1]
            ).map_err(db_error)?;
        }
        transaction
            .execute(
                "INSERT INTO events(call_id,sequence,event_id,event) VALUES(?1,?2,?3,?4)",
                params![call_id, event.sequence, event.event_id, text],
            )
            .map_err(db_error)?;
        if let Some(value) = event.payload.get("state") {
            let state: CallState = serde_json::from_value(value.clone()).map_err(|_| {
                Error::new("PROTOCOL_ERROR", "Invalid call state in runtime event.")
            })?;
            if state.call_id != *call_id || state.session_id != event.session_id {
                return Err(Error::new(
                    "PROTOCOL_ERROR",
                    "Call state correlation mismatch.",
                ));
            }
            Self::state_in(&transaction, &state, event.sequence)?;
        }
        if let Some(value) = event.payload.get("receipt") {
            let receipt: CommandReceipt = serde_json::from_value(value.clone())
                .map_err(|_| Error::new("PROTOCOL_ERROR", "Invalid command event receipt."))?;
            if receipt.session_id != event.session_id || receipt.call_id.as_ref() != Some(call_id) {
                return Err(Error::new(
                    "PROTOCOL_ERROR",
                    "Receipt event correlation mismatch.",
                ));
            }
            Self::receipt_in(&transaction, &receipt)?;
        }
        match event.kind.as_str() {
            "transcript.partial" | "transcript.final" | "transcript.interrupted" => {
                Self::segment_in(&transaction, call_id, event)?;
            }
            "transcript.gap" => {
                transaction.execute(
                    "INSERT OR IGNORE INTO gaps(call_id,first_sequence,last_sequence) VALUES(?1,?2,?2)",
                    params![call_id,event.sequence],
                ).map_err(db_error)?;
            }
            "summary.ready" => {
                transaction
                    .execute(
                        "INSERT INTO summaries(call_id,payload) VALUES(?1,?2)
                     ON CONFLICT(call_id) DO UPDATE SET payload=excluded.payload",
                        params![call_id, encode(&event.payload)?],
                    )
                    .map_err(db_error)?;
            }
            "approval.requested" | "approval.resolved" | "approval.expired" => {
                let payload = event.payload.get("approval").unwrap_or(&event.payload);
                Self::approval_in(&transaction, call_id, payload)?;
            }
            _ => {}
        }
        transaction.commit().map_err(db_error)?;
        Ok(true)
    }

    fn approval_in(transaction: &Transaction<'_>, call_id: &str, payload: &Value) -> Result<()> {
        let id = payload["approval_id"]
            .as_str()
            .ok_or_else(|| Error::new("PROTOCOL_ERROR", "Approval ID is missing."))?;
        if payload.get("call_id").is_some_and(|value| value != call_id) {
            return Err(Error::new(
                "PROTOCOL_ERROR",
                "Approval call correlation mismatch.",
            ));
        }
        let old: Option<(String, String)> = transaction
            .query_row(
                "SELECT call_id,payload FROM approvals WHERE approval_id=?1",
                [id],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()
            .map_err(db_error)?;
        let mut merged: Value = if let Some((old_call, value)) = old {
            if old_call != call_id {
                return Err(Error::new(
                    "EVENT_CONFLICT",
                    "Approval identity changed calls.",
                ));
            }
            decode(&value)?
        } else {
            json!({})
        };
        let late_pending = payload["status"] == "pending"
            && merged["status"]
                .as_str()
                .is_some_and(|status| status != "pending");
        if let (Some(base), Some(update)) = (merged.as_object_mut(), payload.as_object()) {
            for (field, value) in update {
                if late_pending
                    && matches!(
                        field.as_str(),
                        "status" | "actor" | "resolved_at" | "consumed_at"
                    )
                {
                    continue;
                }
                base.insert(field.clone(), value.clone());
            }
        }
        transaction
            .execute(
                "INSERT INTO approvals(approval_id,call_id,payload) VALUES(?1,?2,?3)
             ON CONFLICT(approval_id) DO UPDATE SET payload=excluded.payload",
                params![id, call_id, encode(&merged)?],
            )
            .map_err(db_error)?;
        Ok(())
    }

    pub fn cache_approvals(&mut self, call_id: &str, approvals: &Value) -> Result<()> {
        let values = approvals
            .as_array()
            .ok_or_else(|| Error::new("PROTOCOL_ERROR", "Invalid approval list response."))?;
        let transaction = self.connection.transaction().map_err(db_error)?;
        for approval in values {
            Self::approval_in(&transaction, call_id, approval)?;
        }
        transaction.commit().map_err(db_error)
    }

    fn state_in(transaction: &Transaction<'_>, state: &CallState, sequence: u64) -> Result<()> {
        let old: Option<String> = transaction
            .query_row(
                "SELECT state FROM calls WHERE call_id=?1",
                [&state.call_id],
                |r| r.get(0),
            )
            .optional()
            .map_err(db_error)?;
        if let Some(old) = old {
            let old: CallState = decode(&old)?;
            if old.last_sequence > sequence {
                return Ok(());
            }
        }
        let mut state = state.clone();
        state.last_sequence = state.last_sequence.max(sequence);
        transaction.execute(
            "INSERT INTO calls(call_id,session_id,updated_at,state,brief)
             VALUES(?1,?2,?3,?4,(SELECT brief FROM pending_briefs WHERE call_id=?1))
             ON CONFLICT(call_id) DO UPDATE SET updated_at=excluded.updated_at,state=excluded.state",
            params![state.call_id,state.session_id,state.updated_at.to_rfc3339(),encode(&state)?],
        ).map_err(db_error)?;
        Ok(())
    }

    fn segment_in(transaction: &Transaction<'_>, call_id: &str, event: &Event) -> Result<()> {
        let payload = &event.payload;
        let id = payload["segment_id"]
            .as_str()
            .ok_or_else(|| Error::new("PROTOCOL_ERROR", "Transcript segment ID is missing."))?;
        let old: Option<(u64, bool, bool, String)> = transaction.query_row(
            "SELECT revision,final,interrupted,payload FROM transcript_segments WHERE call_id=?1 AND segment_id=?2",
            params![call_id,id], |r| Ok((r.get(0)?,r.get(1)?,r.get(2)?,r.get(3)?))
        ).optional().map_err(db_error)?;
        let revision = payload["revision"].as_u64().unwrap_or(0);
        let final_segment = event.kind == "transcript.final" || payload["final"] == true;
        let mut interrupted =
            event.kind == "transcript.interrupted" || payload["interrupted"] == true;
        let mut merged = payload.clone();
        if let Some((old_revision, old_final, old_interrupted, old_payload)) = old {
            interrupted |= old_interrupted;
            if event.kind == "transcript.interrupted" {
                merged = decode(&old_payload)?;
                merged["interrupted"] = json!(true);
                merged["delivery"] = json!("unknown");
                transaction.execute(
                    "UPDATE transcript_segments SET interrupted=1,payload=?1 WHERE call_id=?2 AND segment_id=?3",
                    params![encode(&merged)?,call_id,id]
                ).map_err(db_error)?;
                return Ok(());
            }
            if revision < old_revision || old_final && !final_segment {
                return Ok(());
            }
        }
        if !merged["text"].is_string() || !merged["speaker"].is_string() {
            return Err(Error::new(
                "PROTOCOL_ERROR",
                "Transcript segment is incomplete.",
            ));
        }
        merged["interrupted"] = json!(interrupted);
        merged["final"] = json!(final_segment);
        if interrupted && merged["speaker"] == "assistant" {
            merged["delivery"] = json!("unknown");
        }
        transaction.execute(
            "INSERT INTO transcript_segments(call_id,segment_id,revision,final,interrupted,payload,first_sequence)
             VALUES(?1,?2,?3,?4,?5,?6,?7)
             ON CONFLICT(call_id,segment_id) DO UPDATE SET revision=excluded.revision,final=excluded.final,
               interrupted=excluded.interrupted,payload=excluded.payload",
            params![call_id,id,revision,final_segment,interrupted,encode(&merged)?,event.sequence]
        ).map_err(db_error)?;
        Ok(())
    }

    pub fn save_state(&mut self, state: &CallState) -> Result<()> {
        let transaction = self.connection.transaction().map_err(db_error)?;
        Self::state_in(&transaction, state, state.last_sequence)?;
        transaction.commit().map_err(db_error)
    }

    pub fn interrupt_session(&mut self, session: &str) -> Result<()> {
        let calls = self.states()?;
        let transaction = self.connection.transaction().map_err(db_error)?;
        for mut state in calls {
            if state.session_id != session || state.terminal() && state.hangup_status != "unknown" {
                continue;
            }
            let next: u64 = transaction
                .query_row(
                    "SELECT coalesce(max(sequence),0)+1 FROM events WHERE call_id=?1",
                    [&state.call_id],
                    |row| row.get(0),
                )
                .map_err(db_error)?;
            // This boundary marks possible loss, not a fabricated provider event.
            transaction.execute(
                "INSERT OR IGNORE INTO gaps(call_id,first_sequence,last_sequence) VALUES(?1,?2,?2)",
                params![state.call_id,next],
            ).map_err(db_error)?;
            state.lifecycle = crate::protocol::Lifecycle::TerminationUnknown;
            state.hangup_status = "unknown".into();
            state.transcript_status = "partial".into();
            if matches!(state.task_outcome.as_str(), "not_started" | "in_progress") {
                state.task_outcome = "unknown".into();
            }
            if state.summary_status == "pending" {
                state.summary_status = "unavailable".into();
            }
            state
                .termination_reason
                .get_or_insert_with(|| "owner_connection_lost".into());
            Self::state_in(&transaction, &state, state.last_sequence)?;
        }
        transaction.commit().map_err(db_error)
    }

    pub fn state(&self, id: &str) -> Result<CallState> {
        let text: String = self
            .connection
            .query_row("SELECT state FROM calls WHERE call_id=?1", [id], |r| {
                r.get(0)
            })
            .optional()
            .map_err(db_error)?
            .ok_or_else(|| Error::new("NOT_FOUND", "Call is not in encrypted history."))?;
        let mut state: CallState = decode(&text)?;
        let gap_count: u64 = self
            .connection
            .query_row("SELECT count(*) FROM gaps WHERE call_id=?1", [id], |r| {
                r.get(0)
            })
            .map_err(db_error)?;
        if gap_count > 0
            || state.transcript_status == "complete"
                && self.last_sequence(id)? < state.last_sequence
        {
            state.transcript_status = "partial".to_owned();
            if state.summary_status == "complete" {
                state.summary_status = "partial".into();
            }
        }
        Ok(state)
    }

    pub fn batch(&self, id: &str, after: u64) -> Result<EventBatch> {
        let mut statement = self.connection
            .prepare("SELECT event FROM events WHERE call_id=?1 AND sequence>?2 ORDER BY sequence LIMIT ?3")
            .map_err(db_error)?;
        let rows = statement
            .query_map(params![id, after, (BATCH_SIZE + 1) as u64], |r| {
                r.get::<_, String>(0)
            })
            .map_err(db_error)?;
        let mut events: Vec<Event> = Vec::new();
        let mut bytes = 0;
        let mut has_more = false;
        for row in rows {
            let text = row.map_err(db_error)?;
            if events.len() == BATCH_SIZE || bytes + text.len() > 3 * 1024 * 1024 {
                has_more = true;
                break;
            }
            bytes += text.len();
            events.push(decode(&text)?);
        }
        let next_cursor = events.last().map(|e| e.sequence).unwrap_or(after);
        let call_state = match self.state(id) {
            Ok(state) => Some(state),
            Err(error) if error.code == "NOT_FOUND" => None,
            Err(error) => return Err(error),
        };
        Ok(EventBatch {
            events,
            next_cursor,
            has_more,
            call_state,
        })
    }

    pub fn last_sequence(&self, id: &str) -> Result<u64> {
        self.connection
            .query_row(
                "SELECT coalesce(max(sequence),0) FROM events WHERE call_id=?1",
                [id],
                |r| r.get(0),
            )
            .map_err(db_error)
    }

    pub fn session_call_ids(&self, session: &str) -> Result<Vec<String>> {
        let mut statement = self
            .connection
            .prepare(
                "SELECT call_id FROM calls WHERE session_id=?1
                 UNION SELECT call_id FROM command_intents WHERE session_id=?1 AND call_id IS NOT NULL
                 UNION SELECT call_id FROM commands WHERE json_extract(receipt,'$.session_id')=?1 AND call_id IS NOT NULL",
            )
            .map_err(db_error)?;
        let result = statement
            .query_map([session], |r| r.get(0))
            .map_err(db_error)?
            .collect::<std::result::Result<_, _>>()
            .map_err(db_error);
        result
    }

    pub fn states(&self) -> Result<Vec<CallState>> {
        let mut statement = self
            .connection
            .prepare("SELECT call_id FROM calls ORDER BY updated_at DESC")
            .map_err(db_error)?;
        let ids: Vec<String> = statement
            .query_map([], |r| r.get(0))
            .map_err(db_error)?
            .collect::<std::result::Result<_, _>>()
            .map_err(db_error)?;
        ids.iter().map(|id| self.state(id)).collect()
    }

    pub fn transcript(&self, id: &str) -> Result<Value> {
        let state = self.state(id)?;
        let mut statement = self
            .connection
            .prepare(
                "SELECT payload FROM transcript_segments WHERE call_id=?1 ORDER BY first_sequence",
            )
            .map_err(db_error)?;
        let segments: Vec<Value> = statement
            .query_map([id], |r| r.get::<_, String>(0))
            .map_err(db_error)?
            .map(|row| decode(&row.map_err(db_error)?))
            .collect::<Result<_>>()?;
        let mut statement = self.connection.prepare(
            "SELECT first_sequence,last_sequence FROM gaps WHERE call_id=?1 ORDER BY first_sequence"
        ).map_err(db_error)?;
        let gaps: Vec<Value> = statement
            .query_map([id], |r| {
                Ok(json!({"first_sequence":r.get::<_,u64>(0)?,"last_sequence":r.get::<_,u64>(1)?}))
            })
            .map_err(db_error)?
            .collect::<std::result::Result<_, _>>()
            .map_err(db_error)?;
        Ok(
            json!({"call_id":id,"transcript_status":state.transcript_status,"segments":segments,"gaps":gaps}),
        )
    }

    pub fn history(&self, id: &str) -> Result<Value> {
        let state = self.state(id)?;
        let brief: Option<String> = self
            .connection
            .query_row("SELECT brief FROM calls WHERE call_id=?1", [id], |r| {
                r.get(0)
            })
            .map_err(db_error)?;
        let summary: Option<String> = self
            .connection
            .query_row(
                "SELECT payload FROM summaries WHERE call_id=?1",
                [id],
                |r| r.get(0),
            )
            .optional()
            .map_err(db_error)?;
        Ok(
            json!({"state":state,"brief":brief,"summary":summary.map(|s| decode::<Value>(&s)).transpose()?}),
        )
    }

    pub fn approvals(&self, id: &str) -> Result<Vec<Value>> {
        let mut statement = self
            .connection
            .prepare("SELECT payload FROM approvals WHERE call_id=?1 ORDER BY approval_id")
            .map_err(db_error)?;
        let approvals = statement
            .query_map([id], |r| r.get::<_, String>(0))
            .map_err(db_error)?
            .map(|row| decode(&row.map_err(db_error)?))
            .collect();
        approvals
    }

    pub fn approval_call(&self, id: &str) -> Result<String> {
        self.connection
            .query_row(
                "SELECT call_id FROM approvals WHERE approval_id=?1",
                [id],
                |r| r.get(0),
            )
            .optional()
            .map_err(db_error)?
            .ok_or_else(|| Error::new("NOT_FOUND", "Approval is not in encrypted history."))
    }

    pub fn prune(&mut self, before: chrono::DateTime<chrono::Utc>, dry_run: bool) -> Result<Value> {
        let ids: Vec<String> = self
            .states()?
            .iter()
            .filter(|state| {
                state.updated_at < before && state.terminal() && state.hangup_status != "unknown"
            })
            .map(|s| s.call_id.clone())
            .collect();
        if !dry_run {
            let transaction = self.connection.transaction().map_err(db_error)?;
            for id in &ids {
                for table in [
                    "events",
                    "transcript_segments",
                    "summaries",
                    "approvals",
                    "commands",
                    "command_intents",
                    "pending_briefs",
                    "gaps",
                    "calls",
                ] {
                    transaction
                        .execute(&format!("DELETE FROM {table} WHERE call_id=?1"), [id])
                        .map_err(db_error)?;
                }
            }
            transaction.commit().map_err(db_error)?;
            self.connection
                .execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")
                .map_err(db_error)?;
        }
        Ok(json!({"dry_run":dry_run,"before":before,"call_ids":ids,"count":ids.len()}))
    }

    pub fn backup(&self, path: &Path, key: &CipherKey) -> Result<()> {
        if path.exists() {
            return Err(Error::new("CONFLICT", "Backup destination already exists."));
        }
        let mut target = Store::open(path, key, true)?;
        let backup = rusqlite::backup::Backup::new(&self.connection, &mut target.connection)
            .map_err(db_error)?;
        backup
            .run_to_completion(64, Duration::from_millis(10), None)
            .map_err(db_error)?;
        drop(backup);
        target.integrity_check()?;
        target
            .connection
            .execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")
            .map_err(db_error)
    }
}

fn require_cipher(version: Option<&str>) -> Result<()> {
    if version.is_none_or(|v| !v.starts_with("4.")) {
        return Err(Error::new(
            "SQLCIPHER_UNAVAILABLE",
            "SQLCipher 4 is required. Plain SQLite is never supported.",
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::Lifecycle;
    use chrono::Utc;
    use std::os::unix::fs::PermissionsExt;

    fn temporary_store() -> (tempfile::TempDir, Store) {
        let dir = tempfile::tempdir().unwrap();
        std::fs::set_permissions(dir.path(), std::fs::Permissions::from_mode(0o700)).unwrap();
        let store = Store::open(
            &dir.path().join("history.db"),
            &CipherKey::from_bytes([42; 32]),
            true,
        )
        .unwrap();
        (dir, store)
    }

    fn event(seq: u64, kind: &str, payload: Value) -> Event {
        Event {
            schema_version: "1".into(),
            event_id: format!("event-{seq}"),
            session_id: "session-1".into(),
            call_id: Some("call-1".into()),
            sequence: seq,
            timestamp: Utc::now(),
            kind: kind.into(),
            command_id: None,
            payload,
        }
    }

    fn state() -> CallState {
        CallState {
            call_id: "call-1".into(),
            session_id: "session-1".into(),
            target: "pstn:+12025550123".into(),
            source: "simulation".into(),
            route: "simulation".into(),
            provider_mode: "local-fake".into(),
            created_at: Utc::now(),
            updated_at: Utc::now(),
            deadline: Utc::now(),
            lifecycle: Lifecycle::Connected,
            task_outcome: "in_progress".into(),
            termination_reason: None,
            hangup_status: "pending".into(),
            transcript_status: "complete".into(),
            summary_status: "pending".into(),
            last_sequence: 1,
        }
    }

    #[test]
    fn real_cipher_wrong_key_wal_backup_and_plain_sqlite_header() {
        let (dir, mut store) = temporary_store();
        store
            .ingest(&event(1, "call.state_changed", json!({"state":state()})))
            .unwrap();
        let marker = "PRIVATE_TRANSCRIPT_CIPHER_MARKER";
        store
            .ingest(&event(
                2,
                "transcript.final",
                json!({
                    "segment_id":"s1","revision":1,"final":true,"interrupted":false,
                    "speaker":"recipient","text":marker,"delivery":"received"
                }),
            ))
            .unwrap();
        for file in ["history.db", "history.db-wal"] {
            let bytes = std::fs::read(dir.path().join(file)).unwrap();
            assert!(!bytes.starts_with(b"SQLite format 3"));
            assert!(!bytes.windows(marker.len()).any(|v| v == marker.as_bytes()));
        }
        assert!(Store::open(
            &dir.path().join("history.db"),
            &CipherKey::from_bytes([41; 32]),
            false
        )
        .is_err());
        let restored = Store::open(
            &dir.path().join("history.db"),
            &CipherKey::from_bytes([42; 32]),
            false,
        )
        .unwrap();
        assert_eq!(
            restored.transcript("call-1").unwrap()["segments"][0]["text"],
            marker
        );
        let backup = dir.path().join("backup.db");
        store
            .backup(&backup, &CipherKey::from_bytes([42; 32]))
            .unwrap();
        let restored = Store::open(&backup, &CipherKey::from_bytes([42; 32]), false).unwrap();
        assert_eq!(
            restored.transcript("call-1").unwrap()["segments"][0]["text"],
            marker
        );
        assert!(!std::fs::read(backup)
            .unwrap()
            .starts_with(b"SQLite format 3"));
        store.integrity_check().unwrap();
    }

    #[test]
    fn atomic_dedup_revisions_interruption_and_gaps() {
        let (_dir, mut store) = temporary_store();
        store
            .ingest(&event(1, "call.state_changed", json!({"state":state()})))
            .unwrap();
        let segment = event(
            2,
            "transcript.final",
            json!({
                "segment_id":"s1","revision":2,"speaker":"assistant","text":"Generated words","delivery":"generated"
            }),
        );
        assert!(store.ingest(&segment).unwrap());
        assert!(!store.ingest(&segment).unwrap());
        store.ingest(&event(3,"transcript.partial",json!({
            "segment_id":"s1","revision":1,"speaker":"assistant","text":"Generated","delivery":"generated"
        }))).unwrap();
        store
            .ingest(&event(
                5,
                "transcript.interrupted",
                json!({"segment_id":"s1"}),
            ))
            .unwrap();
        let transcript = store.transcript("call-1").unwrap();
        assert_eq!(transcript["segments"].as_array().unwrap().len(), 1);
        assert_eq!(transcript["segments"][0]["text"], "Generated words");
        assert_eq!(transcript["segments"][0]["interrupted"], true);
        assert_eq!(transcript["transcript_status"], "partial");
        assert_eq!(store.batch("call-1", 2).unwrap().events.len(), 2);
        assert_eq!(store.batch("call-1", 99).unwrap().next_cursor, 99);
    }

    #[test]
    fn unavailable_write_fails_without_acknowledgeable_event() {
        let (_dir, mut store) = temporary_store();
        store
            .connection
            .execute_batch("PRAGMA query_only=ON")
            .unwrap();
        assert!(store
            .ingest(&event(1, "call.warning", json!({"code":"TEST"})))
            .is_err());
        assert!(store.batch("call-1", 0).unwrap().events.is_empty());
    }

    #[test]
    fn cipher_absence_and_disk_full_fail_closed() {
        assert!(require_cipher(None).is_err());
        assert!(require_cipher(Some("")).is_err());
        assert!(require_cipher(Some("3.0")).is_err());
        let (_dir, mut store) = temporary_store();
        let pages: u64 = store
            .connection
            .query_row("PRAGMA page_count", [], |r| r.get(0))
            .unwrap();
        store
            .connection
            .execute_batch(&format!("PRAGMA max_page_count={pages}"))
            .unwrap();
        assert!(store
            .ingest(&event(
                1,
                "transcript.final",
                json!({
                    "segment_id":"s1","revision":1,"speaker":"recipient",
                    "text":"x".repeat(100_000),"delivery":"received"
                }),
            ))
            .is_err());
        assert!(store.batch("call-1", 0).unwrap().events.is_empty());
    }

    #[test]
    fn migration_and_targeted_pruning_preserve_unresolved_attempts() {
        let (dir, store) = temporary_store();
        store
            .connection
            .execute_batch(
                "DROP TABLE pending_briefs; ALTER TABLE command_intents DROP COLUMN call_id;
             DELETE FROM schema_migrations WHERE version=2;",
            )
            .unwrap();
        drop(store);
        assert!(Store::open(
            &dir.path().join("history.db"),
            &CipherKey::from_bytes([42; 32]),
            false
        )
        .is_err());
        let mut store = Store::open(
            &dir.path().join("history.db"),
            &CipherKey::from_bytes([42; 32]),
            true,
        )
        .unwrap();
        let command = CommandRequest {
            schema_version: "1".into(),
            session_id: "session-1".into(),
            operation: "calls.start".into(),
            idempotency_key: "intent-1".into(),
            call_id: None,
            payload: json!({
                "target":"pstn:+12025550123","task":"brief-prune-marker"
            }),
        };
        store.save_intent(&command).unwrap();
        let receipt = CommandReceipt {
            schema_version: "1".into(),
            command_id: "command-1".into(),
            session_id: "session-1".into(),
            call_id: Some("call-1".into()),
            operation: "calls.start".into(),
            status: "succeeded".into(),
            accepted_at: Utc::now(),
            updated_at: Utc::now(),
            error: None,
            result: None,
        };
        store.link_receipt(&command, &receipt).unwrap();
        let mut ended = state();
        ended.lifecycle = Lifecycle::Ended;
        ended.hangup_status = "confirmed".into();
        store.save_state(&ended).unwrap();
        assert_eq!(
            store.history("call-1").unwrap()["brief"],
            "brief-prune-marker"
        );
        let mut unresolved = command;
        unresolved.idempotency_key = "intent-unknown".into();
        store.save_intent(&unresolved).unwrap();
        let before = Utc::now() + chrono::Duration::days(1);
        assert_eq!(store.prune(before, true).unwrap()["count"], 1);
        assert!(store.history("call-1").is_ok());
        assert_eq!(store.prune(before, false).unwrap()["count"], 1);
        assert!(store.history("call-1").is_err());
        let intents: u64 = store
            .connection
            .query_row("SELECT count(*) FROM command_intents", [], |r| r.get(0))
            .unwrap();
        assert_eq!(intents, 1);
    }

    #[test]
    fn event_before_receipt_does_not_regress_command_completion() {
        let (_dir, mut store) = temporary_store();
        let receipt = CommandReceipt {
            schema_version: "1".into(),
            command_id: "command-1".into(),
            session_id: "session-1".into(),
            call_id: Some("call-1".into()),
            operation: "calls.start".into(),
            status: "succeeded".into(),
            accepted_at: Utc::now(),
            updated_at: Utc::now(),
            error: None,
            result: None,
        };
        store
            .ingest(&event(1, "command.succeeded", json!({"receipt":receipt})))
            .unwrap();
        let mut late = receipt;
        late.status = "accepted".into();
        store.save_receipt(&late).unwrap();
        assert_eq!(store.receipt("command-1").unwrap().status, "succeeded");
    }

    #[test]
    fn queried_approvals_route_before_events_and_do_not_regress_resolution() {
        let (_dir, mut store) = temporary_store();
        let approval = json!({
            "approval_id":"approval-1","call_id":"call-1","status":"approved",
            "action_hash":"sha256:example","actor":"authorized-operator"
        });
        store.cache_approvals("call-1", &json!([approval])).unwrap();
        assert_eq!(store.approval_call("approval-1").unwrap(), "call-1");
        let mut pending = approval;
        pending["status"] = json!("pending");
        pending["actor"] = Value::Null;
        store
            .ingest(&event(1, "approval.requested", pending.clone()))
            .unwrap();
        let stored = store.approvals("call-1").unwrap();
        assert_eq!(stored[0]["status"], "approved");
        assert_eq!(stored[0]["actor"], "authorized-operator");
        pending["call_id"] = json!("call-2");
        assert_eq!(
            store
                .cache_approvals("call-2", &json!([pending]))
                .unwrap_err()
                .code,
            "EVENT_CONFLICT"
        );
    }

    #[test]
    fn lost_control_marks_local_uncertainty_without_inventing_provider_events() {
        let (_dir, mut store) = temporary_store();
        store
            .ingest(&event(1, "call.state", json!({"state":state()})))
            .unwrap();
        store.interrupt_session("session-1").unwrap();
        let observed = store.state("call-1").unwrap();
        assert_eq!(observed.lifecycle, Lifecycle::TerminationUnknown);
        assert_eq!(observed.transcript_status, "partial");
        assert_eq!(observed.hangup_status, "unknown");
        assert_eq!(store.batch("call-1", 0).unwrap().events.len(), 1);
        let mut ended = observed;
        ended.lifecycle = Lifecycle::Ended;
        ended.hangup_status = "confirmed".into();
        ended.last_sequence = 2;
        ended.transcript_status = "complete".into();
        store
            .ingest(&event(2, "call.state", json!({"state":ended})))
            .unwrap();
        assert_eq!(store.state("call-1").unwrap().hangup_status, "confirmed");
        assert_eq!(store.state("call-1").unwrap().transcript_status, "partial");
        store.interrupt_session("session-1").unwrap();
        assert_eq!(store.state("call-1").unwrap().lifecycle, Lifecycle::Ended);
    }
}
