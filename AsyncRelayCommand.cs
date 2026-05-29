using System.Windows.Input;

namespace SimpleMusicPlayer;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Predicate<object?>? _canExecute;
    private readonly bool _disableWhileRunning;
    private bool _isRunning;

    public AsyncRelayCommand(
        Func<object?, Task> executeAsync,
        Predicate<object?>? canExecute = null,
        bool disableWhileRunning = true)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
        _disableWhileRunning = disableWhileRunning;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => (!_disableWhileRunning || !_isRunning) && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync(parameter);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
