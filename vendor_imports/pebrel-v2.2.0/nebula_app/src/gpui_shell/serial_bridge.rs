//! Application-owned services outlive the Settings surface, not the application.
use crate::serial_bridge::{Hub, Manager};
use gpui::{App, Global};
use parking_lot::Mutex;
use std::{path::PathBuf, sync::Arc};

pub(super) struct SerialHub(pub Hub);
impl Global for SerialHub {}

pub(super) fn init(cx: &mut App) {
    let legacy = std::env::var_os("LOCALAPPDATA")
        .map(|path| PathBuf::from(path).join("Com2Tcp/settings.xml"));
    let hub = Arc::new(Mutex::new(Manager::open(
        nebula_settings::settings_dir().join("serial_channels.json"),
        legacy,
    )));
    cx.set_global(SerialHub(hub));
    cx.on_app_quit(|cx| {
        let hub = cx.global::<SerialHub>().0.clone();
        let stop = cx.background_executor().spawn(async move {
            hub.lock().shutdown();
        });
        async move { stop.await }
    })
    .detach();
}
