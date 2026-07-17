//! Single-instance enforcement: opening a media file from Explorer while the
//! player is already running must feed that file into the existing window
//! instead of spawning a second one.
//!
//! The first process to start ("primary") owns a local IPC endpoint (a named
//! pipe on Windows, a Unix domain socket elsewhere). Later launches connect
//! to it, hand over their sources (one per line, UTF-8, closed by an EOT
//! byte) and exit; an empty message just asks the primary to bring its
//! window to the front.

use std::io;

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

/// Windows named pipes have no half-close: a client-side `shutdown()` never
/// surfaces as EOF on the server, so framing must be explicit. ASCII EOT
/// cannot appear in file paths or URLs.
const MESSAGE_TERMINATOR: u8 = 0x04;

/// Outcome of the startup single-instance handshake.
pub enum Acquire {
    /// This process is the primary; keep the listener alive and pass it to
    /// [`spawn_listener`] once the UI is up.
    Primary(Listener),
    /// Sources were handed to an already-running instance; exit now.
    Forwarded,
    /// The endpoint could neither be owned nor reached (e.g. leftover state
    /// from a crashed run that also blocks reconnection). Run as a normal
    /// standalone window rather than failing to start at all.
    Standalone,
}

pub fn acquire(runtime: &tokio::runtime::Runtime, sources: &[String]) -> Acquire {
    // Pipe/socket creation registers with the tokio reactor and panics
    // outside a runtime context, even though the calls look synchronous.
    let _runtime_context = runtime.enter();
    let message = encode_sources(sources);

    // Two rounds cover the primary-just-exited race: bind fails because the
    // old endpoint still exists, then connect fails because its owner died —
    // the retry binds successfully and this process becomes the new primary.
    for _ in 0..2 {
        match imp::try_bind() {
            Ok(listener) => return Acquire::Primary(listener),
            Err(_) => match runtime.block_on(imp::send_to_primary(&message)) {
                Ok(()) => return Acquire::Forwarded,
                Err(_) => std::thread::sleep(std::time::Duration::from_millis(200)),
            },
        }
    }

    Acquire::Standalone
}

/// Accepts forwarded-open messages for the rest of the process lifetime.
/// `on_open` runs on the tokio runtime; the caller is responsible for
/// marshalling back onto the UI thread.
pub fn spawn_listener(
    handle: &tokio::runtime::Handle,
    listener: Listener,
    on_open: impl Fn(Vec<String>) + Send + Sync + 'static,
) {
    handle.spawn(imp::run(listener, on_open));
}

/// Relative paths are resolved against *this* process's working directory
/// before forwarding, because the primary's working directory is unrelated.
/// URLs and other non-existent-path sources pass through untouched.
pub fn absolutize_sources(sources: &[String]) -> Vec<String> {
    sources
        .iter()
        .map(|source| {
            let path = std::path::Path::new(source);
            if path.is_relative() && path.exists() {
                std::path::absolute(path)
                    .map(|p| p.to_string_lossy().into_owned())
                    .unwrap_or_else(|_| source.clone())
            } else {
                source.clone()
            }
        })
        .collect()
}

fn encode_sources(sources: &[String]) -> String {
    sources.join("\n")
}

/// Sends `message`, then blocks until the primary acknowledges (or drops the
/// connection), so this process doesn't exit before the message was drained.
async fn send_message<S>(stream: &mut S, message: &str) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    stream.write_all(message.as_bytes()).await?;
    stream.write_all(&[MESSAGE_TERMINATOR]).await?;
    stream.flush().await?;

    let mut ack = [0u8; 1];
    let _ = stream.read(&mut ack).await;
    Ok(())
}

/// Reads until the terminator, then acknowledges receipt.
async fn receive_message<S>(stream: &mut S) -> io::Result<String>
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    let mut bytes = Vec::new();
    let mut buf = [0u8; 4096];
    loop {
        let read = stream.read(&mut buf).await?;
        if read == 0 {
            break;
        }
        bytes.extend_from_slice(&buf[..read]);
        if let Some(position) = bytes.iter().position(|&b| b == MESSAGE_TERMINATOR) {
            bytes.truncate(position);
            break;
        }
    }

    let _ = stream.write_all(&[0x06]).await; // ACK; best effort
    Ok(String::from_utf8_lossy(&bytes).into_owned())
}

fn decode_sources(message: &str) -> Vec<String> {
    message
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(str::to_string)
        .collect()
}

#[cfg(windows)]
pub use windows_imp::Listener;
#[cfg(windows)]
use windows_imp as imp;

#[cfg(windows)]
mod windows_imp {
    use super::{decode_sources, io, receive_message, send_message};
    use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeServer, ServerOptions};

    pub struct Listener {
        server: NamedPipeServer,
    }

    fn pipe_name() -> String {
        // Per-user so two Windows sessions don't fight over one endpoint.
        let user = std::env::var("USERNAME").unwrap_or_default();
        format!(r"\\.\pipe\SimpleMusicPlayer.{user}")
    }

    pub fn try_bind() -> io::Result<Listener> {
        // `first_pipe_instance` makes creation fail if another process
        // already owns the name, which is the whole primary-election test.
        let server = ServerOptions::new()
            .first_pipe_instance(true)
            .reject_remote_clients(true)
            .create(pipe_name())?;
        Ok(Listener { server })
    }

    pub async fn send_to_primary(message: &str) -> io::Result<()> {
        // Grant the primary the right to steal foreground focus: this
        // process was just launched by the user, so it holds that right and
        // may donate it. Best effort — activation degrades, forwarding works.
        unsafe {
            let _ = windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(
                windows::Win32::UI::WindowsAndMessaging::ASFW_ANY,
            );
        }

        let mut client = ClientOptions::new().open(pipe_name())?;
        send_message(&mut client, message).await
    }

    pub async fn run(listener: Listener, on_open: impl Fn(Vec<String>) + Send + Sync + 'static) {
        let mut server = listener.server;
        loop {
            if server.connect().await.is_err() {
                continue;
            }

            // A fresh instance must exist before handling the connected one,
            // otherwise a second launch in that window sees "no pipe" and
            // starts standalone.
            let replacement = match ServerOptions::new().reject_remote_clients(true).create(pipe_name()) {
                Ok(next) => next,
                Err(_) => break,
            };
            let mut connected = std::mem::replace(&mut server, replacement);

            if let Ok(message) = receive_message(&mut connected).await {
                on_open(decode_sources(&message));
            }
        }
    }
}

#[cfg(unix)]
pub use unix_imp::Listener;
#[cfg(unix)]
use unix_imp as imp;

#[cfg(unix)]
mod unix_imp {
    use super::{decode_sources, io, receive_message, send_message};
    use tokio::net::{UnixListener, UnixStream};

    pub struct Listener {
        listener: UnixListener,
    }

    fn socket_path() -> std::path::PathBuf {
        let user = std::env::var("USER").unwrap_or_default();
        std::env::temp_dir().join(format!("SimpleMusicPlayer-{user}.sock"))
    }

    pub fn try_bind() -> io::Result<Listener> {
        let path = socket_path();
        match UnixListener::bind(&path) {
            Ok(listener) => Ok(Listener { listener }),
            Err(err) if err.kind() == io::ErrorKind::AddrInUse => {
                // Either a live primary (caller will reach it via connect) or
                // a stale socket from a crash. Probe synchronously: if nobody
                // answers, reclaim the path.
                if std::os::unix::net::UnixStream::connect(&path).is_ok() {
                    Err(err)
                } else {
                    std::fs::remove_file(&path)?;
                    let listener = UnixListener::bind(&path)?;
                    Ok(Listener { listener })
                }
            }
            Err(err) => Err(err),
        }
    }

    pub async fn send_to_primary(message: &str) -> io::Result<()> {
        let mut stream = UnixStream::connect(socket_path()).await?;
        send_message(&mut stream, message).await
    }

    pub async fn run(listener: Listener, on_open: impl Fn(Vec<String>) + Send + Sync + 'static) {
        loop {
            let Ok((mut connected, _)) = listener.listener.accept().await else {
                continue;
            };

            if let Ok(message) = receive_message(&mut connected).await {
                on_open(decode_sources(&message));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decode_splits_lines_and_drops_blanks() {
        assert_eq!(
            decode_sources("D:\\Music\\a.mp3\nhttps://example.com\n\n"),
            vec!["D:\\Music\\a.mp3".to_string(), "https://example.com".to_string()]
        );
        assert!(decode_sources("").is_empty());
    }

    #[test]
    fn absolutize_keeps_urls_and_absolute_paths() {
        let sources = vec![
            "https://example.com/watch?v=1".to_string(),
            std::env::current_dir().unwrap().to_string_lossy().into_owned(),
        ];
        assert_eq!(absolutize_sources(&sources), sources);
    }

    #[test]
    fn absolutize_resolves_existing_relative_paths() {
        let dir = std::env::temp_dir().join("smp-single-instance-test");
        std::fs::create_dir_all(&dir).unwrap();
        let previous = std::env::current_dir().unwrap();
        // Serialize with other cwd-touching tests if any appear later.
        std::env::set_current_dir(&dir).unwrap();
        let result = absolutize_sources(&[".".to_string()]);
        std::env::set_current_dir(previous).unwrap();

        assert!(std::path::Path::new(&result[0]).is_absolute());
    }
}
