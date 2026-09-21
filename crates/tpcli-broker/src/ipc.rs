use crate::{
    config::socket_path,
    protocol::{MAX_MESSAGE, MAX_REQUEST},
    Error, Result,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tokio::{
    io::{AsyncBufRead, AsyncBufReadExt, AsyncWrite, AsyncWriteExt, BufReader},
    net::{unix::OwnedReadHalf, UnixStream},
};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Request {
    pub schema_version: String,
    pub session_id: String,
    pub op: String,
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub operation: String,
    #[serde(default)]
    pub idempotency_key: String,
    #[serde(default)]
    pub payload: Value,
    #[serde(default)]
    pub after: u64,
    #[serde(default)]
    pub follow: bool,
    #[serde(default)]
    pub wait_seconds: u64,
}

impl Request {
    pub fn new(session: &str, op: &str) -> Self {
        Self {
            schema_version: "1".into(),
            session_id: session.into(),
            op: op.into(),
            id: String::new(),
            operation: String::new(),
            idempotency_key: String::new(),
            payload: Value::Null,
            after: 0,
            follow: false,
            wait_seconds: 0,
        }
    }
}

pub async fn read_frame<R: AsyncBufRead + Unpin>(
    reader: &mut R,
    max: usize,
) -> Result<Option<Vec<u8>>> {
    let mut data = Vec::new();
    loop {
        let available = reader
            .fill_buf()
            .await
            .map_err(|_| Error::new("IPC_DISCONNECTED", "IPC connection closed."))?;
        if available.is_empty() {
            if data.is_empty() {
                return Ok(None);
            }
            return Err(Error::new("PROTOCOL_ERROR", "Incomplete IPC frame."));
        }
        let end = available.iter().position(|b| *b == b'\n');
        let count = end.map_or(available.len(), |i| i + 1);
        if data.len() + count > max {
            return Err(Error::new(
                "INVALID_INPUT",
                "IPC message exceeds the size limit.",
            ));
        }
        data.extend_from_slice(&available[..count]);
        reader.consume(count);
        if end.is_some() {
            return Ok(Some(data));
        }
    }
}

pub async fn write_frame<W: AsyncWrite + Unpin>(writer: &mut W, value: &Value) -> Result<()> {
    let mut encoded = serde_json::to_vec(value)
        .map_err(|_| Error::new("PROTOCOL_ERROR", "Cannot encode IPC response."))?;
    if encoded.len() > 4 * 1024 * 1024 {
        return Err(Error::new(
            "PROTOCOL_ERROR",
            "IPC response exceeds the bounded limit.",
        ));
    }
    encoded.push(b'\n');
    tokio::time::timeout(
        std::time::Duration::from_secs(2),
        writer.write_all(&encoded),
    )
    .await
    .map_err(|_| {
        Error::new(
            "SUBSCRIBER_SLOW",
            "Subscriber stalled; resume from its last committed cursor.",
        )
    })?
    .map_err(|_| Error::new("IPC_DISCONNECTED", "IPC consumer detached."))?;
    Ok(())
}

pub async fn connect(request: &Request) -> Result<BufReader<OwnedReadHalf>> {
    let path = socket_path(&request.session_id)?;
    let stream = UnixStream::connect(path).await.map_err(|_| {
        Error::new(
            "SESSION_REQUIRED",
            "No live session broker. Use session exec/run, or query explicit offline history.",
        )
    })?;
    if stream
        .peer_cred()
        .map_err(|_| Error::new("IPC_PERMISSIONS", "Cannot authenticate broker peer."))?
        .uid()
        != unsafe { libc::geteuid() }
    {
        return Err(Error::new(
            "IPC_PERMISSIONS",
            "Broker peer belongs to another OS user.",
        ));
    }
    let (read, mut write) = stream.into_split();
    let value = serde_json::to_value(request)
        .map_err(|_| Error::new("INVALID_INPUT", "Cannot encode IPC request."))?;
    if serde_json::to_vec(&value)
        .map_err(|_| Error::new("INVALID_INPUT", "Cannot encode request."))?
        .len()
        > MAX_REQUEST
    {
        return Err(Error::new("INVALID_INPUT", "Request exceeds 64 KiB."));
    }
    write_frame(&mut write, &value).await?;
    write
        .shutdown()
        .await
        .map_err(|_| Error::new("IPC_DISCONNECTED", "IPC connection closed."))?;
    Ok(BufReader::new(read))
}

pub fn result(value: Value) -> Result<Value> {
    if let Some(error) = value
        .as_object()
        .filter(|object| object.len() == 1)
        .and_then(|object| object.get("error"))
    {
        return Err(serde_json::from_value(error.clone())
            .unwrap_or_else(|_| Error::new("PROTOCOL_ERROR", "Invalid broker error response.")));
    }

    Ok(value)
}

pub async fn call(request: &Request) -> Result<Value> {
    let mut reader = connect(request).await?;
    let frame = tokio::time::timeout(
        std::time::Duration::from_secs(request.wait_seconds.min(30) + 40),
        read_frame(&mut reader, 4 * 1024 * 1024),
    )
    .await
    .map_err(|_| {
        Error::new(
            "IPC_TIMEOUT",
            "Broker did not return a receipt; retry only with the same key.",
        )
    })??
    .ok_or_else(|| {
        Error::new(
            "IPC_DISCONNECTED",
            "Broker closed before returning a response.",
        )
    })?;
    result(
        serde_json::from_slice(&frame)
            .map_err(|_| Error::new("PROTOCOL_ERROR", "Invalid IPC response."))?,
    )
}

pub async fn next_event(reader: &mut BufReader<OwnedReadHalf>) -> Result<Option<Value>> {
    let Some(frame) = read_frame(reader, MAX_MESSAGE).await? else {
        return Ok(None);
    };
    result(
        serde_json::from_slice(&frame)
            .map_err(|_| Error::new("PROTOCOL_ERROR", "Invalid event stream."))?,
    )
    .map(Some)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn failed_receipts_retain_ids_instead_of_becoming_transport_errors() {
        let receipt: Value = serde_json::from_str(include_str!(
            "../../../contracts/v1/fixtures/receipt-failed.json"
        ))
        .unwrap();
        let decoded = result(receipt).unwrap();
        assert_eq!(decoded["status"], "failed");
        assert!(decoded["command_id"].is_string());
        assert_eq!(decoded["error"]["code"], "CREATE_AMBIGUOUS");
        assert!(result(serde_json::json!({
            "error":{"code":"STALE_APPROVAL","message":"Stale approval.","retryable":false}
        }))
        .is_err());
    }
}
