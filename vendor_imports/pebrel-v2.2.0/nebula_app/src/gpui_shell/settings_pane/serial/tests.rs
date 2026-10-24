use super::*;
use crate::gpui_shell::{config::Settings, serial_bridge::SerialHub};
use crate::serial_bridge::Manager;
use gpui::{Modifiers, TestAppContext, VisualTestContext, size};
use gpui_component::Root;
use parking_lot::Mutex;

fn draw(window: &mut VisualTestContext) {
    window.run_until_parked();
    window.update(|window, cx| {
        let _ = window.draw(cx);
    });
}
fn click(selector: &'static str, window: &mut VisualTestContext) {
    let bounds =
        window.debug_bounds(selector).unwrap_or_else(|| panic!("missing hitbox: {selector}"));
    window.simulate_click(bounds.center(), Modifiers::default());
    draw(window);
}

fn setup(
    cx: &mut TestAppContext,
    path: &std::path::Path,
) -> (Entity<SettingsPane>, VisualTestContext) {
    let mut manager = Manager::open(path.to_owned(), None);
    let mut config = ChannelConfig::new(1, 2011);
    config.port_name = "PEBREL_TEST_PORT_DOES_NOT_EXIST".into();
    manager.update(config).unwrap();
    cx.update(|cx| {
        gpui_component::init(cx);
        cx.set_reduce_motion(true);
        cx.set_global(Settings::load(ThemeName::Nord));
        cx.set_global(SerialHub(Arc::new(Mutex::new(manager))));
    });
    let mut owner = None;
    let (_, mut window) = cx.add_window_view(|window, cx| {
        let pane = cx.new(|cx| SettingsPane::new(window, cx));
        pane.update(cx, |pane, cx| {
            pane.active_section = SERIAL_SECTION;
            pane.serial.snapshots = cx.global::<SerialHub>().0.lock().snapshots();
            pane.serial.selected = Some(1);
            pane.serial_sync_editor(window, cx);
        });
        owner = Some(pane.clone());
        Root::new(pane, window, cx)
    });
    window.simulate_resize(size(px(1280.0), px(1800.0)));
    draw(&mut window);
    (owner.unwrap(), (*window).clone())
}

#[gpui::test]
fn native_select_keyboard_save_add_remove_and_narrow_layout(cx: &mut TestAppContext) {
    let _guard = crate::gpui_shell::settings_fixture::lock_theme_studio();
    let folder = tempfile::tempdir().unwrap();
    let path = folder.path().join("serial.json");
    let (pane, mut window) = setup(cx, &path);
    assert!(window.debug_bounds("serial-page").is_some());
    click("serial-select-0", &mut window);
    window.simulate_keystrokes("up enter");
    draw(&mut window);
    assert_eq!(pane.read_with(&mut window, |pane, cx| pane.serial.draft(cx).unwrap().data_bits), 7);
    click("nebula-btn-serial-save", &mut window);
    assert!(pane.read_with(&mut window, |pane, _| pane.serial.saved));
    assert_eq!(Manager::open(path, None).snapshots()[0].config.data_bits, 7);
    click("nebula-btn-serial-add", &mut window);
    assert_eq!(pane.read_with(&mut window, |pane, _| pane.serial.snapshots.len()), 2);
    click("nebula-btn-serial-remove", &mut window);
    assert_eq!(pane.read_with(&mut window, |pane, _| pane.serial.snapshots.len()), 1);
    window.simulate_resize(size(px(800.0), px(1800.0)));
    draw(&mut window);
    let input = window.debug_bounds("serial-input-0").unwrap();
    let page = window.debug_bounds("serial-page").unwrap();
    assert!(input.size.height >= px(32.0));
    assert!(input.origin.x >= page.origin.x && input.right() <= page.right());
}

#[gpui::test]
fn start_failure_returns_to_editable_state_and_connect_does_not_open_a_window(
    cx: &mut TestAppContext,
) {
    let _guard = crate::gpui_shell::settings_fixture::lock_theme_studio();
    let folder = tempfile::tempdir().unwrap();
    let (pane, mut window) = setup(cx, &folder.path().join("serial.json"));
    click("nebula-btn-serial-start", &mut window);
    // Only the deliberately invalid fixture name is opened; no hardware port is touched.
    let deadline = std::time::Instant::now() + Duration::from_secs(3);
    loop {
        let failed = window.read(|cx| {
            cx.global::<SerialHub>().0.lock().snapshots()[0].status.phase == Phase::Failed
        });
        if failed {
            break;
        }
        assert!(std::time::Instant::now() < deadline);
        std::thread::sleep(Duration::from_millis(2));
    }
    window.executor().advance_clock(Duration::from_millis(250));
    draw(&mut window);
    assert_eq!(
        pane.read_with(&mut window, |pane, _| pane.serial.selected().unwrap().status.phase),
        Phase::Failed
    );
    let received = std::rc::Rc::new(std::cell::Cell::new(false));
    let result = received.clone();
    window.update(|_, cx| {
        cx.subscribe(&pane, move |_, event, _| {
            if matches!(event, SettingsPaneEvent::LaunchSerial(_)) {
                result.set(true);
            }
        })
        .detach();
        pane.update(cx, |pane, cx| {
            pane.serial.snapshots[0].status.phase = Phase::Running;
            cx.notify();
        });
    });
    draw(&mut window);
    let before = window.read(|cx| cx.windows().len());
    click("nebula-btn-serial-connect", &mut window);
    assert!(received.get(), "connect did not emit the workspace terminal command");
    assert_eq!(window.read(|cx| cx.windows().len()), before, "settings spawned another GUI");
}

/// Real GPUI settings only: no PTY, no automatic serial-port opening.
#[test]
#[ignore = "manual native visual review; requires fresh PEBREL_CONFIG_DIR and PEBREL_SERIAL_QA_DIR"]
fn native_serial_visual_review() {
    let config =
        std::path::PathBuf::from(std::env::var_os("PEBREL_CONFIG_DIR").expect("isolated config"));
    let output =
        std::path::PathBuf::from(std::env::var_os("PEBREL_SERIAL_QA_DIR").expect("QA output"));
    assert!(config.is_absolute() && output.is_absolute());
    assert!(!config.join("pebrel_settings.txt").exists(), "use a fresh isolated config");
    std::fs::create_dir_all(&config).unwrap();
    std::fs::create_dir_all(&output).unwrap();
    std::fs::write(
        config.join("pebrel_settings.txt"),
        "theme=Nord\nfollow_system_theme=0\nlanguage=zh-CN\nfont_size=14\n",
    )
    .unwrap();
    gpui_platform::application().with_assets(crate::gpui_shell::NebulaAssets).run(move |cx| {
        crate::gpui_shell::init(cx, None);
        let handle = cx
            .open_window(
                gpui::WindowOptions {
                    titlebar: Some(gpui::TitlebarOptions {
                        title: Some("Pebrel · 串口服务界面验收".into()),
                        ..Default::default()
                    }),
                    window_bounds: Some(gpui::WindowBounds::Windowed(gpui::Bounds::new(
                        gpui::point(px(40.0), px(30.0)),
                        size(px(1100.0), px(850.0)),
                    ))),
                    focus: false,
                    show: true,
                    ..Default::default()
                },
                |window, cx| {
                    let pane = cx.new(|cx| SettingsPane::new(window, cx));
                    pane.update(cx, |pane, _| pane.active_section = SERIAL_SECTION);
                    cx.new(|cx| Root::new(pane, window, cx))
                },
            )
            .unwrap();
        cx.spawn(async move |cx| {
            cx.background_executor().timer(Duration::from_secs(1)).await;
            std::fs::write(output.join("ready"), std::process::id().to_string()).unwrap();
            for _ in 0..600 {
                if output.join("capture-complete").exists() {
                    break;
                }
                cx.background_executor().timer(Duration::from_millis(500)).await;
            }
            // Drop the surface and its input/animation tasks before the test app audits handles.
            cx.update_window(handle.into(), |_, window, _| window.remove_window()).unwrap();
            cx.background_executor().timer(Duration::from_millis(500)).await;
            cx.update(|cx| cx.quit());
        })
        .detach();
    });
}
