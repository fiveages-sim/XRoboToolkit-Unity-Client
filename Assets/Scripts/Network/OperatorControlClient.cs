using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using XRoboToolkit.Network;

using NetTcpClient = System.Net.Sockets.TcpClient;

/// <summary>
/// Length-framed Remote Vision control client implemented in managed C#.
///
/// The vendor AAR client reads the four-byte TCP length prefix with one Read
/// call and shares a socket across reconnect threads. This client uses
/// read-exact semantics and a run generation so an old thread cannot close or
/// mutate a newer connection.
/// </summary>
public sealed class OperatorControlClient : IDisposable
{
    private const int ConnectTimeoutMs = 2000;
    private const int IoTimeoutMs = 1500;
    // Only for a stalled mid-frame body read. Waiting for the next length prefix
    // must NOT idle-out: headset↔headset source is often silent after OPEN_CAMERA
    // (no AUDIO_CONFIG/PING). A 5s idle close caused Java TcpServer EOF, then
    // Listen renegotiated OPEN_CAMERA in a loop and the video never stayed up.
    private const int PartialReadIdleTimeoutMs = 15000;
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    private readonly object _clientLock = new object();
    private readonly object _sendLock = new object();
    private Thread _thread;
    private NetTcpClient _client;
    private volatile bool _stopRequested = true;
    private volatile bool _connected;
    private int _runId;

    public event Action Connected;
    public event Action Disconnected;
    public event Action<byte[]> DataReceived;
    public event Action<string> Error;

    public bool IsConnected => _connected;
    public bool IsRunning => !_stopRequested;

    public void Connect(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Operator control host must not be empty.", nameof(host));
        }

        Disconnect(false);
        int runId = Interlocked.Increment(ref _runId);
        _stopRequested = false;
        _connected = false;
        _thread = new Thread(() => Run(host.Trim(), port, runId));
        _thread.IsBackground = true;
        _thread.Name = "OperatorControlClient";
        _thread.Start();
    }

    public bool SendCommand(string command, byte[] data = null)
    {
        if (!_connected || string.IsNullOrEmpty(command))
        {
            return false;
        }

        byte[] body = NetworkDataProtocolSerializer.Serialize(
            new NetworkDataProtocol(command, data ?? new byte[0]));
        byte[] packet = new byte[sizeof(int) + body.Length];
        WriteUInt32BigEndian(packet, 0, unchecked((uint)body.Length));
        Buffer.BlockCopy(body, 0, packet, sizeof(int), body.Length);

        lock (_sendLock)
        {
            NetTcpClient client;
            lock (_clientLock)
            {
                client = _client;
            }
            if (client == null || !client.Connected)
            {
                return false;
            }

            try
            {
                NetworkStream stream = client.GetStream();
                stream.Write(packet, 0, packet.Length);
                stream.Flush();
                return true;
            }
            catch (Exception exception)
            {
                Error?.Invoke($"Operator control send failed: {exception.Message}");
                CloseClient(client);
                return false;
            }
        }
    }

    /// <summary>
    /// Best-effort flush of the control socket after CLOSE_CAMERA so teardown
    /// is not lost when DisconnectSilently closes the TCP connection next.
    /// </summary>
    public void FlushSend()
    {
        lock (_sendLock)
        {
            NetTcpClient client;
            lock (_clientLock)
            {
                client = _client;
            }
            if (client == null || !client.Connected)
            {
                return;
            }

            try
            {
                client.GetStream().Flush();
            }
            catch (Exception)
            {
                // Teardown path: ignore flush errors.
            }
        }
    }

    public void Disconnect()
    {
        Disconnect(true);
    }

    public void DisconnectSilently()
    {
        Disconnect(false);
    }

    private void Disconnect(bool notify)
    {
        bool wasRunning = !_stopRequested || _connected || _thread != null;
        _stopRequested = true;
        Interlocked.Increment(ref _runId);
        CloseCurrentClient();
        if (_thread != null && _thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(500);
        }
        _thread = null;
        _connected = false;
        if (notify && wasRunning)
        {
            Disconnected?.Invoke();
        }
    }

    private void Run(string host, int port, int runId)
    {
        NetTcpClient tcp = null;
        try
        {
            tcp = ConnectWithTimeout(host, port, ConnectTimeoutMs);
            tcp.NoDelay = true;
            tcp.SendTimeout = IoTimeoutMs;
            tcp.ReceiveTimeout = IoTimeoutMs;
            tcp.SendBufferSize = 4096;
            tcp.ReceiveBufferSize = 4096;

            lock (_clientLock)
            {
                if (!IsRunActive(runId))
                {
                    tcp.Close();
                    return;
                }
                _client = tcp;
                _connected = true;
            }

            if (!IsRunActive(runId))
            {
                return;
            }
            Connected?.Invoke();
            NetworkStream stream = tcp.GetStream();
            byte[] lengthBuffer = new byte[sizeof(int)];
            while (IsRunActive(runId))
            {
                // Indefinite wait for the next framed message — source may be silent.
                if (!ReadExactly(stream, lengthBuffer, lengthBuffer.Length, runId,
                        allowIndefiniteWait: true))
                {
                    break;
                }
                int bodyLength = ReadInt32BigEndian(lengthBuffer, 0);
                if (bodyLength <= 0 || bodyLength > MaxBodyBytes)
                {
                    throw new InvalidDataException(
                        $"Invalid operator control body length: {bodyLength}");
                }
                byte[] body = new byte[bodyLength];
                if (!ReadExactly(stream, body, bodyLength, runId, allowIndefiniteWait: false))
                {
                    break;
                }
                DataReceived?.Invoke(body);
            }
        }
        catch (Exception exception)
        {
            if (IsRunActive(runId))
            {
                Error?.Invoke($"Operator control connection failed: {exception.Message}");
            }
        }
        finally
        {
            bool notifyDisconnected = false;
            lock (_clientLock)
            {
                if (_client == tcp)
                {
                    _client = null;
                    if (IsRunActive(runId))
                    {
                        _connected = false;
                        notifyDisconnected = true;
                    }
                }
            }
            if (tcp != null)
            {
                tcp.Close();
            }
            if (notifyDisconnected && IsRunActive(runId))
            {
                Disconnected?.Invoke();
            }
        }
    }

    private bool ReadExactly(
        NetworkStream stream,
        byte[] buffer,
        int count,
        int runId,
        bool allowIndefiniteWait)
    {
        int offset = 0;
        long deadlineTicks = allowIndefiniteWait
            ? long.MaxValue
            : DateTime.UtcNow.AddMilliseconds(PartialReadIdleTimeoutMs).Ticks;
        while (offset < count && IsRunActive(runId))
        {
            try
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    return false;
                }
                offset += read;
                if (!allowIndefiniteWait)
                {
                    deadlineTicks = DateTime.UtcNow.AddMilliseconds(PartialReadIdleTimeoutMs).Ticks;
                }
            }
            catch (IOException exception) when (IsSocketTimeout(exception))
            {
                if (!allowIndefiniteWait && DateTime.UtcNow.Ticks >= deadlineTicks)
                {
                    throw new TimeoutException("operator control partial-read idle timeout");
                }
                continue;
            }
        }
        return offset == count;
    }

    private static bool IsSocketTimeout(IOException exception)
    {
        return exception.InnerException is SocketException socketException &&
               socketException.SocketErrorCode == SocketError.TimedOut;
    }

    private bool IsRunActive(int runId)
    {
        return !_stopRequested && runId == Interlocked.CompareExchange(ref _runId, 0, 0);
    }

    private static NetTcpClient ConnectWithTimeout(string host, int port, int timeoutMs)
    {
        NetTcpClient tcp = new NetTcpClient();
        IAsyncResult result = tcp.BeginConnect(host, port, null, null);
        try
        {
            bool connected = result.AsyncWaitHandle.WaitOne(timeoutMs);
            if (!connected)
            {
                throw new TimeoutException($"connect timeout to {host}:{port}");
            }
            tcp.EndConnect(result);
            return tcp;
        }
        catch
        {
            tcp.Close();
            throw;
        }
        finally
        {
            result.AsyncWaitHandle.Close();
        }
    }

    private void CloseCurrentClient()
    {
        NetTcpClient client;
        lock (_clientLock)
        {
            client = _client;
            _client = null;
        }
        CloseClient(client);
    }

    private static void CloseClient(NetTcpClient client)
    {
        if (client == null)
        {
            return;
        }
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception)
        {
            // Best effort: Close below is the authoritative teardown.
        }
        client.Close();
    }

    private static int ReadInt32BigEndian(byte[] buffer, int offset)
    {
        return (buffer[offset] << 24) |
               (buffer[offset + 1] << 16) |
               (buffer[offset + 2] << 8) |
               buffer[offset + 3];
    }

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    public void Dispose()
    {
        Disconnect(false);
        Connected = null;
        Disconnected = null;
        DataReceived = null;
        Error = null;
    }
}
