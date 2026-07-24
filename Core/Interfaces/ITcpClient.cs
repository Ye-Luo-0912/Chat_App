using Core.Models;

namespace Core.Interfaces;

/// <summary>
/// TCP 客户端接口，定义了连接、发送和接收数据的基本功能，并通过事件通知外部数据接收和连接状态的改变。
/// </summary>
public interface ITcpClient : IDisposable
{
    /// <summary>
    /// 连接状态属性，指示当前是否已成功连接到服务器，供外部调用者检查连接状态，以便在发送数据和接收数据时能够正确地判断当前的连接状态，避免在未连接的情况下进行网络操作。
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 连接到服务器的方法，接受一个 ServerEndpoint 对象作为参数，包含服务器的相关信息，如 IP 地址和端口号，以及一个可选的 CancellationToken 参数用于取消连接操作。
    /// 该方法是异步的，返回一个 Task，表示连接操作的完成状态。通过这个方法，客户端可以尝试连接到指定的服务器，并在连接成功后进行数据通信。
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default);

    /// <summary>
    /// 断开与服务器的连接的方法，接受一个可选的字符串参数 reason 用于描述断开连接的原因，以及一个可选的 CancellationToken 参数用于取消断开操作。该方法是异步的，返回一个 Task，表示断开连接操作的完成状态。通过这个方法，客户端可以主动断开与服务器的连接，并提供断开原因以便日志记录或用户通知。
    /// </summary>
    /// <param name="data"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default);

    /// <summary>
    /// 发送数据的方法，接受一个 ReadOnlyMemory<byte> 参数 data，表示要发送的数据块，以及一个可选的 CancellationToken 参数 token 用于取消发送操作。该方法是异步的，返回一个 Task，表示发送操作的完成状态。通过这个方法，客户端可以将数据发送到服务器，并在发送完成后继续执行其他操作。
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task ReceiveDataAsync (CancellationToken token);

    /// <summary>
    /// 断开与服务器的连接的方法，接受一个可选的字符串参数 reason 用于描述断开连接的原因。通过这个方法，客户端可以主动断开与服务器的连接，并提供断开原因以便日志记录或用户通知。
    /// </summary>
    /// <param name="reason"></param>
    void Disconnect(string? reason = null);

    /// <summary>
    /// 连接状态改变事件，传递一个字符串参数来描述当前的连接状态，如 "Connected"、"Disconnected" 或具体的错误信息。
    /// </summary>
    public event EventHandler<string>? ConnectionStatusChanged;


    /// <summary>
    /// 数据接收事件，传递一个 ReadOnlyMemory<byte> 参数来提供接收到的数据块，确保在接收数据时能够正确地通知外部订阅者，以便界面或其他组件能够及时处理接收到的数据。
    /// </summary>
    public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
}