using System.Buffers;
using System.ComponentModel;
using System.Net.Sockets;
using System.Threading.Channels;
using Core.Interfaces;
using Core.Models;

namespace Infrastructure.Networking;

/// <summary>
/// TCP 客户端实现，提供连接、发送和接收数据的功能，并通过事件通知外部数据接收和连接状态的改变。
/// </summary>
public class TcpClientExample : ITcpClient, IDisposable
{
    private Socket? _tcpClient;
    private CancellationTokenSource? _receiveCts;
    private CancellationTokenSource? _sendCts;
    private Task? _receiveTask;
    private Task? _sendTask;

    private bool _isConnected;
    public bool IsConnected => _isConnected;

    private bool _disposed;

    public event EventHandler<string>? ConnectionStatusChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

    private readonly Lock _syncRoot = new();

    // 连接代际：每次 ConnectAsync 递增。接收循环只有在自己所属代际等于当前代际时，
    // 才允许修改全局连接状态（如触发 Disconnect），避免旧循环误关新连接（P0-3）。
    private int _connectionGeneration;

    // 有界单写发送队列：所有 SendAsync 调用方只负责入队，一个独占发送循环完整发送每一帧，
    // 消除多请求并发导致的 TCP 帧边界交错（P0-2）。队列容量形成背压。
    private readonly Channel<OutboundFrame> _sendChannel =
        Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;


    /// <summary>
    /// 连接到服务器并启动收发循环。
    /// </summary>
    public async Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
    {
        // 重连前清理半开连接，避免双 Socket（不发状态事件，避免误触发重连）。
        Disconnect();

        Socket clientSocket;
        CancellationTokenSource receiveCts;
        CancellationTokenSource sendCts;
        int generation;
        lock (_syncRoot)
        {
            clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            generation = ++_connectionGeneration;
            _tcpClient = clientSocket;
            receiveCts = _receiveCts = new CancellationTokenSource();
            sendCts = _sendCts = new CancellationTokenSource();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await clientSocket.ConnectAsync(endpoint.ServerIpAddress, endpoint.ServerPort, cts.Token);
            lock (_syncRoot)
            {
                _isConnected = true;
            }
            OnPropertyChanged(nameof(IsConnected));
            ConnectionStatusChanged?.Invoke(this, "Connected");

            // 收发循环各自捕获本次连接的代际与 CTS，任务保存以便 Dispose 时等待（不再 fire-and-forget）。
            _receiveTask = ReceiveDataAsync(receiveCts.Token, generation, clientSocket);
            _sendTask = SendLoopAsync(sendCts.Token);
        }
        catch (Exception)
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_tcpClient, clientSocket))
                {
                    _tcpClient = null;
                    _receiveCts = null;
                    _sendCts = null;
                }
            }
            receiveCts.Cancel();
            receiveCts.Dispose();
            sendCts.Cancel();
            sendCts.Dispose();
            clientSocket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 发送数据：复制到独立内存后入队，由独占发送循环完整发送。
    /// 队列已满时 WriteAsync 会等待，形成背压（P0-2）。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        lock (_syncRoot)
        {
            if (!_isConnected || _tcpClient is null)
                throw new InvalidOperationException("Not connected to server");
        }

        // 入队前复制数据到独立内存：调用方持有的 buffer 可能在入队后被重用/释放。
        var owned = data.ToArray();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new OutboundFrame(owned, tcs);

        try
        {
            await _sendChannel.Writer.WriteAsync(frame, token).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException("连接已关闭，无法发送", ex);
        }

        // 等待独占发送循环完成本帧，传播其异常。
        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 独占发送循环：从队列逐帧完整发送，保证帧边界原子性（P0-2）。
    /// </summary>
    private async Task SendLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var frame in _sendChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    Socket? socket;
                    lock (_syncRoot)
                    {
                        socket = _tcpClient;
                    }
                    if (socket is null)
                    {
                        frame.Tcs.TrySetException(new InvalidOperationException("连接已关闭"));
                        continue;
                    }

                    var data = frame.Data;
                    var totalSent = 0;
                    while (totalSent < data.Length)
                    {
                        var sent = await socket.SendAsync(data[totalSent..], SocketFlags.None, token).ConfigureAwait(false);
                        if (sent <= 0)
                            throw new SocketException((int)SocketError.ConnectionReset);
                        totalSent += sent;
                    }
                    frame.Tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    frame.Tcs.TrySetException(ex);
                    DrainSendChannel(ex);
                    Disconnect("Connection lost during send");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect 取消，正常退出。
        }
        catch (Exception ex)
        {
            DrainSendChannel(ex);
            Disconnect($"Send loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// 排空发送队列，将所有待发帧标记为失败。
    /// </summary>
    private void DrainSendChannel(Exception ex)
    {
        while (_sendChannel.Reader.TryRead(out var frame))
        {
            frame.Tcs.TrySetException(ex);
        }
    }

    /// <summary>
    /// 接收数据循环（接口兼容入口）。实际由 ConnectAsync 启动带代际的重载。
    /// </summary>
    public Task ReceiveDataAsync(CancellationToken token)
    {
        int generation;
        Socket? socket;
        lock (_syncRoot)
        {
            generation = _connectionGeneration;
            socket = _tcpClient;
        }
        if (socket is null) return Task.CompletedTask;
        return ReceiveDataAsync(token, generation, socket);
    }

    /// <summary>
    /// 接收数据循环。buffer 为局部变量，避免重连时新旧循环归还竞态（P0-3）。
    /// 只有当前代际的循环才允许触发 Disconnect，避免误关新连接。
    /// </summary>
    private async Task ReceiveDataAsync(CancellationToken token, int generation, Socket socket)
    {
        var buffer = _bufferPool.Rent(4096);
        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    if (IsCurrentGeneration(generation))
                        Disconnect("Graceful disconnect");
                    break;
                }

                var receivedMemory = buffer.AsMemory(0, bytesRead);
                OnDataChunkReceived?.Invoke(this, receivedMemory);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentGeneration(generation))
                Disconnect($"Receive error: {ex.Message}");
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    /// <summary>判断调用方所属的连接代际是否仍是当前代际。</summary>
    private bool IsCurrentGeneration(int generation)
    {
        return Volatile.Read(ref _connectionGeneration) == generation;
    }

    /// <summary>
    /// 断开与服务器的连接，取消收发循环并排空发送队列。
    /// </summary>
    public void Disconnect(string? reason = null)
    {
        bool shouldNotify;
        CancellationTokenSource? receiveCts;
        CancellationTokenSource? sendCts;
        lock (_syncRoot)
        {
            receiveCts = _receiveCts;
            _receiveCts = null;
            sendCts = _sendCts;
            _sendCts = null;

            if (!_isConnected && _tcpClient is null)
            {
                shouldNotify = false;
            }
            else
            {
                shouldNotify = _isConnected || !string.IsNullOrEmpty(reason);
                _isConnected = false;
                try
                {
                    _tcpClient?.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                finally
                {
                    _tcpClient?.Dispose();
                    _tcpClient = null;
                }
            }
        }

        // 在锁外取消，避免锁内长时间阻塞。
        try { receiveCts?.Cancel(); } catch { }
        try { sendCts?.Cancel(); } catch { }
        receiveCts?.Dispose();
        sendCts?.Dispose();

        // 排空发送队列，通知所有等待的 SendAsync 失败。
        DrainSendChannel(new InvalidOperationException("连接已断开"));

        OnPropertyChanged(nameof(IsConnected));

        if (shouldNotify && !string.IsNullOrEmpty(reason))
            ConnectionStatusChanged?.Invoke(this, reason);
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Disconnect();
            _sendChannel.Writer.TryComplete();
            // 等待收发循环退出（带短超时，避免 Dispose 长时间阻塞）。
            try { _sendTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _receiveTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        _disposed = true;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
        }
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private readonly struct OutboundFrame
    {
        public OutboundFrame(ReadOnlyMemory<byte> data, TaskCompletionSource<bool> tcs)
        {
            Data = data;
            Tcs = tcs;
        }
        public ReadOnlyMemory<byte> Data { get; }
        public TaskCompletionSource<bool> Tcs { get; }
    }
}
