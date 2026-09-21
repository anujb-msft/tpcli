use crate::{
    auth,
    config::{private_dir, socket_path, Profile},
    ipc::{self, Request},
    protocol::{self, CommandRequest, Event, SessionInfo, MAX_REQUEST},
    remote::{ControlSocket, Remote},
    store::Store,
    Error, Result,
};
use futures_util::{SinkExt, StreamExt};
use serde_json::{json, Value};
use std::{
    fs::{File, OpenOptions},
    os::{
        fd::AsRawFd,
        unix::fs::{OpenOptionsExt, PermissionsExt},
    },
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex, MutexGuard,
    },
    time::Duration,
};
use tokio::{
    io::BufReader,
    net::{unix::OwnedWriteHalf, UnixListener, UnixStream},
    sync::{broadcast, watch, Semaphore},
    task::JoinHandle,
};
use tokio_tungstenite::tungstenite::Message;

pub struct ProfileLock(File);

impl ProfileLock {
    pub fn acquire(profile: &Profile) -> Result<Self> {
        let dir = profile.state_dir()?;
        private_dir(&dir)?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .mode(0o600)
            .custom_flags(libc::O_NOFOLLOW)
            .open(dir.join("writer.lock"))
            .map_err(|_| Error::new("IPC_PERMISSIONS", "Cannot open profile writer lock."))?;
        if unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } != 0 {
            return Err(Error::new(
                "PROFILE_BUSY",
                "This profile already has an active writer/broker.",
            ));
        }
        Ok(Self(file))
    }
}

impl Drop for ProfileLock {
    fn drop(&mut self) {
        unsafe {
            libc::flock(self.0.as_raw_fd(), libc::LOCK_UN);
        }
    }
}

pub struct Broker {
    pub session: SessionInfo,
    pub socket: PathBuf,
    pub remote: Remote,
    store: Mutex<Store>,
    notifications: broadcast::Sender<()>,
    shutdown: watch::Sender<bool>,
    failure: watch::Sender<Option<Error>>,
    closed: AtomicBool,
    _lock: ProfileLock,
}

pub struct RunningBroker {
    pub broker: Arc<Broker>,
    tasks: Vec<JoinHandle<()>>,
}

impl Broker {
    pub async fn start(profile: Profile) -> Result<RunningBroker> {
        let lock = ProfileLock::acquire(&profile)?;
        let key = auth::load_key(&profile)?;
        let store = Store::open(&profile.database_path()?, &key, true)?;
        let remote = Remote::new(profile)?;
        let session = remote.create_session().await?;
        let mut control = match remote.control(&session.session_id).await {
            Ok(control) => control,
            Err(error) => {
                let _ = remote.close_session(&session.session_id).await;
                return Err(error);
            }
        };
        let first = tokio::time::timeout(Duration::from_secs(5), control.next())
            .await
            .map_err(|_| {
                Error::new(
                    "RUNTIME_UNREACHABLE",
                    "Runtime ownership handshake timed out.",
                )
            })?
            .ok_or_else(|| {
                Error::new("SESSION_REVOKED", "Runtime closed the ownership handshake.")
            })?
            .map_err(|_| Error::new("PROTOCOL_ERROR", "Runtime ownership handshake failed."))?;
        let ready: Event = serde_json::from_slice(&first.into_data()).map_err(|_| {
            Error::new(
                "PROTOCOL_ERROR",
                "Runtime did not send a session.ready event.",
            )
        })?;
        ready.validate()?;
        if ready.kind != "session.ready" || ready.session_id != session.session_id {
            return Err(Error::new(
                "PROTOCOL_ERROR",
                "Session handshake correlation mismatch.",
            ));
        }
        let socket = socket_path(&session.session_id)?;
        let listener = UnixListener::bind(&socket).map_err(|_| {
            Error::new(
                "IPC_PERMISSIONS",
                "Cannot bind the protected session socket.",
            )
        })?;
        std::fs::set_permissions(&socket, std::fs::Permissions::from_mode(0o600))
            .map_err(|_| Error::new("IPC_PERMISSIONS", "Cannot protect the session socket."))?;
        let (notifications, _) = broadcast::channel(128);
        let (shutdown, _) = watch::channel(false);
        let (failure, _) = watch::channel(None);
        let broker = Arc::new(Self {
            session,
            socket,
            remote,
            store: Mutex::new(store),
            notifications,
            shutdown,
            failure,
            closed: AtomicBool::new(false),
            _lock: lock,
        });
        let ipc_broker = broker.clone();
        let control_broker = broker.clone();
        let tasks = vec![
            tokio::spawn(async move {
                ipc_broker.accept(listener).await;
            }),
            tokio::spawn(async move {
                control_broker.control_loop(control).await;
            }),
        ];
        Ok(RunningBroker { broker, tasks })
    }

    fn store(&self) -> Result<MutexGuard<'_, Store>> {
        self.store
            .lock()
            .map_err(|_| Error::new("ENCRYPTED_STORE_FAILED", "History writer is unavailable."))
    }

    pub fn failures(&self) -> watch::Receiver<Option<Error>> {
        self.failure.subscribe()
    }

    pub fn ready(&self) -> Value {
        json!({
            "schema_version":"1","type":"session.ready","session_id":self.session.session_id,
            "socket":self.socket,"provider_mode":self.session.provider_mode,
            "lease_expires_at":self.session.lease_expires_at
        })
    }

    pub fn state(&self, id: &str) -> Result<protocol::CallState> {
        self.store()?.state(id)
    }

    fn ingest(&self, event: &Event) -> Result<()> {
        if event.session_id != self.session.session_id {
            return Err(Error::new(
                "PROTOCOL_ERROR",
                "Event belongs to a different supervision session.",
            ));
        }
        if self.store()?.ingest(event)? {
            let _ = self.notifications.send(());
        }
        Ok(())
    }

    async fn control_loop(self: Arc<Self>, mut socket: ControlSocket) {
        let mut shutdown = self.shutdown.subscribe();
        let mut heartbeat = tokio::time::interval(Duration::from_secs(5));
        heartbeat.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        let outcome: Result<()> = async {
            loop {
                tokio::select! {
                    biased;
                    _ = shutdown.changed() => break,
                    _ = heartbeat.tick() => {
                        if self.closed.load(Ordering::SeqCst) {break;}
                        socket.send(Message::Text(r#"{"type":"heartbeat"}"#.into())).await
                            .map_err(|_| Error::new("OWNER_CONNECTION_LOST","Owner heartbeat failed; scope is revoked."))?;
                    }
                    message = socket.next() => {
                        let message = message.ok_or_else(||Error::new("OWNER_CONNECTION_LOST","Owner control socket closed."))?
                            .map_err(|_|Error::new("OWNER_CONNECTION_LOST","Owner control socket failed."))?;
                        match message {
                            Message::Text(text) => {
                                let event: Event = serde_json::from_str(&text)
                                    .map_err(|_|Error::new("PROTOCOL_ERROR","Invalid runtime event."))?;
                                self.ingest(&event)?;
                                if let Some(call) = &event.call_id {
                                    socket.send(Message::Text(json!({
                                        "type":"ack","call_id":call,"sequence":event.sequence
                                    }).to_string().into())).await
                                        .map_err(|_|Error::new("OWNER_CONNECTION_LOST","Persistence ACK failed."))?;
                                }
                                if event.kind == "session.revoked" {
                                    return Err(Error::new("SESSION_REVOKED","Runtime revoked this ownership scope."));
                                }
                            }
                            Message::Ping(data) => {socket.send(Message::Pong(data)).await
                                .map_err(|_|Error::new("OWNER_CONNECTION_LOST","Control ping failed."))?;}
                            Message::Close(_) => return Err(Error::new("OWNER_CONNECTION_LOST","Runtime closed ownership.")),
                            Message::Binary(_) => return Err(Error::new("PROTOCOL_ERROR","Audio/binary data is not allowed on CLI control.")),
                            _ => {}
                        }
                    }
                }
            }
            Ok(())
        }.await;
        if let Err(error) = outcome {
            if !self.closed.swap(true, Ordering::SeqCst) {
                let error = match self
                    .store()
                    .and_then(|mut store| store.interrupt_session(&self.session.session_id))
                {
                    Ok(()) => error,
                    Err(storage_error) => storage_error,
                };
                self.failure.send_replace(Some(error));
                self.shutdown.send_replace(true);
                // This independent HTTP stop is best effort; the remote lease/watchdog remains authoritative.
                let _ = tokio::time::timeout(
                    Duration::from_secs(2),
                    self.remote.close_session(&self.session.session_id),
                )
                .await;
            }
        }
        let _ = socket.close(None).await;
    }

    async fn accept(self: Arc<Self>, listener: UnixListener) {
        let semaphore = Arc::new(Semaphore::new(64));
        let mut shutdown = self.shutdown.subscribe();
        loop {
            tokio::select! {
                _ = shutdown.changed() => break,
                accepted = listener.accept() => {
                    let (stream,_) = match accepted {
                        Ok(pair)=>pair,
                        Err(_)=>{
                            self.failure.send_replace(Some(Error::new("IPC_FAILED","Session listener failed.")));
                            self.shutdown.send_replace(true);
                            break;
                        }
                    };
                    let permit = match semaphore.clone().try_acquire_owned() {
                        Ok(permit)=>permit,
                        Err(_)=>{
                            let error=Error::new("BROKER_BUSY","Broker connection limit reached; retry this observation later.");
                            let frame=format!("{}\n",error.json());
                            let _=stream.try_write(frame.as_bytes());
                            continue;
                        }
                    };
                    let broker = self.clone();
                    tokio::spawn(async move {
                        let _permit = permit;
                        broker.serve(stream).await;
                    });
                }
            }
        }
    }

    async fn serve(self: Arc<Self>, stream: UnixStream) {
        if !stream
            .peer_cred()
            .is_ok_and(|credential| credential.uid() == unsafe { libc::geteuid() })
        {
            return;
        }
        let (read, mut write) = stream.into_split();
        let outcome: Result<()> = async {
            let mut read = BufReader::new(read);
            let frame = tokio::time::timeout(
                Duration::from_secs(3),
                ipc::read_frame(&mut read, MAX_REQUEST),
            )
            .await
            .map_err(|_| Error::new("IPC_TIMEOUT", "Command request timed out."))??
            .ok_or_else(|| Error::new("INVALID_INPUT", "Missing IPC request."))?;
            let request: Request = serde_json::from_slice(&frame)
                .map_err(|_| Error::new("INVALID_INPUT", "Invalid IPC request."))?;
            if request.schema_version != "1" || request.session_id != self.session.session_id {
                return Err(Error::new(
                    "SESSION_REQUIRED",
                    "Request does not match this session/version.",
                ));
            }
            if request.op == "events" {
                self.stream(&request, &mut write).await
            } else {
                let value = self.handle(&request).await?;
                ipc::write_frame(&mut write, &value).await
            }
        }
        .await;
        if let Err(error) = outcome {
            let _ = ipc::write_frame(&mut write, &error.json()).await;
            if error.code == "ENCRYPTED_STORE_FAILED" || error.code == "EVENT_CONFLICT" {
                self.failure.send_replace(Some(error));
                self.closed.store(true, Ordering::SeqCst);
                self.shutdown.send_replace(true);
                let _ = tokio::time::timeout(
                    Duration::from_secs(2),
                    self.remote.close_session(&self.session.session_id),
                )
                .await;
            }
        }
    }

    async fn owned_state(&self, id: &str) -> Result<protocol::CallState> {
        let state = self.remote.state(id).await?;
        if state.session_id != self.session.session_id {
            return Err(Error::new(
                "SESSION_REQUIRED",
                "Call is not owned by this broker's session.",
            ));
        }
        self.store()?.save_state(&state)?;
        self.store()?.state(id)
    }

    async fn approval_call(&self, id: &str) -> Result<String> {
        let cached = self.store()?.approval_call(id);
        match cached {
            Ok(call) => return Ok(call),
            Err(error) if error.code == "NOT_FOUND" => {}
            Err(error) => return Err(error),
        }
        // Query receipts can precede the matching event on the control connection.
        let calls = self.store()?.session_call_ids(&self.session.session_id)?;
        for call in calls {
            let approvals = match self.remote.approvals(&call).await {
                Ok(approvals) => approvals,
                Err(error) if error.code == "NOT_FOUND" => continue,
                Err(error) => return Err(error),
            };
            self.store()?.cache_approvals(&call, &approvals)?;
            if approvals
                .as_array()
                .is_some_and(|values| values.iter().any(|a| a["approval_id"] == id))
            {
                return Ok(call);
            }
        }
        Err(Error::new(
            "NOT_FOUND",
            "Approval is not known to this session.",
        ))
    }

    pub async fn handle(&self, request: &Request) -> Result<Value> {
        if request.session_id != self.session.session_id {
            return Err(Error::new("SESSION_REQUIRED", "Session routing mismatch."));
        }
        match request.op.as_str() {
            "ready" => Ok(self.ready()),
            "submit" => {
                if self.closed.load(Ordering::SeqCst) {
                    return Err(Error::new("SESSION_REVOKED", "Owner scope is closed."));
                }
                let mut id = if request.id.is_empty() {
                    None
                } else {
                    Some(request.id.clone())
                };
                if request.operation == "calls.start" {
                    protocol::validate_start(&request.payload)?;
                } else if request.operation == "approvals.resolve" {
                    let approval = request.payload["approval_id"]
                        .as_str()
                        .ok_or_else(|| Error::new("INVALID_INPUT", "Approval ID is required."))?;
                    id = Some(self.approval_call(approval).await?);
                } else {
                    protocol::valid_id(id.as_deref().unwrap_or_default())?;
                }
                let key = if request.idempotency_key.is_empty() {
                    uuid::Uuid::new_v4().to_string()
                } else {
                    request.idempotency_key.clone()
                };
                protocol::valid_id(&key)?;
                let command = CommandRequest {
                    schema_version: "1".into(),
                    session_id: self.session.session_id.clone(),
                    operation: request.operation.clone(),
                    idempotency_key: key,
                    call_id: id,
                    payload: request.payload.clone(),
                };
                self.store()?.save_intent(&command)?;
                let receipt = match self.remote.submit(&command).await {
                    Ok(receipt) => receipt,
                    Err(error)
                        if matches!(
                            error.code.as_str(),
                            "RUNTIME_UNREACHABLE" | "PROTOCOL_ERROR"
                        ) =>
                    {
                        return Err(Error::new("SUBMISSION_UNKNOWN",&format!(
                            "Command submission is uncertain. Retry the identical request with idempotency key {}; never create a new attempt automatically.",
                            command.idempotency_key
                        )));
                    }
                    Err(error) => return Err(error),
                };
                if receipt.session_id != self.session.session_id || receipt.schema_version != "1" {
                    return Err(Error::new(
                        "PROTOCOL_ERROR",
                        "Command receipt correlation mismatch.",
                    ));
                }
                self.store()?.link_receipt(&command, &receipt)?;
                Ok(serde_json::to_value(receipt)
                    .map_err(|_| Error::new("PROTOCOL_ERROR", "Cannot encode receipt."))?)
            }
            "command_status" => {
                let receipt = self.remote.command(&request.id).await?;
                if receipt.session_id != self.session.session_id {
                    return Err(Error::new(
                        "SESSION_REQUIRED",
                        "Command belongs to another session.",
                    ));
                }
                self.store()?.save_receipt(&receipt)?;
                Ok(json!(receipt))
            }
            "call_status" => Ok(json!(self.owned_state(&request.id).await?)),
            "approvals" => {
                self.owned_state(&request.id).await?;
                let approvals = self.remote.approvals(&request.id).await?;
                self.store()?.cache_approvals(&request.id, &approvals)?;
                Ok(approvals)
            }
            "session_close" => {
                let value = self.remote.close_session(&self.session.session_id).await?;
                self.closed.store(true, Ordering::SeqCst);
                self.failure.send_replace(Some(Error::new(
                    "SESSION_REVOKED",
                    "Session explicitly closed.",
                )));
                Ok(value)
            }
            _ => Err(Error::new("INVALID_INPUT", "Unknown IPC operation.")),
        }
    }

    async fn stream(&self, request: &Request, write: &mut OwnedWriteHalf) -> Result<()> {
        protocol::valid_id(&request.id)?;
        if request.wait_seconds > 30 || request.follow && request.wait_seconds > 0 {
            return Err(Error::new(
                "INVALID_INPUT",
                "Use follow OR a bounded 0..30 second wait.",
            ));
        }
        if !self
            .store()?
            .session_call_ids(&self.session.session_id)?
            .contains(&request.id)
        {
            return Err(Error::new(
                "NOT_FOUND",
                "Call is not known to this session.",
            ));
        }
        // Subscribe before the first database read: ingestion cannot slip through the replay/live boundary.
        let mut notifications = self.notifications.subscribe();
        let mut shutdown = self.shutdown.subscribe();
        let mut cursor = request.after;
        let deadline = tokio::time::Instant::now() + Duration::from_secs(request.wait_seconds);
        loop {
            let batch = self.store()?.batch(&request.id, cursor)?;
            if let Some(state) = &batch.call_state {
                if state.session_id != self.session.session_id {
                    return Err(Error::new(
                        "SESSION_REQUIRED",
                        "Call belongs to another session.",
                    ));
                }
            }
            if request.follow {
                for event in &batch.events {
                    ipc::write_frame(write, &json!(event))
                        .await
                        .map_err(|mut error| {
                            error.next_cursor = Some(cursor);
                            error
                        })?;
                    cursor = event.sequence;
                }
                if batch.has_more {
                    continue;
                }
            } else if !batch.events.is_empty()
                || request.wait_seconds == 0
                || tokio::time::Instant::now() >= deadline
            {
                return ipc::write_frame(write, &json!(batch)).await;
            }
            if batch
                .call_state
                .as_ref()
                .is_some_and(|state| state.terminal() && cursor >= state.last_sequence)
            {
                if request.follow {
                    return Ok(());
                }
                return ipc::write_frame(write, &json!(batch)).await;
            }
            tokio::select! {
                _=shutdown.changed()=>{
                    if request.follow {return Ok(());}
                    let batch=self.store()?.batch(&request.id,cursor)?;
                    return ipc::write_frame(write,&json!(batch)).await;
                }
                result=notifications.recv()=>{
                    if result.is_err() {
                        let mut error=Error::new("SUBSCRIBER_SLOW","Subscriber fell behind; resume with the last cursor.");
                        error.next_cursor=Some(cursor);
                        return Err(error);
                    }
                }
                _=tokio::time::sleep_until(deadline),if !request.follow=>{}
                _=tokio::time::sleep(Duration::from_secs(1)),if request.follow=>{
                    let mut peer=libc::pollfd {fd:write.as_ref().as_raw_fd(),events:0,revents:0};
                    if unsafe {libc::poll(&mut peer,1,0)}>0
                        && peer.revents&(libc::POLLHUP|libc::POLLERR|libc::POLLNVAL)!=0 {
                        return Err(Error::new("IPC_DISCONNECTED","Viewer detached."));
                    }
                }
            }
        }
    }
}

impl RunningBroker {
    pub async fn close(self) -> Result<()> {
        self.broker.closed.store(true, Ordering::SeqCst);
        let close_result = tokio::time::timeout(
            Duration::from_secs(2),
            self.broker
                .remote
                .close_session(&self.broker.session.session_id),
        )
        .await;
        let calls = self
            .broker
            .store()?
            .session_call_ids(&self.broker.session.session_id)?;
        let deadline = tokio::time::Instant::now() + Duration::from_secs(3);
        loop {
            let mut all_terminal = true;
            for id in &calls {
                let cursor = self.broker.store()?.last_sequence(id)?;
                if let Ok(Ok(batch)) = tokio::time::timeout(
                    Duration::from_millis(500),
                    self.broker.remote.events(id, cursor),
                )
                .await
                {
                    for event in batch.events {
                        self.broker.ingest(&event)?;
                    }
                }
                if let Ok(Ok(state)) =
                    tokio::time::timeout(Duration::from_millis(500), self.broker.remote.state(id))
                        .await
                {
                    all_terminal &= state.terminal();
                    self.broker.store()?.save_state(&state)?;
                } else {
                    all_terminal = false;
                }
            }
            if all_terminal || tokio::time::Instant::now() >= deadline {
                break;
            }
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
        self.broker
            .store()?
            .interrupt_session(&self.broker.session.session_id)?;
        self.broker.shutdown.send_replace(true);
        for task in self.tasks {
            let _ = tokio::time::timeout(Duration::from_secs(2), task).await;
        }
        std::fs::remove_file(&self.broker.socket)
            .map_err(|_| Error::new("IPC_FAILED", "Cannot remove the closed session socket."))?;
        match close_result {
            Ok(Ok(_))=>Ok(()),
            _=>Err(Error::new("TERMINATION_UNKNOWN","Explicit owner revocation could not be acknowledged; the remote watchdog/lease must reconcile.")),
        }
    }
}
