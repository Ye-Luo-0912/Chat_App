namespace Core.Interfaces;

/// <summary>
/// 轻量内存事件总线，用于解耦网络层持久化与 UI 层增量更新。
/// 所有订阅在 UI 线程之外触发，订阅者需自行切线程。
/// </summary>
public interface IEventBus
{
    void Publish<T>(T @event) where T : notnull;
    IDisposable Subscribe<T>(Action<T> handler) where T : notnull;
}
