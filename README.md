# COM ⇄ TCP 多串口、多客户端桌面工具

## 当前默认：Pebrel 原生整合版

桌面及开始菜单的 **Pebrel Terminal Hub** 直接打开
`D:\Apps\Pebrel-Serial\pebrel.exe`，不再经过独立启动台。
在 Pebrel 内进入 **设置 → 串口服务**，启动通道后点击 **打开终端**，
连接就在当前窗口的新标签内打开。串口设置使用 Pebrel 原生控件和主题。

- 配置：`D:\Apps\Pebrel-Serial\data`，已导入旧配置的 COM11 / TCP 2011 和 COM16 / TCP 2016；普通启动不自动打开串口。
- `pebrel.cmd` 打开本地 UTF-8 终端；`pebrel-com11.cmd`、`pebrel-com12.cmd` 保留原端口含义，`pebrel-com16.cmd` 连接 TCP 2016。
- 终端传输使用随应用提供的 `runtime\pebrel-serial.exe`，不依赖 Ncat，也不打开第二个 GUI。
- 源码：`F:\tools\com2tcp\vendor_imports\pebrel-v2.2.0`，本地分支 `robin/serial-bridge`；构建依赖及 Windows SDK 位于 `D:\DevTools\Pebrel`。
- 构建、回归及合并上游更新见 [原生整合版说明](vendor_imports/pebrel-v2.2.0/ROBIN-SERIAL.md)。
- 切换前的快捷方式、命令文件及配置快照位于 `D:\Apps\Pebrel-Serial\backups\before-integration-20261023`；旧程序和原始配置仍保留。

下文的独立 C# 工具保留用于恢复和原有 `nc` 用法，不是当前默认界面。
同一串口不能同时由 Pebrel、旧工具或 MobaXterm 占用；使用原生版时无需再运行 `start.cmd`。

Windows 桌面串口转发工具，使用 C#、Windows Forms 和 .NET Framework 4.8，无第三方运行依赖。

## 启动

先关闭旧版窗口，再双击 `start.cmd`。源码更新后会重新生成 `bin\Com2Tcp.exe` 并打开界面；直接打开原有 EXE 不会自动重新编译。生成后的 EXE 可以单独复制使用，目标电脑需有 .NET Framework 4.8。

仅生成 EXE：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

## 多串口通道

1. 第一条通道会恢复旧版保存的串口、波特率、TCP 地址和端口。
2. 点击“添加串口通道”，为其他串口设置独立的监听端口。
3. 每个标签页可以单独“启动服务”“停止服务”，也可以使用“全部启动”“全部停止”。
4. 启动后，每条通道的多个客户端都可以读取和写入对应的串口。
5. 通道分别显示客户端数量、收发字节数和日志；鼠标停留在连接状态上可查看客户端地址。
6. 删除通道前需先停止该通道，至少保留一条通道；关闭窗口时会停止全部通道并释放串口。

例如：

| 串口 | TCP 监听地址 | TCP 端口 | 客户端命令 |
| --- | --- | --- | --- |
| COM11 | 127.0.0.1 | 2011 | `nc 127.0.0.1 2011` |
| COM12 | 127.0.0.1 | 2012 | `nc 127.0.0.1 2012` |

可以在多个终端或设备上连接同一个 TCP 端口。不同通道分别管理各自的串口、TCP 客户端和发送队列；某条通道启动失败或串口异常时，其他通道继续运行。

同一串口只能由一条运行中的通道使用。启动前检查重复串口及重叠的 TCP 监听地址、端口，其他程序占用的端口或串口会在实际打开时报告错误。

默认串口配置为 `115200 / 8 / None / One`。无软硬件流控，DTR/RTS 关闭。波特率可手动输入，是否支持取决于串口驱动和设备。

- `0.0.0.0`：监听本机所有 IPv4 地址，远程设备连接电脑的实际 IPv4 地址。
- `127.0.0.1`：仅本机访问。
- 监听端口可设置为 `1–65535`。跨电脑连接需网络可达且防火墙允许入站；工具不会自动修改防火墙。

## 多客户端读写与广播

- 串口收到的字节原样广播给本通道所有已连接客户端。
- 每个客户端发来的字节原样写入本通道串口；同一串口的写入按收到的数据块串行执行。
- 每个客户端有独立发送队列，队列上限为 256 KiB（另有一个正在发送的数据块）。队列满或一次 TCP 发送等待超过 2 秒时，仅断开该客户端，其他连接继续转发。
- TCP 客户端正常断开后可以重新连接，不影响其他客户端或通道。
- 没有客户端时，串口输入被读取后丢弃；新客户端不会收到历史数据。
- 串口读写失败时停止该通道；串口写入超时为 2 秒。
- 每条通道独立计数，每次启动重新计数；TCP 发送字节数为所有客户端发送量之和，因此广播时可大于串口接收字节数。

TCP 是字节流，工具不解析命令、分配应答归属或保留发送分包边界。多个客户端写入的命令可能在不同接收块之间交错，串口回复会发给全部客户端。对需要严格命令应答对应的设备，应由客户端协调写入；交互终端的光标位置查询也会广播给全部终端。

## 配置保存

配置自动保存在 `%LOCALAPPDATA%\Com2Tcp\settings.xml`，包含全部通道；普通启动恢复配置但不自动打开串口，`--start` 启动全部通道。

首次保存升级后的旧版配置时，会先备份到同目录的 `settings.xml.single.bak`，再写入多通道格式。新格式用于新版工具，旧版工具不支持读取全部通道。

## 源码

- `Program.cs`：桌面入口及 `--start` 参数。
- `MainForm.cs`：通道标签页、批量启停、配置保存与旧配置迁移。
- `ChannelPanel.cs`：单条通道的串口/TCP 设置、状态、日志和独立启停。
- `BridgeService.cs`：多客户端连接、独立发送队列、串口广播与写入排队。
- `build.ps1` / `start.cmd`：编译与启动。

## Windows nc 交互控制台

ESP-IDF 控制台会查询终端光标位置。原始 Ncat 未关闭 Windows 控制台的按行输入和本地回显，导致光标应答被显示成 `^[[行;列R`，并等到回车才送给设备。

`NcConsole.cs` 启动器在交互式控制台启用 VT 输入、关闭按行输入和本地回显，然后运行已安装的 Scoop Nmap Ncat。退出时恢复控制台模式；重定向或管道输入保持原有行为。串口桥接仍然原样转发字节。

已将 `C:\Users\Robin\scoop\shims\nc.shim` 的目标设为 `F:\tools\com2tcp\bin\NcConsole.exe`，现有 `nc.exe` 命令无需增加参数。

启动 COM11 服务后直接连接：

```powershell
nc 127.0.0.1 2011
```

按回车显示设备提示符，输入 `help` 查看命令，Ctrl+C 退出客户端。服务仍独占串口，点击“停止服务”释放串口。

GUI 可用 `bin\Com2Tcp.exe --start` 启动全部已保存通道；不带参数保持手动启动。`NcConsole.exe` 使用当前用户默认 Scoop 的 `apps\nmap\current\ncat.exe`，需要保留 Nmap 安装。

## 已完成验证（2026-10-15）

- 已编译并部署新版 `bin\Com2Tcp.exe`。
- 自动化集成测试通过 8 组场景：4 客户端二进制广播与并发写入、双通道及串口异常隔离、40 次断线重连、慢客户端隔离（快速客户端完整接收 2 MiB）、停止时取消排队写入、端口/串口打开失败清理、连接与停止并发，以及 GUI 的增删/启停/重启/配置迁移/保存/关闭清理。
- 上述自动化测试运行生产转发代码，使用真实本机 TCP socket、内存串口和 WinForms 消息循环，不向物理设备发送随机测试数据。
- 实体测试同时运行 COM11（ESP32-C3，115200，TCP 2011）和 COM12（Linux，1500000，TCP 2012），每条通道接入 3 个客户端，共 6 个连接。每个客户端均完成写入与接收，广播字节逐一相同；两通道重连通过。
- MobaXterm 登录环境的内置 `nc`（Toybox）执行 `nc 127.0.0.1 2012`，发送 `ls` 后正常收到设备目录；Windows `nc` 启动器执行设备 `printf` 回读通过。
- 测试结果：`tests\results\latest.txt`、`tests\results\hardware-results.json`；GUI 集成测试渲染图为 `tests\results\ui-validation.png`。

运行不依赖物理设备的完整集成测试：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

本次实体设备验收脚本：

```powershell
python .\tests\verify-hardware.py --linux-shell-confirmed
```

实体脚本需要两个服务已启动，COM11 为上述 ESP32 控制台、COM12 已在 Linux shell；只发送 `help`、`printf` 和 `ls`。

连接 TCP 只接入当前串口会话，不会新建设备 shell，也不会补发此前屏幕内容。本次实测发现 COM12 停在 `nmtui` 设置界面，已取消返回 `cat@lubancat:~$`；主机名核对仍为 `lubancat`。MobaXterm 的登录环境优先使用 `/usr/bin/nc`，与 Windows 的 `NcConsole.exe` 入口分别验证。

## 历史方案：独立启动台（2026-10-22）

本节记录切换前的方案。旧启动台位于 `bin\PebrelLauncher.exe`，可手动打开用于恢复；
当前桌面及开始菜单快捷方式、`pebrel*.cmd` 的目标以上面的原生整合版为准。
重新运行本节的 `install-launcher.ps1` 会将快捷方式恢复为旧启动台。

启动台提供本地 UTF-8 终端、COM11（TCP 2011）、COM12（TCP 2012）三个入口；还可打开串口服务、配置目录以及程序位置。重复打开启动台会回到原窗口。

COM 行显示本机 `127.0.0.1` 对应端口的监听状态，每 3 秒更新，也可手动点击“刷新状态”。“已监听”只表示 TCP 端口可连接，不代表设备已登录或命令执行正常。端口未监听时连接会显示提示，不会创建一个立即失败的终端；请先点“打开串口服务”，在对应通道中手动启动服务。此按钮只打开或前置串口服务窗口，不会自动打开串口或修改通道设置。

启动台继续使用 `clients\pebrel-data` 的独立配置及已有的 `pebrel-shell.ps1` UTF-8 初始化。它需要保留当前 `bin`、`clients` 和初始化脚本的目录结构；不要只把启动台 EXE 移到桌面，应使用快捷方式。

重新生成启动台及安装当前用户的桌面 / 开始菜单图标：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-launcher.ps1
```

只编译启动台使用 `build-launcher.ps1`；源代码和多尺寸图标生成器在 `launcher` 目录。此构建不会重新编译或重启串口服务。

已在 `clients\pebrel-v2.2.0` 放置官方 Windows 便携版。下面的原脚本入口仍可使用：

| 启动入口 | 用途 |
| --- | --- |
| `pebrel.cmd` | 打开 Pebrel 本地终端 |
| `pebrel-com11.cmd` | 连接 COM11 对应的 `127.0.0.1:2011` |
| `pebrel-com12.cmd` | 连接 COM12 对应的 `127.0.0.1:2012` |

先在串口桥接工具中启动对应通道，再双击连接入口。连接使用 `bin\NcConsole.exe` 进行原始 TCP 通信，启用即时 VT 输入，关闭 Windows 控制台的按行输入和本地回显。输入显示和命令历史由设备端控制台提供，退出连接按 Ctrl+C 或关闭终端标签。

三个启动入口及两个快速启动配置均先加载 `pebrel-shell.ps1`，将 PowerShell 的 `[Console]::InputEncoding`、`[Console]::OutputEncoding` 和 `$OutputEncoding` 设为无 BOM 的 UTF-8，再启动连接。单独执行 `chcp 65001` 不会同步 Windows PowerShell 已缓存的编码设置。已有终端不会重新执行初始化，修改后请通过上述入口新建终端连接。

初始化脚本同时兼容 Pebrel 追加的 PowerShell Shell Integration 参数；参数按名称绑定，防止终端集成脚本路径被误当成 TCP 端口。

主配置为 `clients\pebrel-v2.2.0\pebrel.lua`；三个终端入口注册在 `clients\pebrel-data\terminal_profiles.json`，界面设置及运行数据也保存在 `clients\pebrel-data`。默认终端选择 `PowerShell UTF-8`，启动入口通过 `--shell` 指定注册配置，确保 Pebrel 已运行时仍能打开正确的标签页。启动入口设置 `PEBREL_CONFIG_DIR`，因此请通过这些入口启动。当前界面语言设为中文，并关闭内联建议及自动安装 AI 工具 Hook。

便携包 SHA256：`20023c035d7aa28680345c0049781b03a0c6a430cfdf5ebf8b3bd3d04ebf4c62`，与官方 GitHub Release 资产摘要一致。已通过 Pebrel 配置检查和 PowerShell 语法检查；通过正式注册入口启动的终端中，输入编码、输出编码、管道编码及原生控制台代码页均确认是 `65001`。COM12 入口已建立到 `127.0.0.1:2012` 的连接。本次未向串口设备发送验证命令，尚未重跑设备端 `nmcli` 检查画面。

### 启动台验证（2026-10-22）

- 9 组自动化场景通过：三个入口的参数和隔离配置、离线连接拦截、依赖缺失、配置错误、串口服务及目录入口、Windows 参数引用和 EXE 路径识别、只读监听检测、7 种图标尺寸，以及实际 WinForms 按钮、刷新、错误反馈和默认回车动作。
- 已检查实际 Windows 窗口显示，并实测桌面 / 开始菜单快捷方式的目标、工作目录和图标；重复启动只保留原窗口。
- 已实际点击“配置目录”“程序位置”“打开串口服务”：资源管理器分别打开 `clients\pebrel-data` 和程序根目录；串口服务复用原窗口，保持 0 / 2 条通道运行，未自动启动通道。
- 本地 Pebrel 终端实测输入、输出、PowerShell 管道编码均为 `65001`。
- COM11、COM12 入口均通过真实 Pebrel → PowerShell → NcConsole → Ncat → TCP 链路，终端 Grid 正确收到 UTF-8 中文与 ANSI 着色测试标记。连接测试使用临时回环服务器，结束后释放端口、关闭测试标签；没有向物理串口设备发送命令，不替代物理串口验收。
- 结果位于 `tests\results\launcher-latest.txt`、`tests\results\launcher-runtime.json`、`tests\results\launcher-desktop.json`；界面测试渲染图为 `tests\results\launcher-ui.png`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-launcher-tests.ps1
```

真实终端链路测试需先通过启动台打开一个本地终端，并保证 TCP 2011、2012 空闲。此脚本仅创建临时 TCP 回环服务，不会停止或代替正在使用的串口服务：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\verify-launcher-runtime.ps1
```
