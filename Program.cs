using System;
using System.Windows.Forms;

namespace Com2Tcp
{
    internal static class Program
    {
        [STAThread]
        // 打开串口桥接界面；--start 使用已保存配置自动启动服务，便于 nc 联调。
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(Array.IndexOf(args, "--start") >= 0));
        }
    }
}
