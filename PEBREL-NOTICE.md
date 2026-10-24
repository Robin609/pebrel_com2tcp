# Pebrel 界面来源

启动台和串口配置界面共用 `ui/PebrelTheme.cs`。主题和控件样式适配自
[Pebrel v2.2.0](https://github.com/Kuddev/pebrel/tree/v2.2.0)，提交
`2fde458e3ba9dfdee89bb89e392fb5cec80d54e2`：

- `nebula_app/src/display/ui/theme.rs`：`nord_skin` 原始色值及透明叠色。
- `nebula_app/src/display/ui/tokens.rs`：间距、6px 控件圆角、32px 最小控件高度。
- `nebula_app/src/gpui_shell/theme.rs`：Nord 主按钮、悬停和选中状态。
- `nebula_app/src/gpui_shell/widgets.rs`：透明 / 描边动作、下拉框、焦点反馈。
- `nebula_app/src/gpui_shell/settings_pane.rs`、`settings_pane/design.rs`：左侧导航、留白分组和字段层级。
- `extra/logo/nebula-titanium.png`：原版默认应用图标。

原源码保留在 `vendor_imports/pebrel-v2.2.0`，许可证为 GPL-3.0；副本见
`licenses/Pebrel-GPL-3.0.txt`。上述样式适配文件同样按 GPL-3.0-or-later 提供。
此处将 GPUI 的视觉契约适配到现有 WinForms 控件；未修改或重新编译 Pebrel 本体。
串口转发、持久化格式和原启动脚本继续沿用现有实现。
