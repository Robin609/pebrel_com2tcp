using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using PebrelLauncher;

internal static class LauncherTests
{
    private static int _passed;
    private static string _fixture;
    private static string _results;
    private static FakeDesktop _desktop;
    private static bool _com11 = true, _com12 = true;

    private sealed class FakeDesktop : IDesktopActions
    {
        internal readonly List<ProcessStartInfo> Started = new List<ProcessStartInfo>();
        internal string Folder;
        internal string Bridge;
        internal bool FailStart;
        public void Start(ProcessStartInfo start)
        {
            if (FailStart) throw new IOException("启动失败测试");
            Started.Add(start);
        }
        public void OpenFolder(string path) { Folder = path; }
        public void OpenBridge(string executable, string root) { Bridge = executable; }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            string root = Path.GetFullPath(args[0]);
            if (args.Length > 1)
            {
                LauncherController actual = LauncherController.Create(root);
                if (args[1] == "--launch") actual.Launch((TerminalMode)Enum.Parse(typeof(TerminalMode), args[2]));
                else if (args[1] == "--status") { ListenerState state = actual.Refresh(); Console.WriteLine("COM11=" + state.Com11 + ";COM12=" + state.Com12 + ";Error=" + state.Error); }
                else if (args[1] == "--open-bridge") actual.OpenBridge();
                else if (args[1] == "--open-config") actual.OpenConfiguration();
                else if (args[1] == "--open-location") actual.OpenLocation();
                else throw new ArgumentException("Unknown smoke-test action");
                Console.WriteLine("ACTION_DISPATCHED " + args[1]);
                return 0;
            }
            _results = Path.Combine(root, "tests", "results");
            _fixture = Path.Combine(_results, "launcher-fixture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_fixture);
            foreach (string file in new[] { "clients/pebrel-v2.2.0/pebrel.exe", "clients/pebrel-v2.2.0/pebrel.lua", "pebrel-shell.ps1", "bin/NcConsole.exe", "bin/Com2Tcp.exe", "ncat.exe", "powershell.exe" })
            {
                string path = Path.Combine(_fixture, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "fixture");
            }
            Directory.CreateDirectory(Path.Combine(_fixture, "clients/pebrel-data"));
            WriteProfiles();
            _desktop = new FakeDesktop();
            LauncherController controller = Controller();

            Check("three modes preserve named profile, paths and isolated environment", delegate {
                foreach (TerminalMode mode in Enum.GetValues(typeof(TerminalMode)))
                {
                    ProcessStartInfo start = controller.Prepare(mode);
                    string id = mode == TerminalMode.Local ? "com2tcp-utf8" : mode == TerminalMode.Com11 ? "com2tcp-com11-utf8" : "com2tcp-com12-utf8";
                    Assert(start.Arguments.EndsWith("--shell \"profile:powershell|" + id + "\""), "profile selection");
                    Assert(start.Arguments.Contains("--config-file \"" + Path.GetFullPath(Path.Combine(_fixture, "clients/pebrel-v2.2.0/pebrel.lua")) + "\""), "config path");
                    Assert(start.WorkingDirectory == _fixture, "working directory");
                    Assert(start.EnvironmentVariables["PEBREL_CONFIG_DIR"] == controller.ConfigurationDirectory, "isolated config directory");
                    Assert(!start.UseShellExecute && start.CreateNoWindow, "no command window");
                }
                controller.Launch(TerminalMode.Local);
                Assert(_desktop.Started.Count == 1, "launch dispatch");
            });
            Check("offline ports refuse connection; local terminal remains available", delegate {
                _com11 = _com12 = false;
                Expect(delegate { controller.Prepare(TerminalMode.Com11); }, "2011");
                Expect(delegate { controller.Prepare(TerminalMode.Com12); }, "2012");
                controller.Prepare(TerminalMode.Local);
                _com11 = _com12 = true;
            });
            Check("missing Ncat affects serial modes only", delegate {
                string file = Path.Combine(_fixture, "ncat.exe");
                File.Move(file, file + ".saved");
                try { Expect(delegate { controller.Prepare(TerminalMode.Com11); }, "Ncat"); controller.Prepare(TerminalMode.Local); }
                finally { File.Move(file + ".saved", file); }
            });
            Check("missing exe, invalid profiles and missing profile are actionable", delegate {
                string file = controller.Executable;
                File.Move(file, file + ".saved");
                try { Expect(delegate { controller.Prepare(TerminalMode.Local); }, "Pebrel"); }
                finally { File.Move(file + ".saved", file); }
                string profiles = Path.Combine(controller.ConfigurationDirectory, "terminal_profiles.json");
                File.WriteAllText(profiles, "not-json");
                Expect(delegate { controller.Prepare(TerminalMode.Local); }, "配置");
                File.WriteAllText(profiles, "{\"profiles\":[]}");
                Expect(delegate { controller.Prepare(TerminalMode.Com11); }, "com2tcp-com11-utf8");
                WriteProfiles();
                Expect(delegate { controller.Prepare((TerminalMode)99); }, "未知");
            });
            Check("bridge and both folders dispatch exact targets", delegate {
                controller.OpenBridge(); Assert(_desktop.Bridge == Path.GetFullPath(Path.Combine(_fixture, "bin/Com2Tcp.exe")), "bridge target");
                controller.OpenConfiguration(); Assert(_desktop.Folder == controller.ConfigurationDirectory, "configuration target");
                controller.OpenLocation(); Assert(_desktop.Folder == _fixture, "location target");
            });
            Check("root lookup and Windows argument quoting", delegate {
                Assert(LauncherController.FindRoot(Path.Combine(_fixture, "bin")) == _fixture, "bin discovery");
                Assert(LauncherController.FindRoot(Path.Combine(_fixture, "bin") + Path.DirectorySeparatorChar) == _fixture, "executable BaseDirectory trailing slash");
                Assert(LauncherController.FindRoot(_fixture) == _fixture, "root discovery");
                Assert(LauncherController.Quote("C:\\two words\\") == "\"C:\\two words\\\\\"", "trailing slash");
                Assert(LauncherController.Quote("a\"b") == "\"a\\\"b\"", "quote escaping");
            });
            Check("listener detection observes real TCP without connecting", delegate {
                TcpListener server = new TcpListener(IPAddress.Loopback, 0);
                server.Start();
                try
                {
                    int port = ((IPEndPoint)server.LocalEndpoint).Port;
                    var endpoints = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                    Assert(LauncherController.IsListening(endpoints, port), "live listener");
                    Assert(!LauncherController.IsListening(new[] { new IPEndPoint(IPAddress.IPv6Loopback, port) }, port), "not IPv6-only");
                    Assert(!server.Pending(), "status did not establish a client");
                }
                finally { server.Stop(); }
                LauncherController broken = new LauncherController(_fixture, _desktop, delegate { throw new IOException("denied"); }, Path.Combine(_fixture, "ncat.exe"));
                Assert(broken.Refresh().Error == "denied", "detection error");
                Expect(delegate { broken.Prepare(TerminalMode.Com11); }, "denied");
            });
            Check("icon has all seven sizes and executable has an associated icon", delegate {
                using (BinaryReader reader = new BinaryReader(File.OpenRead(Path.Combine(root, "launcher/PebrelLauncher.ico"))))
                {
                    Assert(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 7, "ICO header");
                    foreach (int size in new[] { 16, 24, 32, 48, 64, 128, 256 })
                    {
                        int width = reader.ReadByte(); reader.ReadByte();
                        Assert((width == 0 ? 256 : width) == size, "icon size " + size);
                        reader.ReadBytes(14);
                    }
                }
                using (Icon icon = Icon.ExtractAssociatedIcon(Path.Combine(root, "tests/bin/PebrelLauncher.exe")))
                    Assert(icon != null, "executable icon");
            });
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Check("visible form wires every action, refresh, error and keyboard default", delegate {
                LauncherForm form = new LauncherForm(controller);
                try
                {
                    form.Show();
                    Assert(form.BackColor == PebrelUi.PebrelTheme.Panel, "shared Pebrel Nord background");
                    PumpUntil(delegate { return ((Label)form.Controls.Find("Com11StatusLabel", true)[0]).Text != "检测中…"; });
                    foreach (string button in new[] { "LocalTerminalButton", "Com11Button", "Com12Button", "BridgeButton", "ConfigurationButton" })
                        ((Button)form.Controls.Find(button, true)[0]).PerformClick();
                    Assert(_desktop.Started.Count == 4, "all terminal buttons fired");
                    Assert(_desktop.Folder == controller.ConfigurationDirectory, "configuration button");
                    Assert(form.AcceptButton == form.Controls.Find("LocalTerminalButton", true)[0], "Enter opens local terminal");
                    LinkLabel location = (LinkLabel)form.Controls.Find("LocationLink", true)[0];
                    location.GetType().GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(location, new object[] { new LinkLabelLinkClickedEventArgs(location.Links[0]) });
                    Assert(_desktop.Folder == _fixture, "location link");
                    using (Bitmap render = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(render, new Rectangle(0, 0, form.Width, form.Height)); render.Save(Path.Combine(_results, "launcher-ui.png")); }
                    _com11 = false;
                    ((Button)form.Controls.Find("RefreshButton", true)[0]).PerformClick();
                    PumpUntil(delegate { return ((Label)form.Controls.Find("Com11StatusLabel", true)[0]).Text.Contains("未监听"); });
                    ((Button)form.Controls.Find("Com11Button", true)[0]).PerformClick();
                    Assert(form.Controls.Find("FeedbackLabel", true)[0].Text.Contains("2011"), "offline error shown");
                    _desktop.FailStart = true;
                    ((Button)form.Controls.Find("LocalTerminalButton", true)[0]).PerformClick();
                    Assert(form.Controls.Find("FeedbackLabel", true)[0].Text == "启动失败测试", "process failure shown");
                    _desktop.FailStart = false;
                    form.Close();
                }
                catch (Exception error) { Console.WriteLine("UI failure before cleanup: " + error); throw; }
                finally { form.Dispose(); }
            });
            Console.WriteLine("PASS: " + _passed + " launcher scenarios");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }

    private static LauncherController Controller()
    { return new LauncherController(_fixture, _desktop, delegate { return new ListenerState(_com11, _com12, null); }, Path.Combine(_fixture, "ncat.exe")); }
    private static void WriteProfiles()
    {
        string command = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Path.Combine(_fixture, "powershell.exe"));
        string[] ids = { "com2tcp-utf8", "com2tcp-com11-utf8", "com2tcp-com12-utf8" };
        string json = "{\"profiles\":[" + string.Join(",", ids.Select(id => "{\"id\":\"" + id + "\",\"shell_id\":\"powershell\",\"command\":" + command + "}")) + "]}";
        File.WriteAllText(Path.Combine(_fixture, "clients/pebrel-data/terminal_profiles.json"), json);
    }
    private static void Check(string name, Action body) { body(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("ASSERT " + message); }
    private static void Expect(Action action, string text)
    {
        try { action(); } catch (Exception error) { Assert(error.Message.Contains(text), "expected error: " + text + "; actual: " + error.Message); return; }
        throw new Exception("Expected failure: " + text);
    }
    private static void PumpUntil(Func<bool> ready)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!ready() && timer.ElapsedMilliseconds < 4000) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(ready(), "UI state timed out");
    }
}
