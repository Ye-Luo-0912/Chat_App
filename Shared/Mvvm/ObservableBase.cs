using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Chat_App.Shared.Mvvm;

/// <summary>
/// 提供基于 INotifyPropertyChanged 的基础实现，用于替代 ReactiveObject。
/// </summary>
public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 设置属性值，如果发生变化则触发 PropertyChanged 事件。
    /// </summary>
    /// <typeparam name="T">属性类型</typeparam>
    /// <param name="field">引用字段</param>
    /// <param name="value">新值</param>
    /// <param name="propertyName">属性名（由编译器自动注入）</param>
    /// <returns>如果值发生改变返回 true，否则返回 false。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 手动触发属性变更通知。
    /// </summary>
    /// <param name="propertyName">属性名</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
