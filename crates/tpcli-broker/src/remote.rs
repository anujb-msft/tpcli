use crate::{
    auth,
    config::Profile,
    protocol::{CallState, CommandReceipt, CommandRequest, EventBatch, SessionInfo, MAX_MESSAGE},
    Error, Result,
};
use reqwest::Method;
use serde::{de::DeserializeOwned, Deserialize};
use serde_json::{json, Value};
use std::time::Duration;
use tokio::net::TcpStream;
use tokio_tungstenite::{
    tungstenite::{client::IntoClientRequest, http::HeaderValue, protocol::WebSocketConfig},
    MaybeTlsStream, WebSocketStream,
};

pub type ControlSocket = WebSocketStream<MaybeTlsStream<TcpStream>>;

#[derive(Clone)]
pub struct Remote {
    profile: Profile,
    client: reqwest::Client,
}

#[derive(Deserialize)]
struct ErrorResponse {
    error: Error,
}

impl Remote {
    pub fn new(profile: Profile) -> Result<Self> {
        let mut builder = reqwest::Client::builder()
            .timeout(Duration::from_secs(35))
            .connect_timeout(Duration::from_secs(5))
            .redirect(reqwest::redirect::Policy::none());
        if profile.provider_mode == "local-fake" {
            builder = builder.no_proxy();
        }
        let client = builder
            .build()
            .map_err(|_| Error::new("CONFIGURATION", "Cannot initialize runtime HTTPS client."))?;
        Ok(Self { profile, client })
    }

    async fn request<T: DeserializeOwned>(
        &self,
        method: Method,
        path: &str,
        body: Option<&Value>,
    ) -> Result<T> {
        let token = auth::bearer(&self.profile).await?;
        let url = format!("{}{}", self.profile.runtime_url.trim_end_matches('/'), path);
        let mut request = self.client.request(method, url).bearer_auth(token.as_str());
        if let Some(body) = body {
            request = request.json(body);
        }
        let mut response = request.send().await.map_err(|_| {
            Error::new(
                "RUNTIME_UNREACHABLE",
                "Runtime request did not complete. Do not redial with a new key; recover the original receipt.",
            )
        })?;
        let status = response.status();
        let mut bytes = Vec::new();
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|_| Error::new("RUNTIME_UNREACHABLE", "Runtime response was interrupted."))?
        {
            if bytes.len() + chunk.len() > 4 * 1024 * 1024 {
                return Err(Error::new(
                    "PROTOCOL_ERROR",
                    "Runtime response exceeds the bounded limit.",
                ));
            }
            bytes.extend_from_slice(&chunk);
        }
        if !status.is_success() {
            if let Ok(error) = serde_json::from_slice::<ErrorResponse>(&bytes) {
                return Err(error.error);
            }
            return Err(match status.as_u16() {
                401 | 403 => Error::new(
                    "AUTH_REQUIRED",
                    "Runtime authentication or authorization failed.",
                ),
                404 => Error::new("NOT_FOUND", "Runtime record was not found."),
                409 => Error::new("CONFLICT", "Runtime rejected conflicting or stale state."),
                _ => Error::new(
                    "RUNTIME_FAILED",
                    "Runtime rejected the request; payload is not logged.",
                ),
            });
        }
        serde_json::from_slice(&bytes).map_err(|_| {
            Error::new(
                "PROTOCOL_ERROR",
                "Runtime returned an invalid protocol response.",
            )
        })
    }

    pub async fn capabilities(&self, online: bool) -> Result<Value> {
        self.request(
            Method::GET,
            if online {
                "/v1/capabilities?online=true"
            } else {
                "/v1/capabilities"
            },
            None,
        )
        .await
    }

    pub async fn create_session(&self) -> Result<SessionInfo> {
        let session: SessionInfo = self
            .request(Method::POST, "/v1/sessions", Some(&json!({})))
            .await?;
        if session.schema_version != "1" || session.provider_mode != self.profile.provider_mode {
            return Err(Error::new(
                "PROTOCOL_ERROR",
                "Runtime version/provider differs from the configured profile; no call will be submitted.",
            ));
        }
        crate::protocol::valid_id(&session.session_id)?;
        Ok(session)
    }

    pub async fn close_session(&self, session: &str) -> Result<Value> {
        self.request(Method::DELETE, &format!("/v1/sessions/{session}"), None)
            .await
    }

    pub async fn submit(&self, command: &CommandRequest) -> Result<CommandReceipt> {
        let body = serde_json::to_value(command)
            .map_err(|_| Error::new("INVALID_INPUT", "Cannot encode command."))?;
        self.request(Method::POST, "/v1/commands", Some(&body))
            .await
    }

    pub async fn command(&self, id: &str) -> Result<CommandReceipt> {
        crate::protocol::valid_id(id)?;
        self.request(Method::GET, &format!("/v1/commands/{id}"), None)
            .await
    }

    pub async fn state(&self, id: &str) -> Result<CallState> {
        crate::protocol::valid_id(id)?;
        self.request(Method::GET, &format!("/v1/calls/{id}"), None)
            .await
    }

    pub async fn events(&self, id: &str, after: u64) -> Result<EventBatch> {
        crate::protocol::valid_id(id)?;
        self.request(
            Method::GET,
            &format!("/v1/calls/{id}/events?after={after}"),
            None,
        )
        .await
    }

    pub async fn approvals(&self, id: &str) -> Result<Value> {
        crate::protocol::valid_id(id)?;
        self.request(Method::GET, &format!("/v1/calls/{id}/approvals"), None)
            .await
    }

    pub async fn control(&self, session: &str) -> Result<ControlSocket> {
        let token = auth::bearer(&self.profile).await?;
        let mut url = url::Url::parse(&self.profile.runtime_url)
            .map_err(|_| Error::new("CONFIGURATION", "Invalid runtime URL."))?;
        let scheme = if url.scheme() == "https" { "wss" } else { "ws" };
        url.set_scheme(scheme)
            .map_err(|_| Error::new("CONFIGURATION", "Invalid control URL."))?;
        url.set_path(&format!("/v1/sessions/{session}/control"));
        let mut request = url
            .as_str()
            .into_client_request()
            .map_err(|_| Error::new("PROTOCOL_ERROR", "Cannot create control request."))?;
        request.headers_mut().insert(
            "Authorization",
            HeaderValue::from_str(&format!("Bearer {}", token.as_str()))
                .map_err(|_| Error::new("AUTH_FAILED", "Invalid bearer credential."))?,
        );
        let config = WebSocketConfig::default()
            .max_message_size(Some(MAX_MESSAGE))
            .max_frame_size(Some(MAX_MESSAGE));
        let (socket, _) = tokio::time::timeout(
            Duration::from_secs(5),
            tokio_tungstenite::connect_async_with_config(request, Some(config), false),
        )
        .await
        .map_err(|_| Error::new("RUNTIME_UNREACHABLE", "Control connection timed out."))?
        .map_err(|_| {
            Error::new(
                "AUTH_FAILED",
                "Authenticated control connection was refused.",
            )
        })?;
        Ok(socket)
    }
}
