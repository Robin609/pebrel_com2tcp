# 在一个 Pebrel 内使用串口服务

本地定制分支为 `robin/serial-bridge`，基点为上游 `v2.2.0`
（`2fde458e3ba9dfdee89bb89e392fb5cec80d54e2`）。`upstream` 指向
`https://github.com/Kuddev/pebrel.git`。本分支不向上游自动推送。

## 使用

以 `serial-bridge` feature 构建后，打开 Pebrel 设置，选择 **串口服务**。
配置串口、波特率、校验、停止位、IPv4 地址和 TCP 端口，然后启动服务。
点击 **打开终端**，连接会在当前 Pebrel 窗口的新标签中打开。
返回工作区或关闭设置页不会停止通道；停止服务或退出 Pebrel 会释放串口。

这是进程内服务和原生 GPUI 页面，不启动 `Com2Tcp.exe`、启动台、PowerShell
或 Ncat。配置页直接使用上游的 `Input`、`Select`、`NebulaButton` 和主题。
`runtime\pebrel-serial.exe` 是随应用提供的无 GUI 控制台入口，在当前标签的 PTY 内运行。
它与主程序 `serial-connect` 子命令共用传输实现；Windows GUI 子系统的主 EXE
不能直接作为 ConPTY 的控制台子进程，不能用它替代这个入口。

新配置为数据目录内的 `serial_channels.json`，版本字段为 `1`。首次使用且新配置
不存在时，从 `%LOCALAPPDATA%\Com2Tcp\settings.xml` 导入原通道，保留源文件。
已有新配置损坏时显示错误，不以旧配置或默认值覆盖。普通启动不自动打开串口。
编辑后点击保存；启动服务和切换通道时也会保存当前有效修改。

`--data-dir <目录>` 可为此定制版指定独立数据目录，不改系统环境变量。
原来的 `--config-file`、Shell 配置和不带新参数的启动方式保持可用。

## 在 D 盘构建

当前构建依赖位于 `D:\DevTools\Pebrel`，Rust 版本由 `rust-toolchain.toml` 固定。
脚本只为当前构建进程设置环境，Cargo 下载、临时文件和编译结果均使用 D 盘。

```powershell
.\scripts\build-robin.ps1 -Mode Check
.\scripts\build-robin.ps1 -Mode Test
.\scripts\build-robin.ps1 -Mode UiTest
.\scripts\build-robin.ps1 -Mode Build
```

主程序输出为 `D:\DevTools\Pebrel\target\release\pebrel.exe`，同时构建
`pebrel-hook.exe` 和 `pebrel-serial.exe`。安装时将两个辅助入口及
`conpty.dll`、`OpenConsole.exe` 放入主程序旁的 `runtime` 目录。
本机旧快捷入口使用的数据目录脚本来自 `scripts\robin-shell.ps1`，安装到 `data\pebrel-shell.ps1`。
交付使用 release 构建，避免 GPUI 调试渲染器依赖编译机上的着色器源码。
`BuildRoot` 参数可改变
工具链和缓存根目录。Check 更新必要的锁文件后，构建和测试使用 `--locked`。

默认上游 feature 集合不变；定制构建脚本显式启用 `serial-bridge`。不启用该 feature
时不显示串口菜单，不编译服务模块。Windows SDK 的头文件、库和工具位于
`D:\DevTools\Pebrel\WindowsKits\10`，由 `vcvars64.bat` 读取注册位置。

## 合并上游更新

更新保留此分支，不以下载官方 EXE 的方式覆盖定制版。定制构建的安装归属标记为
`Source · Serial`，官方自更新入口会提示通过源码合并和重建更新。

先保证当前工作已提交，然后将要跟进的真实标签合并到本地分支：

```powershell
git switch robin/serial-bridge
git fetch upstream --tags
# 将 <上游新标签> 替换为上一步取得的实际版本标签。
git merge --no-ff <上游新标签>
.\scripts\build-robin.ps1 -Mode Check
.\scripts\build-robin.ps1 -Mode Test
.\scripts\build-robin.ps1 -Mode UiTest
.\scripts\build-robin.ps1 -Mode Build
```

合并冲突优先核对这几处薄接线：Cargo feature、CLI 参数、GPUI 初始化、设置导航、
终端创建事件和安装归属。业务模块集中在 `src/serial_bridge`；页面集中在
`src/gpui_shell/settings_pane/serial` 及 `serial.rs`，不维护上游主题或终端核心的副本。

更新后先用独立数据目录检查页面、原始 TCP 输入、UTF-8、断线和退出清理，再替换
日常 EXE。保留旧二进制和替换前的数据目录副本；旧程序未退出时不要强制覆盖。

## 验证范围

测试使用内存串口和真实回环 TCP，覆盖广播、原始输入、重连、停止、失败隔离、
配置导入和损坏保护；GPUI 测试使用实际控件及键盘/命中区域。
2026-10-23 本机验证：14 项相关自动回归通过，包括 5 项后端、2 项 GPUI 交互、
2 项导航、4 项更新归属及 1 项辅助入口路径测试；原生设置页面已做实际显示检查。
真实 release 辅助入口已通过重定向二进制传输，以及 Pebrel ConPTY 内的 UTF-8
逐字节输出、上下方向键和回车验收。可用隔离数据目录重现：

```powershell
python -X utf8 scripts/verify-robin-terminal.py --app-dir D:\Apps\Pebrel-Serial --qa-dir D:\DevTools\Pebrel\qa
```

该脚本只使用临时回环端口和新建的数据目录，不操作物理串口或现有终端。
物理串口尚未做设备收发验收；连接前先断开其他终端对该串口的占用。
