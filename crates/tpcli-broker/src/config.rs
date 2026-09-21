use crate::{protocol::valid_id, Error, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    os::unix::fs::{DirBuilderExt, MetadataExt},
    path::{Path, PathBuf},
};
use url::Url;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Profile {
    pub runtime_url: String,
    pub provider_mode: String,
    pub tenant_id: Option<String>,
    pub client_id: Option<String>,
    pub scope: Option<String>,
    pub data_dir: Option<PathBuf>,
    #[serde(skip)]
    pub name: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Config {
    profiles: BTreeMap<String, Profile>,
}

fn home() -> Result<PathBuf> {
    std::env::var_os("HOME").map(PathBuf::from).ok_or_else(|| {
        Error::new(
            "CONFIGURATION",
            "HOME or an explicit config path is required.",
        )
    })
}

impl Profile {
    pub fn load(path: Option<&Path>, name: &str) -> Result<Self> {
        valid_id(name)?;
        let default_path;
        let path = match path {
            Some(p) => p,
            None => {
                default_path = home()?.join(".config/tpcli/config.toml");
                &default_path
            }
        };
        let text = std::fs::read_to_string(path)
            .map_err(|_| Error::new("CONFIGURATION", "Cannot read profile configuration."))?;
        let mut config: Config = toml::from_str(&text).map_err(|_| {
            Error::new("CONFIGURATION", "Invalid or unknown profile configuration.")
        })?;
        let mut profile = config.profiles.remove(name).ok_or_else(|| {
            Error::new("CONFIGURATION", "The requested profile is not configured.")
        })?;
        profile.name = name.to_owned();
        profile.validate()?;
        Ok(profile)
    }

    pub fn validate(&self) -> Result<()> {
        let url = Url::parse(&self.runtime_url)
            .map_err(|_| Error::new("CONFIGURATION", "Invalid runtime URL."))?;
        if !url.username().is_empty()
            || url.password().is_some()
            || url.query().is_some()
            || url.fragment().is_some()
            || url.path() != "/"
        {
            return Err(Error::new(
                "CONFIGURATION",
                "Runtime URL must be an origin without credentials, query, or path.",
            ));
        }
        match self.provider_mode.as_str() {
            "local-fake" => {
                if url.scheme() != "http"
                    || !matches!(url.host_str(), Some("127.0.0.1" | "[::1]" | "localhost"))
                {
                    return Err(Error::new(
                        "CONFIGURATION",
                        "The explicit local-fake provider requires loopback HTTP.",
                    ));
                }
            }
            "azure" => {
                if url.scheme() != "https"
                    || self
                        .tenant_id
                        .as_ref()
                        .is_none_or(|id| uuid::Uuid::parse_str(id).is_err())
                    || self
                        .client_id
                        .as_ref()
                        .is_none_or(|id| uuid::Uuid::parse_str(id).is_err())
                    || self.scope.as_ref().is_none_or(|s| s.trim().is_empty())
                {
                    return Err(Error::new(
                        "CONFIGURATION",
                        "Azure profiles require HTTPS, tenant/client UUIDs, and a runtime scope.",
                    ));
                }
            }
            _ => {
                return Err(Error::new(
                    "CONFIGURATION",
                    "provider_mode must be azure or explicitly local-fake.",
                ));
            }
        }
        if self.data_dir.as_ref().is_some_and(|d| !d.is_absolute()) {
            return Err(Error::new("CONFIGURATION", "data_dir must be absolute."));
        }
        Ok(())
    }

    pub fn state_dir(&self) -> Result<PathBuf> {
        match &self.data_dir {
            Some(path) => Ok(path.clone()),
            None => Ok(home()?.join(".local/share/tpcli").join(&self.name)),
        }
    }

    pub fn database_path(&self) -> Result<PathBuf> {
        Ok(self.state_dir()?.join("history.db"))
    }

    pub fn test_mode(&self) -> bool {
        self.provider_mode == "local-fake" && std::env::var("TPCLI_TEST_MODE").as_deref() == Ok("1")
    }
}

pub fn private_dir(path: &Path) -> Result<()> {
    if !path.exists() {
        std::fs::DirBuilder::new()
            .recursive(true)
            .mode(0o700)
            .create(path)
            .map_err(|_| Error::new("IPC_PERMISSIONS", "Cannot create private state directory."))?;
    }
    let metadata = std::fs::symlink_metadata(path)
        .map_err(|_| Error::new("IPC_PERMISSIONS", "Cannot inspect state directory."))?;
    if !metadata.is_dir()
        || metadata.file_type().is_symlink()
        || metadata.uid() != unsafe { libc::geteuid() }
        || metadata.mode() & 0o077 != 0
    {
        return Err(Error::new(
            "IPC_PERMISSIONS",
            "State directory must be owned by this user, not a symlink, and mode 0700.",
        ));
    }
    Ok(())
}

pub fn ipc_dir() -> Result<PathBuf> {
    let uid = unsafe { libc::geteuid() };
    let path = PathBuf::from(format!("/tmp/tpcli-{uid}"));
    private_dir(&path)?;
    Ok(path)
}

pub fn socket_path(session_id: &str) -> Result<PathBuf> {
    valid_id(session_id)?;
    if session_id.len() > 64 {
        return Err(Error::new(
            "INVALID_INPUT",
            "Session ID is too long for IPC.",
        ));
    }
    Ok(ipc_dir()?.join(format!("{session_id}.sock")))
}
