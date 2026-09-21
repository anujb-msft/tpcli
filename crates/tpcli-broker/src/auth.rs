use crate::{config::Profile, Error, Result};
use chrono::{DateTime, Duration, Utc};
#[cfg(target_os = "macos")]
use rand::RngCore;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use zeroize::Zeroizing;

pub struct CipherKey(Zeroizing<[u8; 32]>);

impl CipherKey {
    pub fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(Zeroizing::new(bytes))
    }

    pub fn hex(&self) -> Zeroizing<String> {
        Zeroizing::new(hex::encode(self.0.as_ref()))
    }
}

#[derive(Serialize, Deserialize)]
struct Token {
    access_token: String,
    refresh_token: Option<String>,
    expires_at: DateTime<Utc>,
}

fn test_key(profile: &Profile) -> Result<Option<CipherKey>> {
    if let Ok(value) = std::env::var("TPCLI_TEST_KEY") {
        let value = Zeroizing::new(value);
        if !profile.test_mode() {
            return Err(Error::new(
                "TEST_MODE_REQUIRED",
                "Injected keys are permitted only in explicit local-fake test mode.",
            ));
        }
        let mut bytes = [0u8; 32];
        hex::decode_to_slice(value.as_bytes(), &mut bytes).map_err(|_| {
            Error::new(
                "KEY_UNAVAILABLE",
                "Test key must be exactly 32 bytes encoded as hex.",
            )
        })?;
        return Ok(Some(CipherKey::from_bytes(bytes)));
    }
    if profile.test_mode() {
        return Err(Error::new(
            "KEY_UNAVAILABLE",
            "Explicit test mode requires an injected ephemeral key; OS credentials are never consulted.",
        ));
    }
    Ok(None)
}

#[cfg(target_os = "macos")]
fn credential(profile: &Profile, kind: &str) -> Result<keyring::Entry> {
    keyring::Entry::new("tpcli", &format!("{}:{kind}", profile.name))
        .map_err(|_| Error::new("KEY_UNAVAILABLE", "Cannot access the OS credential store."))
}

fn get_secret(profile: &Profile, kind: &str) -> Result<Zeroizing<String>> {
    #[cfg(target_os = "macos")]
    {
        credential(profile, kind)?
            .get_password()
            .map(Zeroizing::new)
            .map_err(|_| Error::new("KEY_UNAVAILABLE", "Required Keychain entry is unavailable."))
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = (profile, kind);
        Err(Error::new(
            "KEYSTORE_UNSUPPORTED",
            "Production credentials currently require macOS Keychain.",
        ))
    }
}

fn set_secret(profile: &Profile, kind: &str, value: &str) -> Result<()> {
    #[cfg(target_os = "macos")]
    {
        credential(profile, kind)?
            .set_password(value)
            .map_err(|_| Error::new("KEY_UNAVAILABLE", "Cannot write the Keychain entry."))
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = (profile, kind, value);
        Err(Error::new(
            "KEYSTORE_UNSUPPORTED",
            "Production credentials currently require macOS Keychain.",
        ))
    }
}

pub fn load_key(profile: &Profile) -> Result<CipherKey> {
    if let Some(key) = test_key(profile)? {
        return Ok(key);
    }
    let value = get_secret(profile, "sqlcipher")?;
    let mut bytes = [0u8; 32];
    hex::decode_to_slice(value.as_bytes(), &mut bytes)
        .map_err(|_| Error::new("KEY_UNAVAILABLE", "The encryption key is invalid."))?;
    Ok(CipherKey::from_bytes(bytes))
}

fn initialize_key(profile: &Profile) -> Result<()> {
    if test_key(profile)?.is_some() {
        return Ok(());
    }
    #[cfg(target_os = "macos")]
    {
        let entry = credential(profile, "sqlcipher")?;
        match entry.get_password() {
            Ok(value) => {
                let value = Zeroizing::new(value);
                if value.len() != 64 || hex::decode(value.as_bytes()).is_err() {
                    return Err(Error::new("KEY_UNAVAILABLE", "Existing key is invalid."));
                }
                return Ok(());
            }
            Err(keyring::Error::NoEntry) => {}
            Err(_) => {
                return Err(Error::new(
                    "KEY_UNAVAILABLE",
                    "Cannot read existing key; it will not be replaced.",
                ));
            }
        }
        if profile.database_path()?.exists() {
            return Err(Error::new(
                "KEY_UNAVAILABLE",
                "History exists without its key. Restore the original Keychain backup; a new key cannot recover it.",
            ));
        }
        let mut bytes = Zeroizing::new([0u8; 32]);
        rand::thread_rng().fill_bytes(bytes.as_mut());
        set_secret(profile, "sqlcipher", &Zeroizing::new(hex::encode(*bytes)))
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = rand::thread_rng();
        Err(Error::new(
            "KEYSTORE_UNSUPPORTED",
            "Production credentials currently require macOS Keychain.",
        ))
    }
}

pub fn auth_status(profile: &Profile) -> Result<Value> {
    if profile.provider_mode == "local-fake" {
        let available = std::env::var("TPCLI_FAKE_TOKEN").is_ok_and(|v| v.len() >= 32);
        return Ok(json!({
            "provider_mode":"local-fake",
            "authenticated":available,
            "simulation":true,
            "key_available":load_key(profile).is_ok()
        }));
    }
    let token = get_secret(profile, "entra-token")?;
    let token: Token = serde_json::from_str(&token)
        .map_err(|_| Error::new("AUTH_REQUIRED", "Saved authentication is invalid."))?;
    Ok(json!({
        "provider_mode":"azure",
        "authenticated":token.expires_at > Utc::now(),
        "expires_at":token.expires_at,
        "key_available":load_key(profile).is_ok()
    }))
}

pub async fn bearer(profile: &Profile) -> Result<Zeroizing<String>> {
    if profile.provider_mode == "local-fake" {
        let token = std::env::var("TPCLI_FAKE_TOKEN").map_err(|_| {
            Error::new(
                "AUTH_REQUIRED",
                "Set the local-fake runtime token in the environment.",
            )
        })?;
        if token.len() < 32 {
            return Err(Error::new(
                "AUTH_REQUIRED",
                "Local-fake token is too short.",
            ));
        }
        return Ok(Zeroizing::new(token));
    }
    let encoded = get_secret(profile, "entra-token")
        .map_err(|_| Error::new("AUTH_REQUIRED", "Run tpcli auth login for this profile."))?;
    let token: Token = serde_json::from_str(&encoded)
        .map_err(|_| Error::new("AUTH_REQUIRED", "Saved authentication is invalid."))?;
    if token.expires_at > Utc::now() + Duration::seconds(90) {
        return Ok(Zeroizing::new(token.access_token));
    }
    let refresh = token
        .refresh_token
        .ok_or_else(|| Error::new("AUTH_REQUIRED", "Authentication expired; sign in again."))?;
    let client_id = profile.client_id.as_deref().unwrap_or_default();
    let response = client()?
        .post(oauth_url(profile, "token")?)
        .form(&[
            ("client_id", client_id),
            ("grant_type", "refresh_token"),
            ("refresh_token", refresh.as_str()),
            ("scope", profile.scope.as_deref().unwrap_or_default()),
        ])
        .send()
        .await
        .map_err(|_| Error::new("AUTH_FAILED", "Token refresh could not reach Entra."))?;
    if !response.status().is_success() {
        return Err(Error::new(
            "AUTH_REQUIRED",
            "Entra requires a new interactive sign-in.",
        ));
    }
    let value: Value = response
        .json()
        .await
        .map_err(|_| Error::new("AUTH_FAILED", "Invalid Entra token response."))?;
    let mut refreshed = token_from_value(value)?;
    if refreshed.refresh_token.is_none() {
        refreshed.refresh_token = Some(refresh);
    }
    save_token(profile, &refreshed)?;
    Ok(Zeroizing::new(refreshed.access_token))
}

fn oauth_url(profile: &Profile, path: &str) -> Result<String> {
    let tenant = profile
        .tenant_id
        .as_ref()
        .ok_or_else(|| Error::new("CONFIGURATION", "Tenant is required."))?;
    Ok(format!(
        "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/{path}"
    ))
}

fn client() -> Result<reqwest::Client> {
    reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(20))
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|_| Error::new("AUTH_FAILED", "Cannot initialize HTTPS client."))
}

fn token_from_value(value: Value) -> Result<Token> {
    Ok(Token {
        access_token: value["access_token"]
            .as_str()
            .ok_or_else(|| Error::new("AUTH_FAILED", "Entra returned no access token."))?
            .to_owned(),
        refresh_token: value["refresh_token"].as_str().map(str::to_owned),
        expires_at: Utc::now() + Duration::seconds(value["expires_in"].as_i64().unwrap_or(0)),
    })
}

fn save_token(profile: &Profile, token: &Token) -> Result<()> {
    let value = Zeroizing::new(
        serde_json::to_string(token)
            .map_err(|_| Error::new("AUTH_FAILED", "Cannot encode authentication."))?,
    );
    set_secret(profile, "entra-token", &value)
}

pub async fn login(profile: &Profile) -> Result<Value> {
    if profile.provider_mode == "local-fake" {
        bearer(profile).await?;
        initialize_key(profile)?;
        return auth_status(profile);
    }
    let client = client()?;
    let scope = format!(
        "{} offline_access",
        profile.scope.as_deref().unwrap_or_default()
    );
    let device = client
        .post(oauth_url(profile, "devicecode")?)
        .form(&[
            (
                "client_id",
                profile.client_id.as_deref().unwrap_or_default(),
            ),
            ("scope", scope.as_str()),
        ])
        .send()
        .await
        .map_err(|_| Error::new("AUTH_FAILED", "Cannot reach Entra device sign-in."))?;
    if !device.status().is_success() {
        return Err(Error::new(
            "AUTH_FAILED",
            "Entra rejected device sign-in configuration.",
        ));
    }
    let device: Value = device
        .json()
        .await
        .map_err(|_| Error::new("AUTH_FAILED", "Invalid device sign-in response."))?;
    let code = device["device_code"]
        .as_str()
        .ok_or_else(|| Error::new("AUTH_FAILED", "Missing device sign-in code."))?;
    let user_code = device["user_code"]
        .as_str()
        .ok_or_else(|| Error::new("AUTH_FAILED", "Missing user sign-in code."))?;
    // This is an intentional local authentication prompt, never runtime telemetry.
    eprintln!("Sign in at https://microsoft.com/devicelogin with code {user_code}");
    let mut interval = device["interval"].as_u64().unwrap_or(5).max(5);
    let deadline = tokio::time::Instant::now()
        + std::time::Duration::from_secs(device["expires_in"].as_u64().unwrap_or(900).min(900));
    while tokio::time::Instant::now() < deadline {
        tokio::time::sleep(std::time::Duration::from_secs(interval)).await;
        let response = client
            .post(oauth_url(profile, "token")?)
            .form(&[
                (
                    "client_id",
                    profile.client_id.as_deref().unwrap_or_default(),
                ),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                ("device_code", code),
            ])
            .send()
            .await
            .map_err(|_| Error::new("AUTH_FAILED", "Entra sign-in poll failed."))?;
        let success = response.status().is_success();
        let value: Value = response
            .json()
            .await
            .map_err(|_| Error::new("AUTH_FAILED", "Invalid Entra sign-in response."))?;
        if success {
            let token = token_from_value(value)?;
            save_token(profile, &token)?;
            initialize_key(profile)?;
            return auth_status(profile);
        }
        match value["error"].as_str() {
            Some("authorization_pending") => {}
            Some("slow_down") => interval = (interval + 5).min(30),
            _ => return Err(Error::new("AUTH_FAILED", "Sign-in was denied or expired.")),
        }
    }
    Err(Error::new("AUTH_FAILED", "Device sign-in expired."))
}
