using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Com2Tcp
{
    internal interface ISerialTransport : IDisposable
    {
        void Open();
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
    }

    internal sealed class SerialPortTransport : ISerialTransport
    {
        private readonly SerialPort _port;

        public SerialPortTransport(BridgeOptions options)
        {
            _port = new SerialPort(options.PortName, options.BaudRate,
                options.Parity, options.DataBits, options.StopBits);
            _port.Handshake = Handshake.None;
            _port.DtrEnable = false;
            _port.RtsEnable = false;
            _port.ReadTimeout = 250;
            _port.WriteTimeout = 2000;
        }

        public void Open() { _port.Open(); }
        public int Read(byte[] buffer, int offset, int count) { return _port.Read(buffer, offset, count); }
        public void Write(byte[] buffer, int offset, int count) { _port.Write(buffer, offset, count); }
        public void Dispose() { _port.Dispose(); }
    }

    internal sealed class BridgeOptions
    {
        public string PortName;
        public int BaudRate;
        public int DataBits;
        public Parity Parity;
        public StopBits StopBits;
        public IPAddress ListenAddress;
        public int ListenPort;
    }

    internal sealed class BridgeSnapshot
    {
        public bool Running;
        public string[] ClientAddresses = new string[0];
        public long SerialReceived;
        public long SerialSent;
        public long TcpReceived;
        public long TcpSent;
    }

    // A service instance represents one serial channel and one start/stop session.
    internal sealed class BridgeService
    {
        private sealed class Connection : IDisposable
        {
            private const int MaximumQueuedBytes = 256 * 1024;
            private readonly object _queueGate = new object();
            private readonly Queue<byte[]> _outbound = new Queue<byte[]>();
            private int _queuedBytes;
            private bool _closed;
            public readonly TcpClient Client;
            public readonly NetworkStream Stream;
            public readonly string Address;
            public readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
            public readonly SemaphoreSlim OutputReady = new SemaphoreSlim(0);

            public Connection(TcpClient client)
            {
                Client = client;
                Address = client.Client.RemoteEndPoint.ToString();
                Stream = client.GetStream();
            }

            public bool TryEnqueue(byte[] payload)
            {
                lock (_queueGate)
                {
                    if (_closed || _queuedBytes + payload.Length > MaximumQueuedBytes)
                        return false;
                    _outbound.Enqueue(payload);
                    _queuedBytes += payload.Length;
                    OutputReady.Release();
                    return true;
                }
            }

            public byte[] Dequeue()
            {
                lock (_queueGate)
                {
                    if (_outbound.Count == 0)
                        return null;
                    byte[] payload = _outbound.Dequeue();
                    _queuedBytes -= payload.Length;
                    return payload;
                }
            }

            public void Close()
            {
                lock (_queueGate)
                {
                    if (_closed)
                        return;
                    _closed = true;
                    _outbound.Clear();
                    _queuedBytes = 0;
                    Lifetime.Cancel();
                    Client.Close();
                }
            }

            public void Dispose()
            {
                Close();
                lock (_queueGate)
                {
                    OutputReady.Dispose();
                    Lifetime.Dispose();
                }
            }
        }

        private readonly Action<string> _log;
        private readonly object _clientGate = new object();
        private readonly SemaphoreSlim _serialWriteGate = new SemaphoreSlim(1, 1);
        private readonly List<Connection> _clients = new List<Connection>();
        private readonly List<Task> _clientTasks = new List<Task>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Func<BridgeOptions, ISerialTransport> _createSerial;
        private ISerialTransport _serial;
        private TcpListener _listener;
        private Task _acceptTask;
        private Task _serialTask;
        private int _started;
        private int _stopping;
        private long _serialReceived;
        private long _serialSent;
        private long _tcpReceived;
        private long _tcpSent;

        public BridgeService(Action<string> log)
            : this(log, delegate(BridgeOptions options) { return new SerialPortTransport(options); })
        {
        }

        internal BridgeService(Action<string> log, Func<BridgeOptions, ISerialTransport> createSerial)
        {
            _log = log;
            _createSerial = createSerial;
        }

        public void Start(BridgeOptions options)
        {
            try
            {
                _serial = _createSerial(options);
                _serial.Open();

                _listener = new TcpListener(options.ListenAddress, options.ListenPort);
                _listener.Start(128);
                Interlocked.Exchange(ref _started, 1);
                _serialTask = Task.Factory.StartNew(SerialReadLoop,
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _acceptTask = AcceptLoopAsync();
                _log(string.Format("已启动：{0}，{1} bps；TCP {2}:{3}；支持多客户端广播",
                    options.PortName, options.BaudRate, options.ListenAddress, options.ListenPort));
            }
            catch
            {
                RequestStop();
                try
                {
                    if (_serial != null)
                        _serial.Dispose();
                }
                finally
                {
                    _serialWriteGate.Dispose();
                    _shutdown.Dispose();
                }
                throw;
            }
        }

        public BridgeSnapshot Snapshot()
        {
            List<string> addresses = new List<string>();
            lock (_clientGate)
                foreach (Connection client in _clients)
                    addresses.Add(client.Address);

            return new BridgeSnapshot
            {
                Running = Volatile.Read(ref _started) == 1 && Volatile.Read(ref _stopping) == 0,
                ClientAddresses = addresses.ToArray(),
                SerialReceived = Interlocked.Read(ref _serialReceived),
                SerialSent = Interlocked.Read(ref _serialSent),
                TcpReceived = Interlocked.Read(ref _tcpReceived),
                TcpSent = Interlocked.Read(ref _tcpSent)
            };
        }

        public async Task StopAsync()
        {
            RequestStop();
            try
            {
                // Stop accepting before collecting all admitted clients and their workers.
                if (_acceptTask != null)
                    await _acceptTask.ConfigureAwait(false);
                List<Task> pending;
                lock (_clientGate)
                    pending = new List<Task>(_clientTasks);
                if (_serialTask != null)
                    pending.Add(_serialTask);
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            finally
            {
                // Finite serial timeouts let I/O finish before the port is disposed.
                try
                {
                    if (_serial != null)
                        _serial.Dispose();
                }
                finally
                {
                    _serialWriteGate.Dispose();
                    _shutdown.Dispose();
                }
            }
            _log("服务已停止，全部客户端已断开，串口已释放。");
        }

        private void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
                return;
            _shutdown.Cancel();
            if (_listener != null)
                _listener.Stop();
            foreach (Connection connection in Clients())
                Disconnect(connection);
        }

        private Connection[] Clients()
        {
            lock (_clientGate)
                return _clients.ToArray();
        }

        private void Fail(string message, Exception error)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                _log(message + "：" + error.Message + "；本通道转发已停止。");
                RequestStop();
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Connection connection;
                    try
                    {
                        client.NoDelay = true;
                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        connection = new Connection(client);
                    }
                    catch (Exception error)
                    {
                        client.Close();
                        if (!_shutdown.IsCancellationRequested)
                            _log("客户端初始化失败：" + error.Message);
                        continue;
                    }

                    bool admitted;
                    lock (_clientGate)
                    {
                        admitted = !_shutdown.IsCancellationRequested;
                        if (admitted)
                            _clients.Add(connection);
                    }
                    if (!admitted)
                    {
                        connection.Dispose();
                        continue;
                    }

                    _log("客户端已连接：" + connection.Address);
                    Task clientTask = Task.Run(delegate { return HandleClientAsync(connection); });
                    lock (_clientGate)
                    {
                        _clientTasks.RemoveAll(delegate(Task task) { return task.IsCompleted; });
                        _clientTasks.Add(clientTask);
                    }
                }
            }
            catch (Exception error)
            {
                Fail("TCP 监听失败", error);
            }
        }

        private async Task HandleClientAsync(Connection connection)
        {
            try
            {
                Task receive = Task.Run(delegate { return ReceiveLoopAsync(connection); });
                await Task.WhenAll(receive, SendLoopAsync(connection)).ConfigureAwait(false);
            }
            finally
            {
                Disconnect(connection);
                connection.Dispose();
            }
        }

        private async Task ReceiveLoopAsync(Connection connection)
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!_shutdown.IsCancellationRequested && !connection.Lifetime.IsCancellationRequested)
                {
                    int count = await connection.Stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    Interlocked.Add(ref _tcpReceived, count);
                    await _serialWriteGate.WaitAsync(connection.Lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        if (_shutdown.IsCancellationRequested || !IsActive(connection))
                            break;
                        try
                        {
                            // Serialize each received chunk; TCP streams have no command boundaries.
                            _serial.Write(buffer, 0, count);
                            Interlocked.Add(ref _serialSent, count);
                        }
                        catch (Exception error)
                        {
                            Fail("串口写入失败", error);
                            break;
                        }
                    }
                    finally
                    {
                        _serialWriteGate.Release();
                    }
                }
            }
            catch (Exception error)
            {
                if (!_shutdown.IsCancellationRequested && IsActive(connection))
                    _log("TCP 接收中断（" + connection.Address + "）：" + error.Message);
            }
            finally
            {
                Disconnect(connection);
            }
        }

        private async Task SendLoopAsync(Connection connection)
        {
            try
            {
                while (!connection.Lifetime.IsCancellationRequested)
                {
                    await connection.OutputReady.WaitAsync(connection.Lifetime.Token).ConfigureAwait(false);
                    byte[] payload = connection.Dequeue();
                    if (payload == null)
                        continue;
                    Task write = connection.Stream.WriteAsync(payload, 0, payload.Length);
                    using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token))
                    {
                        Task delay = Task.Delay(2000, timeout.Token);
                        if (await Task.WhenAny(write, delay).ConfigureAwait(false) != write)
                            Disconnect(connection, "TCP 发送超时或连接已关闭");
                        timeout.Cancel();
                        // Closing the socket unblocks Framework NetworkStream I/O; observe its result.
                        await write.ConfigureAwait(false);
                    }
                    Interlocked.Add(ref _tcpSent, payload.Length);
                }
            }
            catch (Exception error)
            {
                if (!_shutdown.IsCancellationRequested && IsActive(connection))
                    _log("TCP 发送中断（" + connection.Address + "）：" + error.Message);
            }
            finally
            {
                Disconnect(connection);
            }
        }

        private void SerialReadLoop()
        {
            byte[] buffer = new byte[4096];
            while (!_shutdown.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = _serial.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception error)
                {
                    Fail("串口读取失败", error);
                    return;
                }
                if (count == 0)
                    continue;
                Interlocked.Add(ref _serialReceived, count);
                Connection[] clients = Clients();
                // Bytes received without clients are discarded, never replayed to later clients.
                if (clients.Length == 0)
                    continue;
                byte[] payload = new byte[count];
                Buffer.BlockCopy(buffer, 0, payload, 0, count);
                // Immutable payloads can be shared; every client has an independent bounded queue.
                foreach (Connection connection in clients)
                    if (!connection.TryEnqueue(payload))
                        Disconnect(connection, "客户端发送队列已满或连接已关闭");
            }
        }

        private bool IsActive(Connection connection)
        {
            lock (_clientGate)
                return _clients.Contains(connection);
        }

        private void Disconnect(Connection connection, string reason = null)
        {
            bool removed;
            lock (_clientGate)
                removed = _clients.Remove(connection);
            connection.Close();
            if (removed)
                _log("客户端已断开：" + connection.Address + (reason == null ? "" : "（" + reason + "）"));
        }
    }
}
