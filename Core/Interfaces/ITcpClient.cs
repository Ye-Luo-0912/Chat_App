using System.Buffers;
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
    /// 连接到指定服务器端点。成功后可通过 <see cref="SendAsync(ReadOnlyMemory{byte}, CancellationToken)"/> 发送数据，
    /// 并调用 <see cref="ReceiveDataAsync"/> 启动接收循环。
    /// </summary>
    /// <param name="endpoint">服务器端点（主机、端口）。</param>
    /// <param name="token">取消令牌。</param>
    Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default);

    /// <summary>
    /// 发送数据到服务器。数据在返回 Task 前已同步复制到独立缓冲区，调用方可在返回后释放源内存。
    /// </summary>
    /// <param name="data">要发送的数据块。</param>
    /// <param name="token">取消令牌。</param>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default);

    /// <summary>
    /// P0-十 零拷贝出站重载：传输层直接消费 owner.Memory 并在发送完成后 Dispose，
    /// 避免旧 SendAsync(ReadOnlyMemory) 内部 data.ToArray() 的完整帧复制。
    /// 默认实现回退到复制路径以兼容测试用 mock（未实现此方法的 mock 走 DIM）。
    /// 调用方一旦调用此方法即转移所有权，不得再使用/释放 owner。
    /// </summary>
    Task SendAsync(IMemoryOwner<byte> owner, CancellationToken token = default)
    {
        // 默认：复制到独立内存后走旧路径，并释放 owner。
        // 旧 SendAsync(ReadOnlyMemory) 在返回 Task 前同步完成 ToArray 复制，
        // 因此在 Dispose 前数据已被独立拷出，安全。
        using (owner)
        {
            return SendAsync(owner.Memory, token);
        }
    }

    /// <summary>
    /// 启动接收循环，持续从服务器读取数据并通过 <see cref="OnDataChunkReceived"/> 事件通知。
    /// 阻塞至连接关闭或令牌取消。
    /// </summary>
    /// <param name="token">取消令牌；取消时结束接收循环。</param>
    Task ReceiveDataAsync(CancellationToken token);

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
