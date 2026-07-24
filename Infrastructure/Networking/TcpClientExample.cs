using System.Buffers;
using System.ComponentModel;
using System.Net.Sockets;
using Core.Interfaces;
using Core.Models;

namespace Infrastructure.Networking;


/// <summary>
/// TCP 客户端实现，提供连接、发送和接收数据的功能，并通过事件通知外部数据接收和连接状态的改变。
/// </summary>
public class TcpClientExample: ITcpClient, IDisposable
{
    //使用 Socket 类来实现 TCP 客户端功能，提供更底层的网络通信控制，适用于需要高性能和自定义网络协议的场景。
    private Socket? _tcpClient;

    //使用 CancellationTokenSource 来管理接收数据的取消操作，确保在断开连接或对象销毁时能够正确地停止接收任务，避免资源泄漏和未处理的异步操作。
    private CancellationTokenSource? _receiveCts;

    //使用一个布尔字段来跟踪连接状态，确保在发送数据和接收数据时能够正确地判断当前的连接状态，避免在未连接的情况下进行网络操作。
    private bool _isConnected;

    //提供一个公共属性 IsConnected 来暴露当前的连接状态，供外部调用者检查是否已连接到服务器。
    public bool IsConnected => _isConnected;

    //使用一个布尔字段来跟踪对象是否已被销毁，确保在 Dispose 方法中正确地释放资源，并避免在对象已销毁的情况下进行操作。
    private bool _disposed;

    //连接状态改变事件，传递一个字符串参数来描述当前的连接状态，如 "Connected"、"Disconnected" 或具体的错误信息。
    public event EventHandler<string>? ConnectionStatusChanged;

    //实现 INotifyPropertyChanged 接口，提供属性改变通知机制，确保在属性值发生变化时能够正确地通知外部订阅者，以便界面或其他组件能够及时更新。
    public event PropertyChangedEventHandler? PropertyChanged;

    //使用 ArrayPool<byte> 来管理接收缓冲区，避免频繁分配和释放大块内存，提高性能和减少垃圾回收压力。
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    //接收缓冲区的租用和归还由 ReceiveDataAsync 方法负责，确保在接收数据时能够高效地使用内存资源。
    private byte[]? _receiveBuffer;

    //使用一个同步对象来保护对共享资源（如 TCP 客户端实例和连接状态）的访问，确保线程安全，避免竞争条件和数据不一致。
    private readonly Lock _syncRoot = new();

/*    //捕获当前的 SynchronizationContext，以便在接收数据时能够正确地将事件调度回 UI 线程，避免跨线程操作引发的异常。
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;*/
    public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;


    /// <summary>
    /// 连接到服务器并开始接收数据
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
    {
        // 重连前清理半开连接，避免双 Socket（不发状态事件，避免误触发重连）。
        Disconnect();

        Socket? clientSocket;
        lock (_syncRoot)
        {
            clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _tcpClient = clientSocket;
            _receiveCts = new CancellationTokenSource();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await clientSocket.ConnectAsync(endpoint.ServerIpAddress, endpoint.ServerPort, cts.Token);
            _isConnected = true;
            OnPropertyChanged(nameof(IsConnected));
            ConnectionStatusChanged?.Invoke(this, "Connected");
            _ = ReceiveDataAsync(_receiveCts.Token);
        }
        catch (Exception)
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_tcpClient, clientSocket))
                {
                    _tcpClient = null;
                    _receiveCts?.Cancel();
                    _receiveCts?.Dispose();
                    _receiveCts = null;
                }
            }

            clientSocket.Dispose();
            throw;
        }
    }


    /// <summary>
    /// 发送数据到服务器，确保在连接状态下发送，并处理可能的异常情况，如连接断开等。
    /// </summary>
    /// <param name="data"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        //确保线程安全地访问连接状态和 TCP 客户端实例
        Socket socket;
        lock(_syncRoot)
        {
            //如果未连接或 TCP 客户端实例为 null，则抛出异常，提示调用者当前无法发送数据
            if (!IsConnected || _tcpClient == null)
                throw new InvalidOperationException("Not connected to server");
            socket = _tcpClient;
        }

        try
        {
            //使用循环确保发送所有数据，处理可能的部分发送情况
            var tolalSent = 0;
            while (tolalSent < data.Length)
            {
                //使用 Socket 的 SendAsync 方法发送数据，处理可能的异常情况，如连接断开等
                var sent = await socket.SendAsync(data[tolalSent..], SocketFlags.None, token);
                if (sent <= 0)
                {
                    //如果发送了0字节，说明连接可能已断开，调用 Disconnect 方法进行清理，并抛出异常以通知调用者
                    Disconnect("Connection lost during send");
                    throw new SocketException((int)SocketError.ConnectionReset);
                }

                //累加已发送的字节数，继续发送剩余的数据
                tolalSent += sent;
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            //如果发生 SocketException 或 ObjectDisposedException，说明连接可能已断开或资源已被释放，调用 Disconnect 方法进行清理，并重新抛出异常以通知调用者
            Disconnect();
            throw;
        }
    }


    /// <summary>
    /// 接收数据的异步方法，持续监听服务器发送的数据，并通过事件通知外部订阅者。
    /// 确保在接收过程中正确处理取消操作和异常情况，避免资源泄漏和未处理的异步操作。
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task ReceiveDataAsync(CancellationToken token)
    {
        //租用一个缓冲区来接收数据，确保在接收完成后能够正确地归还缓冲区，避免内存泄漏和过度分配。
        _receiveBuffer = _bufferPool.Rent(4096);
        try
        {
            while (!token.IsCancellationRequested)
            {
                
                Socket socket;

                //确保线程安全地访问 TCP 客户端实例，避免在接收数据时发生竞争条件和数据不一致。
                lock (_syncRoot)
                {
                    //如果 TCP 客户端实例为 null，说明连接已断开，直接返回，避免继续尝试接收数据。
                    if (_tcpClient is null)
                        return;

                    socket = _tcpClient;
                }

                //使用 Socket 的 ReceiveAsync 方法接收数据，处理可能的异常情况，如连接断开等
                var bytesRead = await socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, token);
                if (bytesRead == 0)
                {
                    Disconnect("Graceful disconnect");
                    break;
                }

                //将接收到的数据转换为 ReadOnlyMemory<byte>，以便通过事件传递给外部订阅者，避免不必要的内存复制，提高性能。
                var receivedMemory = _receiveBuffer.AsMemory(0, bytesRead);

                //触发数据接收事件，通知外部订阅者有新的数据可用
                OnDataChunkReceived?.Invoke(this , receivedMemory);
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
            if (_receiveBuffer is not null)
            {
                _bufferPool.Return(_receiveBuffer);
                _receiveBuffer = null;
            }
        }
    }


    /// <summary>
    /// 断开与服务器的连接，确保资源得到正确释放，并通知外部连接状态的改变。
    /// 提供可选的断开原因参数，以便调用者了解断开的具体原因。
    /// </summary>
    /// <param name="reason"></param>
    public void Disconnect(string? reason = null)
    {
        var shouldNotify = false;
        lock (_syncRoot)
        {
            if (!_isConnected && _tcpClient is null)
                return;

            shouldNotify = _isConnected || !string.IsNullOrEmpty(reason);
            _isConnected = false;
            _receiveCts?.Cancel();
            try
            {
                _tcpClient?.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _tcpClient?.Dispose();
                _tcpClient = null;
                _receiveCts?.Dispose();
                _receiveCts = null;
            }
        }

        OnPropertyChanged(nameof(IsConnected));

        if (shouldNotify && !string.IsNullOrEmpty(reason))
            ConnectionStatusChanged?.Invoke(this, reason);
    }


    /// <summary>
    /// 触发属性改变事件，确保在属性值发生变化时能够正确地通知外部订阅者，以便界面或其他组件能够及时更新。
    /// </summary>
    /// <param name="propertyName"></param>
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 实现 IDisposable 接口，确保在对象生命周期结束时正确释放资源，避免内存泄漏和未释放的网络连接。
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            _receiveCts?.Dispose();
            _tcpClient?.Dispose();
        }
        _disposed = true;
    }


    /// <summary>
    /// 实现 IDisposable 接口，确保在对象生命周期结束时正确释放资源，避免内存泄漏和未释放的网络连接。
    /// </summary>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            _receiveCts?.Dispose();
            _tcpClient?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
    
    ~TcpClientExample() => Dispose();
}