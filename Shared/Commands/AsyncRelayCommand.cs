using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Chat_App.Shared.Commands;

/// <summary>
/// 异步命令的接口，提供引发 CanExecuteChanged 和取消执行的能力
/// </summary>
public interface IAsyncRelayCommand : ICommand
{
    void RaiseCanExecuteChanged();
    void Cancel();
    bool IsExecuting { get; }
}

/// <summary>
/// 无参数的异步命令实现。用于替代 ReactiveCommand。
/// 支持执行期间防重复点击，支持传入 CanExecute 委托，支持异常处理回调，支持取消操作。
/// </summary>
public class AsyncRelayCommand : IAsyncRelayCommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onException;
    private CancellationTokenSource? _cts;
    private bool _isExecuting;

    public bool IsExecuting => _isExecuting;

    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onException = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onException = onException;
    }

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onException = null)
        : this(_ => execute(), canExecute, onException)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        return _canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _cts = new CancellationTokenSource();
        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户取消操作，不视为错误
        }
        catch (Exception ex)
        {
            _onException?.Invoke(ex);
        }
        finally
        {
            _isExecuting = false;
            _cts?.Dispose();
            _cts = null;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 取消当前正在执行的操作
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 带参数的异步命令实现。
/// </summary>
public class AsyncRelayCommand<T> : IAsyncRelayCommand
{
    private readonly Func<T?, CancellationToken, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private readonly Action<Exception>? _onException;
    private CancellationTokenSource? _cts;
    private bool _isExecuting;

    public bool IsExecuting => _isExecuting;

    public AsyncRelayCommand(
        Func<T?, CancellationToken, Task> execute,
        Func<T?, bool>? canExecute = null,
        Action<Exception>? onException = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onException = onException;
    }

    public AsyncRelayCommand(
        Func<T?, Task> execute,
        Func<T?, bool>? canExecute = null,
        Action<Exception>? onException = null)
        : this((p, _) => execute(p), canExecute, onException)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        return _canExecute?.Invoke(ConvertParameter(parameter)) ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        var typedParam = ConvertParameter(parameter);
        _cts = new CancellationTokenSource();

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute(typedParam, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户取消操作，不视为错误
        }
        catch (Exception ex)
        {
            _onException?.Invoke(ex);
        }
        finally
        {
            _isExecuting = false;
            _cts?.Dispose();
            _cts = null;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 取消当前正在执行的操作
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 将 object 参数转换为泛型类型 T
    /// </summary>
    private static T? ConvertParameter(object? parameter)
    {
        if (parameter is null) return default;
        if (parameter is T t) return t;
        if (typeof(T).IsAssignableFrom(parameter.GetType())) return (T)parameter;
        return default;
    }
}

/// <summary>
/// 同步命令实现
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
		ArgumentNullException.ThrowIfNull(execute);
		_execute = _ => execute();
        if (canExecute != null) _canExecute = _ => canExecute();
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            _execute(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
