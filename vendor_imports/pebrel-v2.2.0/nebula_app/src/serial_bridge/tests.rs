use super::*;
use std::{
    io::{Read, Write},
    net::{TcpListener, TcpStream},
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};

#[derive(Clone, Default)]
struct MemoryPort {
    input: Arc<Mutex<VecDeque<u8>>>,
    written: Arc<Mutex<Vec<u8>>>,
    failed: Arc<AtomicBool>,
}
impl Read for MemoryPort {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if self.failed.load(Ordering::Acquire) {
            return Err(io::Error::other("serial unplugged"));
        }
        let mut input = self.input.lock();
        let size = input.len().min(buffer.len());
        for byte in &mut buffer[..size] {
            *byte = input.pop_front().unwrap();
        }
        drop(input);
        if size == 0 {
            std::thread::sleep(Duration::from_millis(1));
            return Err(io::ErrorKind::TimedOut.into());
        }
        Ok(size)
    }
}
impl Write for MemoryPort {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.written.lock().extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
fn free_port() -> u16 {
    TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap().local_addr().unwrap().port()
}
fn wait(mut ready: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(4);
    while !ready() {
        assert!(Instant::now() < deadline, "condition timed out");
        std::thread::sleep(Duration::from_millis(2));
    }
}
fn configured(manager: &mut Manager, id: u64, name: &str) -> ChannelConfig {
    let mut config = ChannelConfig::new(id, free_port());
    config.port_name = name.into();
    manager.update(config.clone()).unwrap();
    config
}
fn connect(config: &ChannelConfig) -> TcpStream {
    let stream = TcpStream::connect(config.endpoint()).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
    stream
}

#[test]
fn binary_broadcast_serial_writes_reconnect_and_stop() {
    let folder = tempfile::tempdir().unwrap();
    let mut manager = Manager::open(folder.path().join("serial.json"), None);
    let config = configured(&mut manager, 1, "SIM_A");
    let serial = MemoryPort::default();
    let open = serial.clone();
    manager.start_with(1, Arc::new(move |_| Ok(Box::new(open.clone())))).unwrap();
    wait(|| manager.snapshots()[0].status.phase == Phase::Running);
    let mut a = connect(&config);
    let mut b = connect(&config);
    wait(|| manager.snapshots()[0].status.clients == 2);
    let payload = "中文 ●\x1b[32m UTF-8\x1b[0m\0".as_bytes();
    serial.input.lock().extend(payload);
    for stream in [&mut a, &mut b] {
        let mut output = vec![0; payload.len()];
        stream.read_exact(&mut output).unwrap();
        assert_eq!(output, payload);
    }
    a.write_all(b"one\0").unwrap();
    wait(|| serial.written.lock().len() == 4);
    b.write_all(b"two\r").unwrap();
    wait(|| serial.written.lock().len() == 8);
    assert_eq!(*serial.written.lock(), b"one\0two\r");
    drop(a);
    drop(b);
    wait(|| manager.snapshots()[0].status.clients == 0);
    for _ in 0..8 {
        let stream = connect(&config);
        wait(|| manager.snapshots()[0].status.clients == 1);
        drop(stream);
        wait(|| manager.snapshots()[0].status.clients == 0);
    }
    let mut stream = connect(&config);
    wait(|| manager.snapshots()[0].status.clients == 1);
    manager.stop(1).unwrap();
    match stream.read(&mut [0]) {
        Ok(0) => {},
        Err(error)
            if matches!(
                error.kind(),
                io::ErrorKind::ConnectionReset | io::ErrorKind::ConnectionAborted
            ) => {},
        other => panic!("stopped service left the client connected: {other:?}"),
    }
    assert!(TcpListener::bind(config.endpoint()).is_ok(), "listener leaked after stop");
}

#[test]
fn channel_failure_isolation_conflicts_and_restart() {
    let folder = tempfile::tempdir().unwrap();
    let mut manager = Manager::open(folder.path().join("serial.json"), None);
    let a = configured(&mut manager, 1, "SIM_A");
    let second = manager.add().unwrap();
    let b = configured(&mut manager, second, "SIM_B");
    let serial = MemoryPort::default();
    let open = serial.clone();
    manager.start_with(1, Arc::new(move |_| Ok(Box::new(open.clone())))).unwrap();
    manager.start_with(second, Arc::new(|_| Ok(Box::new(MemoryPort::default())))).unwrap();
    wait(|| manager.snapshots().iter().all(|s| s.status.phase == Phase::Running));
    assert!(manager.remove(1).is_err());
    assert!(manager.update(a.clone()).is_err());
    serial.failed.store(true, Ordering::Release);
    wait(|| manager.snapshots()[0].status.phase == Phase::Failed);
    assert_eq!(manager.snapshots()[1].status.phase, Phase::Running);
    assert!(TcpStream::connect(b.endpoint()).is_ok());
    let mut duplicate = a.clone();
    duplicate.port_name = "sim_b".into();
    manager.update(duplicate).unwrap();
    assert_eq!(manager.start(1).unwrap_err().kind(), io::ErrorKind::AddrInUse);
    manager.update(a.clone()).unwrap();
    manager.start_with(1, Arc::new(|_| Ok(Box::new(MemoryPort::default())))).unwrap();
    wait(|| manager.snapshots()[0].status.phase == Phase::Running);
    manager.shutdown();
    assert!(TcpListener::bind(a.endpoint()).is_ok());
    assert!(TcpListener::bind(b.endpoint()).is_ok());
}

#[test]
fn open_and_bind_failures_release_resources() {
    let folder = tempfile::tempdir().unwrap();
    let mut manager = Manager::open(folder.path().join("serial.json"), None);
    let config = configured(&mut manager, 1, "SIM");
    manager.start_with(1, Arc::new(|_| Err(io::Error::other("open refused")))).unwrap();
    wait(|| manager.snapshots()[0].status.phase == Phase::Failed);
    let occupied = TcpListener::bind(config.endpoint()).unwrap();
    manager.start_with(1, Arc::new(|_| Ok(Box::new(MemoryPort::default())))).unwrap();
    wait(|| manager.snapshots()[0].status.phase == Phase::Failed);
    drop(occupied);
    manager.start_with(1, Arc::new(|_| Ok(Box::new(MemoryPort::default())))).unwrap();
    wait(|| manager.snapshots()[0].status.phase == Phase::Running);
    manager.shutdown();
    assert!(TcpListener::bind(config.endpoint()).is_ok());
}

#[test]
fn configuration_migration_atomic_save_and_corruption() {
    let folder = tempfile::tempdir().unwrap();
    let legacy = folder.path().join("settings.xml");
    let path = folder.path().join("serial.json");
    let xml = "<SavedConfiguration><Channels><SavedSettings><PortName>COM16</PortName><BaudRate>1500000</BaudRate><DataBits>7</DataBits><Parity>Even</Parity><StopBits>OnePointFive</StopBits><ListenAddress>0.0.0.0</ListenAddress><ListenPort>2016</ListenPort></SavedSettings></Channels></SavedConfiguration>";
    std::fs::write(&legacy, xml).unwrap();
    let manager = Manager::open(path.clone(), Some(legacy.clone()));
    assert!(manager.load_error.is_none());
    let snapshot = &manager.snapshots()[0];
    assert_eq!(
        (
            snapshot.config.port_name.as_str(),
            snapshot.config.baud_rate,
            snapshot.config.data_bits,
            snapshot.config.stop_bits.as_str()
        ),
        ("COM16", 1500000, 7, "OnePointFive")
    );
    assert_eq!(snapshot.status.phase, Phase::Stopped);
    assert_eq!(std::fs::read_to_string(&legacy).unwrap(), xml);
    drop(manager);
    std::fs::write(&path, "broken json").unwrap();
    let mut manager = Manager::open(path.clone(), Some(legacy));
    assert!(manager.load_error.is_some());
    assert!(manager.add().is_err());
    assert_eq!(std::fs::read_to_string(path).unwrap(), "broken json");
}
