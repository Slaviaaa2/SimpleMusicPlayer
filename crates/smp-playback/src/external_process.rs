use std::ffi::OsStr;
use std::fmt;
use std::path::Path;
use std::process::Stdio;

use tokio::io::AsyncReadExt;
use tokio::process::Command;
use tokio_util::sync::CancellationToken;

pub struct ExternalProcessResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}

#[derive(Debug)]
pub enum RunError {
    Cancelled,
    Io(std::io::Error),
}

impl fmt::Display for RunError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            RunError::Cancelled => write!(f, "the process was cancelled"),
            RunError::Io(err) => write!(f, "{err}"),
        }
    }
}

impl std::error::Error for RunError {}

#[derive(Default)]
pub struct ExternalProcessRunner;

impl ExternalProcessRunner {
    pub fn new() -> Self {
        Self
    }

    pub async fn run<I, S>(
        &self,
        program: &str,
        args: I,
        working_directory: Option<&Path>,
        cancellation: &CancellationToken,
    ) -> Result<ExternalProcessResult, RunError>
    where
        I: IntoIterator<Item = S>,
        S: AsRef<OsStr>,
    {
        let mut command = Command::new(program);
        command
            .args(args)
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        if let Some(dir) = working_directory {
            command.current_dir(dir);
        }

        #[cfg(windows)]
        {
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            command.creation_flags(CREATE_NO_WINDOW);
        }

        let mut child = command.spawn().map_err(RunError::Io)?;
        let pid = child.id();

        let mut stdout_pipe = child.stdout.take().expect("stdout was piped");
        let mut stderr_pipe = child.stderr.take().expect("stderr was piped");

        let stdout_task = tokio::spawn(async move {
            let mut buf = String::new();
            let _ = stdout_pipe.read_to_string(&mut buf).await;
            buf
        });
        let stderr_task = tokio::spawn(async move {
            let mut buf = String::new();
            let _ = stderr_pipe.read_to_string(&mut buf).await;
            buf
        });

        tokio::select! {
            status = child.wait() => {
                let status = status.map_err(RunError::Io)?;
                let stdout = stdout_task.await.unwrap_or_default();
                let stderr = stderr_task.await.unwrap_or_default();
                Ok(ExternalProcessResult {
                    exit_code: status.code().unwrap_or(-1),
                    stdout,
                    stderr,
                })
            }
            _ = cancellation.cancelled() => {
                if let Some(pid) = pid {
                    kill_tree(pid);
                }
                let _ = child.kill().await;
                let _ = stdout_task.await;
                let _ = stderr_task.await;
                Err(RunError::Cancelled)
            }
        }
    }
}

/// Best-effort tree-kill, matching `Process.Kill(entireProcessTree: true)` in
/// ExternalProcessRunner.cs -- important because yt-dlp spawns ffmpeg as a
/// child process, which would otherwise be orphaned on cancellation.
#[cfg(windows)]
fn kill_tree(pid: u32) {
    let _ = std::process::Command::new("taskkill")
        .args(["/PID", &pid.to_string(), "/T", "/F"])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status();
}

#[cfg(not(windows))]
fn kill_tree(_pid: u32) {
    // Non-Windows child processes aren't spawned into their own process
    // group, so there is no portable tree-kill here; `child.kill()` still
    // terminates the direct child.
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[tokio::test]
    async fn captures_stdout_and_exit_code() {
        let runner = ExternalProcessRunner::new();
        let cancellation = CancellationToken::new();
        let result = runner
            .run("cmd", ["/C", "echo hello"], None, &cancellation)
            .await
            .expect("process should run");

        assert_eq!(result.exit_code, 0);
        assert!(result.stdout.trim().eq_ignore_ascii_case("hello"));
    }

    #[cfg(windows)]
    #[tokio::test]
    async fn cancellation_stops_a_long_running_process() {
        let runner = ExternalProcessRunner::new();
        let cancellation = CancellationToken::new();
        cancellation.cancel();

        let result = runner
            .run("cmd", ["/C", "timeout /T 30"], None, &cancellation)
            .await;

        assert!(matches!(result, Err(RunError::Cancelled)));
    }
}
