use crate::{Error, Result};
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;

pub const VERSION: &str = "1";
pub const MAX_MESSAGE: usize = 256 * 1024;
pub const MAX_REQUEST: usize = 64 * 1024;
pub const BATCH_SIZE: usize = 128;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CommandRequest {
    pub schema_version: String,
    pub session_id: String,
    pub operation: String,
    pub idempotency_key: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub call_id: Option<String>,
    pub payload: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CommandReceipt {
    pub schema_version: String,
    pub command_id: String,
    pub session_id: String,
    pub call_id: Option<String>,
    pub operation: String,
    pub status: String,
    pub accepted_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<Error>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionInfo {
    pub schema_version: String,
    pub session_id: String,
    pub status: String,
    pub lease_expires_at: DateTime<Utc>,
    pub provider_mode: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Lifecycle {
    Accepted,
    Preflight,
    Dialing,
    Connected,
    Ending,
    Ended,
    FailedBeforeConnect,
    TerminationUnknown,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CallState {
    pub call_id: String,
    pub session_id: String,
    pub target: String,
    pub source: String,
    pub route: String,
    pub provider_mode: String,
    pub created_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
    pub deadline: DateTime<Utc>,
    pub lifecycle: Lifecycle,
    pub task_outcome: String,
    pub termination_reason: Option<String>,
    pub hangup_status: String,
    pub transcript_status: String,
    pub summary_status: String,
    pub last_sequence: u64,
}

impl CallState {
    pub fn terminal(&self) -> bool {
        matches!(
            self.lifecycle,
            Lifecycle::Ended | Lifecycle::FailedBeforeConnect | Lifecycle::TerminationUnknown
        )
    }

    pub fn success(&self) -> bool {
        self.lifecycle == Lifecycle::Ended
            && self.task_outcome == "completed"
            && self.hangup_status == "confirmed"
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Event {
    pub schema_version: String,
    pub event_id: String,
    pub session_id: String,
    pub call_id: Option<String>,
    pub sequence: u64,
    pub timestamp: DateTime<Utc>,
    #[serde(rename = "type")]
    pub kind: String,
    pub command_id: Option<String>,
    pub payload: Value,
}

impl Event {
    pub fn validate(&self) -> Result<()> {
        if self.schema_version != VERSION {
            return Err(Error::new("PROTOCOL_VERSION", "Unsupported event version."));
        }
        if self.event_id.is_empty()
            || self.session_id.is_empty()
            || !self.payload.is_object()
            || self.call_id.is_some() && self.sequence == 0
        {
            return Err(Error::new(
                "PROTOCOL_ERROR",
                "Invalid runtime event envelope.",
            ));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EventBatch {
    pub events: Vec<Event>,
    pub next_cursor: u64,
    pub has_more: bool,
    pub call_state: Option<CallState>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Approval {
    pub approval_id: String,
    pub call_id: String,
    pub action_hash: String,
    pub expires_at: DateTime<Utc>,
    pub status: String,
    pub conversation_version: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub material_terms: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub actor: Option<String>,
}

pub fn valid_id(id: &str) -> Result<()> {
    if id.is_empty()
        || id.len() > 128
        || !id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    {
        return Err(Error::new("INVALID_INPUT", "Invalid identifier."));
    }
    Ok(())
}

pub fn validate_start(payload: &Value) -> Result<()> {
    let target = payload["target"]
        .as_str()
        .ok_or_else(|| Error::new("INVALID_TARGET", "A tagged destination is required."))?;
    let valid = if let Some(number) = target.strip_prefix("pstn:+") {
        (7..=15).contains(&number.len())
            && !number.starts_with('0')
            && number.bytes().all(|b| b.is_ascii_digit())
    } else if let Some(id) = target.strip_prefix("teams:") {
        id.len() == 36 && uuid::Uuid::parse_str(id).is_ok()
    } else {
        false
    };
    if !valid {
        return Err(Error::new(
            "INVALID_TARGET",
            "Use pstn:+E164 or teams:<Entra-object-UUID>; no implicit route.",
        ));
    }
    if !payload["task"]
        .as_str()
        .is_some_and(|s| !s.trim().is_empty() && s.len() <= 16_384)
    {
        return Err(Error::new(
            "INVALID_INPUT",
            "Task must contain 1..16384 UTF-8 bytes.",
        ));
    }
    if let Some(duration) = payload.get("max_duration_seconds") {
        if !duration.as_u64().is_some_and(|v| (30..=3600).contains(&v)) {
            return Err(Error::new(
                "INVALID_INPUT",
                "Duration must be an integer from 30 through 3600 seconds.",
            ));
        }
    }
    if payload
        .get("allow_voicemail")
        .is_some_and(|value| !value.is_boolean())
    {
        return Err(Error::new(
            "INVALID_INPUT",
            "allow_voicemail must be boolean.",
        ));
    }
    if payload.as_object().is_some_and(|o| {
        o.keys().any(|k| {
            !matches!(
                k.as_str(),
                "target" | "task" | "max_duration_seconds" | "allow_voicemail"
            )
        })
    }) {
        return Err(Error::new("INVALID_INPUT", "Unknown call request field."));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shared_fixtures_round_trip() {
        let command: CommandRequest =
            serde_json::from_str(include_str!("../../../contracts/v1/fixtures/command.json"))
                .unwrap();
        validate_start(&command.payload).unwrap();
        let event: Event =
            serde_json::from_str(include_str!("../../../contracts/v1/fixtures/event.json"))
                .unwrap();
        event.validate().unwrap();
        let encoded = serde_json::to_value(&event).unwrap();
        assert_eq!(encoded["type"], "transcript.final");
        assert_eq!(encoded["sequence"], 8);
    }

    #[test]
    fn all_shared_fixtures_satisfy_the_versioned_schema() {
        let source: Value =
            serde_json::from_str(include_str!("../../../contracts/v1/protocol.schema.json"))
                .unwrap();
        let fixtures =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../contracts/v1/fixtures");
        for path in std::fs::read_dir(fixtures).unwrap() {
            let path = path.unwrap().path();
            if path.extension().and_then(|s| s.to_str()) != Some("json") {
                continue;
            }
            let name = path.file_stem().unwrap().to_str().unwrap();
            let kind = name.split('-').next().unwrap();
            let mut schema = source.clone();
            schema["$ref"] = serde_json::json!(format!("#/$defs/{kind}"));
            let validator = jsonschema::validator_for(&schema).unwrap();
            let value: Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
            assert!(validator.is_valid(&value), "Fixture failed schema: {name}");
            if kind == "event" {
                let mut invalid = value;
                invalid["schema_version"] = serde_json::json!("2");
                assert!(!validator.is_valid(&invalid));
            }
        }
    }

    #[test]
    fn reject_implicit_and_invalid_routes() {
        for target in ["+12025550123", "pstn:+02025550123", "teams:alice", "sip:x"] {
            assert!(validate_start(&serde_json::json!({"target":target,"task":"test"})).is_err());
        }
    }

    #[test]
    fn completed_task_is_not_a_confirmed_hangup() {
        let mut state: CallState =
            serde_json::from_str(include_str!("../../../contracts/v1/fixtures/state.json"))
                .unwrap();
        state.task_outcome = "completed".into();
        assert!(!state.success());
        state.lifecycle = Lifecycle::Ended;
        assert!(!state.success());
        state.hangup_status = "confirmed".into();
        assert!(state.success());
        state.task_outcome = "partial".into();
        assert!(!state.success());
    }
}
