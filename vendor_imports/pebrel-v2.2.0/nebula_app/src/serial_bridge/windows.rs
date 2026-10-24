use super::{ChannelConfig, runtime::SerialIo};
use std::{
    fs::OpenOptions,
    io,
    mem::size_of,
    os::windows::{fs::OpenOptionsExt, io::AsRawHandle},
};
use windows::Win32::{Devices::Communication::*, Foundation::HANDLE};

pub(super) fn open(config: &ChannelConfig) -> io::Result<Box<dyn SerialIo>> {
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .share_mode(0)
        .open(format!(r"\\.\{}", config.port_name.trim()))?;
    let handle = HANDLE(file.as_raw_handle());
    let mut dcb = DCB { DCBlength: size_of::<DCB>() as u32, ..Default::default() };
    unsafe { GetCommState(handle, &mut dcb) }.map_err(io::Error::other)?;
    dcb.BaudRate = config.baud_rate;
    dcb.ByteSize = config.data_bits;
    dcb.Parity = match config.parity.as_str() {
        "Odd" => ODDPARITY,
        "Even" => EVENPARITY,
        "Mark" => MARKPARITY,
        "Space" => SPACEPARITY,
        _ => NOPARITY,
    };
    dcb.StopBits = match config.stop_bits.as_str() {
        "OnePointFive" => ONE5STOPBITS,
        "Two" => TWOSTOPBITS,
        _ => ONESTOPBIT,
    };
    // Binary transfer, optional parity; no flow control, DTR or RTS, just like Com2Tcp.
    dcb._bitfield = 1 | (u32::from(config.parity != "None") << 1);
    unsafe { SetCommState(handle, &dcb) }.map_err(io::Error::other)?;
    let timeouts = COMMTIMEOUTS {
        ReadIntervalTimeout: u32::MAX,
        ReadTotalTimeoutConstant: 10,
        WriteTotalTimeoutConstant: 2000,
        ..Default::default()
    };
    unsafe { SetCommTimeouts(handle, &timeouts) }.map_err(io::Error::other)?;
    Ok(Box::new(file))
}

pub(super) fn ports() -> io::Result<Vec<String>> {
    let key = match winreg::RegKey::predef(winreg::enums::HKEY_LOCAL_MACHINE)
        .open_subkey(r"HARDWARE\DEVICEMAP\SERIALCOMM")
    {
        Ok(key) => key,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error),
    };
    let mut ports = Vec::new();
    for value in key.enum_values() {
        let (name, _) = value?;
        ports.push(key.get_value::<String, _>(&name)?);
    }
    ports.sort_by_key(|name| name.trim_start_matches("COM").parse::<u32>().unwrap_or(u32::MAX));
    ports.dedup();
    Ok(ports)
}
