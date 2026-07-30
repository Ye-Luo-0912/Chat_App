using System;
using System.Collections.Generic;
using Core.Interfaces;

namespace Core.Services;

/// <summary>
/// 内存事件总线实现。线程安全的订阅/发布。
/// Publish 同步调用所有 handler（不切线程），handler 内部自行异步处理。
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();

    public void Publish<T>(T @event) where T : notnull
    {
        List<Action<T>>? snapshot;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                return;
            snapshot = new List<Action<T>>(list.Count);
            foreach (var d in list)
            {
                if (d is Action<T> typed)
                    snapshot.Add(typed);
            }
        }

        foreach (var handler in snapshot)
        {
            try { handler(@event); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"EventBus handler error: {ex}"); }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                list = new List<Delegate>();
                _handlers[typeof(T)] = list;
            }
            list.Add(handler);
        }

        return new Unsubscriber<T>(this, handler);
    }

    private void Unsubscribe<T>(Action<T> handler) where T : notnull
    {
        lock (_lock)
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                    _handlers.Remove(typeof(T));
            }
        }
    }

    private sealed class Unsubscriber<T>(InMemoryEventBus bus, Action<T> handler) : IDisposable
        where T : notnull
    {
        public void Dispose() => bus.Unsubscribe(handler);
    }
}
