using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using PebrelUi;

namespace Com2Tcp
{
    public sealed class SavedConfiguration
    {
        public List<SavedSettings> Channels = new List<SavedSettings>();
    }

    internal sealed class MainForm : Form
    {
        private readonly List<ChannelPanel> _channels = new List<ChannelPanel>();
        private readonly TabControl _tabs = new PebrelTabs();
        private readonly Button _add = new PebrelButton { Kind = ButtonKind.Outline };
        private readonly Button _remove = new PebrelButton { Kind = ButtonKind.Danger };
        private readonly Button _startAll = new PebrelButton { Kind = ButtonKind.Primary };
        private readonly Button _stopAll = new PebrelButton { Kind = ButtonKind.Outline };
        private readonly Label _summary = new Label();
        private readonly Timer _saveTimer = new Timer();
        private readonly string _settingsPath;
        private readonly Func<Action<string>, BridgeService> _bridgeFactory;
        private bool _loading = true;
        private bool _batchBusy;
        private bool _closing;
        private bool _closeRequested;
        private bool _closeQueued;
        private bool _allowClose;
        private bool _legacySettings;

        public MainForm(bool autoStart)
            : this(autoStart, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Com2Tcp", "settings.xml"), null)
        {
        }

        internal MainForm(bool autoStart, string settingsPath, Func<Action<string>, BridgeService> bridgeFactory)
        {
            _settingsPath = settingsPath;
            _bridgeFactory = bridgeFactory;
            Text = "COM ↔ TCP 多串口转发";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 9.5F);
            ForeColor = PebrelTheme.Ink;
            BackColor = PebrelTheme.Panel;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1040, 800);
            MinimumSize = new Size(940, 780);
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(16, 16, 16, 12);
            HandleCreated += delegate { PebrelTheme.ApplyTitleBar(this); };
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            Label heading = new Label();
            heading.Text = "串口服务";
            heading.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            heading.AutoSize = true;
            heading.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(heading, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.Margin = new Padding(0, 0, 0, 8);
            AddButton(actions, _add, "添加串口通道");
            AddButton(actions, _remove, "删除当前通道");
            AddButton(actions, _startAll, "全部启动");
            AddButton(actions, _stopAll, "全部停止");
            root.Controls.Add(actions, 0, 1);
            _tabs.Dock = DockStyle.Fill;
            _tabs.Multiline = true;
            _tabs.Margin = Padding.Empty;
            root.Controls.Add(_tabs, 0, 2);
            _summary.AutoSize = true;
            _summary.ForeColor = PebrelTheme.Muted;
            _summary.Margin = new Padding(0, 8, 0, 0);
            root.Controls.Add(_summary, 0, 3);

            _add.Click += delegate { AddChannel(CreateChannelSettings()); };
            _remove.Click += delegate { RemoveSelected(); };
            _startAll.Click += async delegate { await StartAllAsync(); };
            _stopAll.Click += async delegate { await StopAllAsync(); };
            _tabs.SelectedIndexChanged += delegate { UpdateActions(); };
            _saveTimer.Interval = 600;
            _saveTimer.Tick += delegate { _saveTimer.Stop(); SaveConfiguration(); };

            string loadError;
            foreach (SavedSettings settings in LoadConfiguration(out loadError))
                AddChannel(settings ?? new SavedSettings());
            if (_channels.Count == 0)
                AddChannel(new SavedSettings());
            _loading = false;
            UpdateActions();
            if (loadError != null)
                _channels[0].QueueLog(loadError);
            FormClosing += OnFormClosing;
            FormClosed += delegate { _saveTimer.Dispose(); };
            Shown += async delegate { if (autoStart) await StartAllAsync(); };
        }

        private static void AddButton(FlowLayoutPanel panel, Button button, string text)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Height = 34;
            button.Margin = new Padding(0, 0, 10, 0);
            panel.Controls.Add(button);
        }

        private void AddChannel(SavedSettings settings)
        {
            ChannelPanel channel = new ChannelPanel(settings, _bridgeFactory);
            channel.ValidateStart = ValidateChannelStart;
            channel.Changed += OnChannelChanged;
            TabPage page = new TabPage(channel.Caption);
            page.BackColor = PebrelTheme.Panel;
            page.ForeColor = PebrelTheme.Ink;
            page.Padding = Padding.Empty;
            page.Controls.Add(channel);
            _channels.Add(channel);
            _tabs.TabPages.Add(page);
            _tabs.SelectedTab = page;
            OnChannelChanged(channel);
        }

        private SavedSettings CreateChannelSettings()
        {
            SavedSettings settings = new SavedSettings();
            HashSet<int> ports = new HashSet<int>(_channels.Select(delegate(ChannelPanel channel) { return channel.ListenPort; }));
            for (int port = 9000; port <= 65535; port++)
                if (!ports.Contains(port))
                {
                    settings.ListenPort = port;
                    break;
                }
            try
            {
                settings.PortName = SerialPort.GetPortNames().FirstOrDefault(delegate(string name)
                {
                    return !_channels.Any(delegate(ChannelPanel channel)
                    {
                        return string.Equals(channel.SerialPortName, name, StringComparison.OrdinalIgnoreCase);
                    });
                }) ?? "";
            }
            catch (Exception error)
            {
                if (_channels.Count > 0)
                    _channels[0].QueueLog("枚举新通道串口失败：" + error.Message);
            }
            return settings;
        }

        private void RemoveSelected()
        {
            int index = _tabs.SelectedIndex;
            if (index < 0 || _channels.Count <= 1 || _channels[index].IsRunning || _channels[index].IsBusy)
                return;
            ChannelPanel channel = _channels[index];
            TabPage page = _tabs.TabPages[index];
            channel.Changed -= OnChannelChanged;
            _channels.RemoveAt(index);
            _tabs.TabPages.Remove(page);
            page.Dispose();
            UpdateActions();
            ScheduleSave();
        }

        private string ValidateChannelStart(ChannelPanel current, BridgeOptions options)
        {
            foreach (ChannelPanel channel in _channels)
            {
                if (channel == current || (!channel.IsRunning && !channel.IsBusy))
                    continue;
                if (string.Equals(channel.SerialPortName, options.PortName, StringComparison.OrdinalIgnoreCase))
                    return "串口 " + options.PortName + " 已被另一条通道使用。";
                IPAddress otherAddress;
                if (channel.ListenPort == options.ListenPort &&
                    IPAddress.TryParse(channel.ListenAddress, out otherAddress) &&
                    (otherAddress.Equals(IPAddress.Any) || options.ListenAddress.Equals(IPAddress.Any) ||
                     otherAddress.Equals(options.ListenAddress)))
                    return "TCP 监听端口 " + options.ListenPort + " 与另一条通道冲突。";
            }
            return null;
        }

        private async Task StartAllAsync()
        {
            if (_batchBusy || _channels.Any(delegate(ChannelPanel channel) { return channel.IsBusy; }))
                return;
            SetBatchBusy(true);
            ChannelPanel firstFailure = null;
            try
            {
                foreach (ChannelPanel channel in _channels)
                {
                    if (_closeRequested)
                        break;
                    if (!channel.IsRunning)
                    {
                        bool started = await channel.StartAsync(false);
                        if (!started && firstFailure == null)
                            firstFailure = channel;
                    }
                }
                if (firstFailure != null)
                    _tabs.SelectedIndex = _channels.IndexOf(firstFailure);
            }
            finally
            {
                SetBatchBusy(false);
                SaveConfiguration();
                QueueCloseIfReady();
            }
        }

        private async Task StopAllAsync()
        {
            if (_batchBusy || _channels.Any(delegate(ChannelPanel channel) { return channel.IsBusy; }))
                return;
            SetBatchBusy(true);
            try
            {
                await Task.WhenAll(_channels.Select(delegate(ChannelPanel channel) { return channel.StopAsync(); }));
            }
            finally
            {
                SetBatchBusy(false);
                QueueCloseIfReady();
            }
        }

        private void SetBatchBusy(bool value)
        {
            _batchBusy = value;
            foreach (ChannelPanel channel in _channels)
                channel.Enabled = !value;
            UpdateActions();
        }

        private void OnChannelChanged(ChannelPanel channel)
        {
            int index = _channels.IndexOf(channel);
            if (index >= 0)
                _tabs.TabPages[index].Text = channel.Caption;
            UpdateActions();
            ScheduleSave();
            QueueCloseIfReady();
        }

        private void UpdateActions()
        {
            bool busy = _batchBusy || _closing || _channels.Any(delegate(ChannelPanel channel) { return channel.IsBusy; });
            int selected = _tabs.SelectedIndex;
            _add.Enabled = !busy;
            _remove.Enabled = !busy && _channels.Count > 1 && selected >= 0 && !_channels[selected].IsRunning;
            _startAll.Enabled = !busy && _channels.Any(delegate(ChannelPanel channel) { return !channel.IsRunning; });
            _stopAll.Enabled = !busy && _channels.Any(delegate(ChannelPanel channel) { return channel.IsRunning; });
            _summary.Text = string.Format("运行 {0} / {1} 条通道 · 每个串口独立 TCP 端口 · 配置自动保存",
                _channels.Count(delegate(ChannelPanel channel) { return channel.IsRunning; }), _channels.Count);
        }

        private void QueueCloseIfReady()
        {
            if (!_closeRequested || _closing || _batchBusy || _closeQueued || IsDisposed ||
                _channels.Any(delegate(ChannelPanel channel) { return channel.IsBusy; }))
                return;
            _closeQueued = true;
            BeginInvoke(new Action(delegate
            {
                _closeQueued = false;
                if (!IsDisposed)
                    Close();
            }));
        }

        private async void OnFormClosing(object sender, FormClosingEventArgs args)
        {
            if (_allowClose)
                return;
            if (_closing || _batchBusy || _channels.Any(delegate(ChannelPanel channel) { return channel.IsBusy; }))
            {
                args.Cancel = true;
                _closeRequested = true;
                return;
            }
            if (!_channels.Any(delegate(ChannelPanel channel) { return channel.IsRunning; }))
            {
                _saveTimer.Stop();
                SaveConfiguration();
                return;
            }
            args.Cancel = true;
            _closing = true;
            SetBatchBusy(true);
            try
            {
                await Task.WhenAll(_channels.Select(delegate(ChannelPanel channel) { return channel.StopAsync(); }));
            }
            finally
            {
                _saveTimer.Stop();
                SaveConfiguration();
                _allowClose = true;
                Close();
            }
        }

        private List<SavedSettings> LoadConfiguration(out string error)
        {
            error = null;
            try
            {
                if (File.Exists(_settingsPath))
                {
                    using (XmlReader reader = XmlReader.Create(_settingsPath))
                    {
                        reader.MoveToContent();
                        if (reader.LocalName == "SavedSettings")
                        {
                            _legacySettings = true;
                            SavedSettings legacy = (SavedSettings)new XmlSerializer(typeof(SavedSettings)).Deserialize(reader);
                            return new List<SavedSettings> { legacy ?? new SavedSettings() };
                        }
                        SavedConfiguration config = (SavedConfiguration)new XmlSerializer(typeof(SavedConfiguration)).Deserialize(reader);
                        if (config != null && config.Channels != null)
                            return config.Channels;
                    }
                }
            }
            catch (Exception failure)
            {
                error = "读取配置失败，使用默认值：" + failure.Message;
            }
            return new List<SavedSettings>();
        }

        private void ScheduleSave()
        {
            if (_loading || _closing)
                return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveConfiguration()
        {
            if (_loading)
                return;
            string temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                SavedConfiguration config = new SavedConfiguration();
                foreach (ChannelPanel channel in _channels)
                    config.Channels.Add(channel.GetSettings());
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                if (_legacySettings && File.Exists(_settingsPath) && !File.Exists(_settingsPath + ".single.bak"))
                    File.Copy(_settingsPath, _settingsPath + ".single.bak");
                using (FileStream stream = File.Create(temporary))
                    new XmlSerializer(typeof(SavedConfiguration)).Serialize(stream, config);
                if (File.Exists(_settingsPath))
                    File.Replace(temporary, _settingsPath, null);
                else
                    File.Move(temporary, _settingsPath);
                _legacySettings = false;
            }
            catch (Exception error)
            {
                _summary.Text = "配置保存失败：" + error.Message;
                if (_channels.Count > 0)
                    _channels[0].QueueLog(_summary.Text);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
