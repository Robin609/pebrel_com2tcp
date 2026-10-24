using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace PebrelLauncher
{
    internal enum TerminalMode { Local, Com11, Com12 }

    internal interface IDesktopActions
    {
        void Start(ProcessStartInfo start);
        void OpenFolder(string folder);
        void OpenBridge(string executable, string workingDirectory);
    }

    internal sealed class DesktopActions : IDesktopActions
    {
        public void Start(ProcessStartInfo start)
        {
            using (Process process = Process.Start(start))
                if (process == null) throw new IOException("Windows 未能启动所选程序。");
        }

        public void OpenFolder(string folder)
        {
            Start(new ProcessStartInfo("explorer.exe", LauncherController.Quote(folder)) {
                UseShellExecute = false, CreateNoWindow = true
            });
        }

        public void OpenBridge(string executable, string workingDirectory)
        {
            foreach (Process process in Process.GetProcessesByName("Com2Tcp"))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase)
                            && process.MainWindowHandle != IntPtr.Zero)
                        {
                            NativeWindow.ShowWindow(process.MainWindowHandle, 9);
                            NativeWindow.SetForegroundWindow(process.MainWindowHandle);
                            return;
                        }
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            Start(new ProcessStartInfo(executable) {
                WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true
            });
        }
    }

    internal static class NativeWindow
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr window, int command);
    }

    internal sealed class ListenerState
    {
        internal readonly bool Com11;
        internal readonly bool Com12;
        internal readonly string Error;
        internal ListenerState(bool com11, bool com12, string error)
        { Com11 = com11; Com12 = com12; Error = error; }
    }

    internal sealed class LauncherController
    {
        internal readonly string Root;
        internal readonly string ConfigurationDirectory;
        internal readonly string Executable;
        private readonly IDesktopActions _desktop;
        private readonly Func<ListenerState> _listeners;
        private readonly string _ncat;

        internal LauncherController(string root, IDesktopActions desktop, Func<ListenerState> listeners, string ncat)
        {
            Root = Path.GetFullPath(root);
            Executable = Path.Combine(Root, "clients", "pebrel-v2.2.0", "pebrel.exe");
            ConfigurationDirectory = Path.Combine(Root, "clients", "pebrel-data");
            _desktop = desktop;
            _listeners = listeners;
            _ncat = ncat;
        }

        internal static LauncherController Create(string root)
        {
            return new LauncherController(root, new DesktopActions(), ReadListeners,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "scoop", "apps", "nmap", "current", "ncat.exe"));
        }

        internal static string FindRoot(string applicationDirectory)
        {
            DirectoryInfo directory = new DirectoryInfo(applicationDirectory);
            string root = directory.FullName;
            if (root.Length > directory.Root.FullName.Length)
                root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Directory.Exists(Path.Combine(root, "clients"))) return root;
            string parent = directory.Parent == null ? null : directory.Parent.FullName;
            if (parent != null && Directory.Exists(Path.Combine(parent, "clients"))) return parent;
            throw new DirectoryNotFoundException("未找到 clients 目录。请把启动台放回 com2tcp 的 bin 目录。");
        }

        internal ListenerState Refresh()
        {
            try { return _listeners(); }
            catch (Exception error) { return new ListenerState(false, false, error.Message); }
        }

        internal static ListenerState ReadListeners()
        {
            IPEndPoint[] endpoints = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return new ListenerState(IsListening(endpoints, 2011), IsListening(endpoints, 2012), null);
        }

        internal static bool IsListening(IEnumerable<IPEndPoint> endpoints, int port)
        {
            // The existing clients connect to IPv4 127.0.0.1, not an arbitrary NIC or IPv6-only listener.
            return endpoints.Any(delegate(IPEndPoint endpoint) {
                return endpoint.Port == port &&
                    (endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Address.Equals(IPAddress.Any));
            });
        }

        private static void RequireFile(string file, string label)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("缺少" + label + "：" + file, file);
        }

        internal ProcessStartInfo Prepare(TerminalMode mode)
        {
            if (!Enum.IsDefined(typeof(TerminalMode), mode)) throw new ArgumentException("未知终端入口。");
            RequireFile(Executable, " Pebrel 程序");
            string config = Path.Combine(Root, "clients", "pebrel-v2.2.0", "pebrel.lua");
            RequireFile(config, " Pebrel 配置");
            RequireFile(Path.Combine(Root, "pebrel-shell.ps1"), " UTF-8 初始化脚本");
            string profilesFile = Path.Combine(ConfigurationDirectory, "terminal_profiles.json");
            RequireFile(profilesFile, "终端入口配置");
            string profileId = mode == TerminalMode.Local ? "com2tcp-utf8" :
                mode == TerminalMode.Com11 ? "com2tcp-com11-utf8" : "com2tcp-com12-utf8";
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> document = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(profilesFile));
                object entries;
                if (document == null || !document.TryGetValue("profiles", out entries))
                    throw new FormatException("缺少 profiles 列表");
                object[] profiles = entries as object[];
                System.Collections.ArrayList list = entries as System.Collections.ArrayList;
                IEnumerable<object> values = profiles != null ? profiles : list != null ? list.Cast<object>() : null;
                if (values == null) throw new FormatException("profiles 必须是数组");
                Dictionary<string, object> profile = values.OfType<Dictionary<string, object>>().SingleOrDefault(
                    delegate(Dictionary<string, object> entry) { return entry.ContainsKey("id") && (entry["id"] as string) == profileId; });
                if (profile == null) throw new FormatException("找不到入口 " + profileId);
                if (!profile.ContainsKey("shell_id") || (profile["shell_id"] as string) != "powershell")
                    throw new FormatException("入口需要 powershell shell_id");
                object command;
                if (!profile.TryGetValue("command", out command) || !(command is string))
                    throw new FormatException("入口缺少 command");
                RequireFile((string)command, " PowerShell 程序");
            }
            catch (FileNotFoundException) { throw; }
            catch (Exception error) { throw new InvalidDataException("终端入口配置不可用：" + error.Message, error); }

            if (mode != TerminalMode.Local)
            {
                RequireFile(Path.Combine(Root, "bin", "NcConsole.exe"), " TCP 启动器");
                RequireFile(_ncat, " Ncat；请恢复 Scoop Nmap 安装");
                ListenerState state = Refresh();
                if (state.Error != null) throw new IOException("无法检测 TCP 监听状态：" + state.Error);
                if (!(mode == TerminalMode.Com11 ? state.Com11 : state.Com12))
                    throw new IOException("TCP " + (mode == TerminalMode.Com11 ? "2011" : "2012") +
                        " 未监听。请先打开串口服务，启动对应通道。");
            }
            ProcessStartInfo start = new ProcessStartInfo(Executable,
                "--config-file " + Quote(config) + " --working-directory " + Quote(Root) +
                " --shell " + Quote("profile:powershell|" + profileId));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = Root;
            start.EnvironmentVariables["PEBREL_CONFIG_DIR"] = ConfigurationDirectory;
            return start;
        }

        internal void Launch(TerminalMode mode) { _desktop.Start(Prepare(mode)); }
        internal void OpenConfiguration() { OpenFolder(ConfigurationDirectory); }
        internal void OpenLocation() { OpenFolder(Root); }
        private void OpenFolder(string folder)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("目录不存在：" + folder);
            _desktop.OpenFolder(folder);
        }
        internal void OpenBridge()
        {
            string executable = Path.Combine(Root, "bin", "Com2Tcp.exe");
            RequireFile(executable, "串口服务程序；请先运行 build.ps1");
            _desktop.OpenBridge(executable, Root);
        }

        internal static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
