use super::*;
use std::{fs, io::Write, path::Path};

#[derive(Serialize, Deserialize)]
struct Configuration {
    version: u32,
    channels: Vec<ChannelConfig>,
}

pub(super) fn load(path: &Path, legacy: Option<&Path>) -> io::Result<Vec<ChannelConfig>> {
    if path.exists() {
        let config: Configuration = serde_json::from_slice(&fs::read(path)?)?;
        if config.version != 1 || config.channels.is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Unsupported or empty serial configuration",
            ));
        }
        let mut ids = std::collections::HashSet::new();
        if config.channels.iter().any(|c| !ids.insert(c.id)) {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "Duplicate channel ID"));
        }
        return Ok(config.channels);
    }
    let channels = if let Some(legacy) = legacy.filter(|p| p.exists()) {
        import_legacy(&fs::read_to_string(legacy)?)?
    } else {
        vec![ChannelConfig::new(1, 2011)]
    };
    save(path, &channels)?;
    Ok(channels)
}

pub(super) fn save(path: &Path, channels: &[ChannelConfig]) -> io::Result<()> {
    let parent =
        path.parent().ok_or_else(|| io::Error::other("Missing configuration directory"))?;
    fs::create_dir_all(parent)?;
    let mut file = tempfile::NamedTempFile::new_in(parent)?;
    serde_json::to_writer_pretty(
        &mut file,
        &Configuration { version: 1, channels: channels.to_vec() },
    )?;
    file.flush()?;
    file.as_file().sync_all()?;
    file.persist(path).map_err(|error| error.error)?;
    Ok(())
}

fn import_legacy(xml: &str) -> io::Result<Vec<ChannelConfig>> {
    let document = roxmltree::Document::parse(xml)
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    let mut configs = Vec::new();
    for node in document.descendants().filter(|node| node.has_tag_name("SavedSettings")) {
        let value = |name| {
            node.children()
                .find(|child| child.has_tag_name(name))
                .and_then(|n| n.text())
                .unwrap_or("")
        };
        let invalid =
            || io::Error::new(io::ErrorKind::InvalidData, "Invalid legacy Com2Tcp configuration");
        configs.push(ChannelConfig {
            id: configs.len() as u64 + 1,
            port_name: value("PortName").into(),
            baud_rate: value("BaudRate").parse().map_err(|_| invalid())?,
            data_bits: value("DataBits").parse().map_err(|_| invalid())?,
            parity: value("Parity").into(),
            stop_bits: value("StopBits").into(),
            address: value("ListenAddress").parse().map_err(|_| invalid())?,
            port: value("ListenPort").parse().map_err(|_| invalid())?,
        });
    }
    if configs.is_empty() {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "No legacy serial channels"));
    }
    Ok(configs)
}
