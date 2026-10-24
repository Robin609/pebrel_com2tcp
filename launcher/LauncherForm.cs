using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using PebrelUi;

namespace PebrelLauncher
{
    internal sealed class LauncherForm : Form
    {
        internal const string WindowTitle = "Pebrel 启动台";
        private static readonly Color Ink = PebrelTheme.Ink;
        private static readonly Color Muted = PebrelTheme.Muted;
        private static readonly Color Accent = PebrelTheme.Accent;
        private readonly LauncherController _controller;
        private readonly Label _com11 = new Label();
        private readonly Label _com12 = new Label();
        private readonly Label _feedback = new Label();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly ToolTip _tips = new ToolTip();
        private readonly Button _refresh;
        private bool _refreshing;
        private bool _closing;

        internal LauncherForm(LauncherController controller)
        {
            _controller = controller;
            Text = WindowTitle;
            Name = "PebrelLauncherWindow";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            ForeColor = Ink;
            BackColor = PebrelTheme.Panel;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(724, 528);
            MinimumSize = new Size(660, 550);
            HandleCreated += delegate { PebrelTheme.ApplyTitleBar(this); };
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("PebrelLauncher.ico"))
                Icon = new Icon(icon, 32, 32);

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 20), ColumnCount = 1, RowCount = 7 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 33.334F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(root);

            Panel heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            PictureBox logo = new PictureBox { Location = new Point(0, 5), Size = new Size(52, 52), SizeMode = PictureBoxSizeMode.Zoom };
            using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream("PebrelLauncher.png"))
            using (Image original = Image.FromStream(image)) logo.Image = new Bitmap(original);
            heading.Controls.Add(logo);
            Label title = new Label { Text = "Pebrel", Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), AutoSize = true, Location = new Point(64, 1) };
            heading.Controls.Add(title);
            heading.Controls.Add(new Label { Text = "选择一个终端，开始工作。", ForeColor = Muted, AutoSize = true, Location = new Point(67, 43) });
            Label version = new Label { Text = "2.2.0  /  UTF-8", ForeColor = Muted, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            heading.Controls.Add(version);
            heading.Resize += delegate { version.Location = new Point(Math.Max(270, heading.ClientSize.Width - version.PreferredWidth), 15); };
            root.Controls.Add(heading, 0, 0);

            root.Controls.Add(Profile("本地终端", "PowerShell · UTF-8", null, "打开终端(&L)", "LocalTerminalButton", delegate { Launch(TerminalMode.Local); }), 0, 1);
            root.Controls.Add(Profile("COM11", "127.0.0.1:2011 · Raw TCP", _com11, "连接(&1)", "Com11Button", delegate { Launch(TerminalMode.Com11); }), 0, 2);
            root.Controls.Add(Profile("COM12", "127.0.0.1:2012 · Raw TCP", _com12, "连接(&2)", "Com12Button", delegate { Launch(TerminalMode.Com12); }), 0, 3);

            FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0), WrapContents = false };
            tools.Controls.Add(ActionButton("打开串口服务(&B)", "BridgeButton", delegate { Act(_controller.OpenBridge, "已打开串口服务；在对应通道中启动监听。"); }));
            tools.Controls.Add(ActionButton("配置目录(&C)", "ConfigurationButton", delegate { Act(_controller.OpenConfiguration, "已打开配置目录。"); }));
            _refresh = ActionButton("刷新状态(&R)", "RefreshButton", async delegate { await RefreshStatus(); });
            tools.Controls.Add(_refresh);
            root.Controls.Add(tools, 0, 4);

            _feedback.Name = "FeedbackLabel";
            _feedback.Dock = DockStyle.Fill;
            _feedback.TextAlign = ContentAlignment.MiddleLeft;
            _feedback.Text = "串口连接需要先启动对应 TCP 通道。";
            _feedback.ForeColor = Muted;
            _feedback.AutoEllipsis = true;
            root.Controls.Add(_feedback, 0, 5);
            LinkLabel location = new LinkLabel { Text = "程序位置：" + controller.Root, AutoEllipsis = true, Dock = DockStyle.Fill, LinkColor = Muted, ActiveLinkColor = Accent, VisitedLinkColor = Muted, Name = "LocationLink", TextAlign = ContentAlignment.MiddleLeft };
            location.LinkClicked += delegate { Act(_controller.OpenLocation, "已打开程序所在目录。"); };
            root.Controls.Add(location, 0, 6);
            AcceptButton = (Button)Controls.Find("LocalTerminalButton", true)[0];
            _timer.Interval = 3000;
            _timer.Tick += async delegate { await RefreshStatus(); };
            Shown += async delegate { await RefreshStatus(); _timer.Start(); };
            FormClosing += delegate { _closing = true; _timer.Stop(); };
            FormClosed += delegate { _timer.Dispose(); _tips.Dispose(); logo.Image.Dispose(); };
        }

        private Control Profile(string name, string description, Label status, string action, string buttonName, Action clicked)
        {
            TableLayoutPanel row = new TableLayoutPanel { BackColor = PebrelTheme.Panel, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 10, 0, 10), Margin = new Padding(0, 0, 0, 8) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
            FlowLayoutPanel summary = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            summary.Controls.Add(new Label { Text = name, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 14, 0) });
            if (status != null)
            {
                status.Name = name == "COM11" ? "Com11StatusLabel" : "Com12StatusLabel";
                status.Text = "检测中…";
                status.AutoSize = true;
                status.ForeColor = Muted;
                status.Margin = new Padding(0, 5, 0, 0);
                summary.Controls.Add(status);
            }
            row.Controls.Add(summary, 0, 0);
            row.Controls.Add(new Label { Text = description, ForeColor = Muted, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            Button button = ActionButton(action, buttonName, delegate { clicked(); });
            button.Dock = DockStyle.Fill;
            ((PebrelButton)button).Kind = buttonName == "LocalTerminalButton" ? ButtonKind.Primary : ButtonKind.Outline;
            button.Margin = new Padding(0, 4, 0, 4);
            row.Controls.Add(button, 1, 0);
            row.SetRowSpan(button, 2);
            return row;
        }

        private static Button ActionButton(string text, string name, EventHandler clicked)
        {
            Button button = new PebrelButton { Text = text, Name = name, AccessibleName = text.Replace("&", ""), AutoSize = true, Height = 34, Margin = new Padding(0, 0, 10, 0), Kind = ButtonKind.Outline };
            button.Click += clicked;
            return button;
        }

        private void Launch(TerminalMode mode)
        {
            string name = mode == TerminalMode.Local ? "本地终端" : mode == TerminalMode.Com11 ? "COM11" : "COM12";
            Act(delegate { _controller.Launch(mode); }, "已请求打开 " + name + "，请在 Pebrel 中继续。");
        }

        private void Act(Action action, string success)
        {
            try { action(); Feedback(success, false); }
            catch (Exception error) { Feedback(error.Message, true); }
        }

        private void Feedback(string text, bool error)
        {
            _feedback.Text = text;
            _feedback.ForeColor = error ? PebrelTheme.Danger : Muted;
            _tips.SetToolTip(_feedback, text);
            _feedback.AccessibleDescription = text;
        }

        private async Task RefreshStatus()
        {
            if (_refreshing || _closing) return;
            _refreshing = true;
            _refresh.Enabled = false;
            try
            {
                ListenerState state = await Task.Run(delegate { return _controller.Refresh(); });
                if (_closing || IsDisposed) return;
                SetStatus(_com11, state.Com11, state.Error);
                SetStatus(_com12, state.Com12, state.Error);
            }
            finally
            {
                _refreshing = false;
                if (!_closing && !IsDisposed) _refresh.Enabled = true;
            }
        }

        private void SetStatus(Label label, bool listening, string error)
        {
            label.Text = error != null ? "无法检测" : listening ? "● 已监听" : "○ 未监听";
            label.ForeColor = error != null ? PebrelTheme.Danger : listening ? PebrelTheme.Success : PebrelTheme.Warning;
            _tips.SetToolTip(label, error ?? "此状态仅表示本机 TCP 端口是否监听。");
        }
    }
}
