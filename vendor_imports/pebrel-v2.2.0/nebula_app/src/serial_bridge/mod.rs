//! Optional in-process serial/TCP capability. GPUI only consumes snapshots and commands.
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::{collections::VecDeque, io, net::Ipv4Addr, path::PathBuf, sync::Arc};

pub mod client;
mod runtime;
mod store;
#[cfg(test)]
mod tests;
#[cfg(windows)]
mod windows;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct ChannelConfig {
    pub id: u64,
    pub port_name: String,
    pub baud_rate: u32,
    pub data_bits: u8,
    pub parity: String,
    pub stop_bits: String,
    pub address: Ipv4Addr,
    pub port: u16,
}

impl ChannelConfig {
    pub fn new(id: u64, port: u16) -> Self {
        Self {
            id,
            port_name: String::new(),
            baud_rate: 115200,
            data_bits: 8,
            parity: "None".into(),
            stop_bits: "One".into(),
            address: Ipv4Addr::LOCALHOST,
            port,
        }
    }
    pub fn validate(&self) -> io::Result<()> {
        if self.port_name.trim().is_empty()
            || self.baud_rate == 0
            || !(5..=8).contains(&self.data_bits)
            || !["None", "Odd", "Even", "Mark", "Space"].contains(&self.parity.as_str())
            || !["One", "OnePointFive", "Two"].contains(&self.stop_bits.as_str())
            || self.port == 0
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Invalid serial or TCP configuration",
            ));
        }
        Ok(())
    }
    pub fn endpoint(&self) -> std::net::SocketAddr {
        std::net::SocketAddr::from((self.address, self.port))
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Phase {
    #[default]
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed,
}

#[derive(Clone, Debug, Default)]
pub struct Status {
    pub phase: Phase,
    pub clients: usize,
    pub serial_rx: u64,
    pub serial_tx: u64,
    pub tcp_rx: u64,
    pub tcp_tx: u64,
    pub logs: VecDeque<String>,
}
impl Status {
    fn log(&mut self, text: impl std::fmt::Display) {
        if self.logs.len() == 300 {
            self.logs.pop_front();
        }
        self.logs.push_back(format!("{}  {text}", chrono::Local::now().format("%H:%M:%S")));
    }
}

#[derive(Clone, Debug)]
pub struct Snapshot {
    pub config: ChannelConfig,
    pub status: Status,
}

struct Channel {
    config: ChannelConfig,
    status: Arc<Mutex<Status>>,
    worker: Option<runtime::Worker>,
}
impl Channel {
    fn new(config: ChannelConfig) -> Self {
        Self { config, status: Arc::new(Mutex::new(Status::default())), worker: None }
    }
    fn active(&self) -> bool {
        self.worker.as_ref().is_some_and(|worker| !worker.finished())
    }
}

pub type Hub = Arc<Mutex<Manager>>;
pub struct Manager {
    path: PathBuf,
    channels: Vec<Channel>,
    pub load_error: Option<String>,
}
impl Manager {
    pub fn open(path: PathBuf, legacy: Option<PathBuf>) -> Self {
        match store::load(&path, legacy.as_deref()) {
            Ok(configs) => Self {
                path,
                channels: configs.into_iter().map(Channel::new).collect(),
                load_error: None,
            },
            Err(error) => Self { path, channels: Vec::new(), load_error: Some(error.to_string()) },
        }
    }
    pub fn snapshots(&self) -> Vec<Snapshot> {
        self.channels
            .iter()
            .map(|channel| Snapshot {
                config: channel.config.clone(),
                status: channel.status.lock().clone(),
            })
            .collect()
    }
    fn configurations(&self) -> Vec<ChannelConfig> {
        self.channels.iter().map(|c| c.config.clone()).collect()
    }
    fn ready(&self) -> io::Result<()> {
        match &self.load_error {
            Some(error) => Err(io::Error::other(error.clone())),
            None => Ok(()),
        }
    }
    pub fn add(&mut self) -> io::Result<u64> {
        self.ready()?;
        let id = self.channels.iter().map(|c| c.config.id).max().unwrap_or(0) + 1;
        let port = (2011..=u16::MAX)
            .find(|port| self.channels.iter().all(|c| c.config.port != *port))
            .ok_or_else(|| io::Error::other("No available TCP port number"))?;
        let config = ChannelConfig::new(id, port);
        let mut configs = self.configurations();
        configs.push(config.clone());
        store::save(&self.path, &configs)?;
        self.channels.push(Channel::new(config));
        Ok(id)
    }
    pub fn update(&mut self, config: ChannelConfig) -> io::Result<()> {
        self.ready()?;
        config.validate()?;
        let index = self
            .channels
            .iter()
            .position(|c| c.config.id == config.id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "Channel removed"))?;
        if self.channels[index].active() {
            return Err(io::Error::other("Stop the channel before editing"));
        }
        let mut configs = self.configurations();
        configs[index] = config.clone();
        store::save(&self.path, &configs)?;
        self.channels[index].config = config;
        Ok(())
    }
    pub fn remove(&mut self, id: u64) -> io::Result<()> {
        self.ready()?;
        if self.channels.len() <= 1 {
            return Err(io::Error::other("Keep at least one channel"));
        }
        let index = self
            .channels
            .iter()
            .position(|c| c.config.id == id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "Channel removed"))?;
        if self.channels[index].active() {
            return Err(io::Error::other("Stop the channel before removing"));
        }
        let mut configs = self.configurations();
        configs.remove(index);
        store::save(&self.path, &configs)?;
        self.channels.remove(index);
        Ok(())
    }
    pub fn start(&mut self, id: u64) -> io::Result<()> {
        self.start_with(id, Arc::new(open_serial))
    }
    fn start_with(&mut self, id: u64, open: runtime::OpenSerial) -> io::Result<()> {
        self.ready()?;
        let config = self
            .channels
            .iter()
            .find(|c| c.config.id == id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "Channel removed"))?
            .config
            .clone();
        config.validate()?;
        if self.channels.iter().any(|c| {
            c.active()
                && (c.config.id == id
                    || c.config.port_name.eq_ignore_ascii_case(&config.port_name)
                    || (c.config.port == config.port
                        && (c.config.address.is_unspecified()
                            || config.address.is_unspecified()
                            || c.config.address == config.address)))
        }) {
            return Err(io::Error::new(
                io::ErrorKind::AddrInUse,
                "Serial port or TCP endpoint is already in use",
            ));
        }
        let channel = self.channels.iter_mut().find(|c| c.config.id == id).unwrap();
        channel.worker.take();
        *channel.status.lock() = Status { phase: Phase::Starting, ..Status::default() };
        match runtime::spawn(config, channel.status.clone(), open) {
            Ok(worker) => channel.worker = Some(worker),
            Err(error) => {
                let mut status = channel.status.lock();
                status.phase = Phase::Failed;
                status.log(&error);
                return Err(error);
            },
        }
        Ok(())
    }
    pub fn stop(&mut self, id: u64) -> io::Result<()> {
        let channel = self
            .channels
            .iter_mut()
            .find(|c| c.config.id == id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "Channel removed"))?;
        if let Some(worker) = channel.worker.take() {
            channel.status.lock().phase = Phase::Stopping;
            worker.stop();
            channel.status.lock().phase = Phase::Stopped;
        }
        Ok(())
    }
    pub fn shutdown(&mut self) {
        for channel in &mut self.channels {
            if let Some(worker) = channel.worker.take() {
                worker.stop();
            }
        }
    }
}
impl Drop for Manager {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn open_serial(config: &ChannelConfig) -> io::Result<Box<dyn runtime::SerialIo>> {
    #[cfg(windows)]
    {
        windows::open(config)
    }
    #[cfg(not(windows))]
    {
        let _ = config;
        Err(io::Error::new(io::ErrorKind::Unsupported, "Serial bridge currently requires Windows"))
    }
}
pub fn ports() -> io::Result<Vec<String>> {
    #[cfg(windows)]
    {
        windows::ports()
    }
    #[cfg(not(windows))]
    {
        Err(io::Error::new(io::ErrorKind::Unsupported, "Serial bridge currently requires Windows"))
    }
}
