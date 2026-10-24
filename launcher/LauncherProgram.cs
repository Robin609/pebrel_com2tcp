using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Pebrel 启动台")]
[assembly: AssemblyDescription("Pebrel UTF-8 本地终端与 COM TCP 连接入口")]
[assembly: AssemblyProduct("Pebrel Terminal Hub")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace PebrelLauncher
{
    internal static class LauncherProgram
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (Mutex instance = new Mutex(true, "Local\\Com2Tcp.PebrelLauncher.v1", out created))
            {
                if (!created)
                {
                    IntPtr window = NativeWindow.FindWindow(null, LauncherForm.WindowTitle);
                    if (window != IntPtr.Zero)
                    {
                        NativeWindow.ShowWindow(window, 9);
                        NativeWindow.SetForegroundWindow(window);
                    }
                    return;
                }
                try
                {
                    string root = LauncherController.FindRoot(AppDomain.CurrentDomain.BaseDirectory);
                    Application.Run(new LauncherForm(LauncherController.Create(root)));
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, "Pebrel 启动台无法打开", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { instance.ReleaseMutex(); }
            }
        }
    }
}
