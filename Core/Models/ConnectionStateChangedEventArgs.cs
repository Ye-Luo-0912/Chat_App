namespace Core.Models;

/// <summary>
/// TCP 连接状态（结构化状态替代字符串）。
/// </summary>
public enum ConnectionState : byte
{
    Disconnected = 0,
    Connected = 1
}

/// <summary>
/// 连接状态变更事件参数：State + Reason（断开原因/错误信息）。
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState state, string? reason = null)
    {
        State = state;
        Reason = reason;
    }

    /// <summary>新的连接状态。</summary>
    public ConnectionState State { get; }

    /// <summary>断开原因或错误信息；Connected 时通常为 null。</summary>
    public string? Reason { get; }
}
