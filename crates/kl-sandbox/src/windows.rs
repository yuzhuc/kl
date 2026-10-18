//! Windows AppContainer + Job Object sandbox backend.
//!
//! The AppContainer token has no filesystem access outside the ACL roots
//! granted here. The kill-on-close job object keeps descendants attached to
//! the same lifetime. Network capabilities are deliberately not granted; full
//! network isolation is not claimed because it is not verified by this build.

use crate::{
    SandboxBackend, SandboxCapabilities, SandboxConfig, SandboxError, SandboxPolicy, SandboxProbe,
    SandboxRequest, enforce_policy, path_key,
};
use kl_exec::CommandSpec;
use serde::Serialize;
use std::collections::HashSet;
use std::ffi::OsStr;
use std::fs::File;
use std::io::{BufReader, Read, Write};
use std::mem::{size_of, zeroed};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::FromRawHandle;
use std::path::{Path, PathBuf};
use std::ptr::{null, null_mut};
use std::sync::{Arc, Mutex, OnceLock};
use windows_sys::Win32::Foundation::{
    CloseHandle, GENERIC_ALL, GENERIC_EXECUTE, GENERIC_READ, GetLastError, HANDLE,
    HANDLE_FLAG_INHERIT, LocalFree, SetHandleInformation, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Security::Authorization::{
    EXPLICIT_ACCESS_W, GRANT_ACCESS, GetNamedSecurityInfoW, SE_FILE_OBJECT, SetEntriesInAclW,
    SetNamedSecurityInfoW, TRUSTEE_IS_SID, TRUSTEE_IS_UNKNOWN,
};
use windows_sys::Win32::Security::Isolation::{
    CreateAppContainerProfile, DeriveAppContainerSidFromAppContainerName,
};
use windows_sys::Win32::Security::{
    ACCESS_ALLOWED_ACE, ACE_HEADER, ACL, ACL_REVISION, ACL_SIZE_INFORMATION, AclSizeInformation,
    AddAce, CONTAINER_INHERIT_ACE, DACL_SECURITY_INFORMATION, EqualSid, FreeSid, GetAce,
    GetAclInformation, InitializeAcl, OBJECT_INHERIT_ACE, PROTECTED_DACL_SECURITY_INFORMATION,
    PSID, SECURITY_ATTRIBUTES, SECURITY_CAPABILITIES,
};

use windows_sys::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
    SetInformationJobObject, TerminateJobObject,
};
use windows_sys::Win32::System::Pipes::CreatePipe;
use windows_sys::Win32::System::Threading::{
    CREATE_NO_WINDOW, CREATE_SUSPENDED, CreateProcessW, DeleteProcThreadAttributeList,
    EXTENDED_STARTUPINFO_PRESENT, GetExitCodeProcess, INFINITE, InitializeProcThreadAttributeList,
    PROC_THREAD_ATTRIBUTE_HANDLE_LIST, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
    PROCESS_INFORMATION, ResumeThread, STARTF_USESTDHANDLES, STARTUPINFOEXW,
    UpdateProcThreadAttribute, WaitForSingleObject,
};

const APPCONTAINER_NAME: &str = "KL.Sandbox.v3";
const APPCONTAINER_DISPLAY_NAME: &str = "KL sandbox";
const APPCONTAINER_DESCRIPTION: &str = "KL OS execution sandbox";

static GRANTED_ACLS: OnceLock<Mutex<HashSet<(String, u32, bool)>>> = OnceLock::new();
static SANDBOX_LOCK: OnceLock<Mutex<()>> = OnceLock::new();

pub(crate) fn app_container_backend() -> Arc<dyn SandboxBackend> {
    Arc::new(AppContainerBackend::new())
}

struct AppContainerBackend {
    sid: Option<Sid>,
    error: Option<String>,
}

impl AppContainerBackend {
    fn new() -> Self {
        match create_app_container_sid() {
            Ok(sid) => Self {
                sid: Some(sid),
                error: None,
            },
            Err(error) => Self {
                sid: None,
                error: Some(error),
            },
        }
    }
}

impl SandboxBackend for AppContainerBackend {
    fn name(&self) -> &'static str {
        "appcontainer"
    }

    fn probe(&self, _config: &SandboxConfig) -> SandboxProbe {
        match &self.sid {
            Some(_) => SandboxProbe {
                available: true,
                detail: "Windows AppContainer token and kill-on-close Job Object are available"
                    .to_owned(),
                capabilities: SandboxCapabilities::app_container(),
            },
            None => SandboxProbe {
                available: false,
                detail: format!(
                    "AppContainer profile unavailable: {}",
                    self.error.as_deref().unwrap_or("unknown error")
                ),
                capabilities: SandboxCapabilities::unimplemented(),
            },
        }
    }

    fn capabilities(&self) -> SandboxCapabilities {
        SandboxCapabilities::app_container()
    }

    fn spawn(
        &self,
        spec: &CommandSpec,
        policy: &SandboxPolicy,
    ) -> Result<crate::SandboxChild, SandboxError> {
        let Some(sid) = &self.sid else {
            return Err(SandboxError::Unavailable(
                self.error
                    .clone()
                    .unwrap_or_else(|| "AppContainer profile unavailable".to_owned()),
            ));
        };
        let runner = runner_path()?;
        let (config_path, config_dir) = write_runner_config(spec)?;
        let mut runner_spec = CommandSpec::new(
            runner.to_string_lossy().to_string(),
            vec![config_path.to_string_lossy().to_string()],
            spec.cwd.clone(),
        );
        runner_spec.timeout = spec.timeout;
        runner_spec.max_output_bytes = spec.max_output_bytes;
        let mut effective_policy = policy.clone();
        effective_policy.read_roots.push(config_dir.clone());
        let lock = SANDBOX_LOCK.get_or_init(|| Mutex::new(()));
        let _guard = lock.lock().unwrap_or_else(|error| error.into_inner());
        grant_policy(&effective_policy, sid)?;
        grant_program(spec, sid)?;
        grant_program(&runner_spec, sid)?;
        let mut child = launch(&runner_spec, sid)?;
        child.cleanup_dirs.push(config_dir);
        Ok(crate::SandboxChild::from_app_container(child))
    }
    fn enforce(
        &self,
        request: &SandboxRequest,
        policy: &SandboxPolicy,
    ) -> Result<(), SandboxError> {
        enforce_policy(request, policy)
    }

    fn grant(&self, policy: &SandboxPolicy) -> Result<(), SandboxError> {
        let Some(sid) = &self.sid else {
            return Err(SandboxError::Unavailable(
                self.error
                    .clone()
                    .unwrap_or_else(|| "AppContainer profile unavailable".to_owned()),
            ));
        };
        for root in &policy.read_roots {
            apply_ace(root, sid, GENERIC_READ | GENERIC_EXECUTE)
                .map_err(|error| SandboxError::Spawn(format!("ACL grant failed: {error}")))?;
        }
        for root in &policy.write_roots {
            apply_ace(root, sid, GENERIC_ALL)
                .map_err(|error| SandboxError::Spawn(format!("ACL grant failed: {error}")))?;
        }
        for path in &policy.deny_paths {
            protect_sensitive_path(path, sid.ptr())
                .map_err(|error| SandboxError::Spawn(format!("ACL protect failed: {error}")))?;
        }
        Ok(())
    }
}

pub(crate) struct AppContainerChild {
    process: OwnedHandle,
    job: Option<OwnedHandle>,
    pub(crate) stdin: Option<Box<dyn Write + Send>>,
    pub(crate) stdout: Option<Box<dyn Read + Send>>,
    pub(crate) stderr: Option<Box<dyn Read + Send>>,
    exit_code: Option<u32>,
    cleanup_dirs: Vec<PathBuf>,
}

impl AppContainerChild {
    pub(crate) fn id(&self) -> u32 {
        self.process.id()
    }

    pub(crate) fn try_wait(&mut self) -> Result<Option<i32>, SandboxError> {
        if let Some(code) = self.exit_code {
            return Ok(Some(code as i32));
        }
        let wait = unsafe { WaitForSingleObject(self.process.handle(), 0) };
        match wait {
            WAIT_OBJECT_0 => {
                let code = exit_code(self.process.handle())?;
                self.exit_code = Some(code);
                Ok(Some(code as i32))
            }
            WAIT_TIMEOUT => Ok(None),
            _ => Err(SandboxError::Spawn(format!(
                "WaitForSingleObject failed: {}",
                last_error()
            ))),
        }
    }

    pub(crate) fn wait(&mut self) -> Result<i32, SandboxError> {
        if let Some(code) = self.exit_code {
            return Ok(code as i32);
        }
        let wait = unsafe { WaitForSingleObject(self.process.handle(), INFINITE) };
        if wait != WAIT_OBJECT_0 {
            return Err(SandboxError::Spawn(format!(
                "WaitForSingleObject failed: {}",
                last_error()
            )));
        }
        let code = exit_code(self.process.handle())?;
        self.exit_code = Some(code);
        Ok(code as i32)
    }

    pub(crate) fn kill(&mut self) -> Result<(), SandboxError> {
        if self.exit_code.is_none() {
            if let Some(job) = &self.job {
                unsafe {
                    TerminateJobObject(job.handle(), 1);
                }
            }
            let _ = self.wait();
        }
        Ok(())
    }

    pub(crate) fn cleanup_descendants(&mut self) -> Result<(), SandboxError> {
        if let Some(job) = self.job.take() {
            unsafe {
                TerminateJobObject(job.handle(), 1);
            }
        }
        Ok(())
    }
}

impl Drop for AppContainerChild {
    fn drop(&mut self) {
        let _ = self.kill();
    }
}

struct OwnedHandle(HANDLE);

unsafe impl Send for OwnedHandle {}
unsafe impl Sync for OwnedHandle {}

impl OwnedHandle {
    fn new(handle: HANDLE) -> Self {
        Self(handle)
    }

    fn handle(&self) -> HANDLE {
        self.0
    }

    fn id(&self) -> u32 {
        self.0 as usize as u32
    }
}

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
}

struct Sid(usize);

unsafe impl Send for Sid {}
unsafe impl Sync for Sid {}

impl Sid {
    fn ptr(&self) -> PSID {
        self.0 as PSID
    }
}

impl Drop for Sid {
    fn drop(&mut self) {
        if self.0 != 0 {
            unsafe {
                FreeSid(self.ptr());
            }
        }
    }
}

fn create_app_container_sid() -> Result<Sid, String> {
    let lock = SANDBOX_LOCK.get_or_init(|| Mutex::new(()));
    let _guard = lock.lock().unwrap_or_else(|error| error.into_inner());
    let name = wide(APPCONTAINER_NAME);
    let display = wide(APPCONTAINER_DISPLAY_NAME);
    let description = wide(APPCONTAINER_DESCRIPTION);
    let mut sid: PSID = null_mut();
    let hr = unsafe {
        CreateAppContainerProfile(
            name.as_ptr(),
            display.as_ptr(),
            description.as_ptr(),
            null(),
            0,
            &mut sid,
        )
    };
    if hr >= 0 && !sid.is_null() {
        return Ok(Sid(sid as usize));
    }

    let mut derived: PSID = null_mut();
    let hr = unsafe { DeriveAppContainerSidFromAppContainerName(name.as_ptr(), &mut derived) };
    if hr >= 0 && !derived.is_null() {
        Ok(Sid(derived as usize))
    } else {
        Err(format!(
            "CreateAppContainerProfile/DeriveAppContainerSidFromAppContainerName failed: 0x{hr:08x}"
        ))
    }
}
fn grant_policy(policy: &SandboxPolicy, sid: &Sid) -> Result<(), SandboxError> {
    let mut grants: Vec<(PathBuf, u32, bool)> = Vec::new();
    for root in &policy.read_roots {
        push_grant(
            &mut grants,
            root.clone(),
            GENERIC_READ | GENERIC_EXECUTE,
            false,
        );
    }
    for root in &policy.write_roots {
        push_grant(&mut grants, root.clone(), GENERIC_ALL, false);
    }
    for (path, access, _) in grants {
        apply_ace(&path, sid, access).map_err(|error| {
            SandboxError::Spawn(format!("ACL grant failed for {}: {error}", path.display()))
        })?;
    }
    for path in &policy.deny_paths {
        protect_sensitive_path(path, sid.ptr()).map_err(|error| {
            SandboxError::Spawn(format!(
                "ACL protect failed for {}: {error}",
                path.display()
            ))
        })?;
    }
    Ok(())
}

fn grant_program(spec: &CommandSpec, sid: &Sid) -> Result<(), SandboxError> {
    let Some(program) = find_program(&spec.program) else {
        return Ok(());
    };
    if is_protected_install_path(&program) {
        return Ok(());
    }
    apply_ace(&program, sid, GENERIC_READ | GENERIC_EXECUTE).map_err(|error| {
        SandboxError::Spawn(format!(
            "ACL grant failed for {}: {error}",
            program.display()
        ))
    })
}
fn is_protected_install_path(path: &Path) -> bool {
    path_key(path).starts_with("c:\\windows")
}
fn push_grant(grants: &mut Vec<(PathBuf, u32, bool)>, path: PathBuf, access: u32, deny: bool) {
    if !path.exists() {
        return;
    }
    let key = path.to_string_lossy().to_ascii_lowercase();
    let index = grants.iter().position(|(existing, _, existing_deny)| {
        existing.to_string_lossy().to_ascii_lowercase() == key && *existing_deny == deny
    });
    if let Some(index) = index {
        grants[index].1 |= access;
    } else {
        grants.push((path, access, deny));
    }
}

fn apply_ace(path: &Path, sid: &Sid, access: u32) -> Result<(), String> {
    let key = (path.to_string_lossy().to_ascii_lowercase(), access, false);
    let granted = GRANTED_ACLS.get_or_init(|| Mutex::new(HashSet::new()));
    {
        let mut granted = granted.lock().unwrap_or_else(|error| error.into_inner());
        if !granted.insert(key) {
            return Ok(());
        }
    }

    let wide_path = wide(path);
    let mut old_dacl: *mut ACL = null_mut();
    let mut descriptor = null_mut();
    let status = unsafe {
        GetNamedSecurityInfoW(
            wide_path.as_ptr(),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION,
            null_mut(),
            null_mut(),
            &mut old_dacl,
            null_mut(),
            &mut descriptor,
        )
    };
    if status != 0 {
        return Err(format!(
            "GetNamedSecurityInfoW({}) failed: {status}",
            path.display()
        ));
    }

    let mut entry: EXPLICIT_ACCESS_W = unsafe { zeroed() };
    entry.grfAccessPermissions = access;
    entry.grfAccessMode = GRANT_ACCESS;
    entry.grfInheritance = if path.is_dir() {
        OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE
    } else {
        0
    };
    entry.Trustee.TrusteeForm = TRUSTEE_IS_SID;
    entry.Trustee.TrusteeType = TRUSTEE_IS_UNKNOWN;
    entry.Trustee.ptstrName = sid.ptr() as *mut u16;

    let mut new_dacl: *mut ACL = null_mut();
    let status = unsafe { SetEntriesInAclW(1, &entry, old_dacl, &mut new_dacl) };
    if status != 0 {
        unsafe {
            LocalFree(descriptor);
        }
        return Err(format!("SetEntriesInAclW failed: {status}"));
    }
    let status = unsafe {
        SetNamedSecurityInfoW(
            wide_path.as_ptr(),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION,
            null_mut(),
            null_mut(),
            new_dacl,
            null_mut(),
        )
    };
    unsafe {
        LocalFree(descriptor);
        if !new_dacl.is_null() {
            LocalFree(new_dacl.cast());
        }
    }
    if status != 0 {
        return Err(format!("SetNamedSecurityInfoW failed: {status}"));
    }
    Ok(())
}

fn protect_sensitive_path(path: &Path, sid: PSID) -> Result<(), String> {
    let wide_path = wide(path);
    let mut old_dacl: *mut ACL = null_mut();
    let mut descriptor = null_mut();
    let status = unsafe {
        GetNamedSecurityInfoW(
            wide_path.as_ptr(),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION,
            null_mut(),
            null_mut(),
            &mut old_dacl,
            null_mut(),
            &mut descriptor,
        )
    };
    if status != 0 || old_dacl.is_null() {
        return Err(format!(
            "GetNamedSecurityInfoW({}) failed: {status}",
            path.display()
        ));
    }

    let mut info: ACL_SIZE_INFORMATION = unsafe { zeroed() };
    if unsafe {
        GetAclInformation(
            old_dacl,
            &mut info as *mut _ as *mut _,
            size_of::<ACL_SIZE_INFORMATION>() as u32,
            AclSizeInformation,
        )
    } == 0
    {
        unsafe {
            LocalFree(descriptor);
        }
        return Err("GetAclInformation failed".to_owned());
    }

    let size = info.AclBytesInUse.max(64);
    let mut acl_bytes = vec![0u8; size as usize];
    let new_acl = acl_bytes.as_mut_ptr().cast::<ACL>();
    if unsafe { InitializeAcl(new_acl, size, ACL_REVISION) } == 0 {
        unsafe {
            LocalFree(descriptor);
        }
        return Err("InitializeAcl failed".to_owned());
    }
    for index in 0..info.AceCount {
        let mut ace: *mut core::ffi::c_void = null_mut();
        if unsafe { GetAce(old_dacl, index, &mut ace) } == 0 || ace.is_null() {
            unsafe {
                LocalFree(descriptor);
            }
            return Err(format!("GetAce({index}) failed"));
        }
        let header = unsafe { &*(ace as *const ACE_HEADER) };
        if header.AceType == 0 {
            let allowed = unsafe { &*(ace as *const ACCESS_ALLOWED_ACE) };
            let ace_sid = &allowed.SidStart as *const u32 as PSID;
            if unsafe { EqualSid(ace_sid, sid) } != 0 {
                continue;
            }
        }
        let mut bytes =
            unsafe { std::slice::from_raw_parts(ace as *const u8, header.AceSize as usize) }
                .to_vec();
        if bytes.len() > 1 {
            bytes[1] &= !0x10;
        }
        if unsafe {
            AddAce(
                new_acl,
                ACL_REVISION,
                u32::MAX,
                bytes.as_ptr().cast(),
                bytes.len() as u32,
            )
        } == 0
        {
            unsafe {
                LocalFree(descriptor);
            }
            return Err(format!("AddAce({index}) failed"));
        }
    }

    let status = unsafe {
        SetNamedSecurityInfoW(
            wide_path.as_ptr(),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            null_mut(),
            null_mut(),
            new_acl,
            null_mut(),
        )
    };
    unsafe {
        LocalFree(descriptor);
    }
    if status != 0 {
        return Err(format!("SetNamedSecurityInfoW failed: {status}"));
    }
    Ok(())
}
fn launch(spec: &CommandSpec, sid: &Sid) -> Result<AppContainerChild, SandboxError> {
    let security = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: null_mut(),
        bInheritHandle: 1,
    };
    let mut stdin_read: HANDLE = null_mut();
    let mut stdin_write: HANDLE = null_mut();
    let mut stdout_read: HANDLE = null_mut();
    let mut stdout_write: HANDLE = null_mut();
    let mut stderr_read: HANDLE = null_mut();
    let mut stderr_write: HANDLE = null_mut();
    unsafe {
        if CreatePipe(&mut stdin_read, &mut stdin_write, &security, 0) == 0
            || CreatePipe(&mut stdout_read, &mut stdout_write, &security, 0) == 0
            || CreatePipe(&mut stderr_read, &mut stderr_write, &security, 0) == 0
        {
            close_handle(stdin_read);
            close_handle(stdin_write);
            close_handle(stdout_read);
            close_handle(stdout_write);
            close_handle(stderr_read);
            close_handle(stderr_write);
            return Err(SandboxError::Spawn(format!(
                "CreatePipe failed: {}",
                last_error()
            )));
        }
    }
    for handle in [stdin_write, stdout_read, stderr_read] {
        unsafe {
            SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0);
        }
    }

    let mut attribute_size = 0usize;
    unsafe {
        InitializeProcThreadAttributeList(null_mut(), 2, 0, &mut attribute_size);
    }
    let mut attributes = vec![0u8; attribute_size];
    let attribute_list = attributes.as_mut_ptr().cast();
    if unsafe { InitializeProcThreadAttributeList(attribute_list, 2, 0, &mut attribute_size) } == 0
    {
        cleanup_handles(
            stdin_read,
            stdin_write,
            stdout_read,
            stdout_write,
            stderr_read,
            stderr_write,
        );
        return Err(SandboxError::Spawn(format!(
            "InitializeProcThreadAttributeList failed: {}",
            last_error()
        )));
    }

    let capabilities = SECURITY_CAPABILITIES {
        AppContainerSid: sid.ptr(),
        Capabilities: null_mut(),
        CapabilityCount: 0,
        Reserved: 0,
    };
    let attribute_ok = unsafe {
        UpdateProcThreadAttribute(
            attribute_list,
            0,
            PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES as usize,
            &capabilities as *const _ as *const _,
            size_of::<SECURITY_CAPABILITIES>(),
            null_mut(),
            null(),
        )
    };
    let handles = [stdin_read, stdout_write, stderr_write];
    let handle_ok = unsafe {
        UpdateProcThreadAttribute(
            attribute_list,
            0,
            PROC_THREAD_ATTRIBUTE_HANDLE_LIST as usize,
            handles.as_ptr().cast(),
            size_of::<HANDLE>() * handles.len(),
            null_mut(),
            null(),
        )
    };
    if attribute_ok == 0 || handle_ok == 0 {
        unsafe {
            DeleteProcThreadAttributeList(attribute_list);
        }
        cleanup_handles(
            stdin_read,
            stdin_write,
            stdout_read,
            stdout_write,
            stderr_read,
            stderr_write,
        );
        return Err(SandboxError::Spawn(format!(
            "UpdateProcThreadAttribute failed: {}",
            last_error()
        )));
    }

    let mut startup: STARTUPINFOEXW = unsafe { zeroed() };
    startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = stdin_read;
    startup.StartupInfo.hStdOutput = stdout_write;
    startup.StartupInfo.hStdError = stderr_write;
    startup.lpAttributeList = attribute_list;

    let application = find_program(&spec.program).map(wide);
    let mut command_line = command_line(&spec.program, &spec.args);
    let current_dir = wide(&spec.cwd);

    let mut process: PROCESS_INFORMATION = unsafe { zeroed() };
    let flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW;
    let created = unsafe {
        CreateProcessW(
            application.as_ref().map_or(null(), |path| path.as_ptr()),
            command_line.as_mut_ptr(),
            null(),
            null(),
            1,
            flags,
            null(),
            current_dir.as_ptr(),
            &startup as *const STARTUPINFOEXW as *const _,
            &mut process,
        )
    };
    unsafe {
        DeleteProcThreadAttributeList(attribute_list);
    }

    if created == 0 {
        cleanup_handles(
            stdin_read,
            stdin_write,
            stdout_read,
            stdout_write,
            stderr_read,
            stderr_write,
        );
        return Err(SandboxError::Spawn(format!(
            "CreateProcessW failed: {}",
            last_error()
        )));
    }
    let job = unsafe { CreateJobObjectW(null(), null()) };
    if job.is_null() {
        unsafe {
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        cleanup_handles(
            stdin_read,
            stdin_write,
            stdout_read,
            stdout_write,
            stderr_read,
            stderr_write,
        );
        return Err(SandboxError::Spawn(format!(
            "CreateJobObjectW failed: {}",
            last_error()
        )));
    }
    let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { zeroed() };
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    let configured = unsafe {
        SetInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            &limits as *const _ as *const _,
            size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        )
    };
    let assigned = unsafe { AssignProcessToJobObject(job, process.hProcess) };
    if configured == 0 || assigned == 0 {
        unsafe {
            TerminateJobObject(job, 1);
            CloseHandle(job);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        cleanup_handles(
            stdin_read,
            stdin_write,
            stdout_read,
            stdout_write,
            stderr_read,
            stderr_write,
        );
        return Err(SandboxError::Spawn(format!(
            "job setup failed: {}",
            last_error()
        )));
    }

    let resumed = unsafe { ResumeThread(process.hThread) };
    unsafe {
        CloseHandle(process.hThread);
    }
    if resumed == u32::MAX {
        unsafe {
            TerminateJobObject(job, 1);
            CloseHandle(job);
            CloseHandle(process.hProcess);
        }
        cleanup_handles(
            stdin_read,
            stdin_write,
            stdout_read,
            stdout_write,
            stderr_read,
            stderr_write,
        );
        return Err(SandboxError::Spawn(format!(
            "ResumeThread failed: {}",
            last_error()
        )));
    }

    close_handle(stdin_read);
    close_handle(stdout_write);
    close_handle(stderr_write);
    let stdin = unsafe { File::from_raw_handle(stdin_write) };
    let stdout = unsafe { File::from_raw_handle(stdout_read) };
    let stderr = unsafe { File::from_raw_handle(stderr_read) };

    Ok(AppContainerChild {
        process: OwnedHandle::new(process.hProcess),
        job: Some(OwnedHandle::new(job)),
        stdin: Some(Box::new(stdin)),
        stdout: Some(Box::new(BufReader::new(stdout))),
        stderr: Some(Box::new(BufReader::new(stderr))),
        exit_code: None,
        cleanup_dirs: Vec::new(),
    })
}

#[derive(Serialize)]
struct RunnerConfig {
    program: String,
    args: Vec<String>,
    cwd: PathBuf,
    clear_env: bool,
    env: Vec<(String, String)>,
}

fn runner_path() -> Result<PathBuf, SandboxError> {
    let current = std::env::current_exe().map_err(|error| {
        SandboxError::Spawn(format!("cannot locate current executable: {error}"))
    })?;
    let mut candidates = Vec::new();
    if let Some(parent) = current.parent() {
        candidates.push(parent.join("kl-sandbox-runner.exe"));
        if let Some(grandparent) = parent.parent() {
            candidates.push(grandparent.join("kl-sandbox-runner.exe"));
        }
    }
    candidates
        .into_iter()
        .find(|path| path.exists())
        .ok_or_else(|| {
            SandboxError::Spawn(
                "kl-sandbox-runner.exe is missing next to the current executable".to_owned(),
            )
        })
}

fn write_runner_config(spec: &CommandSpec) -> Result<(PathBuf, PathBuf), SandboxError> {
    let root = std::env::temp_dir().join("kl-sandbox");
    std::fs::create_dir_all(&root)?;
    let unique = format!(
        "{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or(0)
    );
    let dir = root.join(unique);
    std::fs::create_dir(&dir)?;
    let path = dir.join("config.json");
    let program = find_program(&spec.program)
        .map(|path| path.to_string_lossy().to_string())
        .unwrap_or_else(|| spec.program.clone());
    let mut env = spec.env.clone();
    if !spec.clear_env {
        for name in [
            "PATH",
            "PATHEXT",
            "SystemRoot",
            "ComSpec",
            "WINDIR",
            "TEMP",
            "TMP",
        ] {
            if !env
                .iter()
                .any(|(existing, _)| existing.eq_ignore_ascii_case(name))
            {
                if let Ok(value) = std::env::var(name) {
                    env.push((name.to_owned(), value));
                }
            }
        }
    }
    let config = RunnerConfig {
        program,
        args: spec.args.clone(),
        cwd: spec.cwd.clone(),
        clear_env: spec.clear_env,
        env,
    };
    let bytes = serde_json::to_vec(&config)
        .map_err(|error| SandboxError::Spawn(format!("cannot encode sandbox config: {error}")))?;
    std::fs::write(&path, bytes)?;
    Ok((path, dir))
}
fn command_line(program: &str, args: &[String]) -> Vec<u16> {
    let mut line = quote_arg(program);
    for arg in args {
        line.push(' ');
        line.push_str(&quote_arg(arg));
    }
    wide(line)
}

fn quote_arg(arg: &str) -> String {
    if !arg.is_empty() && !arg.contains([' ', '\t', '"']) && !arg.ends_with('\\') {
        return arg.to_owned();
    }
    let mut quoted = String::with_capacity(arg.len() + 2);
    quoted.push('"');
    let mut backslashes = 0usize;
    for character in arg.chars() {
        if character == '\\' {
            backslashes += 1;
        } else if character == '"' {
            quoted.push_str(&"\\".repeat(backslashes * 2 + 1));
            quoted.push('"');
            backslashes = 0;
        } else {
            quoted.push_str(&"\\".repeat(backslashes));
            backslashes = 0;
            quoted.push(character);
        }
    }
    quoted.push_str(&"\\".repeat(backslashes * 2));
    quoted.push('"');
    quoted
}

fn find_program(program: &str) -> Option<PathBuf> {
    let path = Path::new(program);
    if path.is_absolute() || program.contains('\\') || program.contains('/') {
        return path.exists().then(|| path.to_path_buf());
    }
    let search = std::env::var_os("PATH")?;
    let extensions = std::env::var_os("PATHEXT")
        .map(|value| {
            value
                .to_string_lossy()
                .split(';')
                .map(str::to_owned)
                .collect::<Vec<_>>()
        })
        .unwrap_or_else(|| vec![".EXE".to_owned(), ".CMD".to_owned(), ".BAT".to_owned()]);
    for directory in std::env::split_paths(&search) {
        let candidate = directory.join(program);
        if candidate.is_file() {
            return Some(candidate);
        }
        for extension in &extensions {
            let candidate = directory.join(format!("{program}{extension}"));
            if candidate.is_file() {
                return Some(candidate);
            }
        }
    }
    None
}

fn exit_code(process: HANDLE) -> Result<u32, SandboxError> {
    let mut code = 0u32;
    if unsafe { GetExitCodeProcess(process, &mut code) } == 0 {
        return Err(SandboxError::Spawn(format!(
            "GetExitCodeProcess failed: {}",
            last_error()
        )));
    }
    Ok(code)
}

fn close_handle(handle: HANDLE) {
    if !handle.is_null() {
        unsafe {
            CloseHandle(handle);
        }
    }
}

fn cleanup_handles(
    stdin_read: HANDLE,
    stdin_write: HANDLE,
    stdout_read: HANDLE,
    stdout_write: HANDLE,
    stderr_read: HANDLE,
    stderr_write: HANDLE,
) {
    for handle in [
        stdin_read,
        stdin_write,
        stdout_read,
        stdout_write,
        stderr_read,
        stderr_write,
    ] {
        close_handle(handle);
    }
}

fn wide(value: impl AsRef<OsStr>) -> Vec<u16> {
    value.as_ref().encode_wide().chain(Some(0)).collect()
}

fn last_error() -> u32 {
    unsafe { GetLastError() }
}
