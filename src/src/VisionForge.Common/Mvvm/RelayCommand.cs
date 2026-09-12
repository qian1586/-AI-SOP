using System.Windows.Input;

namespace VisionForge.Common.Mvvm;

/// <summary>
/// 最简命令实现。
///
/// 注意：这里 <b>没有</b> 使用 WPF 的 CommandManager.RequerySuggested 自动重查，
/// 因为本文件位于不依赖 WPF 的 Common 层。需要刷新按钮可用状态时，
/// 显式调用 <see cref="RaiseCanExecuteChanged"/>。
///
/// 这样做的收益：Common 层可以被 WinForms 版、控制台版、Web 版复用，
/// 不会被 UI 框架绑死 —— 这正是"分层"的意义。
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 带参数的命令。常用于列表项上的按钮（如"编辑这一条配方"）。
/// </summary>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (parameter is T t) return _canExecute?.Invoke(t) ?? true;
        if (parameter is null) return _canExecute?.Invoke(default) ?? true;
        return false;
    }

    public void Execute(object? parameter)
    {
        _execute(parameter is T t ? t : default);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令。工控上位机里"启动相机""连接 PLC"这类操作必须异步，
/// 否则界面会卡死 —— 这是新手最常犯的错误。
///
/// 内置了执行中保护：命令执行期间自动 CanExecute=false，避免重复点击。
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        IsRunning = true;
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // 命令里的异常绝不能让它逃逸到 UI 线程，否则整个程序崩掉
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>异步命令内部异常的统一出口，由 ViewModel 订阅后弹提示或写日志。</summary>
    public event EventHandler<Exception>? ErrorOccurred;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
