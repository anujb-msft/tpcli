use serde::{Deserialize, Serialize};

pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Error {
    pub code: String,
    pub message: String,
    #[serde(default)]
    pub retryable: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub next_cursor: Option<u64>,
}

impl Error {
    pub fn new(code: &str, message: &str) -> Self {
        Self {
            code: code.to_owned(),
            message: message.to_owned(),
            retryable: false,
            next_cursor: None,
        }
    }

    pub fn exit_code(&self) -> u8 {
        match self.code.as_str() {
            "INVALID_INPUT" | "INVALID_TARGET" | "PROTOCOL_VERSION" => 2,
            "SESSION_REQUIRED"
            | "CONFIGURATION"
            | "AUTH_REQUIRED"
            | "AUTH_FAILED"
            | "AUTH_PENDING"
            | "KEY_UNAVAILABLE"
            | "KEYSTORE_UNSUPPORTED"
            | "IPC_PERMISSIONS"
            | "CAPABILITY_UNSUPPORTED"
            | "READINESS_REQUIRED"
            | "TEST_MODE_REQUIRED" => 3,
            "TASK_INCOMPLETE" | "TERMINATION_UNKNOWN" => 5,
            "CONFLICT"
            | "IDEMPOTENCY_CONFLICT"
            | "APPROVAL_STALE"
            | "STALE_APPROVAL"
            | "APPROVAL_EXPIRED"
            | "SESSION_REVOKED"
            | "CALL_ACTIVE" => 6,
            _ => 4,
        }
    }

    pub fn json(&self) -> serde_json::Value {
        serde_json::json!({ "error": self })
    }
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for Error {}

pub(crate) fn db_error(_: rusqlite::Error) -> Error {
    Error::new(
        "ENCRYPTED_STORE_FAILED",
        "Encrypted storage failed; check the key, available disk space, and permissions. Supervision cannot continue without durable ingestion.",
    )
}
