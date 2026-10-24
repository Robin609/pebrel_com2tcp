//! Shared raw TCP client for the GUI CLI and the console-subsystem PTY entry.
//! No second GUI, PowerShell, Ncat, local echo or line buffering is involved.
use std::{
    io::{self, Read, Write},
    net::{IpAddr, Shutdown, SocketAddr, TcpStream},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};

#[derive(clap::Args, Debug)]
pub struct ClientArgs {
    #[arg(long)]
    pub host: IpAddr,
    #[arg(long)]
    pub port: u16,
}

/// Portable packages keep helpers in runtime/; Cargo builds place them alongside.
pub fn terminal_executable(gui_executable: &std::path::Path) -> std::path::PathBuf {
    let directory = gui_executable.parent().expect("executable directory");
    let name = if cfg!(windows) { "pebrel-serial.exe" } else { "pebrel-serial" };
    let packaged = directory.join("runtime").join(name);
    if packaged.is_file() { packaged } else { directory.join(name) }
}

#[cfg(test)]
mod tests {
    #[test]
    fn terminal_helper_resolves_cargo_and_portable_layouts() {
        let folder = tempfile::tempdir().unwrap();
        let gui = folder.path().join("pebrel.exe");
        let development = super::terminal_executable(&gui);
        assert_eq!(development.parent(), Some(folder.path()));
        let runtime = folder.path().join("runtime");
        std::fs::create_dir(&runtime).unwrap();
        let packaged = runtime.join(development.file_name().unwrap());
        std::fs::write(&packaged, b"fixture").unwrap();
        assert_eq!(super::terminal_executable(&gui), packaged);
    }
}

pub fn run(args: ClientArgs) -> io::Result<()> {
    let socket =
        TcpStream::connect_timeout(&SocketAddr::new(args.host, args.port), Duration::from_secs(3))?;
    socket.set_nodelay(true)?;
    #[cfg(windows)]
    let _mode = ConsoleMode::raw()?;
    #[cfg(windows)]
    let interactive = _mode.as_ref().is_some_and(|mode| mode.input.is_some());
    #[cfg(not(windows))]
    let interactive = false;
    let interrupted = Arc::new(AtomicBool::new(false));
    let input_interrupted = interrupted.clone();
    let mut writer = socket.try_clone()?;
    std::thread::Builder::new().name("serial-terminal-input".into()).spawn(move || {
        let mut buffer = [0u8; 4096];
        let mut input = io::stdin().lock();
        loop {
            match input.read(&mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(size) => {
                    if interactive
                        && let Some(at) = buffer[..size].iter().position(|byte| *byte == 3)
                    {
                        if at > 0 {
                            let _ = writer.write_all(&buffer[..at]);
                        }
                        input_interrupted.store(true, Ordering::Release);
                        let _ = writer.shutdown(Shutdown::Both);
                        return;
                    }
                    if writer.write_all(&buffer[..size]).is_err() {
                        break;
                    }
                },
            }
        }
        let _ = writer.shutdown(Shutdown::Write);
    })?;
    let mut reader = socket;
    let mut buffer = [0u8; 8192];
    loop {
        let size = match reader.read(&mut buffer) {
            Ok(size) => size,
            Err(_) if interrupted.load(Ordering::Acquire) => break,
            Err(error) => return Err(error),
        };
        if size == 0 {
            break;
        }
        write_output(&buffer[..size])?;
    }
    Ok(())
}

#[cfg(windows)]
struct ConsoleMode {
    input: Option<(windows_sys::Win32::Foundation::HANDLE, u32)>,
    output: Option<(windows_sys::Win32::Foundation::HANDLE, u32)>,
    input_codepage: u32,
    output_codepage: u32,
}
#[cfg(windows)]
impl ConsoleMode {
    fn raw() -> io::Result<Option<Self>> {
        use windows_sys::Win32::System::Console::*;
        unsafe {
            let input = GetStdHandle(STD_INPUT_HANDLE);
            let mut previous = 0;
            let is_input_console = GetConsoleMode(input, &mut previous) != 0;
            let mut guard = Self {
                input: None,
                output: None,
                input_codepage: GetConsoleCP(),
                output_codepage: GetConsoleOutputCP(),
            };
            if is_input_console {
                guard.input = Some((input, previous));
                let mode = (previous | ENABLE_VIRTUAL_TERMINAL_INPUT)
                    & !(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT);
                if SetConsoleMode(input, mode) == 0 || SetConsoleCP(65001) == 0 {
                    return Err(io::Error::last_os_error());
                }
            }
            let output = GetStdHandle(STD_OUTPUT_HANDLE);
            let mut output_mode = 0;
            if GetConsoleMode(output, &mut output_mode) != 0 {
                guard.output = Some((output, output_mode));
                if SetConsoleMode(
                    output,
                    output_mode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING,
                ) == 0
                    || SetConsoleOutputCP(65001) == 0
                {
                    return Err(io::Error::last_os_error());
                }
            }
            if guard.input.is_none() && guard.output.is_none() { Ok(None) } else { Ok(Some(guard)) }
        }
    }
}
#[cfg(windows)]
impl Drop for ConsoleMode {
    fn drop(&mut self) {
        unsafe {
            if let Some((input, previous)) = self.input {
                windows_sys::Win32::System::Console::SetConsoleMode(input, previous);
            }
            if let Some((output, previous)) = self.output {
                windows_sys::Win32::System::Console::SetConsoleMode(output, previous);
            }
            if self.input_codepage != 0 {
                windows_sys::Win32::System::Console::SetConsoleCP(self.input_codepage);
            }
            if self.output_codepage != 0 {
                windows_sys::Win32::System::Console::SetConsoleOutputCP(self.output_codepage);
            }
        }
    }
}

fn write_output(bytes: &[u8]) -> io::Result<()> {
    #[cfg(windows)]
    {
        use windows_sys::Win32::{
            Storage::FileSystem::WriteFile,
            System::Console::{GetStdHandle, STD_OUTPUT_HANDLE},
        };
        let mut remaining = bytes;
        while !remaining.is_empty() {
            let mut written = 0;
            let result = unsafe {
                WriteFile(
                    GetStdHandle(STD_OUTPUT_HANDLE),
                    remaining.as_ptr(),
                    remaining.len() as u32,
                    &mut written,
                    std::ptr::null_mut(),
                )
            };
            if result == 0 {
                return Err(io::Error::last_os_error());
            }
            if written == 0 {
                return Err(io::ErrorKind::WriteZero.into());
            }
            remaining = &remaining[written as usize..];
        }
        Ok(())
    }
    #[cfg(not(windows))]
    {
        let mut output = io::stdout().lock();
        output.write_all(bytes)?;
        output.flush()
    }
}
