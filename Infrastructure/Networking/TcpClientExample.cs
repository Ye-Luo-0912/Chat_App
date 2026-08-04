using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
    /// <summary>
    /// Socket 收发缓冲（64KB）：回环/局域网吞吐基准显示 8KB→64KB 提升约 40%，
    /// 64KB→256KB 无进一步收益；Windows loopback 动态调节下 64KB 为稳定甜点。
    /// 须在 Connect 前设置（影响 TCP 窗口初始值），故置于 ConnectionSession 构造。
    /// </summary>
    public const int SocketBufferBytes = 64 * 1024;

    private readonly Lock _syncRoot = new();
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private ConnectionSession? _currentSession;
    private bool _disposed;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

    /// <summary>
    /// TLS 服务端证书校验回调。为 null 时使用系统默认信任链校验（生产默认严格校验）。
    /// 开发/测试环境可注入宽松回调以信任自签证书——注意：宽松回调仅限开发/测试，
    /// 生产必须保持 null 并使用系统信任链；此回调与吊销检查独立，两者都需显式配置。
    /// </summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

    /// <summary>
    /// 证书吊销检查模式。生产默认 Online（在线 CRL/OCSP 检查）；
    /// 开发/测试使用自签证书时必须显式设为 NoCheck（与生产策略严格分离）。
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.Online;

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
    /// 并发 ConnectAsync 由互斥门串行化：后到者先等待前一次连接流程完全结束，
    /// 再关闭刚建立的会话重连——保证任意时刻最多一条连接处于建立/激活过程，
    /// 先完成连接的会话不会被后完成的并发会话覆盖而泄漏。
    /// </summary>
    public async Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
    {
        await _connectGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(endpoint, token).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ConnectCoreAsync(ServerEndpoint endpoint, CancellationToken token)
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

            // TLS 传输。UseTls 时用 SslStream 包装 TCP 流并完成 TLS 握手；
            // 明文端口（如本地开发 127.0.0.1）保持裸流。此后收发全部走 session.Stream。
            Stream stream = new NetworkStream(session.Socket, ownsSocket: false);
            if (endpoint.UseTls)
            {
                var sslStream = RemoteCertificateValidationCallback is { } validateCallback
                    ? new SslStream(stream, leaveInnerStreamOpen: false, validateCallback)
                    : new SslStream(stream, leaveInnerStreamOpen: false);
                var targetHost = string.IsNullOrWhiteSpace(endpoint.TlsServerName)
                    ? endpoint.ServerIpAddress
                    : endpoint.TlsServerName;
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = RevocationMode
                }, cts.Token).ConfigureAwait(false);
                stream = sslStream;
            }
            session.Stream = stream;

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
    /// 高优先级发送：入队高优通道，由发送循环优先排空（心跳/超时敏感帧）。
    /// 所有权语义与 <see cref="SendAsync(IMemoryOwner{byte}, CancellationToken)"/> 相同。
    /// </summary>
    public async Task SendPriorityAsync(IMemoryOwner<byte> owner, CancellationToken token = default)
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
            await session.PriorityChannel.Writer.WriteAsync(frame, token).ConfigureAwait(false);
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
    /// 高优通道优先排空——心跳等保活帧不被普通消息积压饿死。
    /// Channel 归属会话，旧会话循环不会取到新连接帧。
    /// </summary>
    private async Task SendLoopAsync(ConnectionSession session, CancellationToken token)
    {
        try
        {
            while (true)
            {
                // 高优优先：先排空高优通道，再取普通通道（阻塞等待新帧）。
                while (session.PriorityChannel.Reader.TryRead(out var priorityFrame))
                {
                    if (!await SendFrameAsync(session, priorityFrame, token).ConfigureAwait(false))
                        return;
                }

                if (session.SendChannel.Reader.TryRead(out var frame))
                {
                    if (!await SendFrameAsync(session, frame, token).ConfigureAwait(false))
                        return;
                    continue;
                }

                var ready = await session.SendChannel.Reader.WaitToReadAsync(token).ConfigureAwait(false);
                if (!ready)
                    break; // 通道已 Complete（断连），正常退出
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect 取消，正常退出：通道已被 TryComplete 拒绝新帧，
            // 此处排空可能在取消竞态中残留的帧，避免其 tcs 永远不完成。
            DrainSendChannel(session.SendChannel, new InvalidOperationException("连接已断开"));
            DrainSendChannel(session.PriorityChannel, new InvalidOperationException("连接已断开"));
        }
        catch (Exception ex)
        {
            DrainSendChannel(session.SendChannel, ex);
            DrainSendChannel(session.PriorityChannel, ex);
            DisconnectSession(session, $"Send loop error: {ex.Message}");
        }
    }

    /// <summary>发送单帧：写流 + 完成/失败 tcs + 归还池化内存；失败时断连并返回 false。</summary>
    private async Task<bool> SendFrameAsync(ConnectionSession session, OutboundFrame frame, CancellationToken token)
    {
        try
        {
            var data = frame.Data;
            // SslStream 单次 Write 提交全部 TLS 帧；NetworkStream（阻塞 socket）也全量写出。
            // Stream 在 ConnectCoreAsync 中激活会话前已设置，循环仅在激活后启动。
            await session.Stream!.WriteAsync(data, token).ConfigureAwait(false);
            frame.Tcs.TrySetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            // 断线取消时统一以"连接已断开"失败：避免调用方把 OperationCanceledException
            // 误判为自身取消（发送循环的 token 仅来自 SendCts，OCE 必然意味着断线）。
            var failure = ex is OperationCanceledException
                ? new InvalidOperationException("连接已断开")
                : ex;
            frame.Tcs.TrySetException(failure);
            frame.Owner?.Dispose();
            DrainSendChannel(session.SendChannel, new InvalidOperationException("连接已断开"));
            DrainSendChannel(session.PriorityChannel, new InvalidOperationException("连接已断开"));
            // 身份绑定断连：旧会话循环的异常不得关闭新会话。
            DisconnectSession(session, "Connection lost during send");
            return false;
        }
        finally
        {
            if (frame.Tcs.Task.IsCompletedSuccessfully)
                frame.Owner?.Dispose();
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
                    // Stream 在 ConnectCoreAsync 中激活会话前已设置。
                    bytesRead = await session.Stream!.ReadAsync(buffer, token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    // 服务端优雅关闭：仅当本会话仍是当前会话时才触发断线事件。
                    DisconnectSession(session, "Graceful disconnect");
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
            DisconnectSession(session, $"Receive error: {ex.Message}");
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
        // 循环已退出：完整释放会话资源（CTS/Socket/Stream/Channel），防止遗留。
        session.Dispose();
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
        session.Dispose();
    }

    /// <summary>
    /// 会话身份绑定的断连：仅当 <paramref name="source"/> 仍是当前会话时才清理全局状态
    /// 并触发断线事件。旧会话循环的异常/延迟退出绝不能关闭新建立的连接。
    /// </summary>
    private void DisconnectSession(ConnectionSession source, string reason)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_currentSession, source))
                return; // 旧会话迟到异常：新会话不受影响。
            _currentSession = null;
        }
        CancelAndDrainSession(source);
        RaiseDisconnected(reason);
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
        // 先 TryComplete 再排空：背压中阻塞在 WriteAsync 的发送方立即收到 ChannelClosedException
        //（"连接已关闭，无法发送"），且 TryComplete 之后不可能再有新帧进入通道；
        // 随后排空残余帧并置为失败，保证所有 pending 调用方都被结束，不会永远挂起。
        session.SendChannel.Writer.TryComplete();
        session.PriorityChannel.Writer.TryComplete();
        DrainSendChannel(session.SendChannel, new InvalidOperationException("连接已断开"));
        DrainSendChannel(session.PriorityChannel, new InvalidOperationException("连接已断开"));
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
        _connectGate.Dispose();
        // 不等待循环退出：同步 Dispose 不阻塞；应用关闭路径使用 DisposeAsync。
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 单条连接的所有资源：Socket、TLS/明文流、发送 Channel、收发 CTS 与任务。
    /// </summary>
    private sealed class ConnectionSession : IDisposable
    {
        public Guid ConnectionId { get; }
        public Socket Socket { get; }

        /// <summary>收发流：UseTls 时为 SslStream（TLS 加密传输），否则为 NetworkStream。连接成功后设置。</summary>
        public Stream? Stream { get; set; }

        public Channel<OutboundFrame> SendChannel { get; } =
            Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        /// <summary>
        /// 高优通道：心跳等保活/超时敏感帧走此通道，发送循环优先排空。
        /// 容量同普通通道；高优帧量级低（心跳周期级），不会挤压普通通道背压语义。
        /// </summary>
        public Channel<OutboundFrame> PriorityChannel { get; } =
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
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                SendBufferSize = SocketBufferBytes,
                ReceiveBufferSize = SocketBufferBytes
            };
        }

        /// <summary>连接成功建立后标记为活动。</summary>
        public void Activate() => Volatile.Write(ref _active, 1);

        public bool IsActive => Volatile.Read(ref _active) == 1;

        /// <summary>关闭 Socket（Shutdown + Dispose），取消中的收发调用随即抛 ObjectDisposedException 退出。</summary>
        public void ShutdownAndDisposeSocket()
        {
            try { Socket.Shutdown(SocketShutdown.Both); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            Socket.Dispose();
        }

        public void Dispose()
        {
            try { SendCts.Cancel(); } catch (ObjectDisposedException) { }
            try { ReceiveCts.Cancel(); } catch (ObjectDisposedException) { }
            ShutdownAndDisposeSocket();
            try { Stream?.Dispose(); } catch (ObjectDisposedException) { }
            SendCts.Dispose();
            ReceiveCts.Dispose();
            SendChannel.Writer.TryComplete();
            PriorityChannel.Writer.TryComplete();
            DrainSendChannel(SendChannel, new InvalidOperationException("连接已断开"));
            DrainSendChannel(PriorityChannel, new InvalidOperationException("连接已断开"));
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
