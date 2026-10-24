using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;
using PebrelUi;

namespace Com2Tcp
{
    public sealed class SavedSettings
    {
        public string PortName = "";
        public string BaudRate = "115200";
        public int DataBits = 8;
        public string Parity = "None";
        public string StopBits = "One";
        public string ListenAddress = "0.0.0.0";
        public int ListenPort = 9000;
    }

    internal sealed class ChannelPanel : UserControl
    {
        private readonly ComboBox _ports = new PebrelComboBox();
        private readonly ComboBox _baud = new PebrelComboBox();
        private readonly ComboBox _dataBits = new PebrelComboBox();
        private readonly ComboBox _parity = new PebrelComboBox();
        private readonly ComboBox _stopBits = new PebrelComboBox();
        private readonly ComboBox _address = new PebrelComboBox();
        private readonly NumericUpDown _port = new PebrelNumber();
        private readonly Button _start = new PebrelButton { Kind = ButtonKind.Primary };
        private readonly Label _status = new Label();
        private readonly Label _counters = new Label();
        private readonly TextBox _logs = new TextBox();
        private readonly GroupBox _serialGroup = new PebrelGroup();
        private readonly GroupBox _tcpGroup = new PebrelGroup();
        private readonly Timer _timer = new Timer();
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private readonly ToolTip _clientTips = new ToolTip();
        private readonly Func<Action<string>, BridgeService> _bridgeFactory;
        private BridgeService _bridge;
        private bool _busy;
        private string _lastClientAddresses;
        public event Action<ChannelPanel> Changed;
        public Func<ChannelPanel, BridgeOptions, string> ValidateStart;
        public bool IsBusy { get { return _busy; } }
        public bool IsRunning { get { return _bridge != null; } }
        public string SerialPortName { get { return _ports.Text.Trim(); } }
        public string ListenAddress { get { return _address.Text.Trim(); } }
        public int ListenPort { get { return (int)_port.Value; } }
        public string Caption
        {
            get { return (SerialPortName.Length == 0 ? "未选串口" : SerialPortName) + " · TCP " + ListenPort; }
        }

        // 每个面板独立管理一组串口配置、TCP 监听及客户端。
        public ChannelPanel(SavedSettings settings, Func<Action<string>, BridgeService> bridgeFactory = null)
        {
            _bridgeFactory = bridgeFactory;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            Dock = DockStyle.Fill;
            Padding = new Padding(28, 20, 20, 12);
            BackColor = PebrelTheme.Panel;
            ForeColor = PebrelTheme.Ink;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < root.RowCount; row++)
                root.RowStyles.Add(new RowStyle(row == 5 ? SizeType.Percent : SizeType.AutoSize, row == 5 ? 100 : 0));
            Controls.Add(root);

            Label heading = new Label();
            heading.Text = "通道设置";
            heading.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            heading.AutoSize = true;
            heading.Margin = new Padding(0, 0, 0, 24);
            root.Controls.Add(heading, 0, 0);

            ConfigureCombo(_ports, false, null);
            ConfigureCombo(_baud, false, new object[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" });
            ConfigureCombo(_dataBits, true, new object[] { "5", "6", "7", "8" });
            ConfigureCombo(_parity, true, Enum.GetNames(typeof(Parity)));
            ConfigureCombo(_stopBits, true, new object[] { "One", "OnePointFive", "Two" });
            ConfigureCombo(_address, false, new object[] { "0.0.0.0", "127.0.0.1" });

            TableLayoutPanel serialFields = CreateGroup(_serialGroup, "串口", 184, 3, 2);
            AddField(serialFields, "串口", _ports, 0, 0);
            AddField(serialFields, "波特率（可输入）", _baud, 1, 0);
            Button refresh = new PebrelButton { Kind = ButtonKind.Outline };
            refresh.Text = "刷新串口";
            refresh.AutoSize = true;
            refresh.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            refresh.Margin = new Padding(0, 24, 0, 0);
            refresh.Click += delegate { RefreshPorts(_ports.Text); };
            serialFields.Controls.Add(refresh, 2, 0);
            AddField(serialFields, "数据位", _dataBits, 0, 1);
            AddField(serialFields, "校验", _parity, 1, 1);
            AddField(serialFields, "停止位", _stopBits, 2, 1);
            root.Controls.Add(_serialGroup, 0, 1);

            TableLayoutPanel tcpFields = CreateGroup(_tcpGroup, "TCP 服务", 128, 2, 1);
            AddField(tcpFields, "监听 IPv4 地址", _address, 0, 0);
            _port.Minimum = 1;
            _port.Maximum = 65535;
            AddField(tcpFields, "监听端口", _port, 1, 0);
            Label addressHint = new PebrelLabel();
            addressHint.Text = "127.0.0.1 仅本机访问；0.0.0.0 允许其他设备连接。";
            addressHint.ForeColor = PebrelTheme.Muted;
            addressHint.AutoSize = true;
            addressHint.Margin = new Padding(0, 0, 0, 0);
            tcpFields.RowCount = 2;
            tcpFields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tcpFields.Controls.Add(addressHint, 0, 1);
            tcpFields.SetColumnSpan(addressHint, 2);
            root.Controls.Add(_tcpGroup, 0, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.Margin = new Padding(0, 8, 0, 8);
            _start.Text = "启动服务";
            _start.Size = new Size(120, 36);
            _start.Click += async delegate { await ToggleAsync(); };
            actions.Controls.Add(_start);
            _status.Text = "已停止";
            _status.ForeColor = PebrelTheme.Muted;
            _status.AutoSize = true;
            _status.Margin = new Padding(16, 10, 0, 0);
            actions.Controls.Add(_status);
            root.Controls.Add(actions, 0, 3);

            _counters.AutoSize = true;
            _counters.ForeColor = PebrelTheme.Muted;
            _counters.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(_counters, 0, 4);
            _logs.Multiline = true;
            _logs.ReadOnly = true;
            _logs.ScrollBars = ScrollBars.Vertical;
            _logs.Dock = DockStyle.Fill;
            _logs.BackColor = PebrelTheme.Input;
            _logs.ForeColor = PebrelTheme.Ink;
            _logs.BorderStyle = BorderStyle.None;
            _logs.Font = new Font("Consolas", 10F);
            _logs.HandleCreated += delegate { PebrelTheme.StyleNativeControl(_logs); };
            Panel logFrame = new Panel { Dock = DockStyle.Fill, BackColor = PebrelTheme.Input, Padding = new Padding(12), Margin = Padding.Empty };
            logFrame.Controls.Add(_logs);
            root.Controls.Add(logFrame, 0, 5);

            Label footer = new Label();
            footer.Text = "串口接收广播给全部客户端 · 写入排队 · 无客户端时丢弃串口输入 · 无流控";
            footer.ForeColor = PebrelTheme.Muted;
            footer.AutoSize = true;
            footer.Margin = new Padding(0, 10, 0, 0);
            root.Controls.Add(footer, 0, 6);

            LoadSettings(settings ?? new SavedSettings());
            RefreshPorts(_ports.Text);
            UpdateCounters(new BridgeSnapshot());
            QueueLog("本通道可同时连接多个客户端；串口回复广播给全部客户端，写入按数据块排队。");
            _timer.Interval = 250;
            _timer.Tick += async delegate { await RefreshStateAsync(); };
            _timer.Start();
            foreach (ComboBox combo in new[] { _ports, _baud, _dataBits, _parity, _stopBits, _address })
                combo.TextChanged += delegate { NotifyChanged(); };
            _port.ValueChanged += delegate { NotifyChanged(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _clientTips.Dispose();
            }
            base.Dispose(disposing);
        }

        private static void ConfigureCombo(ComboBox combo, bool listOnly, object[] values)
        {
            combo.DropDownStyle = listOnly ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
            if (values != null)
                combo.Items.AddRange(values);
        }

        private static TableLayoutPanel CreateGroup(GroupBox group, string title, int height, int columns, int rows)
        {
            group.Text = title;
            group.Dock = DockStyle.Fill;
            group.Height = height;
            group.Margin = new Padding(0, 0, 0, 8);
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.Dock = DockStyle.Fill;
            fields.Margin = Padding.Empty;
            fields.ColumnCount = columns;
            fields.RowCount = rows;
            for (int column = 0; column < columns; column++)
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
            for (int row = 0; row < rows; row++)
                fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            group.Controls.Add(fields);
            return fields;
        }

        private static void AddField(TableLayoutPanel panel, string label, Control control, int column, int row)
        {
            Panel field = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, column == panel.ColumnCount - 1 ? 0 : 16, 0) };
            Label caption = new PebrelLabel();
            caption.Text = label;
            caption.AutoSize = true;
            caption.Margin = Padding.Empty;
            caption.ForeColor = PebrelTheme.Muted;
            caption.Dock = DockStyle.Top;
            caption.Height = 24;
            caption.AutoSize = false;
            control.AccessibleName = label;
            PebrelInputFrame frame = new PebrelInputFrame(control) { Dock = DockStyle.Top };
            field.Controls.Add(frame);
            field.Controls.Add(caption);
            panel.Controls.Add(field, column, row);
        }

        private void RefreshPorts(string preferred)
        {
            try
            {
                string[] names = SerialPort.GetPortNames();
                Array.Sort(names, delegate(string left, string right)
                {
                    int leftNumber, rightNumber;
                    if (left.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                        right.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(left.Substring(3), out leftNumber) &&
                        int.TryParse(right.Substring(3), out rightNumber))
                        return leftNumber.CompareTo(rightNumber);
                    return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                });
                _ports.Items.Clear();
                _ports.Items.AddRange(names);
                _ports.Text = !string.IsNullOrWhiteSpace(preferred) ? preferred : (names.Length > 0 ? names[0] : "");
                if (names.Length == 0)
                    QueueLog("没有发现串口，可插入设备后刷新，也可手动输入 COM 名称。");
            }
            catch (Exception error)
            {
                QueueLog("枚举串口失败：" + error.Message);
            }
        }

        private BridgeOptions ReadOptions()
        {
            if (string.IsNullOrWhiteSpace(_ports.Text))
                throw new ArgumentException("请选择或输入串口，例如 COM17。");
            int baud;
            if (!int.TryParse(_baud.Text.Trim(), out baud) || baud <= 0)
                throw new ArgumentException("波特率必须是正整数。");
            IPAddress address;
            if (!IPAddress.TryParse(_address.Text.Trim(), out address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("请输入有效 IPv4 地址，例如 0.0.0.0 或 127.0.0.1。");
            return new BridgeOptions
            {
                PortName = _ports.Text.Trim(),
                BaudRate = baud,
                DataBits = int.Parse(_dataBits.Text, CultureInfo.InvariantCulture),
                Parity = (Parity)Enum.Parse(typeof(Parity), _parity.Text),
                StopBits = (StopBits)Enum.Parse(typeof(StopBits), _stopBits.Text),
                ListenAddress = address,
                ListenPort = (int)_port.Value
            };
        }

        private async Task ToggleAsync()
        {
            if (_busy)
                return;
            if (_bridge == null)
                await StartAsync(true);
            else
                await StopAsync();
        }

        public async Task<bool> StartAsync(bool showError)
        {
            if (_busy || _bridge != null)
                return false;
            BridgeOptions options;
            try
            {
                options = ReadOptions();
                string error = ValidateStart == null ? null : ValidateStart(this, options);
                if (error != null)
                    throw new ArgumentException(error);
            }
            catch (Exception error)
            {
                QueueLog("配置错误：" + error.Message);
                if (showError)
                    MessageBox.Show(this, error.Message, "检查配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            SetBusy(true);
            _status.Text = "正在启动…";
            _status.ForeColor = PebrelTheme.Warning;
            BridgeService bridge = _bridgeFactory == null ? new BridgeService(QueueLog) : _bridgeFactory(QueueLog);
            try
            {
                await Task.Run(delegate { bridge.Start(options); });
                _bridge = bridge;
                _start.Text = "停止服务";
                ((PebrelButton)_start).Kind = ButtonKind.Danger;
                _lastClientAddresses = null;
                UpdateCounters(new BridgeSnapshot());
                return true;
            }
            catch (Exception error)
            {
                _status.Text = "启动失败";
                _status.ForeColor = PebrelTheme.Danger;
                QueueLog("启动失败：" + error.Message);
                if (showError)
                    MessageBox.Show(this, error.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                SetBusy(false);
            }
        }

        public async Task StopAsync()
        {
            if (_busy || _bridge == null)
                return;
            SetBusy(true);
            _status.Text = "正在停止…";
            BridgeService bridge = _bridge;
            try
            {
                await Task.Run(async delegate { await bridge.StopAsync(); });
                UpdateCounters(bridge.Snapshot());
            }
            catch (Exception error)
            {
                QueueLog("停止服务时发生错误：" + error.Message);
            }
            finally
            {
                _bridge = null;
                _status.Text = "已停止";
                _status.ForeColor = PebrelTheme.Muted;
                _clientTips.SetToolTip(_status, "");
                _start.Text = "启动服务";
                ((PebrelButton)_start).Kind = ButtonKind.Primary;
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            _busy = value;
            _start.Enabled = !value;
            _serialGroup.Enabled = !value && _bridge == null;
            _tcpGroup.Enabled = !value && _bridge == null;
            NotifyChanged();
        }

        private async Task RefreshStateAsync()
        {
            string entry;
            int drained = 0;
            while (drained++ < 100 && _logQueue.TryDequeue(out entry))
                _logs.AppendText(entry + Environment.NewLine);
            if (_logs.TextLength > 64000)
            {
                _logs.Text = _logs.Text.Substring(_logs.TextLength - 32000);
                _logs.SelectionStart = _logs.TextLength;
                _logs.ScrollToCaret();
            }
            if (_bridge == null || _busy)
                return;
            BridgeSnapshot snapshot = _bridge.Snapshot();
            UpdateCounters(snapshot);
            if (!snapshot.Running)
            {
                await StopAsync();
                return;
            }
            _status.Text = string.Format("正在监听 · 已连接 {0} 个客户端", snapshot.ClientAddresses.Length);
            _status.ForeColor = PebrelTheme.Success;
            string addresses = string.Join(Environment.NewLine, snapshot.ClientAddresses);
            if (_lastClientAddresses != addresses)
            {
                _lastClientAddresses = addresses;
                _clientTips.SetToolTip(_status, addresses);
            }
        }

        private void UpdateCounters(BridgeSnapshot snapshot)
        {
            _counters.Text = string.Format("串口接收 {0:N0} B    串口发送 {1:N0} B    |    TCP 接收 {2:N0} B    TCP 发送 {3:N0} B",
                snapshot.SerialReceived, snapshot.SerialSent, snapshot.TcpReceived, snapshot.TcpSent);
        }

        public void QueueLog(string message)
        {
            if (_logQueue.Count < 300)
                _logQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        }

        private void LoadSettings(SavedSettings settings)
        {
            _ports.Text = settings.PortName ?? "";
            _baud.Text = settings.BaudRate ?? "115200";
            SelectSaved(_dataBits, settings.DataBits.ToString(CultureInfo.InvariantCulture), "8");
            SelectSaved(_parity, settings.Parity, "None");
            SelectSaved(_stopBits, settings.StopBits, "One");
            _address.Text = settings.ListenAddress ?? "0.0.0.0";
            _port.Value = settings.ListenPort >= 1 && settings.ListenPort <= 65535 ? settings.ListenPort : 9000;
        }

        private static void SelectSaved(ComboBox combo, string saved, string fallback)
        {
            combo.SelectedItem = saved != null && combo.Items.Contains(saved) ? saved : fallback;
        }

        public SavedSettings GetSettings()
        {
            return new SavedSettings
            {
                PortName = SerialPortName,
                BaudRate = _baud.Text.Trim(),
                DataBits = int.Parse(_dataBits.Text, CultureInfo.InvariantCulture),
                Parity = _parity.Text,
                StopBits = _stopBits.Text,
                ListenAddress = ListenAddress,
                ListenPort = ListenPort
            };
        }

        private void NotifyChanged()
        {
            Action<ChannelPanel> handler = Changed;
            if (handler != null)
                handler(this);
        }
    }
}
