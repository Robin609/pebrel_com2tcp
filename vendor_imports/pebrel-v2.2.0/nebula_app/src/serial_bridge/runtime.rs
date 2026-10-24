use super::{ChannelConfig, Phase, Status};
use parking_lot::Mutex;
use std::{
    collections::VecDeque,
    io::{self, Read, Write},
    net::{TcpListener, TcpStream},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

pub(super) trait SerialIo: Read + Write + Send {}
impl<T: Read + Write + Send> SerialIo for T {}
pub(super) type OpenSerial =
    Arc<dyn Fn(&ChannelConfig) -> io::Result<Box<dyn SerialIo>> + Send + Sync>;
const QUEUE_LIMIT: usize = 256 * 1024;
const SEND_TIMEOUT: Duration = Duration::from_secs(2);

pub(super) struct Worker {
    cancel: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}
impl Worker {
    pub fn finished(&self) -> bool {
        self.thread.as_ref().is_none_or(JoinHandle::is_finished)
    }
    pub fn stop(mut self) {
        self.cancel.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        self.cancel.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

struct Client {
    socket: TcpStream,
    queue: VecDeque<Arc<[u8]>>,
    offset: usize,
    pending: usize,
    progress: Instant,
}
impl Client {
    fn queue(&mut self, bytes: Arc<[u8]>) -> bool {
        if self.pending + bytes.len() > QUEUE_LIMIT {
            return false;
        }
        if self.pending == 0 {
            self.progress = Instant::now();
        }
        self.pending += bytes.len();
        self.queue.push_back(bytes);
        true
    }
    fn flush(&mut self, status: &mut Status) -> io::Result<()> {
        while let Some(bytes) = self.queue.front() {
            match self.socket.write(&bytes[self.offset..]) {
                Ok(0) => return Err(io::ErrorKind::WriteZero.into()),
                Ok(size) => {
                    status.tcp_tx += size as u64;
                    self.pending -= size;
                    self.offset += size;
                    self.progress = Instant::now();
                    if self.offset == bytes.len() {
                        self.offset = 0;
                        self.queue.pop_front();
                    }
                },
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(error) => return Err(error),
            }
        }
        if self.pending > 0 && self.progress.elapsed() > SEND_TIMEOUT {
            return Err(io::ErrorKind::TimedOut.into());
        }
        Ok(())
    }
}

pub(super) fn spawn(
    config: ChannelConfig,
    status: Arc<Mutex<Status>>,
    open: OpenSerial,
) -> io::Result<Worker> {
    let cancel = Arc::new(AtomicBool::new(false));
    let cancelled = cancel.clone();
    let thread = thread::Builder::new().name(format!("serial-{}", config.id)).spawn(move || {
        let result = run(&config, &status, &cancelled, open);
        let mut status = status.lock();
        status.clients = 0;
        if let Err(error) = result {
            status.phase = Phase::Failed;
            status.log(error);
        } else {
            status.phase = Phase::Stopped;
            status.log("Service stopped; clients disconnected and serial port released");
        }
    })?;
    Ok(Worker { cancel, thread: Some(thread) })
}

fn run(
    config: &ChannelConfig,
    status: &Mutex<Status>,
    cancel: &AtomicBool,
    open: OpenSerial,
) -> io::Result<()> {
    let mut serial = open(config)?;
    let listener = TcpListener::bind(config.endpoint())?;
    listener.set_nonblocking(true)?;
    {
        let mut status = status.lock();
        status.phase = Phase::Running;
        status.log(format!(
            "{} @ {} baud; TCP {}",
            config.port_name,
            config.baud_rate,
            config.endpoint()
        ));
    }
    let mut clients: Vec<Client> = Vec::new();
    let mut buffer = [0u8; 8192];
    while !cancel.load(Ordering::Acquire) {
        loop {
            match listener.accept() {
                Ok((socket, peer)) => {
                    socket.set_nonblocking(true)?;
                    socket.set_nodelay(true)?;
                    clients.push(Client {
                        socket,
                        queue: VecDeque::new(),
                        offset: 0,
                        pending: 0,
                        progress: Instant::now(),
                    });
                    status.lock().log(format!("Client connected: {peer}"));
                },
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(error) => return Err(error),
            }
            if cancel.load(Ordering::Acquire) {
                break;
            }
        }
        let mut index = 0;
        while index < clients.len() && !cancel.load(Ordering::Acquire) {
            match clients[index].socket.read(&mut buffer) {
                Ok(0) => {
                    clients.remove(index);
                    continue;
                },
                Ok(size) => {
                    serial.write_all(&buffer[..size])?;
                    let mut status = status.lock();
                    status.tcp_rx += size as u64;
                    status.serial_tx += size as u64;
                },
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {},
                Err(_) => {
                    clients.remove(index);
                    continue;
                },
            }
            index += 1;
        }
        match serial.read(&mut buffer) {
            Ok(size) if size > 0 => {
                status.lock().serial_rx += size as u64;
                // Arc shares one immutable serial chunk across client queues.
                let bytes: Arc<[u8]> = Arc::from(&buffer[..size]);
                clients.retain_mut(|client| {
                    if client.queue(bytes.clone()) {
                        true
                    } else {
                        status.lock().log("Slow client disconnected: send queue full");
                        false
                    }
                });
            },
            Ok(_) => {},
            Err(error)
                if matches!(error.kind(), io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock) => {
            },
            Err(error) => return Err(error),
        }
        // Never hold the status lock across serial I/O or a blocking socket operation.
        let mut state = status.lock();
        clients.retain_mut(|client| {
            if let Err(error) = client.flush(&mut state) {
                state.log(format!("Client disconnected: {error}"));
                false
            } else {
                true
            }
        });
        state.clients = clients.len();
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slow_client_queue_is_bounded_independently() {
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0)).unwrap();
        let peer = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let (socket, _) = listener.accept().unwrap();
        let mut client = Client {
            socket,
            queue: VecDeque::new(),
            offset: 0,
            pending: 0,
            progress: Instant::now(),
        };
        assert!(client.queue(Arc::from(vec![0; QUEUE_LIMIT])));
        assert!(!client.queue(Arc::from([1u8])));
        assert_eq!(client.pending, QUEUE_LIMIT);
        assert_eq!(client.queue.len(), 1);
        drop(client);
        drop(peer);
    }
}
