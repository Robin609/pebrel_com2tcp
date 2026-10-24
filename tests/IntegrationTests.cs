using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Com2Tcp.Tests
{
    internal sealed class MemorySerial : ISerialTransport
    {
        private readonly BlockingCollection<object> _input = new BlockingCollection<object>();
        private readonly List<byte> _written = new List<byte>();
        private int _activeWrites;
        public int MaxConcurrentWrites;
        public int WriteDelay;
        public bool FailOpen;
        public volatile bool Disposed;
        public int WrittenCount { get { lock (_written) return _written.Count; } }
        public int ActiveWrites { get { return Volatile.Read(ref _activeWrites); } }
        public byte[] Written { get { lock (_written) return _written.ToArray(); } }
        public void Open() { if (FailOpen) throw new IOException("simulated unavailable serial port"); }
        public void Inject(byte[] bytes)
        {
            for (int offset = 0; offset < bytes.Length; offset += 4096)
            {
                byte[] part = new byte[Math.Min(4096, bytes.Length - offset)];
                Buffer.BlockCopy(bytes, offset, part, 0, part.Length);
                _input.Add(part);
            }
        }
        public void Fault() { _input.Add(new IOException("simulated serial unplug")); }
        public int Read(byte[] buffer, int offset, int count)
        {
            object value;
            if (!_input.TryTake(out value, 100)) throw new TimeoutException();
            Exception error = value as Exception;
            if (error != null) throw error;
            byte[] bytes = (byte[])value;
            if (bytes.Length > count) throw new InvalidOperationException("test transport chunk exceeds read buffer");
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
            return bytes.Length;
        }
        public void Write(byte[] buffer, int offset, int count)
        {
            int active = Interlocked.Increment(ref _activeWrites);
            MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, active);
            try
            {
                if (WriteDelay > 0) Thread.Sleep(WriteDelay);
                lock (_written)
                    for (int i = 0; i < count; i++) _written.Add(buffer[offset + i]);
            }
            finally { Interlocked.Decrement(ref _activeWrites); }
        }
        public void Dispose() { Disposed = true; }
    }

    internal sealed class TestChannel : IDisposable
    {
        public readonly MemorySerial Serial;
        public readonly BridgeService Service;
        public readonly int Port;
        private Task _stop;
        public TestChannel(int port = 0)
        {
            Port = port == 0 ? TestRunner.FreePort() : port;
            Serial = new MemorySerial();
            Service = new BridgeService(delegate(string message) { }, delegate(BridgeOptions options) { return Serial; });
            Service.Start(TestRunner.Options(Port));
        }
        public async Task<TcpClient> Connect(int receiveBuffer = 0)
        {
            TcpClient client = new TcpClient();
            client.NoDelay = true;
            if (receiveBuffer > 0) client.ReceiveBufferSize = receiveBuffer;
            await client.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);
            return client;
        }
        public Task Stop()
        {
            if (_stop == null) _stop = Service.StopAsync();
            return TestRunner.Deadline(_stop, 5000, "channel stop");
        }
        public void Dispose() { Stop().GetAwaiter().GetResult(); }
    }

    internal static class TestRunner
    {
        private static int _passed;
        private static string _results;
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            _results = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "results");
            Directory.CreateDirectory(_results);
            try
            {
                if (Array.IndexOf(args, "--ui-only") < 0) CoreTests().GetAwaiter().GetResult();
                UiTests();
                Console.WriteLine("PASS ALL: " + _passed + " behavioral scenarios; real TCP sockets, isolated serial transport and GUI.");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL: " + error);
                return 1;
            }
        }

        public static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        public static BridgeOptions Options(int port)
        {
            return new BridgeOptions { PortName = "SIM", BaudRate = 115200, DataBits = 8,
                Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.One,
                ListenAddress = IPAddress.Loopback, ListenPort = port };
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        private static void Same(byte[] expected, byte[] actual, string message)
        {
            Check(expected.SequenceEqual(actual), message + " (" + expected.Length + " vs " + actual.Length + ")");
        }
        public static async Task Deadline(Task task, int milliseconds, string message)
        {
            if (await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false) != task)
                throw new TimeoutException(message);
            await task.ConfigureAwait(false);
        }
        private static async Task Wait(Func<bool> condition, string message, int milliseconds = 5000)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException(message);
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
        private static async Task<byte[]> Read(TcpClient client, int length)
        {
            byte[] result = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                Task<int> reading = client.GetStream().ReadAsync(result, offset, length - offset);
                await Deadline(reading, 8000, "TCP receive " + offset + "/" + length).ConfigureAwait(false);
                int count = reading.Result;
                if (count == 0) throw new IOException("unexpected TCP EOF at " + offset + "/" + length);
                offset += count;
            }
            return result;
        }
        private static byte[] Bytes(string value) { return Encoding.UTF8.GetBytes(value); }
        private static async Task Scenario(string name, Func<Task> run)
        {
            await run().ConfigureAwait(false);
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private static async Task CoreTests()
        {
            await Scenario("4 clients: binary broadcast, concurrent writes and byte counters", async delegate
            {
                using (TestChannel channel = new TestChannel())
                {
                    List<TcpClient> clients = new List<TcpClient>();
                    try
                    {
                        for (int i = 0; i < 4; i++) clients.Add(await channel.Connect());
                        await Wait(delegate { return channel.Service.Snapshot().ClientAddresses.Length == 4; }, "four clients admitted");
                        byte[] binary = Enumerable.Range(0, 2048).Select(delegate(int i) { return (byte)i; }).ToArray();
                        Task<byte[]>[] reads = clients.Select(delegate(TcpClient client) { return Read(client, binary.Length); }).ToArray();
                        channel.Serial.Inject(binary);
                        foreach (byte[] received in await Task.WhenAll(reads)) Same(binary, received, "binary broadcast mismatch");
                        channel.Serial.WriteDelay = 20;
                        byte[][] messages = Enumerable.Range(0, 4).Select(delegate(int n) { return Enumerable.Repeat((byte)(n + 100), 256).ToArray(); }).ToArray();
                        await Task.WhenAll(clients.Select(delegate(TcpClient client, int index) { return client.GetStream().WriteAsync(messages[index], 0, messages[index].Length); }));
                        await Wait(delegate { return channel.Serial.WrittenCount == 1024; }, "all concurrent writes delivered");
                        byte[] expected = messages.SelectMany(delegate(byte[] b) { return b; }).OrderBy(delegate(byte b) { return b; }).ToArray();
                        Same(expected, channel.Serial.Written.OrderBy(delegate(byte b) { return b; }).ToArray(), "concurrent write data corruption");
                        Check(channel.Serial.MaxConcurrentWrites == 1, "serial writes overlap");
                        await Wait(delegate { return channel.Service.Snapshot().TcpSent == binary.Length * 4; }, "broadcast counters");
                        BridgeSnapshot snapshot = channel.Service.Snapshot();
                        Check(snapshot.SerialReceived == binary.Length && snapshot.SerialSent == 1024 && snapshot.TcpReceived == 1024, "incorrect byte counters");
                    }
                    finally { foreach (TcpClient client in clients) client.Close(); }
                }
            });
            await Scenario("2 channels: routing isolation and serial-unplug fault isolation", async delegate
            {
                using (TestChannel a = new TestChannel())
                using (TestChannel b = new TestChannel())
                using (TcpClient ca = await a.Connect())
                using (TcpClient cb = await b.Connect())
                {
                    await Wait(delegate { return a.Service.Snapshot().ClientAddresses.Length == 1 && b.Service.Snapshot().ClientAddresses.Length == 1; }, "both channels connected");
                    a.Serial.Inject(Bytes("channel A")); b.Serial.Inject(Bytes("channel B"));
                    Same(Bytes("channel A"), await Read(ca, 9), "channel A route");
                    Same(Bytes("channel B"), await Read(cb, 9), "channel B route");
                    a.Serial.Fault();
                    await Wait(delegate { return !a.Service.Snapshot().Running; }, "serial failure stops affected channel");
                    await a.Stop();
                    b.Serial.Inject(Bytes("still running"));
                    Same(Bytes("still running"), await Read(cb, 13), "second channel stopped by first channel failure");
                    Check(b.Service.Snapshot().Running && a.Serial.Disposed, "fault cleanup or isolation failed");
                }
            });
            await Scenario("40 disconnect/reconnect cycles; disconnected serial bytes are not replayed", async delegate
            {
                using (TestChannel channel = new TestChannel())
                {
                    channel.Serial.Inject(Bytes("discard me"));
                    await Wait(delegate { return channel.Service.Snapshot().SerialReceived == 10; }, "discard pre-connect input");
                    for (int i = 0; i < 40; i++)
                    {
                        using (TcpClient client = await channel.Connect())
                        {
                            await Wait(delegate { return channel.Service.Snapshot().ClientAddresses.Length == 1; }, "reconnect admitted");
                            byte[] value = BitConverter.GetBytes(i);
                            channel.Serial.Inject(value);
                            Same(value, await Read(client, value.Length), "reconnect data or history replay");
                        }
                        await Wait(delegate { return channel.Service.Snapshot().ClientAddresses.Length == 0; }, "disconnected client removed");
                    }
                }
            });
            await Scenario("slow client is dropped; fast client receives complete 2 MiB broadcast", async delegate
            {
                using (TestChannel channel = new TestChannel())
                using (TcpClient slow = await channel.Connect(4096))
                using (TcpClient fast = await channel.Connect())
                {
                    await Wait(delegate { return channel.Service.Snapshot().ClientAddresses.Length == 2; }, "slow/fast clients admitted");
                    object gate = Field<object>(channel.Service, "_clientGate");
                    lock (gate)
                    {
                        System.Collections.IEnumerable list = Field<System.Collections.IEnumerable>(channel.Service, "_clients");
                        foreach (object connection in list)
                        {
                            TcpClient server = (TcpClient)connection.GetType().GetField("Client").GetValue(connection);
                            if (server.Client.RemoteEndPoint.ToString() == slow.Client.LocalEndPoint.ToString()) server.SendBufferSize = 4096;
                        }
                    }
                    byte[] all = Enumerable.Range(0, 2 * 1024 * 1024).Select(delegate(int i) { return (byte)(i * 31); }).ToArray();
                    Task<byte[]> reading = Read(fast, all.Length);
                    for (int offset = 0; offset < all.Length; offset += 4096)
                    {
                        byte[] part = new byte[4096]; Buffer.BlockCopy(all, offset, part, 0, part.Length);
                        channel.Serial.Inject(part);
                        await Task.Delay(2);
                    }
                    Same(all, await reading, "fast client lost bytes");
                    await Wait(delegate { return channel.Service.Snapshot().ClientAddresses.Length == 1; }, "slow client not dropped", 6000);
                    Check(channel.Service.Snapshot().Running, "slow client stopped service");
                    await fast.GetStream().WriteAsync(new byte[] { 42 }, 0, 1);
                    await Wait(delegate { return channel.Serial.WrittenCount == 1; }, "fast client cannot write after slow client dropped");
                }
            });
            await Scenario("stop cancels queued writers and releases the listener/serial port", async delegate
            {
                TestChannel channel = new TestChannel();
                TcpClient a = await channel.Connect(), b = await channel.Connect();
                try
                {
                    channel.Serial.WriteDelay = 700;
                    await a.GetStream().WriteAsync(Bytes("first"), 0, 5);
                    await Wait(delegate { return channel.Serial.ActiveWrites == 1; }, "first writer active");
                    await b.GetStream().WriteAsync(Bytes("second"), 0, 6);
                    Stopwatch timer = Stopwatch.StartNew();
                    await channel.Stop();
                    Check(timer.ElapsedMilliseconds < 2500 && channel.Serial.Disposed, "stop hung or serial leaked");
                    using (TestChannel replacement = new TestChannel(channel.Port))
                        Check(replacement.Service.Snapshot().Running, "listener cannot be reused after stop");
                }
                finally { a.Close(); b.Close(); channel.Dispose(); }
            });
            await Scenario("TCP bind failure and unavailable serial port clean up resources", async delegate
            {
                using (TestChannel owner = new TestChannel())
                {
                    MemorySerial serial = new MemorySerial();
                    BridgeService conflict = new BridgeService(delegate(string s) { }, delegate(BridgeOptions o) { return serial; });
                    bool failed = false;
                    try { conflict.Start(Options(owner.Port)); } catch (SocketException) { failed = true; }
                    Check(failed && serial.Disposed, "bind failure leaked serial port");
                }
                MemorySerial broken = new MemorySerial { FailOpen = true };
                BridgeService unavailable = new BridgeService(delegate(string s) { }, delegate(BridgeOptions o) { return broken; });
                bool openFailed = false;
                try { unavailable.Start(Options(FreePort())); } catch (IOException) { openFailed = true; }
                Check(openFailed && broken.Disposed, "serial open failure leaked resources");
                await Task.FromResult(0);
            });
            await Scenario("connect/stop race does not leave client tasks or bound ports", async delegate
            {
                using (TestChannel channel = new TestChannel())
                {
                    List<TcpClient> clients = new List<TcpClient>();
                    List<Task> attempts = new List<Task>();
                    for (int i = 0; i < 30; i++)
                    {
                        TcpClient client = new TcpClient(); clients.Add(client);
                        attempts.Add(Task.Run(async delegate { try { await client.ConnectAsync(IPAddress.Loopback, channel.Port); } catch (SocketException) { } }));
                    }
                    await Task.Delay(10);
                    await channel.Stop();
                    foreach (TcpClient client in clients) client.Close();
                    await Deadline(Task.WhenAll(attempts), 5000, "connect attempts stuck after stop");
                    Check(!channel.Service.Snapshot().Running && channel.Serial.Disposed, "race cleanup failed");
                }
            });
        }

        private static T Field<T>(object value, string name)
        {
            return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        }
        private static List<ChannelPanel> Channels(MainForm form) { return Field<List<ChannelPanel>>(form, "_channels"); }
        private static bool Idle(MainForm form)
        {
            return !Field<bool>(form, "_batchBusy") && Channels(form).All(delegate(ChannelPanel c) { return !c.IsBusy; });
        }
        private static void SetChannel(ChannelPanel channel, string serial, int port)
        {
            Field<ComboBox>(channel, "_ports").Text = serial;
            Field<NumericUpDown>(channel, "_port").Value = port;
            Field<ComboBox>(channel, "_address").Text = "127.0.0.1";
        }
        private static void UiTests()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string folder = Path.Combine(Path.GetTempPath(), "com2tcp-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string settings = Path.Combine(folder, "settings.xml");
            int portA = FreePort(), portB = FreePort();
            File.WriteAllText(settings, "<SavedSettings><PortName>SIM_A</PortName><BaudRate>1500000</BaudRate><DataBits>8</DataBits><Parity>None</Parity><StopBits>One</StopBits><ListenAddress>127.0.0.1</ListenAddress><ListenPort>" + portA + "</ListenPort></SavedSettings>");
            ConcurrentDictionary<string, MemorySerial> serials = new ConcurrentDictionary<string, MemorySerial>();
            List<MemorySerial> allSerials = new List<MemorySerial>();
            Func<Action<string>, BridgeService> factory = delegate(Action<string> log)
            {
                return new BridgeService(log, delegate(BridgeOptions options)
                {
                    MemorySerial serial = new MemorySerial();
                    serials[options.PortName] = serial;
                    lock (allSerials) allSerials.Add(serial);
                    return serial;
                });
            };
            Exception failure = null;
            ApplicationContext context = new ApplicationContext();
            MainForm form = new MainForm(false, settings, factory);
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Shown += async delegate
            {
                try
                {
                    await Task.Delay(30); // Let native child-edit initialization settle.
                    Check(Channels(form).Count == 1 && Channels(form)[0].SerialPortName == "SIM_A" && Channels(form)[0].GetSettings().BaudRate == "1500000", "legacy settings migration failed");
                    Check(form.BackColor == PebrelUi.PebrelTheme.Panel && Field<TabControl>(form, "_tabs").Alignment == TabAlignment.Left, "shared Pebrel theme and navigation missing");
                    ChannelPanel appearance = Channels(form)[0];
                    foreach (string name in new[] { "_ports", "_baud", "_dataBits", "_parity", "_stopBits", "_address" })
                    {
                        ComboBox combo = Field<ComboBox>(appearance, name);
                        Check(combo.BackColor == PebrelUi.PebrelTheme.Input && combo.ForeColor == PebrelUi.PebrelTheme.Ink, "field theme: " + name);
                        Check(combo.Parent.ClientRectangle.Contains(combo.Bounds), "field clipped: " + name);
                        if (combo.DropDownStyle == ComboBoxStyle.DropDown)
                            Check(combo.SelectionLength == 0, "inactive field retains selection: " + name);
                    }
                    Check(Field<TextBox>(appearance, "_logs").BorderStyle == BorderStyle.None, "log kept native white border");
                    Field<Button>(form, "_add").PerformClick();
                    Check(Channels(form).Count == 2, "add channel button failed");
                    SetChannel(Channels(form)[1], "SIM_B", portB);
                    Field<Button>(form, "_startAll").PerformClick();
                    await Wait(delegate { return Idle(form) && Channels(form).All(delegate(ChannelPanel c) { return c.IsRunning; }); }, "GUI start all");
                    Check(!Field<Button>(form, "_remove").Enabled, "running channel can be removed");
                    using (TcpClient a = new TcpClient())
                    using (TcpClient a2 = new TcpClient())
                    using (TcpClient b = new TcpClient())
                    {
                        await a.ConnectAsync(IPAddress.Loopback, portA); await a2.ConnectAsync(IPAddress.Loopback, portA); await b.ConnectAsync(IPAddress.Loopback, portB);
                        await Task.Delay(100);
                        serials["SIM_A"].Inject(Bytes("gui A")); serials["SIM_B"].Inject(Bytes("gui B"));
                        Same(Bytes("gui A"), await Read(a, 5), "GUI first channel");
                        Same(Bytes("gui A"), await Read(a2, 5), "GUI multi client");
                        Same(Bytes("gui B"), await Read(b, 5), "GUI second channel");
                    }
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(_results, "ui-validation.png"));
                    }
                    Size normalSize = form.Size;
                    form.Size = form.MinimumSize;
                    form.PerformLayout();
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(_results, "pebrel-style-minimum.png"));
                    }
                    form.Size = normalSize;
                    Field<Button>(form, "_stopAll").PerformClick();
                    await Wait(delegate { return Idle(form) && Channels(form).All(delegate(ChannelPanel c) { return !c.IsRunning; }); }, "GUI stop all");
                    SetChannel(Channels(form)[1], "SIM_B", portA);
                    Field<Button>(form, "_startAll").PerformClick();
                    await Wait(delegate { return Idle(form) && Channels(form)[0].IsRunning; }, "GUI conflicting port completes");
                    Check(!Channels(form)[1].IsRunning, "GUI allowed duplicate listening endpoint");
                    Field<Button>(form, "_stopAll").PerformClick();
                    await Wait(delegate { return Idle(form) && !Channels(form)[0].IsRunning; }, "stop after port conflict");
                    SetChannel(Channels(form)[1], "SIM_A", portB);
                    Field<Button>(form, "_startAll").PerformClick();
                    await Wait(delegate { return Idle(form) && Channels(form)[0].IsRunning; }, "GUI duplicate serial completes");
                    Check(!Channels(form)[1].IsRunning, "GUI allowed duplicate serial");
                    Field<Button>(form, "_stopAll").PerformClick();
                    await Wait(delegate { return Idle(form) && !Channels(form)[0].IsRunning; }, "stop after serial conflict");
                    Field<TabControl>(form, "_tabs").SelectedIndex = 1;
                    Field<Button>(form, "_remove").PerformClick();
                    Check(Channels(form).Count == 1, "remove stopped channel failed");
                    Field<Button>(form, "_add").PerformClick();
                    SetChannel(Channels(form)[1], "SIM_B", portB);
                    Field<Button>(form, "_startAll").PerformClick();
                    await Wait(delegate { return Idle(form) && Channels(form).All(delegate(ChannelPanel c) { return c.IsRunning; }); }, "GUI restart all");
                    await Task.Delay(750);
                    XDocument config = XDocument.Load(settings);
                    Check(config.Root.Name.LocalName == "SavedConfiguration" && config.Descendants("SavedSettings").Count() == 2, "multi channel settings not persisted");
                    Check(File.Exists(settings + ".single.bak"), "legacy configuration backup missing");
                    form.Close();
                    await Wait(delegate { return form.IsDisposed; }, "GUI close with running channels");
                    lock (allSerials) Check(allSerials.All(delegate(MemorySerial serial) { return serial.Disposed; }), "GUI close leaked serial channels");
                    _passed++;
                    Console.WriteLine("PASS GUI: legacy migration, add/remove, two-channel start/stop/restart, broadcast, conflicts, persistence and close cleanup");
                }
                catch (Exception error) { failure = error; }
                try
                {
                    if (!form.IsDisposed)
                    {
                        await Wait(delegate { return Idle(form); }, "GUI error cleanup idle");
                        await Task.WhenAll(Channels(form).Select(delegate(ChannelPanel c) { return c.StopAsync(); }));
                    }
                }
                catch (Exception cleanupError) { if (failure == null) failure = cleanupError; }
                finally
                {
                    form.Dispose();
                    context.ExitThread();
                }
            };
            form.Show();
            Application.Run(context);
            if (failure != null) throw new Exception("GUI integration test failed", failure);
        }
    }
}
