using System.Buffers;
using System.ComponentModel;
using System.Net.Sockets;
using System.Threading.Channels;
using Core.Interfaces;
using Core.Models;

namespace Chat_App.Infrastructure.Networking;

/// <summary>
/// TCP 客户端实现，提供连接、发送和接收数据的功能，并通过事件通知外部数据接收和连接状态的改变。
/// 每条连接拥有独立的 ConnectionSession（Socket/Channel/CTS/收发任务），
/// 重连时先关闭旧会话并等待其收发循环退出，再创建新会话。
/// </summary>
public class TcpClientExample : ITcpClient, IDisposable, IAsyncDisposable
{
    private readonly Lock _syncRoot = new();
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private ConnectionSession? _currentSession;
    private bool _disposed;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

    public bool IsConnected
    {
        get
        {
            lock (_syncRoot)
                return _currentSession is { IsActive: true };
        }
    }

    /// <summary>
    /// 连接到服务器并启动收发循环。
    /// 重连顺序：先关闭旧会话并等待旧收发循环退出 → 创建新会话 → 开始新连接。
    /// </summary>
    public async Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
    {
        // 静默关闭旧会话（不发状态事件，避免误触发重连），并等待旧收发循环退出。
        await CloseSessionSilentlyAsync().ConfigureAwait(false);

        var session = new ConnectionSession(Guid.NewGuid());
        lock (_syncRoot)
        {
            if (_disposed)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(TcpClientExample));
            }
            _currentSession = session;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await session.Socket.ConnectAsync(endpoint.ServerIpAddress, endpoint.ServerPort, cts.Token).ConfigureAwait(false);
            session.Activate();
            OnPropertyChanged(nameof(IsConnected));
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected));

            session.SendTask = Task.Run(() => SendLoopAsync(session, session.SendCts.Token));
            session.ReceiveTask = Task.Run(() => ReceiveLoopAsync(session, session.ReceiveCts.Token));
        }
        catch
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_currentSession, session))
                    _currentSession = null;
            }
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 发送数据：复制到独立内存后入队，由独占发送循环完整发送。
    /// 队列已满时 WriteAsync 会等待，形成背压。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        var (session, _) = GetActiveSession();
        if (session is null)
            throw new InvalidOperationException("Not connected to server");

        // 入队前复制数据到独立内存：调用方持有的 buffer 可能在入队后被重用/释放。
        var owned = data.ToArray();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new OutboundFrame(owned, owner: null, tcs);

        try
        {
            await session.SendChannel.Writer.WriteAsync(frame, token).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException("连接已关闭，无法发送", ex);
        }

        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 零拷贝出站：直接接管 owner 的池化内存入队，发送完成后由发送循环 Dispose。
    /// 不产生 data.ToArray 的完整帧复制。调用方转移所有权后不得再使用 owner。
    /// 入队被取消或连接已关闭时同样释放 owner。
    /// </summary>
    public async Task SendAsync(IMemoryOwner<byte> owner, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var (session, _) = GetActiveSession();
        if (session is null)
        {
            owner.Dispose();
            throw new InvalidOperationException("Not connected to server");
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new OutboundFrame(owner.Memory, owner, tcs);

        try
        {
            await session.SendChannel.Writer.WriteAsync(frame, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            owner.Dispose();
            throw;
        }
        catch (ChannelClosedException ex)
        {
            owner.Dispose();
            throw new InvalidOperationException("连接已关闭，无法发送", ex);
        }

        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 独占发送循环：从本会话队列逐帧完整发送，保证帧边界原子性。
    /// Channel 归属会话，旧会话循环不会取到新连接帧。
    /// </summary>
    private async Task SendLoopAsync(ConnectionSession session, CancellationToken token)
    {
        try
        {
            await foreach (var frame in session.SendChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var data = frame.Data;
                    var totalSent = 0;
                    while (totalSent < data.Length)
                    {
                        var sent = await session.Socket.SendAsync(data[totalSent..], SocketFlags.None, token).ConfigureAwait(false);
                        if (sent <= 0)
                            throw new SocketException((int)SocketError.ConnectionReset);
                        totalSent += sent;
                    }
                    frame.Tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    frame.Tcs.TrySetException(ex);
                    frame.Owner?.Dispose();
                    DrainSendChannel(session.SendChannel, ex);
                    Disconnect("Connection lost during send");
                    return;
                }
                finally
                {
                    if (frame.Tcs.Task.IsCompletedSuccessfully)
                        frame.Owner?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect 取消，正常退出。
        }
        catch (Exception ex)
        {
            DrainSendChannel(session.SendChannel, ex);
            Disconnect($"Send loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// 排空发送队列，将所有待发帧标记为失败，并归还其池化内存。
    /// </summary>
    private static void DrainSendChannel(Channel<OutboundFrame> channel, Exception ex)
    {
        while (channel.Reader.TryRead(out var frame))
        {
            frame.Tcs.TrySetException(ex);
            frame.Owner?.Dispose();
        }
    }

    /// <summary>
    /// 接收数据循环（接口兼容入口）：为当前会话启动接收循环。
    /// </summary>
    public Task ReceiveDataAsync(CancellationToken token)
    {
        ConnectionSession? session;
        lock (_syncRoot)
            session = _currentSession;
        return session is null ? Task.CompletedTask : ReceiveLoopAsync(session, token);
    }

    /// <summary>
    /// 接收数据循环。buffer 为局部变量，避免重连时新旧循环归还竞态。
    /// </summary>
    private async Task ReceiveLoopAsync(ConnectionSession session, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await session.Socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    Disconnect("Graceful disconnect");
                    break;
                }

                // 事件同步回调中同步消费（RoutePacket 零拷贝），回调返回后缓冲才可复用。
                OnDataChunkReceived?.Invoke(this, buffer.AsMemory(0, bytesRead));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Disconnect($"Receive error: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 断开与服务器的连接：取消收发循环、关闭 Socket、排空发送队列。
    /// 同步版本不等待循环退出；异步版本会真正等待。
    /// </summary>
    public void Disconnect(string? reason = null)
    {
        var session = TakeCurrentSession();
        if (session is null)
            return;

        CancelAndDrainSession(session);
        RaiseDisconnected(reason);
    }

    /// <summary>异步断开：取消收发循环并等待其真正退出后返回。</summary>
    public async Task DisconnectAsync(string? reason = null, CancellationToken token = default)
    {
        var session = TakeCurrentSession();
        if (session is null)
            return;

        CancelAndDrainSession(session);
        await WaitForSessionLoopsAsync(session, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        RaiseDisconnected(reason);
    }

    /// <summary>静默关闭旧会话（重连路径）：不触发 Disconnected 事件，避免误触发重连。</summary>
    private async Task CloseSessionSilentlyAsync()
    {
        var session = TakeCurrentSession();
        if (session is null)
            return;
        CancelAndDrainSession(session);
        await WaitForSessionLoopsAsync(session, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
    }

    private ConnectionSession? TakeCurrentSession()
    {
        lock (_syncRoot)
        {
            var session = _currentSession;
            _currentSession = null;
            return session;
        }
    }

    private (ConnectionSession? Session, bool Active) GetActiveSession()
    {
        lock (_syncRoot)
        {
            var session = _currentSession;
            return (session, session?.IsActive ?? false);
        }
    }

    private static void CancelAndDrainSession(ConnectionSession session)
    {
        try { session.SendCts.Cancel(); } catch (ObjectDisposedException) { }
        try { session.ReceiveCts.Cancel(); } catch (ObjectDisposedException) { }
        session.ShutdownAndDisposeSocket();
        DrainSendChannel(session.SendChannel, new InvalidOperationException("连接已断开"));
    }

    private static async Task WaitForSessionLoopsAsync(ConnectionSession session, TimeSpan timeout, CancellationToken token)
    {
        var loops = new[] { session.SendTask, session.ReceiveTask }
            .Where(t => t is not null)
            .Cast<Task>()
            .ToArray();
        if (loops.Length == 0)
            return;
        try
        {
            var all = Task.WhenAll(loops);
            if (await Task.WhenAny(all, Task.Delay(timeout, token)).ConfigureAwait(false) == all)
                await all.ConfigureAwait(false);
        }
        catch
        {
            // 循环退出异常已在循环内部处理；等待超时也视为已尽力。
        }
    }

    private void RaiseDisconnected(string? reason)
    {
        OnPropertyChanged(nameof(IsConnected));
        if (!string.IsNullOrEmpty(reason))
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync("dispose").ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        bool shouldDispose;
        lock (_syncRoot)
        {
            shouldDispose = !_disposed;
            _disposed = true;
        }
        if (!shouldDispose)
            return;
        Disconnect("dispose");
        // 不等待循环退出：同步 Dispose 不阻塞；应用关闭路径使用 DisposeAsync。
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 单条连接的所有资源：Socket、发送 Channel、收发 CTS 与任务。
    /// </summary>
    private sealed class ConnectionSession : IDisposable
    {
        public Guid ConnectionId { get; }
        public Socket Socket { get; }
        public Channel<OutboundFrame> SendChannel { get; } =
            Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        public CancellationTokenSource SendCts { get; } = new();
        public CancellationTokenSource ReceiveCts { get; } = new();
        public Task? SendTask { get; set; }
        public Task? ReceiveTask { get; set; }

        private int _active;

        public ConnectionSession(Guid connectionId)
        {
            ConnectionId = connectionId;
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        /// <summary>连接成功建立后标记为活动。</summary>
        public void Activate() => Volatile.Write(ref _active, 1);

        public bool IsActive => Volatile.Read(ref _active) == 1;

        /// <summary>关闭 Socket（Shutdown + Dispose），取消中的收发调用随即抛 ObjectDisposedException 退出。</summary>
        public void ShutdownAndDisposeSocket()
        {
            try { Socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
            catch (ObjectDisposedException) { }
            Socket.Dispose();
        }

        public void Dispose()
        {
            try { SendCts.Cancel(); } catch (ObjectDisposedException) { }
            try { ReceiveCts.Cancel(); } catch (ObjectDisposedException) { }
            ShutdownAndDisposeSocket();
            SendCts.Dispose();
            ReceiveCts.Dispose();
            SendChannel.Writer.TryComplete();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await WaitForSessionLoopsAsync(this, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private readonly struct OutboundFrame
    {
        public OutboundFrame(ReadOnlyMemory<byte> data, IMemoryOwner<byte>? owner, TaskCompletionSource<bool> tcs)
        {
            Data = data;
            Owner = owner;
            Tcs = tcs;
        }
        public ReadOnlyMemory<byte> Data { get; }
        /// <summary>
        /// 池化内存所有权。非 null 时表示帧数据来自 owner.Memory，
        /// 发送完成后必须 Dispose 以归还 ArrayPool；null 表示 Data 是独立 ToArray 副本（旧路径）。
        /// </summary>
        public IMemoryOwner<byte>? Owner { get; }
        public TaskCompletionSource<bool> Tcs { get; }
    }
}
