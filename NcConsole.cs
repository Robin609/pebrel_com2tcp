using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class NcConsole
{
    private static readonly string NcatPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "nmap", "current", "ncat.exe");
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    // 转发 nc 参数，在交互式控制台启用即时 VT 输入，并在退出时恢复原有模式。
    private static int Main(string[] args)
    {
        IntPtr input = GetStdHandle(-10), output = GetStdHandle(-11);
        uint inputMode, outputMode;
        bool hasInput = GetConsoleMode(input, out inputMode);
        bool hasOutput = GetConsoleMode(output, out outputMode);
        bool changedInput = false, changedOutput = false;
        ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; };
        try
        {
            if (hasInput && hasOutput)
            {
                changedInput = SetConsoleMode(input,
                    (inputMode & ~(EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput);
                changedOutput = SetConsoleMode(output, outputMode | EnableVirtualTerminalProcessing);
                Console.CancelKeyPress += cancel;
            }
            StringBuilder arguments = new StringBuilder();
            foreach (string arg in args)
            {
                if (arguments.Length != 0) arguments.Append(' ');
                arguments.Append(QuoteArgument(arg));
            }
            using (Process child = Process.Start(new ProcessStartInfo(NcatPath, arguments.ToString()) {
                UseShellExecute = false
            }))
            {
                child.WaitForExit();
                return child.ExitCode;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("nc: " + error.Message);
            return 1;
        }
        finally
        {
            if (hasInput && hasOutput) Console.CancelKeyPress -= cancel;
            if (changedInput) SetConsoleMode(input, inputMode);
            if (changedOutput) SetConsoleMode(output, outputMode);
        }
    }

    // 按 Windows 命令行规则保留参数中的空格、引号与反斜杠。
    private static string QuoteArgument(string value)
    {
        StringBuilder result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
}
