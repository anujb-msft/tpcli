use clap::{Args, Parser, Subcommand, ValueEnum};
use serde_json::{json, Value};
use std::{
    fs::OpenOptions,
    io::{IsTerminal, Read, Write},
    os::unix::{
        fs::{MetadataExt, OpenOptionsExt},
        process::CommandExt,
    },
    path::{Path, PathBuf},
    time::Duration,
};
use tokio::io::AsyncReadExt;
use tpcli_broker::{
    auth,
    broker::{Broker, ProfileLock},
    config::{socket_path, Profile},
    ipc::{self, Request},
    protocol::{valid_id, CallState, EventBatch, MAX_REQUEST},
    remote::Remote,
    store::Store,
    Error, Result,
};

#[derive(Parser)]
#[command(
    name = "tpcli",
    version,
    about = "Session-supervised Teams Phone / Azure Voice Live CLI"
)]
struct Cli {
    #[arg(long, global = true, env = "TPCLI_CONFIG")]
    config: Option<PathBuf>,
    #[arg(long, global = true, env = "TPCLI_PROFILE", default_value = "default")]
    profile: String,
    #[arg(long, global = true, env = "TPCLI_SESSION")]
    session: Option<String>,
    #[arg(long, global = true)]
    json: bool,
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    Auth {
        #[command(subcommand)]
        command: AuthCommand,
    },
    Profile {
        #[command(subcommand)]
        command: ProfileCommand,
    },
    Doctor {
        #[arg(long)]
        online: bool,
    },
    Session {
        #[command(subcommand)]
        command: SessionCommand,
    },
    Call {
        target: String,
        #[arg(long)]
        task_file: PathBuf,
        #[arg(long,default_value_t=600,value_parser=clap::value_parser!(u64).range(30..=3600))]
        max_duration_seconds: u64,
        #[arg(long)]
        allow_voicemail: bool,
    },
    Calls {
        #[command(subcommand)]
        command: CallsCommand,
    },
    Commands {
        #[command(subcommand)]
        command: CommandsCommand,
    },
    Approvals {
        #[command(subcommand)]
        command: ApprovalCommand,
    },
    History {
        #[command(subcommand)]
        command: HistoryCommand,
    },
    Transcripts {
        #[command(subcommand)]
        command: TranscriptCommand,
    },
}

#[derive(Subcommand)]
enum AuthCommand {
    Login,
    Status,
}
#[derive(Subcommand)]
enum ProfileCommand {
    Show,
}
#[derive(Subcommand)]
enum SessionCommand {
    Run {
        /// The supervising host keeps stdin open; EOF revokes ownership.
        #[arg(long)]
        owner_stdin: bool,
    },
    Exec {
        #[arg(required = true, trailing_var_arg = true, allow_hyphen_values = true)]
        command: Vec<String>,
    },
    Close {
        session_id: String,
    },
}
#[derive(Subcommand)]
enum CallsCommand {
    Start {
        #[arg(long, required = true)]
        request_stdin: bool,
        #[arg(long)]
        idempotency_key: Option<String>,
    },
    Status {
        call_id: String,
        #[arg(long)]
        offline: bool,
    },
    Events {
        call_id: String,
        #[arg(long, default_value_t = 0)]
        after: u64,
        #[arg(long, conflicts_with = "wait_seconds")]
        follow: bool,
        #[arg(long,value_parser=clap::value_parser!(u64).range(0..=30))]
        wait_seconds: Option<u64>,
    },
    Wait {
        call_id: String,
    },
    Instruct {
        #[command(flatten)]
        control: Control,
        #[arg(long, required = true)]
        text_stdin: bool,
    },
    Dtmf {
        #[command(flatten)]
        control: Control,
        #[arg(long, required = true)]
        digits_stdin: bool,
    },
    Stop {
        #[command(flatten)]
        control: Control,
    },
}
#[derive(Args)]
struct Control {
    call_id: String,
    #[arg(long)]
    idempotency_key: Option<String>,
}
#[derive(Subcommand)]
enum CommandsCommand {
    Status {
        command_id: String,
        #[arg(long)]
        offline: bool,
    },
}
#[derive(Subcommand)]
enum ApprovalCommand {
    List {
        #[arg(long)]
        call: String,
    },
    Resolve {
        approval_id: String,
        #[arg(long, value_enum)]
        decision: Decision,
        #[arg(long)]
        action_hash: String,
        #[arg(long)]
        idempotency_key: Option<String>,
    },
}
#[derive(Clone, ValueEnum)]
enum Decision {
    Approve,
    Deny,
}
#[derive(Subcommand)]
enum HistoryCommand {
    List,
    Show {
        call_id: String,
    },
    Prune {
        #[arg(long)]
        before: String,
        #[arg(long)]
        dry_run: bool,
    },
    Backup {
        #[arg(long)]
        destination: PathBuf,
    },
}
#[derive(Subcommand)]
enum TranscriptCommand {
    Show { call_id: String },
}

#[tokio::main]
async fn main() {
    // Private database, WAL, lock and socket files must never inherit a permissive umask.
    unsafe {
        libc::umask(0o077);
    }
    let cli = match Cli::try_parse() {
        Ok(cli) => cli,
        Err(error) => {
            if !matches!(
                error.kind(),
                clap::error::ErrorKind::DisplayHelp | clap::error::ErrorKind::DisplayVersion
            ) && std::env::args_os().any(|argument| argument == "--json")
            {
                let _ = output(
                    &Error::new(
                        "INVALID_INPUT",
                        "Invalid command syntax; use the command's --help.",
                    )
                    .json(),
                    true,
                );
                std::process::exit(2);
            }
            error.exit();
        }
    };
    let exit = match execute(&cli).await {
        Ok(code) => code,
        Err(error) if error.code == "OUTPUT_CLOSED" => 0,
        Err(error) => {
            if cli.json {
                let _ = output(&error.json(), true);
            } else {
                eprintln!("{error}");
            }
            error.exit_code()
        }
    };
    std::process::exit(i32::from(exit));
}

fn output(value: &Value, machine: bool) -> Result<()> {
    let text = if machine {
        serde_json::to_string(value)
    } else if value["type"] == "session.ready" {
        Ok(format!(
            "Session {} ({})\nThis scope owns the call; Ctrl+C here requests hangup.",
            value["session_id"].as_str().unwrap_or("unknown"),
            value["provider_mode"].as_str().unwrap_or("unknown")
        ))
    } else if let Some(kind) = value["type"].as_str() {
        let payload = &value["payload"];
        let detail = if let Some(text) = payload["text"].as_str() {
            format!(
                "{} ({}){}: {text}",
                payload["speaker"].as_str().unwrap_or("participant"),
                payload["delivery"].as_str().unwrap_or("unknown delivery"),
                if payload["interrupted"] == true {
                    " interrupted"
                } else {
                    ""
                }
            )
        } else if let Ok(state) = serde_json::from_value::<CallState>(payload["state"].clone()) {
            let now = chrono::Utc::now();
            format!(
                "Call {} ({:?}) | {} -> {} | {} / {} | elapsed {}s, remaining {}s | task: {}, hangup: {}",
                state.call_id,state.lifecycle,state.source,state.target,state.route,state.provider_mode,
                (now-state.created_at).num_seconds().max(0),
                (state.deadline-now).num_seconds().max(0),state.task_outcome,state.hangup_status
            )
        } else {
            serde_json::to_string(payload)
                .map_err(|_| Error::new("PROTOCOL_ERROR", "Cannot render event payload."))?
        };
        Ok(format!(
            "{} [{}] {detail}",
            value["sequence"].as_u64().unwrap_or(0),
            kind
        ))
    } else {
        serde_json::to_string_pretty(value)
    };
    let mut text = text.map_err(|_| Error::new("PROTOCOL_ERROR", "Cannot render result."))?;
    text.push('\n');
    std::io::stdout()
        .lock()
        .write_all(text.as_bytes())
        .map_err(|_| Error::new("OUTPUT_CLOSED", "Output consumer detached."))
}

fn store(profile: &Profile, writable: bool) -> Result<Store> {
    Store::open(
        &profile.database_path()?,
        &auth::load_key(profile)?,
        writable,
    )
}

fn session_for(
    cli: &Cli,
    profile: &Profile,
    call: Option<&str>,
    command: Option<&str>,
) -> Result<String> {
    if let Some(session) = &cli.session {
        valid_id(session)?;
        return Ok(session.clone());
    }
    if let Some(call) = call {
        return Ok(store(profile, false)?.state(call)?.session_id);
    }
    if let Some(command) = command {
        return Ok(store(profile, false)?.receipt(command)?.session_id);
    }
    Err(Error::new(
        "SESSION_REQUIRED",
        "Use --session or run this command inside tpcli session exec.",
    ))
}

async fn stdin_text(max: usize) -> Result<String> {
    let mut bytes = Vec::new();
    tokio::io::stdin()
        .take((max + 1) as u64)
        .read_to_end(&mut bytes)
        .await
        .map_err(|_| Error::new("INVALID_INPUT", "Cannot read request stdin."))?;
    if bytes.len() > max {
        return Err(Error::new(
            "INVALID_INPUT",
            "Input exceeds the bounded size limit.",
        ));
    }
    String::from_utf8(bytes).map_err(|_| Error::new("INVALID_INPUT", "Input must be UTF-8."))
}

fn protected_task(path: &Path) -> Result<String> {
    let file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)
        .map_err(|_| Error::new("INVALID_INPUT", "Cannot open protected task file."))?;
    let metadata = file
        .metadata()
        .map_err(|_| Error::new("INVALID_INPUT", "Cannot inspect task file."))?;
    if !metadata.is_file()
        || metadata.uid() != unsafe { libc::geteuid() }
        || metadata.mode() & 0o077 != 0
    {
        return Err(Error::new(
            "INVALID_INPUT",
            "Task file must be a user-owned regular file with mode 0600.",
        ));
    }
    let mut text = String::new();
    file.take(16_385)
        .read_to_string(&mut text)
        .map_err(|_| Error::new("INVALID_INPUT", "Task file must contain UTF-8 text."))?;
    if text.len() > 16_384 {
        return Err(Error::new("INVALID_INPUT", "Task file exceeds 16 KiB."));
    }
    Ok(text)
}

fn mutation(
    session: &str,
    operation: &str,
    id: &str,
    payload: Value,
    key: Option<&String>,
) -> Request {
    let mut request = Request::new(session, "submit");
    request.operation = operation.into();
    request.id = id.into();
    request.payload = payload;
    request.idempotency_key = key
        .cloned()
        .unwrap_or_else(|| uuid::Uuid::new_v4().to_string());
    request
}

async fn execute(cli: &Cli) -> Result<u8> {
    let profile = Profile::load(cli.config.as_deref(), &cli.profile)?;
    match &cli.command {
        Command::Auth { command } => {
            let value = match command {
                AuthCommand::Login => auth::login(&profile).await?,
                AuthCommand::Status => auth::auth_status(&profile)?,
            };
            output(&value, cli.json)?;
        }
        Command::Profile {
            command: ProfileCommand::Show,
        } => {
            output(
                &json!({
                    "profile":profile,
                    "capabilities":{"pstn":"requires_runtime_readiness","teams":false,
                        "teams_gate":"Direct Teams/Voice Live media is not established."},
                    "secrets":"OS credential store; never in configuration",
                    "simulation":profile.provider_mode=="local-fake"
                }),
                cli.json,
            )?;
        }
        Command::Doctor { online } => {
            let key = auth::load_key(&profile)?;
            let path = profile.database_path()?;
            let exists = path.exists();
            let _lock = if exists {
                None
            } else {
                Some(ProfileLock::acquire(&profile)?)
            };
            let store = Store::open(&path, &key, !exists)?;
            store.integrity_check()?;
            let mut value = json!({
                "profile":profile.name,"provider_mode":profile.provider_mode,
                "checks":[
                    {"name":"configuration","status":"ready"},
                    {"name":"os_key","status":"ready"},
                    {"name":"sqlcipher","status":"ready","version":store.cipher_version()?},
                    {"name":"encrypted_history","status":"ready"},
                    {"name":"local_protocol","status":"ready","version":"1"}
                ],
                "online":online,"live_call_verified":false
            });
            if *online {
                value["runtime"] = Remote::new(profile)?.capabilities(true).await?;
            } else {
                value["runtime"] = json!({"status":"not_checked","network_operations":0});
            }
            output(&value, cli.json)?;
        }
        Command::Session { command } => match command {
            SessionCommand::Run { owner_stdin } => {
                return session_run(cli, profile, *owner_stdin).await
            }
            SessionCommand::Exec { command } => return session_exec(cli, profile, command).await,
            SessionCommand::Close { session_id } => {
                valid_id(session_id)?;
                let request = Request::new(session_id, "session_close");
                output(&ipc::call(&request).await?, cli.json)?;
            }
        },
        Command::Call {
            target,
            task_file,
            max_duration_seconds,
            allow_voicemail,
        } => {
            let payload = json!({
                "target":target,"task":protected_task(task_file)?,
                "max_duration_seconds":max_duration_seconds,"allow_voicemail":allow_voicemail
            });
            tpcli_broker::protocol::validate_start(&payload)?;
            return single_call(cli, profile, payload).await;
        }
        Command::Calls { command } => match command {
            CallsCommand::Start {
                idempotency_key, ..
            } => {
                let session = session_for(cli, &profile, None, None)?;
                let payload: Value = serde_json::from_str(&stdin_text(MAX_REQUEST).await?)
                    .map_err(|_| {
                        Error::new(
                            "INVALID_INPUT",
                            "Request stdin must be a JSON start payload.",
                        )
                    })?;
                let request = mutation(
                    &session,
                    "calls.start",
                    "",
                    payload,
                    idempotency_key.as_ref(),
                );
                output(&ipc::call(&request).await?, cli.json)?;
            }
            CallsCommand::Status { call_id, offline } => {
                valid_id(call_id)?;
                let value = if *offline {
                    json!({"offline":true,"state":store(&profile,false)?.state(call_id)?})
                } else if let Ok(session) = session_for(cli, &profile, Some(call_id), None) {
                    if socket_path(&session)?.exists() {
                        let mut request = Request::new(&session, "call_status");
                        request.id = call_id.clone();
                        ipc::call(&request).await?
                    } else {
                        json!(Remote::new(profile)?.state(call_id).await?)
                    }
                } else {
                    json!(Remote::new(profile)?.state(call_id).await?)
                };
                output(&value, cli.json)?;
            }
            CallsCommand::Events {
                call_id,
                after,
                follow,
                wait_seconds,
            } => {
                valid_id(call_id)?;
                let session = session_for(cli, &profile, Some(call_id), None)?;
                if !socket_path(&session)?.exists() && !follow && wait_seconds.unwrap_or(0) == 0 {
                    output(
                        &json!(store(&profile, false)?.batch(call_id, *after)?),
                        cli.json,
                    )?;
                } else {
                    let mut request = Request::new(&session, "events");
                    request.id = call_id.clone();
                    request.after = *after;
                    request.follow = *follow;
                    request.wait_seconds = wait_seconds.unwrap_or(0);
                    if *follow {
                        let mut reader = ipc::connect(&request).await?;
                        while let Some(event) = ipc::next_event(&mut reader).await? {
                            output(&event, cli.json)?;
                        }
                    } else {
                        output(&ipc::call(&request).await?, cli.json)?;
                    }
                }
            }
            CallsCommand::Wait { call_id } => {
                let session = session_for(cli, &profile, Some(call_id), None)?;
                return wait_call(cli, &profile, &session, call_id).await;
            }
            CallsCommand::Instruct { control, .. } => {
                let text = stdin_text(16_384).await?;
                if text.trim().is_empty() {
                    return Err(Error::new("INVALID_INPUT", "Instruction cannot be empty."));
                }
                let session = session_for(cli, &profile, Some(&control.call_id), None)?;
                let request = mutation(
                    &session,
                    "calls.instruct",
                    &control.call_id,
                    json!({"text":text}),
                    control.idempotency_key.as_ref(),
                );
                output(&ipc::call(&request).await?, cli.json)?;
            }
            CallsCommand::Dtmf { control, .. } => {
                let digits = stdin_text(64).await?.trim().to_owned();
                if digits.is_empty()
                    || digits.len() > 32
                    || !digits
                        .bytes()
                        .all(|b| b.is_ascii_digit() || b == b'*' || b == b'#')
                {
                    return Err(Error::new(
                        "INVALID_INPUT",
                        "DTMF requires 1..32 characters from 0-9, *, #.",
                    ));
                }
                let session = session_for(cli, &profile, Some(&control.call_id), None)?;
                let request = mutation(
                    &session,
                    "calls.dtmf",
                    &control.call_id,
                    json!({"digits":digits}),
                    control.idempotency_key.as_ref(),
                );
                output(&ipc::call(&request).await?, cli.json)?;
            }
            CallsCommand::Stop { control } => {
                let session = session_for(cli, &profile, Some(&control.call_id), None)?;
                let request = mutation(
                    &session,
                    "calls.stop",
                    &control.call_id,
                    json!({}),
                    control.idempotency_key.as_ref(),
                );
                output(&ipc::call(&request).await?, cli.json)?;
            }
        },
        Command::Commands {
            command:
                CommandsCommand::Status {
                    command_id,
                    offline,
                },
        } => {
            valid_id(command_id)?;
            let value = if *offline {
                json!({"offline":true,"receipt":store(&profile,false)?.receipt(command_id)?})
            } else if let Ok(session) = session_for(cli, &profile, None, Some(command_id)) {
                if socket_path(&session)?.exists() {
                    let mut request = Request::new(&session, "command_status");
                    request.id = command_id.clone();
                    ipc::call(&request).await?
                } else {
                    json!(Remote::new(profile)?.command(command_id).await?)
                }
            } else {
                json!(Remote::new(profile)?.command(command_id).await?)
            };
            output(&value, cli.json)?;
        }
        Command::Approvals { command } => match command {
            ApprovalCommand::List { call } => {
                let session = session_for(cli, &profile, Some(call), None)?;
                let mut request = Request::new(&session, "approvals");
                request.id = call.clone();
                output(&ipc::call(&request).await?, cli.json)?;
            }
            ApprovalCommand::Resolve {
                approval_id,
                decision,
                action_hash,
                idempotency_key,
            } => {
                valid_id(approval_id)?;
                if action_hash.len() != 71
                    || !action_hash.starts_with("sha256:")
                    || !action_hash[7..]
                        .bytes()
                        .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
                {
                    return Err(Error::new(
                        "INVALID_INPUT",
                        "Use the exact sha256: action hash from the pending approval.",
                    ));
                }
                let session = match &cli.session {
                    Some(id) => id.clone(),
                    None => {
                        let history = store(&profile, false)?;
                        history
                            .state(&history.approval_call(approval_id)?)?
                            .session_id
                    }
                };
                let decision = match decision {
                    Decision::Approve => "approve",
                    Decision::Deny => "deny",
                };
                let request = mutation(
                    &session,
                    "approvals.resolve",
                    "",
                    json!({
                        "approval_id":approval_id,"decision":decision,"action_hash":action_hash
                    }),
                    idempotency_key.as_ref(),
                );
                output(&ipc::call(&request).await?, cli.json)?;
            }
        },
        Command::History { command } => match command {
            HistoryCommand::List => {
                output(&json!({"calls":store(&profile,false)?.states()?}), cli.json)?
            }
            HistoryCommand::Show { call_id } => {
                output(&store(&profile, false)?.history(call_id)?, cli.json)?
            }
            HistoryCommand::Prune { before, dry_run } => {
                let before = chrono::NaiveDate::parse_from_str(before, "%Y-%m-%d")
                    .map_err(|_| Error::new("INVALID_INPUT", "Use a UTC cutoff date YYYY-MM-DD."))?
                    .and_hms_opt(0, 0, 0)
                    .ok_or_else(|| Error::new("INVALID_INPUT", "Invalid UTC date."))?
                    .and_utc();
                let _lock = if *dry_run {
                    None
                } else {
                    Some(ProfileLock::acquire(&profile)?)
                };
                output(
                    &store(&profile, !dry_run)?.prune(before, *dry_run)?,
                    cli.json,
                )?;
            }
            HistoryCommand::Backup { destination } => {
                let key = auth::load_key(&profile)?;
                let source = store(&profile, false)?;
                source.backup(destination, &key)?;
                output(
                    &json!({"encrypted":true,"key":"same OS credential-store key","destination":destination}),
                    cli.json,
                )?;
            }
        },
        Command::Transcripts {
            command: TranscriptCommand::Show { call_id },
        } => {
            output(&store(&profile, false)?.transcript(call_id)?, cli.json)?;
        }
    }
    Ok(0)
}

async fn wait_call(cli: &Cli, profile: &Profile, session: &str, id: &str) -> Result<u8> {
    let mut after = 0;
    loop {
        if !socket_path(session)?.exists() {
            let state = store(profile, false)?.state(id)?;
            if state.terminal() {
                output(&json!(state), cli.json)?;
                return Ok(if state.success() { 0 } else { 5 });
            }
            return Err(Error::new(
                "SESSION_REQUIRED",
                "Owner broker has exited; only timestamped offline state is available.",
            ));
        }
        let mut request = Request::new(session, "events");
        request.id = id.into();
        request.after = after;
        request.wait_seconds = 10;
        let batch: EventBatch = serde_json::from_value(ipc::call(&request).await?)
            .map_err(|_| Error::new("PROTOCOL_ERROR", "Invalid finite event batch."))?;
        after = batch.next_cursor;
        if batch.has_more {
            continue;
        }
        if let Some(state) = batch.call_state {
            if state.terminal() && after >= state.last_sequence {
                output(&json!(state), cli.json)?;
                return Ok(if state.success() { 0 } else { 5 });
            }
        }
    }
}

struct Signals {
    interrupt: tokio::signal::unix::Signal,
    terminate: tokio::signal::unix::Signal,
    hangup: tokio::signal::unix::Signal,
}
impl Signals {
    fn new() -> Result<Self> {
        use tokio::signal::unix::{signal, SignalKind};
        let failed = |_| Error::new("SUPERVISION_FAILED", "Cannot establish signal supervision.");
        Ok(Self {
            interrupt: signal(SignalKind::interrupt()).map_err(failed)?,
            terminate: signal(SignalKind::terminate()).map_err(failed)?,
            hangup: signal(SignalKind::hangup()).map_err(failed)?,
        })
    }
    async fn next(&mut self) -> i32 {
        tokio::select! {
            _=self.interrupt.recv()=>libc::SIGINT,
            _=self.terminate.recv()=>libc::SIGTERM,
            _=self.hangup.recv()=>libc::SIGHUP,
        }
    }
}

async fn parent_loss(parent: libc::pid_t) {
    loop {
        if unsafe { libc::getppid() } != parent {
            return;
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
}

async fn terminal_loss(enabled: bool) {
    if !enabled {
        std::future::pending::<()>().await;
    }
    loop {
        let mut terminal = libc::pollfd {
            fd: libc::STDIN_FILENO,
            events: libc::POLLIN,
            revents: 0,
        };
        let result = unsafe { libc::poll(&mut terminal, 1, 0) };
        if result > 0 && terminal.revents & (libc::POLLHUP | libc::POLLERR | libc::POLLNVAL) != 0 {
            return;
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
}

async fn stdin_closed(enabled: bool) {
    // Tokio stdin uses an uncancellable blocking read that can outlive host loss.
    terminal_loss(enabled).await;
}

fn stdin_is_pipe() -> bool {
    let mut metadata = std::mem::MaybeUninit::<libc::stat>::uninit();
    if unsafe { libc::fstat(libc::STDIN_FILENO, metadata.as_mut_ptr()) } != 0 {
        return false;
    }
    unsafe { metadata.assume_init().st_mode & libc::S_IFMT == libc::S_IFIFO }
}

async fn session_run(cli: &Cli, profile: Profile, owner_stdin: bool) -> Result<u8> {
    if owner_stdin && !stdin_is_pipe() {
        return Err(Error::new(
            "SESSION_REQUIRED",
            "--owner-stdin requires a supervising host-held pipe, not a file or terminal.",
        ));
    }
    if !owner_stdin && !std::io::stdin().is_terminal() {
        return Err(Error::new(
            "SESSION_REQUIRED",
            "Headless session run requires --owner-stdin and a supervising host-held pipe.",
        ));
    }
    let parent = unsafe { libc::getppid() };
    let mut signals = Signals::new()?;
    let running = Broker::start(profile).await?;
    if let Err(error) = output(&running.broker.ready(), true) {
        running.close().await?;
        return Err(error);
    }
    if !cli.json {
        eprintln!("Owner scope active. Ctrl+C or host pipe closure revokes all calls. No unattended detach.");
    }
    let mut failures = running.broker.failures();
    let known_failure = failures.borrow().clone();
    let failure = if known_failure.is_some() {
        known_failure
    } else {
        tokio::select! {
            _=signals.next()=>None,
            _=stdin_closed(owner_stdin)=>None,
            _=parent_loss(parent)=>None,
            _=terminal_loss(std::io::stdin().is_terminal())=>None,
            _=failures.changed()=>failures.borrow().clone(),
        }
    };
    running.close().await?;
    if let Some(error) = failure {
        if error.code != "SESSION_REVOKED" {
            return Err(error);
        }
    }
    Ok(0)
}

struct TerminalForeground(Option<libc::pid_t>);
impl TerminalForeground {
    fn give_to(pid: libc::pid_t) -> Result<Self> {
        if !std::io::stdin().is_terminal() {
            return Ok(Self(None));
        }
        let group = unsafe { libc::getpgrp() };
        if unsafe { libc::tcgetpgrp(libc::STDIN_FILENO) } != group {
            return Err(Error::new(
                "SUPERVISION_FAILED",
                "session exec must begin in the terminal foreground.",
            ));
        }
        unsafe {
            libc::signal(libc::SIGTTOU, libc::SIG_IGN);
        }
        if unsafe { libc::tcsetpgrp(libc::STDIN_FILENO, pid) } != 0 {
            unsafe {
                libc::signal(libc::SIGTTOU, libc::SIG_DFL);
            }
            return Err(Error::new(
                "SUPERVISION_FAILED",
                "Cannot give the child terminal job control.",
            ));
        }
        // The child may have attempted terminal input between spawn and tcsetpgrp.
        unsafe {
            libc::kill(-pid, libc::SIGCONT);
        }
        Ok(Self(Some(group)))
    }
}
impl Drop for TerminalForeground {
    fn drop(&mut self) {
        if let Some(group) = self.0 {
            unsafe {
                libc::tcsetpgrp(libc::STDIN_FILENO, group);
                libc::signal(libc::SIGTTOU, libc::SIG_DFL);
            }
        }
    }
}

async fn session_exec(cli: &Cli, profile: Profile, command: &[String]) -> Result<u8> {
    let parent = unsafe { libc::getppid() };
    let mut signals = Signals::new()?;
    let running = Broker::start(profile).await?;
    let mut child_command = tokio::process::Command::new(&command[0]);
    child_command
        .args(&command[1..])
        .env("TPCLI_SESSION", &running.broker.session.session_id)
        .env("TPCLI_SOCKET", &running.broker.socket)
        .env("TPCLI_PROFILE", &cli.profile)
        .kill_on_drop(true);
    if let Some(config) = &cli.config {
        let path = std::fs::canonicalize(config)
            .map_err(|_| Error::new("CONFIGURATION", "Cannot resolve config path."))?;
        child_command.env("TPCLI_CONFIG", path);
    }
    child_command.as_std_mut().process_group(0);
    let mut child = match child_command.spawn() {
        Ok(child) => child,
        Err(_) => {
            running.close().await?;
            return Err(Error::new(
                "INVALID_INPUT",
                "Cannot start supervised shell/agent.",
            ));
        }
    };
    let pid = child
        .id()
        .ok_or_else(|| Error::new("SUPERVISION_FAILED", "Child has no process ID."))?
        as libc::pid_t;
    let foreground = match TerminalForeground::give_to(pid) {
        Ok(foreground) => foreground,
        Err(error) => {
            let _ = child.kill().await;
            running.close().await?;
            return Err(error);
        }
    };
    let mut failures = running.broker.failures();
    let mut failure = failures.borrow().clone();
    let code = loop {
        if failure.is_some() {
            unsafe {
                libc::kill(-pid, libc::SIGHUP);
            }
            break 4;
        }
        tokio::select! {
            status=child.wait()=>break status
                .map_err(|_|Error::new("SUPERVISION_FAILED","Cannot observe supervised child."))?
                .code().unwrap_or(128).clamp(0,255) as u8,
            signal=signals.next()=>{
                if signal==libc::SIGINT {
                    // A foreground viewer's SIGINT must not revoke its parent shell session.
                    unsafe {libc::kill(-pid,libc::SIGINT);}
                    continue;
                }
                unsafe {libc::kill(-pid,libc::SIGHUP);}
                break 128+signal as u8;
            }
            _=parent_loss(parent)=>{
                unsafe {libc::kill(-pid,libc::SIGHUP);}
                break 129;
            }
            _=terminal_loss(std::io::stdin().is_terminal())=>{
                unsafe {libc::kill(-pid,libc::SIGHUP);}
                break 129;
            }
            _=failures.changed()=>{
                failure=failures.borrow().clone();
                unsafe {libc::kill(-pid,libc::SIGHUP);}
                break 4;
            }
        }
    };
    drop(foreground);
    running.close().await?;
    if let Some(error) = failure {
        return Err(error);
    }
    Ok(code)
}

async fn single_call(cli: &Cli, profile: Profile, payload: Value) -> Result<u8> {
    let parent = unsafe { libc::getppid() };
    let mut signals = Signals::new()?;
    // Never inherit ownership from TPCLI_SESSION. A shortcut always has its own broker and scope.
    let running = Broker::start(profile.clone()).await?;
    let session = running.broker.session.session_id.clone();
    if let Err(error) = output(&running.broker.ready(), cli.json) {
        running.close().await?;
        return Err(error);
    }
    if !cli.json {
        eprintln!("Stop: Ctrl+C. Other commands can use --session {session}. Approval is never automatic.");
    }
    let request = mutation(&session, "calls.start", "", payload, None);
    let receipt = match running.broker.handle(&request).await {
        Ok(receipt) => receipt,
        Err(error) => {
            running.close().await?;
            return Err(error);
        }
    };
    let id = receipt["call_id"]
        .as_str()
        .ok_or_else(|| Error::new("PROTOCOL_ERROR", "Start receipt omitted call ID."))?
        .to_owned();
    if !cli.json {
        eprintln!("Call {id} accepted; connection and completion are reported separately.");
    }
    let mut failures = running.broker.failures();
    let observe = async {
        let mut after = 0;
        loop {
            let mut request = Request::new(&session, "events");
            request.id = id.clone();
            request.after = after;
            request.wait_seconds = 10;
            let batch: EventBatch = serde_json::from_value(ipc::call(&request).await?)
                .map_err(|_| Error::new("PROTOCOL_ERROR", "Invalid event batch."))?;
            for event in &batch.events {
                output(&json!(event), cli.json)?;
            }
            after = batch.next_cursor;
            if batch.has_more {
                continue;
            }
            if let Some(state) = batch.call_state {
                if state.terminal() && after >= state.last_sequence {
                    return Ok::<CallState, Error>(state);
                }
            }
        }
    };
    let known_failure = failures.borrow().clone();
    let observed = if let Some(error) = known_failure {
        Some(Err(error))
    } else {
        tokio::select! {
            result=observe=>Some(result),
            _=signals.next()=>None,
            _=parent_loss(parent)=>None,
            _=terminal_loss(std::io::stdin().is_terminal())=>None,
            _=failures.changed()=>Some(Err(failures.borrow().clone().unwrap_or_else(
                ||Error::new("SESSION_REVOKED","Session ended.")
            ))),
        }
    };
    running.close().await?;
    let state = match observed {
        Some(Ok(state)) => state,
        Some(Err(error)) => return Err(error),
        None => store(&profile, false)?.state(&id)?,
    };
    output(&json!(state), cli.json)?;
    Ok(if state.success() { 0 } else { 5 })
}
