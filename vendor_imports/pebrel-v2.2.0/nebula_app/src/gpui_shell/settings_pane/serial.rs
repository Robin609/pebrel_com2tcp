//! Serial settings use Pebrel's actual inputs, selects, actions and theme roles.
use super::*;
use crate::gpui_shell::widgets::{NebulaButton, settings_select_frame};
use crate::{
    i18n::Message,
    serial_bridge::{ChannelConfig, Phase, Snapshot},
};

const PARITIES: [&str; 5] = ["None", "Odd", "Even", "Mark", "Space"];
const STOPS: [&str; 3] = ["One", "OnePointFive", "Two"];
#[cfg(all(test, feature = "gpui-test-support"))]
mod tests;

pub(super) struct SerialState {
    snapshots: Vec<Snapshot>,
    selected: Option<u64>,
    loaded: Option<u64>,
    inputs: Vec<Entity<InputState>>,
    selects: Vec<SharedSelect>,
    ports: Vec<String>,
    busy: bool,
    dirty: bool,
    syncing: bool,
    sequence: u64,
    failure: Option<String>,
    saved: bool,
    monitor: Option<Task<()>>,
    _subscriptions: Vec<Subscription>,
}

impl SerialState {
    pub(super) fn new(window: &mut Window, cx: &mut Context<SettingsPane>) -> Self {
        let inputs: Vec<_> = (0..4).map(|_| cx.new(|cx| InputState::new(window, cx))).collect();
        let values = [vec!["5", "6", "7", "8"], PARITIES.to_vec(), vec!["1", "1.5", "2"]];
        let selects: Vec<_> = values
            .into_iter()
            .map(|values| {
                cx.new(|cx| {
                    SelectState::new(
                        values.into_iter().map(SharedString::from).collect::<Vec<_>>(),
                        Some(IndexPath::default().row(0)),
                        window,
                        cx,
                    )
                })
            })
            .collect();
        let mut subscriptions = Vec::new();
        for input in &inputs {
            subscriptions.push(cx.subscribe(input, |this, _, event, cx| {
                if matches!(event, InputEvent::Change) && !this.serial.syncing {
                    this.serial.dirty = true;
                    this.serial.saved = false;
                    cx.notify();
                }
            }));
        }
        for select in &selects {
            subscriptions.push(cx.subscribe(select, |this, _, event, cx| {
                if matches!(event, SelectEvent::Confirm(_)) && !this.serial.syncing {
                    this.serial.dirty = true;
                    this.serial.saved = false;
                    cx.notify();
                }
            }));
        }
        Self {
            snapshots: Vec::new(),
            selected: None,
            loaded: None,
            inputs,
            selects,
            ports: Vec::new(),
            busy: false,
            dirty: false,
            syncing: false,
            sequence: 0,
            failure: None,
            saved: false,
            monitor: None,
            _subscriptions: subscriptions,
        }
    }
    fn selected(&self) -> Option<&Snapshot> {
        self.snapshots.iter().find(|snapshot| Some(snapshot.config.id) == self.selected)
    }
    fn draft(&self, cx: &App) -> Result<ChannelConfig, String> {
        let mut config = self.selected().ok_or("No channel selected")?.config.clone();
        let language = crate::gpui_shell::config::ui_language(cx);
        let text = |index: usize| self.inputs[index].read(cx).value().trim().to_string();
        config.port_name = text(0);
        if config.port_name.is_empty() {
            return Err(language.text(Message::SerialErrorPortName).into());
        }
        config.baud_rate = text(1)
            .parse::<u32>()
            .ok()
            .filter(|value| *value > 0)
            .ok_or_else(|| language.text(Message::SerialErrorBaud))?;
        config.address = text(2).parse().map_err(|_| language.text(Message::SerialErrorAddress))?;
        config.port = text(3)
            .parse::<u16>()
            .ok()
            .filter(|value| *value > 0)
            .ok_or_else(|| language.text(Message::SerialErrorTcpPort))?;
        let index = |field: usize| {
            self.selects[field].read(cx).selected_index(cx).map(|path| path.row).unwrap_or(0)
        };
        config.data_bits = (index(0) + 5) as u8;
        config.parity = PARITIES[index(1)].into();
        config.stop_bits = STOPS[index(2)].into();
        config.validate().map_err(|error| error.to_string())?;
        Ok(config)
    }
}

#[derive(Clone, Copy)]
enum Command {
    Save,
    Start,
    Stop,
    Add,
    Remove,
    StartAll,
    StopAll,
    Refresh,
    Select(u64),
}

impl SettingsPane {
    fn serial_sync_editor(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.serial.loaded == self.serial.selected {
            return;
        }
        let Some(snapshot) = self.serial.selected().cloned() else { return };
        let config = snapshot.config;
        self.serial.syncing = true;
        let values = [
            config.port_name,
            config.baud_rate.to_string(),
            config.address.to_string(),
            config.port.to_string(),
        ];
        for (input, value) in self.serial.inputs.clone().iter().zip(values) {
            input.update(cx, |input, cx| input.set_value(value, window, cx));
        }
        let indexes = [
            usize::from(config.data_bits.saturating_sub(5)).min(3),
            PARITIES.iter().position(|value| *value == config.parity).unwrap_or(0),
            STOPS.iter().position(|value| *value == config.stop_bits).unwrap_or(0),
        ];
        for (select, index) in self.serial.selects.clone().iter().zip(indexes) {
            select.update(cx, |select, cx| {
                select.set_selected_index(Some(IndexPath::default().row(index)), window, cx)
            });
        }
        self.serial.loaded = self.serial.selected;
        self.serial.dirty = false;
        self.serial.syncing = false;
    }

    fn serial_monitor(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.serial.monitor.is_some() {
            return;
        }
        let Some(hub) = cx
            .try_global::<crate::gpui_shell::serial_bridge::SerialHub>()
            .map(|global| global.0.clone())
        else {
            self.serial.failure = Some("Serial services have not been initialized".into());
            return;
        };
        self.serial.monitor = Some(cx.spawn_in(window, async move |this, cx| {
            let mut first = true;
            loop {
                let Ok(sequence) =
                    this.update(cx, |this, _| (!this.serial.busy).then_some(this.serial.sequence))
                else {
                    break;
                };
                if let Some(sequence) = sequence {
                    let hub = hub.clone();
                    let initial = first;
                    let (snapshots, error, ports) = cx
                        .background_executor()
                        .spawn(async move {
                            let manager = hub.lock();
                            (
                                manager.snapshots(),
                                manager.load_error.clone(),
                                initial.then(crate::serial_bridge::ports),
                            )
                        })
                        .await;
                    first = false;
                    if this
                        .update_in(cx, |this, window, cx| {
                            if this.serial.busy || this.serial.sequence != sequence {
                                return;
                            }
                            this.serial.snapshots = snapshots;
                            if let Some(error) = error {
                                this.serial.failure = Some(error);
                            }
                            if let Some(ports) = ports {
                                match ports {
                                    Ok(ports) => this.serial.ports = ports,
                                    Err(error) => this.serial.failure = Some(error.to_string()),
                                }
                            }
                            if this.serial.selected.is_none() {
                                this.serial.selected =
                                    this.serial.snapshots.first().map(|s| s.config.id);
                            }
                            this.serial_sync_editor(window, cx);
                            if this.active_section == SERIAL_SECTION {
                                cx.notify();
                            }
                        })
                        .is_err()
                    {
                        break;
                    }
                }
                cx.background_executor().timer(Duration::from_millis(250)).await;
            }
        }));
    }

    fn serial_select(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if self.serial.dirty {
            self.serial_command(Command::Select(id), window, cx);
        } else {
            self.serial.selected = Some(id);
            self.serial_sync_editor(window, cx);
            cx.notify();
        }
    }

    fn serial_command(&mut self, command: Command, window: &mut Window, cx: &mut Context<Self>) {
        if self.serial.busy {
            return;
        }
        let draft = if matches!(command, Command::Save | Command::Start)
            || (self.serial.dirty
                && matches!(command, Command::Select(_) | Command::Add | Command::StartAll))
        {
            match self.serial.draft(cx) {
                Ok(config) => Some(config),
                Err(error) => {
                    self.serial.failure = Some(error);
                    cx.notify();
                    return;
                },
            }
        } else {
            None
        };
        self.serial.sequence = self.serial.sequence.wrapping_add(1);
        let sequence = self.serial.sequence;
        let selected = self.serial.selected;
        let Some(hub) = cx
            .try_global::<crate::gpui_shell::serial_bridge::SerialHub>()
            .map(|global| global.0.clone())
        else {
            self.serial.failure = Some("Serial services have not been initialized".into());
            cx.notify();
            return;
        };
        self.serial.busy = true;
        self.serial.failure = None;
        self.serial.saved = false;
        let work = cx.background_executor().spawn(async move {
            let mut manager = hub.lock();
            if let Some(config) = draft {
                manager.update(config).map_err(|error| error.to_string())?;
            }
            let selected_id = || selected.ok_or_else(|| "No channel selected".to_owned());
            let mut next = selected;
            let mut ports = None;
            let result = match command {
                Command::Save => Ok(()),
                Command::Start => manager.start(selected_id()?),
                Command::Stop => manager.stop(selected_id()?),
                Command::Add => manager.add().map(|id| next = Some(id)),
                Command::Remove => manager
                    .remove(selected_id()?)
                    .map(|()| next = manager.snapshots().first().map(|s| s.config.id)),
                Command::Refresh => crate::serial_bridge::ports().map(|found| ports = Some(found)),
                Command::Select(id) => {
                    next = Some(id);
                    Ok(())
                },
                Command::StartAll | Command::StopAll => {
                    let mut failures = Vec::new();
                    for snapshot in manager.snapshots() {
                        let active = matches!(
                            snapshot.status.phase,
                            Phase::Starting | Phase::Running | Phase::Stopping
                        );
                        let result = if matches!(command, Command::StopAll) {
                            manager.stop(snapshot.config.id)
                        } else if !active {
                            manager.start(snapshot.config.id)
                        } else {
                            Ok(())
                        };
                        if let Err(error) = result {
                            failures.push(format!("{}: {error}", snapshot.config.port_name));
                        }
                    }
                    if failures.is_empty() {
                        Ok(())
                    } else {
                        Err(std::io::Error::other(failures.join("\n")))
                    }
                },
            };
            result.map_err(|error| error.to_string())?;
            Ok::<_, String>((manager.snapshots(), next, ports))
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = work.await;
            let _ = this.update_in(cx, |this, window, cx| {
                if this.serial.sequence != sequence {
                    return;
                }
                this.serial.busy = false;
                match result {
                    Ok((snapshots, selected, ports)) => {
                        this.serial.snapshots = snapshots;
                        this.serial.selected = selected;
                        if let Some(ports) = ports {
                            this.serial.ports = ports;
                        }
                        if !matches!(command, Command::Stop | Command::StopAll | Command::Refresh) {
                            this.serial.loaded = None;
                            this.serial_sync_editor(window, cx);
                        }
                        this.serial.saved = matches!(command, Command::Save);
                    },
                    Err(error) => this.serial.failure = Some(error),
                }
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }

    fn serial_input(&self, field: usize, editable: bool) -> gpui::AnyElement {
        div()
            .id(("serial-input", field))
            .debug_selector(move || format!("serial-input-{field}"))
            .w(px(SETTINGS_SELECT_WIDTH))
            .child(Input::new(&self.serial.inputs[field]).disabled(!editable).w_full())
            .into_any_element()
    }
    fn serial_select_control(&self, field: usize, editable: bool, cx: &App) -> gpui::AnyElement {
        settings_select_frame(
            format!("serial-select-{field}"),
            Select::new(&self.serial.selects[field]).appearance(false).disabled(!editable).h_full(),
            cx,
        )
        .debug_selector(move || format!("serial-select-{field}"))
        .w(px(SETTINGS_SELECT_WIDTH))
        .into_any_element()
    }

    pub(super) fn section_serial(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::AnyElement {
        self.serial_monitor(window, cx);
        let language = crate::gpui_shell::config::ui_language(cx);
        let text = |message| language.text(message);
        let busy = self.serial.busy;
        let mut channels = h_flex().w_full().flex_wrap().gap_2();
        for snapshot in &self.serial.snapshots {
            let id = snapshot.config.id;
            let label = format!(
                "{} · TCP {}",
                if snapshot.config.port_name.is_empty() {
                    text(Message::SerialPortName)
                } else {
                    &snapshot.config.port_name
                },
                snapshot.config.port
            );
            channels = channels.child(
                Button::new(("serial-channel", id))
                    .label(label)
                    .selected(self.serial.selected == Some(id))
                    .disabled(busy)
                    .on_click(
                        cx.listener(move |this, _, window, cx| this.serial_select(id, window, cx)),
                    ),
            );
        }
        let toolbar =
            h_flex()
                .gap_2()
                .flex_wrap()
                .child(
                    NebulaButton::new("serial-add")
                        .label(text(Message::SerialAdd))
                        .outline()
                        .disabled(busy)
                        .on_click(cx.listener(|this, _, window, cx| {
                            this.serial_command(Command::Add, window, cx)
                        })),
                )
                .child(
                    NebulaButton::new("serial-start-all")
                        .label(text(Message::SerialStartAll))
                        .disabled(busy || self.serial.snapshots.is_empty())
                        .on_click(cx.listener(|this, _, window, cx| {
                            this.serial_command(Command::StartAll, window, cx)
                        })),
                )
                .child(
                    NebulaButton::new("serial-stop-all")
                        .label(text(Message::SerialStopAll))
                        .disabled(
                            busy || !self.serial.snapshots.iter().any(|s| {
                                matches!(s.status.phase, Phase::Starting | Phase::Running)
                            }),
                        )
                        .on_click(cx.listener(|this, _, window, cx| {
                            this.serial_command(Command::StopAll, window, cx)
                        })),
                )
                .when(busy, |toolbar| toolbar.child(Spinner::new().xsmall()));
        let mut root = v_flex()
            .id("serial-page")
            .debug_selector(|| "serial-page".into())
            .w_full()
            .gap_5()
            .child(design::group_heading(text(Message::SerialTitle), self.font_size_px(cx), cx))
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(text(Message::SerialDescription)),
            )
            .child(channels)
            .child(toolbar);
        if let Some(error) = &self.serial.failure {
            root = root.child(div().text_color(cx.theme().danger).child(error.clone()));
        }
        #[cfg(not(windows))]
        {
            return root.child(text(Message::SerialUnsupported)).into_any_element();
        }
        let Some(snapshot) = self.serial.selected().cloned() else {
            return root.child(text(Message::SerialLoading)).into_any_element();
        };
        let active =
            matches!(snapshot.status.phase, Phase::Starting | Phase::Running | Phase::Stopping);
        let editable = !busy && !active;
        let mut port_actions = h_flex().gap_2().flex_wrap().child(
            NebulaButton::new("serial-refresh")
                .label(text(Message::SerialRefresh))
                .outline()
                .disabled(busy)
                .on_click(cx.listener(|this, _, window, cx| {
                    this.serial_command(Command::Refresh, window, cx)
                })),
        );
        for port in self.serial.ports.clone() {
            let value = port.clone();
            port_actions = port_actions.child(
                Button::new(SharedString::from(format!("serial-port-{port}")))
                    .label(port)
                    .ghost()
                    .disabled(!editable)
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.serial.inputs[0]
                            .update(cx, |input, cx| input.set_value(value.clone(), window, cx))
                    })),
            );
        }
        let serial = v_flex()
            .w_full()
            .gap_1()
            .child(port_actions)
            .child(self.row(text(Message::SerialPortName), "", self.serial_input(0, editable), cx))
            .child(self.row(text(Message::SerialBaudRate), "", self.serial_input(1, editable), cx))
            .child(self.row(
                text(Message::SerialDataBits),
                "",
                self.serial_select_control(0, editable, cx),
                cx,
            ))
            .child(self.row(
                text(Message::SerialParity),
                "",
                self.serial_select_control(1, editable, cx),
                cx,
            ))
            .child(self.row(
                text(Message::SerialStopBits),
                "",
                self.serial_select_control(2, editable, cx),
                cx,
            ))
            .child(self.row(
                text(Message::SerialAddress),
                text(Message::SerialAddressHelp),
                self.serial_input(2, editable),
                cx,
            ))
            .child(self.row(text(Message::SerialTcpPort), "", self.serial_input(3, editable), cx));
        let phase = match snapshot.status.phase {
            Phase::Stopped => Message::SerialStopped,
            Phase::Starting => Message::SerialStarting,
            Phase::Running => Message::SerialRunning,
            Phase::Stopping => Message::SerialStopping,
            Phase::Failed => Message::SerialFailed,
        };
        let actions = h_flex()
            .gap_2()
            .flex_wrap()
            .child(
                NebulaButton::new("serial-save")
                    .label(text(Message::SerialSave))
                    .outline()
                    .disabled(!editable || !self.serial.dirty)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.serial_command(Command::Save, window, cx)
                    })),
            )
            .child(
                NebulaButton::new("serial-start")
                    .label(text(Message::SerialStart))
                    .primary()
                    .disabled(!editable)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.serial_command(Command::Start, window, cx)
                    })),
            )
            .child(
                NebulaButton::new("serial-stop")
                    .label(text(Message::SerialStop))
                    .disabled(busy || !active)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.serial_command(Command::Stop, window, cx)
                    })),
            )
            .child(
                NebulaButton::new("serial-connect")
                    .label(text(Message::SerialConnect))
                    .outline()
                    .disabled(busy || self.serial.dirty || snapshot.status.phase != Phase::Running)
                    .on_click(cx.listener(|this, _, _, cx| {
                        if let Some(snapshot) =
                            this.serial.selected().filter(|s| s.status.phase == Phase::Running)
                        {
                            cx.emit(SettingsPaneEvent::LaunchSerial(snapshot.config.clone()));
                        }
                    })),
            )
            .child(
                NebulaButton::new("serial-remove")
                    .label(text(Message::SerialRemove))
                    .danger()
                    .disabled(!editable || self.serial.snapshots.len() <= 1)
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.serial_command(Command::Remove, window, cx)
                    })),
            );
        let counters = h_flex()
            .gap_4()
            .flex_wrap()
            .text_sm()
            .text_color(cx.theme().muted_foreground)
            .children(
                [
                    (Message::SerialSerialRx, snapshot.status.serial_rx),
                    (Message::SerialSerialTx, snapshot.status.serial_tx),
                    (Message::SerialTcpRx, snapshot.status.tcp_rx),
                    (Message::SerialTcpTx, snapshot.status.tcp_tx),
                ]
                .into_iter()
                .map(|(label, value)| div().child(format!("{} {value} B", text(label)))),
            );
        // Logs are byte-transfer diagnostics, never commands or automatic device probes.
        let logs = div().id("serial-activity").h(px(180.0)).overflow_y_scrollbar().child(
            v_flex()
                .w_full()
                .gap_1()
                .font_family(cx.theme().mono_font_family.clone())
                .text_sm()
                .children(snapshot.status.logs.into_iter().map(|line| div().child(line))),
        );
        root = root
            .child(
                div()
                    .text_color(if snapshot.status.phase == Phase::Failed {
                        cx.theme().danger
                    } else {
                        cx.theme().muted_foreground
                    })
                    .child(format!(
                        "{} · {} {}",
                        text(phase),
                        text(Message::SerialClients),
                        snapshot.status.clients
                    )),
            )
            .child(actions)
            .child(serial)
            .child(counters)
            .child(div().text_sm().text_color(cx.theme().muted_foreground).child(text(
                if self.serial.dirty {
                    Message::SerialDirty
                } else if self.serial.saved {
                    Message::SerialSaved
                } else {
                    Message::SerialSaveHint
                },
            )))
            .child(div().font_semibold().child(text(Message::SerialLog)))
            .child(logs);
        root.into_any_element()
    }
}
