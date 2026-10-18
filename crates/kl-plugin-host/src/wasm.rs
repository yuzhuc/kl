use crate::manifest::PluginManifest;
use serde_json::Value;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use wasmtime::component::{Component, Linker};
use wasmtime::{Config, Engine, Store, StoreContextMut, StoreLimits, StoreLimitsBuilder};

#[derive(Debug, thiserror::Error, Clone)]
pub enum WasmError {
    #[error("wasm engine error: {0}")]
    Engine(String),
    #[error("invalid wasm component: {0}")]
    InvalidComponent(String),
    #[error("wasm component instantiation failed: {0}")]
    Instantiation(String),
    #[error("wasm plugin health check failed: {0}")]
    Health(String),
    #[error("wasm plugin call timed out after {0:?}")]
    Timeout(Duration),
    #[error("wasm plugin trapped: {0}")]
    Trap(String),
    #[error("wasm plugin protocol error: {0}")]
    Protocol(String),
    #[error("wasm plugin is stopping: {0}")]
    Stopped(String),
}

struct WasmState {
    limits: StoreLimits,
    logs: Vec<String>,
    log_bytes: usize,
}

impl WasmState {
    fn new(memory_limit_bytes: usize) -> Self {
        Self {
            limits: StoreLimitsBuilder::new()
                .memory_size(memory_limit_bytes)
                .build(),
            logs: Vec::new(),
            log_bytes: 0,
        }
    }

    fn log(&mut self, mut message: String) {
        const MAX_LOG_ENTRIES: usize = 64;
        const MAX_LOG_BYTES: usize = 8 * 1024;
        let remaining = MAX_LOG_BYTES.saturating_sub(self.log_bytes);
        if self.logs.len() >= MAX_LOG_ENTRIES || remaining == 0 {
            return;
        }
        if message.len() > remaining {
            let mut end = remaining;
            while !message.is_char_boundary(end) {
                end -= 1;
            }
            message.truncate(end);
        }
        self.log_bytes += message.len();
        self.logs.push(message);
    }
}

pub(crate) struct WasmInstance {
    manifest: PluginManifest,
    component: Component,
    engine: Engine,
    call_timeout: Duration,
    call_lock: Mutex<()>,
    in_flight: Arc<AtomicUsize>,
    draining: AtomicBool,
    healthy: AtomicBool,
}

impl WasmInstance {
    pub fn start(
        manifest: PluginManifest,
        manifest_path: &std::path::Path,
    ) -> Result<Self, WasmError> {
        let mut config = Config::new();
        config.wasm_component_model(true);
        config.epoch_interruption(true);
        let engine = Engine::new(&config).map_err(|error| WasmError::Engine(error.to_string()))?;
        let entrypoint = manifest.entrypoint_path(manifest_path);
        let component = Component::from_file(&engine, &entrypoint).map_err(|error| {
            WasmError::InvalidComponent(format!("{}: {error:#}", entrypoint.display()))
        })?;
        let instance = Self {
            call_timeout: Duration::from_millis(manifest.call_timeout_ms.max(1)),
            manifest,
            component,
            engine,
            call_lock: Mutex::new(()),
            in_flight: Arc::new(AtomicUsize::new(0)),
            draining: AtomicBool::new(false),
            healthy: AtomicBool::new(true),
        };
        let timeout = Duration::from_millis(instance.manifest.health_timeout_ms.max(100));
        let raw = instance.call_string("health", None, timeout)?;
        let health: Value = serde_json::from_str(&raw)
            .map_err(|error| WasmError::Health(format!("invalid health JSON: {error}")))?;
        if health.get("ok").and_then(Value::as_bool) != Some(true) {
            return Err(WasmError::Health(raw));
        }
        Ok(instance)
    }

    pub fn manifest(&self) -> &PluginManifest {
        &self.manifest
    }

    pub fn is_healthy(&self) -> bool {
        !self.draining.load(Ordering::Relaxed) && self.healthy.load(Ordering::Relaxed)
    }

    pub fn execute(&self, input: Value) -> Result<Value, WasmError> {
        let input = serde_json::to_string(&input)
            .map_err(|error| WasmError::Protocol(format!("cannot encode input: {error}")))?;
        let raw = self.call_string("execute", Some(&input), self.call_timeout)?;
        serde_json::from_str(&raw)
            .map_err(|error| WasmError::Protocol(format!("invalid execute JSON: {error}")))
    }

    pub fn stop(&self, timeout: Duration) {
        self.draining.store(true, Ordering::SeqCst);
        let deadline = Instant::now() + timeout;
        while self.in_flight.load(Ordering::SeqCst) > 0 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
    }

    fn begin_request(&self) -> Result<InFlightGuard, WasmError> {
        if self.draining.load(Ordering::SeqCst) {
            return Err(WasmError::Stopped(self.manifest.id.clone()));
        }
        self.in_flight.fetch_add(1, Ordering::SeqCst);
        Ok(InFlightGuard {
            in_flight: self.in_flight.clone(),
        })
    }

    fn call_string(
        &self,
        export: &str,
        input: Option<&str>,
        timeout: Duration,
    ) -> Result<String, WasmError> {
        let _request = self.begin_request()?;
        let _call = self
            .call_lock
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let memory_limit = self
            .manifest
            .memory_limit_mb
            .max(1)
            .saturating_mul(1024 * 1024) as usize;
        let mut store = Store::new(&self.engine, WasmState::new(memory_limit));
        store.limiter(|state| &mut state.limits);
        store.set_epoch_deadline(1);

        let timed_out = Arc::new(AtomicBool::new(false));
        let timeout_flag = timed_out.clone();
        let engine = self.engine.clone();
        let (done, wait) = mpsc::channel();
        std::thread::spawn(move || {
            if wait.recv_timeout(timeout).is_err() {
                timeout_flag.store(true, Ordering::SeqCst);
                engine.increment_epoch();
            }
        });

        let result = self.invoke(&mut store, export, input);
        let _ = done.send(());
        if timed_out.load(Ordering::SeqCst) {
            self.healthy.store(false, Ordering::Relaxed);
            return Err(WasmError::Timeout(timeout));
        }
        match result {
            Ok(value) => {
                self.healthy.store(true, Ordering::Relaxed);
                Ok(value)
            }
            Err(error) => {
                self.healthy.store(false, Ordering::Relaxed);
                Err(error)
            }
        }
    }

    fn invoke(
        &self,
        store: &mut Store<WasmState>,
        export: &str,
        input: Option<&str>,
    ) -> Result<String, WasmError> {
        let mut linker = Linker::new(&self.engine);
        add_host_imports(&mut linker)?;
        let instance = linker
            .instantiate(&mut *store, &self.component)
            .map_err(|error| WasmError::Instantiation(format!("{error:#}")))?;
        match input {
            Some(input) => {
                let function = instance
                    .get_typed_func::<(String,), (String,)>(&mut *store, export)
                    .map_err(|error| WasmError::Protocol(format!("{export}: {error:#}")))?;
                function
                    .call(&mut *store, (input.to_owned(),))
                    .map(|(value,)| value)
                    .map_err(|error| WasmError::Trap(format!("{error:#}")))
            }
            None => {
                let function = instance
                    .get_typed_func::<(), (String,)>(&mut *store, export)
                    .map_err(|error| WasmError::Protocol(format!("{export}: {error:#}")))?;
                function
                    .call(&mut *store, ())
                    .map(|(value,)| value)
                    .map_err(|error| WasmError::Trap(format!("{error:#}")))
            }
        }
    }
}

struct InFlightGuard {
    in_flight: Arc<AtomicUsize>,
}

impl Drop for InFlightGuard {
    fn drop(&mut self) {
        self.in_flight.fetch_sub(1, Ordering::SeqCst);
    }
}

fn add_host_imports(linker: &mut Linker<WasmState>) -> Result<(), WasmError> {
    linker
        .root()
        .func_wrap(
            "host-log",
            |mut store: StoreContextMut<'_, WasmState>, (message,): (String,)| {
                store.data_mut().log(message);
                Ok(())
            },
        )
        .map_err(|error| WasmError::Protocol(format!("host-log import: {error:#}")))?;
    linker
        .root()
        .func_wrap(
            "host-time",
            |_store: StoreContextMut<'_, WasmState>, (): ()| -> wasmtime::Result<(u64,)> {
                let millis = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_millis();
                Ok((u64::try_from(millis).unwrap_or(u64::MAX),))
            },
        )
        .map_err(|error| WasmError::Protocol(format!("host-time import: {error:#}")))?;
    Ok(())
}
