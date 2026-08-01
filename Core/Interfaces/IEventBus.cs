namespace Core.Interfaces;

/// <summary>
/// 轻量内存事件总线，用于解耦网络层持久化与 UI 层增量更新。
/// 所有订阅在 UI 线程之外触发，订阅者需自行切线程。
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// 发布事件，同步通知所有 <typeparamref name="T"/> 类型的订阅者。
    /// 订阅者在调用线程执行；如需切线程由订阅者自行处理。
    /// </summary>
    /// <typeparam name="T">事件类型。</typeparam>
    /// <param name="event">事件实例。</param>
    void Publish<T>(T @event) where T : notnull;

    /// <summary>
    /// 订阅指定类型的事件。返回的 <see cref="IDisposable"/> 释放后取消订阅。
    /// </summary>
    /// <typeparam name="T">事件类型。</typeparam>
    /// <param name="handler">事件处理回调。</param>
    /// <returns>释放即取消订阅的句柄。</returns>
    IDisposable Subscribe<T>(Action<T> handler) where T : notnull;
}
